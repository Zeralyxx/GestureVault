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

        // Tracks temp files open so we can clean them on lock
        private readonly List<string> _openTempFiles = new();

        // ── Auto-lock ─────────────────────────────────────────────────────────
        private DispatcherQueueTimer? _autoLockTimer;
        private int _autoLockMinutes = 5;
        private bool _autoLockEnabled = true;
        private bool _isLightMode = true;
        private bool _currentPasswordVisible = false;
        private bool _newPasswordVisible = false;
        private bool _confirmPasswordVisible = false;
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
            string itemBadge = item.Type switch
            {
                VaultItemType.Password => "PW",
                VaultItemType.Note => "NT",
                VaultItemType.Document => "FL",
                _ => "IT"
            };

            string subtitle = item.Type switch
            {
                VaultItemType.Password =>
                    $"Password | Updated {item.UpdatedAt}",
                VaultItemType.Note =>
                    $"Secure Note | Updated {item.UpdatedAt}",
                VaultItemType.Document =>
                    $"Files | {item.Files.Count} file(s) | {item.UpdatedAt}",
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
                Text = itemBadge,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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
            ViewItemTitle.Text = item.Title;
            ViewItemType.Text = item.Type switch
            {
                VaultItemType.Password => "Password",
                VaultItemType.Note => "Secure Note",
                VaultItemType.Document => "Files",
                _ => "Item"
            };

            ViewItemContent.Children.Clear();

            switch (item.Type)
            {
                case VaultItemType.Password:
                    AddViewRow("Username", item.Username ?? "-");
                    AddViewRow("Password", item.Password ?? "-",
                               isPassword: true);
                    if (!string.IsNullOrEmpty(item.Url))
                        AddViewRow("Website", item.Url);
                    break;

                case VaultItemType.Note:
                    AddViewRow("Note", item.NoteContent ?? "-");
                    break;

                case VaultItemType.Document:
                    AddViewRow("Description", item.Description ?? "-");
                    BuildFilesFileList(item);
                    break;
            }

            AddViewRow("Last updated", item.UpdatedAt);
            ViewItemOverlay.Visibility = Visibility.Visible;
            ResetIdleTimer();
        }

        // ── Files file list inside the view overlay ────────────────────────
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

            AddImagePreviewStrip(item);

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
                    _storage.RemoveFileFromItem(vf);
                    item.Files.Remove(vf);
                    item.UpdatedAt = DateTime.Now.ToString("MMM dd, yyyy");
                    SaveItems();
                    RefreshDisplay();
                    OpenItem(item);
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

        private VaultFile ImportFileIntoVault(VaultItem item, string sourcePath, string? displayName = null)
        {
            var vf = _storage.AddFileToItem(item.Id, sourcePath);
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
            var imageFiles = item.Files.Where(f => IsImageFile(f.FileName)).Take(12).ToList();
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
                    Content = "Show",
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
                    revealBtn.Content = revealed ? "Hide" : "Show";
                };
                Grid.SetColumn(revealBtn, 1);

                var copyBtn = new Button
                {
                    Content = "Copy",
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6),
                    VerticalAlignment = VerticalAlignment.Center
                };
                copyBtn.Click += async (s, e) =>
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(value);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

                    // Brief visual feedback
                    copyBtn.Content = "Copied";
                    await System.Threading.Tasks.Task.Delay(1500);
                    copyBtn.Content = "Copy";
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
                // Delete encrypted files from disk if file
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

            SettingsOverlay.Visibility = Visibility.Visible;
            ResetIdleTimer();
        }

        private void CloseSettingsButton_Click(object s, RoutedEventArgs e) =>
            SettingsOverlay.Visibility = Visibility.Collapsed;

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
            button.Content = visible ? "Hide" : "Show";
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
            SaveHints();

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
