using Anfeta.UI.Models;
using Anfeta.UI.Models.Search;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Bookmarks;
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
        private ViewMode _mode = ViewMode.Explorer;
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

            _voiceSplitDefaultBg = VoiceSplit.Background;
            _voiceSplitDefaultFg = VoiceSplit.Foreground;

            var sp = App.AppHost.Services;
            _stt = sp.GetRequiredService<ISpeechToTextService>();
            _repo = sp.GetRequiredService<VoiceCommandsRepository>();
            _voiceEngine = sp.GetRequiredService<VoiceCommandEngine>();
            _voiceOrchestrator = sp.GetRequiredService<VoiceSearchOrchestrator>();
            _voicePost = sp.GetRequiredService<IVoicePostActionService>();

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
        }

        private void SearchView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_isIndexStateHooked)
            {
                DropboxIndexCoordinator.StateChanged -= OnIndexStateChanged;
                SearchFocusBridge.FocusRequested -= OnSearchFocusRequested;
                _isIndexStateHooked = false;
            }
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
            ResultsList.ItemsSource = Results;

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
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = $"{Results.Count} resultados";
        }

        #endregion
    }
}