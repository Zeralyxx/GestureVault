using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Graphics;

namespace GestureVault
{
    public sealed partial class MainWindow : Window
    {
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
            {
                presenter.Maximize();
            }
        }

        private bool isPasswordVisible = false;

        private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (isPasswordVisible)
            {
                MasterPasswordBox.Password = PasswordTextBox.Text;
                MasterPasswordBox.Visibility = Visibility.Visible;
                PasswordTextBox.Visibility = Visibility.Collapsed;
                EyeIcon.Text = "👁";
            }
            else
            {
                PasswordTextBox.Text = MasterPasswordBox.Password;
                PasswordTextBox.Visibility = Visibility.Visible;
                MasterPasswordBox.Visibility = Visibility.Collapsed;
                EyeIcon.Text = "🙈";
            }

            isPasswordVisible = !isPasswordVisible;
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            string password = isPasswordVisible ? PasswordTextBox.Text : MasterPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(password))
            {
                ContentDialog dialog = new ContentDialog
                {
                    Title = "Missing field",
                    Content = "Please enter your master password.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };

                _ = dialog.ShowAsync();
                return;
            }

            // 👉 Navigate to Gesture step
            var gestureWindow = new GestureVerificationWindow();
            gestureWindow.Activate();

            this.Close();
        }
        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}