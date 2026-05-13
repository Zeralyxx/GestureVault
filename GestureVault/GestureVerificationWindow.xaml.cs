using GestureVault.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using System;
using System.Linq;

namespace GestureVault
{
    public sealed partial class GestureVerificationWindow : Window
    {
        private GestureService? _gestureService;
        private bool _isTransitioning = false;
        private string[] _gestureSequence = { "SWIPE_RIGHT", "SWIPE_RIGHT", "SWIPE_RIGHT" };
        private int _gestureIndex = 0;
        private bool _waitingForNextGesture = false;
        private string _expectedGesture => _gestureSequence[_gestureIndex];

        public GestureVerificationWindow()
        {
            this.InitializeComponent();
            SetWindowSizeAndCenter();
            LoadExpectedGesture();
            UpdateGestureStepUi();
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
            var settings = ApplicationData.Current.LocalSettings;
            string? savedSequence = settings.Values["RegisteredGestureSequence"] as string;
            if (!string.IsNullOrWhiteSpace(savedSequence))
            {
                var gestures = savedSequence
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(3)
                    .ToArray();
                if (gestures.Length == 3)
                {
                    _gestureSequence = gestures;
                    return;
                }
            }

            string savedGesture = settings.Values["RegisteredGesture"] as string ?? "SWIPE_RIGHT";
            _gestureSequence = new[] { savedGesture, savedGesture, savedGesture };
        }

        private void InitializeGestureService()
        {
            _gestureService = new GestureService(DispatcherQueue.GetForCurrentThread());

            // ── Camera preview frames ─────────────────────────────────────────
            _gestureService.FrameReady += async (s, frame) =>
            {
                if (_isTransitioning || _waitingForNextGesture) return;

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

                    DetectedGestureText.Text = "Gesture detected";

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
            _waitingForNextGesture = true;
            GestureStatusText.Text = $"{GestureStepLabel()} recognized";
            DetectedGestureText.Text = "Gesture accepted";
            NextGestureButton.Content = _gestureIndex == _gestureSequence.Length - 1
                ? "Unlock Vault"
                : "Next Gesture";
            NextGestureButton.IsEnabled = true;
            RedoGestureButton.IsEnabled = true;
        }
        private void GestureMismatched(string detected)
        {
            GestureStatusText.Text = "Wrong gesture";
            DetectedGestureText.Text = "Gesture not accepted. Return your palm to the center and try again.";

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

        private string GestureStepLabel() => _gestureIndex switch
        {
            0 => "First gesture",
            1 => "Second gesture",
            _ => "Final gesture"
        };

        private void UpdateGestureStepUi()
        {
            GestureStepText.Text = $"{GestureStepLabel()} of 3";
            GestureStatusText.Text = $"Do {GestureStepLabel().ToLowerInvariant()}";
            DetectedGestureText.Text = "Place an open palm in the center, then perform your registered swipe.";
            NextGestureButton.Content = "Next Gesture";
            NextGestureButton.IsEnabled = false;
            RedoGestureButton.IsEnabled = false;
            _waitingForNextGesture = false;
        }

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
                    UpdateGestureStepUi();
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
                NextGestureButton.IsEnabled = false;
                RedoGestureButton.IsEnabled = false;
                _waitingForNextGesture = false;
                StartCameraButton.Content = "Start Camera";
            }
        }

        private void RedoGestureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            UpdateGestureStepUi();
        }

        private void NextGestureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning || !_waitingForNextGesture) return;

            if (_gestureIndex >= _gestureSequence.Length - 1)
            {
                _isTransitioning = true;
                GestureStatusText.Text = "All gestures matched";
                DetectedGestureText.Text = "Access granted. Opening vault...";
                StartCameraButton.IsEnabled = false;
                NextGestureButton.IsEnabled = false;
                NavigateToVault();
                return;
            }

            _gestureIndex++;
            UpdateGestureStepUi();
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

        private async void ShowHintButton_Click(object sender, RoutedEventArgs e)
        {
            string hint = ApplicationData.Current.LocalSettings.Values["GestureHint"] as string ?? string.Empty;
            var dialog = new ContentDialog
            {
                Title = "Gesture hint",
                Content = string.IsNullOrWhiteSpace(hint)
                    ? "No gesture hint has been saved."
                    : hint,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

    }
}
