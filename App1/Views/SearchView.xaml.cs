using Anfeta.UI.Models;
using Anfeta.UI.Models.Search;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Bookmarks;
using Anfeta.UI.Services.Dropbox;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Speech;
using Anfeta.UI.Services.VoiceCommands;
using Anfeta.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using static Anfeta.UI.Helpers.AppSettingsKeys;


namespace Anfeta.UI.Views
{
    public sealed partial class SearchView : Page, ISearchCommandSink
    {
        #region ===== Fields / Const / Enums =====

        // enums
        private enum ViewMode { Explorer, Bookmarks }
        private enum SearchSourceScope { All, Notion, Dropbox }

        private ViewMode _mode = ViewMode.Explorer;
        private SearchSourceScope _activeSourceScope = SearchSourceScope.All;
        private bool _isUpdatingFilterCombo;

        // Win32 file attributes (Dropbox / OneDrive placeholders)
        private const int FILE_ATTRIBUTE_OFFLINE = 0x00001000;
        private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
        private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

        // state
        private string DROPBOX_ROOT = "";
        private string _currentFolder = "";
        private bool _isBrowsing = false;
        private bool _onlyBookmarks = false;
        private bool _onlyFolders = false;
        private string? _extFilter = null;
        private string _sortKey = "name_asc";

        // debounce / tokens
        private DispatcherTimer? _searchDebounceTimer;
        private CancellationTokenSource? _searchCts;
        private CancellationTokenSource? _cts;

        // UI / help popup
        private ContentControl? _helpBodyHost;

        // icons / bookmarks
        private readonly ShellIconService _iconService = new();
        private readonly BookmarksService _bookmarksService = new();
        private List<BookmarkItem> _bookmarks = new();

        // collections
        public ObservableCollection<SearchResultRow> Results { get; } = new();
        private readonly ObservableCollection<SearchResultGroup> _resultGroups = new();
        private ObservableCollection<FolderNode> _treeRoots = new();
        private readonly Stack<string> _backStack = new();
        private readonly Stack<string> _forwardStack = new();

        // highlight
        private List<string> _highlightTerms = new();

        // extras
        private bool _allowProgrammaticSearch = false;
        private bool _foldersPaneVisible = true;

        // exclusion
        private const string LS_ExcludedFolders = "ExcludedFolders";
        private readonly List<string> _excludedFolders = new();
        private readonly ObservableCollection<string> _excludedFoldersUi = new();

        // saved searches / commands
        private const string LS_SavedSearches = "SavedSearches";
        private const string LS_CommandsExpanded = "CommandsExpanded";
        private const string LS_ExcludedExpanded = "ExcludedExpanded";

        // auto-reindex
        private CancellationTokenSource? _autoReindexCts;

        // voz — _voiceRepo eliminado: era dead code, nunca fue asignado ni usado.
        //        La DI resuelve VoiceCommandsRepository en _repo.
        private readonly VoiceCommandEngine _voiceEngine;
        private readonly VoiceSearchOrchestrator _voiceOrchestrator;
        private bool _isListening = false;
        private CancellationTokenSource? _voiceCts;
        private readonly ISpeechToTextService _stt;
        private readonly VoiceCommandsRepository _repo;
        private bool _voiceInitDone;
        private readonly IVoicePostActionService _voicePost;
        private Brush? _voiceSplitDefaultBg;
        private Brush? _voiceSplitDefaultFg;
        private readonly Brush _voiceActiveBg = new SolidColorBrush(Color.FromArgb(255, 60, 20, 20));
        private readonly Brush _voiceActiveFg = new SolidColorBrush(Colors.White);

        // acciones / file ops
        private readonly DropboxPathMapper _dropboxPathMapper;
        private readonly DropboxFileService _dropboxFileService;
        private readonly DropboxSyncService _dropboxSyncService;
        private readonly SemaphoreSlim _bootstrapLock = new(1, 1);
        private bool _bootstrappedOnce = false;
        private readonly SemaphoreSlim _mutLock = new(1, 1);
        private CancellationTokenSource? _refreshCts;
        private string? _currentFolderPath;

        // pestañas
        public event EventHandler<string>? TabTitleChanged;
        public event EventHandler? WorkspaceChanged;

