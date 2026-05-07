using GestureVault.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        private bool _currentPwVisible = false;
        private bool _newPwVisible = false;
        private bool _confirmPwVisible = false;

        // Tracks temp files open so we can clean them on lock
        private readonly List<string> _openTempFiles = new();

        // ── Auto-lock ─────────────────────────────────────────────────────────
        private DispatcherQueueTimer? _autoLockTimer;
        private int _autoLockMinutes = 5;
        private bool _autoLockEnabled = true;
        private bool _isLightMode = true;
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
            string emoji = item.Type switch
            {
                VaultItemType.Password => "🔑",
                VaultItemType.Note => "📝",
                VaultItemType.Document => "📄",
                _ => "📦"
            };

            string subtitle = item.Type switch
            {
                VaultItemType.Password =>
                    $"Password • Updated {item.UpdatedAt}",
                VaultItemType.Note =>
                    $"Secure Note • Updated {item.UpdatedAt}",
                VaultItemType.Document =>
                    $"Document • {item.Files.Count} file(s) • {item.UpdatedAt}",
                _ => item.UpdatedAt
            };

            var openBtn = new Button
            {
                Content = "Open",
                Width = 90,
                Style = (Style)((FrameworkElement)this.Content)
                              .Resources["CardButtonStyle"]
            };
            openBtn.Click += (s, e) => OpenItem(item);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = emoji,
                FontSize = 28,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            Grid.SetColumn(icon, 0);

            var info = new StackPanel();
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
            Grid.SetColumn(info, 1);
            Grid.SetColumn(openBtn, 2);

            grid.Children.Add(icon);
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

        // ══════════════════════════════════════════════════════════════════════
        // ADD ITEM
        // ══════════════════════════════════════════════════════════════════════
        private void AddItemButton_Click(object sender, RoutedEventArgs e)
        {
            ItemTypeCombo.SelectedIndex = 0;
            ItemTitleBox.Text = string.Empty;
            ItemUsernameBox.Text = string.Empty;
            ItemPasswordBox.Password = string.Empty;
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

        private void ItemTypeCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (PasswordFields == null) return;
            switch (ItemTypeCombo.SelectedIndex)
            {
                case 0: ShowPasswordFields(); break;
                case 1: ShowNoteFields(); break;
                case 2: ShowDocumentFields(); break;
            }
        }

        private void ShowPasswordFields()
        {
            PasswordFields.Visibility = Visibility.Visible;
            NoteContentBox.Visibility = Visibility.Collapsed;
            DocumentFields.Visibility = Visibility.Collapsed;
        }
        private void ShowNoteFields()
        {
            PasswordFields.Visibility = Visibility.Collapsed;
            NoteContentBox.Visibility = Visibility.Visible;
            DocumentFields.Visibility = Visibility.Collapsed;
        }
        private void ShowDocumentFields()
        {
            PasswordFields.Visibility = Visibility.Collapsed;
            NoteContentBox.Visibility = Visibility.Collapsed;
            DocumentFields.Visibility = Visibility.Visible;
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
        /// e.g. "Documents\Tax2024\receipt.pdf"
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
                    item.Password = ItemPasswordBox.Password;
                    item.Url = ItemUrlBox.Text.Trim();
                    break;

                case 1:
                    item.Type = VaultItemType.Note;
                    item.NoteContent = NoteContentBox.Text.Trim();
                    break;

                case 2:
                    item.Type = VaultItemType.Document;
                    item.Description = ItemDescriptionBox.Text.Trim();

                    // Encrypt and copy each pending file into the vault
                    foreach (var (sourcePath, _) in _pendingFiles)
                    {
                        try
                        {
                            var vf = _storage.AddFileToItem(item.Id, sourcePath);
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
            ViewItemTitle.Text = item.Title;
            ViewItemType.Text = item.Type switch
            {
                VaultItemType.Password => "🔑 Password",
                VaultItemType.Note => "📝 Secure Note",
                VaultItemType.Document => "📄 Document",
                _ => "Item"
            };

            ViewItemContent.Children.Clear();

            switch (item.Type)
            {
                case VaultItemType.Password:
                    AddViewRow("Username", item.Username ?? "—");
                    AddViewRow("Password", item.Password ?? "—",
                               isPassword: true);
                    if (!string.IsNullOrEmpty(item.Url))
                        AddViewRow("Website", item.Url);
                    break;

                case VaultItemType.Note:
                    AddViewRow("Note", item.NoteContent ?? "—");
                    break;

                case VaultItemType.Document:
                    AddViewRow("Description", item.Description ?? "—");
                    BuildDocumentFileList(item);
                    break;
            }

            AddViewRow("Last updated", item.UpdatedAt);
            ViewItemOverlay.Visibility = Visibility.Visible;
            ResetIdleTimer();
        }

        // ── Document file list inside the view overlay ────────────────────────
        private void BuildDocumentFileList(VaultItem item)
        {
            // Section header + Add Files button
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });

            headerGrid.Children.Add(new TextBlock
            {
                Text = $"Files ({item.Files.Count})",
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

            if (item.Files.Count == 0)
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
                return;
            }

            foreach (var vf in item.Files)
                ViewItemContent.Children.Add(BuildFileRow(item, vf));
        }

        private Border BuildFileRow(VaultItem item, VaultFile vf)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });

            var namePanel = new StackPanel();
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
                Text = $"Added {vf.AddedAt}  •  🔒 encrypted",
                FontSize = 11,
                Foreground = new SolidColorBrush(SecondaryText)
            });
            Grid.SetColumn(namePanel, 0);

            // Open button — decrypts to temp and launches with default app
            var openBtn = new Button
            {
                Content = "Open",
                Width = 64,
                Height = 32,
                Margin = new Thickness(8, 0, 6, 0),
                Style = (Style)((FrameworkElement)this.Content)
                              .Resources["CardButtonStyle"]
            };
            openBtn.Click += async (s, e) =>
            {
                try
                {
                    string tempPath = _storage.DecryptFileToTemp(vf);
                    _openTempFiles.Add(tempPath);
                    // Launch with default system app
                    var launchFile = await StorageFile.GetFileFromPathAsync(tempPath);
                    await Windows.System.Launcher.LaunchFileAsync(launchFile);
                }
                catch (Exception ex)
                {
                    await ShowDialog("Open failed",
                        $"Could not open file: {ex.Message}");
                }
                ResetIdleTimer();
            };
            Grid.SetColumn(openBtn, 1);

            // Delete button — removes from vault
            var delBtn = new Button
            {
                Content = "🗑",
                Width = 36,
                Height = 32,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(
                    Color.FromArgb(255, 229, 57, 53))
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
                    _storage.RemoveFileFromItem(vf);
                    item.Files.Remove(vf);
                    item.UpdatedAt = DateTime.Now.ToString("MMM dd, yyyy");
                    SaveItems();
                    RefreshDisplay();
                    // Refresh the view overlay with updated file list
                    OpenItem(item);
                }
                ResetIdleTimer();
            };
            Grid.SetColumn(delBtn, 2);

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

        // ── Add more files to an existing document item ───────────────────────
        private async void AddFilesToExistingItem(VaultItem item)
        {
            // Ask whether to add files or a folder
            var choice = new ContentDialog
            {
                Title = "Add to vault item",
                Content = "Would you like to add individual files or an entire folder?",
                PrimaryButtonText = "📄 Files",
                SecondaryButtonText = "📁 Folder",
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
                    var vf = _storage.AddFileToItem(item.Id, path);
                    // Override stored display name with relative path for folders
                    vf.FileName = name;
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
                var pwRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10
                };
                var pwText = new TextBlock
                {
                    Text = new string('•', value.Length),
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(PrimaryText)
                };
                var revealBtn = new Button
                {
                    Content = "👁",
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4)
                };
                bool revealed = false;
                revealBtn.Click += (s, e) =>
                {
                    revealed = !revealed;
                    pwText.Text = revealed ? value : new string('•', value.Length);
                    revealBtn.Content = revealed ? "🙈" : "👁";
                };
                pwRow.Children.Add(pwText);
                pwRow.Children.Add(revealBtn);
                row.Children.Add(pwRow);
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

            var confirm = new ContentDialog
            {
                Title = "Delete item",
                Content = $"Permanently delete \"{_selectedItem.Title}\"? " +
                            $"All attached files will be securely erased.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                // Use VaultStorageService.DeleteItem which handles removing
                // associated encrypted files and metadata.
                _storage.DeleteItem(_selectedItem);

                _allItems.Remove(_selectedItem);
                SaveItems();
                RefreshDisplay();
                ViewItemOverlay.Visibility = Visibility.Collapsed;
                _selectedItem = null;
            }
            ResetIdleTimer();
        }

        private void CloseViewItem_Click(object sender, RoutedEventArgs e)
        {
            ViewItemOverlay.Visibility = Visibility.Collapsed;
            _selectedItem = null;
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
            FilterDocumentButton.Style =
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
        private void FilterDocuments_Click(object s, RoutedEventArgs e) =>
            SetFilter(VaultItemType.Document, "Documents");

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
            _isLightMode = settings.Values["IsLightMode"] as bool? ?? true;

            AutoLockToggle.IsOn = _autoLockEnabled;
            LightModeToggle.IsOn = _isLightMode;
            AutoLockCombo.SelectedIndex = _autoLockMinutes switch
            {
                1 => 0,
                5 => 1,
                10 => 2,
                30 => 3,
                _ => 1
            };
            ApplyTheme(_isLightMode);
        }

        private void SettingsButton_Click(object s, RoutedEventArgs e)
        {
            // Clear credential fields every time the overlay opens
            CurrentPasswordBox.Password = string.Empty;
            ChangeNewPasswordBox.Password = string.Empty;
            ChangeConfirmPasswordBox.Password = string.Empty;
            ChangePassphraseBox.Text = string.Empty;

            SettingsOverlay.Visibility = Visibility.Visible;
            ResetIdleTimer();
        }

        private void CloseSettingsButton_Click(object s, RoutedEventArgs e) =>
            SettingsOverlay.Visibility = Visibility.Collapsed;

        private void ToggleCurrentPw_Click(object sender, RoutedEventArgs e)
        {
            _currentPwVisible = !_currentPwVisible;
            if (_currentPwVisible)
            {
                CurrentPasswordTextBox.Text = CurrentPasswordBox.Password;
                CurrentPasswordTextBox.Visibility = Visibility.Visible;
                CurrentPasswordBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                CurrentPasswordBox.Password = CurrentPasswordTextBox.Text;
                CurrentPasswordBox.Visibility = Visibility.Visible;
                CurrentPasswordTextBox.Visibility = Visibility.Collapsed;
            }
            EyeIconCurrent.Text = _currentPwVisible ? "\uED1A" : "\uE7B3";
        }

        private void ToggleNewPw_Click(object sender, RoutedEventArgs e)
        {
            _newPwVisible = !_newPwVisible;
            if (_newPwVisible)
            {
                ChangeNewPasswordTextBox.Text = ChangeNewPasswordBox.Password;
                ChangeNewPasswordTextBox.Visibility = Visibility.Visible;
                ChangeNewPasswordBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                ChangeNewPasswordBox.Password = ChangeNewPasswordTextBox.Text;
                ChangeNewPasswordBox.Visibility = Visibility.Visible;
                ChangeNewPasswordTextBox.Visibility = Visibility.Collapsed;
            }
            EyeIconNew.Text = _newPwVisible ? "\uED1A" : "\uE7B3";
        }

        private void ToggleConfirmPw_Click(object sender, RoutedEventArgs e)
        {
            _confirmPwVisible = !_confirmPwVisible;
            if (_confirmPwVisible)
            {
                ChangeConfirmPasswordTextBox.Text = ChangeConfirmPasswordBox.Password;
                ChangeConfirmPasswordTextBox.Visibility = Visibility.Visible;
                ChangeConfirmPasswordBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                ChangeConfirmPasswordBox.Password = ChangeConfirmPasswordTextBox.Text;
                ChangeConfirmPasswordBox.Visibility = Visibility.Visible;
                ChangeConfirmPasswordTextBox.Visibility = Visibility.Collapsed;
            }
            EyeIconConfirm.Text = _confirmPwVisible ? "\uED1A" : "\uE7B3";
        }

        // ── Change password ───────────────────────────────────────────────────
        private async void ChangePasswordButton_Click(object s, RoutedEventArgs e)
        {
            string current = _currentPwVisible ? CurrentPasswordTextBox.Text : CurrentPasswordBox.Password;
            string newPw = _newPwVisible ? ChangeNewPasswordTextBox.Text : ChangeNewPasswordBox.Password;
            string confirm = _confirmPwVisible ? ChangeConfirmPasswordTextBox.Text : ChangeConfirmPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(current) ||
                string.IsNullOrWhiteSpace(newPw) ||
                string.IsNullOrWhiteSpace(confirm))
            {
                await ShowDialog("Missing fields",
                    "Please fill in all three password fields.");
                return;
            }

            // Verify current password
            string enteredHash = HashPassword(current);
            string? storedHash = ApplicationData.Current.LocalSettings
                                     .Values["MasterPasswordHash"] as string;
            if (storedHash == null || enteredHash != storedHash)
            {
                await ShowDialog("Incorrect password",
                    "The current password you entered is wrong.");
                CurrentPasswordBox.Password = string.Empty;
                return;
            }

            if (newPw.Length < 8)
            {
                await ShowDialog("Password too short",
                    "Your new password must be at least 8 characters.");
                return;
            }
            if (newPw != confirm)
            {
                await ShowDialog("Passwords don't match",
                    "The new password and confirmation don't match.");
                ChangeNewPasswordBox.Password = string.Empty;
                ChangeConfirmPasswordBox.Password = string.Empty;
                return;
            }

            // Persist new hash and update session
            ApplicationData.Current.LocalSettings.Values["MasterPasswordHash"] =
                HashPassword(newPw);
            SessionState.MasterPassword = newPw;
            _storage.UnlockWithPassword(newPw);

            CurrentPasswordBox.Password = string.Empty;
            ChangeNewPasswordBox.Password = string.Empty;
            ChangeConfirmPasswordBox.Password = string.Empty;

            await ShowDialog("Password updated",
                "Your master password has been changed successfully.");
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

            // TODO: encrypt before saving (Issue #2 — plaintext passphrase in LocalSettings)
            ApplicationData.Current.LocalSettings.Values["RegisteredPassphrase"] = phrase;

            ChangePassphraseBox.Text = string.Empty;

            await ShowDialog("Passphrase updated",
                "Your voice passphrase has been changed. It will take effect on the next login.");
            ResetIdleTimer();
        }

        // ── Hash helper (mirrors MainWindow / RegistrationWindow) ─────────────
        private static string HashPassword(string password)
        {
            byte[] bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes);
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

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoLockEnabled"] = _autoLockEnabled;
            settings.Values["AutoLockMinutes"] = _autoLockMinutes;
            settings.Values["IsLightMode"] = _isLightMode;

            ApplyTheme(_isLightMode);
            StartAutoLockTimer();
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

        private void LockVault()
        {
            _autoLockTimer?.Stop();

            // Clean up all decrypted temp files before locking
            foreach (var t in _openTempFiles)
                _storage.CleanupTempFile(t);
            _openTempFiles.Clear();

            _storage.Lock();
            SessionState.MasterPassword = string.Empty;

            var mainWindow = new MainWindow();
            mainWindow.Activate();
            this.Close();
        }

        private void LockVaultButton_Click(object s, RoutedEventArgs e) =>
            LockVault();

        // ══════════════════════════════════════════════════════════════════════
        // THEME HELPERS
        // ══════════════════════════════════════════════════════════════════════
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