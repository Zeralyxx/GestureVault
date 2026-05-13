using GestureVault.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;
using Windows.UI;

namespace GestureVault
{
    public sealed partial class RegistrationWindow : Window
    {
        // Tracks which password field is currently visible
        private bool _newPasswordVisible = false;
        private bool _confirmPasswordVisible = false;
        private GestureService? _registrationGestureService;
        private string _registeredGestureDirection = "SWIPE_RIGHT"; // default
        private readonly List<string> _registeredGestureSequence = new();
        private int _registrationGestureIndex = 0;
        private bool _waitingForNextRegistrationGesture = false;
        private bool _gestureRecorded = false;

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
                EyeIcon1.Text = "🙈";
            }
            else
            {
                NewPasswordBox.Password = NewPasswordTextBox.Text;
                NewPasswordBox.Visibility = Visibility.Visible;
                NewPasswordTextBox.Visibility = Visibility.Collapsed;
                EyeIcon1.Text = "👁";
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
                EyeIcon2.Text = "🙈";
            }
            else
            {
                ConfirmPasswordBox.Password = ConfirmPasswordTextBox.Text;
                ConfirmPasswordBox.Visibility = Visibility.Visible;
                ConfirmPasswordTextBox.Visibility = Visibility.Collapsed;
                EyeIcon2.Text = "👁";
            }
        }

        // ── Password strength meter ───────────────────────────────────────────
        private void UpdateStrength()
        {
            string pw = _newPasswordVisible
                ? NewPasswordTextBox.Text
                : NewPasswordBox.Password;

            var strength = PasswordService.AssessStrength(pw);

            StrengthLabel.Text = $"{strength.Label} ({strength.CrackTimeDescription})";
            StrengthLabel.Foreground = new SolidColorBrush(ParseColor(strength.Color));

            double pct = strength.Score / 10.0;
            StrengthBar.Width = pct * 360;
            StrengthBar.Background = new SolidColorBrush(ParseColor(strength.Color));
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

            // Use new strength assessment
            var strength = PasswordService.AssessStrength(pw);
            if (strength.Strength <= PasswordService.PasswordStrength.Weak)
            {
                string message = "Your password is too weak.\n\n";
                if (strength.Suggestions.Count > 0)
                {
                    message += "Suggestions:\n• " + string.Join("\n• ", strength.Suggestions);
                }
                await ShowDialog("Weak Password", message);
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

        private async void Step3Finish_Click(object sender, RoutedEventArgs e)
        {
            FinishSetupButton.IsEnabled = false;

            try
            {
            // Stop camera if running
            _registrationGestureService?.Stop();
            _registrationGestureService?.Dispose();
            _registrationGestureService = null;

            string pw = _newPasswordVisible
                ? NewPasswordTextBox.Text
                : NewPasswordBox.Password;

            var settings = ApplicationData.Current.LocalSettings;

            // Save username
            settings.Values["Username"] = "User";

            // Store password with secure hashing
            settings.Values["MasterPasswordHash"] = PasswordService.HashPassword(pw);
            settings.Values["PasswordHint"] = PasswordHintBox.Text.Trim();
            settings.Values["PassphraseHint"] = PassphraseHintBox.Text.Trim();
            settings.Values["GestureHint"] = GestureHintBox.Text.Trim();

            // Persist voice passphrase — encrypted
            var storage = new VaultStorageService();
            storage.UnlockWithPassword(pw);
            storage.SavePassphrase(PassphraseBox.Text.Trim());
            storage.Lock();

            // ── Gesture: save the ACTUALLY detected gesture ─────────────────────────
            settings.Values["RegisteredGesture"] = _registeredGestureDirection;
            settings.Values["RegisteredGestureSequence"] = string.Join("|",
                _registeredGestureSequence.Count >= 3
                    ? _registeredGestureSequence.Take(3)
                    : Enumerable.Repeat(_registeredGestureDirection, 3));

            // Mark registration complete
            settings.Values["RegistrationComplete"] = true;

            var mainWindow = new MainWindow();
            mainWindow.Activate();
            this.Close();
            }
            catch (Exception ex)
            {
                FinishSetupButton.IsEnabled = _gestureRecorded;
                await ShowDialog("Setup could not be saved", ex.Message);
            }
        }

        // ── Step UI switcher ──────────────────────────────────────────────────
        private void AdvanceTo(int step)
        {
            Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

            // Initialize gesture service when entering step 3
            if (step == 3)
            {
                _gestureRecorded = false;
                _registeredGestureSequence.Clear();
                _registrationGestureIndex = 0;
                _waitingForNextRegistrationGesture = false;
                GestureRecordedBadge.Visibility = Visibility.Collapsed;
                RegistrationDetectedGesture.Text = "No gesture detected yet";
                RegistrationCameraStatus.Text = "Click 'Start Camera' to begin";
                RegistrationGestureInstruction.Text = $"{RegistrationGestureStepLabel()}: perform a clear swipe.";
                RegistrationCameraPlaceholder.Visibility = Visibility.Visible;
                RegistrationCameraPreview.Source = null;
                StartRegistrationCameraButton.Content = "📷 Start Camera";
                NextRegistrationGestureButton.Content = "Next Gesture";
                NextRegistrationGestureButton.IsEnabled = false;
                FinishSetupButton.IsEnabled = false;
            }
            else
            {
                // Stop camera when leaving step 3
                _registrationGestureService?.Stop();
                _registrationGestureService?.Dispose();
                _registrationGestureService = null;
            }

            SetDotActive(Step1Dot, Step1DotText, step >= 1);
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

        // Add this method to initialize gesture service when entering Step 3:
        private void InitializeRegistrationGesture()
        {
            _registrationGestureService = new GestureService(DispatcherQueue.GetForCurrentThread());

            // Frame preview
            _registrationGestureService.FrameReady += async (s, frame) =>
            {
                try
                {
                    var bitmapSource = await ImageConverter.MatToSoftwareBitmapSource(frame);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        RegistrationCameraPreview.Source = bitmapSource;
                    });
                }
                catch { /* Skip bad frames */ }
            };

            // Gesture detected
            _registrationGestureService.GestureDetected += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_registeredGestureSequence.Count >= 3 || _waitingForNextRegistrationGesture)
                    {
                        return;
                    }

                    string gestureName = GestureDirectionToLabel(e.Direction);

                    _registeredGestureSequence.Add(e.Direction);
                    _waitingForNextRegistrationGesture = true;
                    RegistrationDetectedGesture.Text = gestureName;

                    _registeredGestureDirection = e.Direction;
                    _gestureRecorded = _registeredGestureSequence.Count >= 3;

                    GestureRecordedBadge.Visibility = Visibility.Visible;
                    RecordedGestureText.Text = $"Registered: {GestureSequenceLabel()}";
                    NextRegistrationGestureButton.Content = "Next Gesture";
                    NextRegistrationGestureButton.IsEnabled = !_gestureRecorded;
                    FinishSetupButton.IsEnabled = _gestureRecorded;
                    RegistrationGestureInstruction.Text = _gestureRecorded
                        ? "All 3 gestures recorded. Click Finish Setup."
                        : $"{RegistrationGestureStepLabel()} recorded. Click Next Gesture when you are ready.";
                });
            };
        }

        // Camera button handler for registration:
        private void StartRegistrationCamera_Click(object sender, RoutedEventArgs e)
        {
            if (_registrationGestureService == null)
                InitializeRegistrationGesture();

            if (!_registrationGestureService!.IsRunning)
            {
                try
                {
                    _registrationGestureService.Start();
                    RegistrationCameraPlaceholder.Visibility = Visibility.Collapsed;
                    RegistrationCameraStatus.Text = "Camera active - perform your swipe now";
                    RegistrationGestureInstruction.Text = $"{RegistrationGestureStepLabel()}: perform a clear swipe.";
                    StartRegistrationCameraButton.Content = "⏹ Stop Camera";
                }
                catch (Exception ex)
                {
                    RegistrationCameraStatus.Text = $"Camera error: {ex.Message}";
                }
            }
            else
            {
                _registrationGestureService.Stop();
                RegistrationCameraPlaceholder.Visibility = Visibility.Visible;
                NextRegistrationGestureButton.IsEnabled = _waitingForNextRegistrationGesture && !_gestureRecorded;

                if (_gestureRecorded)
                {
                    RegistrationCameraStatus.Text = "✓ Gesture recorded";
                }
                else
                {
                    RegistrationCameraStatus.Text = "No gesture detected. Try again.";
                }

                StartRegistrationCameraButton.Content = "📷 Start Camera";
            }
        }

        private string GestureSequenceLabel() =>
            string.Join(" -> ", _registeredGestureSequence.Select(GestureDirectionToLabel));

        private string RegistrationGestureStepLabel() => _registrationGestureIndex switch
        {
            0 => "First gesture",
            1 => "Second gesture",
            _ => "Final gesture"
        };

        private void NextRegistrationGesture_Click(object sender, RoutedEventArgs e)
        {
            if (!_waitingForNextRegistrationGesture)
            {
                return;
            }

            if (_gestureRecorded)
            {
                FinishSetupButton.IsEnabled = true;
                NextRegistrationGestureButton.IsEnabled = false;
                RegistrationGestureInstruction.Text = "All 3 gestures recorded. Click Finish Setup.";
                return;
            }

            _registrationGestureIndex++;
            _waitingForNextRegistrationGesture = false;
            NextRegistrationGestureButton.IsEnabled = false;
            RegistrationDetectedGesture.Text = "Ready for next gesture";
            RegistrationGestureInstruction.Text = $"{RegistrationGestureStepLabel()}: perform a clear swipe.";
        }

        private static string GestureDirectionToLabel(string direction) => direction switch
        {
            "SWIPE_RIGHT" => "Swipe Right",
            "SWIPE_LEFT" => "Swipe Left",
            "SWIPE_UP" => "Swipe Up",
            "SWIPE_DOWN" => "Swipe Down",
            _ => direction
        };

        //DEMO TEST

        private void SkipGesture_Click(object sender, RoutedEventArgs e)
        {
            // Stop camera if running
            _registrationGestureService?.Stop();
            _registrationGestureService?.Dispose();

            // Use default gesture sequence
            _registeredGestureDirection = "SWIPE_RIGHT";
            _registeredGestureSequence.Clear();
            _registeredGestureSequence.AddRange(new[] { "SWIPE_RIGHT", "SWIPE_LEFT", "SWIPE_UP" });
            _registrationGestureIndex = 2;
            _waitingForNextRegistrationGesture = false;
            _gestureRecorded = true;

            // Enable finish button
            FinishSetupButton.IsEnabled = true;
            NextRegistrationGestureButton.IsEnabled = false;

            // Update UI
            RegistrationDetectedGesture.Text = "⏭ Skipped (Demo)";
            GestureRecordedBadge.Visibility = Visibility.Visible;
            RecordedGestureText.Text = $"Registered: {GestureSequenceLabel()}";
            RegistrationCameraStatus.Text = "Gesture sequence set to default";
        }
    

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a hex color string like "#EF4444" or "EF4444" to Windows.UI.Color.
        /// </summary>
        private static Color ParseColor(string hex)
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb(255,
                byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
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
