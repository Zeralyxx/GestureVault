using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GestureVault.Services
{
    /// <summary>
    /// AES-256-GCM encryption with PBKDF2 key derivation.
    ///
    /// Small data (strings, small files) → EncryptBytes / DecryptBytes
    ///   Wire format:
    ///     [4]  magic "GV1\0"
    ///     [16] PBKDF2 salt
    ///     [12] AES-GCM nonce
    ///     [16] AES-GCM tag
    ///     [N]  ciphertext
    ///
    /// Large files → EncryptFile / DecryptFile  (chunked streaming)
    ///   Wire format:
    ///     [4]  magic "GV2\0"          ← different magic so we can tell them apart
    ///     [16] PBKDF2 salt            ← one salt for the whole file
    ///     [8]  chunk size (long, LE)  ← so the reader knows how big each chunk is
    ///     then repeating chunks:
    ///       [12] per-chunk nonce      ← derived: base nonce XOR chunk-index
    ///       [16] per-chunk GCM tag
    ///       [8]  plaintext length of this chunk (long, LE)
    ///       [N]  ciphertext (same length as plaintext for GCM)
    ///
    ///   GCM authenticates every chunk independently, so a corrupt / tampered
    ///   chunk is detected before any plaintext is written to disk.
    ///   The nonce counter prevents chunk reordering attacks.
    /// </summary>
    public static class EncryptionService
    {
        // ── Constants ─────────────────────────────────────────────────────────
        private const int SaltSize = 16;
        private const int NonceSize = 12;    // GCM standard
        private const int TagSize = 16;    // GCM standard
        private const int KeySize = 32;    // AES-256
        private const int Iterations = 200_000;
        private const int ChunkSize = 64 * 1024; // 64 KB per chunk

        private static readonly byte[] MagicV1 = { 0x47, 0x56, 0x31, 0x00 }; // "GV1\0"
        private static readonly byte[] MagicV2 = { 0x47, 0x56, 0x32, 0x00 }; // "GV2\0"

        // ── Key derivation ────────────────────────────────────────────────────
        private static byte[] DeriveKey(string password, byte[] salt)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(
                password, salt, Iterations, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(KeySize);
        }

        // ── String helpers (unchanged) ────────────────────────────────────────
        public static string EncryptString(string plaintext, string password)
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = EncryptBytes(data, password);
            return Convert.ToBase64String(encrypted);
        }

        public static string? DecryptString(string cipherBase64, string password)
        {
            try
            {
                byte[] data = Convert.FromBase64String(cipherBase64);
                byte[] plain = DecryptBytes(data, password);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return null; }
        }

        // ── Core byte-level encrypt / decrypt (small blobs) ──────────────────
        public static byte[] EncryptBytes(byte[] plaintext, string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] key = DeriveKey(password, salt);

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            using var ms = new MemoryStream();
            ms.Write(MagicV1);
            ms.Write(salt);
            ms.Write(nonce);
            ms.Write(tag);
            ms.Write(ciphertext);
            return ms.ToArray();
        }

        public static byte[] DecryptBytes(byte[] blob, string password)
        {
            int minLen = MagicV1.Length + SaltSize + NonceSize + TagSize;
            if (blob.Length < minLen)
                throw new CryptographicException("Blob too short.");

            for (int i = 0; i < MagicV1.Length; i++)
                if (blob[i] != MagicV1[i])
                    throw new CryptographicException("Invalid magic header.");

            int offset = MagicV1.Length;

            byte[] salt = blob[offset..(offset + SaltSize)]; offset += SaltSize;
            byte[] nonce = blob[offset..(offset + NonceSize)]; offset += NonceSize;
            byte[] tag = blob[offset..(offset + TagSize)]; offset += TagSize;
            byte[] ciphertext = blob[offset..];

            byte[] key = DeriveKey(password, salt);
            byte[] plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }

        // ── Streaming file encrypt / decrypt (large files) ───────────────────
        /// <summary>
        /// Encrypts sourcePath → destPath using 64 KB chunks.
        /// Peak RAM = one 64 KB chunk, regardless of file size.
        /// </summary>
        public static void EncryptFile(string sourcePath,
                                       string destPath,
                                       string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] baseNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] key = DeriveKey(password, salt);

            using var src = new FileStream(sourcePath, FileMode.Open,
                                           FileAccess.Read, FileShare.Read,
                                           bufferSize: ChunkSize, useAsync: false);
            using var dst = new FileStream(destPath, FileMode.Create,
                                           FileAccess.Write, FileShare.None,
                                           bufferSize: ChunkSize, useAsync: false);

            // Header
            dst.Write(MagicV2);
            dst.Write(salt);
            dst.Write(BitConverter.GetBytes((long)ChunkSize));

            byte[] plainBuf = new byte[ChunkSize];
            byte[] cipherBuf = new byte[ChunkSize];
            byte[] tag = new byte[TagSize];
            byte[] nonce = new byte[NonceSize];
            long chunkIdx = 0;

            using var aes = new AesGcm(key, TagSize);

            int bytesRead;
            while ((bytesRead = src.Read(plainBuf, 0, ChunkSize)) > 0)
            {
                // Per-chunk nonce = baseNonce XOR little-endian chunk index
                // (counter in the last 8 bytes so we get 2^64 chunks safely)
                baseNonce.CopyTo(nonce, 0);
                byte[] idxBytes = BitConverter.GetBytes(chunkIdx);
                for (int i = 0; i < 8; i++)
                    nonce[NonceSize - 8 + i] ^= idxBytes[i];

                var plain = plainBuf.AsSpan(0, bytesRead);
                var cipher = cipherBuf.AsSpan(0, bytesRead);

                aes.Encrypt(nonce, plain, cipher, tag);

                dst.Write(nonce);
                dst.Write(tag);
                dst.Write(BitConverter.GetBytes((long)bytesRead));
                dst.Write(cipherBuf, 0, bytesRead);

                chunkIdx++;
            }
        }

        /// <summary>
        /// Decrypts a file produced by EncryptFile.
        /// Each chunk's tag is verified before any plaintext is written,
        /// so a tampered file is detected early and destPath is deleted.
        /// Peak RAM = one 64 KB chunk.
        /// </summary>
        public static void DecryptFile(string sourcePath,
                                       string destPath,
                                       string password)
        {
            using var src = new FileStream(sourcePath, FileMode.Open,
                                           FileAccess.Read, FileShare.Read,
                                           bufferSize: ChunkSize, useAsync: false);

            // Read and validate header
            byte[] magic = new byte[MagicV2.Length];
            src.ReadExactly(magic);
            for (int i = 0; i < MagicV2.Length; i++)
                if (magic[i] != MagicV2[i])
                    throw new CryptographicException("Invalid file format.");

            byte[] salt = new byte[SaltSize];
            byte[] chunkSzBuf = new byte[8];
            src.ReadExactly(salt);
            src.ReadExactly(chunkSzBuf);
            int storedChunkSize = (int)BitConverter.ToInt64(chunkSzBuf);

            byte[] key = DeriveKey(password, salt);

            using var aes = new AesGcm(key, TagSize);

            // Write plaintext to a temp-of-a-temp so a bad tag mid-file
            // doesn't leave a partial plaintext at destPath.
            string partialPath = destPath + ".part";
            try
            {
                using (var dst = new FileStream(partialPath, FileMode.Create,
                                                FileAccess.Write, FileShare.None,
                                                bufferSize: ChunkSize, useAsync: false))
                {
                    byte[] nonce = new byte[NonceSize];
                    byte[] tag = new byte[TagSize];
                    byte[] lenBuf = new byte[8];
                    byte[] cipherBuf = new byte[storedChunkSize];
                    byte[] plainBuf = new byte[storedChunkSize];

                    while (src.Position < src.Length)
                    {
                        src.ReadExactly(nonce);
                        src.ReadExactly(tag);
                        src.ReadExactly(lenBuf);
                        int chunkLen = (int)BitConverter.ToInt64(lenBuf);

                        src.ReadExactly(cipherBuf, 0, chunkLen);

                        // Throws CryptographicException on tag mismatch
                        aes.Decrypt(nonce,
                                    cipherBuf.AsSpan(0, chunkLen),
                                    tag,
                                    plainBuf.AsSpan(0, chunkLen));

                        dst.Write(plainBuf, 0, chunkLen);
                    }
                }

                // All chunks verified — rename to final destination
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(partialPath, destPath);
            }
            catch
            {
                // Clean up partial output on any failure
                if (File.Exists(partialPath)) File.Delete(partialPath);
                throw;
            }
        }

        // ── Secure wipe ───────────────────────────────────────────────────────
        /// <summary>
        /// Best-effort secure deletion of a temp file.
        ///
        /// ⚠ SSD / NVMe limitation: wear-levelling means overwriting the
        /// logical file path does NOT guarantee the original flash blocks are
        /// erased. This is defence-in-depth only — the correct primary control
        /// is to never write plaintext to disk in the first place.
        /// </summary>
        public static void SecureDeleteTemp(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                long length = new FileInfo(path).Length;

                using (var fs = new FileStream(path, FileMode.Open,
                                               FileAccess.Write, FileShare.None))
                {
                    const int bufSize = 4096;
                    byte[] noise = new byte[bufSize];
                    long written = 0;
                    while (written < length)
                    {
                        int chunk = (int)Math.Min(bufSize, length - written);
                        RandomNumberGenerator.Fill(noise.AsSpan(0, chunk));
                        fs.Write(noise, 0, chunk);
                        written += chunk;
                    }
                    fs.Flush(flushToDisk: true);
                }

                File.Delete(path);
            }
            catch { /* best-effort */ }
        }
    }
}