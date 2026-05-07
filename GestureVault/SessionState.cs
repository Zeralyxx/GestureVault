namespace GestureVault
{
    /// <summary>
    /// Holds in-memory session data that only lives while the vault is unlocked.
    /// Cleared on every lock or app exit.
    /// </summary>
    public static class SessionState
    {
        /// <summary>
        /// The plaintext master password for the current session.
        /// Used by VaultStorageService to derive the AES-256 key.
        /// Never written to disk.
        /// </summary>
        public static string MasterPassword { get; set; } = string.Empty;
    }
}