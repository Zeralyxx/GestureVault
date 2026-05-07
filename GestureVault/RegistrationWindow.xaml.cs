using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Storage;
using Windows.UI;

namespace GestureVault
{
    public sealed partial class RegistrationWindow : Window
    {
        // Tracks which password field is currently visible
        private bool _newPasswordVisible = false;
        private bool _confirmPasswordVisible = false;

        public RegistrationWindow()
        {
            this.InitializeComponent();
            SetWindowSizeAndCenter();

            // Live strength meter as the user types
            NewPasswordBox.PasswordChanged += (_, _) => UpdateStrength();
            NewPasswordTextBox.TextChanged += (_, _) => UpdateStrength();
        }

        private void SetWindowSizeAndCenter()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow.Presenter is OverlappedPresenter p) p.Maximize();
        }

        // ── Eye toggles ───────────────────────────────────────────────────────
        private void ToggleNewPassword_Click(object sender, RoutedEventArgs e)
        {
            _newPasswordVisible = !_newPasswordVisible;
            if (_newPasswordVisible)
            {
                NewPasswordTextBox.Text = NewPasswordBox.Password;
                NewPasswordTextBox.Visibility = Visibility.Visible;
                NewPasswordBox.Visibility = Visibility.Collapsed;
                EyeIcon1.Text = "\uED1A";
            }
            else
            {
                NewPasswordBox.Password = NewPasswordTextBox.Text;
                NewPasswordBox.Visibility = Visibility.Visible;
                NewPasswordTextBox.Visibility = Visibility.Collapsed;
                EyeIcon1.Text = "\uE7B3";
            }
        }

        private void ToggleConfirmPassword_Click(object sender, RoutedEventArgs e)
        {
            _confirmPasswordVisible = !_confirmPasswordVisible;
            if (_confirmPasswordVisible)
            {
                ConfirmPasswordTextBox.Text = ConfirmPasswordBox.Password;
                ConfirmPasswordTextBox.Visibility = Visibility.Visible;
                ConfirmPasswordBox.Visibility = Visibility.Collapsed;
                EyeIcon2.Text = "\uED1A";
            }
            else
            {
                ConfirmPasswordBox.Password = ConfirmPasswordTextBox.Text;
                ConfirmPasswordBox.Visibility = Visibility.Visible;
                ConfirmPasswordTextBox.Visibility = Visibility.Collapsed;
                EyeIcon2.Text = "\uE7B3";
            }
        }

        // ── Password strength meter ───────────────────────────────────────────
        private void UpdateStrength()
        {
            string pw = _newPasswordVisible
                ? NewPasswordTextBox.Text
                : NewPasswordBox.Password;

            var (score, label, color) = ScorePassword(pw);

            StrengthLabel.Text = label;
            StrengthLabel.Foreground = new SolidColorBrush(color);

            // Animate bar width relative to the card width (≈ 404px inner)
            // Score 0-4 maps to 0%, 25%, 50%, 75%, 100%
            double pct = score / 4.0;
            // The bar's parent grid fills the card; we use a fixed 360px max
            StrengthBar.Width = pct * 360;
            StrengthBar.Background = new SolidColorBrush(color);
        }

        private static (int score, string label, Color color) ScorePassword(string pw)
        {
            if (string.IsNullOrEmpty(pw))
                return (0, "—", Color.FromArgb(255, 156, 163, 175));

            int score = 0;
            if (pw.Length >= 8) score++;
            if (pw.Length >= 12) score++;
            if (Regex.IsMatch(pw, @"[A-Z]") && Regex.IsMatch(pw, @"[a-z]")) score++;
            if (Regex.IsMatch(pw, @"[0-9]")) score++;
            if (Regex.IsMatch(pw, @"[^A-Za-z0-9]")) score++;

            // Clamp to 4
            score = Math.Min(score, 4);

            return score switch
            {
                0 or 1 => (score, "Weak", Color.FromArgb(255, 239, 68, 68)),
                2 => (score, "Fair", Color.FromArgb(255, 245, 158, 11)),
                3 => (score, "Good", Color.FromArgb(255, 59, 130, 246)),
                _ => (score, "Strong", Color.FromArgb(255, 34, 197, 94))
            };
        }

        // ── Step navigation ───────────────────────────────────────────────────
        private async void Step1Next_Click(object sender, RoutedEventArgs e)
        {
            string pw = _newPasswordVisible
                ? NewPasswordTextBox.Text
                : NewPasswordBox.Password;
            string confirm = _confirmPasswordVisible
                ? ConfirmPasswordTextBox.Text
                : ConfirmPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(pw))
            {
                await ShowDialog("Password required", "Please enter a master password.");
                return;
            }
            if (pw.Length < 8)
            {
                await ShowDialog("Password too short",
                    "Your master password must be at least 8 characters.");
                return;
            }
            if (pw != confirm)
            {
                await ShowDialog("Passwords don't match",
                    "The two passwords you entered are different. Please try again.");
                ConfirmPasswordBox.Password = string.Empty;
                ConfirmPasswordTextBox.Text = string.Empty;
                return;
            }

            AdvanceTo(2);
        }

        private void Step2Back_Click(object sender, RoutedEventArgs e) => AdvanceTo(1);

        private async void Step2Next_Click(object sender, RoutedEventArgs e)
        {
            string phrase = PassphraseBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(phrase))
            {
                await ShowDialog("Passphrase required",
                    "Please enter a voice passphrase before continuing.");
                return;
            }
            if (phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            {
                await ShowDialog("Passphrase too short",
                    "Your passphrase should contain at least two words so it's easy to speak consistently.");
                return;
            }

            AdvanceTo(3);
        }

        private void Step3Back_Click(object sender, RoutedEventArgs e) => AdvanceTo(2);

        private void Step3Finish_Click(object sender, RoutedEventArgs e)
        {
            string pw = _newPasswordVisible
                ? NewPasswordTextBox.Text
                : NewPasswordBox.Password;

            var settings = ApplicationData.Current.LocalSettings;

            // ── Persist master password hash ──────────────────────────────────────
            settings.Values["MasterPasswordHash"] = HashPassword(pw);

            // ── Persist voice passphrase — encrypted, not plaintext ───────────────
            // VaultStorageService.SavePassphrase() derives the same AES-256-GCM key
            // from the master password and stores only the ciphertext in LocalSettings
            // under "RegisteredPassphraseEnc".  The old "RegisteredPassphrase"
            // plaintext key is never written.
            var storage = new GestureVault.Services.VaultStorageService();
            storage.UnlockWithPassword(pw);                      // sets the key material
            storage.SavePassphrase(PassphraseBox.Text.Trim());   // encrypts + stores
            storage.Lock();                                       // clears key from RAM

            // ── Gesture: saved as placeholder ─────────────────────────────────────
            settings.Values["RegisteredGesture"] = GestureCombo.SelectedIndex switch
            {
                0 => "SWIPE_RIGHT",
                1 => "SWIPE_LEFT",
                2 => "SWIPE_UP",
                3 => "SWIPE_DOWN",
                _ => "SWIPE_RIGHT"
            };

            // Mark registration complete so App skips this window next launch
            settings.Values["RegistrationComplete"] = true;

            var mainWindow = new MainWindow();
            mainWindow.Activate();
            this.Close();
        }

        // ── Step UI switcher ──────────────────────────────────────────────────
        private void AdvanceTo(int step)
        {
            Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

            // Update step dots
            SetDotActive(Step1Dot, Step2DotText, step >= 1);
            SetDotActive(Step2Dot, Step2DotText, step >= 2);
            SetDotActive(Step3Dot, Step3DotText, step >= 3);
        }

        private static void SetDotActive(Border dot, TextBlock label, bool active)
        {
            dot.Background = active
                ? new SolidColorBrush(Color.FromArgb(255, 79, 142, 247))  // AccentBrush
                : new SolidColorBrush(Color.FromArgb(255, 217, 226, 242)); // CardBorderBrush
            label.Foreground = active
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string HashPassword(string password)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes);
        }

        private async System.Threading.Tasks.Task ShowDialog(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}