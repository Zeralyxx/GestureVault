using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace GestureVault
{
    public sealed partial class VoiceVerificationWindow : Window
    {
        private bool isListening = false;

        public VoiceVerificationWindow()
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

        private void StartListeningButton_Click(object sender, RoutedEventArgs e)
        {
            isListening = !isListening;

            if (isListening)
            {
                VoiceStatusText.Text = "Listening...";
                VoiceInstructionText.Text = "Speak your passphrase.";
                StartListeningButton.Content = "Verify Voice";
            }
            else
            {
                VoiceStatusText.Text = "Voice verified";
                VoiceInstructionText.Text = "Access granted.";

                var vaultWindow = new VaultDashboardWindow();
                vaultWindow.Activate();

                this.Close();
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            var gestureWindow = new GestureVerificationWindow();
            gestureWindow.Activate();

            this.Close();
        }
    }
}