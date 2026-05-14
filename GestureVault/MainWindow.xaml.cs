using GestureVault.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Storage;

namespace GestureVault
{
    public sealed partial class MainWindow : Window
    {
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
            }
            else
            {
                PasswordTextBox.Text = MasterPasswordBox.Password;
                PasswordTextBox.Visibility = Visibility.Visible;
                MasterPasswordBox.Visibility = Visibility.Collapsed;
            }

            _isPasswordVisible = !_isPasswordVisible;
            EyeIcon.Text = _isPasswordVisible ? "\uE9A9" : "\uE9A8";
        }

        // ── Continue button ───────────────────────────────────────────────────
        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            if (AuthAttemptService.IsLocked("Password", out string lockoutMessage))
            {
                ShowDialog("Password locked", lockoutMessage);
                return;
            }

            string entered = _isPasswordVisible
                ? PasswordTextBox.Text
                : MasterPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(entered))
            {
                ShowDialog("Missing field", "Please enter your master password.");
                return;
            }

            string? storedHash = ApplicationData.Current.LocalSettings
                                     .Values["MasterPasswordHash"] as string;

            if (storedHash == null || !PasswordService.VerifyPassword(entered, storedHash))
            {
                string remainingMessage = AuthAttemptService.RegisterFailure("Password");
                ShowDialog("Incorrect password",
                    $"The password you entered is incorrect. {remainingMessage}");

                MasterPasswordBox.Password = string.Empty;
                PasswordTextBox.Text = string.Empty;
                return;
            }

            AuthAttemptService.Reset("Password");

            // Check if hash needs upgrading
            if (PasswordService.NeedsRehash(storedHash))
            {
                ApplicationData.Current.LocalSettings.Values["MasterPasswordHash"] =
                    PasswordService.HashPassword(entered);
            }

            SessionState.MasterPassword = entered;
            var voiceWindow = new VoiceVerificationWindow();
            voiceWindow.Activate();
            this.Close();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void ShowHintButton_Click(object sender, RoutedEventArgs e)
        {
            string hint = ApplicationData.Current.LocalSettings.Values["PasswordHint"] as string ?? string.Empty;
            ShowDialog("Password hint",
                string.IsNullOrWhiteSpace(hint)
                    ? "No password hint has been saved."
                    : hint);
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
