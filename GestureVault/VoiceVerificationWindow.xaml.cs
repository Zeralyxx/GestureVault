using GestureVault.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using System;
using Microsoft.UI.Xaml;

namespace GestureVault
{
    public sealed partial class VoiceVerificationWindow : Window
    {
        private VoiceService? _voiceService;

        public VoiceVerificationWindow()
        {
            this.InitializeComponent();
            SetWindowSizeAndCenter();
            InitializeVoiceService();
        }

        private void SetWindowSizeAndCenter()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }

        private void InitializeVoiceService()
        {
            _voiceService = new VoiceService(DispatcherQueue.GetForCurrentThread());

            // ── Success ───────────────────────────────────────────────────────
            _voiceService.PassphraseVerified += (s, e) =>
            {
                VoiceStatusText.Text = "Voice verified ✓";
                VoiceInstructionText.Text = "Access granted.";
                DetectedPhraseText.Text = "Verified. Continuing...";
                StartListeningButton.IsEnabled = false;

                // Short delay so the user can read the success state
                // before the window transitions
                var timer = DispatcherQueue.GetForCurrentThread()
                    .CreateTimer();
                timer.Interval = System.TimeSpan.FromSeconds(1.2);
                timer.IsRepeating = false;
                timer.Tick += (_, _) =>
                {
                    var gestureWindow = new GestureVerificationWindow();
                    gestureWindow.Activate();
                    this.Close();
                };
                timer.Start();
            };

            // ── Failure ───────────────────────────────────────────────────────
            _voiceService.VerificationFailed += (s, e) =>
            {
                VoiceStatusText.Text = "Not recognized";
                VoiceInstructionText.Text = e.Reason;
                DetectedPhraseText.Text = "Ready to try again.";

                // Re-enable button so user can retry
                StartListeningButton.Content = "Try Again";
                StartListeningButton.IsEnabled = true;
            };
        }

        // ── Button handlers ───────────────────────────────────────────────────
        private void StartListeningButton_Click(object sender, RoutedEventArgs e)
        {
            // Update UI to listening state
            VoiceStatusText.Text = "Listening...";
            VoiceInstructionText.Text = "Speak your passphrase now.";
            DetectedPhraseText.Text = "Listening for your passphrase.";
            StartListeningButton.IsEnabled = false;
            StartListeningButton.Content = "Listening...";

            _voiceService?.StartListening();
        }
        

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _voiceService?.StopListening();
            _voiceService?.Dispose();

            var mainWindow = new MainWindow();
            mainWindow.Activate();
            this.Close();
        }
    }
}
