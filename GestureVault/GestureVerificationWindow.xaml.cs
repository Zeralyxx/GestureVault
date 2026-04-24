using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace GestureVault
{
    public sealed partial class GestureVerificationWindow : Window
    {
        private bool isCameraRunning = false;

        public GestureVerificationWindow()
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

        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            isCameraRunning = !isCameraRunning;

            if (isCameraRunning)
            {
                GestureStatusText.Text = "Scanning gesture...";
                DetectedGestureText.Text = "Looking for registered hand gesture...";
                StartCameraButton.Content = "Verify Gesture";
            }
            else
            {
                GestureStatusText.Text = "Gesture verified";
                DetectedGestureText.Text = "Gesture matched successfully";

                // 👉 Move to voice step
                var voiceWindow = new VoiceVerificationWindow();
                voiceWindow.Activate();

                this.Close();
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = new MainWindow();
            mainWindow.Activate();

            this.Close();
        }
    }
}