using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using ICSharpCode.AvalonEdit.Highlighting;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Rendering;

namespace RblxExecutorUI
{
    public class EditorTabItem : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _content = string.Empty;

        public string Title
        {
            get => _title;
            set
            {
                if (_title == value) return;
                _title = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (_content == value) return;
                _content = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ScriptItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsRemote { get; set; }
        public string Meta { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public string KeyBadge { get; set; } = "Keyless";
        public string AccessBadge { get; set; } = "Free";
        public string TrustBadge { get; set; } = "Community";
    }

    public class UiSettings
    {
        public bool WordWrap { get; set; } = false;
        public bool ShowLineNumbers { get; set; } = true;
        public bool HighlightCurrentLine { get; set; } = true;
        public bool ShowWhitespace { get; set; } = false;
        public bool ConvertTabsToSpaces { get; set; } = true;
        public bool ScrollPastEnd { get; set; } = true;
        public bool AutoAttach { get; set; } = false;
        public bool ConfirmBeforeKill { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;
        public bool AutoRefreshScriptHubOnOpen { get; set; } = false;
        public bool DebugConsole { get; set; } = false;
        public int EditorFontSize { get; set; } = 15;
        public int TabSize { get; set; } = 4;
        public string EditorFontFamily { get; set; } = "JetBrains Mono";
    }

    public partial class MainWindow : Window
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        private DispatcherTimer _notificationTimer;
        private DispatcherTimer _autoAttachTimer;
        public ObservableCollection<ScriptItem> ScriptsList { get; set; } = new ObservableCollection<ScriptItem>();
        public ObservableCollection<EditorTabItem> EditorTabs { get; set; } = new ObservableCollection<EditorTabItem>();
        public EditorTabItem? SelectedEditorTab { get; set; }
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly DispatcherTimer _scriptSearchDebounceTimer = new DispatcherTimer();
        private readonly HashSet<string> _loadedScriptUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_settings.json");
        private UiSettings _uiSettings = new UiSettings();
        private ScrollViewer? _editorTabsScrollViewer;

        private bool _isExecuting = false;
        private bool _isSwitchingTabs = false;
        private bool _isLoadingScripts = false;
        private bool _hasMoreScripts = true;
        private bool _isAttached = false;
        private bool _isAutoAttachAttemptRunning = false;
        private bool _isApplyingSettings = false;
        private bool _isUpdatingHubToggles = false;
        private int _tabCounter = 1;
        private int _currentScriptPage = 1;
        private string _currentScriptQuery = string.Empty;
        private DateTime _lastScriptHubRefreshUtc = DateTime.MinValue;
        private ScriptItem? _activeScriptDetailsItem;

        private const string DefaultEditorTemplate =
            "-- Aureo Executor\n" +
            "-- made by jinx\n" +
            "-- https://discord.gg/DXZffUNtcN\n\n" +
            "local Players = game:GetService(\"Players\")\n" +
            "local LocalPlayer = Players.LocalPlayer\n\n" +
            "print((\"Aureo loaded for %s\"):format(LocalPlayer.Name))";

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            DataContext = this;

            _notificationTimer = new DispatcherTimer();
            _notificationTimer.Interval = TimeSpan.FromSeconds(3);
            _notificationTimer.Tick += (s, e) => CloseNotification();
            _autoAttachTimer = new DispatcherTimer();
            _autoAttachTimer.Interval = TimeSpan.FromSeconds(6);
            _autoAttachTimer.Tick += AutoAttachTimer_Tick;

            ScriptHubList.ItemsSource = ScriptsList;
            _httpClient.Timeout = TimeSpan.FromSeconds(12);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Aureo/1.0 (+https://rscripts.net)");
            _scriptSearchDebounceTimer.Interval = TimeSpan.FromMilliseconds(420);
            _scriptSearchDebounceTimer.Tick += ScriptSearchDebounceTimer_Tick;
            InitializeEditorTabs();
            EditorTabs.CollectionChanged += EditorTabs_CollectionChanged;
            
            try 
            {
                string xshdPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lua.xshd");
                if (File.Exists(xshdPath))
                {
                    using (XmlTextReader reader = new XmlTextReader(xshdPath))
                    {
                        Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                    }
                }
            } 
            catch (Exception ex)
            {
                App.LogException(ex, "LoadLuaHighlighting");
            }
        }

        private void LoadScriptsFolder()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                ScriptsList.Clear();
                var files = Directory.GetFiles(path, "*.*")
                                     .Where(s => s.EndsWith(".lua") || s.EndsWith(".luau") || s.EndsWith(".txt"));

                foreach (var file in files)
                {
                    ScriptsList.Add(new ScriptItem
                    { 
                        Name = Path.GetFileName(file), 
                        FullPath = file,
                        IsRemote = false,
                        Meta = "Local script",
                        Description = "Local script from your Scripts folder.",
                        ImageUrl = string.Empty,
                        KeyBadge = "Local",
                        AccessBadge = "Editable",
                        TrustBadge = "Workspace"
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogException(ex, "LoadScriptsFolder");
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var luaDef = HighlightingManager.Instance.GetDefinition("Lua");
                if (luaDef != null)
                {
                    Editor.SyntaxHighlighting = luaDef;
                }
                if (SelectedEditorTab != null)
                {
                    _isSwitchingTabs = true;
                    Editor.Text = SelectedEditorTab.Content;
                    _isSwitchingTabs = false;
                }

                ApplyEditorVisualTuning();
                LoadUiSettings();
                ApplyUiSettingsToControls();
                ApplyEditorRuntimeSettings();
                UpdateHubAvailability();
                if (_uiSettings.DebugConsole)
                {
                    ToggleConsole_Checked(this, new RoutedEventArgs());
                }
                else
                {
                    ToggleConsole_Unchecked(this, new RoutedEventArgs());
                }
                
                InitializeCore();
                UpdateAttachStateUi();
                await StartScriptHubQueryAsync(string.Empty);
                if (_uiSettings.AutoAttach)
                {
                    _autoAttachTimer.Start();
                    _ = AttemptAttachAsync(false);
                }
            }
            catch (Exception ex)
            {
                InjectionStatus.Text = "INIT ERROR";
                InjectionStatus.Foreground = Brushes.Orange;
                App.LogException(ex, "MainWindow_Loaded");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InitializeCore()
        {
            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Syntax.dll");
            if (!ValidateNativeDll(dllPath, out string validationError))
            {
                InjectionStatus.Text = "DLL ERROR";
                InjectionStatus.Foreground = Brushes.Orange;
                App.LogException(new BadImageFormatException(validationError), "InitializeCore");
                return;
            }

            bool init = RblxCore.Initialize();
            if (init)
            {
                InjectionStatus.Text = "NOT ATTACHED";
                InjectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(142, 142, 142)); // SilverText
                _isAttached = false;
            }
            else
            {
                InjectionStatus.Text = "SYSCALL FAILED";
                InjectionStatus.Foreground = Brushes.Red;
                _isAttached = false;
            }

            UpdateAttachStateUi();
        }

        private void InitializeEditorTabs()
        {
            EditorTabs.Clear();
            _tabCounter = 1;
            EditorTabs.Add(new EditorTabItem
            {
                Title = $"Untitled {_tabCounter++}",
                Content = DefaultEditorTemplate
            });
            SelectedEditorTab = EditorTabs[0];
        }

        private void AddEditorTab_Click(object sender, RoutedEventArgs e)
        {
            var newTab = new EditorTabItem
            {
                Title = $"Untitled {_tabCounter++}",
                Content = DefaultEditorTemplate
            };
            EditorTabs.Add(newTab);
            EditorTabsList.SelectedItem = newTab;
            UpdateEditorTabsHeaderLayout();
            ShowNotification($"Opened {newTab.Title}", false);
        }

        private void CloseEditorTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not EditorTabItem tab) return;

            if (EditorTabs.Count == 1)
            {
                tab.Content = string.Empty;
                tab.Title = "Untitled 1";
                Editor.Clear();
                Editor.Focus();
                return;
            }

            int index = EditorTabs.IndexOf(tab);
            if (index < 0) return;

            bool wasSelected = ReferenceEquals(SelectedEditorTab, tab);
            EditorTabs.Remove(tab);

            if (wasSelected)
            {
                int nextIndex = Math.Min(index, EditorTabs.Count - 1);
                EditorTabsList.SelectedItem = EditorTabs[nextIndex];
            }

            UpdateEditorTabsHeaderLayout();
        }

        private void EditorTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSwitchingTabs) return;

            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is EditorTabItem oldTab)
            {
                oldTab.Content = Editor.Text;
            }

            if (e.AddedItems.Count > 0 && e.AddedItems[0] is EditorTabItem newTab)
            {
                SelectedEditorTab = newTab;
                _isSwitchingTabs = true;
                Editor.Text = newTab.Content;
                Editor.CaretOffset = Editor.Text.Length;
                _isSwitchingTabs = false;
                Editor.Focus();
            }
        }