        // dictado
        private readonly SpeechSynthesizer _dictSynth = new();
        private MediaPlayer? _dictPlayer;
        private IReadOnlyList<SearchResultRow> _dictList = Array.Empty<SearchResultRow>();
        private int _dictIndex = 0;
        private bool _dictPlaying = false;
        private CancellationTokenSource? _dictCts;

        // sugerencias
        private bool _suppressSuggest = false;
        private bool _useExpandedQueryOnSubmit = false;

        // batch rename
        private const string LS_BATCH_RENAME_HISTORY = "BatchRename.FormatHistory.v1";
        private const int BATCH_RENAME_HISTORY_MAX = 8;
        private bool _isIndexStateHooked;

        // filtros avanzados
        private readonly SavedSearchFiltersRepository _savedFiltersRepository = new();
        private readonly SavedSearchFiltersService _savedFiltersService;
        private readonly ObservableCollection<SavedSearchFilter> _savedFilters = new();
        private QueryMatchOptions _currentMatchOptions = new();

        // importar filtros
        private readonly EverythingCsvFilterImporter _csvFilterImporter = new();
        private readonly FilePickerService _filePickerService = new();
        //colores predefinidos 
        private const string LS_SearchBackgroundTheme = "SearchBackgroundTheme";
        private const string LS_SearchTextScale = "Search.TextScale";
        private const string LS_DefaultSearchTag = "Search.DefaultTag";
        private const string LS_SearchSourceScope = "Search.SourceScope";
        private const string LS_ResultPathColumnWidth = "Search.ResultColumns.Path";
        private const string LS_ResultDateColumnWidth = "Search.ResultColumns.Date";
        private const string LS_ResultStarColumnWidth = "Search.ResultColumns.Star";
        private const double RESULT_PATH_MIN = 70;
        private const double RESULT_PATH_MAX = 420;
        private const double RESULT_DATE_MIN = 90;
        private const double RESULT_DATE_MAX = 280;
        private const double RESULT_STAR_MIN = 32;
        private const double RESULT_STAR_MAX = 90;
        private const string CUSTOM_TAG_VALUE = "__custom__";
        private readonly Dictionary<FrameworkElement, double> _originalFontSizes = new();
        private double _textScale = 1.0;
        private bool _loadingModulePreferences;
        private bool _defaultTagAppliedOnce;
        private string _lastAppliedDefaultTag = string.Empty;
        private DateTime _lastTextScaleVisualPassUtc = DateTime.MinValue;

        // estado visual global de carga
        private int _busyOperationCount;

        // arrastrar archivos hacia Notion
        private bool _isNotionFileDragActive;

        #endregion

        #region ===== Internal Models =====

        private sealed class RowView : AdvancedQueryV3.IItemView
        {
            private readonly SearchResultRow _x;
            public RowView(SearchResultRow x) => _x = x;

            public string? Name => _x.Name;
            public string? Path => _x.Target;
            public string? Folder => System.IO.Path.GetDirectoryName(_x.Target ?? "");
            public string? Extension => System.IO.Path.GetExtension(_x.Target ?? "").TrimStart('.');
            public string? Type => string.Equals(_x.Type, "FOLDER", StringComparison.OrdinalIgnoreCase) ? "folder" : "file";
            public long SizeBytes => _x.Size;
            public DateTime ModifiedLocalDate => ParseServerModified(_x.ServerModified);
            public int DaysModified => ModifiedLocalDate == DateTime.MinValue
                                            ? int.MaxValue
                                            : (int)(DateTime.Now.Date - ModifiedLocalDate.Date).TotalDays;
            public string? SearchText => $"{_x.Name} {_x.Target}";

