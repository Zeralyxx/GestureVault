using System;
using Windows.Storage;

namespace GestureVault.Services
{
    internal static class AuthAttemptService
    {
        private const int MaxAttempts = 3;
        private const int DefaultLockoutSeconds = 30;

        public static bool IsLocked(string factor, out string message)
        {
            long lockedUntilTicks = GetLong($"{factor}LockedUntilTicks");
            DateTime now = DateTime.UtcNow;
            int lockoutSeconds = GetLockoutSeconds();
            long maxLockedUntilTicks = now.AddSeconds(lockoutSeconds).Ticks;
            if (lockedUntilTicks > maxLockedUntilTicks)
            {
                lockedUntilTicks = maxLockedUntilTicks;
                Settings.Values[$"{factor}LockedUntilTicks"] = lockedUntilTicks;
            }

            if (lockedUntilTicks <= now.Ticks)
            {
                message = string.Empty;
                return false;
            }

            var remaining = TimeSpan.FromTicks(lockedUntilTicks - now.Ticks);
            message = $"Too many failed attempts. Try again in {FormatDuration(remaining)}.";
            return true;
        }

        public static string RegisterFailure(string factor)
        {
            int attempts = GetInt($"{factor}FailedAttempts") + 1;
            Settings.Values[$"{factor}FailedAttempts"] = attempts;

            if (attempts < MaxAttempts)
                return $"{MaxAttempts - attempts} attempt(s) remaining before a temporary lockout.";

            int lockoutSeconds = GetLockoutSeconds();
            Settings.Values[$"{factor}LockedUntilTicks"] = DateTime.UtcNow.AddSeconds(lockoutSeconds).Ticks;
            Settings.Values[$"{factor}FailedAttempts"] = 0;
            return $"Too many failed attempts. This step is locked for {FormatDuration(TimeSpan.FromSeconds(lockoutSeconds))}.";
        }

        public static void Reset(string factor)
        {
            Settings.Values[$"{factor}FailedAttempts"] = 0;
            Settings.Values[$"{factor}LockedUntilTicks"] = 0L;
        }

        private static ApplicationDataContainer Settings => ApplicationData.Current.LocalSettings;

        private static int GetLockoutSeconds()
        {
            int seconds = GetInt("AuthLockoutSeconds");
            return seconds > 0 ? seconds : DefaultLockoutSeconds;
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes >= 1)
                return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))} minute(s)";

            return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))} second(s)";
        }

        private static int GetInt(string key) =>
            Settings.Values[key] is int value ? value : 0;

        private static long GetLong(string key) =>
            Settings.Values[key] switch
            {
                long value => value,
                int value => value,
                _ => 0L
            };
    }
}
