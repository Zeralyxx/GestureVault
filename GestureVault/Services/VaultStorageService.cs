using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace GestureVault.Services
{
    /// <summary>
    /// Manages the on-disk vault structure:
    ///
    ///   %LocalAppData%\GestureVault\
    ///     vault\
    ///       {itemId}\
    ///         meta.gv1          ← encrypted JSON for this one VaultItem
    ///         {fileId}.gv1      ← each attached file, encrypted (documents only)
    ///     temp\                 ← wiped on startup and on lock
    ///
    /// Each VaultItem's metadata lives in its own file.
    /// Adding / editing / deleting one item rewrites only that item's meta.gv1,
    /// not the entire vault — O(1) I/O instead of O(N).
    ///
    /// Passwords and notes have a meta.gv1 but no attached .gv1 file blobs.
    ///
    /// ── Passphrase storage ─────────────────────────────────────────────────
    /// The voice passphrase is stored encrypted (AES-256-GCM via EncryptionService)
    /// in LocalSettings under "RegisteredPassphraseEnc".  The old plaintext key
    /// "RegisteredPassphrase" is migrated and deleted on first unlock.
    /// </summary>
    public class VaultStorageService
    {
        // ── Paths ─────────────────────────────────────────────────────────────
        private readonly string _vaultDir;
        private readonly string _tempDir;

        private string _masterPassword = string.Empty;

        // ── Internal temp-file registry ───────────────────────────────────────
        private readonly List<string> _activeTempFiles = new();

        public VaultStorageService()
        {
            string appData = ApplicationData.Current.LocalFolder.Path;
            _vaultDir = Path.Combine(appData, "vault");
            _tempDir = Path.Combine(appData, "temp");

            Directory.CreateDirectory(_vaultDir);
            Directory.CreateDirectory(_tempDir);
        }

        // ── Session password ──────────────────────────────────────────────────
        public void UnlockWithPassword(string password)
        {
            _masterPassword = password;
            MigratePassphraseIfNeeded(); // one-time migration from plaintext → encrypted
        }

        public void Lock()
        {
            CleanupTempFiles();
            _masterPassword = string.Empty;
        }

        // ── Metadata — per-item files ─────────────────────────────────────────

        /// <summary>
        /// Loads all VaultItems by reading and decrypting each meta.gv1 file
        /// in the vault directory. Corrupt or unreadable items are skipped.
        /// </summary>
        public List<VaultItem> LoadItems()
        {
            var items = new List<VaultItem>();

            foreach (string itemDir in Directory.EnumerateDirectories(_vaultDir))
            {
                string metaPath = Path.Combine(itemDir, "meta.gv1");
                if (!File.Exists(metaPath)) continue;

                try
                {
                    string? json = EncryptionService.DecryptString(
                        File.ReadAllText(metaPath), _masterPassword);

                    if (json == null) continue;

                    var item = JsonSerializer.Deserialize<VaultItem>(json);
                    if (item != null) items.Add(item);
                }
                catch
                {
                    // Skip corrupted items rather than crashing the whole load
                }
            }

            return items;
        }

        /// <summary>
        /// Saves a single VaultItem. Only this item's meta.gv1 is rewritten.
        /// Call this after every add / edit operation.
        /// </summary>
        public void SaveItem(VaultItem item)
        {
            string itemDir = GetItemDir(item.Id);
            Directory.CreateDirectory(itemDir);

            string metaPath = Path.Combine(itemDir, "meta.gv1");
            string json = JsonSerializer.Serialize(item);
            string encrypted = EncryptionService.EncryptString(json, _masterPassword);

            File.WriteAllText(metaPath, encrypted);
        }

        /// <summary>
        /// Deletes a VaultItem's entire directory (metadata + any attached files).
        /// </summary>
        public void DeleteItem(VaultItem item)
        {
            string itemDir = GetItemDir(item.Id);
            if (Directory.Exists(itemDir))
                Directory.Delete(itemDir, recursive: true);
        }

        /// <summary>
        /// Legacy bulk-save kept for migration helpers or batch imports.
        /// Prefer SaveItem for normal operations.
        /// </summary>
        public void SaveItems(List<VaultItem> items)
        {
            foreach (var item in items)
                SaveItem(item);
        }

        // ── Document file management ──────────────────────────────────────────

        /// <summary>
        /// Encrypts and copies sourcePath into the vault folder for itemId.
        /// Returns the VaultFile descriptor to persist in the VaultItem.
        /// Uses chunked streaming — safe for files of any size.
        /// </summary>
        public VaultFile AddFileToItem(string itemId, string sourcePath)
        {
            string itemDir = GetItemDir(itemId);
            Directory.CreateDirectory(itemDir);

            string fileId = Guid.NewGuid().ToString();
            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(itemDir, fileId + ".gv1");

            EncryptionService.EncryptFile(sourcePath, destPath, _masterPassword);

            return new VaultFile
            {
                FileId = fileId,
                FileName = fileName,
                StoredAt = destPath,
                AddedAt = DateTime.Now.ToString("MMM dd, yyyy")
            };
        }

        /// <summary>
        /// Decrypts a vault file to a temp path and returns the temp path.
        /// Uses chunked streaming — safe for files of any size.
        /// Always call CleanupTempFile(path) when done.
        /// </summary>
        public string DecryptFileToTemp(VaultFile vaultFile)
        {
            string ext = Path.GetExtension(vaultFile.FileName);
            string tempPath = Path.Combine(_tempDir, vaultFile.FileId + ext);

            EncryptionService.DecryptFile(vaultFile.StoredAt, tempPath, _masterPassword);

            lock (_activeTempFiles) _activeTempFiles.Add(tempPath);
            return tempPath;
        }

        /// <summary>Secure-deletes one encrypted vault file.</summary>
        public void RemoveFileFromItem(VaultFile vaultFile)
        {
            EncryptionService.SecureDeleteTemp(vaultFile.StoredAt);
        }

        // ── Temp cleanup ──────────────────────────────────────────────────────
        public void CleanupTempFile(string tempPath)
        {
            EncryptionService.SecureDeleteTemp(tempPath);
            lock (_activeTempFiles) _activeTempFiles.Remove(tempPath);
        }

        public void CleanupTempFiles()
        {
            lock (_activeTempFiles)
            {
                foreach (string f in _activeTempFiles)
                    EncryptionService.SecureDeleteTemp(f);
                _activeTempFiles.Clear();
            }

            // Sweep any disk remnants from a crashed session
            if (!Directory.Exists(_tempDir)) return;
            foreach (string f in Directory.GetFiles(_tempDir))
                EncryptionService.SecureDeleteTemp(f);
        }

        // ── Passphrase helpers ────────────────────────────────────────────────

        /// <summary>
        /// Saves the voice passphrase encrypted with the master password.
        /// Call this from RegistrationWindow instead of writing plaintext.
        /// </summary>
        public void SavePassphrase(string passphrase)
        {
            string encrypted = EncryptionService.EncryptString(passphrase, _masterPassword);
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["RegisteredPassphraseEnc"] = encrypted;
            settings.Values.Remove("RegisteredPassphrase"); // remove plaintext if present
        }

        /// <summary>
        /// Loads and decrypts the voice passphrase.
        /// Returns null if not set or decryption fails.
        /// </summary>
        public string? LoadPassphrase()
        {
            var settings = ApplicationData.Current.LocalSettings;
            string? enc = settings.Values["RegisteredPassphraseEnc"] as string;
            if (enc == null) return null;

            return EncryptionService.DecryptString(enc, _masterPassword);
        }

        /// <summary>
        /// One-time migration: if an old plaintext passphrase exists, encrypt
        /// it and delete the plaintext. Called automatically on UnlockWithPassword.
        /// </summary>
        private void MigratePassphraseIfNeeded()
        {
            var settings = ApplicationData.Current.LocalSettings;

            if (settings.Values["RegisteredPassphrase"] is string plaintext
                && !string.IsNullOrEmpty(plaintext)
                && settings.Values["RegisteredPassphraseEnc"] == null)
            {
                SavePassphrase(plaintext); // encrypts and removes plaintext key
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private string GetItemDir(string itemId) =>
            Path.Combine(_vaultDir, itemId);
    }
}