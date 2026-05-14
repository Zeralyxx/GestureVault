using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

namespace GestureVault.Services
{
    internal sealed class GestureCountdownPopup
    {
        private ContentDialog? _dialog;
        private TextBlock? _countdownText;
        private TextBlock? _instructionText;
        private bool _isShowing;

        public void Show(XamlRoot xamlRoot, string title, int secondsRemaining, string instruction)
        {
            EnsureDialog(xamlRoot, title);

            if (_countdownText != null)
                _countdownText.Text = secondsRemaining.ToString();

            if (_instructionText != null)
                _instructionText.Text = instruction;

            if (_dialog == null || _isShowing)
                return;

            var dialog = _dialog;
            _isShowing = true;
            _ = ShowAsync(dialog);
        }

        public void Hide()
        {
            try
            {
                _dialog?.Hide();
            }
            catch
            {
                // Dialog may already be closing.
            }

            _dialog = null;
            _countdownText = null;
            _instructionText = null;
            _isShowing = false;
        }

        private async System.Threading.Tasks.Task ShowAsync(ContentDialog dialog)
        {
            try
            {
                await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                // Another ContentDialog is already open. The inline countdown text still updates.
            }
            finally
            {
                if (ReferenceEquals(_dialog, dialog))
                    _isShowing = false;
            }
        }

        private void EnsureDialog(XamlRoot xamlRoot, string title)
        {
            if (_dialog != null)
                return;

            _countdownText = new TextBlock
            {
                Text = "3",
                FontSize = 72,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            };

            _instructionText = new TextBlock
            {
                Text = "Get your hand sign ready.",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            var content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 260,
                Children =
                {
                    _countdownText,
                    _instructionText
                }
            };

            _dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                XamlRoot = xamlRoot,
                DefaultButton = ContentDialogButton.None
            };
            _dialog.Closed += (_, _) =>
            {
                _dialog = null;
                _countdownText = null;
                _instructionText = null;
                _isShowing = false;
            };
        }
    }
}