        private void Editor_TextChanged(object sender, EventArgs e)
        {
            if (_isSwitchingTabs || SelectedEditorTab == null) return;
            SelectedEditorTab.Content = Editor.Text;
        }

        private void FocusEditor_Click(object sender, RoutedEventArgs e)
        {
            Editor.Focus();
        }

        private void EditorTabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(UpdateEditorTabsHeaderLayout, DispatcherPriority.Background);
        }

        private void EditorTabsList_Loaded(object sender, RoutedEventArgs e)
        {
            _editorTabsScrollViewer = FindDescendant<ScrollViewer>(EditorTabsList);
            UpdateEditorTabsHeaderLayout();
        }

        private void EditorTabsHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateEditorTabsHeaderLayout();
        }

        private void EditorTabsList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _editorTabsScrollViewer ??= FindDescendant<ScrollViewer>(EditorTabsList);
            if (_editorTabsScrollViewer == null) return;
            if (_editorTabsScrollViewer.ExtentWidth <= _editorTabsScrollViewer.ViewportWidth + 0.5) return;

            double delta = e.Delta > 0 ? -120 : 120;
            double nextOffset = _editorTabsScrollViewer.HorizontalOffset + delta;
            if (nextOffset < 0) nextOffset = 0;
            double maxOffset = Math.Max(0, _editorTabsScrollViewer.ExtentWidth - _editorTabsScrollViewer.ViewportWidth);
            if (nextOffset > maxOffset) nextOffset = maxOffset;
            _editorTabsScrollViewer.ScrollToHorizontalOffset(nextOffset);
            e.Handled = true;
        }

        private void UpdateEditorTabsHeaderLayout()
        {
            if (EditorTabsHeaderGrid == null || EditorTabsColumn == null || AddTabButton == null || EditorTabsList == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                _editorTabsScrollViewer ??= FindDescendant<ScrollViewer>(EditorTabsList);
                if (_editorTabsScrollViewer == null) return;

                double addButtonWidth = AddTabButton.ActualWidth;
                double availableTabsWidth = Math.Max(0, EditorTabsHeaderGrid.ActualWidth - addButtonWidth - 12);
                bool overflow = _editorTabsScrollViewer.ExtentWidth > availableTabsWidth + 0.5;

                EditorTabsColumn.Width = overflow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
                if (!overflow)
                {
                    _editorTabsScrollViewer.ScrollToHorizontalOffset(0);
                }
            }, DispatcherPriority.Loaded);
        }

        private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T target)
                {
                    return target;
                }

                T? result = FindDescendant<T>(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private void LoadUiSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    UiSettings? loaded = JsonSerializer.Deserialize<UiSettings>(json);
                    if (loaded != null)
                    {
                        _uiSettings = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogException(ex, "LoadUiSettings");
            }
        }

        private void SaveUiSettings()
        {
            if (_isApplyingSettings) return;

            try
            {
                string json = JsonSerializer.Serialize(_uiSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                App.LogException(ex, "SaveUiSettings");
            }
        }

        private void ApplyUiSettingsToControls()
        {
            _isApplyingSettings = true;
            try
            {
                WordWrapToggle.IsChecked = _uiSettings.WordWrap;
                LineNumbersToggle.IsChecked = _uiSettings.ShowLineNumbers;
                HighlightCurrentLineToggle.IsChecked = _uiSettings.HighlightCurrentLine;
                ShowWhitespaceToggle.IsChecked = _uiSettings.ShowWhitespace;
                ConvertTabsToggle.IsChecked = _uiSettings.ConvertTabsToSpaces;
                ScrollPastEndToggle.IsChecked = _uiSettings.ScrollPastEnd;
                AutoAttachToggle.IsChecked = _uiSettings.AutoAttach;
                ConfirmKillToggle.IsChecked = _uiSettings.ConfirmBeforeKill;
                ShowNotificationsToggle.IsChecked = _uiSettings.ShowNotifications;
                AutoRefreshScriptHubToggle.IsChecked = _uiSettings.AutoRefreshScriptHubOnOpen;
                DebugConsoleToggle.IsChecked = _uiSettings.DebugConsole;

                int size = Math.Clamp(_uiSettings.EditorFontSize, 12, 24);
                int tabSize = Math.Clamp(_uiSettings.TabSize, 2, 8);
                EditorFontSizeSlider.Value = size;
                TabSizeSlider.Value = tabSize;
                EditorFontSizeValueText.Text = size.ToString();
                TabSizeInlineValueText.Text = tabSize.ToString();
                TabSizeValueText.Text = $"{tabSize} Spaces";

                string desiredFont = string.IsNullOrWhiteSpace(_uiSettings.EditorFontFamily) ? "JetBrains Mono" : _uiSettings.EditorFontFamily;
                bool foundFont = false;
                foreach (var item in EditorFontFamilyCombo.Items)
                {
                    if (item is ComboBoxItem cbItem && string.Equals(cbItem.Content?.ToString(), desiredFont, StringComparison.OrdinalIgnoreCase))
                    {
                        EditorFontFamilyCombo.SelectedItem = cbItem;
                        foundFont = true;
                        break;
                    }
                }
                if (!foundFont && EditorFontFamilyCombo.Items.Count > 0)
                {
                    EditorFontFamilyCombo.SelectedIndex = 0;
                }
            }
            finally
            {
                _isApplyingSettings = false;
            }
        }

        private void ApplyEditorRuntimeSettings()
        {
            if (Editor == null) return;
            Editor.WordWrap = _uiSettings.WordWrap;
            Editor.ShowLineNumbers = _uiSettings.ShowLineNumbers;
            Editor.FontSize = Math.Clamp(_uiSettings.EditorFontSize, 12, 24);
            Editor.HorizontalScrollBarVisibility = _uiSettings.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            Editor.Options.HighlightCurrentLine = _uiSettings.HighlightCurrentLine;
            Editor.Options.ShowSpaces = _uiSettings.ShowWhitespace;
            Editor.Options.ShowTabs = _uiSettings.ShowWhitespace;
            Editor.Options.ConvertTabsToSpaces = _uiSettings.ConvertTabsToSpaces;
            Editor.Options.AllowScrollBelowDocument = _uiSettings.ScrollPastEnd;
            Editor.Options.IndentationSize = Math.Clamp(_uiSettings.TabSize, 2, 8);

            string font = string.IsNullOrWhiteSpace(_uiSettings.EditorFontFamily) ? "JetBrains Mono" : _uiSettings.EditorFontFamily;
            try
            {
                Editor.FontFamily = new FontFamily(font);
                SettingsEditorFontPreviewText.Text = font;
            }
            catch
            {
                Editor.FontFamily = new FontFamily("Consolas");
                SettingsEditorFontPreviewText.Text = "Consolas";
            }

            if (EditorFontSizeValueText != null)
            {
                EditorFontSizeValueText.Text = ((int)Editor.FontSize).ToString();
            }
            if (TabSizeInlineValueText != null)
            {
                int appliedTabSize = Editor.Options.IndentationSize;
                TabSizeInlineValueText.Text = appliedTabSize.ToString();
                TabSizeValueText.Text = $"{appliedTabSize} Spaces";
            }
        }

        private void WordWrap_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.WordWrap = true;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void WordWrap_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.WordWrap = false;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void LineNumbers_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ShowLineNumbers = true;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void LineNumbers_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ShowLineNumbers = false;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void HighlightCurrentLine_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.HighlightCurrentLine = true;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void HighlightCurrentLine_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.HighlightCurrentLine = false;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void ShowWhitespace_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ShowWhitespace = true;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void ShowWhitespace_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ShowWhitespace = false;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void ConvertTabs_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ConvertTabsToSpaces = true;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void ConvertTabs_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ConvertTabsToSpaces = false;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void ScrollPastEnd_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ScrollPastEnd = true;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void ScrollPastEnd_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ScrollPastEnd = false;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void EditorFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || Editor == null) return;
            if (EditorFontSizeValueText == null) return;
            int size = (int)Math.Round(e.NewValue);
            _uiSettings.EditorFontSize = size;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void TabSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || Editor == null) return;
            int size = (int)Math.Round(e.NewValue);
            _uiSettings.TabSize = size;
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void EditorFontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || Editor == null || _isApplyingSettings) return;
            if (EditorFontFamilyCombo.SelectedItem is not ComboBoxItem selected || selected.Content is null) return;
            _uiSettings.EditorFontFamily = selected.Content.ToString() ?? "JetBrains Mono";
            ApplyEditorRuntimeSettings();
            SaveUiSettings();
        }

        private void AutoAttach_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.AutoAttach = true;
            _autoAttachTimer.Start();
            SaveUiSettings();
            _ = AttemptAttachAsync(false);
        }

        private void AutoAttach_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.AutoAttach = false;
            _autoAttachTimer.Stop();
            SaveUiSettings();
        }

        private void ConfirmKill_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ConfirmBeforeKill = true;
            SaveUiSettings();
        }

        private void ConfirmKill_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ConfirmBeforeKill = false;
            SaveUiSettings();
        }

        private void ShowNotifications_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ShowNotifications = true;
            SaveUiSettings();
            ShowNotification("Notifications enabled", false);
        }

        private void ShowNotifications_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.ShowNotifications = false;
            SaveUiSettings();
            CloseNotification();
        }

        private void AutoRefreshScriptHub_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.AutoRefreshScriptHubOnOpen = true;
            SaveUiSettings();
        }

        private void AutoRefreshScriptHub_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || Editor == null) return;
            _uiSettings.AutoRefreshScriptHubOnOpen = false;
            SaveUiSettings();
        }

        private async void AutoAttachTimer_Tick(object? sender, EventArgs e)
        {
            if (!_uiSettings.AutoAttach || _isAttached || _isAutoAttachAttemptRunning) return;
            await AttemptAttachAsync(false);
        }

        private void ApplyEditorVisualTuning()
        {
            Editor.Options.EnableHyperlinks = false;
            Editor.Options.EnableEmailHyperlinks = false;
            Editor.Options.CutCopyWholeLine = true;
            Editor.Options.ShowBoxForControlCharacters = false;

            Editor.TextArea.Caret.CaretBrush = new SolidColorBrush(Color.FromRgb(250, 204, 21));
            Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(76, 59, 130, 246));
            Editor.TextArea.SelectionBorder = new Pen(new SolidColorBrush(Color.FromArgb(116, 96, 165, 255)), 1);

            Editor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.FromRgb(130, 180, 255));
            Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(46, 39, 52, 73));
            Editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.FromArgb(85, 55, 74, 103)), 1);
        }

        private static string BuildScriptApiUrl(int page, string query)
        {
            string safeQuery = Uri.EscapeDataString(query ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeQuery))
            {
                return $"https://rscripts.net/api/v2/scripts?page={page}&orderBy=date&sort=desc";
            }

            return $"https://rscripts.net/api/v2/scripts?page={page}&orderBy=date&sort=desc&q={safeQuery}";
        }

        private static string ExtractImageUrl(JsonElement script)
        {
            if (!script.TryGetProperty("image", out var imageProp))
            {
                return string.Empty;
            }

            if (imageProp.ValueKind == JsonValueKind.Object && imageProp.TryGetProperty("url", out var urlProp))
            {
                return urlProp.GetString() ?? string.Empty;
            }

            if (imageProp.ValueKind == JsonValueKind.String)
            {
                string? val = imageProp.GetString();
                return val ?? string.Empty;
            }

            return string.Empty;
        }

        private static JsonElement? FindPropertyIgnoreCase(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value;
                }
            }

            return null;
        }

        private static bool? ReadFlexibleBool(JsonElement element, params string[] candidateNames)
        {
            foreach (string name in candidateNames)
            {
                JsonElement? match = FindPropertyIgnoreCase(element, name);
                if (match is not JsonElement value)
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;

                if (value.ValueKind == JsonValueKind.Number)
                {
                    if (value.TryGetInt32(out int intVal))
                    {
                        return intVal != 0;
                    }

                    if (value.TryGetDouble(out double doubleVal))
                    {
                        return Math.Abs(doubleVal) > double.Epsilon;
                    }
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    string str = value.GetString() ?? string.Empty;
                    if (bool.TryParse(str, out bool boolVal))
                    {
                        return boolVal;
                    }

                    if (int.TryParse(str, out int intVal))
                    {
                        return intVal != 0;
                    }

                    if (str.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
                    if (str.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
                }
            }

            return null;
        }

        private static string ReadFlexibleString(JsonElement element, params string[] candidateNames)
        {
            foreach (string name in candidateNames)
            {
                JsonElement? match = FindPropertyIgnoreCase(element, name);
                if (match is not JsonElement value) continue;
                if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
                if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                {
                    return value.ToString();
                }
            }

            return string.Empty;
        }

        private async Task<bool> FetchScriptHubPageAsync(int page, string query, bool clearExisting)
        {
            try
            {
                if (_isLoadingScripts) return false;
                _isLoadingScripts = true;
                ScriptHubLoadingText.Text = "Loading scripts...";

                if (clearExisting)
                {
                    ScriptsList.Clear();
                    _loadedScriptUrls.Clear();
                }

                string url = BuildScriptApiUrl(page, query);
                string json = await _httpClient.GetStringAsync(url);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("scripts", out JsonElement scriptsArray) || scriptsArray.ValueKind != JsonValueKind.Array)
                {
                    _hasMoreScripts = false;
                    ScriptHubLoadingText.Text = ScriptsList.Count == 0 ? "No scripts found." : "No more scripts.";
                    return false;
                }

                int added = 0;
                foreach (JsonElement script in scriptsArray.EnumerateArray())
                {
                    string title = script.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Untitled" : "Untitled";
                    string rawUrl = script.TryGetProperty("rawScript", out var rawProp) ? rawProp.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(rawUrl) || !_loadedScriptUrls.Add(rawUrl))
                    {
                        continue;
                    }

                    string author = "Unknown";
                    bool userVerified = false;
                    if (script.TryGetProperty("user", out var userObj) &&
                        userObj.ValueKind == JsonValueKind.Object &&
                        userObj.TryGetProperty("username", out var usernameProp))
                    {
                        author = usernameProp.GetString() ?? "Unknown";
                        userVerified = ReadFlexibleBool(userObj, "verified", "isVerified", "is_verified") ?? false;
                    }

                    string viewsText = script.TryGetProperty("views", out var viewsProp) ? viewsProp.ToString() : "0";
                    string likesText = script.TryGetProperty("likes", out var likesProp) ? likesProp.ToString() : "0";
                    string description = ReadFlexibleString(script, "description", "scriptDescription", "desc", "summary", "about");
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        description = "No description was provided for this script.";
                    }
                    bool requiresKey = ReadFlexibleBool(script, "key", "requiresKey", "keyRequired", "keySystem", "hasKeySystem", "is_key_system") ?? false;
                    bool isPaid = ReadFlexibleBool(script, "paid", "isPaid", "premium", "isPremium", "is_paid") ?? false;
                    bool scriptVerified = ReadFlexibleBool(script, "verified", "isVerified", "is_verified", "trusted", "isTrusted") ?? false;
                    bool isVerified = scriptVerified || userVerified;
                    ScriptsList.Add(new ScriptItem
                    {
                        Name = title,
                        FullPath = rawUrl,
                        IsRemote = true,
                        Meta = $"{author} • {viewsText} views • {likesText} likes",
                        Description = description,
                        ImageUrl = ExtractImageUrl(script),
                        KeyBadge = requiresKey ? "Key Required" : "Keyless",
                        AccessBadge = isPaid ? "Paid" : "Free",
                        TrustBadge = isVerified ? "Verified" : "Community"
                    });
                    added++;
                }

                _hasMoreScripts = scriptsArray.GetArrayLength() > 0;
                if (added == 0 && page == 1)
                {
                    ScriptHubLoadingText.Text = "No scripts found.";
                }
                else if (!_hasMoreScripts || scriptsArray.GetArrayLength() == 0)
                {
                    ScriptHubLoadingText.Text = "No more scripts.";
                }
                else
                {
                    ScriptHubLoadingText.Text = string.Empty;
                }

                return added > 0;
            }
            catch (Exception ex)
            {
                App.LogException(ex, "FetchScriptHubPageAsync");
                ScriptHubLoadingText.Text = "Failed to fetch scripts.";
                if (ScriptsList.Count == 0)
                {
                    LoadScriptsFolder();
                }
                ShowNotification("Failed to reach Rscripts API", true);
                return false;
            }
            finally
            {
                _isLoadingScripts = false;
            }
        }

        private async Task StartScriptHubQueryAsync(string query)
        {
            _currentScriptQuery = query.Trim();
            _currentScriptPage = 1;
            _hasMoreScripts = true;
            await FetchScriptHubPageAsync(_currentScriptPage, _currentScriptQuery, true);
        }

        private async Task LoadNextScriptHubPageAsync()
        {
            if (_isLoadingScripts || !_hasMoreScripts) return;
            _currentScriptPage++;
            bool added = await FetchScriptHubPageAsync(_currentScriptPage, _currentScriptQuery, false);
            if (!added)
            {
                _hasMoreScripts = false;
            }
        }

        private async void RefreshScriptHub_Click(object sender, RoutedEventArgs e)
        {
            await StartScriptHubQueryAsync(ScriptHubSearchBox?.Text ?? string.Empty);
            ShowNotification("ScriptHub refreshed", false);
        }

        private void ScriptSearchDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _scriptSearchDebounceTimer.Stop();
            _ = StartScriptHubQueryAsync(ScriptHubSearchBox?.Text ?? string.Empty);
        }

        private void ScriptHubSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _scriptSearchDebounceTimer.Stop();
            _scriptSearchDebounceTimer.Start();
        }

        private async void ScriptHubScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange <= 0) return;
            if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 220)
            {
                await LoadNextScriptHubPageAsync();
            }
        }

        private async System.Threading.Tasks.Task<string?> LoadScriptContentAsync(ScriptItem item)
        {
            if (item.IsRemote)
            {
                return await _httpClient.GetStringAsync(item.FullPath);
            }

            if (File.Exists(item.FullPath))
            {
                return await File.ReadAllTextAsync(item.FullPath);
            }

            return null;
        }

        private static string BuildSnippet(string source, int maxChars = 1200)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return "No preview available.";
            }

            string cleaned = source.Replace("\r\n", "\n").Trim();
            if (cleaned.Length <= maxChars) return cleaned;
            return cleaned[..maxChars] + "\n\n... (truncated)";
        }

        private static bool ValidateNativeDll(string dllPath, out string error)
        {
            error = string.Empty;

            if (!File.Exists(dllPath))
            {
                error = $"Missing native module: {dllPath}";
                return false;
            }

            try
            {
                using FileStream fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < 64)
                {
                    error = $"Native module is too small ({fs.Length} bytes): {dllPath}";
                    return false;
                }

                Span<byte> mz = stackalloc byte[2];
                if (fs.Read(mz) != 2 || mz[0] != (byte)'M' || mz[1] != (byte)'Z')
                {
                    error = $"Native module is not a valid PE file (missing MZ header): {dllPath}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Failed to validate native module at {dllPath}: {ex.Message}";
                return false;
            }

            return true;
        }

        // Window controls
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }

            DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // Tabs
        private void Tab_Editor(object sender, RoutedEventArgs e) => SwitchTab(ViewEditor);
        private void Tab_ScriptHub(object sender, RoutedEventArgs e) => SwitchTab(ViewScriptHub);
        private void Tab_Hub(object sender, RoutedEventArgs e)
        {
            SwitchTab(ViewHub);
            if (!_isAttached)
            {
                ShowNotification("Attach to Roblox first to use Hub tools", true);
            }
        }
        private void Tab_Clients(object sender, RoutedEventArgs e) => SwitchTab(ViewClients);
        private void Tab_Settings(object sender, RoutedEventArgs e) => SwitchTab(ViewSettings);

        private void SwitchTab(Grid target)
        {
            if (ViewEditor == null) return; // Prevent init crash
            ViewEditor.Visibility = Visibility.Collapsed;
            ViewScriptHub.Visibility = Visibility.Collapsed;
            ViewHub.Visibility = Visibility.Collapsed;
            ViewClients.Visibility = Visibility.Collapsed;
            ViewSettings.Visibility = Visibility.Collapsed;

            target.Visibility = Visibility.Visible;
            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            target.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            if (ReferenceEquals(target, ViewScriptHub))
            {
                MaybeAutoRefreshScriptHub();
            }
        }

        private void UpdateHubAvailability()
        {
            if (HubToolsCard == null || HubLockedCard == null) return;
            HubToolsCard.IsEnabled = _isAttached;
            HubLockedCard.Visibility = _isAttached ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateAttachStateUi()
        {
            if (ExecuteMainButton != null)
            {
                ExecuteMainButton.IsEnabled = _isAttached;
                ExecuteMainButton.Opacity = _isAttached ? 1 : 0.65;
                ExecuteMainButton.ToolTip = _isAttached ? "Execute script" : "Attach to Roblox to execute";
            }

            if (KillMainButton != null)
            {
                KillMainButton.IsEnabled = _isAttached;
                KillMainButton.Opacity = _isAttached ? 1 : 0.65;
                KillMainButton.ToolTip = _isAttached ? "Kill Roblox process" : "No attached Roblox session";
            }

            if (AttachMainButton != null)
            {
                AttachMainButton.IsEnabled = !_isAttached;
                AttachMainButton.Opacity = _isAttached ? 0.65 : 1;
                AttachMainButton.ToolTip = _isAttached ? "Already attached" : "Attach to Roblox";
            }

            UpdateHubAvailability();
        }

        private bool ExecuteHubToggleScript(string script, string featureLabel)
        {
            if (!_isAttached)
            {
                ShowNotification("Attach to Roblox first", true);
                return false;
            }

            string runtimeScript = PrepareScriptForExecution(script);
            int result = -1;
            if (result == 0) return true;

            string error = RblxCore.GetLastError();
            ShowNotification($"{featureLabel} failed: {error}", true);
            return false;
        }

        private static string PrepareScriptForExecution(string script)
        {
            // Leave source untouched here; HttpGet compatibility is handled in native executor hooks.
            return script;
        }

        private void TryCleanupHubRuntime()
        {
            if (!_isAttached)
            {
                return;
            }

            const string cleanupScript = "print(\"Public build: runtime disabled\")";
            _ = cleanupScript;
        }

        private async Task TryCleanupHubRuntimeAsync(int timeoutMs = 1200)
        {
            if (!_isAttached)
            {
                return;
            }

            try
            {
                var cleanupTask = Task.Run(() => TryCleanupHubRuntime());
                await Task.WhenAny(cleanupTask, Task.Delay(timeoutMs));
            }
            catch
            {
                // Non-critical best-effort cleanup only.
            }
        }

        private void SetHubToggleState(ToggleButton toggle, bool value)
        {
            _isUpdatingHubToggles = true;
            try
            {
                toggle.IsChecked = value;
            }
            finally
            {
                _isUpdatingHubToggles = false;
            }
        }

        private void HubNoclip_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "Noclip"))
            {
                ShowNotification("Noclip enabled", false);
            }
            else
            {
                SetHubToggleState(HubNoclipToggle, false);
            }
        }

        private void HubNoclip_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "Noclip"))
            {
                ShowNotification("Noclip disabled", false);
            }
            else
            {
                SetHubToggleState(HubNoclipToggle, true);
            }
        }

        private void HubFly_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "Fly"))
            {
                ShowNotification("Fly enabled", false);
            }
            else
            {
                SetHubToggleState(HubFlyToggle, false);
            }
        }

        private void HubFly_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "Fly"))
            {
                ShowNotification("Fly disabled", false);
            }
            else
            {
                SetHubToggleState(HubFlyToggle, true);
            }
        }

        private void HubInfiniteJump_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "Infinite Jump"))
            {
                ShowNotification("Infinite jump enabled", false);
            }
            else
            {
                SetHubToggleState(HubInfiniteJumpToggle, false);
            }
        }

        private void HubInfiniteJump_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "Infinite Jump"))
            {
                ShowNotification("Infinite jump disabled", false);
            }
            else
            {
                SetHubToggleState(HubInfiniteJumpToggle, true);
            }
        }

        private void HubSpeed_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "Speed")) ShowNotification("Speed enabled", false);
            else SetHubToggleState(HubSpeedToggle, false);
        }

        private void HubSpeed_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "Speed")) ShowNotification("Speed disabled", false);
            else SetHubToggleState(HubSpeedToggle, true);
        }

        private void HubAntiAfk_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "Anti-AFK")) ShowNotification("Anti-AFK enabled", false);
            else SetHubToggleState(HubAntiAfkToggle, false);
        }

        private void HubAntiAfk_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "Anti-AFK")) ShowNotification("Anti-AFK disabled", false);
            else SetHubToggleState(HubAntiAfkToggle, true);
        }

        private void HubGodMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "God Mode")) ShowNotification("God Mode enabled", false);
            else SetHubToggleState(HubGodModeToggle, false);
        }

        private void HubGodMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "God Mode")) ShowNotification("God Mode disabled", false);
            else SetHubToggleState(HubGodModeToggle, true);
        }

        private void HubReset_Click(object sender, RoutedEventArgs e)
        {
            const string script = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(script, "Reset")) ShowNotification("Character reset", false);
        }

        private void HubFullbright_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "Fullbright")) ShowNotification("Fullbright enabled", false);
            else SetHubToggleState(HubFullbrightToggle, false);
        }

        private void HubFullbright_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "Fullbright")) ShowNotification("Fullbright disabled", false);
            else SetHubToggleState(HubFullbrightToggle, true);
        }

        private void HubEsp_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "ESP")) ShowNotification("ESP enabled", false);
            else SetHubToggleState(HubEspToggle, false);
        }

        private void HubEsp_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "ESP")) ShowNotification("ESP disabled", false);
            else SetHubToggleState(HubEspToggle, true);
        }

        private void HubClickTp_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string onScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(onScript, "Click TP")) ShowNotification("Click TP enabled (hold LeftAlt + click)", false);
            else SetHubToggleState(HubClickTpToggle, false);
        }

        private void HubClickTp_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHubToggles) return;
            const string offScript = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(offScript, "Click TP")) ShowNotification("Click TP disabled", false);
            else SetHubToggleState(HubClickTpToggle, true);
        }

        private void MaybeAutoRefreshScriptHub()
        {
            if (!_uiSettings.AutoRefreshScriptHubOnOpen) return;
            if (DateTime.UtcNow - _lastScriptHubRefreshUtc < TimeSpan.FromSeconds(45)) return;
            _lastScriptHubRefreshUtc = DateTime.UtcNow;
            _ = StartScriptHubQueryAsync(ScriptHubSearchBox?.Text ?? string.Empty);
        }

        // Notification System
        private void ShowNotification(string message, bool isError = false)
        {
            if (!_uiSettings.ShowNotifications) return;
            NotificationText.Text = message;
            
            if (isError)
            {
                NotificationBox.BorderBrush = Brushes.OrangeRed;
                NotificationIcon.Data = Geometry.Parse("M11.9998 9.00006V12.7501M2.69653 16.1257C1.83114 17.6257 2.91371 19.5001 4.64544 19.5001H19.3541C21.0858 19.5001 22.1684 17.6257 21.303 16.1257L13.9487 3.37819C13.0828 1.87736 10.9167 1.87736 10.0509 3.37819L2.69653 16.1257ZM11.9998 15.7501H12.0073V15.7576H11.9998V15.7501Z");
                NotificationIcon.Stroke = Brushes.OrangeRed;
            }
            else
            {
                NotificationBox.BorderBrush = new SolidColorBrush(Color.FromRgb(197, 165, 111)); // Gold accent
                NotificationIcon.Data = Geometry.Parse("M9 12.75L11.25 15L15 9.75M21 12C21 16.9706 16.9706 21 12 21C7.02944 21 3 16.9706 3 12C3 7.02944 7.02944 3 12 3C16.9706 3 21 7.02944 21 12Z");
                NotificationIcon.Stroke = new SolidColorBrush(Color.FromRgb(197, 165, 111));
            }

            NotificationBox.Visibility = Visibility.Visible;
            
            // Slide in animation
            TranslateTransform transform = new TranslateTransform(50, 0);
            NotificationBox.RenderTransform = transform;
            
            DoubleAnimation slideIn = new DoubleAnimation(50, 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new QuarticEase() { EasingMode = EasingMode.EaseOut }
            };
            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));

            transform.BeginAnimation(TranslateTransform.XProperty, slideIn);
            NotificationBox.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            _notificationTimer.Stop();
            _notificationTimer.Start();
        }

        private void CloseNotification()
        {
            _notificationTimer.Stop();
            
            TranslateTransform transform = new TranslateTransform(0, 0);
            NotificationBox.RenderTransform = transform;
            
            DoubleAnimation slideOut = new DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(200));
            DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            
            fadeOut.Completed += (s, e) => NotificationBox.Visibility = Visibility.Collapsed;

            transform.BeginAnimation(TranslateTransform.XProperty, slideOut);
            NotificationBox.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            if (!_isAttached)
            {
                ShowNotification("Attach to Roblox first", true);
                return;
            }

            if (_isExecuting) return;
            _isExecuting = true;
            try
            {
                string script = Editor.Text;
                if (string.IsNullOrWhiteSpace(script))
                {
                    ShowNotification("No script to execute", true);
                    _isExecuting = false;
                    return;
                }

                Console.WriteLine($"[C# UI] Executing EDITOR script ({script.Length} chars)...");
                ShowNotification("Executing Script...", false);

                string runtimeScript = PrepareScriptForExecution(script);
                int result = -1;
                e.Handled = true;

                if (result == 0)
                {
                    ShowNotification("Executed Successfully!", false);
                }
                else
                {
                    string error = RblxCore.GetLastError();
                    ShowNotification($"Error: {error}", true);
                }
            }
            catch (Exception ex)
            {
                ShowNotification("Execution Exception", true);
                App.LogException(ex, "Execute_Click");
            }
            finally
            {
                _isExecuting = false;
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Editor.Text = "";
            ShowNotification("Editor Cleared", false);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
                if (!Directory.Exists(scriptPath)) Directory.CreateDirectory(scriptPath);

                var dialog = new Microsoft.Win32.SaveFileDialog()
                {
                    InitialDirectory = scriptPath,
                    Filter = "Lua Scripts (*.lua;*.luau;*.txt)|*.lua;*.luau;*.txt|All Files (*.*)|*.*",
                    DefaultExt = "lua"
                };

                if (dialog.ShowDialog() == true)
                {
                    File.WriteAllText(dialog.FileName, Editor.Text);
                    if (SelectedEditorTab != null)
                    {
                        SelectedEditorTab.Title = Path.GetFileName(dialog.FileName);
                        SelectedEditorTab.Content = Editor.Text;
                    }
                    ShowNotification("Script Saved Successfully", false);
                }
            }
            catch (Exception)
            {
                ShowNotification("Failed to save file", true);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
                if (!Directory.Exists(scriptPath)) Directory.CreateDirectory(scriptPath);

                var dialog = new Microsoft.Win32.OpenFileDialog()
                {
                    InitialDirectory = scriptPath,
                    Filter = "Lua Scripts (*.lua;*.luau;*.txt)|*.lua;*.luau;*.txt|All Files (*.*)|*.*",
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                string content = File.ReadAllText(dialog.FileName);
                var newTab = new EditorTabItem
                {
                    Title = Path.GetFileName(dialog.FileName),
                    Content = content
                };

                EditorTabs.Add(newTab);
                EditorTabsList.SelectedItem = newTab;
                ShowNotification($"Opened {newTab.Title}", false);
            }
            catch (Exception)
            {
                ShowNotification("Failed to open script", true);
            }
        }

        private async void ExecuteScriptHub_Click(object sender, RoutedEventArgs e)
        {
            if (!_isAttached)
            {
                ShowNotification("Attach to Roblox first", true);
                return;
            }

            if (_isExecuting) return;
            _isExecuting = true;
            try 
            {
                if (sender is Button btn && btn.Tag is ScriptItem item)
                {
                    string? content = await LoadScriptContentAsync(item);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        Console.WriteLine($"[C# UI] Executing HUB script: {item.Name} ({content.Length} chars)...");
                        ShowNotification($"Executing {item.Name}...", false);
                        string runtimeScript = PrepareScriptForExecution(content);
                        _ = runtimeScript;
                        e.Handled = true;
                    }
                    else
                    {
                        ShowNotification("Script content unavailable", true);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowNotification("Hub script error!", true);
                App.LogException(ex, "ExecuteScriptHub_Click");
            }
            finally
            {
                _isExecuting = false;
            }
        }

        private async void CopyScriptHub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is ScriptItem item)
                {
                    string? content = await LoadScriptContentAsync(item);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        Clipboard.SetText(content);
                        ShowNotification("Code Copied!", false);
                        e.Handled = true;
                    }
                    else
                    {
                        ShowNotification("Nothing to copy", true);
                    }
                }
            }
            catch (Exception)
            {
                ShowNotification("Copy Failed!", true);
            }
        }

        private async void DeleteScriptHub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is ScriptItem item)
                {
                    string? content = await LoadScriptContentAsync(item);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        ShowNotification("Script unavailable", true);
                        return;
                    }

                    AddEditorTab_Click(this, new RoutedEventArgs());
                    Editor.Text = content;
                    if (SelectedEditorTab != null)
                    {
                        SelectedEditorTab.Title = item.Name.Length > 30 ? item.Name[..30] : item.Name;
                        SelectedEditorTab.Content = content;
                    }
                    Tab_Editor(this, new RoutedEventArgs());
                    ShowNotification($"Loaded {item.Name} into editor", false);
                    e.Handled = true;
                }
            }
            catch (Exception)
            {
                ShowNotification("Load failed!", true);
            }
        }

        private async void ScriptHubCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border || border.Tag is not ScriptItem item) return;
            _activeScriptDetailsItem = item;

            ScriptDetailsTitleText.Text = item.Name;
            ScriptDetailsMetaText.Text = item.Meta;
            ScriptDetailsBadgesText.Text = $"{item.KeyBadge} • {item.AccessBadge} • {item.TrustBadge}";
            ScriptDetailsDescriptionText.Text = item.Description;
            ScriptDetailsPreviewText.Text = "Loading script preview...";
            ScriptDetailsOverlay.Visibility = Visibility.Visible;

            try
            {
                string? content = await LoadScriptContentAsync(item);
                if (!ReferenceEquals(_activeScriptDetailsItem, item)) return;
                ScriptDetailsPreviewText.Text = content is null ? "Unable to load script content." : BuildSnippet(content);
            }
            catch
            {
                if (!ReferenceEquals(_activeScriptDetailsItem, item)) return;
                ScriptDetailsPreviewText.Text = "Failed to load script content preview.";
            }
        }

        private void CloseScriptDetails_Click(object sender, RoutedEventArgs e)
        {
            _activeScriptDetailsItem = null;
            ScriptDetailsOverlay.Visibility = Visibility.Collapsed;
        }

        private async void ScriptDetailsExecute_Click(object sender, RoutedEventArgs e)
        {
            if (!_isAttached)
            {
                ShowNotification("Attach to Roblox first", true);
                return;
            }

            if (_activeScriptDetailsItem == null) return;
            if (_isExecuting) return;
            _isExecuting = true;
            try
            {
                string? content = await LoadScriptContentAsync(_activeScriptDetailsItem);
                if (string.IsNullOrWhiteSpace(content))
                {
                    ShowNotification("Script content unavailable", true);
                    return;
                }

                ShowNotification($"Executing {_activeScriptDetailsItem.Name}...", false);
                string runtimeScript = PrepareScriptForExecution(content);
                int result = -1;
                if (result != 0)
                {
                    ShowNotification($"Error: {RblxCore.GetLastError()}", true);
                }
            }
            finally
            {
                _isExecuting = false;
            }
        }

        private async void ScriptDetailsCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_activeScriptDetailsItem == null) return;
            string? content = await LoadScriptContentAsync(_activeScriptDetailsItem);
            if (string.IsNullOrWhiteSpace(content))
            {
                ShowNotification("Nothing to copy", true);
                return;
            }

            Clipboard.SetText(content);
            ShowNotification("Code Copied!", false);
        }

        private async void ScriptDetailsLoad_Click(object sender, RoutedEventArgs e)
        {
            if (_activeScriptDetailsItem == null) return;
            string? content = await LoadScriptContentAsync(_activeScriptDetailsItem);
            if (string.IsNullOrWhiteSpace(content))
            {
                ShowNotification("Script unavailable", true);
                return;
            }

            var newTab = new EditorTabItem
            {
                Title = _activeScriptDetailsItem.Name.Length > 40 ? _activeScriptDetailsItem.Name[..40] : _activeScriptDetailsItem.Name,
                Content = content
            };

            EditorTabs.Add(newTab);
            EditorTabsList.SelectedItem = newTab;
            Tab_Editor(this, new RoutedEventArgs());
            ShowNotification($"Loaded {_activeScriptDetailsItem.Name} into editor", false);
        }

        private void HubInfiniteYield_Click(object sender, RoutedEventArgs e)
        {
            const string script = "print(\"Public build: runtime disabled\")";
            if (ExecuteHubToggleScript(script, "Infinite Yield"))
            {
                ShowNotification("Infinite Yield executed", false);
            }
        }

        private void KillRoblox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_uiSettings.ConfirmBeforeKill)
                {
                    var confirm = MessageBox.Show(
                        "Force close all Roblox client processes?",
                        "Confirm Kill",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                var procs = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
                _ = TryCleanupHubRuntimeAsync(600);
                foreach (var p in procs) { p.Kill(); }
                ShowNotification("Roblox Terminated", false);
            }
            catch { ShowNotification("No Roblox to Kill", true); }
        }

        private void StartProcessMonitor(uint pid)
        {
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.Tick += (s, ev) => 
            {
                try {
                    var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (proc.HasExited) throw new Exception();
                } catch {
                    timer.Stop();
                    _isAttached = false;
                    SetHubToggleState(HubNoclipToggle, false);
                    SetHubToggleState(HubFlyToggle, false);
                    SetHubToggleState(HubInfiniteJumpToggle, false);
                    SetHubToggleState(HubSpeedToggle, false);
                    SetHubToggleState(HubAntiAfkToggle, false);
                    SetHubToggleState(HubGodModeToggle, false);
                    SetHubToggleState(HubFullbrightToggle, false);
                    SetHubToggleState(HubEspToggle, false);
                    SetHubToggleState(HubClickTpToggle, false);
                    UpdateAttachStateUi();
                    // Reset UI
                    InjectionStatus.Text = "NOT INJECTED";
                    InjectionStatus.Foreground = Brushes.Red;
                    ClientPidText.Text = "PID: None | Place: None";
                    ClientStatusBadge.Text = "READY";
                    ClientStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(197, 165, 111));
                    ClientBadgeBg.Background = new SolidColorBrush(Color.FromArgb(35, 197, 165, 111));
                    ClientAccountName.Text = "Not Connected";
                    ClientAvatarImage.Source = null;
                    RblxCore.Disconnect();
                }
            };
            timer.Start();
        }

        private async Task<bool> AttemptAttachAsync(bool manualTrigger)
        {
            if (_isAttached || _isAutoAttachAttemptRunning)
            {
                return _isAttached;
            }

            _isAutoAttachAttemptRunning = true;
            try
            {
                if (manualTrigger)
                {
                    ShowNotification("Attaching to Roblox...", false);
                }

                uint pid = 0;
                bool connected = false;

                await Task.Run(() =>
                {
                    pid = RblxCore.FindRobloxProcess();
                    if (pid != 0)
                    {
                        connected = RblxCore.Connect(pid);
                    }
                });

                if (pid == 0)
                {
                    _isAttached = false;
                    UpdateAttachStateUi();
                    if (manualTrigger) ShowNotification("Roblox not found!", true);
                    return false;
                }

                if (!connected)
                {
                    _isAttached = false;
                    UpdateAttachStateUi();
                    if (manualTrigger)
                    {
                        ShowNotification("Connect failed!", true);
                        InjectionStatus.Text = "FAILED";
                        InjectionStatus.Foreground = Brushes.Red;
                    }
                    return false;
                }

                _isAttached = true;
                UpdateAttachStateUi();
                _ = TryCleanupHubRuntimeAsync();
                if (manualTrigger || _uiSettings.AutoAttach)
                {
                    ShowNotification("Successfully attached to game!", false);
                }
                InjectionStatus.Text = "STABLE";
                InjectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(197, 165, 111));

                ClientPidText.Text = $"PID: {pid} | Place: Active";
                ClientStatusBadge.Text = "ACTIVE SESSION";
                ClientStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(197, 165, 111));
                ClientBadgeBg.Background = new SolidColorBrush(Color.FromArgb(40, 197, 165, 111));

                StartClientDataPoller(pid);
                StartProcessMonitor(pid);
                return true;
            }
            catch (Exception ex)
            {
                _isAttached = false;
                UpdateAttachStateUi();
                if (manualTrigger)
                {
                    ShowNotification("Attach Exception", true);
                }
                App.LogException(ex, "AttemptAttachAsync");
                return false;
            }
            finally
            {
                _isAutoAttachAttemptRunning = false;
            }
        }

        private void StartClientDataPoller(uint pid)
        {
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            int attempts = 0;
            
            timer.Tick += (s, ev) => 
            {
                attempts++;
                if (attempts > 15) {
                    timer.Stop();
                    return; // Give up after 30 seconds
                }

                var sb = new System.Text.StringBuilder(512);
                if (RblxCore.GetClientInfo(sb, sb.Capacity))
                {
                    string body = sb.ToString();
                    var parts = body.Split('|');
                    if (parts.Length >= 4 && parts[0] != "Unknown") // Now expects 4 parts (Name, UserId, JobId, PlaceId)
                    {
                        timer.Stop(); // Data found, stop polling
                        
                        string accountName = parts[0];
                        string userId = parts[1];
                        string jobId = parts[2];
                        string placeId = parts[3];

                        // Run the web requests asynchronously so UI doesn't freeze
                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            string finalPlaceName = $"Place: {placeId}";
                            try
                            {
                                using (var client = new System.Net.Http.HttpClient())
                                {
                                    client.DefaultRequestHeaders.Add("User-Agent", "Roblox/WinInet");
                                    
                                    // 1. Resolve PlaceId -> Game Name
                                    if (placeId != "0")
                                    {
                                        string apiRes = await client.GetStringAsync($"https://economy.roblox.com/v2/assets/{placeId}/details");
                                        var match = System.Text.RegularExpressions.Regex.Match(apiRes, "\"Name\"\\s*:\\s*\"([^\"]+)\"");
                                        if (match.Success) finalPlaceName = match.Groups[1].Value;
                                    }

                                    // 2. Resolve UserId -> Avatar JSON -> Actual Image URL -> Download Stream
                                    string avatarMetaUrl = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
                                    string metaRes = await client.GetStringAsync(avatarMetaUrl);
                                    string actualImgUrl = "";
                                    
                                    var imgMatch = System.Text.RegularExpressions.Regex.Match(metaRes, "\"imageUrl\"\\s*:\\s*\"([^\"]+)\"");
                                    if (imgMatch.Success) actualImgUrl = imgMatch.Groups[1].Value;

                                    byte[] imgBytes = null;
                                    if (!string.IsNullOrEmpty(actualImgUrl))
                                    {
                                        imgBytes = await client.GetByteArrayAsync(actualImgUrl);
                                    }

                                    Dispatcher.Invoke(() =>
                                    {
                                        ClientAccountName.Text = accountName;
                                        ClientPidText.Text = $"PID: {pid} | {finalPlaceName}";

                                        if (imgBytes != null)
                                        {
                                            try
                                            {
                                                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                                                bmp.BeginInit();
                                                bmp.StreamSource = new System.IO.MemoryStream(imgBytes);
                                                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                                bmp.EndInit();
                                                ClientAvatarImage.Source = bmp;
                                            }
                                            catch { }
                                        }
                                    });
                                }
                            }
                            catch
                            {
                                // Fallback if web request fails
                                Dispatcher.Invoke(() =>
                                {
                                    ClientAccountName.Text = accountName;
                                    ClientPidText.Text = $"PID: {pid} | Place: {placeId}";
                                });
                            }
                        });
                    }
                }
            };
            timer.Start();
        }

        private async void Attach_Click(object sender, RoutedEventArgs e)
        {
            await AttemptAttachAsync(true);
        }

        private void ToggleConsole_Checked(object sender, RoutedEventArgs e)
        {
            _uiSettings.DebugConsole = true;
            var handle = GetConsoleWindow();
            if (handle == IntPtr.Zero)
            {
                AllocConsole();
                RblxCore.RedirConsole(); // Sync C++ stdout to new console
                Console.WriteLine("[C# UI] Console allocated on user request.");
                handle = GetConsoleWindow();
            }
            
            if (handle != IntPtr.Zero) ShowWindow(handle, SW_SHOW);
            if (IsLoaded)
            {
                SaveUiSettings();
            }
        }

        private void ToggleConsole_Unchecked(object sender, RoutedEventArgs e)
        {
            _uiSettings.DebugConsole = false;
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero) ShowWindow(handle, SW_HIDE);
            if (IsLoaded)
            {
                SaveUiSettings();
            }
        }
    }
}



