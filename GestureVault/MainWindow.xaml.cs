using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;

namespace GestureVault
{
    public sealed partial class MainWindow : Window
    {
        // ── TEMP placeholder password — remove once RegistrationWindow is built ──
        // SHA256 hash of "Vault123!" — this is what the user must type to get in
        private const string PlaceholderPasswordHash =
            "B9C950640A1D2A4B6F1E0A3E82F2F7C8D4F5E6A7B8C9D0E1F2A3B4C5D6E7F8A9";

        private bool _isPasswordVisible = false;

        public MainWindow()
        {
            this.InitializeComponent();
            SetWindowSizeAndCenter();
        }

        private void SetWindowSizeAndCenter()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }

        // ── Eye toggle ────────────────────────────────────────────────────────
        private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPasswordVisible)
            {
                MasterPasswordBox.Password = PasswordTextBox.Text;
                MasterPasswordBox.Visibility = Visibility.Visible;
                PasswordTextBox.Visibility = Visibility.Collapsed;
                EyeIcon.Text = "\uE7B3";
            }
            else
            {
                PasswordTextBox.Text = MasterPasswordBox.Password;
                PasswordTextBox.Visibility = Visibility.Visible;
                MasterPasswordBox.Visibility = Visibility.Collapsed;
                EyeIcon.Text = "\uED1A";
            }

            _isPasswordVisible = !_isPasswordVisible;
        }

        // ── Continue button ───────────────────────────────────────────────────
        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            string entered = _isPasswordVisible
                ? PasswordTextBox.Text
                : MasterPasswordBox.Password;

            // 1. Empty check
            if (string.IsNullOrWhiteSpace(entered))
            {
                ShowDialog("Missing field", "Please enter your master password.");
                return;
            }

            // 2. Hash what the user typed and compare to stored hash
            string enteredHash = HashPassword(entered);
            string? storedHash = ApplicationData.Current.LocalSettings
                                     .Values["MasterPasswordHash"] as string;

            if (storedHash == null || enteredHash != storedHash)
            {
                ShowDialog("Incorrect password",
                    "The password you entered is incorrect. Please try again.");

                // Clear the field so they start fresh
                MasterPasswordBox.Password = string.Empty;
                PasswordTextBox.Text = string.Empty;
                return;
            }


            SessionState.MasterPassword = entered;

            // 3. Password correct — move to voice step
            var voiceWindow = new VoiceVerificationWindow();
            voiceWindow.Activate();
            this.Close();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string HashPassword(string password)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes); // uppercase hex string
        }

        private async void ShowDialog(string title, string message)
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