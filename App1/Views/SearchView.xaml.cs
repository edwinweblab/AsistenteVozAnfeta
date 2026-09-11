using Anfeta.UI.Models;
using Anfeta.UI.Models.Notion;
using Anfeta.UI.Models.Search;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Bookmarks;
using Anfeta.UI.Services.Dropbox;
using Anfeta.UI.Services.Notion;
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
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
        private enum ResultGroupingMode
        {
            None,
            Domain,
            DomainNoBilling,
            Month,
            Name,
            Area,
            AreaNoBilling
        }

        // Dominio completo para resultados/predictivo.
        // - Permite dominio después de @: contacto@mhad.com.mx -> mhad.com.mx
        // - Impide arrancar después de un punto: mhad.com.mx NO puede -> com.mx
        private const string ResultDomainPattern =
            @"(?<![\w.-])(?:https?://)?(?:www\.)?" +
            @"(?<domain>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+" +
            @"(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|" +
            @"com|mx|org|net|io|co|app|dev))" +
            @"(?=$|[/:?#\s)\]}>.,;!])";

        private ViewMode _mode = ViewMode.Explorer;
        private SearchSourceScope _activeSourceScope = SearchSourceScope.All;
        private ResultGroupingMode _resultGroupingMode = ResultGroupingMode.None;
        private CollectionViewSource? _groupedResultsViewSource;
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
        // Resultados abren siempre con lo más reciente arriba.
        // El usuario todavía puede cambiar el orden manualmente durante la sesión.
        private string _sortKey = "mod_desc";

        // debounce / tokens
        private DispatcherTimer? _searchDebounceTimer;
        private CancellationTokenSource? _searchCts;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _selectionPreviewDebounceCts;

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
        private static int _sharedVoiceInitStarted;
        private static int _sharedDailyCalendarAutomationStarted;
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

        // Inicialización visual/pesada por pestaña. Las pestañas ocultas se crean
        // prácticamente vacías y difieren filtros, watchers y pintado hasta que
        // el usuario realmente las selecciona.
        private readonly SemaphoreSlim _runtimeInitializationLock = new(1, 1);
        private bool _runtimeInitializationCompleted;

        // Las pestañas restauradas que no están visibles pueden diferir su
        // pintado inicial. Así no construimos miles de filas XAML para cada
        // pestaña durante el arranque. SearchTabsView las activa al seleccionarlas.
        public bool DeferInitialIndexPaint { get; set; }
        private bool _deferredIndexPaintPending;
        private long _lastPaintedIndexVersion = -1;

        // Refresco ligero de Resultados. No reemplaza el watcher existente:
        // solo lo despierta con más frecuencia. CheckNotionChangesAsync ya
        // tiene gate global y mínimo de 30 s, por lo que no multiplica probes.
        private Microsoft.UI.Dispatching.DispatcherQueueTimer?
            _fastNotionResultsRefreshTimer;

        private readonly SemaphoreSlim _mutLock = new(1, 1);
        private CancellationTokenSource? _refreshCts;
        private string? _currentFolderPath;

        // pestañas
        public event EventHandler<string>? TabTitleChanged;
        public event EventHandler<string>? TabModeChanged;
        public event EventHandler? WorkspaceChanged;

        // Modo visual actual de ESTA pestaña. SearchTabsView lo usa únicamente
        // para mostrar el icono 🔎/calendario; no dispara cargas adicionales.
        public string CurrentTabMode =>
            _calendarViewActive ? "calendar" : "results";

        private void NotifyTabModeChanged(string mode)
        {
            TabModeChanged?.Invoke(
                this,
                string.Equals(mode, "calendar", StringComparison.OrdinalIgnoreCase)
                    ? "calendar"
                    : "results");
            // actualizar flag que indica si la búsqueda debe limitarse a actividades del calendario
            _soloActividadesCalendario = string.Equals(mode, "calendar", StringComparison.OrdinalIgnoreCase);
        }

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
        private const string LS_ResultGroupingMode = "Search.ResultGrouping.Mode";
        private const string LS_ResultPathColumnWidth = "Search.ResultColumns.Path";
        private const string LS_ResultDateColumnWidth = "Search.ResultColumns.Date";
        private const string LS_ResultStatusColumnWidth = "Search.ResultColumns.Status";
        private const string LS_ResultScheduledDateColumnWidth = "Search.ResultColumns.ScheduledDate";
        private const string LS_ResultColPathVisible = "Search.ResultColumns.Path.Visible";
        private const string LS_ResultColStatusVisible = "Search.ResultColumns.Status.Visible";
        private const string LS_ResultColScheduledDateVisible = "Search.ResultColumns.ScheduledDate.Visible";
        private const string LS_ResultColDateVisible = "Search.ResultColumns.Date.Visible";
        private bool _colPathVisible = true;
        private bool _colStatusVisible = true;
        private bool _colScheduledDateVisible = true;
        private bool _colDateVisible = true;
        private double _savedPathWidth = 150;
        private double _savedStatusWidth = 180;
        private double _savedScheduledDateWidth = 155;
        private double _savedDateWidth = 145;
        private const string LS_DetailsPaneWidth = "Search.DetailsPane.Width";
        private const double DETAILS_PANE_MIN = 260;
        private const double DETAILS_PANE_DEFAULT = 380;
        private const double DETAILS_PANE_MAX = 750;
        private const double RESULT_PATH_MIN = 70;
        private const double RESULT_PATH_MAX = 420;
        private const double RESULT_DATE_MIN = 120;
        private const double RESULT_DATE_MAX = 280;
        private const double RESULT_STATUS_MIN = 100;
        private const double RESULT_STATUS_MAX = 420;
        private const double RESULT_SCHEDULED_DATE_MIN = 120;
        private const double RESULT_SCHEDULED_DATE_MAX = 280;
        private const string CUSTOM_TAG_VALUE = "__custom__";
        private readonly Dictionary<FrameworkElement, double> _originalFontSizes = new();
        private double _textScale = 1.0;
        private bool _loadingModulePreferences;
        private bool _defaultTagAppliedOnce;
        private string _lastAppliedDefaultTag = string.Empty;

        // estado visual global de carga
        private int _busyOperationCount;

        // arrastrar archivos hacia Notion
        private bool _isNotionFileDragActive;

        // vista previa de páginas de Notion
        private readonly NotionPagePreviewService _notionPreviewService = new();
        private CancellationTokenSource? _notionPreviewCts;
        private string _activePreviewPageId = string.Empty;
        private IReadOnlyList<NotionPreviewBlock> _activePreviewBlocks = Array.Empty<NotionPreviewBlock>();
        private SearchResultRow? _activePreviewRow;
        private readonly SpeechSynthesizer _previewSpeechSynth = new();
        private MediaPlayer? _previewSpeechPlayer;
        private bool _previewSpeechPlaying;

        private CancellationTokenSource? _localImagePreviewCts;
        private string _activeLocalImagePath = string.Empty;
        private double _localImageZoom = 1.0;
        private int _localImagePixelWidth;
        private int _localImagePixelHeight;
        private bool _localImageFitMode;
        private bool _localImageWheelHandlerHooked;

        // vista de resultados estilo Everything
        private const string LS_ResultsViewZoomLevel =
            "Search.ResultsView.ZoomLevel";
        private int _resultsViewZoomLevel;
        private bool _resultsWheelHandlerHooked;
        private CancellationTokenSource? _thumbnailLoadCts;

        // calendario comparativo de Revisiones
        private readonly NotionCalendarService _notionCalendarService = new();
        private CancellationTokenSource? _calendarCts;
        private DateTime _calendarSelectedDate = DateTime.Today;
        private bool _calendarViewActive;
        private bool _soloActividadesCalendario;
        private Color _calendarThemeColor =
            Color.FromArgb(255, 21, 21, 21);

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
            public string? SearchText =>
                $"{_x.Name} {_x.Target} {_x.SearchText} " +
                $"{_x.Description} {_x.ProjectUpdateStatus}";

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
            PresetViews.Add(new WeakReference<SearchView>(this));

            // El indicador de Fecha por hacer es independiente de los
            // ordenamientos existentes por nombre y fecha modificada.
            HeaderNameSortButton.Click +=
                (_, __) => ScheduledDateSortArrow.Text = string.Empty;

            HeaderModifiedSortButton.Click +=
                (_, __) => ScheduledDateSortArrow.Text = string.Empty;

            _savedFiltersService = new SavedSearchFiltersService(_savedFiltersRepository);

            // La suscripción a StateChanged vive SOLO en SearchView_Loaded bajo _isIndexStateHooked.
            // No se suscribe aquí para evitar handler duplicado y memory leak.

            ResultsList.ItemsSource = Results;
            FolderTree.ItemsSource = new ObservableCollection<FolderNode>();

            Loaded += SearchView_Loaded;

            // BLOQUE 11 · Ctrl+V global. Se engancha una sola vez por pestaña.
            // El handler ignora cualquier campo editable para conservar el
            // pegado normal de TextBox / RichEditBox / AutoSuggestBox.
            RootLayout.KeyDown += RootLayout_GlobalPasteKeyDown;

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
            Unloaded -= SearchView_Unloaded;
            Unloaded += SearchView_Unloaded;

            // Una pestaña restaurada/recién creada que todavía no está activa no
            // inicializa filtros, watchers, mensajes ni pinta miles de filas.
            // SearchTabsView la materializa al seleccionarla.
            if (DeferInitialIndexPaint)
            {
                _deferredIndexPaintPending = true;
                ForceHideLoadingState();
                return;
            }

            await EnsureSearchViewRuntimeInitializedAsync();

            if (Interlocked.CompareExchange(
                    ref _sharedVoiceInitStarted,
                    1,
                    0) == 0)
            {
                await _voiceEngine.ReloadAsync();
            }

            await EnsureIndexBootstrappedAsync();
            await ApplyDefaultTagIfEmptyAsync();

            // Resultados de Notion se comprueban con mayor frecuencia que el
            // watcher histórico de 2 min. El gate compartido evita duplicados.
            StartFastNotionResultsRefreshWatcher();

            // Pipeline unico de arranque del calendario: primero mantenimiento
            // diario en background y despues precarga de Hoy/checklist/proyectos.
            // Asi no compiten varios procesos pesados de Notion al mismo tiempo.
            StartCalendarStartupPipeline();

            NotifyTabModeChanged(CurrentTabMode);
            ForceHideLoadingState();

            // Precalentamiento silencioso en segundo plano de Ollama para eliminar el retraso de arranque en frío
            _ = Task.Run(() => LocalAiService.WarmupAsync());
        }

        private async Task EnsureSearchViewRuntimeInitializedAsync()
        {
            if (_runtimeInitializationCompleted)
            {
                EnsureSearchViewRuntimeSubscriptions();
                return;
            }

            await _runtimeInitializationLock.WaitAsync();

            try
            {
                if (_runtimeInitializationCompleted)
                {
                    EnsureSearchViewRuntimeSubscriptions();
                    return;
                }

                LoadSearchBackgroundTheme();
                LoadModulePreferences();
                LoadResultColumnWidths();
                LoadDetailsPaneWidth();
                LoadResultsViewMode();
                EnsureResultsWheelHandler();
                InitializeMessagesView();
                UpdateColumnSortIndicators();
                EnsureSearchViewRuntimeSubscriptions();

                LoadExcludedFolders();
                RefreshExcludedFoldersUi();
                LoadSavedSearches();
                RefreshSavedSearchesUi();
                CommandsSidebarList.ItemsSource = _savedSearches;
                RefreshCommandsSidebarUi();
                LoadSidebarExpandedStates();
                await LoadSavedFiltersAsync();

                // No bloquea la creación/selección de la pestaña.
                _ = LoadBookmarksAsync();

                ApplyTextScaleToVisualTree();
                _runtimeInitializationCompleted = true;
            }
            finally
            {
                _runtimeInitializationLock.Release();
            }
        }

        private void EnsureSearchViewRuntimeSubscriptions()
        {
            if (_isIndexStateHooked)
                return;

            DropboxIndexCoordinator.StateChanged += OnIndexStateChanged;
            SearchFocusBridge.FocusRequested += OnSearchFocusRequested;
            _isIndexStateHooked = true;
            AttachMessagesNavigationBridge();
        }

        private void SearchView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_isIndexStateHooked)
            {
                DropboxIndexCoordinator.StateChanged -= OnIndexStateChanged;
                SearchFocusBridge.FocusRequested -= OnSearchFocusRequested;
                _isIndexStateHooked = false;
            }

            DetachMessagesNavigationBridge();
            // Unloaded also occurs when switching tabs, not only when closing.
            _messagesRefreshTimer?.Stop();
            _messagesConversationCts?.Cancel();
            CancelPendingSearch();
            StopFastNotionResultsRefreshWatcher();
            StopNotionChangeWatcher();
            StopDropboxChangeWatcher();

            try
            {
                _notionPreviewCts?.Cancel();
                _notionPreviewCts?.Dispose();
                _notionPreviewCts = null;
            }
            catch
            {
                // La cancelación de la vista previa no debe bloquear el cierre.
            }

            try
            {
                _localImagePreviewCts?.Cancel();
                _localImagePreviewCts?.Dispose();
                _localImagePreviewCts = null;
            }
            catch
            {
            }

            try
            {
                _thumbnailLoadCts?.Cancel();
                _thumbnailLoadCts?.Dispose();
                _thumbnailLoadCts = null;
            }
            catch
            {
            }

            try
            {
                _calendarCts?.Cancel();
                _calendarCts?.Dispose();
                _calendarCts = null;
            }
            catch
            {
            }

            try
            {
                _notionNoticeTimer?.Stop();
                _notionNoticeTimer = null;
            }
            catch
            {
            }
        }
        /// <summary>
        /// Guarda el estado de una pestaña restaurada sin ejecutar búsquedas ni
        /// recorridos de carpeta. La pestaña se materializa cuando el usuario la abre.
        /// </summary>
        public void StageDeferredTabState(SearchTabState state)
        {
            if (state == null)
                return;
            _stagedSearchState = state;

            DeferInitialIndexPaint = true;
            _deferredIndexPaintPending = true;
            _currentFolderPath = (state.CurrentFolder ?? string.Empty).Trim();

            _allowProgrammaticSearch = true;
            SearchBox.Text = state.Query ?? string.Empty;
            _allowProgrammaticSearch = false;

            SetTabTitle(SearchBox.Text);
        }

        /// <summary>
        /// Activa una pestaña que estaba diferida. No vuelve a sincronizar; usa el
        /// índice compartido en memoria y restaura únicamente su vista local.
        /// </summary>
        public async Task ActivateDeferredTabAsync(SearchTabState? state = null)
        {
            DeferInitialIndexPaint = false;

            // La pestaña ya se mostró visualmente antes de ejecutar este bloque.
            // Aquí se materializa solo cuando realmente queda seleccionada.
            await EnsureSearchViewRuntimeInitializedAsync();
            await RefreshSharedSearchPresetsAsync();

            if (Interlocked.CompareExchange(
                    ref _sharedVoiceInitStarted,
                    1,
                    0) == 0)
            {
                await _voiceEngine.ReloadAsync();
            }

            if (!_bootstrappedOnce)
                await EnsureIndexBootstrappedAsync();

            StartNotionChangeWatcher();
            StartDropboxChangeWatcher();
            StartFastNotionResultsRefreshWatcher();

            if (_calendarViewActive)
                StartCalendarChangesTimer();

            StartCalendarStartupPipeline();

            if (state != null)
            {
                _stagedSearchState = null;
                _deferredIndexPaintPending = false;
                await RestoreTabStateAsync(state);
                await ApplyDefaultTagIfEmptyAsync();
                NotifyTabModeChanged(CurrentTabMode);
                return;
            }

            if (_deferredIndexPaintPending && App.LocalIndex.HasData)
            {
                _deferredIndexPaintPending = false;

                // Volver a una pestaña NO repinta miles de resultados si el
                // índice compartido no cambió desde la última vez que se vio.
                if (_lastPaintedIndexVersion != App.LocalIndex.Version ||
                    !string.Equals(_lastCompletedSearchQuery, SearchBox.Text, StringComparison.Ordinal))
                    await PaintLoadedIndexAsync();
            }

            await ApplyDefaultTagIfEmptyAsync();
            NotifyTabModeChanged(CurrentTabMode);
        }

        /// <summary>
        /// Pone una pestaña en modo de fondo. No destruye su estado: solo evita
        /// repintados y timers/watchers mientras otra pestaña está seleccionada.
        /// </summary>
        public void SuspendAsBackgroundTab()
        {
            CancelPendingSearch();
            DeferInitialIndexPaint = true;
            _deferredIndexPaintPending = true;

            StopFastNotionResultsRefreshWatcher();
            StopNotionChangeWatcher();
            StopDropboxChangeWatcher();
            StopCalendarChangesTimer();

            // Una pestaña de fondo tampoco recibe cambios globales ni el hotkey
            // de foco. Al seleccionarla se vuelve a enganchar sin reindexar.
            if (_isIndexStateHooked)
            {
                DropboxIndexCoordinator.StateChanged -= OnIndexStateChanged;
                SearchFocusBridge.FocusRequested -= OnSearchFocusRequested;
                _isIndexStateHooked = false;
                DetachMessagesNavigationBridge();
            }
        }

        public async Task RefreshFromSharedIndexIfChangedAsync()
        {
            if (DeferInitialIndexPaint || !App.LocalIndex.HasData)
                return;

            if (_lastPaintedIndexVersion == App.LocalIndex.Version)
                return;

            await PaintLoadedIndexAsync();
        }

        private void StartFastNotionResultsRefreshWatcher()
        {
            if (DeferInitialIndexPaint ||
                _fastNotionResultsRefreshTimer != null)
            {
                return;
            }

            _fastNotionResultsRefreshTimer =
                DispatcherQueue.CreateTimer();

            _fastNotionResultsRefreshTimer.Interval =
                TimeSpan.FromSeconds(30);

            _fastNotionResultsRefreshTimer.Tick +=
                async (_, __) =>
                {
                    await RefreshNotionResultsFastAsync();
                };

            _fastNotionResultsRefreshTimer.Start();

            // Primera comprobación sin esperar 30 segundos.
            _ = RefreshNotionResultsFastAsync();
        }

        private void StopFastNotionResultsRefreshWatcher()
        {
            if (_fastNotionResultsRefreshTimer == null)
                return;

            try
            {
                _fastNotionResultsRefreshTimer.Stop();
            }
            catch
            {
            }

            _fastNotionResultsRefreshTimer = null;
        }

        private async Task RefreshNotionResultsFastAsync()
        {
            // No competir con el calendario cuando está visible.
            if (DeferInitialIndexPaint ||
                _calendarViewActive)
            {
                return;
            }

            try
            {
                // Este método ya tiene:
                // - SharedNotionProbeGate
                // - ReserveSharedProbe(..., 30 s)
                // - sync incremental
                // Por eso este timer NO genera una sincronización completa.
                await CheckNotionChangesAsync();

                // Si otra pestaña hizo el cambio sobre el índice global,
                // repinta esta vista inmediatamente conservando su búsqueda.
                await RefreshFromSharedIndexIfChangedAsync();
            }
            catch
            {
                // Es un watcher silencioso: nunca bloquear Resultados.
            }
        }

        private void OnSearchFocusRequested()
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(150);

                SearchBox.Focus(FocusState.Programmatic);
                MoveSearchBoxCaretToEnd();
            });
        }

        private void SelectTextInsideSearchBox()
        {
            var textBox = FindVisualChild<TextBox>(SearchBox);

            if (textBox != null)
            {
                textBox.Focus(FocusState.Programmatic);
                MoveSearchBoxCaretToEnd();
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
            ResetPreviewPanel();
        }

        private void FinishUi()
        {
            NotifyWorkspaceChanged();
            ForceHideLoadingState();
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = $"{Results.Count} resultados";
        }

        #endregion
        private string _selectedMonthFilter = "all";
        private bool _isUpdatingMonthFilterCombo;

        private void UpdateMonthFilterComboItems()
        {
            if (MonthFilterCombo == null || _isUpdatingMonthFilterCombo)
                return;

            _isUpdatingMonthFilterCombo = true;
            try
            {
                var availableMonths = Results
                    .Select(r => r.MonthChipText)
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m)
                    .ToList();

                var currentTag = (MonthFilterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? _selectedMonthFilter;

                MonthFilterCombo.Items.Clear();

                var allItem = new ComboBoxItem { Content = "Todos", Tag = "all" };
                MonthFilterCombo.Items.Add(allItem);

                ComboBoxItem selectedItemToSet = allItem;

                foreach (var month in availableMonths)
                {
                    var item = new ComboBoxItem { Content = month, Tag = month };
                    MonthFilterCombo.Items.Add(item);
                    if (string.Equals(currentTag, month, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedItemToSet = item;
                    }
                }

                if (Results.Any(r => string.IsNullOrWhiteSpace(r.MonthChipText)))
                {
                    var noMonthItem = new ComboBoxItem { Content = "Sin mes", Tag = "nomonth" };
                    MonthFilterCombo.Items.Add(noMonthItem);
                    if (string.Equals(currentTag, "nomonth", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedItemToSet = noMonthItem;
                    }
                }

                MonthFilterCombo.SelectedItem = selectedItemToSet;
                _selectedMonthFilter = (selectedItemToSet.Tag?.ToString() ?? "all").Trim();
            }
            finally
            {
                _isUpdatingMonthFilterCombo = false;
            }
        }

        private List<SearchResultRow> GetFilteredResultsForDisplay()
        {
            if (string.Equals(_selectedMonthFilter, "all", StringComparison.OrdinalIgnoreCase))
                return Results.ToList();

            if (string.Equals(_selectedMonthFilter, "nomonth", StringComparison.OrdinalIgnoreCase))
                return Results.Where(r => string.IsNullOrWhiteSpace(r.MonthChipText)).ToList();

            return Results.Where(r => string.Equals(r.MonthChipText, _selectedMonthFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void RefreshResultsListView()
        {
            _lastCompletedSearchQuery = SearchBox.Text;
            NotifyWorkspaceChanged();
            // Prepara únicamente metadatos VISUALES de Dropbox.
            // Target conserva la ruta local absoluta que usan abrir/renombrar/eliminar.
            foreach (var row in Results)
                ApplyDropboxDisplayMetadata(row);

            UpdateMonthFilterComboItems();

            var displayRows = GetFilteredResultsForDisplay();

            if (_resultGroupingMode == ResultGroupingMode.None)
            {
                _groupedResultsViewSource = null;
                _resultGroups.Clear();
                if (!ReferenceEquals(ResultsList.ItemsSource, displayRows))
                {
                    ResultsList.ItemsSource = displayRows;
                }
            }
            else
            {
                var newGroups = BuildResultGroups(displayRows).ToList();

                // Comprobar si los grupos son idénticos en clave y conteo
                bool groupsIdentical = _resultGroups.Count == newGroups.Count;
                if (groupsIdentical)
                {
                    for (int g = 0; g < newGroups.Count; g++)
                    {
                        if (!string.Equals(_resultGroups[g].Key, newGroups[g].Key, StringComparison.Ordinal) ||
                            _resultGroups[g].Count != newGroups[g].Count)
                        {
                            groupsIdentical = false;
                            break;
                        }
                    }
                }

                if (groupsIdentical && _groupedResultsViewSource != null && ResultsList.ItemsSource != null)
                {
                    // Estructura idéntica: NO tocar ItemsSource ni CollectionViewSource para evitar el salto y congelamiento
                }
                else
                {
                    _resultGroups.Clear();

                    foreach (var group in newGroups)
                        _resultGroups.Add(group);

                    if (_groupedResultsViewSource == null)
                    {
                        _groupedResultsViewSource = new CollectionViewSource
                        {
                            Source = _resultGroups,
                            IsSourceGrouped = true
                        };
                        ResultsList.ItemsSource = _groupedResultsViewSource.View;
                    }
                    else if (ResultsList.ItemsSource == null)
                    {
                        ResultsList.ItemsSource = _groupedResultsViewSource.View;
                    }
                }
            }

            if (ResultsThumbnailGrid != null)
            {
                ResultsThumbnailGrid.ItemsSource =
                    _resultGroupingMode == ResultGroupingMode.None
                        ? displayRows
                        : _groupedResultsViewSource?.View;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyTextScaleToVisualTree();
                ApplyResultColumnWidthsToVisualTree();
                ApplyResultsViewMode();
            });
        }

        private IEnumerable<SearchResultGroup> BuildResultGroups(
            List<SearchResultRow> rows)
        {
            if (rows.Count == 0 ||
                _resultGroupingMode == ResultGroupingMode.None)
            {
                return Enumerable.Empty<SearchResultGroup>();
            }

            var projects = rows
                .GroupBy(row => GetResultGroupName(row, null))
                .OrderBy(group => IsFallbackGroup(group.Key) ? 1 : 0)
                .ThenBy(group => group.Key);

            if (_resultGroupingMode is ResultGroupingMode.Domain or ResultGroupingMode.DomainNoBilling)
            {
                var targetRows = _resultGroupingMode == ResultGroupingMode.DomainNoBilling
                    ? rows.Where(r => !IsExcludedByNoBillingMode(r)).ToList()
                    : rows;

                var domainProjects = targetRows
                    .GroupBy(row => GetResultGroupName(row, null))
                    .OrderBy(group => IsFallbackGroup(group.Key) ? 1 : 0)
                    .ThenBy(group => group.Key);

                // Dentro de cada proyecto el Key numérico impone
                // exactamente A -> P -> R -> Z -> Otros -> Cobros/Pagos.
                return domainProjects.SelectMany(project => project
                    .GroupBy(GetWorkflowGroupState)
                    .OrderBy(state => state.Key)
                    .Select(state => new SearchResultGroup(
                        $"{project.Key} · {GetWorkflowStateLabel(state.Key)}",
                        OrderWorkflowRows(state))));
            }

            if (_resultGroupingMode == ResultGroupingMode.Month)
            {
                var monthGroups = rows
                    .GroupBy(row => string.IsNullOrWhiteSpace(row.MonthChipText) ? "Sin mes" : row.MonthChipText)
                    .OrderBy(group => group.Key == "Sin mes" ? 1 : 0)
                    .ThenByDescending(group => group.Key);

                return monthGroups.Select(group => new SearchResultGroup(
                    $"Mes: {group.Key}",
                    OrderWorkflowRows(group)));
            }

            if (_resultGroupingMode is ResultGroupingMode.Area or ResultGroupingMode.AreaNoBilling)
            {
                var targetRows = _resultGroupingMode == ResultGroupingMode.AreaNoBilling
                    ? rows.Where(r => !IsExcludedByNoBillingMode(r)).ToList()
                    : rows;

                var areaGroups = targetRows
                    .GroupBy(row => row?.AreaGroupName ?? "Otros")
                    .OrderBy(group => IsFallbackGroup(group.Key) ? 1 : 0)
                    .ThenBy(group => group.Key);

                return areaGroups.Select(group => new SearchResultGroup(
                    group.Key,
                    group.OrderBy(row => row.OrderNumericRank)
                        .ThenBy(row => row.VisualTitle, StringComparer.OrdinalIgnoreCase)));
            }

            return projects.Select(group => new SearchResultGroup(group.Key, group));
        }

        private static bool IsExcludedByNoBillingMode(
            SearchResultRow row)
        {
            if (row == null)
                return false;

            var source = (row.ExternalSourceName ?? string.Empty).Trim();

            // Cobrar y pagar es la base de cobros/pagos.
            if (string.Equals(
                    source,
                    "Cobrar y pagar",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var title = (row.DisplayName ?? row.Name ?? string.Empty).Trim();

            // Detectamos COBRAR/PAGAR en el título de la actividad.
            if (Regex.IsMatch(
                    title,
                    @"(?<![\p{L}\p{Nd}_])(?:a?prtuz|sprtuz|rtuz|z)?(?:COBRAR|PAGAR)(?![\p{L}\p{Nd}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant))
            {
                return true;
            }

            return false;
        }

        private static int GetWorkflowGroupState(
            SearchResultRow row)
        {
            if (row == null)
                return WorkflowOther;

            var title =
                (row.DisplayName ?? row.Name ?? string.Empty).Trim();

            var source =
                (row.ExternalSourceName ?? string.Empty).Trim();

            // Las bases auxiliares (Dominios, Correos y Cobrar/Pagar) son
            // referencias operativas del proyecto. John pidió que no compitan
            // con las actividades A/P/R y que queden al final.
            if (string.Equals(
                    source,
                    "Cobrar y pagar",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    source,
                    "Dominios",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    source,
                    "Correos Contraseñas",
                    StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowBillingReferences;
            }

            // También detectamos COBRAR/PAGAR dentro de Revisiones. Se exige
            // token completo para no clasificar palabras que solo lo contengan.
            if (Regex.IsMatch(
                    title,
                    @"(?<![\p{L}\p{Nd}_])(?:a?prtuz|sprtuz|rtuz|z)?(?:COBRAR|PAGAR)(?![\p{L}\p{Nd}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant))
            {
                return WorkflowBillingReferences;
            }

            return GetWorkflowState(title);
        }

        private static IEnumerable<SearchResultRow> OrderWorkflowRows(
            IEnumerable<SearchResultRow> rows)
        {
            // Dentro de cada bloque preservamos orden numérico si existe,
            // luego lo más reciente por Fecha por hacer / modificada; como fallback usamos
            // el título para que el orden sea estable.
            return rows
                .OrderBy(row => row.OrderNumericRank)
                .ThenByDescending(row =>
                    ParseResultOrderDate(row.ScheduledDate))
                .ThenByDescending(row =>
                    ParseResultOrderDate(row.ServerModified))
                .ThenBy(row =>
                    row.DisplayName ?? row.Name ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static DateTime ParseResultOrderDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DateTime.MinValue;

            return DateTime.TryParse(
                    value,
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var current)
                ? current
                : DateTime.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var invariant)
                    ? invariant
                    : DateTime.MinValue;
        }

        private string GetResultGroupName(
            SearchResultRow row,
            Dictionary<string, int>? frequency)
        {
            return _resultGroupingMode switch
            {
                ResultGroupingMode.Domain => GetDomainGroupName(row),
                ResultGroupingMode.DomainNoBilling => GetDomainGroupName(row),
                ResultGroupingMode.Month => string.IsNullOrWhiteSpace(row?.MonthChipText) ? "Sin mes" : row.MonthChipText,
                ResultGroupingMode.Name => GetAssignedPersonGroupName(row),
                ResultGroupingMode.Area => row?.AreaGroupName ?? "Otros",
                ResultGroupingMode.AreaNoBilling => row?.AreaGroupName ?? "Otros",
                _ => "Resultados"
            };
        }

        private static string GetDomainGroupName(SearchResultRow row)
        {
            if (row == null)
                return "Sin dominio";

            if (!string.IsNullOrWhiteSpace(row.DomainChipText) &&
                !SearchResultRow.IsPlaceholderDomain(row.DomainChipText))
            {
                return row.DomainChipText;
            }

            // No depender únicamente del título visible. En zCORREOS/zDOMINIOS
            // el dominio puede venir dentro de un correo, SearchText o Target.
            var sourceText = string.Join(
                " ",
                new[]
                {
                    row.DisplayName,
                    row.Name,
                    row.PathColumn,
                    row.SearchText,
                    row.Description,
                    row.Target
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));

            var match = Regex.Match(
                sourceText,
                ResultDomainPattern,
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);

            if (match.Success)
            {
                var candidate = match.Groups["domain"].Value
                    .Trim()
                    .TrimEnd('.')
                    .ToLowerInvariant();

                if (!SearchResultRow.IsPlaceholderDomain(candidate))
                    return candidate;
            }

            return "Sin dominio";
        }

        private static string GetAssignedPersonGroupName(
            SearchResultRow row)
        {
            var name = (row.DisplayName ?? row.Name ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(name))
                return "Sin persona asignada";

            var aliases = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["jjohn"] = "John",
                ["john"] = "John",

                ["aandr"] = "Andrade",
                ["andrade"] = "Andrade",

                ["nneft"] = "Neftali",
                ["neftali"] = "Neftali",

                ["bbria"] = "Brian",
                ["brian"] = "Brian",

                ["ggena"] = "Genaro",
                ["genaro"] = "Genaro",

                ["iisaia"] = "Isaias",
                ["iisai"] = "Isaias",
                ["isaias"] = "Isaias",
                ["isaías"] = "Isaias",

                ["kkarl"] = "Karla",
                ["karla"] = "Karla",

                ["eedua"] = "Sotelo",
                ["ssote"] = "Sotelo",
                ["sotelo"] = "Sotelo",
                ["eduardo"] = "Sotelo",

                ["aacal"] = "Acali",
                ["acalli"] = "Acali",

                ["eemma"] = "Emmanuel",
                ["emmanuel"] = "Emmanuel"
            };

            var tokens = Regex.Matches(
                    name,
                    @"[\p{L}\p{Nd}]+")
                .Cast<Match>()
                .Select(match => match.Value.Trim())
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToList();

            foreach (var token in tokens)
            {
                if (aliases.TryGetValue(token, out var person))
                    return person;
            }

            return "Sin persona asignada";
        }

        private static bool IsFallbackGroup(string key)
        {
            return key.StartsWith(
                       "Sin ",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       key,
                       "Otros",
                       StringComparison.OrdinalIgnoreCase);
        }

        #region ===== Vista resultados: lista / miniaturas =====

        private void LoadResultsViewMode()
        {
            var values = ApplicationData.Current.LocalSettings.Values;

            _resultsViewZoomLevel =
                values.TryGetValue(
                    LS_ResultsViewZoomLevel,
                    out var raw) &&
                raw is int saved
                    ? Math.Clamp(saved, 0, 3)
                    : 0;

            ApplyResultsViewMode();
        }

        private void EnsureResultsWheelHandler()
        {
            if (_resultsWheelHandlerHooked ||
                ResultsViewHost == null)
            {
                return;
            }

            ResultsViewHost.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(
                    ResultsViewHost_PointerWheelChanged),
                handledEventsToo: true);

            _resultsWheelHandlerHooked = true;
        }

        private void ResultsViewHost_PointerPressed(
            object sender,
            PointerRoutedEventArgs e)
        {
            ResultsViewHost.Focus(FocusState.Pointer);
        }

        private void ResultsViewHost_PointerWheelChanged(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (!IsResultsControlKeyDown())
                return;

            var delta = e
                .GetCurrentPoint(ResultsViewHost)
                .Properties
                .MouseWheelDelta;

            ChangeResultsViewZoom(
                delta > 0 ? 1 : -1);

            e.Handled = true;
        }

        private static bool IsResultsControlKeyDown()
        {
            var leftState =
                Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.LeftControl);

            var rightState =
                Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.RightControl);

            const Windows.UI.Core.CoreVirtualKeyStates down =
                Windows.UI.Core.CoreVirtualKeyStates.Down;

            return (leftState & down) == down ||
                   (rightState & down) == down;
        }

        private void ResultsViewZoomIn_Click(
            object sender,
            RoutedEventArgs e)
        {
            ChangeResultsViewZoom(1);
        }

        private void ResultsViewZoomOut_Click(
            object sender,
            RoutedEventArgs e)
        {
            ChangeResultsViewZoom(-1);
        }

        private void ChangeResultsViewZoom(int direction)
        {
            var next = Math.Clamp(
                _resultsViewZoomLevel + direction,
                0,
                3);

            if (next == _resultsViewZoomLevel)
                return;

            _resultsViewZoomLevel = next;

            ApplicationData.Current.LocalSettings.Values[
                LS_ResultsViewZoomLevel] =
                _resultsViewZoomLevel;

            ApplyResultsViewMode();

            if (_resultsViewZoomLevel > 0)
                _ = LoadResultThumbnailsAsync();
        }

        private void ApplyResultsViewMode()
        {
            if (ResultsList == null ||
                ResultsThumbnailGrid == null)
            {
                return;
            }

            var useThumbnails =
                _resultsViewZoomLevel > 0;

            ResultsList.Visibility =
                useThumbnails
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            ResultsThumbnailGrid.Visibility =
                useThumbnails
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            ResultsHeaderGrid.Visibility =
                useThumbnails
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            var itemWidth = _resultsViewZoomLevel switch
            {
                1 => 104d,
                2 => 150d,
                3 => 205d,
                _ => 150d
            };

            var itemHeight = _resultsViewZoomLevel switch
            {
                1 => 132d,
                2 => 176d,
                3 => 232d,
                _ => 176d
            };

            foreach (var row in Results)
            {
                row.ThumbnailTileWidth = itemWidth;
                row.ThumbnailTileHeight = itemHeight;
                row.ThumbnailImageHeight =
                    Math.Max(64, itemHeight - 48);
            }

            if (ResultsViewModeText != null)
            {
                ResultsViewModeText.Text =
                    _resultsViewZoomLevel switch
                    {
                        1 => "Pequeña",
                        2 => "Mediana",
                        3 => "Grande",
                        _ => "Lista"
                    };
            }

            if (useThumbnails)
                _ = LoadResultThumbnailsAsync();
        }

        private async Task LoadResultThumbnailsAsync()
        {
            if (_resultsViewZoomLevel <= 0)
                return;

            try
            {
                _thumbnailLoadCts?.Cancel();
                _thumbnailLoadCts?.Dispose();
            }
            catch
            {
            }

            _thumbnailLoadCts =
                new CancellationTokenSource();

            var token = _thumbnailLoadCts.Token;

            var requestedSize =
                _resultsViewZoomLevel switch
                {
                    1 => 96u,
                    2 => 160u,
                    3 => 220u,
                    _ => 160u
                };

            var candidates = Results
                .Where(row =>
                    row.Source != SearchSource.Notion &&
                    row.Thumbnail == null &&
                    IsThumbnailImagePath(row.Target))
                .Take(250)
                .ToList();

            foreach (var row in candidates)
            {
                token.ThrowIfCancellationRequested();

                var thumbnail =
                    await _iconService.GetThumbnailAsync(
                        row.Target,
                        requestedSize,
                        token);

                if (thumbnail != null)
                    row.Thumbnail = thumbnail;
            }
        }

        private static bool IsThumbnailImagePath(
            string? path)
        {
            var extension =
                Path.GetExtension(path ?? string.Empty)
                    .ToLowerInvariant();

            return extension is
                ".png" or ".jpg" or ".jpeg" or
                ".webp" or ".gif" or ".bmp";
        }

        private void ResultsThumbnailGrid_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (ResultsThumbnailGrid.SelectedItem is
                not SearchResultRow row)
            {
                return;
            }

            ResultsList.SelectedItem = row;
        }

        private void ResultsThumbnailGrid_DoubleTapped(
            object sender,
            DoubleTappedRoutedEventArgs e)
        {
            if (ResultsThumbnailGrid.SelectedItem is
                not SearchResultRow row)
            {
                return;
            }

            ResultsList.SelectedItem = row;
            ResultsList_DoubleTapped(
                ResultsList,
                e);
        }

        private void ResultsThumbnailGrid_RightTapped(
            object sender,
            RightTappedRoutedEventArgs e)
        {
            if (ResultsThumbnailGrid.SelectedItem is
                SearchResultRow row)
            {
                ResultsList.SelectedItem = row;
            }

            ResultsContextFlyout.ShowAt(
                ResultsThumbnailGrid,
                e.GetPosition(
                    ResultsThumbnailGrid));

            e.Handled = true;
        }

        #endregion

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
                    "70" => 0.70,
                    "80" => 0.80,
                    "90" => 0.90,
                    "110" => 1.10,
                    "120" => 1.20,
                    "130" => 1.30,
                    "140" => 1.40,
                    "150" => 1.50,
                    "small" => 0.90,
                    "large" => 1.20,
                    _ => 1.00
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

                // Regla global de apertura de Resultados:
                // Date Modified descendente para Notion, Dropbox y Todo.
                _sortKey = "mod_desc";

                var savedGroupingMode =
                    (values[LS_ResultGroupingMode] as string ?? "none")
                    .Trim()
                    .ToLowerInvariant();

                _resultGroupingMode = savedGroupingMode switch
                {
                    "domain" => ResultGroupingMode.Domain,
                    "domain_nobilling" => ResultGroupingMode.DomainNoBilling,
                    "month" => ResultGroupingMode.Month,
                    "name" => ResultGroupingMode.Name,
                    "area" => ResultGroupingMode.Area,
                    "area_nobilling" => ResultGroupingMode.AreaNoBilling,
                    _ => ResultGroupingMode.None
                };

                SelectComboItemByTag(
                    GroupResultsCombo,
                    savedGroupingMode);

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
                "70" => 0.70,
                "80" => 0.80,
                "90" => 0.90,
                "110" => 1.10,
                "120" => 1.20,
                "130" => 1.30,
                "140" => 1.40,
                "150" => 1.50,
                "small" => 0.90,
                "large" => 1.20,
                _ => 1.00
            };

            ApplicationData.Current.LocalSettings.Values[LS_SearchTextScale] = key;
            ApplyTextScaleToVisualTree();
        }

        private void ResultsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer != null)
            {
                if (Math.Abs(_textScale - 1.0) > 0.001)
                {
                    ApplyTextScaleRecursive(args.ItemContainer);
                }
                ApplyResultColumnWidthsRecursive(args.ItemContainer);
            }
        }

        private void ResultsThumbnailGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer != null)
            {
                ApplyTextScaleRecursive(args.ItemContainer);
            }
        }

        private void ApplyTextScaleToVisualTree()
        {
            if (RootLayout == null)
                return;

            ApplyTextScaleRecursive(RootLayout);

            if (QuickCommandsInputHost != null)
                ApplyTextScaleRecursive(QuickCommandsInputHost);

            if (ResultsList != null)
            {
                ApplyTextScaleRecursive(ResultsList);
                for (int i = 0; i < ResultsList.Items.Count; i++)
                {
                    if (ResultsList.ContainerFromIndex(i) is DependencyObject container)
                    {
                        ApplyTextScaleRecursive(container);
                    }
                }
            }

            if (ResultsThumbnailGrid != null)
            {
                ApplyTextScaleRecursive(ResultsThumbnailGrid);
                for (int i = 0; i < ResultsThumbnailGrid.Items.Count; i++)
                {
                    if (ResultsThumbnailGrid.ContainerFromIndex(i) is DependencyObject container)
                    {
                        ApplyTextScaleRecursive(container);
                    }
                }
            }
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

        #region ===== Details pane width =====

        private void LoadDetailsPaneWidth()
        {
            if (DetailsCol == null)
                return;

            var width = ReadColumnWidth(
                LS_DetailsPaneWidth,
                DETAILS_PANE_DEFAULT,
                DETAILS_PANE_MIN,
                DETAILS_PANE_MAX);

            DetailsCol.Width = new GridLength(width);
        }

        private void DetailsPaneSplitter_PointerEntered(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (sender is Thumb thumb)
                thumb.Background = new SolidColorBrush(
                    Color.FromArgb(70, 255, 255, 255));
        }

        private void DetailsPaneSplitter_PointerExited(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (sender is Thumb thumb)
                thumb.Background = new SolidColorBrush(
                    Color.FromArgb(31, 255, 255, 255));
        }

        private void DetailsPaneSplitter_DragDelta(
            object sender,
            DragDeltaEventArgs e)
        {
            if (DetailsCol == null)
                return;

            // El panel está a la derecha:
            // mover el separador a la izquierda aumenta su ancho.
            var currentWidth =
                DetailsCol.ActualWidth > 0
                    ? DetailsCol.ActualWidth
                    : DetailsCol.Width.Value;

            var newWidth = Math.Clamp(
                currentWidth - e.HorizontalChange,
                DETAILS_PANE_MIN,
                DETAILS_PANE_MAX);

            DetailsCol.Width =
                new GridLength(newWidth);
        }

        private void DetailsPaneSplitter_DragCompleted(
            object sender,
            DragCompletedEventArgs e)
        {
            if (DetailsCol == null)
                return;

            var width = Math.Clamp(
                DetailsCol.ActualWidth,
                DETAILS_PANE_MIN,
                DETAILS_PANE_MAX);

            DetailsCol.Width = new GridLength(width);

            ApplicationData.Current.LocalSettings.Values[
                LS_DetailsPaneWidth] = width;
        }

        #endregion

        #region ===== Result column widths & visibility =====

        private void LoadResultColumnWidths()
        {
            if (HeaderPathColumn == null ||
                HeaderStatusColumn == null ||
                HeaderScheduledDateColumn == null ||
                HeaderDateColumn == null)
                return;

            var values = ApplicationData.Current.LocalSettings.Values;

            _savedPathWidth = ReadColumnWidth(LS_ResultPathColumnWidth, 150, RESULT_PATH_MIN, RESULT_PATH_MAX);
            _savedStatusWidth = ReadColumnWidth(LS_ResultStatusColumnWidth, 180, RESULT_STATUS_MIN, RESULT_STATUS_MAX);
            _savedScheduledDateWidth = ReadColumnWidth(LS_ResultScheduledDateColumnWidth, 155, RESULT_SCHEDULED_DATE_MIN, RESULT_SCHEDULED_DATE_MAX);
            _savedDateWidth = ReadColumnWidth(LS_ResultDateColumnWidth, 145, RESULT_DATE_MIN, RESULT_DATE_MAX);

            _colPathVisible = values.TryGetValue(LS_ResultColPathVisible, out var pv) && pv is bool pb ? pb : true;
            _colStatusVisible = values.TryGetValue(LS_ResultColStatusVisible, out var sv) && sv is bool sb ? sb : true;
            _colScheduledDateVisible = values.TryGetValue(LS_ResultColScheduledDateVisible, out var sdv) && sdv is bool sdb ? sdb : true;
            _colDateVisible = values.TryGetValue(LS_ResultColDateVisible, out var dv) && dv is bool db ? db : true;

            UpdateColumnVisibilityFlyoutChecks();
            ApplyColumnVisibilityToHeaders();

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

        private void ApplyColumnVisibilityToHeaders()
        {
            if (HeaderPathColumn == null || HeaderStatusColumn == null ||
                HeaderScheduledDateColumn == null || HeaderDateColumn == null)
                return;

            HeaderPathColumn.Width = _colPathVisible ? new GridLength(_savedPathWidth) : new GridLength(0);
            HeaderStatusColumn.Width = _colStatusVisible ? new GridLength(_savedStatusWidth) : new GridLength(0);
            HeaderScheduledDateColumn.Width = _colScheduledDateVisible ? new GridLength(_savedScheduledDateWidth) : new GridLength(0);
            HeaderDateColumn.Width = _colDateVisible ? new GridLength(_savedDateWidth) : new GridLength(0);

            if (HeaderPathTextBlock != null) HeaderPathTextBlock.Visibility = _colPathVisible ? Visibility.Visible : Visibility.Collapsed;
            if (PathNameSplitter != null) PathNameSplitter.Visibility = _colPathVisible ? Visibility.Visible : Visibility.Collapsed;

            if (HeaderStatusTextBlock != null) HeaderStatusTextBlock.Visibility = _colStatusVisible ? Visibility.Visible : Visibility.Collapsed;
            if (NameStatusSplitter != null) NameStatusSplitter.Visibility = _colStatusVisible ? Visibility.Visible : Visibility.Collapsed;

            if (HeaderScheduledDateSortButton != null) HeaderScheduledDateSortButton.Visibility = _colScheduledDateVisible ? Visibility.Visible : Visibility.Collapsed;
            if (StatusDateSplitter != null) StatusDateSplitter.Visibility = _colScheduledDateVisible ? Visibility.Visible : Visibility.Collapsed;

            if (HeaderModifiedSortButton != null) HeaderModifiedSortButton.Visibility = _colDateVisible ? Visibility.Visible : Visibility.Collapsed;
            if (ScheduledDateDateSplitter != null) ScheduledDateDateSplitter.Visibility = _colDateVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateColumnVisibilityFlyoutChecks()
        {
            if (ColTogglePath != null) ColTogglePath.IsChecked = _colPathVisible;
            if (ColToggleStatus != null) ColToggleStatus.IsChecked = _colStatusVisible;
            if (ColToggleScheduledDate != null) ColToggleScheduledDate.IsChecked = _colScheduledDateVisible;
            if (ColToggleDate != null) ColToggleDate.IsChecked = _colDateVisible;
        }

        private void ColTogglePath_Click(object sender, RoutedEventArgs e)
        {
            _colPathVisible = ColTogglePath?.IsChecked ?? !_colPathVisible;
            ApplyColumnVisibilityToHeaders();
            SaveResultColumnWidths();
            ApplyResultColumnWidthsToVisualTree();
        }

        private void ColToggleStatus_Click(object sender, RoutedEventArgs e)
        {
            _colStatusVisible = ColToggleStatus?.IsChecked ?? !_colStatusVisible;
            ApplyColumnVisibilityToHeaders();
            SaveResultColumnWidths();
            ApplyResultColumnWidthsToVisualTree();
        }

        private void ColToggleScheduledDate_Click(object sender, RoutedEventArgs e)
        {
            _colScheduledDateVisible = ColToggleScheduledDate?.IsChecked ?? !_colScheduledDateVisible;
            ApplyColumnVisibilityToHeaders();
            SaveResultColumnWidths();
            ApplyResultColumnWidthsToVisualTree();
        }

        private void ColToggleDate_Click(object sender, RoutedEventArgs e)
        {
            _colDateVisible = ColToggleDate?.IsChecked ?? !_colDateVisible;
            ApplyColumnVisibilityToHeaders();
            SaveResultColumnWidths();
            ApplyResultColumnWidthsToVisualTree();
        }

        private void ColMaximizeName_Click(object sender, RoutedEventArgs e)
        {
            // Oculta todas las columnas secundarias para que el Nombre ocupe el 100%
            _colPathVisible = false;
            _colStatusVisible = false;
            _colScheduledDateVisible = false;
            _colDateVisible = false;
            UpdateColumnVisibilityFlyoutChecks();
            ApplyColumnVisibilityToHeaders();
            SaveResultColumnWidths();
            ApplyResultColumnWidthsToVisualTree();
            StatusText.Text = "Estado: Columna Nombre maximizada al 100% ✅";
        }

        private void ColResetAll_Click(object sender, RoutedEventArgs e)
        {
            _colPathVisible = true;
            _colStatusVisible = true;
            _colScheduledDateVisible = true;
            _colDateVisible = true;
            _savedPathWidth = 150;
            _savedStatusWidth = 180;
            _savedScheduledDateWidth = 155;
            _savedDateWidth = 145;
            UpdateColumnVisibilityFlyoutChecks();
            ApplyColumnVisibilityToHeaders();
            SaveResultColumnWidths();
            ApplyResultColumnWidthsToVisualTree();
            StatusText.Text = "Estado: Columnas restablecidas por defecto ✅";
        }

        private void PathNameSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_colPathVisible) return;
            var current = HeaderPathColumn.Width.Value;
            var next = Math.Clamp(current + e.HorizontalChange, RESULT_PATH_MIN, RESULT_PATH_MAX);
            _savedPathWidth = next;
            HeaderPathColumn.Width = new GridLength(next);
            ApplyResultColumnWidthsToVisualTree();
        }

        private void NameStatusSplitter_DragDelta(
            object sender,
            DragDeltaEventArgs e)
        {
            if (!_colStatusVisible) return;
            var current = HeaderStatusColumn.Width.Value;
            var next = Math.Clamp(current - e.HorizontalChange, RESULT_STATUS_MIN, RESULT_STATUS_MAX);
            _savedStatusWidth = next;
            HeaderStatusColumn.Width = new GridLength(next);
            ApplyResultColumnWidthsToVisualTree();
        }

        private void StatusScheduledDateSplitter_DragDelta(
            object sender,
            DragDeltaEventArgs e)
        {
            if (!_colStatusVisible || !_colScheduledDateVisible) return;

            var currentStatus = HeaderStatusColumn.Width.Value;
            var currentScheduled = HeaderScheduledDateColumn.Width.Value;

            var minimumDelta = Math.Max(
                RESULT_STATUS_MIN - currentStatus,
                currentScheduled - RESULT_SCHEDULED_DATE_MAX);

            var maximumDelta = Math.Min(
                RESULT_STATUS_MAX - currentStatus,
                currentScheduled - RESULT_SCHEDULED_DATE_MIN);

            var appliedDelta = Math.Clamp(
                e.HorizontalChange,
                minimumDelta,
                maximumDelta);

            _savedStatusWidth = currentStatus + appliedDelta;
            _savedScheduledDateWidth = currentScheduled - appliedDelta;

            HeaderStatusColumn.Width = new GridLength(_savedStatusWidth);
            HeaderScheduledDateColumn.Width = new GridLength(_savedScheduledDateWidth);

            ApplyResultColumnWidthsToVisualTree();
        }

        private void ScheduledDateDateSplitter_DragDelta(
            object sender,
            DragDeltaEventArgs e)
        {
            if (!_colScheduledDateVisible || !_colDateVisible) return;

            var currentScheduled = HeaderScheduledDateColumn.Width.Value;
            var currentDate = HeaderDateColumn.Width.Value;

            var minimumDelta = Math.Max(
                RESULT_SCHEDULED_DATE_MIN - currentScheduled,
                currentDate - RESULT_DATE_MAX);

            var maximumDelta = Math.Min(
                RESULT_SCHEDULED_DATE_MAX - currentScheduled,
                currentDate - RESULT_DATE_MIN);

            var appliedDelta = Math.Clamp(
                e.HorizontalChange,
                minimumDelta,
                maximumDelta);

            _savedScheduledDateWidth = currentScheduled + appliedDelta;
            _savedDateWidth = currentDate - appliedDelta;

            HeaderScheduledDateColumn.Width = new GridLength(_savedScheduledDateWidth);
            HeaderDateColumn.Width = new GridLength(_savedDateWidth);

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
            if (_colPathVisible && HeaderPathColumn.Width.Value > 0)
            {
                _savedPathWidth = HeaderPathColumn.Width.Value;
                values[LS_ResultPathColumnWidth] = _savedPathWidth;
            }
            if (_colStatusVisible && HeaderStatusColumn.Width.Value > 0)
            {
                _savedStatusWidth = HeaderStatusColumn.Width.Value;
                values[LS_ResultStatusColumnWidth] = _savedStatusWidth;
            }
            if (_colScheduledDateVisible && HeaderScheduledDateColumn.Width.Value > 0)
            {
                _savedScheduledDateWidth = HeaderScheduledDateColumn.Width.Value;
                values[LS_ResultScheduledDateColumnWidth] = _savedScheduledDateWidth;
            }
            if (_colDateVisible && HeaderDateColumn.Width.Value > 0)
            {
                _savedDateWidth = HeaderDateColumn.Width.Value;
                values[LS_ResultDateColumnWidth] = _savedDateWidth;
            }

            values[LS_ResultColPathVisible] = _colPathVisible;
            values[LS_ResultColStatusVisible] = _colStatusVisible;
            values[LS_ResultColScheduledDateVisible] = _colScheduledDateVisible;
            values[LS_ResultColDateVisible] = _colDateVisible;
        }

        private void ApplyResultColumnWidthsToVisualTree()
        {
            if (RootLayout == null ||
                HeaderPathColumn == null ||
                HeaderStatusColumn == null ||
                HeaderScheduledDateColumn == null ||
                HeaderDateColumn == null)
                return;

            ApplyResultColumnWidthsRecursive(RootLayout);
        }

        private void ApplyResultColumnWidthsRecursive(DependencyObject node)
        {
            if (node is Grid grid &&
                string.Equals(grid.Tag?.ToString(), "ResultColumns", StringComparison.Ordinal) &&
                grid.ColumnDefinitions.Count >= 9)
            {
                var pathW = _colPathVisible ? HeaderPathColumn.Width.Value : 0;
                var statusW = _colStatusVisible ? HeaderStatusColumn.Width.Value : 0;
                var schedW = _colScheduledDateVisible ? HeaderScheduledDateColumn.Width.Value : 0;
                var dateW = _colDateVisible ? HeaderDateColumn.Width.Value : 0;

                grid.ColumnDefinitions[0].Width = new GridLength(pathW);
                grid.ColumnDefinitions[1].Width = pathW > 0 ? new GridLength(5) : new GridLength(0);
                grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                grid.ColumnDefinitions[3].Width = statusW > 0 ? new GridLength(5) : new GridLength(0);
                grid.ColumnDefinitions[4].Width = new GridLength(statusW);
                grid.ColumnDefinitions[5].Width = schedW > 0 ? new GridLength(5) : new GridLength(0);
                grid.ColumnDefinitions[6].Width = new GridLength(schedW);
                grid.ColumnDefinitions[7].Width = dateW > 0 ? new GridLength(5) : new GridLength(0);
                grid.ColumnDefinitions[8].Width = new GridLength(dateW);
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
            _calendarThemeColor = bgColor;

            if (CalendarHost != null)
                ApplyCalendarTheme(bgColor);

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
