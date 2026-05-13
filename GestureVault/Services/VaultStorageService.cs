using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Storage;

namespace GestureVault.Services
{
    /// <summary>
    /// Manages the on-disk vault structure:
    ///
    ///   %LocalAppData%\GestureVault\
    ///     vault\
    ///       vault.gv1          ← encrypted vault index (all items metadata)
    ///       {itemId}\
    ///         {fileId}.gv1     ← each attached file, encrypted (documents only)
    ///     temp\                 ← wiped on startup and on lock
    ///     backups\              ← manual vault backups
    ///
    /// Vault index contains all item metadata in one encrypted file.
    /// This allows atomic password changes (re-encrypt one file).
    /// </summary>
    public class VaultStorageService
    {
        // ── Paths ─────────────────────────────────────────────────────────────
        private readonly string _vaultDir;
        private readonly string _tempDir;
        private readonly string _backupDir;
        private readonly string _vaultIndexPath;

        private string _masterPassword = string.Empty;
        private readonly List<string> _activeTempFiles = new();

        // ── Constants ─────────────────────────────────────────────────────────
        private const string VaultIndexFile = "vault.gv1";
        private const string VaultBackupIndexFile = "vault_backup.gv1";

        public VaultStorageService()
        {
            string appData = ApplicationData.Current.LocalFolder.Path;
            _vaultDir = Path.Combine(appData, "vault");
            _tempDir = Path.Combine(appData, "temp");
            _backupDir = Path.Combine(appData, "backups");
            _vaultIndexPath = Path.Combine(_vaultDir, VaultIndexFile);

            Directory.CreateDirectory(_vaultDir);
            Directory.CreateDirectory(_tempDir);
            Directory.CreateDirectory(_backupDir);
        }

        // ── Session password ──────────────────────────────────────────────────
        public void UnlockWithPassword(string password)
        {
            _masterPassword = password;
            MigratePassphraseIfNeeded();
        }

        public void Lock()
        {
            CleanupTempFiles();
            _masterPassword = string.Empty;
        }

        // ── Password Verification ────────────────────────────────────────────
        /// <summary>
        /// Quick password verification without loading entire vault.
        /// Just tries to decrypt the vault index header.
        /// </summary>
        public bool VerifyPassword(string password)
        {
            if (!File.Exists(_vaultIndexPath))
                return true; // No vault yet - any password works

            try
            {
                // Try to decrypt just the header
                string encrypted = File.ReadAllText(_vaultIndexPath);
                string? json = EncryptionService.DecryptString(encrypted, password);
                return json != null;
            }
            catch
            {
                return false;
            }
        }

        // ── Metadata — vault index file ──────────────────────────────────────

        /// <summary>
        /// Loads all VaultItems from the encrypted vault index.
        /// </summary>
        public List<VaultItem> LoadItems()
        {
            var items = new List<VaultItem>();

            if (!File.Exists(_vaultIndexPath))
                return items;

            try
            {
                string encrypted = File.ReadAllText(_vaultIndexPath);
                string? json = EncryptionService.DecryptString(encrypted, _masterPassword);

                if (json == null)
                    throw new CryptographicException("Failed to decrypt vault index");

                // Verify integrity
                var vaultData = JsonSerializer.Deserialize<VaultData>(json);
                if (vaultData?.Items == null)
                    throw new InvalidDataException("Corrupted vault index");

                items = vaultData.Items;
            }
            catch (CryptographicException)
            {
                // Password might be wrong or file corrupted
                throw;
            }
            catch (Exception ex)
            {
                // Try to recover from backup
                items = TryRecoverFromBackup();
                if (items.Count == 0)
                    throw new InvalidDataException(
                        "Vault index is corrupted and no backup is available.", ex);
            }

            // Clean up orphaned file directories
            CleanOrphanedDirectories(items);

            return items;
        }

        /// <summary>
        /// Saves all VaultItems to the encrypted vault index (atomic write).
        /// </summary>
        public void SaveItems(List<VaultItem> items)
        {
            var vaultData = new VaultData
            {
                Version = 1,
                LastModified = DateTime.UtcNow,
                Checksum = string.Empty, // Will be set
                Items = items
            };

            // Calculate checksum for integrity verification
            string json = JsonSerializer.Serialize(vaultData);
            vaultData.Checksum = CalculateChecksum(json);
            json = JsonSerializer.Serialize(vaultData);

            string encrypted = EncryptionService.EncryptString(json, _masterPassword);

            // Atomic write: write to temp file first, then rename
            string tempPath = _vaultIndexPath + ".tmp";
            string backupPath = _vaultIndexPath + ".bak";

            try
            {
                // Write to temp
                File.WriteAllText(tempPath, encrypted);

                // Backup existing index if present
                if (File.Exists(_vaultIndexPath))
                {
                    File.Copy(_vaultIndexPath, backupPath, overwrite: true);
                }

                // Atomic rename
                File.Move(tempPath, _vaultIndexPath, overwrite: true);

                // Remove backup after successful write
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            catch
            {
                // Restore from backup on failure
                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, _vaultIndexPath, overwrite: true);
                    File.Delete(backupPath);
                }
                throw;
            }
        }

