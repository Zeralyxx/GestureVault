using GestureVault.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace GestureVault
{
    public sealed partial class VaultDashboardWindow : Window
    {
        // ── Services ──────────────────────────────────────────────────────────
        private readonly VaultStorageService _storage = new();

        // ── State ─────────────────────────────────────────────────────────────
        private List<VaultItem> _allItems = new();
        private List<VaultItem> _filteredItems = new();
        private VaultItemType? _activeFilter = null;
        private string _searchQuery = string.Empty;
        private VaultItem? _selectedItem = null;
        private bool _isEditingItem = false;
        private bool _itemPasswordVisible = false;
        private TextBox? _editTitleBox;
        private TextBox? _editUsernameBox;
        private TextBox? _editPasswordBox;
        private TextBox? _editUrlBox;
        private TextBox? _editNoteBox;
        private TextBox? _editDescriptionBox;
        private const string IconShowGlyph = "\uE890";
        private const string IconHideGlyph = "\uE9A9";
        private const string IconCopyGlyph = "\uE8C8";
        private const string IconCheckGlyph = "\uE73E";

        // Tracks temp files open so we can clean them on lock
        private readonly List<string> _openTempFiles = new();
        private readonly HashSet<string> _previewTempFiles = new();

        // ── Auto-lock ─────────────────────────────────────────────────────────
        private DispatcherQueueTimer? _autoLockTimer;
        private int _autoLockMinutes = 5;
        private int _authLockoutSeconds = 30;
        private bool _autoLockEnabled = true;
        private bool _isLightMode = true;
        private bool _currentPasswordVisible = false;
        private bool _newPasswordVisible = false;
        private bool _confirmPasswordVisible = false;
        private GestureService? _changeGestureService;
        private readonly List<string> _changeGestureSequence = new();
        private int _changeGestureIndex = 0;
        private bool _waitingForNextChangeGesture = false;
        private bool _changeGestureRecorded = false;
        private bool _acceptingChangeGesture = false;
        private string? _pendingChangeGesture;
        private readonly GestureCountdownPopup _changeGestureCountdownPopup = new();
        private int _changeGestureCountdownRun = 0;
        private DateTime _lastActivity = DateTime.Now;

        // ── Constructor ───────────────────────────────────────────────────────
        public VaultDashboardWindow()
        {
            this.InitializeComponent();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow.Presenter is OverlappedPresenter p) p.Maximize();

            // Unlock storage with session password
            string pw = SessionState.MasterPassword;
            _storage.UnlockWithPassword(pw);

            LoadSettings();
            LoadItems();
            StartAutoLockTimer();
        }

        // ══════════════════════════════════════════════════════════════════════
        // PERSISTENCE
        // ══════════════════════════════════════════════════════════════════════
        private void LoadItems()
        {
            _allItems = _storage.LoadItems();
            RefreshDisplay();
        }

        private void SaveItems() => _storage.SaveItems(_allItems);

        // ══════════════════════════════════════════════════════════════════════
        // DISPLAY
        // ══════════════════════════════════════════════════════════════════════
        private void RefreshDisplay()
        {
            _filteredItems = _activeFilter == null
                ? new List<VaultItem>(_allItems)
                : _allItems.Where(i => i.Type == _activeFilter).ToList();

            if (!string.IsNullOrWhiteSpace(_searchQuery))
                _filteredItems = _filteredItems
                    .Where(i => i.Title.Contains(_searchQuery,
                                    StringComparison.OrdinalIgnoreCase))
                    .ToList();

            ItemCountText.Text =
                $"{_allItems.Count} item{(_allItems.Count == 1 ? "" : "s")} stored";

            VaultItemsPanel.Children.Clear();

            if (_filteredItems.Count == 0)
            {
                VaultItemsPanel.Children.Add(new TextBlock
                {
                    Text = "No items found.",
                    FontSize = 15,
                    Opacity = 0.5,
                    Margin = new Thickness(0, 20, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            foreach (var item in _filteredItems)
                VaultItemsPanel.Children.Add(BuildItemCard(item));
        }

        private Border BuildItemCard(VaultItem item)
        {
            string code = item.Type switch
            {
                VaultItemType.Password => "PW",
                VaultItemType.Note => "NT",
                VaultItemType.Document => "FL",
                _ => "IT"
            };

            string subtitle = item.Type switch
            {
                VaultItemType.Password => $"Password | Updated {item.UpdatedAt}",
                VaultItemType.Note => $"Secure Note | Updated {item.UpdatedAt}",
                VaultItemType.Document => $"Files | {item.Files.Count(f => !f.IsDeleted)} file(s) | {item.UpdatedAt}",
                _ => item.UpdatedAt
            };

            var openBtn = new Button
            {
                Content = "Open",
                Width = 90,
                Style = (Style)((FrameworkElement)this.Content).Resources["CardButtonStyle"]
            };
            openBtn.Click += (s, e) => OpenItem(item);

            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var typeCode = new TextBlock
            {
                Text = code,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(PrimaryText)
            };
            Grid.SetColumn(typeCode, 0);

            var divider = new Border
            {
                Width = 1,
                Height = 42,
                Background = new SolidColorBrush(CardBorder),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(divider, 1);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(PrimaryText)
            });
            info.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 13,
                Foreground = new SolidColorBrush(SecondaryText)
            });
            Grid.SetColumn(info, 2);
            Grid.SetColumn(openBtn, 3);

            grid.Children.Add(typeCode);
            grid.Children.Add(divider);
            grid.Children.Add(info);
            grid.Children.Add(openBtn);

            return new Border
            {
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18),
                Background = new SolidColorBrush(CardBackground),
                BorderBrush = new SolidColorBrush(CardBorder),
                Child = grid
            };
        }

        // ADD ITEM
        // ══════════════════════════════════════════════════════════════════════
        private void AddItemButton_Click(object sender, RoutedEventArgs e)
        {
            ItemTypeCombo.SelectedIndex = 0;
            ItemTitleBox.Text = string.Empty;
            ItemUsernameBox.Text = string.Empty;
            ItemPasswordBox.Password = string.Empty;
            ItemPasswordTextBox.Text = string.Empty;
            ItemPasswordTextBox.Visibility = Visibility.Collapsed;
            ItemPasswordBox.Visibility = Visibility.Visible;
            ItemPasswordEyeIcon.Text = IconShowGlyph;
            ItemPasswordCopyIcon.Text = IconCopyGlyph;
            _itemPasswordVisible = false;
            ItemUrlBox.Text = string.Empty;
            NoteContentBox.Text = string.Empty;
            ItemDescriptionBox.Text = string.Empty;
            ChosenFileText.Text = "No files added yet";
            PendingFilesList.Children.Clear();
            _pendingFiles.Clear();
            ShowPasswordFields();
            AddItemOverlay.Visibility = Visibility.Visible;
            ResetIdleTimer();
        }

        // Pending files collected before Save is clicked
        private readonly List<(string sourcePath, string fileName)>
            _pendingFiles = new();


        private void ToggleItemPassword_Click(object sender, RoutedEventArgs e)
        {
            _itemPasswordVisible = !_itemPasswordVisible;
            if (_itemPasswordVisible)
            {
                ItemPasswordTextBox.Text = ItemPasswordBox.Password;
                ItemPasswordTextBox.Visibility = Visibility.Visible;
                ItemPasswordBox.Visibility = Visibility.Collapsed;
                ItemPasswordEyeIcon.Text = IconHideGlyph;
            }
            else
            {
                ItemPasswordBox.Password = ItemPasswordTextBox.Text;
                ItemPasswordBox.Visibility = Visibility.Visible;
                ItemPasswordTextBox.Visibility = Visibility.Collapsed;
                ItemPasswordEyeIcon.Text = IconShowGlyph;
            }
        }

        private async void CopyItemPassword_Click(object sender, RoutedEventArgs e)
        {
            string value = _itemPasswordVisible ? ItemPasswordTextBox.Text : ItemPasswordBox.Password;
            if (string.IsNullOrEmpty(value)) return;

            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(value);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            ItemPasswordCopyIcon.Text = IconCheckGlyph;
            await Task.Delay(1200);
            ItemPasswordCopyIcon.Text = IconCopyGlyph;
        }
        private void ItemTypeCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (PasswordFields == null) return;
            switch (ItemTypeCombo.SelectedIndex)
            {
                case 0: ShowPasswordFields(); break;
                case 1: ShowNoteFields(); break;
                case 2: ShowFilesFields(); break;
            }
        }

        private void ShowPasswordFields()
        {
            PasswordFields.Visibility = Visibility.Visible;
            NoteContentBox.Visibility = Visibility.Collapsed;
            FilesFields.Visibility = Visibility.Collapsed;
        }
        private void ShowNoteFields()
        {
            PasswordFields.Visibility = Visibility.Collapsed;
            NoteContentBox.Visibility = Visibility.Visible;
            FilesFields.Visibility = Visibility.Collapsed;
        }
        private void ShowFilesFields()
        {
            PasswordFields.Visibility = Visibility.Collapsed;
            NoteContentBox.Visibility = Visibility.Collapsed;
            FilesFields.Visibility = Visibility.Visible;
        }

        private async void ChooseFileButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;

            foreach (var file in files)
            {
                _pendingFiles.Add((file.Path, file.Name));
                AddPendingFileRow(file.Name, isPending: true);
            }

            ChosenFileText.Text = $"{_pendingFiles.Count} file(s) ready to save";
            ResetIdleTimer();
        }

        private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            var allFiles = GetAllFilesInFolder(folder.Path);
            if (allFiles.Count == 0)
            {
                await ShowDialog("Empty folder", "The selected folder contains no files.");
                return;
            }

            foreach (var (path, name) in allFiles)
            {
                _pendingFiles.Add((path, name));
                AddPendingFileRow(name, isPending: true);
            }

            ChosenFileText.Text = $"{_pendingFiles.Count} file(s) ready to save";
            ResetIdleTimer();
        }

        /// <summary>
        /// Recursively collects every file under a directory.
        /// Returns a list of (fullPath, displayName) where displayName
        /// preserves the relative sub-path so the user can see folder structure.
        /// e.g. "Files\Tax2024\receipt.pdf"
        /// </summary>
        private static List<(string path, string name)> GetAllFilesInFolder(
            string rootPath)
        {
            var results = new List<(string, string)>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(
                             rootPath, "*", SearchOption.AllDirectories))
                {
                    // Make name relative to the chosen folder's parent so the
                    // folder name itself is included: "FolderName\sub\file.ext"
                    string relative = Path.GetRelativePath(
                        Path.GetDirectoryName(rootPath)!, file);
                    results.Add((file, relative));
                }
            }
            catch { /* skip files we can't read (permissions etc.) */ }
            return results;
        }

        private void AddPendingFileRow(string fileName, bool isPending)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = fileName,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(label, 0);

            var badge = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(
                    isPending
                        ? Color.FromArgb(255, 234, 242, 255)
                        : Color.FromArgb(255, 220, 252, 231)),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = isPending ? "pending" : "encrypted",
                FontSize = 11,
                Foreground = new SolidColorBrush(
                    isPending
                        ? Color.FromArgb(255, 30, 80, 180)
                        : Color.FromArgb(255, 20, 120, 60))
            };
            Grid.SetColumn(badge, 1);

            row.Children.Add(label);
            row.Children.Add(badge);

            var wrapper = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(CardBorder),
                Background = new SolidColorBrush(CardBackground),
                Child = row,
                Margin = new Thickness(0, 0, 0, 6)
            };
            PendingFilesList.Children.Add(wrapper);
        }

        private async void SaveItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ItemTitleBox.Text))
            {
                await ShowDialog("Missing title",
                    "Please enter a title for this item.");
                return;
            }

            var item = new VaultItem
            {
                Title = ItemTitleBox.Text.Trim(),
                UpdatedAt = DateTime.Now.ToString("MMM dd, yyyy")
            };

            switch (ItemTypeCombo.SelectedIndex)
            {
                case 0:
                    item.Type = VaultItemType.Password;
                    item.Username = ItemUsernameBox.Text.Trim();
                    item.Password = _itemPasswordVisible ? ItemPasswordTextBox.Text : ItemPasswordBox.Password;
                    item.Url = ItemUrlBox.Text.Trim();
                    break;

                case 1:
                    item.Type = VaultItemType.Note;
                    item.NoteContent = NoteContentBox.Text.Trim();
                    break;

                case 2:
                    item.Type = VaultItemType.Document;
                    item.Description = ItemDescriptionBox.Text.Trim();

                    if (_pendingFiles.Count == 0)
                    {
                        await ShowDialog("No files selected", "Choose at least one file to add to this vault item.");
                        return;
                    }

                    foreach (var (sourcePath, displayName) in _pendingFiles)
                    {
                        try
                        {
                            var vf = ImportFileIntoVault(item, sourcePath, displayName);
                            item.Files.Add(vf);
                        }
                        catch (Exception ex)
                        {
                            await ShowDialog("File error",
                                $"Could not encrypt {Path.GetFileName(sourcePath)}: {ex.Message}");
                        }
                    }
                    break;
            }

            _allItems.Add(item);
            SaveItems();
            RefreshDisplay();
            AddItemOverlay.Visibility = Visibility.Collapsed;
            _pendingFiles.Clear();
            ResetIdleTimer();
        }

        private void CancelAddItem_Click(object sender, RoutedEventArgs e)
        {
            _pendingFiles.Clear();
            AddItemOverlay.Visibility = Visibility.Collapsed;
            ResetIdleTimer();
        }

        // ══════════════════════════════════════════════════════════════════════
        // VIEW ITEM
        // ══════════════════════════════════════════════════════════════════════
        private void OpenItem(VaultItem item)
        {
            _selectedItem = item;
            _isEditingItem = false;
            RenderSelectedItem();
            ViewItemOverlay.Visibility = Visibility.Visible;
            ResetIdleTimer();
        }

        private void RenderSelectedItem()
        {
            if (_selectedItem == null) return;

            var item = _selectedItem;
            ViewItemTitle.Text = _isEditingItem ? "Edit item" : item.Title;
            ViewItemType.Text = item.Type switch
            {
                VaultItemType.Password => "Password",
                VaultItemType.Note => "Secure Note",
                VaultItemType.Document => "Files",
                _ => "Item"
            };

            ViewItemContent.Children.Clear();
            CleanupPreviewTempFiles();
            AddEditableRow("Title", item.Title, textBox => _editTitleBox = textBox, !_isEditingItem);

            switch (item.Type)
            {
                case VaultItemType.Password:
                    AddEditableRow("Username", item.Username ?? string.Empty, textBox => _editUsernameBox = textBox, !_isEditingItem);
                    AddEditableRow("Password", item.Password ?? string.Empty, textBox => _editPasswordBox = textBox, !_isEditingItem, isPassword: true);
                    AddEditableRow("Website", item.Url ?? string.Empty, textBox => _editUrlBox = textBox, !_isEditingItem);
                    break;

                case VaultItemType.Note:
                    AddEditableRow("Note", item.NoteContent ?? string.Empty, textBox => _editNoteBox = textBox, !_isEditingItem, multiline: true);
                    break;

                case VaultItemType.Document:
                    AddEditableRow("Description", item.Description ?? string.Empty, textBox => _editDescriptionBox = textBox, !_isEditingItem, multiline: true);
                    BuildFilesFileList(item);
                    break;
            }

            AddViewRow("Last updated", item.UpdatedAt);
            DeleteItemButton.Content = _isEditingItem ? "Cancel" : "Delete";
            CloseViewItemButton.Content = _isEditingItem ? "Save" : "Edit";
        }

        // Files file list inside the view overlay ────────────────────────
        private void BuildFilesFileList(VaultItem item)
        {
            // Section header + Add Files button
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });

            headerGrid.Children.Add(new TextBlock
            {
                Text = $"Files ({item.Files.Count(f => !f.IsDeleted)})",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(PrimaryText)
            });

            var addMoreBtn = new Button
            {
                Content = "+ Add Files",
                Style = (Style)((FrameworkElement)this.Content)
                              .Resources["CardButtonStyle"],
                Height = 34
            };
            addMoreBtn.Click += (s, e) => AddFilesToExistingItem(item);
            Grid.SetColumn(addMoreBtn, 1);
            headerGrid.Children.Add(addMoreBtn);

            ViewItemContent.Children.Add(new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(CardBorder),
                Background = new SolidColorBrush(CardBackground),
                Child = headerGrid,
                Margin = new Thickness(0, 0, 0, 4)
            });

            AddImagePreviewStrip(item);

            var activeFiles = item.Files.Where(f => !f.IsDeleted).ToList();
            var removedFiles = item.Files.Where(f => f.IsDeleted).ToList();

            if (activeFiles.Count == 0)
            {
                ViewItemContent.Children.Add(new TextBlock
                {
                    Text = "No files attached yet. Click \"+ Add Files\" to add some.",
                    FontSize = 13,
                    Opacity = 0.6,
                    Margin = new Thickness(4, 0, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(SecondaryText)
                });
            }
            else
            {
                foreach (var vf in activeFiles)
                    ViewItemContent.Children.Add(BuildFileRow(item, vf));
            }

            if (removedFiles.Count > 0)
            {
                ViewItemContent.Children.Add(new TextBlock
                {
                    Text = "Removed files",
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(SecondaryText),
                    Margin = new Thickness(0, 8, 0, 0)
                });
                foreach (var vf in removedFiles)
                    ViewItemContent.Children.Add(BuildRemovedFileRow(item, vf));
            }
        }

        private Border BuildFileRow(VaultItem item, VaultFile vf)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var preview = BuildFilePreview(vf, 56, 42);
            Grid.SetColumn(preview, 0);

            var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            namePanel.Children.Add(new TextBlock
            {
                Text = vf.FileName,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(PrimaryText)
            });
            namePanel.Children.Add(new TextBlock
            {
                Text = $"Added {vf.AddedAt} | encrypted",
                FontSize = 11,
                Foreground = new SolidColorBrush(SecondaryText)
            });
            Grid.SetColumn(namePanel, 1);

            var openBtn = new Button
            {
                Content = "Open",
                Width = 64,
                Height = 32,
                Style = (Style)((FrameworkElement)this.Content).Resources["CardButtonStyle"]
            };
            openBtn.Click += async (s, e) =>
            {
                await OpenVaultFileAsync(vf);
                ResetIdleTimer();
            };
            Grid.SetColumn(openBtn, 2);

            var delBtn = new Button
            {
                Content = "Delete",
                Width = 64,
                Height = 32,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 57, 53))
            };
            delBtn.Click += async (s, e) =>
            {
                var confirm = new ContentDialog
                {
                    Title = "Delete file",
                    Content = $"Remove \"{vf.FileName}\" from this vault item?",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };
                if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                {
                    vf.IsDeleted = true;
                    item.UpdatedAt = DateTime.Now.ToString("MMM dd, yyyy");
                    SaveItems();
                    RefreshDisplay();
                    RenderSelectedItem();
                }
                ResetIdleTimer();
            };
            Grid.SetColumn(delBtn, 3);

            grid.Children.Add(preview);
            grid.Children.Add(namePanel);
            grid.Children.Add(openBtn);
            grid.Children.Add(delBtn);

            return new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(CardBorder),
                Background = new SolidColorBrush(CardBackground),
                Child = grid,
                Margin = new Thickness(0, 0, 0, 6)
            };
        }

        // -- Add more files to an existing file item --------------------------- ───────────────────────
        private async void AddFilesToExistingItem(VaultItem item)
        {
            // Ask whether to add files or a folder
            var choice = new ContentDialog
            {
                Title = "Add to vault item",
                Content = "Would you like to add individual files or an entire folder?",
                PrimaryButtonText = "Files",
                SecondaryButtonText = "Folder",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await choice.ShowAsync();
            if (result == ContentDialogResult.None) return;

            List<(string path, string name)> toAdd = new();

            if (result == ContentDialogResult.Primary)
            {
                // ── File picker ───────────────────────────────────────────────
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add("*");
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0) return;

                foreach (var f in files)
                    toAdd.Add((f.Path, f.Name));
            }
            else
            {
                // ── Folder picker ─────────────────────────────────────────────
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add("*");
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;

                toAdd = GetAllFilesInFolder(folder.Path);
                if (toAdd.Count == 0)
                {
                    await ShowDialog("Empty folder",
                        "The selected folder contains no files.");
                    return;
                }
            }

            foreach (var (path, name) in toAdd)
            {
                try
                {
                    var vf = ImportFileIntoVault(item, path, name);
                    item.Files.Add(vf);
                }
                catch (Exception ex)
                {
                    await ShowDialog("File error",
                        $"Could not encrypt {name}: {ex.Message}");
                }
            }

            item.UpdatedAt = DateTime.Now.ToString("MMM dd, yyyy");
            SaveItems();
            RefreshDisplay();
            OpenItem(item);
            ResetIdleTimer();
        }

        private Border BuildRemovedFileRow(VaultItem item, VaultFile vf)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var preview = BuildFilePreview(vf, 56, 42);
            preview.Opacity = 0.45;
            Grid.SetColumn(preview, 0);

            var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            namePanel.Children.Add(new TextBlock
            {
                Text = vf.FileName,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(SecondaryText)
            });
            namePanel.Children.Add(new TextBlock
            {
                Text = "Removed from vault list; encrypted copy is still available",
                FontSize = 11,
                Foreground = new SolidColorBrush(SecondaryText)
            });
            Grid.SetColumn(namePanel, 1);

            var restoreBtn = new Button
            {
                Content = "Restore",
                Width = 78,
                Height = 32,
                Style = (Style)((FrameworkElement)this.Content).Resources["CardButtonStyle"]
            };
                        restoreBtn.Click += async (s, e) =>
            {
                try
                {
                    ViewItemContent.Children.Clear();
                    CleanupPreviewTempFiles();

                    string restoredPath = _storage.RestoreFileToOriginalPath(vf);
                    _storage.RemoveFileFromItem(vf);
                    item.Files.Remove(vf);
                    item.UpdatedAt = DateTime.Now.ToString("MMM dd, yyyy");
                    SaveItems();
                    RefreshDisplay();
                    RenderSelectedItem();
                    await ShowDialog("File restored", $"Decrypted copy restored to:\n{restoredPath}");
                }
                catch (Exception ex)
                {
                    await ShowDialog("Restore failed", ex.Message);
                }
            };
            Grid.SetColumn(restoreBtn, 2);

            var permanentBtn = new Button
            {
                Content = "Delete",
                Width = 78,
                Height = 32,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 57, 53))
            };
            permanentBtn.Click += async (s, e) =>
            {
                var confirm = new ContentDialog
                {
                    Title = "Delete permanently",
                    Content = $"Permanently delete \"{vf.FileName}\"? This cannot be restored.",
                    PrimaryButtonText = "Delete permanently",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };
                if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                {
                    ViewItemContent.Children.Clear();
                    CleanupPreviewTempFiles();

                    _storage.RemoveFileFromItem(vf);
                    item.Files.Remove(vf);
                    item.UpdatedAt = DateTime.Now.ToString("MMM dd, yyyy");
                    SaveItems();
                    RefreshDisplay();
                    RenderSelectedItem();
                }
            };
            Grid.SetColumn(permanentBtn, 3);

            grid.Children.Add(preview);
            grid.Children.Add(namePanel);
            grid.Children.Add(restoreBtn);
            grid.Children.Add(permanentBtn);

            return new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(CardBorder),
                Background = new SolidColorBrush(Color.FromArgb(255, 250, 250, 250)),
                Child = grid,
                Margin = new Thickness(0, 0, 0, 6)
            };
        }

        private void AddEditableRow(string label, string value, Action<TextBox> bind, bool readOnly, bool isPassword = false, bool multiline = false)
        {
            if (readOnly)
            {
                AddViewRow(label, value, isPassword);
                return;
            }

            var box = new TextBox
            {
                Header = label,
                Text = value,
                CornerRadius = new CornerRadius(12),
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Height = multiline ? 120 : double.NaN
            };
            bind(box);
            ViewItemContent.Children.Add(box);
        }

        private void SaveEditedItem()
        {
            if (_selectedItem == null) return;
            var item = _selectedItem;

            item.Title = _editTitleBox?.Text.Trim() ?? item.Title;
            switch (item.Type)
            {
                case VaultItemType.Password:
                    item.Username = _editUsernameBox?.Text.Trim();
                    item.Password = _editPasswordBox?.Text ?? item.Password;
                    item.Url = _editUrlBox?.Text.Trim();
                    break;
                case VaultItemType.Note:
                    item.NoteContent = _editNoteBox?.Text.Trim();
                    break;
                case VaultItemType.Document:
                    item.Description = _editDescriptionBox?.Text.Trim();
                    break;
            }

            item.UpdatedAt = DateTime.Now.ToString("MMM dd, yyyy");
            SaveItems();
            RefreshDisplay();
            _isEditingItem = false;
            RenderSelectedItem();
        }
        private VaultFile ImportFileIntoVault(VaultItem item, string sourcePath, string? displayName = null)
        {
            var vf = _storage.AddFileToItem(item.Id, sourcePath);
            vf.OriginalPath = sourcePath;
            if (!string.IsNullOrWhiteSpace(displayName))
                vf.FileName = displayName;

            TryRemoveOriginalFile(sourcePath);
            return vf;
        }

        private static void TryRemoveOriginalFile(string sourcePath)
        {
            try
            {
                if (!File.Exists(sourcePath)) return;
                EncryptionService.SecureDeleteTemp(sourcePath);
            }
            catch
            {
                // Best effort: the file may be locked or protected.
            }
        }

        private async Task OpenVaultFileAsync(VaultFile vf)
        {
            try
            {
                string tempPath = _storage.DecryptFileToTemp(vf);
                _openTempFiles.Add(tempPath);
                var launchFile = await StorageFile.GetFileFromPathAsync(tempPath);
                await Windows.System.Launcher.LaunchFileAsync(launchFile);
            }
            catch (Exception ex)
            {
                await ShowDialog("Open failed", $"Could not open file: {ex.Message}");
            }
        }

        private void AddImagePreviewStrip(VaultItem item)
        {
            var imageFiles = item.Files
                .Where(f => !f.IsDeleted && IsImageFile(f.FileName))
                .Take(12)
                .ToList();
            if (imageFiles.Count == 0) return;

            var strip = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            strip.Children.Add(new TextBlock
            {
                Text = "Image preview",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(SecondaryText)
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            foreach (var file in imageFiles)
            {
                var thumb = BuildFilePreview(file, 92, 68);
                thumb.PointerPressed += async (s, e) => await OpenVaultFileAsync(file);
                ToolTipService.SetToolTip(thumb, file.FileName);
                row.Children.Add(thumb);
            }

            strip.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = row
            });

            ViewItemContent.Children.Add(strip);
        }

        private FrameworkElement BuildFilePreview(VaultFile vf, double width, double height)
        {
            if (IsImageFile(vf.FileName))
            {
                try
                {
                    string tempPath = _storage.DecryptFileToTemp(vf);
                    _previewTempFiles.Add(tempPath);
                    _openTempFiles.Add(tempPath);
                    return new Border
                    {
                        Width = width,
                        Height = height,
                        CornerRadius = new CornerRadius(10),
                        Background = new SolidColorBrush(Color.FromArgb(255, 239, 246, 255)),
                        BorderBrush = new SolidColorBrush(CardBorder),
                        BorderThickness = new Thickness(1),
                        Child = new Image
                        {
                            Source = new BitmapImage(new Uri(tempPath)),
                            Stretch = Stretch.UniformToFill
                        }
                    };
                }
                catch
                {
                    // Fall through to icon preview.
                }
            }

            return new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(255, 239, 246, 255)),
                BorderBrush = new SolidColorBrush(CardBorder),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = GetFileGlyph(vf.FileName),
                    FontSize = width > 70 ? 30 : 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private void CleanupPreviewTempFiles()
        {
            foreach (var tempPath in _previewTempFiles.ToList())
            {
                try
                {
                    _storage.CleanupTempFile(tempPath);
                    _openTempFiles.RemoveAll(t => t == tempPath);
                }
                catch
                {
                    // Best effort: a bitmap may release its file handle slightly later.
                }
            }

            _previewTempFiles.Clear();
        }

        private static bool IsImageFile(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp";
        }

        private static string GetFileGlyph(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "??",
                ".doc" or ".docx" => "??",
                ".xls" or ".xlsx" => "??",
                ".ppt" or ".pptx" => "??",
                ".zip" or ".rar" or ".7z" => "??",
                ".mp4" or ".mov" or ".avi" => "??",
                ".mp3" or ".wav" => "??",
                _ => "??"
            };
        }
        private void AddViewRow(string label, string value,
                                 bool isPassword = false)
        {
            var row = new StackPanel { Spacing = 3 };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(SecondaryText)
            });

            if (isPassword)
            {
                var pwGrid = new Grid();
                pwGrid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pwGrid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = GridLength.Auto });
                pwGrid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = GridLength.Auto });

                var pwText = new TextBlock
                {
                    Text = new string('\u2022', value.Length),
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(PrimaryText)
                };
                Grid.SetColumn(pwText, 0);

                var revealBtn = new Button
                {
                    Content = IconGlyph(IconShowGlyph),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                bool revealed = false;
                revealBtn.Click += (s, e) =>
                {
                    revealed = !revealed;
                    pwText.Text = revealed ? value : new string('\u2022', value.Length);
                    revealBtn.Content = IconGlyph(revealed ? IconHideGlyph : IconShowGlyph);
                };
                Grid.SetColumn(revealBtn, 1);

                var copyBtn = new Button
                {
                    Content = "Copy password",
                    Style = (Style)((FrameworkElement)this.Content).Resources["CardButtonStyle"],
                    Padding = new Thickness(12, 6, 12, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 116
                };
                copyBtn.Click += async (s, e) =>
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(value);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

                    // Brief visual feedback
                    copyBtn.Content = "Copied";
                    await System.Threading.Tasks.Task.Delay(1500);
                    copyBtn.Content = "Copy password";
                };
                Grid.SetColumn(copyBtn, 2);

                pwGrid.Children.Add(pwText);
                pwGrid.Children.Add(revealBtn);
                pwGrid.Children.Add(copyBtn);
                row.Children.Add(pwGrid);
            }
            else
            {
                row.Children.Add(new TextBlock
                {
                    Text = value,
                    FontSize = 15,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(PrimaryText)
                });
            }

            ViewItemContent.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 10),
                BorderBrush = new SolidColorBrush(CardBorder),
                Background = new SolidColorBrush(CardBackground),
                Child = row,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        // DELETE ITEM
        // ══════════════════════════════════════════════════════════════════════
        private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItem == null) return;

            if (_isEditingItem)
            {
                _isEditingItem = false;
                RenderSelectedItem();
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Delete item",
                Content = $"Permanently delete \"{_selectedItem.Title}\"? All attached files will be erased.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                if (_selectedItem.Type == VaultItemType.Document)
                    _storage.DeleteItem(_selectedItem);

                _allItems.Remove(_selectedItem);
                SaveItems();
                RefreshDisplay();
                ViewItemOverlay.Visibility = Visibility.Collapsed;
                _selectedItem = null;
            }
            ResetIdleTimer();
        }


        private void CloseViewOverlay_Click(object sender, RoutedEventArgs e)
        {
            _isEditingItem = false;
            ViewItemOverlay.Visibility = Visibility.Collapsed;
            _selectedItem = null;
            ResetIdleTimer();
        }
        private void CloseViewItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isEditingItem)
            {
                SaveEditedItem();
                ResetIdleTimer();
                return;
            }

            _isEditingItem = true;
            RenderSelectedItem();
            ResetIdleTimer();
        }

        // ══════════════════════════════════════════════════════════════════════
        // FILTER + SEARCH
        // ══════════════════════════════════════════════════════════════════════
        private void SetFilter(VaultItemType? filter, string title)
        {
            _activeFilter = filter;
            SectionTitle.Text = title;

            FilterAllButton.Style =
                (Style)((FrameworkElement)this.Content).Resources[
                    filter == null ? "PrimaryButtonStyle" : "CardButtonStyle"];
            FilterPasswordButton.Style =
                (Style)((FrameworkElement)this.Content).Resources[
                    filter == VaultItemType.Password
                        ? "PrimaryButtonStyle" : "CardButtonStyle"];
            FilterNoteButton.Style =
                (Style)((FrameworkElement)this.Content).Resources[
                    filter == VaultItemType.Note
                        ? "PrimaryButtonStyle" : "CardButtonStyle"];
            FilterFilesButton.Style =
                (Style)((FrameworkElement)this.Content).Resources[
                    filter == VaultItemType.Document
                        ? "PrimaryButtonStyle" : "CardButtonStyle"];

            RefreshDisplay();
            ResetIdleTimer();
        }

        private void FilterAll_Click(object s, RoutedEventArgs e) =>
            SetFilter(null, "All Items");
        private void FilterPasswords_Click(object s, RoutedEventArgs e) =>
            SetFilter(VaultItemType.Password, "Passwords");
        private void FilterNotes_Click(object s, RoutedEventArgs e) =>
            SetFilter(VaultItemType.Note, "Secure Notes");
        private void FilterFiles_Click(object s, RoutedEventArgs e) =>
            SetFilter(VaultItemType.Document, "Files");

        private void SearchBox_TextChanged(object s, TextChangedEventArgs e)
        {
            _searchQuery = SearchBox.Text;
            RefreshDisplay();
            ResetIdleTimer();
        }

        // ══════════════════════════════════════════════════════════════════════
        // SETTINGS
        // ══════════════════════════════════════════════════════════════════════
        private void LoadSettings()
        {
            var settings = ApplicationData.Current.LocalSettings;
            _autoLockEnabled = settings.Values["AutoLockEnabled"] as bool? ?? true;
            _autoLockMinutes = settings.Values["AutoLockMinutes"] as int? ?? 5;
            _authLockoutSeconds = settings.Values["AuthLockoutSeconds"] as int? ?? 30;
            _isLightMode = settings.Values["IsLightMode"] as bool? ?? true;

            AutoLockToggle.IsOn = _autoLockEnabled;
            LightModeToggle.IsOn = _isLightMode;
            ChangePasswordHintBox.Text = settings.Values["PasswordHint"] as string ?? string.Empty;
            ChangePassphraseHintBox.Text = settings.Values["PassphraseHint"] as string ?? string.Empty;
            ChangeGestureHintBox.Text = settings.Values["GestureHint"] as string ?? string.Empty;
            AutoLockCombo.SelectedIndex = _autoLockMinutes switch
            {
                1 => 0,
                5 => 1,
                10 => 2,
                30 => 3,
                _ => 1
            };
            AuthLockoutCombo.SelectedIndex = _authLockoutSeconds switch
            {
                30 => 0,
                60 => 1,
                300 => 2,
                600 => 3,
                _ => 0
            };
            ApplyTheme(_isLightMode);
        }

        private void SettingsButton_Click(object s, RoutedEventArgs e)
        {
            // Clear credential fields every time the overlay opens
            CurrentPasswordBox.Password = string.Empty;
            CurrentPasswordTextBox.Text = string.Empty;
            ChangeNewPasswordBox.Password = string.Empty;
            ChangeNewPasswordTextBox.Text = string.Empty;
            ChangeConfirmPasswordBox.Password = string.Empty;
            ChangeConfirmPasswordTextBox.Text = string.Empty;
            SetPasswordFieldVisibility(CurrentPasswordBox, CurrentPasswordTextBox, ToggleCurrentPasswordButton, false);
            SetPasswordFieldVisibility(ChangeNewPasswordBox, ChangeNewPasswordTextBox, ToggleNewPasswordButton, false);
            SetPasswordFieldVisibility(ChangeConfirmPasswordBox, ChangeConfirmPasswordTextBox, ToggleConfirmPasswordButton, false);
            _currentPasswordVisible = false;
            _newPasswordVisible = false;
            _confirmPasswordVisible = false;
            ChangePassphraseBox.Text = string.Empty;
            ChangePasswordHintBox.Text = ApplicationData.Current.LocalSettings.Values["PasswordHint"] as string ?? string.Empty;
            ChangePassphraseHintBox.Text = ApplicationData.Current.LocalSettings.Values["PassphraseHint"] as string ?? string.Empty;
            ChangeGestureHintBox.Text = ApplicationData.Current.LocalSettings.Values["GestureHint"] as string ?? string.Empty;
            StopChangeGestureRegistration();
            ResetChangeGestureUi();

            SettingsOverlay.Visibility = Visibility.Visible;
            ResetIdleTimer();
        }

        private void CloseSettingsButton_Click(object s, RoutedEventArgs e)
        {
            StopChangeGestureRegistration();
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        // ── Change password ───────────────────────────────────────────────────
        private void ToggleCurrentPasswordButton_Click(object s, RoutedEventArgs e) =>
            TogglePasswordField(CurrentPasswordBox, CurrentPasswordTextBox, ToggleCurrentPasswordButton, ref _currentPasswordVisible);

        private void ToggleNewPasswordButton_Click(object s, RoutedEventArgs e) =>
            TogglePasswordField(ChangeNewPasswordBox, ChangeNewPasswordTextBox, ToggleNewPasswordButton, ref _newPasswordVisible);

        private void ToggleConfirmPasswordButton_Click(object s, RoutedEventArgs e) =>
            TogglePasswordField(ChangeConfirmPasswordBox, ChangeConfirmPasswordTextBox, ToggleConfirmPasswordButton, ref _confirmPasswordVisible);

        private static void TogglePasswordField(PasswordBox passwordBox, TextBox textBox, Button button, ref bool visible)
        {
            visible = !visible;
            if (visible)
                textBox.Text = passwordBox.Password;
            else
                passwordBox.Password = textBox.Text;

            SetPasswordFieldVisibility(passwordBox, textBox, button, visible);
        }

        private static void SetPasswordFieldVisibility(PasswordBox passwordBox, TextBox textBox, Button button, bool visible)
        {
            passwordBox.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            textBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            button.Content = IconGlyph(visible ? IconHideGlyph : IconShowGlyph);
        }

        private async void ChangePasswordButton_Click(object s, RoutedEventArgs e)
        {
            string current = _currentPasswordVisible ? CurrentPasswordTextBox.Text : CurrentPasswordBox.Password;
            string newPw = _newPasswordVisible ? ChangeNewPasswordTextBox.Text : ChangeNewPasswordBox.Password;
            string confirm = _confirmPasswordVisible ? ChangeConfirmPasswordTextBox.Text : ChangeConfirmPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(current) ||
                string.IsNullOrWhiteSpace(newPw) ||
                string.IsNullOrWhiteSpace(confirm))
            {
                await ShowDialog("Missing fields", "Please fill in all three password fields.");
                return;
            }

            // Verify current password
            string? storedHash = ApplicationData.Current.LocalSettings
                                     .Values["MasterPasswordHash"] as string;
            if (storedHash == null || !PasswordService.VerifyPassword(current, storedHash))
            {
                await ShowDialog("Incorrect password", "The current password you entered is wrong.");
                CurrentPasswordBox.Password = string.Empty;
                CurrentPasswordTextBox.Text = string.Empty;
                return;
            }

            // Check new password strength
            var strength = PasswordService.AssessStrength(newPw);
            if (strength.Strength <= PasswordService.PasswordStrength.Weak)
            {
                string message = "Your new password is too weak.\n\n";
                if (strength.Suggestions.Count > 0)
                {
                    message += "Suggestions:\n- " + string.Join("\n- ", strength.Suggestions);
                }
                await ShowDialog("Weak Password", message);
                return;
            }

            if (newPw != confirm)
            {
                await ShowDialog("Passwords don't match",
                    "The new password and confirmation don't match.");
                return;
            }

            try
            {
                // Re-encrypt vault with new password if using VaultStorageService
                _storage.ChangePassword(current, newPw);

                // Update stored hash
                ApplicationData.Current.LocalSettings.Values["MasterPasswordHash"] =
                    PasswordService.HashPassword(newPw);
                ApplicationData.Current.LocalSettings.Values["PasswordHint"] =
                    ChangePasswordHintBox.Text.Trim();
                SessionState.MasterPassword = newPw;

                await ShowDialog("Password updated",
                    "Your master password has been changed and all data re-encrypted.");
            }
            catch (Exception ex)
            {
                await ShowDialog("Error", $"Failed to change password: {ex.Message}");
            }

            CurrentPasswordBox.Password = string.Empty;
            CurrentPasswordTextBox.Text = string.Empty;
            ChangeNewPasswordBox.Password = string.Empty;
            ChangeNewPasswordTextBox.Text = string.Empty;
            ChangeConfirmPasswordBox.Password = string.Empty;
            ChangeConfirmPasswordTextBox.Text = string.Empty;
            SetPasswordFieldVisibility(CurrentPasswordBox, CurrentPasswordTextBox, ToggleCurrentPasswordButton, false);
            SetPasswordFieldVisibility(ChangeNewPasswordBox, ChangeNewPasswordTextBox, ToggleNewPasswordButton, false);
            SetPasswordFieldVisibility(ChangeConfirmPasswordBox, ChangeConfirmPasswordTextBox, ToggleConfirmPasswordButton, false);
            _currentPasswordVisible = false;
            _newPasswordVisible = false;
            _confirmPasswordVisible = false;
            ResetIdleTimer();
        }

        // ── Change passphrase ─────────────────────────────────────────────────
        private async void ChangePassphraseButton_Click(object s, RoutedEventArgs e)
        {
            string phrase = ChangePassphraseBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(phrase))
            {
                await ShowDialog("Missing passphrase",
                    "Please enter a new voice passphrase.");
                return;
            }
            if (phrase.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length < 2)
            {
                await ShowDialog("Passphrase too short",
                    "Your passphrase should contain at least two words.");
                return;
            }

            try
            {
                _storage.SavePassphrase(phrase);
                ApplicationData.Current.LocalSettings.Values["PassphraseHint"] =
                    ChangePassphraseHintBox.Text.Trim();
                string? savedPhrase = _storage.LoadPassphrase();
                if (string.IsNullOrWhiteSpace(savedPhrase))
                {
                    await ShowDialog("Passphrase not saved",
                        "The new voice passphrase could not be verified after saving. Please try again.");
                    return;
                }
            }
            catch (Exception ex)
            {
                await ShowDialog("Passphrase not saved",
                    $"Could not save the new voice passphrase: {ex.Message}");
                return;
            }

            ChangePassphraseBox.Text = string.Empty;

            await ShowDialog("Passphrase updated",
                "Your voice passphrase has been changed. Lock the vault and sign in again to use it.");
            ResetIdleTimer();
        }

        private async void SaveHintsButton_Click(object s, RoutedEventArgs e)
        {
            SaveHints();
            await ShowDialog("Hints saved", "Your verification hints have been updated.");
            ResetIdleTimer();
        }

        private void SaveHints()
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["PasswordHint"] = ChangePasswordHintBox.Text.Trim();
            settings.Values["PassphraseHint"] = ChangePassphraseHintBox.Text.Trim();
            settings.Values["GestureHint"] = ChangeGestureHintBox.Text.Trim();
        }

        // -- Change gesture ---------------------------------------------------
        private string ChangeGestureStepLabel() => _changeGestureIndex switch
        {
            0 => "First gesture",
            1 => "Second gesture",
            _ => "Final gesture"
        };

        private void ResetChangeGestureUi()
        {
            _changeGestureSequence.Clear();
            _changeGestureIndex = 0;
            _waitingForNextChangeGesture = false;
            _changeGestureRecorded = false;
            _acceptingChangeGesture = false;
            _pendingChangeGesture = null;
            CancelChangeGestureCountdown();

            ChangeGestureCameraPlaceholder.Visibility = Visibility.Visible;
            ChangeGestureCameraPreview.Source = null;
            ChangeGestureStatusText.Text = "Start the camera. Wait for the countdown before each hand sign.";
            ChangeGestureDetectedText.Text = "No gesture detected yet";
            ChangeGestureRecordedText.Text = "No gestures recorded yet";
            StartChangeGestureCameraButton.Content = "Start Camera";
            NextChangeGestureButton.Content = "Next Gesture";
            NextChangeGestureButton.IsEnabled = false;
            UpdateChangeGestureBackButton(false, "Previous Gesture");
            SaveChangeGestureButton.IsEnabled = false;
        }

        private void InitializeChangeGestureService()
        {
            _changeGestureService = new GestureService(DispatcherQueue.GetForCurrentThread());

            _changeGestureService.FrameReady += async (s, frame) =>
            {
                try
                {
                    var bitmapSource = await ImageConverter.MatToSoftwareBitmapSource(frame);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ChangeGestureCameraPreview.Source = bitmapSource;
                    });
                }
                catch
                {
                    // Skip frames that cannot be converted.
                }
            };

            _changeGestureService.GestureObserved += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_acceptingChangeGesture || _waitingForNextChangeGesture || _changeGestureRecorded)
                        return;

                    ChangeGestureDetectedText.Text = $"Seeing: {GestureDirectionToLabel(e.Direction)}";
                    ChangeGestureStatusText.Text = "Hold it steady until the app asks you to confirm it.";
                });
            };

            _changeGestureService.GestureDetected += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_acceptingChangeGesture)
                        return;

                    if (_changeGestureSequence.Count >= 3 || _waitingForNextChangeGesture)
                        return;

                    CancelChangeGestureCountdown();
                    _waitingForNextChangeGesture = true;
                    _acceptingChangeGesture = false;
                    _pendingChangeGesture = e.Direction;

                    ChangeGestureDetectedText.Text = $"{GestureDirectionToLabel(e.Direction)} detected";
                    ChangeGestureStatusText.Text = "Check the detected label. If it is right, record it. If not, redo.";
                    NextChangeGestureButton.Content = "Record This Gesture";
                    NextChangeGestureButton.IsEnabled = true;
                    UpdateChangeGestureBackButton(true, "Redo Gesture");
                    SaveChangeGestureButton.IsEnabled = false;
                    ResetIdleTimer();
                });
            };
        }

        private void StartChangeGestureCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (_changeGestureService == null)
                InitializeChangeGestureService();

            if (!_changeGestureService!.IsRunning)
            {
                try
                {
                    _changeGestureService.Start();
                    ChangeGestureCameraPlaceholder.Visibility = Visibility.Collapsed;
                    ChangeGestureStatusText.Text =
                        $"{ChangeGestureStepLabel()}: wait for the countdown, then hold a hand sign.";
                    StartChangeGestureCameraButton.Content = "Stop Camera";
                    StartChangeGestureCountdown();
                }
                catch (Exception ex)
                {
                    ChangeGestureStatusText.Text = $"Camera error: {ex.Message}";
                }
            }
            else
            {
                _changeGestureService.Stop();
                CancelChangeGestureCountdown();
                ChangeGestureCameraPlaceholder.Visibility = Visibility.Visible;
                ChangeGestureCameraPreview.Source = null;
                StartChangeGestureCameraButton.Content = "Start Camera";
                ChangeGestureStatusText.Text = _changeGestureRecorded
                    ? "All three gestures are recorded. Click Update Gesture."
                    : "Camera stopped. Start it again to continue recording.";
            }

            ResetIdleTimer();
        }

        private void NextChangeGestureButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_waitingForNextChangeGesture)
                return;

            if (_pendingChangeGesture != null)
            {
                _changeGestureSequence.Add(_pendingChangeGesture);
                _pendingChangeGesture = null;
                _changeGestureRecorded = _changeGestureSequence.Count >= 3;

                UpdateChangeGestureRecordedSummary();
                UpdateChangeGestureBackButton(_changeGestureSequence.Count > 0, "Previous Gesture");
                SaveChangeGestureButton.IsEnabled = _changeGestureRecorded;

                if (_changeGestureRecorded)
                {
                    NextChangeGestureButton.IsEnabled = false;
                    ChangeGestureDetectedText.Text = "All gestures recorded";
                    ChangeGestureStatusText.Text = "All three gestures are recorded. Use Previous Gesture to change the last one, or click Update Gesture.";
                    ResetIdleTimer();
                    return;
                }

                NextChangeGestureButton.Content = "Next Gesture";
                NextChangeGestureButton.IsEnabled = true;
                ChangeGestureDetectedText.Text = $"Gesture {_changeGestureSequence.Count} recorded";
                ChangeGestureStatusText.Text = "Captured above. Click Next Gesture when ready.";
                ResetIdleTimer();
                return;
            }

            if (_changeGestureRecorded)
                return;

            _changeGestureIndex++;
            _waitingForNextChangeGesture = false;
            _acceptingChangeGesture = false;
            _pendingChangeGesture = null;
            NextChangeGestureButton.IsEnabled = false;
            UpdateChangeGestureBackButton(_changeGestureSequence.Count > 0, "Previous Gesture");
            ChangeGestureDetectedText.Text = "Ready for next gesture";
            ChangeGestureStatusText.Text =
                $"{ChangeGestureStepLabel()}: wait for the countdown, then hold a hand sign.";
            StartChangeGestureCountdown();
            ResetIdleTimer();
        }

        private void RedoChangeGestureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingChangeGesture != null)
            {
                _pendingChangeGesture = null;
            }
            else if (_changeGestureSequence.Count > 0)
            {
                _changeGestureSequence.RemoveAt(_changeGestureSequence.Count - 1);
            }

            _changeGestureIndex = Math.Max(0, _changeGestureSequence.Count);
            _waitingForNextChangeGesture = false;
            _changeGestureRecorded = false;
            _acceptingChangeGesture = false;
            _pendingChangeGesture = null;
            UpdateChangeGestureRecordedSummary();
            ChangeGestureDetectedText.Text = "Ready to record again";
            ChangeGestureStatusText.Text =
                $"{ChangeGestureStepLabel()}: wait for the countdown, then hold a hand sign.";
            NextChangeGestureButton.IsEnabled = false;
            NextChangeGestureButton.Content = "Next Gesture";
            UpdateChangeGestureBackButton(_changeGestureSequence.Count > 0, "Previous Gesture");
            SaveChangeGestureButton.IsEnabled = false;
            StartChangeGestureCountdown();
            ResetIdleTimer();
        }

        private void UpdateChangeGestureBackButton(bool isEnabled, string text)
        {
            RedoChangeGestureButton.Content = text;
            RedoChangeGestureButton.IsEnabled = isEnabled;
        }

        private void UpdateChangeGestureRecordedSummary()
        {
            ChangeGestureRecordedText.Text = _changeGestureSequence.Count == 0
                ? "No gestures recorded yet"
                : $"Captured: {string.Join(" -> ", _changeGestureSequence.Select(GestureDirectionToLabel))}";
        }

        private static string GestureDirectionToLabel(string direction) => direction switch
        {
            "OPEN_HAND" => "Open hand",
            "FIST" => "Fist",
            "POINT" => "Point",
            "THUMB_UP" => "Thumbs up",
            "THUMB_DOWN" => "Thumbs down",
            "VICTORY" => "Victory",
            "I_LOVE_YOU" => "I love you",
            "OK_SIGN" => "OK sign",
            "ROCK" => "Rock",
            "THREE" => "Three fingers",
            "FOUR" => "Four fingers",
            "CALL_ME" => "Call me",
            _ => direction
        };

        private void SaveChangeGestureSequence()
        {
            var settings = ApplicationData.Current.LocalSettings;
            string savedSequence = string.Join("|", _changeGestureSequence.Take(3));
            settings.Values["RegisteredGesture"] = _changeGestureSequence[0];
            settings.Values["RegisteredGestureSequence"] = savedSequence;
            settings.Values["GestureHint"] = ChangeGestureHintBox.Text.Trim();
            AuthAttemptService.Reset("Gesture");
        }
        private async void SaveChangeGestureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_changeGestureSequence.Count < 3)
            {
                await ShowDialog("Gesture incomplete", "Record three gestures before saving.");
                return;
            }

            SaveChangeGestureSequence();

            StopChangeGestureRegistration();
            ResetChangeGestureUi();

            await ShowDialog("Gesture updated",
                "Your new gesture sequence has been saved. Lock the vault and sign in again to use it.");
            ResetIdleTimer();
        }

        private void StopChangeGestureRegistration()
        {
            _changeGestureService?.Stop();
            _changeGestureService?.Dispose();
            _changeGestureService = null;
            _acceptingChangeGesture = false;
            CancelChangeGestureCountdown();
        }

        private async void StartChangeGestureCountdown()
        {
            if (_changeGestureService == null || !_changeGestureService.IsRunning || _changeGestureRecorded)
                return;

            int countdownRun = ++_changeGestureCountdownRun;
            _acceptingChangeGesture = false;
            _pendingChangeGesture = null;
            for (int i = 3; i >= 1; i--)
            {
                if (countdownRun != _changeGestureCountdownRun || _changeGestureService == null || !_changeGestureService.IsRunning || _waitingForNextChangeGesture || _changeGestureRecorded)
                {
                    _changeGestureCountdownPopup.Hide();
                    return;
                }

                ChangeGestureStatusText.Text = i.ToString();
                _changeGestureCountdownPopup.Show(
                    this.Content.XamlRoot,
                    $"{ChangeGestureStepLabel()} arming",
                    i,
                    "Get your new hand sign ready.");
                await Task.Delay(1000);
            }

            if (countdownRun != _changeGestureCountdownRun || _changeGestureService == null || !_changeGestureService.IsRunning || _waitingForNextChangeGesture || _changeGestureRecorded)
            {
                _changeGestureCountdownPopup.Hide();
                return;
            }

            _changeGestureCountdownPopup.Hide();
            ChangeGestureStatusText.Text = "Go - hold your hand sign steady.";
            _acceptingChangeGesture = true;
        }

        private void CancelChangeGestureCountdown()
        {
            _changeGestureCountdownRun++;
            _changeGestureCountdownPopup.Hide();
        }



        private void SaveSettingsButton_Click(object s, RoutedEventArgs e)
        {
            _autoLockEnabled = AutoLockToggle.IsOn;
            _isLightMode = LightModeToggle.IsOn;
            _autoLockMinutes = AutoLockCombo.SelectedIndex switch
            {
                0 => 1,
                1 => 5,
                2 => 10,
                3 => 30,
                _ => 5
            };
            _authLockoutSeconds = AuthLockoutCombo.SelectedIndex switch
            {
                0 => 30,
                1 => 60,
                2 => 300,
                3 => 600,
                _ => 30
            };

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoLockEnabled"] = _autoLockEnabled;
            settings.Values["AutoLockMinutes"] = _autoLockMinutes;
            settings.Values["AuthLockoutSeconds"] = _authLockoutSeconds;
            settings.Values["IsLightMode"] = _isLightMode;
            SaveHints();
            if (_changeGestureSequence.Count >= 3)
                SaveChangeGestureSequence();

            ApplyTheme(_isLightMode);
            StartAutoLockTimer();
            StopChangeGestureRegistration();
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void ApplyTheme(bool lightMode)
        {
            if (this.Content is FrameworkElement root)
                root.RequestedTheme = lightMode
                    ? ElementTheme.Light
                    : ElementTheme.Dark;
            RefreshDisplay();
        }

        // ══════════════════════════════════════════════════════════════════════
        // AUTO-LOCK
        // ══════════════════════════════════════════════════════════════════════
        private void StartAutoLockTimer()
        {
            _autoLockTimer?.Stop();
            _autoLockTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _autoLockTimer.Interval = TimeSpan.FromSeconds(30);
            _autoLockTimer.IsRepeating = true;
            _autoLockTimer.Tick += (s, e) =>
            {
                if (!_autoLockEnabled) return;
                if ((DateTime.Now - _lastActivity).TotalMinutes >= _autoLockMinutes)
                    LockVault();
            };
            _autoLockTimer.Start();
        }

        private void ResetIdleTimer() => _lastActivity = DateTime.Now;

        // Update the LockVault method to handle edge cases
        private void LockVault()
        {
            try
            {
                _autoLockTimer?.Stop();

                // Clean up all decrypted temp files before locking
                foreach (var t in _openTempFiles)
                {
                    try
                    {
                        _storage.CleanupTempFile(t);
                    }
                    catch { /* Best effort per file */ }
                }
                _openTempFiles.Clear();

                _storage.Lock();
            }
            catch { /* Ensure we still clear session and navigate even if cleanup fails */ }

            // Clear session state
            SessionState.MasterPassword = string.Empty;

            // Navigate to login
            var mainWindow = new MainWindow();
            mainWindow.Activate();
            this.Close();
        }

        // Replace the LockVaultButton_Click handler
        private async void LockVaultButton_Click(object s, RoutedEventArgs e)
        {
            var confirmDialog = new ContentDialog
            {
                Title = "Lock Vault",
                Content = "Are you sure you want to lock the vault? Any unsaved changes will be preserved.",
                PrimaryButtonText = "Lock",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                LockVault();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // THEME HELPERS
        // ══════════════════════════════════════════════════════════════════════

        private static TextBlock IconGlyph(string glyph, double size = 16) => new()
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private Color CardBackground =>
            _isLightMode
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 30, 41, 59);

        private Color CardBorder =>
            _isLightMode
                ? Color.FromArgb(255, 217, 226, 242)
                : Color.FromArgb(255, 38, 50, 68);

        private Color PrimaryText =>
            _isLightMode
                ? Color.FromArgb(255, 26, 26, 26)
                : Color.FromArgb(255, 249, 250, 251);

        private Color SecondaryText =>
            _isLightMode
                ? Color.FromArgb(255, 102, 112, 133)
                : Color.FromArgb(255, 148, 163, 184);

        // ══════════════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════════════
        private async System.Threading.Tasks.Task ShowDialog(
            string title, string msg)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = msg,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}
