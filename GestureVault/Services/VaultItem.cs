using System;
using System.Collections.Generic;

namespace GestureVault.Services
{
    public enum VaultItemType
    {
        Password,
        Note,
        Document
    }

    /// <summary>
    /// Represents a single item stored in the vault.
    /// Serialised as part of the encrypted vault.gv1 metadata blob.
    /// </summary>
    public class VaultItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public VaultItemType Type { get; set; } = VaultItemType.Password;
        public string Title { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = DateTime.Now.ToString("MMM dd, yyyy");

        // ── Password fields ───────────────────────────────────────────────────
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Url { get; set; }

        // ── Note fields ───────────────────────────────────────────────────────
        public string? NoteContent { get; set; }

        // ── Document fields ───────────────────────────────────────────────────
        public string? Description { get; set; }
        public List<VaultFile> Files { get; set; } = new();
    }

    /// <summary>
    /// Describes a single encrypted file attached to a Document VaultItem.
    /// </summary>
    public class VaultFile
    {
        public string FileId { get; set; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = string.Empty;

        /// <summary>Full path to the .gv1 encrypted file on disk.</summary>
        public string StoredAt { get; set; } = string.Empty;

        public string AddedAt { get; set; } = DateTime.Now.ToString("MMM dd, yyyy");
    }
}