            private static DateTime ParseServerModified(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
                if (DateTime.TryParse(s,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
                    return dt;
                return DateTime.MinValue;
            }
        }

        private sealed class BookmarkView : AdvancedQueryV3.IItemView
        {
            private readonly BookmarkItem _b;
            public BookmarkView(BookmarkItem b) => _b = b;

            public string? Name => _b.Title;
            public string? Path => _b.LocalPath;
            public string? Folder => System.IO.Path.GetDirectoryName(_b.LocalPath ?? "");
            public string? Extension => System.IO.Path.GetExtension(_b.LocalPath ?? _b.Title ?? "").TrimStart('.');
            public string? Type => string.Equals(_b.Type, "FOLDER", StringComparison.OrdinalIgnoreCase) ? "folder" : "file";
            public long SizeBytes => _b.Size;
            public DateTime ModifiedLocalDate => ParseDate(_b.Modified);
            public int DaysModified => ModifiedLocalDate == DateTime.MinValue
                                            ? int.MaxValue
                                            : (int)(DateTime.Now.Date - ModifiedLocalDate.Date).TotalDays;
            public string? SearchText => $"{_b.Title} {_b.LocalPath}";

            private static DateTime ParseDate(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
                if (DateTime.TryParse(s,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
                    return dt;
                return DateTime.MinValue;
            }
        }

        private sealed class FolderNode
        {
            public string Name { get; set; } = "";
            public string FullPath { get; set; } = "";
            public ObservableCollection<FolderNode> Children { get; } = new();
            public bool HasDummyChild { get; set; } = false;
            public bool IsLoaded { get; set; } = false;
        }
        private sealed class SearchResultGroup : ObservableCollection<SearchResultRow>
        {
            public string Key { get; }

            public string HeaderText => $"{Key} · {Count} resultados";

            public SearchResultGroup(string key, IEnumerable<SearchResultRow> items)
                : base(items)
            {
                Key = key;
            }
        }
        #endregion

        #region ===== Constructor / Lifecycle =====

        public SearchView()
        {
            InitializeComponent();

            _savedFiltersService = new SavedSearchFiltersService(_savedFiltersRepository);

            // La suscripción a StateChanged vive SOLO en SearchView_Loaded bajo _isIndexStateHooked.
            // No se suscribe aquí para evitar handler duplicado y memory leak.

            ResultsList.ItemsSource = Results;
            FolderTree.ItemsSource = new ObservableCollection<FolderNode>();

            Loaded += SearchView_Loaded;
            RootLayout.LayoutUpdated += RootLayout_LayoutUpdated;

            _voiceSplitDefaultBg = VoiceSplit.Background;
            _voiceSplitDefaultFg = VoiceSplit.Foreground;

            var sp = App.AppHost.Services;
            _stt = sp.GetRequiredService<ISpeechToTextService>();
            _repo = sp.GetRequiredService<VoiceCommandsRepository>();
            _voiceEngine = sp.GetRequiredService<VoiceCommandEngine>();
            _voiceOrchestrator = sp.GetRequiredService<VoiceSearchOrchestrator>();
            _voicePost = sp.GetRequiredService<IVoicePostActionService>();

            var dropboxAuth = sp.GetRequiredService<DropboxAuthService>();
            _dropboxPathMapper = new DropboxPathMapper();
            _dropboxFileService = new DropboxFileService(dropboxAuth);
            _dropboxSyncService = new DropboxSyncService(dropboxAuth);

            StatusText.Text = "Estado: Dropbox Local";
            ModeText.Text = "Modo: Buscar";
            CountText.Text = "0 resultados";
            EmptyResultsHint.Visibility = Visibility.Visible;

            _ = LoadBookmarksOnStartAsync();

            async Task LoadBookmarksOnStartAsync()
            {
                _bookmarks = await _bookmarksService.LoadAsync(CancellationToken.None);
            }
        }

        private async void SearchView_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSearchBackgroundTheme();
            LoadModulePreferences();
            LoadResultColumnWidths();
            UpdateColumnSortIndicators();
            // Suscripción única controlada por flag — evita duplicados si Loaded se dispara más de una vez
            if (!_isIndexStateHooked)
            {
                DropboxIndexCoordinator.StateChanged += OnIndexStateChanged;
                SearchFocusBridge.FocusRequested += OnSearchFocusRequested;
                _isIndexStateHooked = true;
            }

            Unloaded -= SearchView_Unloaded;
            Unloaded += SearchView_Unloaded;

            LoadExcludedFolders();
            RefreshExcludedFoldersUi();
            LoadSavedSearches();
            RefreshSavedSearchesUi();
            CommandsSidebarList.ItemsSource = _savedSearches;
            RefreshCommandsSidebarUi();
            LoadSidebarExpandedStates();
            await LoadSavedFiltersAsync();

            if (!_voiceInitDone)
            {
                _voiceInitDone = true;
                await _voiceEngine.ReloadAsync();
            }

            _ = LoadBookmarksAsync();

            await ApplyIndexStateAsync();
            await EnsureIndexBootstrappedAsync();
            await ApplyDefaultTagIfEmptyAsync();
            ApplyTextScaleToVisualTree();

            // Seguridad final del arranque:
            // algunas operaciones iniciales pueden anidarse y dejar pendiente
            // el contador visual aunque la carga ya haya terminado.
            ForceHideLoadingState();
        }

        private void SearchView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_isIndexStateHooked)
            {
                DropboxIndexCoordinator.StateChanged -= OnIndexStateChanged;
                SearchFocusBridge.FocusRequested -= OnSearchFocusRequested;
                _isIndexStateHooked = false;
            }

            StopDropboxChangeWatcher();
        }
        private void OnSearchFocusRequested()
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(150);

