using GestureVault.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

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
                AuthAttemptService.Reset("Voice");
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
                string remainingMessage = AuthAttemptService.RegisterFailure("Voice");
                VoiceStatusText.Text = "Not recognized";
                VoiceInstructionText.Text = $"{e.Reason} {remainingMessage}";
                DetectedPhraseText.Text = string.IsNullOrWhiteSpace(e.HeardText)
                    ? "Ready to try again."
                    : e.HeardText;

                if (AuthAttemptService.IsLocked("Voice", out string lockoutMessage))
                {
                    VoiceInstructionText.Text = lockoutMessage;
                    StartListeningButton.Content = "Locked";
                    StartListeningButton.IsEnabled = false;
                    return;
                }

                StartListeningButton.Content = "Try Again";
                StartListeningButton.IsEnabled = true;
            };
        }

        // ── Button handlers ───────────────────────────────────────────────────
        private void StartListeningButton_Click(object sender, RoutedEventArgs e)
        {
            if (AuthAttemptService.IsLocked("Voice", out string lockoutMessage))
            {
                VoiceStatusText.Text = "Voice locked";
                VoiceInstructionText.Text = lockoutMessage;
                StartListeningButton.Content = "Locked";
                StartListeningButton.IsEnabled = false;
                return;
            }

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

        private async void ShowHintButton_Click(object sender, RoutedEventArgs e)
        {
            string hint = ApplicationData.Current.LocalSettings.Values["PassphraseHint"] as string ?? string.Empty;
            var dialog = new ContentDialog
            {
                Title = "Passphrase hint",
                Content = string.IsNullOrWhiteSpace(hint)
                    ? "No passphrase hint has been saved."
                    : hint,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}
