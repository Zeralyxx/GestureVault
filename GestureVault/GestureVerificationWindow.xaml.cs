using GestureVault.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using System;

namespace GestureVault
{
    public sealed partial class GestureVerificationWindow : Window
    {
        private GestureService? _gestureService;
        private bool _isTransitioning = false;
        private string _expectedGesture = "SWIPE_RIGHT";

        public GestureVerificationWindow()
        {
            this.InitializeComponent();
            SetWindowSizeAndCenter();
            LoadExpectedGesture();
            InitializeGestureService();
        }

        private void SetWindowSizeAndCenter()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }

        private void LoadExpectedGesture()
        {
            _expectedGesture = ApplicationData.Current.LocalSettings
                .Values["RegisteredGesture"] as string ?? "SWIPE_RIGHT";
        }

        private void InitializeGestureService()
        {
            _gestureService = new GestureService(DispatcherQueue.GetForCurrentThread());

            // ── Camera preview frames ─────────────────────────────────────────
            _gestureService.FrameReady += async (s, frame) =>
            {
                if (_isTransitioning) return;

                try
                {
                    var bitmapSource = await ImageConverter.MatToSoftwareBitmapSource(frame);

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        CameraPreview.Source = bitmapSource;
                    });
                }
                catch
                {
                    // Frame conversion failed - skip this frame
                }
            };

            // ── Gesture detected ──────────────────────────────────────────────
            _gestureService.GestureDetected += (s, e) =>
            {
                if (_isTransitioning) return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    string gestureName = e.Direction switch
                    {
                        "SWIPE_RIGHT" => "👋 Swipe Right",
                        "SWIPE_LEFT" => "👈 Swipe Left",
                        "SWIPE_UP" => "👆 Swipe Up",
                        "SWIPE_DOWN" => "👇 Swipe Down",
                        _ => e.Direction
                    };

                    DetectedGestureText.Text = $"Detected: {gestureName}";

                    if (e.Direction == _expectedGesture)
                    {
                        GestureMatched();
                    }
                    else
                    {
                        GestureMismatched(e.Direction);
                    }
                });
            };
        }

        private void GestureMatched()
        {
            GestureStatusText.Text = "Gesture matched! ✓";
            DetectedGestureText.Text = $"Correct gesture: {GestureDirectionToEmoji(_expectedGesture)}";
            StartCameraButton.Content = "Access Granted ✓";
            StartCameraButton.IsEnabled = false;

            // Delay then navigate
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = System.TimeSpan.FromSeconds(1.2);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (_isTransitioning) return;
                _isTransitioning = true;
                NavigateToVault();
            };
            timer.Start();
        }

        private void GestureMismatched(string detected)
        {
            string expectedEmoji = GestureDirectionToEmoji(_expectedGesture);
            string detectedEmoji = GestureDirectionToEmoji(detected);

            GestureStatusText.Text = "Wrong gesture";
            DetectedGestureText.Text = $"Expected: {expectedEmoji}\nDetected: {detectedEmoji}\nTry again!";

            // Flash the detection area to indicate mismatch
            // (Simple version: just show text, could add animation later)
        }

        private static string GestureDirectionToEmoji(string direction) => direction switch
        {
            "SWIPE_RIGHT" => "👋 Swipe Right",
            "SWIPE_LEFT" => "👈 Swipe Left",
            "SWIPE_UP" => "👆 Swipe Up",
            "SWIPE_DOWN" => "👇 Swipe Down",
            _ => direction
        };

        private void NavigateToVault()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _gestureService?.Stop();
                _gestureService?.Dispose();
                _gestureService = null;

                var vaultWindow = new VaultDashboardWindow();
                vaultWindow.Activate();
                this.Close();
            });
        }

        // ── Button handlers ───────────────────────────────────────────────────
        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;

            if (_gestureService == null || !_gestureService.IsRunning)
            {
                try
                {
                    _gestureService?.Start();

                    // Hide placeholder, show camera
                    CameraPlaceholder.Visibility = Visibility.Collapsed;
                    GestureStatusText.Text = "Scanning for gesture...";
                    DetectedGestureText.Text = $"Show your registered gesture: {GestureDirectionToEmoji(_expectedGesture)}";
                    StartCameraButton.Content = "Stop Camera";
                }
                catch (Exception ex)
                {
                    GestureStatusText.Text = "Camera error";
                    DetectedGestureText.Text = ex.Message;
                    StartCameraButton.Content = "Retry";
                }
            }
            else
            {
                _gestureService?.Stop();

                CameraPlaceholder.Visibility = Visibility.Visible;
                CameraPreview.Source = null;
                GestureStatusText.Text = "Camera stopped";
                DetectedGestureText.Text = "Waiting for hand gesture...";
                StartCameraButton.Content = "Start Camera";
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            _gestureService?.Stop();
            _gestureService?.Dispose();
            _gestureService = null;

            var voiceWindow = new VoiceVerificationWindow();
            voiceWindow.Activate();
            this.Close();
        }

        // Test (for demo)
        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            _gestureService?.Stop();
            _gestureService?.Dispose();
            _gestureService = null;

            NavigateToVault();
        }

    }
}