                SearchBox.Focus(FocusState.Programmatic);
                SelectTextInsideSearchBox();
            });
        }

        private void SelectTextInsideSearchBox()
        {
            var textBox = FindVisualChild<TextBox>(SearchBox);

            if (textBox != null)
            {
                textBox.Focus(FocusState.Programmatic);
                textBox.SelectAll();
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;

            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild)
                    return typedChild;

                var result = FindVisualChild<T>(child);

                if (result != null)
                    return result;
            }

            return null;
        }
        #endregion

        #region ===== Estado visual de carga =====

        private void ShowLoadingState(
            string status,
            string? detail = null)
        {
            _busyOperationCount++;

            if (StatusText != null && !string.IsNullOrWhiteSpace(status))
                StatusText.Text = status;

            if (LoadingDetailText != null)
            {
                LoadingDetailText.Text = string.IsNullOrWhiteSpace(detail)
                    ? "Espera un momento mientras ANFETA termina la operación."
                    : detail;
            }

            if (LoadingRing != null)
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            }

            if (LoadingOverlay != null)
                LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void UpdateLoadingState(
            string status,
            string? detail = null)
        {
            if (StatusText != null && !string.IsNullOrWhiteSpace(status))
                StatusText.Text = status;

            if (LoadingDetailText != null &&
                !string.IsNullOrWhiteSpace(detail))
            {
                LoadingDetailText.Text = detail;
            }
        }

        private void HideLoadingState()
        {
            // El indicador visual siempre se cierra al terminar la operación
            // actual. Esto evita que un contador desbalanceado por cargas
            // anidadas deje el overlay visible permanentemente.
            _busyOperationCount = 0;

            if (LoadingRing != null)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }

            if (LoadingOverlay != null)
                LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void ForceHideLoadingState()
        {
            _busyOperationCount = 0;

            if (LoadingRing != null)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }

            if (LoadingOverlay != null)
                LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region ===== UI Reset / State =====

        private void ResetSearchModuleState()
        {
            CancelPendingSearch();

            _currentFolder = "";
            _currentFolderPath = "";
            _backStack.Clear();
            _forwardStack.Clear();
            _treeRoots.Clear();

            Results.Clear();
            RefreshResultsListView();

            FolderTree.ItemsSource = new ObservableCollection<FolderNode>();
            EmptyTreeHint.Visibility = Visibility.Visible;

            BreadcrumbText.Text = "/";
            ModeText.Text = "Modo: Explorar (Local)";
            CountText.Text = "0 resultados";
            EmptyResultsHint.Visibility = Visibility.Visible;

            DetailsTitle.Text = "Selecciona un elemento";
            DetailsPath.Text = "-";
            DetailsMeta.Text = "-";
        }

        private void FinishUi()
        {
            ForceHideLoadingState();
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = $"{Results.Count} resultados";
        }

        #endregion
        private void RefreshResultsListView()
        {
            ResultsList.ItemsSource = null;
            ResultsList.ItemsSource = Results;
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyTextScaleToVisualTree();
                ApplyResultColumnWidthsToVisualTree();
            });
        }

        private IEnumerable<SearchResultGroup> BuildResultGroups(List<SearchResultRow> rows)
        {
            if (rows.Count == 0)
                return Enumerable.Empty<SearchResultGroup>();

            var order = new[]
            {
                "Clientes",
                "Dominios",
                "Revisiones",
                "Programas y proyectos",
                "Cobrar y pagar",
                "Correos Contraseñas",
                "Archivos locales",
                "Notion",
                "Otros"
            };

            return rows
                .GroupBy(GetResultGroupName)
                .OrderBy(g =>
                {
                    var index = Array.FindIndex(order, x =>
                        string.Equals(x, g.Key, StringComparison.OrdinalIgnoreCase));

                    return index >= 0 ? index : int.MaxValue;
                })
                .ThenBy(g => g.Key)
                .Select(g => new SearchResultGroup(g.Key, g));
        }

        private string GetResultGroupName(SearchResultRow row)
        {
            if (row.Source == SearchSource.Notion)
            {
                if (!string.IsNullOrWhiteSpace(row.ExternalSourceName))
                    return row.ExternalSourceName;

                return "Notion";
            }

            if (row.Source == SearchSource.Local || row.Source == SearchSource.Dropbox)
                return "Archivos locales";

            return "Otros";
        }

        #region ===== Module Preferences: text size / default tag =====

        private void LoadModulePreferences()
        {
            _loadingModulePreferences = true;

            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                var scaleKey = values[LS_SearchTextScale] as string ?? "normal";
                _textScale = scaleKey switch
                {
                    "small" => 0.90,
                    "large" => 1.20,
                    _ => 1.0
                };

                SelectComboItemByTag(TextScaleCombo, scaleKey);

                var savedSourceScope =
                    (values[LS_SearchSourceScope] as string ?? "all")
                    .Trim()
                    .ToLowerInvariant();

                _activeSourceScope = savedSourceScope switch
                {
                    "notion" => SearchSourceScope.Notion,
                    "dropbox" => SearchSourceScope.Dropbox,
                    _ => SearchSourceScope.All
                };

                SetSourceScopeChipChecks();

                var savedTag = (values[LS_DefaultSearchTag] as string ?? string.Empty).Trim();
                _lastAppliedDefaultTag = savedTag;
                var predefined = new[]
                {
                    string.Empty, "prtuzREVISION", "zclientes", "zdominios",
                    "zproyectos", "zpagar", "zcorreos"
                };

                if (predefined.Any(x => string.Equals(x, savedTag, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectComboItemByTag(DefaultTagCombo, savedTag);
                    CustomDefaultTagBox.Visibility = Visibility.Collapsed;
                    CustomDefaultTagBox.Text = string.Empty;
                }
                else
                {
                    SelectComboItemByTag(DefaultTagCombo, CUSTOM_TAG_VALUE);
                    CustomDefaultTagBox.Visibility = Visibility.Visible;
                    CustomDefaultTagBox.Text = savedTag;
                }
            }
            finally
            {
                _loadingModulePreferences = false;
            }
        }

        private static void SelectComboItemByTag(ComboBox combo, string tag)
        {
            if (combo == null) return;

            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString() ?? string.Empty, tag ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private async void DefaultTagCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingModulePreferences || DefaultTagCombo.SelectedItem is not ComboBoxItem item)
                return;

            var selected = (item.Tag?.ToString() ?? string.Empty).Trim();
            var isCustom = string.Equals(selected, CUSTOM_TAG_VALUE, StringComparison.Ordinal);
            CustomDefaultTagBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

            if (isCustom)
            {
                CustomDefaultTagBox.Focus(FocusState.Programmatic);
                CustomDefaultTagBox.SelectAll();
                return;
            }

            var previousTag = _lastAppliedDefaultTag;
            SaveDefaultTag(selected);
            _lastAppliedDefaultTag = selected;
            await ApplyDefaultTagToCurrentSearchAsync(previousTag, selected, focusSearchBox: true);
        }

        private async void CustomDefaultTagBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingModulePreferences || DefaultTagCombo.SelectedItem is not ComboBoxItem item)
                return;

            if (!string.Equals(item.Tag?.ToString(), CUSTOM_TAG_VALUE, StringComparison.Ordinal))
                return;

            var previousTag = _lastAppliedDefaultTag;
            var customTag = (CustomDefaultTagBox.Text ?? string.Empty).Trim();

            SaveDefaultTag(customTag);
            _lastAppliedDefaultTag = customTag;
            await ApplyDefaultTagToCurrentSearchAsync(previousTag, customTag, focusSearchBox: false);
        }

        private async Task ApplyDefaultTagToCurrentSearchAsync(
            string? previousTag,
            string? newTag,
            bool focusSearchBox)
        {
            var current = (SearchBox?.Text ?? string.Empty).Trim();
            var previous = (previousTag ?? string.Empty).Trim();
            var selected = (newTag ?? string.Empty).Trim();
            var remainder = current;

            if (!string.IsNullOrWhiteSpace(previous))
            {
                if (current.Equals(previous, StringComparison.OrdinalIgnoreCase))
                    remainder = string.Empty;
                else if (current.StartsWith(previous + " ", StringComparison.OrdinalIgnoreCase))
                    remainder = current.Substring(previous.Length).Trim();
            }
            else
            {
                var currentScope = ResolveNotionBaseScope(current);
                if (currentScope.HasBase)
                    remainder = ExtractOriginalRemainderForScope(current, currentScope);
            }

            var finalQuery = string.IsNullOrWhiteSpace(selected)
                ? remainder
                : string.IsNullOrWhiteSpace(remainder)
                    ? EnsureTagTrailingSpace(selected)
                    : $"{selected} {remainder}";

            _suppressSuggest = true;
            SearchBox.Text = finalQuery;
            _suppressSuggest = false;

            if (focusSearchBox)
                MoveSearchBoxCaretToEnd();

            SyncBaseChipsFromQuery(finalQuery);
            SetTabTitle(finalQuery);
            NotifyWorkspaceChanged();

            if (App.LocalIndex.HasData)
                await RunSearchAsync(finalQuery.Trim());
        }

        private void SaveDefaultTag(string? tag)
        {
            ApplicationData.Current.LocalSettings.Values[LS_DefaultSearchTag] = (tag ?? string.Empty).Trim();
        }

        public async Task ApplyDefaultTagIfEmptyAsync(bool force = false)
        {
            if (!force && _defaultTagAppliedOnce)
                return;

            _defaultTagAppliedOnce = true;

            if (!string.IsNullOrWhiteSpace(SearchBox?.Text))
                return;

            var tag = (ApplicationData.Current.LocalSettings.Values[LS_DefaultSearchTag] as string ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(tag))
                return;

            var display = EnsureTagTrailingSpace(tag);
            _suppressSuggest = true;
            SearchBox.Text = display;
            _suppressSuggest = false;
            MoveSearchBoxCaretToEnd();
            SyncBaseChipsFromQuery(tag);

            if (App.LocalIndex.HasData)
                await RunSearchAsync(tag);
        }

        private static string EnsureTagTrailingSpace(string value)
        {
            var clean = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(clean) ? string.Empty : clean + " ";
        }

        private void SaveSourceScopePreference()
        {
            var value = _activeSourceScope switch
            {
                SearchSourceScope.Notion => "notion",
                SearchSourceScope.Dropbox => "dropbox",
                _ => "all"
            };

            ApplicationData.Current.LocalSettings.Values[
                LS_SearchSourceScope] = value;
        }

        private void SetSourceScopeChipChecks()
        {
            if (ChipSourceAll != null)
                ChipSourceAll.IsChecked =
                    _activeSourceScope == SearchSourceScope.All;

            if (ChipSourceNotion != null)
                ChipSourceNotion.IsChecked =
                    _activeSourceScope == SearchSourceScope.Notion;

            if (ChipSourceDropbox != null)
                ChipSourceDropbox.IsChecked =
                    _activeSourceScope == SearchSourceScope.Dropbox;
        }

        private string GetSourceScopeLabel()
        {
            return _activeSourceScope switch
            {
                SearchSourceScope.Notion => "Notion",
                SearchSourceScope.Dropbox => "Dropbox",
                _ => "Todo"
            };
        }

        private IEnumerable<SearchResultRow> ApplyGlobalSourceFilter(
            IEnumerable<SearchResultRow> rows)
        {
            var sourceRows =
                rows ?? Enumerable.Empty<SearchResultRow>();

            return _activeSourceScope switch
            {
                SearchSourceScope.Notion =>
                    sourceRows.Where(x =>
                        x.Source == SearchSource.Notion),

                SearchSourceScope.Dropbox =>
                    sourceRows.Where(x =>
                        x.Source == SearchSource.Local ||
                        x.Source == SearchSource.Dropbox),

                _ => sourceRows
            };
        }

        private void TextScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingModulePreferences || TextScaleCombo.SelectedItem is not ComboBoxItem item)
                return;

            var key = item.Tag?.ToString() ?? "normal";
            _textScale = key switch
            {
                "small" => 0.90,
                "large" => 1.20,
                _ => 1.0
            };

            ApplicationData.Current.LocalSettings.Values[LS_SearchTextScale] = key;
            ApplyTextScaleToVisualTree();
        }

        private void RootLayout_LayoutUpdated(object? sender, object e)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTextScaleVisualPassUtc).TotalMilliseconds < 250)
                return;

            _lastTextScaleVisualPassUtc = now;
            ApplyTextScaleToVisualTree();
            ApplyResultColumnWidthsToVisualTree();
        }

        private void ApplyTextScaleToVisualTree()
        {
            if (RootLayout == null)
                return;

            ApplyTextScaleRecursive(RootLayout);

            if (QuickCommandsInputHost != null)
                ApplyTextScaleRecursive(QuickCommandsInputHost);
        }

        private void ApplyTextScaleRecursive(DependencyObject node)
        {
            if (node is FrameworkElement element)
            {
                switch (element)
                {
                    case TextBlock textBlock:
                        ApplyScaledFontSize(textBlock, textBlock.FontSize);
                        break;
                    case Control control:
                        ApplyScaledFontSize(control, control.FontSize);
                        break;
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                ApplyTextScaleRecursive(VisualTreeHelper.GetChild(node, i));
        }

        private void ApplyScaledFontSize(FrameworkElement element, double currentSize)
        {
            if (currentSize <= 0 || double.IsNaN(currentSize) || double.IsInfinity(currentSize))
                return;

            if (!_originalFontSizes.TryGetValue(element, out var original))
            {
                original = currentSize;
                _originalFontSizes[element] = original;
            }

            var desired = Math.Round(original * _textScale, 1);

            switch (element)
            {
                case TextBlock textBlock when Math.Abs(textBlock.FontSize - desired) > 0.05:
                    textBlock.FontSize = desired;
                    break;
                case Control control when Math.Abs(control.FontSize - desired) > 0.05:
                    control.FontSize = desired;
                    break;
            }
        }

        #endregion

        #region ===== Result column widths =====

        private void LoadResultColumnWidths()
        {
            if (HeaderPathColumn == null || HeaderDateColumn == null || HeaderStarColumn == null)
                return;

            var values = ApplicationData.Current.LocalSettings.Values;

            HeaderPathColumn.Width = new GridLength(
                ReadColumnWidth(LS_ResultPathColumnWidth, 150, RESULT_PATH_MIN, RESULT_PATH_MAX));

            HeaderDateColumn.Width = new GridLength(
                ReadColumnWidth(LS_ResultDateColumnWidth, 130, RESULT_DATE_MIN, RESULT_DATE_MAX));

            HeaderStarColumn.Width = new GridLength(
                ReadColumnWidth(LS_ResultStarColumnWidth, 36, RESULT_STAR_MIN, RESULT_STAR_MAX));

            HeaderNameColumn.Width = new GridLength(1, GridUnitType.Star);
            ApplyResultColumnWidthsToVisualTree();
        }

        private static double ReadColumnWidth(
            string key,
            double fallback,
            double minimum,
            double maximum)
        {
            var values = ApplicationData.Current.LocalSettings.Values;

            if (!values.TryGetValue(key, out var raw) || raw == null)
                return fallback;

            double parsed = raw switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                string s when double.TryParse(
                    s,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var number) => number,
                _ => fallback
            };

            return Math.Clamp(parsed, minimum, maximum);
        }

        private void PathNameSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var current = HeaderPathColumn.Width.Value;
            HeaderPathColumn.Width = new GridLength(
                Math.Clamp(current + e.HorizontalChange, RESULT_PATH_MIN, RESULT_PATH_MAX));

            ApplyResultColumnWidthsToVisualTree();
        }

        private void NameDateSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // Mover el separador a la derecha amplía Name y reduce Date Modified.
            var current = HeaderDateColumn.Width.Value;
            HeaderDateColumn.Width = new GridLength(
                Math.Clamp(current - e.HorizontalChange, RESULT_DATE_MIN, RESULT_DATE_MAX));

            ApplyResultColumnWidthsToVisualTree();
        }

        private void DateStarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var currentDate = HeaderDateColumn.Width.Value;
            var currentStar = HeaderStarColumn.Width.Value;

            var minimumDelta = Math.Max(
                RESULT_DATE_MIN - currentDate,
                currentStar - RESULT_STAR_MAX);

            var maximumDelta = Math.Min(
                RESULT_DATE_MAX - currentDate,
                currentStar - RESULT_STAR_MIN);

            var appliedDelta = Math.Clamp(e.HorizontalChange, minimumDelta, maximumDelta);

            HeaderDateColumn.Width = new GridLength(currentDate + appliedDelta);
            HeaderStarColumn.Width = new GridLength(currentStar - appliedDelta);

            ApplyResultColumnWidthsToVisualTree();
        }

        private void ColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            SaveResultColumnWidths();
            ApplyResultColumnWidthsToVisualTree();
        }

        private void SaveResultColumnWidths()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values[LS_ResultPathColumnWidth] = HeaderPathColumn.Width.Value;
            values[LS_ResultDateColumnWidth] = HeaderDateColumn.Width.Value;
            values[LS_ResultStarColumnWidth] = HeaderStarColumn.Width.Value;
        }

        private void ApplyResultColumnWidthsToVisualTree()
        {
            if (RootLayout == null ||
                HeaderPathColumn == null ||
                HeaderDateColumn == null ||
                HeaderStarColumn == null)
                return;

            ApplyResultColumnWidthsRecursive(RootLayout);
        }

        private void ApplyResultColumnWidthsRecursive(DependencyObject node)
        {
            if (node is Grid grid &&
                string.Equals(grid.Tag?.ToString(), "ResultColumns", StringComparison.Ordinal) &&
                grid.ColumnDefinitions.Count >= 7)
            {
                grid.ColumnDefinitions[0].Width = new GridLength(HeaderPathColumn.Width.Value);
                grid.ColumnDefinitions[1].Width = new GridLength(5);
                grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                grid.ColumnDefinitions[3].Width = new GridLength(5);
                grid.ColumnDefinitions[4].Width = new GridLength(HeaderDateColumn.Width.Value);
                grid.ColumnDefinitions[5].Width = new GridLength(5);
                grid.ColumnDefinitions[6].Width = new GridLength(HeaderStarColumn.Width.Value);
            }

            var childCount = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < childCount; i++)
                ApplyResultColumnWidthsRecursive(VisualTreeHelper.GetChild(node, i));
        }

        #endregion

        #region
        private void LoadSearchBackgroundTheme()
        {
            var theme = ApplicationData.Current.LocalSettings.Values[LS_SearchBackgroundTheme] as string;

            if (string.IsNullOrWhiteSpace(theme))
                theme = "gray";

            ApplySearchBackgroundTheme(theme);
        }

        private void ApplySearchBackgroundTheme(string theme)
        {
            Color bgColor = theme switch
            {
                "blue" => Color.FromArgb(255, 15, 23, 42),
                "purple" => Color.FromArgb(255, 30, 24, 46),
                "green" => Color.FromArgb(255, 16, 32, 28),

                "navy" => Color.FromArgb(255, 10, 22, 40),
                "midnight" => Color.FromArgb(255, 12, 18, 32),
                "violet" => Color.FromArgb(255, 35, 27, 55),
                "wine" => Color.FromArgb(255, 45, 20, 32),
                "slate" => Color.FromArgb(255, 24, 31, 42),
                "coffee" => Color.FromArgb(255, 32, 26, 22),

                _ => Color.FromArgb(255, 21, 21, 21),
            };

            RootLayout.Background = new SolidColorBrush(bgColor);

            ApplicationData.Current.LocalSettings.Values[LS_SearchBackgroundTheme] = theme;
        }

        private void ThemePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item)
                return;

            var theme = item.Tag as string ?? "gray";
            ApplySearchBackgroundTheme(theme);
        }
        #endregion
    }
}