        /// <summary>
        /// Re-encrypts entire vault with a new password.
        /// Call this after password change.
        /// </summary>
        public void ChangePassword(string oldPassword, string newPassword)
        {
            if (!VerifyPassword(oldPassword))
                throw new UnauthorizedAccessException("Current password is incorrect.");

            // Load with old password
            _masterPassword = oldPassword;
            var items = LoadItems();
            string? passphrase = LoadPassphrase();

            // Save with new password
            _masterPassword = newPassword;
            SaveItems(items);
            if (!string.IsNullOrWhiteSpace(passphrase))
                SavePassphrase(passphrase);

            // Update session
            _masterPassword = newPassword;
        }

        // ── Document file management ──────────────────────────────────────────

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

        public string DecryptFileToTemp(VaultFile vaultFile)
        {
            string ext = Path.GetExtension(vaultFile.FileName);
            string tempPath = Path.Combine(_tempDir, vaultFile.FileId + ext);

            EncryptionService.DecryptFile(vaultFile.StoredAt, tempPath, _masterPassword);

            lock (_activeTempFiles) _activeTempFiles.Add(tempPath);
            return tempPath;
        }


        public string RestoreFileToOriginalPath(VaultFile vaultFile)
        {
            string? originalPath = vaultFile.OriginalPath;
            if (string.IsNullOrWhiteSpace(originalPath))
                originalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), vaultFile.FileName);

            string directory = Path.GetDirectoryName(originalPath)!;
            Directory.CreateDirectory(directory);

            string destination = originalPath;
            if (File.Exists(destination))
            {
                string name = Path.GetFileNameWithoutExtension(destination);
                string ext = Path.GetExtension(destination);
                destination = Path.Combine(directory, $"{name} (restored {DateTime.Now:yyyyMMdd-HHmmss}){ext}");
            }

            EncryptionService.DecryptFile(vaultFile.StoredAt, destination, _masterPassword);
            return destination;
        }
        public void RemoveFileFromItem(VaultFile vaultFile)
        {
            EncryptionService.SecureDeleteTemp(vaultFile.StoredAt);
        }

        public void DeleteItem(VaultItem item)
        {
            string itemDir = GetItemDir(item.Id);
            if (Directory.Exists(itemDir))
                Directory.Delete(itemDir, recursive: true);
        }

        // ── Backup & Recovery ─────────────────────────────────────────────────

        /// <summary>
        /// Creates a timestamped backup of the entire vault.
        /// </summary>
        public string CreateBackup()
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupPath = Path.Combine(_backupDir, $"vault_{timestamp}.gv1");

            if (File.Exists(_vaultIndexPath))
            {
                File.Copy(_vaultIndexPath, backupPath);
            }

            // Also backup all encrypted files
            foreach (string itemDir in Directory.EnumerateDirectories(_vaultDir))
            {
                string itemId = Path.GetFileName(itemDir);
                string backupItemDir = Path.Combine(_backupDir, timestamp, itemId);
                Directory.CreateDirectory(backupItemDir);

                foreach (string file in Directory.EnumerateFiles(itemDir))
                {
                    string fileName = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(backupItemDir, fileName));
                }
            }

            return backupPath;
        }

        /// <summary>
        /// Lists available backups sorted by date (newest first).
        /// </summary>
        public List<string> ListBackups()
        {
            return Directory.EnumerateFiles(_backupDir, "vault_*.gv1")
                .OrderByDescending(f => f)
                .ToList();
        }

        /// <summary>
        /// Attempts to recover vault from the most recent backup.
        /// </summary>
        private List<VaultItem> TryRecoverFromBackup()
        {
            var backups = ListBackups();
            if (backups.Count == 0)
                return new List<VaultItem>();

            string latestBackup = backups.First();
            try
            {
                string encrypted = File.ReadAllText(latestBackup);
                string? json = EncryptionService.DecryptString(encrypted, _masterPassword);
                if (json != null)
                {
                    var vaultData = JsonSerializer.Deserialize<VaultData>(json);
                    return vaultData?.Items ?? new List<VaultItem>();
                }
            }
            catch { /* Skip corrupted backups */ }

            return new List<VaultItem>();
        }

        // ── Vault Health Check ────────────────────────────────────────────────

        /// <summary>
        /// Performs a vault health check and returns any issues found.
        /// </summary>
        public List<string> CheckVaultHealth()
        {
            var issues = new List<string>();

            if (!File.Exists(_vaultIndexPath))
            {
                issues.Add("Vault index file not found.");
                return issues;
            }

            try
            {
                string encrypted = File.ReadAllText(_vaultIndexPath);
                string? json = EncryptionService.DecryptString(encrypted, _masterPassword);

                if (json == null)
                {
                    issues.Add("Cannot decrypt vault index. Password may be wrong.");
                    return issues;
                }

                var vaultData = JsonSerializer.Deserialize<VaultData>(json);
                if (vaultData?.Items == null)
                {
                    issues.Add("Vault index is empty or corrupted.");
                    return issues;
                }

                // Check each item's files exist
                foreach (var item in vaultData.Items.Where(i => i.Type == VaultItemType.Document))
                {
                    foreach (var file in item.Files)
                    {
                        if (!File.Exists(file.StoredAt))
                        {
                            issues.Add($"Missing file: {file.FileName} (Item: {item.Title})");
                        }
                    }
                }

                // Check for orphaned files (files without matching items)
                var validPaths = new HashSet<string>();
                foreach (var item in vaultData.Items.Where(i => i.Type == VaultItemType.Document))
                {
                    foreach (var file in item.Files)
                    {
                        validPaths.Add(file.StoredAt);
                    }
                }

                foreach (string itemDir in Directory.EnumerateDirectories(_vaultDir))
                {
                    foreach (string file in Directory.EnumerateFiles(itemDir))
                    {
                        if (!validPaths.Contains(file))
                        {
                            issues.Add($"Orphaned file: {file}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                issues.Add($"Health check failed: {ex.Message}");
            }

            return issues;
        }

        // ── Passphrase helpers ────────────────────────────────────────────────

        public void SavePassphrase(string phrase)
        {
            if (string.IsNullOrWhiteSpace(_masterPassword))
                throw new InvalidOperationException("Vault must be unlocked before saving the voice passphrase.");

            var settings = ApplicationData.Current.LocalSettings;
            string normalizedPhrase = NormalizePassphraseForStorage(phrase);
            string encrypted = EncryptionService.EncryptString(normalizedPhrase, _masterPassword);
            settings.Values["RegisteredPassphraseEnc"] = encrypted;

            // Clear the legacy plain key so it doesn't shadow the encrypted one
            settings.Values.Remove("RegisteredPassphrase");
        }

        public string? LoadPassphrase()
        {
            if (string.IsNullOrWhiteSpace(_masterPassword))
                return null;

            var settings = ApplicationData.Current.LocalSettings;
            string? enc = settings.Values["RegisteredPassphraseEnc"] as string;
            if (enc == null) return null;

            return EncryptionService.DecryptString(enc, _masterPassword);
        }

        private void MigratePassphraseIfNeeded()
        {
            var settings = ApplicationData.Current.LocalSettings;

            if (settings.Values["RegisteredPassphrase"] is string plaintext
                && !string.IsNullOrEmpty(plaintext)
                && settings.Values["RegisteredPassphraseEnc"] == null)
            {
                SavePassphrase(plaintext);
            }
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

            if (!Directory.Exists(_tempDir)) return;
            foreach (string f in Directory.GetFiles(_tempDir))
                EncryptionService.SecureDeleteTemp(f);
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private string GetItemDir(string itemId) =>
            Path.Combine(_vaultDir, itemId);

        private void CleanOrphanedDirectories(List<VaultItem> items)
        {
            var validIds = new HashSet<string>(items.Select(i => i.Id));
            foreach (string dir in Directory.EnumerateDirectories(_vaultDir))
            {
                string dirName = Path.GetFileName(dir);
                if (!validIds.Contains(dirName))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }

        private static string CalculateChecksum(string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToBase64String(hash);
        }

        private static string NormalizePassphraseForStorage(string phrase) =>
            string.Join(" ", phrase.Trim().ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Vault index data structure with integrity checks.
    /// </summary>
    public class VaultData
    {
        public int Version { get; set; } = 1;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
        public string Checksum { get; set; } = string.Empty;
        public List<VaultItem> Items { get; set; } = new();
    }
}
