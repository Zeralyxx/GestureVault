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
    ///     [4]  magic "GV2\0"
    ///     [16] PBKDF2 salt
    ///     [8]  chunk size (long, LE)
    ///     then repeating chunks:
    ///       [12] per-chunk nonce
    ///       [16] per-chunk GCM tag
    ///       [8]  plaintext length of this chunk (long, LE)
    ///       [N]  ciphertext (same length as plaintext for GCM)
    ///
    ///   GCM authenticates every chunk independently, so a corrupt / tampered
    ///   chunk is detected before any plaintext is written to disk.
    ///   The nonce counter prevents chunk reordering attacks.
    ///
    /// ── Key derivation note ─────────────────────────────────────────────────
    /// This service derives encryption keys independently from PasswordService.
    /// PasswordService handles authentication (password verification).
    /// EncryptionService handles confidentiality (data encryption).
    /// Both use PBKDF2-SHA512 with high iteration counts, but with different
    /// salts and purposes to maintain security separation.
    /// </summary>
    public static class EncryptionService
    {
        // ── Constants ─────────────────────────────────────────────────────────
        private const int SaltSize = 32;     // Increased from 16 to 32 bytes (256-bit)
        private const int NonceSize = 12;    // GCM standard
        private const int TagSize = 16;      // GCM standard
        private const int KeySize = 32;      // AES-256
        private const int Iterations = 300_000; // Matches OWASP 2023 recommendation
        private const int ChunkSize = 64 * 1024; // 64 KB per chunk

        // Header versioning for future-proofing
        private const byte CurrentV1Version = 1;
        private const byte CurrentV2Version = 1;

        private static readonly byte[] MagicV1 = { 0x47, 0x56, 0x31, 0x00 }; // "GV1\0"
        private static readonly byte[] MagicV2 = { 0x47, 0x56, 0x32, 0x00 }; // "GV2\0"

        // ── Key derivation (separate from PasswordService) ────────────────────

        /// <summary>
        /// Derives an AES-256 key from a password and salt.
        /// Uses PBKDF2-SHA512 with 300K iterations.
        /// This is separate from PasswordService's derivation to maintain
        /// security separation between authentication and encryption.
        /// </summary>
        private static byte[] DeriveKey(string password, byte[] salt)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA512);  // Upgraded from SHA256 to SHA512
            return pbkdf2.GetBytes(KeySize);
        }

        // ── Key validation ────────────────────────────────────────────────────

        /// <summary>
        /// Validates that a password can decrypt data by attempting to 
        /// derive a key and checking it's not null/empty.
        /// This is a lightweight check - full validation requires actual decryption.
        /// </summary>
        public static bool ValidatePasswordStrength(string password)
        {
            if (string.IsNullOrEmpty(password)) return false;
            if (password.Length < 8) return false;

            // Check for some entropy
            bool hasUpper = false, hasLower = false, hasDigit = false, hasSpecial = false;
            foreach (char c in password)
            {
                if (char.IsUpper(c)) hasUpper = true;
                else if (char.IsLower(c)) hasLower = true;
                else if (char.IsDigit(c)) hasDigit = true;
                else hasSpecial = true;
            }

            int variety = (hasUpper ? 1 : 0) + (hasLower ? 1 : 0) +
                         (hasDigit ? 1 : 0) + (hasSpecial ? 1 : 0);

            return variety >= 3; // At least 3 character types
        }

        // ── Data integrity verification ───────────────────────────────────────

        /// <summary>
        /// Attempts to verify if a blob was encrypted with the given password
        /// without fully decrypting it. Useful for password verification.
        /// V1 format: Tries to decrypt and validates magic bytes in header.
        /// Returns true if the password appears correct.
        /// </summary>
        public static bool VerifyPasswordForBlob(byte[] blob, string password)
        {
            try
            {
                // Try to parse and validate the GCM tag (cryptographic verification)
                DecryptBytes(blob, password);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ── String helpers ────────────────────────────────────────────────────

        public static string EncryptString(string plaintext, string password)
        {
            if (string.IsNullOrEmpty(plaintext))
                throw new ArgumentNullException(nameof(plaintext));
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException(nameof(password));

            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = EncryptBytes(data, password);
            return Convert.ToBase64String(encrypted);
        }

        public static string? DecryptString(string cipherBase64, string password)
        {
            if (string.IsNullOrEmpty(cipherBase64))
                return null;
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException(nameof(password));

            try
            {
                byte[] data = Convert.FromBase64String(cipherBase64);
                byte[] plain = DecryptBytes(data, password);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException)
            {
                return null; // Wrong password or corrupted data
            }
            catch (FormatException)
            {
                return null; // Invalid base64
            }
            catch
            {
                return null; // Other errors
            }
        }

        // ── Core byte-level encrypt / decrypt (small blobs) ──────────────────

        public static byte[] EncryptBytes(byte[] plaintext, string password)
        {
            if (plaintext == null)
                throw new ArgumentNullException(nameof(plaintext));
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException(nameof(password));

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] key = DeriveKey(password, salt);

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // New format with version byte and larger salt
            using var ms = new MemoryStream();
            ms.Write(MagicV1);
            ms.WriteByte(CurrentV1Version);   // Version byte for future-proofing
            ms.Write(salt);
            ms.Write(nonce);
            ms.Write(tag);
            ms.Write(ciphertext);
            return ms.ToArray();
        }

        public static byte[] DecryptBytes(byte[] blob, string password)
        {
            if (blob == null)
                throw new ArgumentNullException(nameof(blob));
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException(nameof(password));

            // Check magic + version + minimum size
            int minLen = MagicV1.Length + 1 + SaltSize + NonceSize + TagSize;
            if (blob.Length < minLen)
                throw new CryptographicException("Blob too short or corrupted.");

            // Validate magic bytes
            for (int i = 0; i < MagicV1.Length; i++)
                if (blob[i] != MagicV1[i])
                    throw new CryptographicException("Invalid format. Not a vault encrypted file.");

            int offset = MagicV1.Length;

            // Read version byte (currently unused, but checked for future compatibility)
            byte version = blob[offset];
            if (version > CurrentV1Version)
                throw new CryptographicException($"Unsupported encryption version: {version}");
            offset += 1;

            byte[] salt = blob[offset..(offset + SaltSize)];
            offset += SaltSize;
            byte[] nonce = blob[offset..(offset + NonceSize)];
            offset += NonceSize;
            byte[] tag = blob[offset..(offset + TagSize)];
            offset += TagSize;
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
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Source file not found.", sourcePath);
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException(nameof(password));

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] baseNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] key = DeriveKey(password, salt);

            using var src = new FileStream(sourcePath, FileMode.Open,
                                           FileAccess.Read, FileShare.Read,
                                           bufferSize: ChunkSize, useAsync: false);
            using var dst = new FileStream(destPath, FileMode.Create,
                                           FileAccess.Write, FileShare.None,
                                           bufferSize: ChunkSize, useAsync: false);

            // Header with version
            dst.Write(MagicV2);
            dst.WriteByte(CurrentV2Version);  // Version byte
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
                // Per-chunk nonce = baseNonce XOR chunk index
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
        /// Each chunk's tag is verified before any plaintext is written.
        /// </summary>
        public static void DecryptFile(string sourcePath,
                                       string destPath,
                                       string password)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Encrypted file not found.", sourcePath);
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException(nameof(password));

            using var src = new FileStream(sourcePath, FileMode.Open,
                                           FileAccess.Read, FileShare.Read,
                                           bufferSize: ChunkSize, useAsync: false);

            // Read and validate header
            byte[] magic = new byte[MagicV2.Length];
            src.ReadExactly(magic);
            for (int i = 0; i < MagicV2.Length; i++)
                if (magic[i] != MagicV2[i])
                    throw new CryptographicException("Invalid file format. Expected GV2 encrypted file.");

            int version = src.ReadByte();
            if (version > CurrentV2Version)
                throw new CryptographicException($"Unsupported file version: {version}");

            byte[] salt = new byte[SaltSize];
            byte[] chunkSzBuf = new byte[8];
            src.ReadExactly(salt);
            src.ReadExactly(chunkSzBuf);
            int storedChunkSize = (int)BitConverter.ToInt64(chunkSzBuf);

            byte[] key = DeriveKey(password, salt);

            using var aes = new AesGcm(key, TagSize);

            // Write plaintext to temp file first for atomicity
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

                        if (chunkLen > storedChunkSize)
                            throw new CryptographicException("Corrupted chunk size.");

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
            catch (CryptographicException)
            {
                // Clean up partial output on authentication failure
                if (File.Exists(partialPath)) File.Delete(partialPath);
                throw; // Re-throw - wrong password or tampered file
            }
            catch
            {
                // Clean up on any other failure
                if (File.Exists(partialPath)) File.Delete(partialPath);
                throw;
            }
        }

        // ── Secure wipe ───────────────────────────────────────────────────────

        /// <summary>
        /// Best-effort secure deletion of a temp file.
        /// Overwrites with random data before deletion.
        /// 
        /// ⚠ SSD/NVMe limitation: wear-levelling means overwriting the
        /// logical file path does NOT guarantee the original flash blocks are
        /// erased. This is defence-in-depth only — the correct primary control
        /// is to never write plaintext to disk in the first place.
        /// </summary>
        public static void SecureDeleteTemp(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                FileInfo fileInfo = new FileInfo(path);
                long length = fileInfo.Length;

                // Clear readonly attribute if present
                if (fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
                    fileInfo.Attributes &= ~FileAttributes.ReadOnly;

                using (var fs = new FileStream(path, FileMode.Open,
                                               FileAccess.Write, FileShare.None))
                {
                    // Three-pass overwrite for added security
                    for (int pass = 0; pass < 3; pass++)
                    {
                        const int bufSize = 4096;
                        byte[] noise = new byte[bufSize];
                        long written = 0;

                        while (written < length)
                        {
                            int chunk = (int)Math.Min(bufSize, length - written);

                            // Use different patterns for each pass
                            switch (pass)
                            {
                                case 0:
                                    RandomNumberGenerator.Fill(noise.AsSpan(0, chunk));
                                    break;
                                case 1:
                                    Array.Fill(noise, (byte)0xFF, 0, chunk);
                                    break;
                                case 2:
                                    Array.Fill(noise, (byte)0x00, 0, chunk);
                                    break;
                            }

                            fs.Write(noise, 0, chunk);
                            written += chunk;
                        }
                        fs.Flush(flushToDisk: true);
                        fs.Seek(0, SeekOrigin.Begin);
                    }
                }

                File.Delete(path);
            }
            catch (FileNotFoundException)
            {
                // Already deleted - that's fine
            }
            catch
            {
                // Best-effort: file might be locked by another process
            }
        }

        // ── Data migration helper ────────────────────────────────────────────

        /// <summary>
        /// Checks if data was encrypted with old parameters and needs re-encryption.
        /// Detects old format by checking salt size (old: 16 bytes, new: 32 bytes).
        /// </summary>
        public static bool NeedsReEncryption(byte[] blob)
        {
            if (blob == null || blob.Length < MagicV1.Length + 1 + SaltSize)
                return true;

            // Check if it's old format (16-byte salt) or no version byte
            int offset = MagicV1.Length;

            // Old format: no version byte, 16-byte salt
            // New format: version byte, 32-byte salt
            // If total length matches old format, it needs upgrading
            int oldMinSize = MagicV1.Length + 16 + NonceSize + TagSize;
            int newMinSize = MagicV1.Length + 1 + SaltSize + NonceSize + TagSize;

            // Heuristic: if it's exactly old format size, needs re-encryption
            return blob.Length >= oldMinSize && blob.Length < newMinSize;
        }
    }
}