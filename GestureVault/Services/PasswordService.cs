using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GestureVault.Services
{
    /// <summary>
    /// Handles password hashing, verification, and strength assessment.
    /// Uses Argon2-like approach with PBKDF2 (200K iterations) + salt.
    /// </summary>
    public static class PasswordService
    {
        // ── Constants ─────────────────────────────────────────────────────────
        private const int SaltSize = 32; // 256 bits
        private const int HashSize = 32; // 256 bits
        private const int Iterations = 300_000; // OWASP 2023 recommendation
        private const int MinPasswordLength = 8;
        private const int StrongPasswordLength = 14;

        // ── Output format: "iterations.salt.hash" (all base64) ───────────────
        private const char Separator = '.';

        // ── Password Hashing ──────────────────────────────────────────────────

        /// <summary>
        /// Hashes a password using PBKDF2-SHA512 with a random salt.
        /// Output format: "300000.saltBase64.hashBase64"
        /// </summary>
        public static string HashPassword(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hash = DeriveKey(password, salt, Iterations, HashSize);

            return $"{Iterations}{Separator}" +
                   $"{Convert.ToBase64String(salt)}{Separator}" +
                   $"{Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// Verifies a password against a stored hash.
        /// Constant-time comparison to prevent timing attacks.
        /// </summary>
        public static bool VerifyPassword(string password, string storedHash)
        {
            try
            {
                string[] parts = storedHash.Split(Separator);
                if (parts.Length != 3)
                    return false;

                int iterations = int.Parse(parts[0]);
                byte[] salt = Convert.FromBase64String(parts[1]);
                byte[] expectedHash = Convert.FromBase64String(parts[2]);

                byte[] actualHash = DeriveKey(password, salt, iterations, expectedHash.Length);

                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if a stored hash uses the latest security parameters.
        /// </summary>
        public static bool NeedsRehash(string storedHash)
        {
            try
            {
                string[] parts = storedHash.Split(Separator);
                if (parts.Length != 3) return true;

                int iterations = int.Parse(parts[0]);
                return iterations < Iterations;
            }
            catch
            {
                return true;
            }
        }

        // ── Password Generation ───────────────────────────────────────────────

        /// <summary>
        /// Generates a strong random password.
        /// </summary>
        public static string GeneratePassword(int length = 20,
            bool includeUppercase = true,
            bool includeLowercase = true,
            bool includeNumbers = true,
            bool includeSpecial = true)
        {
            const string uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // Excluded I,O
            const string lowercase = "abcdefghjkmnpqrstuvwxyz";   // Excluded i,l,o
            const string numbers = "23456789";                     // Excluded 0,1
            const string special = "!@#$%^&*()-_=+[]{};:,.<>?";

            var charSet = new StringBuilder();
            var required = new List<char>();

            if (includeUppercase)
            {
                charSet.Append(uppercase);
                required.Add(uppercase[RandomNumberGenerator.GetInt32(uppercase.Length)]);
            }
            if (includeLowercase)
            {
                charSet.Append(lowercase);
                required.Add(lowercase[RandomNumberGenerator.GetInt32(lowercase.Length)]);
            }
            if (includeNumbers)
            {
                charSet.Append(numbers);
                required.Add(numbers[RandomNumberGenerator.GetInt32(numbers.Length)]);
            }
            if (includeSpecial)
            {
                charSet.Append(special);
                required.Add(special[RandomNumberGenerator.GetInt32(special.Length)]);
            }

            var password = new char[length];

            // Fill with required characters first
            for (int i = 0; i < required.Count && i < length; i++)
                password[i] = required[i];

            // Fill rest with random
            for (int i = required.Count; i < length; i++)
                password[i] = charSet[RandomNumberGenerator.GetInt32(charSet.Length)];

            // Shuffle
            RandomNumberGenerator.Shuffle(password.AsSpan());

            return new string(password);
        }

        // ── Password Strength Assessment ──────────────────────────────────────

        public enum PasswordStrength
        {
            None,
            VeryWeak,
            Weak,
            Fair,
            Strong,
            VeryStrong
        }

        public class StrengthResult
        {
            public PasswordStrength Strength { get; set; }
            public string Label { get; set; } = string.Empty;
            public int Score { get; set; }
            public string Color { get; set; } = string.Empty;
            public List<string> Suggestions { get; set; } = new();
            public TimeSpan EstimatedCrackTime { get; set; }
            public string CrackTimeDescription { get; set; } = string.Empty;
        }

        /// <summary>
        /// Assesses password strength and provides detailed feedback.
        /// </summary>
        public static StrengthResult AssessStrength(string password)
        {
            var result = new StrengthResult();
            var suggestions = new List<string>();

            if (string.IsNullOrEmpty(password))
            {
                result.Strength = PasswordStrength.None;
                result.Label = "No password";
                result.Score = 0;
                result.Color = "#9CA3AF";
                result.EstimatedCrackTime = TimeSpan.Zero;
                result.CrackTimeDescription = "N/A";
                result.Suggestions = new List<string> { "Enter a password" };
                return result;
            }

            int score = 0;

            // Length scoring
            if (password.Length >= MinPasswordLength) score++;
            if (password.Length >= 12) score++;
            if (password.Length >= StrongPasswordLength) score += 2;
            if (password.Length < MinPasswordLength)
                suggestions.Add($"Use at least {MinPasswordLength} characters");

            // Character variety
            bool hasLower = Regex.IsMatch(password, @"[a-z]");
            bool hasUpper = Regex.IsMatch(password, @"[A-Z]");
            bool hasDigit = Regex.IsMatch(password, @"[0-9]");
            bool hasSpecial = Regex.IsMatch(password, @"[^A-Za-z0-9]");
            bool hasUnicode = Regex.IsMatch(password, @"[^\x00-\x7F]");

            if (hasLower && hasUpper) score += 2;
            else if (hasLower || hasUpper) score++;
            else suggestions.Add("Add both uppercase and lowercase letters");

            if (hasDigit) score++;
            else suggestions.Add("Add at least one number");

            if (hasSpecial) score += 2;
            else suggestions.Add("Add special characters (!@#$%^&*)");

            if (hasUnicode) score++;
            else suggestions.Add("Consider adding Unicode characters for extra strength");

            // Pattern penalties
            if (Regex.IsMatch(password, @"(.)\1{2,}"))
            {
                score--;
                suggestions.Add("Avoid repeated characters (e.g., 'aaa')");
            }

            if (Regex.IsMatch(password, @"(?:abc|bcd|cde|def|efg|fgh|ghi|hij|ijk|jkl|klm|lmn|mno|nop|opq|pqr|qrs|rst|stu|tuv|uvw|vwx|wxy|xyz|012|123|234|345|456|567|678|789)", RegexOptions.IgnoreCase))
            {
                score--;
                suggestions.Add("Avoid sequential characters (e.g., 'abc', '123')");
            }

            // Common pattern penalties
            if (Regex.IsMatch(password, @"password|123456|qwerty|admin|letmein|welcome|monkey|dragon", RegexOptions.IgnoreCase))
            {
                score = Math.Min(score, 1);
                suggestions.Add("Avoid common words and patterns");
            }

            // Keyboard pattern check
            string[] keyboardPatterns = { "qwerty", "asdfgh", "zxcvbn", "qazwsx" };
            foreach (var pattern in keyboardPatterns)
            {
                if (password.ToLower().Contains(pattern))
                {
                    score--;
                    suggestions.Add("Avoid keyboard patterns (e.g., 'qwerty')");
                    break;
                }
            }

            // Normalize score
            score = Math.Max(0, score);
            int maxScore = 10;

            // Calculate estimated crack time
            var entropy = CalculateEntropy(password);
            result.EstimatedCrackTime = EstimateCrackTime(entropy);
            result.CrackTimeDescription = FormatCrackTime(result.EstimatedCrackTime);

            // Map to strength levels
            double percentage = (double)score / maxScore;

            if (percentage <= 0.1)
            {
                result.Strength = PasswordStrength.VeryWeak;
                result.Label = "Very Weak";
                result.Color = "#EF4444"; // Red
            }
            else if (percentage <= 0.25)
            {
                result.Strength = PasswordStrength.Weak;
                result.Label = "Weak";
                result.Color = "#F97316"; // Orange
            }
            else if (percentage <= 0.5)
            {
                result.Strength = PasswordStrength.Fair;
                result.Label = "Fair";
                result.Color = "#EAB308"; // Yellow
            }
            else if (percentage <= 0.75)
            {
                result.Strength = PasswordStrength.Strong;
                result.Label = "Strong";
                result.Color = "#3B82F6"; // Blue
            }
            else
            {
                result.Strength = PasswordStrength.VeryStrong;
                result.Label = "Very Strong";
                result.Color = "#22C55E"; // Green
            }

            result.Score = score;
            result.Suggestions = suggestions;
            return result;
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private static byte[] DeriveKey(string password, byte[] salt, int iterations, int outputLength)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA512);
            return pbkdf2.GetBytes(outputLength);
        }

        private static double CalculateEntropy(string password)
        {
            if (string.IsNullOrEmpty(password)) return 0;

            int poolSize = 0;
            if (Regex.IsMatch(password, @"[a-z]")) poolSize += 26;
            if (Regex.IsMatch(password, @"[A-Z]")) poolSize += 26;
            if (Regex.IsMatch(password, @"[0-9]")) poolSize += 10;
            if (Regex.IsMatch(password, @"[^A-Za-z0-9]")) poolSize += 32;
            if (Regex.IsMatch(password, @"[^\x00-\x7F]")) poolSize += 100;

            if (poolSize == 0) poolSize = 26; // Default to lowercase

            return password.Length * Math.Log2(poolSize);
        }

        private static TimeSpan EstimateCrackTime(double entropy)
        {
            // Assume 100 billion guesses/second (modern GPU cluster)
            const double guessesPerSecond = 100_000_000_000;
            double totalGuesses = Math.Pow(2, entropy);
            double seconds = totalGuesses / guessesPerSecond;

            // TimeSpan.MaxValue is about 10.7 million days (~29,000 years)
            // Cap at TimeSpan.MaxValue to prevent overflow
            if (seconds > TimeSpan.MaxValue.TotalSeconds)
                return TimeSpan.MaxValue;

            if (seconds < 0) // Handle negative overflow
                return TimeSpan.MaxValue;

            return TimeSpan.FromSeconds(seconds);
        }

        private static string FormatCrackTime(TimeSpan time)
        {
            if (time == TimeSpan.MaxValue)
                return "Centuries+";

            if (time.TotalSeconds < 1) return "Instantly";
            if (time.TotalSeconds < 60) return $"{time.Seconds} seconds";
            if (time.TotalMinutes < 60) return $"{time.Minutes} minutes";
            if (time.TotalHours < 24) return $"{time.Hours} hours";
            if (time.TotalDays < 30) return $"{time.Days} days";
            if (time.TotalDays < 365) return $"{time.Days / 30} months";

            double years = time.TotalDays / 365;
            if (years < 1000) return $"{years:F0} years";
            if (years < 1_000_000) return $"{years / 1000:F0}K years";
            return $"{years / 1_000_000:F0}M years";
        }
    }
}