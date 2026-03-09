using Anfeta.UI.Models;
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
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using static Anfeta.UI.Helpers.AppSettingsKeys;



namespace Anfeta.UI.Views
{

    public sealed partial class SearchView : Page,ISearchCommandSink

    {
        #region ===== Fields / Const / Enums =====
        // enums
        private enum ViewMode { Explorer, Bookmarks }
        private ViewMode _mode = ViewMode.Explorer;
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

        // icons/bookmarks
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
        //Extras
        private bool _allowProgrammaticSearch = false;
        private bool _foldersPaneVisible = true;
        //Exclusion
        private const string LS_ExcludedFolders = "ExcludedFolders"; // rutas separadas por |
        private readonly List<string> _excludedFolders = new(); // en memoria 
        private readonly ObservableCollection<string> _excludedFoldersUi = new();
        private const string LS_SavedSearches = "SavedSearches"; // JSON
        //Contraibles
        private const string LS_CommandsExpanded = "CommandsExpanded";
        private const string LS_ExcludedExpanded = "ExcludedExpanded";
        //Utils AutoIndex
        private CancellationTokenSource? _autoReindexCts;
        //ComandoVoz 
        private readonly VoiceCommandsRepository _voiceRepo;
        private readonly VoiceCommandEngine _voiceEngine; 
        private readonly VoiceSearchOrchestrator _voiceOrchestrator;
        private bool _isListening = false;
        private CancellationTokenSource? _voiceCts;
        private readonly ISpeechToTextService _stt;
        private readonly VoiceCommandsRepository _repo;
        // opcional: bandera para evitar doble init
        private bool _voiceInitDone;
        private readonly IVoicePostActionService _voicePost;
        private Brush? _voiceSplitDefaultBg;
        private Brush? _voiceSplitDefaultFg;

        private readonly Brush _voiceActiveBg = new SolidColorBrush(Color.FromArgb(255, 60, 20, 20));  // rojo oscuro suave
        private readonly Brush _voiceActiveFg = new SolidColorBrush(Colors.White);
        //Acciones Click Derecho 
        private readonly SemaphoreSlim _bootstrapLock = new(1, 1);
        private bool _bootstrappedOnce = false;
        private readonly SemaphoreSlim _mutLock = new(1, 1);
        private CancellationTokenSource? _refreshCts;
        private string? _currentFolderPath;
        //Cambio de Pestaña Buscador
        public event EventHandler<string>? TabTitleChanged;
        public event EventHandler? WorkspaceChanged;

        #endregion

        #region ===== Internal Models / Views =====
        private sealed class RowView : AdvancedQueryV3.IItemView
        {
            private readonly SearchResultRow _x;
            public RowView(SearchResultRow x) => _x = x;

            public string? Name => _x.Name;
            public string? Path => _x.Target;

            public string? Folder =>
                System.IO.Path.GetDirectoryName(_x.Target ?? "");

            public string? Extension =>
                System.IO.Path.GetExtension(_x.Target ?? "").TrimStart('.');

            public string? Type =>
                string.Equals(_x.Type, "FOLDER", StringComparison.OrdinalIgnoreCase) ? "folder" : "file";

            public long SizeBytes => _x.Size;

            public DateTime ModifiedLocalDate => ParseServerModified(_x.ServerModified);

            public int DaysModified =>
                ModifiedLocalDate == DateTime.MinValue
                    ? int.MaxValue
                    : (int)(DateTime.Now.Date - ModifiedLocalDate.Date).TotalDays;

            public string? SearchText => $"{_x.Name} {_x.Target}";

            private static DateTime ParseServerModified(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;

                // Dropbox suele traer ISO 8601: 2026-01-28T08:...
                if (DateTime.TryParse(
                    s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal,
                    out var dt))
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

            public string? Folder =>
                System.IO.Path.GetDirectoryName(_b.LocalPath ?? "");

            public string? Extension =>
                System.IO.Path.GetExtension(_b.LocalPath ?? _b.Title ?? "").TrimStart('.');

            public string? Type =>
                string.Equals(_b.Type, "FOLDER", StringComparison.OrdinalIgnoreCase) ? "folder" : "file";

            public long SizeBytes => _b.Size;

            public DateTime ModifiedLocalDate => ParseDate(_b.Modified);

            public int DaysModified =>
                ModifiedLocalDate == DateTime.MinValue
                    ? int.MaxValue
                    : (int)(DateTime.Now.Date - ModifiedLocalDate.Date).TotalDays;

            public string? SearchText => $"{_b.Title} {_b.LocalPath}";

            private static DateTime ParseDate(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;

                if (DateTime.TryParse(
                    s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal,
                    out var dt))
                    return dt;

                return DateTime.MinValue;
            }
        }
        private sealed class FolderNode
        {
            public string Name { get; set; } = "";
            public string FullPath { get; set; } = "";

            // MUY IMPORTANTE: ObservableCollection sin setter
            public ObservableCollection<FolderNode> Children { get; } = new();

            // Para lazy load real
            public bool HasDummyChild { get; set; } = false;
            public bool IsLoaded { get; set; } = false;
        }
        #endregion
        #region ===== Constructor / Lifecycle =====
        public SearchView()
        {
            InitializeComponent();
            DropboxIndexCoordinator.StateChanged += OnIndexStateChanged;
            Unloaded += (_, __) => DropboxIndexCoordinator.StateChanged -= OnIndexStateChanged;

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

            // opcional: selecciona default de tamaño de página visual
            // PageSizeCombo.SelectedIndex = 1; // 50 
            _ = LoadBookmarksOnStartAsync();

            async Task LoadBookmarksOnStartAsync()
            {
                _bookmarks = await _bookmarksService.LoadAsync(CancellationToken.None);
            }
        }

        private async void SearchView_Loaded(object sender, RoutedEventArgs e)
        {
            LoadExcludedFolders();
            RefreshExcludedFoldersUi();
            LoadSavedSearches();
            CommandsSidebarList.ItemsSource = _savedSearches;
            RefreshCommandsSidebarUi();
            LoadSidebarExpandedStates();

            if (!_voiceInitDone)
            {
                _voiceInitDone = true;
                await _voiceEngine.ReloadAsync();
            }

            // ✅ Bookmarks (no afecta tabs)
            _ = LoadBookmarksAsync(); // fire-and-forget seguro

            // ✅ Bootstrap índice/UI (una sola fuente de verdad)
            await EnsureIndexBootstrappedAsync();
        }
        private async Task EnsureIndexBootstrappedAsync()
        {
            await _bootstrapLock.WaitAsync();
            try
            {
                // Si ya lo hicimos en esta instancia y hay data, no re-hacer
                if (_bootstrappedOnce && App.LocalIndex.HasData)
                    return;

                // 1) Leer ruta guardada
                var saved = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;
                var hasValidDropboxRoot = !string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved);

                if (!hasValidDropboxRoot)
                {
                    DROPBOX_ROOT = "";
                    ResetSearchModuleState();
                    StatusText.Text = $"Estado: Bookmarks ✅ ({_bookmarks.Count}) | Configura la ruta en Settings.";
                    _bootstrappedOnce = true;
                    return;
                }

                DROPBOX_ROOT = saved!.Trim();

                // 2) Si no hay índice en memoria, intenta cargarlo desde disco (solo si NO estamos indexando)
                if (!App.LocalIndex.HasData && !DropboxIndexCoordinator.IsIndexing)
                {
                    var (ok, cachedRoot, items) = await LocalIndexPersistence.TryLoadAsync(CancellationToken.None);

                    var cacheMatchesRoot =
                        ok &&
                        !string.IsNullOrWhiteSpace(cachedRoot) &&
                        string.Equals(cachedRoot.Trim(), DROPBOX_ROOT, StringComparison.OrdinalIgnoreCase) &&
                        LocalIndexPersistence.RootExists(DROPBOX_ROOT) &&
                        items != null &&
                        items.Count > 0;

                    if (cacheMatchesRoot)
                    {
                        App.LocalIndex.Set(items);
                        DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);
                    }
                }

                // 3) Auto-reindex (solo si ya hay índice y no está indexando)
                if (App.LocalIndex.HasData && !DropboxIndexCoordinator.IsIndexing)
                {
                    var lastIndexedStr =
                        ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] as string;

                    DateTimeOffset? lastIndexedUtc = null;
                    if (!string.IsNullOrWhiteSpace(lastIndexedStr) &&
                        DateTimeOffset.TryParse(lastIndexedStr, out var parsed))
                    {
                        lastIndexedUtc = parsed.ToUniversalTime();
                    }

                    var folderLastWriteUtc = Directory.GetLastWriteTimeUtc(DROPBOX_ROOT);

                    var shouldReindex =
                        lastIndexedUtc == null ||
                        folderLastWriteUtc > lastIndexedUtc.Value.UtcDateTime;

                    if (shouldReindex)
                    {
                        await ReindexCurrentRootAsync(); // ya lo haremos transaccional abajo
                    }
                }

                // 4) Si no hay índice o está indexando -> UI “vacía controlada”
                if (DropboxIndexCoordinator.IsIndexing || !App.LocalIndex.HasData)
                {
                    ResetSearchModuleState();
                    StatusText.Text = DropboxIndexCoordinator.IsIndexing
                        ? $"Estado: Ruta nueva detectada, indexando…"
                        : $"Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";

                    _bootstrappedOnce = true;
                    return;
                }

                // 5) Ya hay índice -> inicializar Explorer (árbol + browse)
                LoadFoldersRoot();
                BuildTreeRoot();

                // ✅ si ya traigo una carpeta restaurada, NO me regreses al root
                var startFolder = (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
                    ? _currentFolderPath
                    : DROPBOX_ROOT;

                await BrowseFolderAsync(startFolder, pushHistory: false);

                CommandsSidebarList.ItemsSource = _savedSearches;
                RefreshCommandsSidebarUi();

                StatusText.Text = $"Estado: Index local listo ✅ ({App.LocalIndex.Count} items)";

                _bootstrappedOnce = true;
            }
            finally
            {
                _bootstrapLock.Release();
            }
        }
        #endregion

        #region ===== Index Coordinator (Auto-index) =====
        private void OnIndexStateChanged()
        {
            // Siempre brinca a UI thread
            DispatcherQueue.TryEnqueue(() =>
            {
                _ = ApplyIndexStateAsync();
            });
        }

        private async Task ApplyIndexStateAsync()
        {
            // 1) Si está indexando => limpiar UI y mostrar mensaje
            if (DropboxIndexCoordinator.IsIndexing)
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }

            // 2) Si hubo error
            if (!string.IsNullOrWhiteSpace(DropboxIndexCoordinator.LastError))
            {
                ResetSearchModuleState();
                StatusText.Text = $"Estado: Error indexando → {DropboxIndexCoordinator.LastError}";
                return;
            }

            // 3) Si está listo y hay índice
            if (DropboxIndexCoordinator.IsReady && App.LocalIndex.HasData)
            {
                var root = DropboxIndexCoordinator.RootPath ?? "";

                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    ResetSearchModuleState();
                    StatusText.Text = "Estado: Ruta inválida. Configura de nuevo en Settings.";
                    return;
                }

                // Actualiza root local del SearchView
                DROPBOX_ROOT = root;

                // Refresca árbol + navega a root
                LoadFoldersRoot();
                BuildTreeRoot();
                // antes: await BrowseFolderAsync(DROPBOX_ROOT, pushHistory:false);

                var startFolder =
                    (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
                        ? _currentFolderPath
                        : DROPBOX_ROOT;

                await BrowseFolderAsync(startFolder, pushHistory: false);

                StatusText.Text = $"Estado: Index local listo ✅ ({App.LocalIndex.Count} items)";
            }
        }
        private async Task ReindexCurrentRootAsync()
        {
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
                return;

            try
            {
                _autoReindexCts?.Cancel();
                _autoReindexCts = new CancellationTokenSource();
                var ct = _autoReindexCts.Token;

                StatusText.Text = "Estado: Detecté cambios en la carpeta. Reindexando…";
                DropboxIndexCoordinator.StartIndexing(DROPBOX_ROOT);

                // Limpiar para evitar resultados viejos mientras reindexa
                App.LocalIndex.Clear();

                // ✅ Construir primero (transaccional)
                var list = await LocalIndexBuilder.BuildAsync(DROPBOX_ROOT, ct);

                // ✅ Nunca sobreescribas con vacío
                if (list == null || list.Count == 0)
                {
                    StatusText.Text = "Estado: Reindex produjo 0 items. Conservo el índice anterior.";
                    DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);
                    return;
                }

                // ✅ Swap atómico en memoria + persistencia
                App.LocalIndex.Set(list);
                await LocalIndexPersistence.SaveAsync(DROPBOX_ROOT, list, ct);

                // ✅ guardar fecha de último indexado
                ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] =
                    DateTimeOffset.UtcNow.ToString("O");

                DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);

                StatusText.Text = $"Estado: Reindex listo ✅ ({App.LocalIndex.Count} items)";
            }
            catch (OperationCanceledException)
            {
                // ok
            }
            catch (Exception ex)
            {
                DropboxIndexCoordinator.MarkError(DROPBOX_ROOT, ex.Message);
                StatusText.Text = $"Estado: Error reindexando → {ex.Message}";
            }
        }
        #endregion

        #region ===== UI Reset / State =====
        private void ResetSearchModuleState()
        {
            CancelPendingSearch();

            _currentFolder = "";
            _backStack.Clear();
            _forwardStack.Clear();

            Results.Clear();
            ResultsList.ItemsSource = Results;

            FolderTree.ItemsSource = new ObservableCollection<FolderNode>();
            EmptyTreeHint.Visibility = Visibility.Visible;

            BreadcrumbText.Text = "/";
            ModeText.Text = "Modo: Explorar (Local)";
            CountText.Text = "0 resultados";
            EmptyResultsHint.Visibility = Visibility.Visible;

            // opcional: limpiar detalles
            DetailsTitle.Text = "Selecciona un elemento";
            DetailsPath.Text = "—";
            DetailsMeta.Text = "—";
            DetailsNotion.Text = "—";
        }
        private void FinishUi()
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;

            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = $"{Results.Count} resultados";
        }
        #endregion

        #region ===== Explorer / Tree / Navigation =====
        private void LoadFoldersRoot()
        {
            if (!Directory.Exists(DROPBOX_ROOT))
                throw new Exception($"No existe la ruta: {DROPBOX_ROOT}");

            BuildTreeRoot();
        }
        private void BuildTreeRoot()
        {
            _treeRoots.Clear();

            var root = new FolderNode
            {
                Name = Path.GetFileName(DROPBOX_ROOT),
                FullPath = DROPBOX_ROOT,
                HasDummyChild = true
            };

            // dummy para que aparezca la flechita de expand
            root.Children.Add(new FolderNode { Name = "Cargando...", FullPath = "" });

            _treeRoots.Add(root);

            FolderTree.ItemsSource = _treeRoots;
            // ✅ si ya hay root, oculta el hint
            EmptyTreeHint.Visibility = (_treeRoots.Count > 0)
                ? Visibility.Collapsed
                : Visibility.Visible;

        }

        private async Task BrowseFolderAsync(string folder, bool pushHistory = true)
        {
            _currentFolderPath = folder;
            NotifyWorkspaceChanged();
            // Si hay texto de búsqueda, el tab debe reflejar la búsqueda, no la carpeta.
            var q = (SearchBox?.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                SetTabTitle(q);
            }
            else
            {
                SetTabTitle(Path.GetFileName(folder.TrimEnd('\\')));
            }
            if (!Directory.Exists(folder))
            {
                StatusText.Text = "Estado: Carpeta no existe";
                return;
            }
            // ✅ Entramos a modo Explorer (browse)
            _mode = ViewMode.Explorer;
            _isBrowsing = true;
            _onlyBookmarks = false;
            _onlyFolders = false;
            _extFilter = null;

            // historial
            if (pushHistory)
            {
                if (!string.IsNullOrWhiteSpace(_currentFolder) &&
                    !string.Equals(_currentFolder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    _backStack.Push(_currentFolder);
                    _forwardStack.Clear();
                }
            }

            _currentFolder = folder;

            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            // ✅ Si la carpeta está excluida, no la navegues
            if (IsExcludedPath(folder))
            {
                Results.Clear();
                ResultsList.ItemsSource = Results;

                BreadcrumbText.Text = "Ruta: (excluida)";
                CountText.Text = "0 resultados";
                EmptyResultsHint.Visibility = Visibility.Visible;

                StatusText.Text = "Estado: Esta carpeta está excluida";
                return;
            }

            try
            {
                var pretty = folder.Equals(DROPBOX_ROOT, StringComparison.OrdinalIgnoreCase)
                    ? "/"
                    : folder.Replace(DROPBOX_ROOT, "").Replace("\\", "/");

                BreadcrumbText.Text = $"Ruta: {pretty}";
                ModeText.Text = "Modo: Explorar (Local)";

                // ✅ LIMPIAR + FORZAR REFRESH (evita “filas fantasma”)
                Results.Clear();
                ResultsList.ItemsSource = null;

                // -------------------------
                // Carpetas primero
                // -------------------------
                IEnumerable<string> dirs;
                try
                {
                    dirs = Directory.EnumerateDirectories(folder);
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: No pude leer carpetas → {ex.Message}";
                    return;
                }

                foreach (var dir in dirs)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    // ✅ excluir carpetas seleccionadas por el usuario
                    if (IsExcludedPath(dir))
                        continue;
                    string name;
                    try
                    {
                        name = new DirectoryInfo(dir).Name;
                    }
                    catch
                    {
                        name = "";
                    }

                    // ✅ filtra solo basura REAL (no filtres "_")
                    if (string.IsNullOrWhiteSpace(name) || name == "—")
                    {
                        System.Diagnostics.Debug.WriteLine($"[BROWSE][FOLDER] SKIP Name='{name}' Target='{dir}'");
                        continue;
                    }

                    var row = new SearchResultRow
                    {
                        Name = name,
                        Target = dir,
                        Type = "FOLDER",
                        Source = SearchSource.Local
                    };

                    row.Icon = _iconService.GetIcon(row.Type, row.Target);

                    Results.Add(row);
                }

                // -------------------------
                // Archivos
                // -------------------------
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder);
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: No pude leer archivos → {ex.Message}";
                    return;
                }

                foreach (var file in files)
                {
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    if (IsExcludedPath(file))
                        continue;

                    FileInfo fi;
                    try
                    {
                        fi = new FileInfo(file);
                    }
                    catch
                    {
                        continue;
                    }

                    var name = fi.Name ?? "";

                    // ✅ filtra solo basura REAL
                    if (string.IsNullOrWhiteSpace(name) || name == "—")
                    {
                        System.Diagnostics.Debug.WriteLine($"[BROWSE][FILE] SKIP Name='{name}' Target='{file}'");
                        continue;
                    }

                    var row = new SearchResultRow
                    {
                        Name = name,
                        Target = file,
                        Type = "FILE",
                        Size = fi.Exists ? fi.Length : 0,
                        ServerModified = fi.Exists ? fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") : "",
                        Source = SearchSource.Local
                    };

                    row.Icon = _iconService.GetIcon(row.Type, row.Target);

                    Results.Add(row);
                }

                // ✅ REASIGNAR SOURCE para que WinUI no recicle mal los containers
                ResultsList.ItemsSource = Results;

                CountText.Text = $"{Results.Count} resultados";
                EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                StatusText.Text = "Estado: Carpeta cargada ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error → {ex.Message}";
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }

            await Task.CompletedTask;
        }

        private async void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            if (args.Item is not FolderNode node) return;

            if (node.IsLoaded) return;
            if (!node.HasDummyChild) return;

            node.Children.Clear();
            node.HasDummyChild = false;

            await Task.Run(() =>
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(node.FullPath))
                    {
                        var child = new FolderNode
                        {
                            Name = Path.GetFileName(dir),
                            FullPath = dir
                        };

                        try
                        {
                            if (Directory.EnumerateDirectories(dir).Any())
                            {
                                child.HasDummyChild = true;
                                child.Children.Add(new FolderNode { Name = "Cargando...", FullPath = "" });
                            }
                        }
                        catch { }

                        DispatcherQueue.TryEnqueue(() => node.Children.Add(child));
                    }

                    DispatcherQueue.TryEnqueue(() => node.IsLoaded = true);
                }
                catch { }
            });
        }

        private async void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is not FolderNode node) return;
            if (string.IsNullOrWhiteSpace(node.FullPath)) return;

            CancelPendingSearch();

            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                SearchBox.Text = "";

            await BrowseFolderAsync(node.FullPath);
        }

        private async void BtnPrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_backStack.Count == 0) return;

            var prev = _backStack.Pop();
            _forwardStack.Push(_currentFolder);
            await BrowseFolderAsync(prev, pushHistory: false);
        }

        private async void BtnNextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_forwardStack.Count == 0) return;

            var next = _forwardStack.Pop();
            _backStack.Push(_currentFolder);
            await BrowseFolderAsync(next, pushHistory: false);
        }
        private async void BtnRefreshTree_Click(object sender, RoutedEventArgs e)
        {
            // 1) Si está indexando, no borres todo, solo informa
            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }

            // 2) Si no hay índice, no limpies UI a lo bestia
            if (!App.LocalIndex.HasData)
            {
                StatusText.Text = "Estado: “No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                return;
            }

            // 3) Asegura root válido
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: Ruta inválida. Configura de nuevo en Settings.";
                return;
            }

            try
            {
                BuildTreeRoot();
                StatusText.Text = "Estado: Árbol actualizado ✅";

                if (_isBrowsing && !string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
                    await BrowseFolderAsync(_currentFolder, pushHistory: false);
                else
                    await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error → {ex.Message}";
            }
        }


        private async void BtnGoRoot_Click(object sender, RoutedEventArgs e)
        {
            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }

            if (!App.LocalIndex.HasData)
            {
                StatusText.Text = "Estado: “No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                return;
            }

            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: Ruta inválida. Configura de nuevo en Settings.";
                return;
            }

            await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);
        }
        private void ToggleFoldersPane_Click(object sender, RoutedEventArgs e)
        {
            _foldersPaneVisible = ToggleFoldersPane.IsChecked == true;

            if (_foldersPaneVisible)
            {
                FoldersPane.Visibility = Visibility.Visible;
                FoldersPaneCol.Width = new GridLength(320);
            }
            else
            {
                FoldersPane.Visibility = Visibility.Collapsed;
                FoldersPaneCol.Width = new GridLength(0);
            }
        }
        private void ToggleDetailsPane_Click(object sender, RoutedEventArgs e)
        {
            var show = ToggleDetailsPane.IsChecked == true;

            if (show)
            {
                DetailsPane.Visibility = Visibility.Visible;
                DetailsCol.Width = new GridLength(340);
            }
            else
            {
                DetailsPane.Visibility = Visibility.Collapsed;
                DetailsCol.Width = new GridLength(0);
            }
        }
        #endregion

        #region ===== Search (Everything-like) =====
        private void EnsureSearchDebounce()
        {
            if (_searchDebounceTimer != null) return;

            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            _searchDebounceTimer.Tick += async (_, __) =>
            {
                _searchDebounceTimer.Stop();

                var q = (SearchBox?.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(q)) return;
                if (!App.LocalIndex.HasData) return;

                _searchCts?.Cancel();
                _searchCts = new CancellationTokenSource();
                var token = _searchCts.Token;

                try
                {
                    await RunLocalSearchAsync(q, token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: Error buscando → {ex.Message}";
                }
            };
        }
        private void CancelPendingSearch()
        {
            _searchDebounceTimer?.Stop();
            _searchCts?.Cancel();
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput && !_allowProgrammaticSearch)
                return;


            EnsureSearchDebounce();

            var q = (sender.Text ?? "").Trim();

            // vacío -> volver a explorer
            if (string.IsNullOrWhiteSpace(q))
            {
                CancelPendingSearch();

                if (DropboxIndexCoordinator.IsIndexing)
                {
                    StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                    return;
                }

                if (!App.LocalIndex.HasData)
                {
                    ResetSearchModuleState();
                    StatusText.Text = "Estado:No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                    return;
                }

                _mode = ViewMode.Explorer;
                ModeText.Text = "Modo: Explorar (Local)";

                var folderToShow =
                    (!string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
                        ? _currentFolder
                        : DROPBOX_ROOT;

                if (!string.IsNullOrWhiteSpace(folderToShow) && Directory.Exists(folderToShow))
                    _ = BrowseFolderAsync(folderToShow, pushHistory: false);

                return;
            }

            // si hay texto -> buscar, pero solo si hay índice
            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }
            SetTabTitle(SearchBox.Text);
            if (!App.LocalIndex.HasData)
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: “No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";

                return;
            }
            NotifyWorkspaceChanged();
            _searchDebounceTimer!.Stop();
            _searchDebounceTimer.Start();
        }
        private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            await RunSearchAsync(sender.Text ?? "");

        }
        private async Task RunSearchAsync(string query)
        {
            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }

            if (!App.LocalIndex.HasData)
            {
                StatusText.Text = "Estado: “No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                return;
            }
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            try
            {
                BreadcrumbText.Text = string.IsNullOrWhiteSpace(query) ? DROPBOX_ROOT : $"Buscar: {query}";
                ModeText.Text = "Modo: Buscar (Local)";
                await RunLocalSearchAsync(query);
                StatusText.Text = "Estado: Búsqueda local ✅";
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }
        private async Task RunLocalSearchAsync(string query)
        {
            _mode = ViewMode.Explorer;
            Results.Clear();

            var rawQuery = (query ?? "").Trim();
            IEnumerable<SearchResultRow> items = App.LocalIndex.GetAll();
            items = items.Where(x => !IsExcludedPath(x.Target));
            var parsed = AdvancedQueryV3.Parse(rawQuery);
            UpdateHighlightTerms(rawQuery, parsed);

            if (string.IsNullOrWhiteSpace(rawQuery))
            {
                // nada
            }
            else
            {
                if (rawQuery == "-")
                    return;

                if (!LooksAdvanced(rawQuery))
                {
                    var q = rawQuery.ToLowerInvariant();
                    items = items.Where(x =>
                    {
                        var name = (x.Name ?? "").ToLowerInvariant();
                        var target = (x.Target ?? "").ToLowerInvariant();
                        return name.Contains(q) || target.Contains(q);
                    });
                }
                else
                {
                    // ✅ AQUÍ YA NO vuelvas a parsear
                    // 1) Expr lógico
                    items = items.Where(x => AdvancedQueryV3.Evaluate(parsed.Expr, new RowView(x)));

                    // 2) PLAN (B: no tocar UI)
                    if (!string.IsNullOrWhiteSpace(parsed.Plan.FolderContains))
                    {
                        var f = parsed.Plan.FolderContains.ToLowerInvariant();
                        items = items.Where(x => (x.Target ?? "").ToLowerInvariant().Contains(f));
                    }

                    if (parsed.Plan.OnlyFolders.HasValue)
                    {
                        var wantFolder = parsed.Plan.OnlyFolders.Value;
                        items = items.Where(x =>
                            wantFolder
                                ? (x.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase)
                                : (x.Type ?? "").Equals("FILE", StringComparison.OrdinalIgnoreCase));
                    }

                    if (!string.IsNullOrWhiteSpace(parsed.Plan.Ext))
                    {
                        var e = parsed.Plan.Ext;
                        items = items.Where(x =>
                        {
                            var ext = Path.GetExtension(x.Target ?? x.Name ?? "")
                                .TrimStart('.')
                                .ToLowerInvariant();

                            if (e == "img")
                                return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";

                            return ext == e;
                        });
                    }
                }
            }

            if (_onlyFolders)
                items = items.Where(x => (x.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(_extFilter))
            {
                items = items.Where(x =>
                {
                    var ext = Path.GetExtension(x.Target ?? x.Name ?? "")
                        .TrimStart('.')
                        .ToLowerInvariant();

                    if (_extFilter == "img")
                        return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";

                    return ext == _extFilter;
                });
            }

            items = _sortKey switch
            {
                "name_desc" => items.OrderByDescending(x => x.Name),
                _ => items.OrderBy(x => x.Name)
            };

            foreach (var it in items.Take(500))
            {
                it.IsBookmarked = _bookmarksService.Exists(_bookmarks, it.Target);

                // 🔥 ICONO SIEMPRE
                it.Icon ??= _iconService.GetIcon(it.Type, it.Target);
                
                Results.Add(it);
            }

            // ✅ refresca templates (para highlight)
            ResultsList.ItemsSource = null;
            ResultsList.ItemsSource = Results;

            CountText.Text = $"{Results.Count} resultados";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _voicePost.NotifySearchResults(Results);
            await Task.CompletedTask;

        }
        private async Task RunLocalSearchAsync(string query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            _mode = ViewMode.Explorer;
            Results.Clear();

            var rawQuery = (query ?? "").Trim();
            IEnumerable<SearchResultRow> items = App.LocalIndex.GetAll();
            items = items.Where(x => !IsExcludedPath(x.Target));
            token.ThrowIfCancellationRequested();

            var parsed = AdvancedQueryV3.Parse(rawQuery);
            UpdateHighlightTerms(rawQuery, parsed);

            if (!string.IsNullOrWhiteSpace(rawQuery))
            {
                if (rawQuery == "-")
                    return;

                if (!LooksAdvanced(rawQuery))
                {
                    var q = rawQuery.ToLowerInvariant();
                    items = items.Where(x =>
                    {
                        var name = (x.Name ?? "").ToLowerInvariant();
                        var target = (x.Target ?? "").ToLowerInvariant();
                        return name.Contains(q) || target.Contains(q);
                    });
                }
                else
                {
                    items = items.Where(x => AdvancedQueryV3.Evaluate(parsed.Expr, new RowView(x)));

                    if (!string.IsNullOrWhiteSpace(parsed.Plan.FolderContains))
                    {
                        var f = parsed.Plan.FolderContains.ToLowerInvariant();
                        items = items.Where(x => (x.Target ?? "").ToLowerInvariant().Contains(f));
                    }

                    if (parsed.Plan.OnlyFolders.HasValue)
                    {
                        var wantFolder = parsed.Plan.OnlyFolders.Value;
                        items = items.Where(x =>
                            wantFolder
                                ? (x.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase)
                                : (x.Type ?? "").Equals("FILE", StringComparison.OrdinalIgnoreCase));
                    }

                    if (!string.IsNullOrWhiteSpace(parsed.Plan.Ext))
                    {
                        var e = parsed.Plan.Ext;
                        items = items.Where(x =>
                        {
                            var ext = Path.GetExtension(x.Target ?? x.Name ?? "")
                                .TrimStart('.')
                                .ToLowerInvariant();

                            if (e == "img")
                                return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";

                            return ext == e;
                        });
                    }
                }
            }

            if (_onlyFolders)
                items = items.Where(x => (x.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(_extFilter))
            {
                items = items.Where(x =>
                {
                    var ext = Path.GetExtension(x.Target ?? x.Name ?? "")
                        .TrimStart('.')
                        .ToLowerInvariant();

                    if (_extFilter == "img")
                        return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";

                    return ext == _extFilter;
                });
            }

            items = _sortKey switch
            {
                "name_desc" => items.OrderByDescending(x => x.Name),
                _ => items.OrderBy(x => x.Name)
            };

            foreach (var it in items.Take(500))
            {
                token.ThrowIfCancellationRequested();

                it.IsBookmarked = _bookmarksService.Exists(_bookmarks, it.Target);
                it.Icon ??= _iconService.GetIcon(it.Type, it.Target);
                
                Results.Add(it);
            }

            ResultsList.ItemsSource = null;
            ResultsList.ItemsSource = Results;

            CountText.Text = $"{Results.Count} resultados";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _voicePost.NotifySearchResults(Results);
            await Task.CompletedTask;
        }
        #endregion

        #region ===== Filters / Sort =====
        private async void ChipFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton chip)
                return;

            // --- 1) Flags (Bookmarks / Folders) ---
            switch (chip.Name)
            {
                case nameof(ChipBookmarks):
                    _onlyBookmarks = chip.IsChecked == true;
                    break;

                case nameof(ChipFolders):
                    _onlyFolders = chip.IsChecked == true;
                    break;
            }

            // --- 2) Extensión (solo una a la vez) ---
            string? newExt = chip.Name switch
            {
                nameof(ChipPdf) => chip.IsChecked == true ? "pdf" : null,
                nameof(ChipDocx) => chip.IsChecked == true ? "docx" : null,
                nameof(ChipXlsx) => chip.IsChecked == true ? "xlsx" : null,
                nameof(ChipImg) => chip.IsChecked == true ? "img" : null,
                nameof(ChipUrl) => chip.IsChecked == true ? "url" : null,
                _ => _extFilter
            };

            _extFilter = newExt;

            // Apagar SOLO los otros chips de extensión (NO tocar Bookmarks/Folders)
            if (_extFilter != null)
            {
                if (chip.Name != nameof(ChipPdf)) ChipPdf.IsChecked = false;
                if (chip.Name != nameof(ChipDocx)) ChipDocx.IsChecked = false;
                if (chip.Name != nameof(ChipXlsx)) ChipXlsx.IsChecked = false;
                if (chip.Name != nameof(ChipImg)) ChipImg.IsChecked = false;
                if (chip.Name != nameof(ChipUrl)) ChipUrl.IsChecked = false;
            }

            // --- 3) Decide qué pintar según modo ---
            if (_onlyBookmarks)
            {
                await ShowBookmarksAsync();
                FinishUi();
                return;
            }

            await RunLocalSearchAsync(SearchBox.Text ?? "");
            FinishUi();
        }

        private async void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SortCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
                _sortKey = tag;

            if (_onlyBookmarks)
                await ShowBookmarksAsync();
            else
                await RunLocalSearchAsync(SearchBox.Text ?? "");

            FinishUi();
        }
        #endregion
        #region ===== Bookmarks =====
        private async Task ShowBookmarksAsync()
        {
            _mode = ViewMode.Bookmarks;

            Results.Clear();

            var list = _bookmarks ?? new List<BookmarkItem>();
            IEnumerable<BookmarkItem> items = list;
            var rawQuery = (SearchBox?.Text ?? "").Trim();
            var parsed = AdvancedQueryV3.Parse(rawQuery);
            UpdateHighlightTerms(rawQuery, parsed);

            if (!string.IsNullOrWhiteSpace(rawQuery))
            {
                if (!LooksAdvanced(rawQuery))
                {
                    var qq = rawQuery.ToLowerInvariant();
                    items = items.Where(b => (b.Title ?? "").ToLowerInvariant().Contains(qq));
                }
                else
                {

                    items = items.Where(b => AdvancedQueryV3.Evaluate(parsed.Expr, new BookmarkView(b)));

                }
            }
            items = items.Where(b => AdvancedQueryV3.Evaluate(parsed.Expr, new BookmarkView(b)));

            if (!string.IsNullOrWhiteSpace(parsed.Plan.FolderContains))
            {
                var f = parsed.Plan.FolderContains.ToLowerInvariant();
                items = items.Where(b => (b.LocalPath ?? "").ToLowerInvariant().Contains(f));
            }


            // 3) filtros: reutiliza los mismos switches que Explorer
            if (_onlyFolders)
                items = items.Where(b => (b.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(_extFilter))
            {
                items = items.Where(b =>
                {
                    var name = b.Title ?? "";
                    var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

                    if (_extFilter == "img")
                        return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";

                    return ext == _extFilter;
                });
            }

            // 4) sort: mismo sortKey
            items = _sortKey switch
            {
                "name_desc" => items.OrderByDescending(b => b.Title),
                _ => items.OrderBy(b => b.Title)
            };

            // 5) map -> SearchResultRow
            foreach (var b in items)
            {
                var localPath = (b.LocalPath ?? "").Trim();

                var row = new SearchResultRow
                {
                    Name = b.Title ?? "",
                    Target = localPath,
                    Type = b.Type ?? "",
                    Size = b.Size,
                    ServerModified = b.Modified ?? "",
                    Source = b.Source,
                    IsBookmarked = !string.IsNullOrWhiteSpace(localPath)
                    && _bookmarksService.Exists(_bookmarks, localPath)
                };

                row.Icon = _iconService.GetIcon(row.Type, row.Target);
                System.Diagnostics.Debug.WriteLine($"{row.Name} icon={(row.Icon == null ? "NULL" : row.Icon.GetType().Name)}");

                Results.Add(row);

            }

            BreadcrumbText.Text = "Bookmarks";
            ModeText.Text = "Modo: Bookmarks";
            CountText.Text = $"{Results.Count} bookmarks";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            await Task.CompletedTask;
        }
        private async Task LoadBookmarksAsync()
        {
            try
            {
                _bookmarks = await _bookmarksService.LoadAsync(CancellationToken.None);
                StatusText.Text = $"Estado: Bookmarks cargados ✅ ({_bookmarks.Count})";
            }
            catch (Exception ex)
            {
                _bookmarks = new();
                StatusText.Text = $"Estado: Error cargando bookmarks → {ex.Message}";
            }
        }
        private async void BtnStar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            // Usamos Tag="{x:Bind}" en el XAML
            if (btn.Tag is not SearchResultRow row) return;

            // La ÚNICA llave real del bookmark (modo local)
            var path = (row.Target ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var ct = CancellationToken.None;

                // 1) Verificar existencia SIEMPRE con LocalPath
                var exists = _bookmarksService.Exists(_bookmarks, path);

                if (exists)
                {
                    // 2) Quitar bookmark
                    _bookmarksService.RemoveByPath(_bookmarks, path);

                    // 3) Guardar (SaveAsync deduplica)
                    await _bookmarksService.SaveAsync(_bookmarks, ct);

                    // 4) UI inmediata
                    row.IsBookmarked = false;
                    StatusText.Text = "Estado: Bookmark eliminado ⭐❌";
                }
                else
                {
                    // 2) Agregar bookmark
                    _bookmarks.Add(new BookmarkItem
                    {
                        Title = row.Name ?? "",
                        LocalPath = path,              // 🔥 CLAVE ÚNICA
                        Source = row.Source,
                        Type = row.Type ?? "",
                        Size = row.Size,
                        Modified = row.ServerModified ?? "",
                        Folder = "General",
                        CreatedAt = DateTimeOffset.Now
                    });

                    // 3) Guardar (evita duplicados)
                    await _bookmarksService.SaveAsync(_bookmarks, ct);

                    // 4) UI inmediata
                    row.IsBookmarked = true;
                    StatusText.Text = "Estado: Bookmark guardado ⭐✅";
                }

                // 5) Si estás en vista Bookmarks, repinta la lista
                if (_mode == ViewMode.Bookmarks)
                    await ShowBookmarksAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error bookmark → {ex.Message}";
            }
        }
        #endregion
        #region ===== Results / Details / Open =====
        private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultRow row)
                return;

            DetailsTitle.Text = row.Name;
            DetailsPath.Text = row.Target;
            var online = File.Exists(row.Target) && NeedsHydration(row.Target);


            DetailsMeta.Text =
                $"Tipo: {row.Type}\n" +
                $"Estado: {(online ? "Online-only (se descarga al abrir)" : "Disponible local")}\n" +
                $"Tamaño: {(row.Size > 0 ? $"{row.Size / 1024:N0} KB" : "—")}\n" +
                $"Modificado: {(!string.IsNullOrWhiteSpace(row.ServerModified) ? row.ServerModified : "—")}";
            // Notion relacionado (si no tienes aún, déjalo en —)
            DetailsNotion.Text = "—";

            if ((row.Type ?? "").Equals("folder", StringComparison.OrdinalIgnoreCase))
                StatusText.Text = "Estado: Es carpeta (usa acciones de navegación) 📁";
            else
                StatusText.Text = "Estado: Seleccionado ✅";
        }
        private async void ResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;
            if (string.IsNullOrWhiteSpace(row.Target)) return;

            // carpeta -> abrir explorador
            // carpeta -> navegar internamente
            if ((row.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase))
            {
                await BrowseFolderAsync(row.Target);
                StatusText.Text = "Estado: Carpeta abierta 📁";
                return;
            }


            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                // 1) si es online-only → descargar bajo demanda
                if (NeedsHydration(row.Target))
                {
                    StatusText.Text = "Estado: Descargando desde Dropbox… ⬇️";
                    var ok = await EnsureHydratedAsync(row.Target, _cts.Token);

                    if (!ok)
                    {
                        StatusText.Text = "Estado: No se pudo descargar (timeout). Revisa tu conexión/Dropbox.";
                        return;
                    }
                }

                // 2) abrir archivo
                StatusText.Text = "Estado: Abriendo…";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = row.Target,
                    UseShellExecute = true
                });

                StatusText.Text = "Estado: Archivo abierto ✅";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error → {ex.Message}";
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }
        private async Task OpenSelectedAsync()
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;
            if (string.IsNullOrWhiteSpace(row.Target)) return;

            // Carpeta -> abrir Explorador
            if ((row.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = row.Target,
                    UseShellExecute = true
                });
                StatusText.Text = "Estado: Carpeta abierta 📁";
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                if (NeedsHydration(row.Target))
                {
                    StatusText.Text = "Estado: Descargando desde Dropbox… ⬇️";
                    var ok = await EnsureHydratedAsync(row.Target, _cts.Token);
                    if (!ok)
                    {
                        StatusText.Text = "Estado: No se pudo descargar (timeout).";
                        return;
                    }
                }

                StatusText.Text = "Estado: Abriendo…";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = row.Target,
                    UseShellExecute = true
                });

                StatusText.Text = "Estado: Archivo abierto ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error → {ex.Message}";
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }
        private void BtnDetailsLink_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;
            if (string.IsNullOrWhiteSpace(row.Target)) return;

            var path = row.Target;

            // Si es archivo: abrir explorer seleccionando el archivo
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });

                StatusText.Text = "Estado: Mostrando archivo en carpeta 📁";
                return;
            }

            // Si es carpeta: abrir carpeta
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });

                StatusText.Text = "Estado: Carpeta abierta 📁";
                return;
            }

            StatusText.Text = "Estado: No existe en local (pulsa doble tap para descargar) ❗";
        }
        private void ResultsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // El ContextFlyout ya existe en XAML, no hace falta lógica aquí por ahora
        }
        #endregion

        #region ===== Help (Popup) =====
        private void MenuHelp_Click(object sender, RoutedEventArgs e)
        {
            // Toggle
            if (HelpPopup.IsOpen)
            {
                HelpPopup.IsOpen = false;
                return;
            }

            // Inyecta tu UI de ayuda (la que ya hiciste)
            HelpContentHost.Content = BuildHelpContentNav();

            HelpPopup.XamlRoot = this.XamlRoot; // importante en WinUI 3
            HelpPopup.IsOpen = true;
        }
        private void HelpPopupClose_Click(object sender, RoutedEventArgs e)
        {
            HelpPopup.IsOpen = false;
        }

        private UIElement BuildHelpContentNav()
        {
            _helpBodyHost = new ContentControl
            {
                Content = BuildHelpExamples() // default
            };

            var nav = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            nav.Children.Add(MakeNavButton("Ejemplos", () => _helpBodyHost.Content = BuildHelpExamples(), isActive: true));
            nav.Children.Add(MakeNavButton("Operadores", () => _helpBodyHost.Content = BuildHelpOperators()));
            nav.Children.Add(MakeNavButton("Filtros", () => _helpBodyHost.Content = BuildHelpFilters()));
            nav.Children.Add(MakeNavButton("Tips", () => _helpBodyHost.Content = BuildHelpTips()));

            var bodyScroll = new ScrollViewer
            {
                Content = _helpBodyHost,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 8, 12, 0)
            };

            return new StackPanel
            {
                Spacing = 12,
                Children =
  {
      nav,
      bodyScroll
  }
            };
        }
        private ToggleButton MakeNavButton(string text, Action onClick, bool isActive = false)
        {
            var t = new ToggleButton
            {
                Content = text,
                IsChecked = isActive,
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(10),
                MinWidth = 110
            };

            t.Click += (_, __) =>
            {
                // deselecciona los demás (mismo padre)
                if (t.Parent is Panel p)
                {
                    foreach (var c in p.Children)
                        if (c is ToggleButton tb) tb.IsChecked = false;
                }

                t.IsChecked = true;
                onClick();
            };

            return t;
        }

        private UIElement CreateSection(string title, string content)
        {
            return new StackPanel
            {
                Spacing = 6,
                Children =
 {
     new TextBlock
     {
         Text = title,
         FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
     },
     new TextBlock
     {
         Text = content,
         TextWrapping = TextWrapping.Wrap,
         Opacity = 0.85
     }
 }
            };
        }
        private UIElement CreateExampleRow(string example, string? note = null, bool run = true, bool replace = true)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

            var btn = new Button
            {
                Content = example,
                Style = (Style)Application.Current.Resources["DefaultButtonStyle"], // si no existe, quítalo
                HorizontalAlignment = HorizontalAlignment.Left
            };
            btn.Click += (_, __) =>
            {
                // (puedes dejar el flag o quitarlo; ya no es necesario si forzamos)
                if (replace || string.IsNullOrWhiteSpace(SearchBox.Text))
                    SearchBox.Text = example;
                else
                    SearchBox.Text = (SearchBox.Text?.Trim() ?? "") + " " + example;

                SearchBox.Focus(FocusState.Programmatic);

                // ✅ fuerza la búsqueda sin depender de Reason/UserInput
                TriggerSearchFromHelp(SearchBox.Text);

                HelpPopup.IsOpen = false;
            };

            row.Children.Add(btn);

            if (!string.IsNullOrWhiteSpace(note))
            {
                row.Children.Add(new TextBlock
                {
                    Text = note,
                    Opacity = 0.75,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            return row;
        }
        private UIElement CreateTokenChip(string token, string? note = null)
        {
            var btn = new Button
            {
                Content = token,
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(999),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            btn.Click += (_, __) =>
            {
                var cur = (SearchBox.Text ?? "").Trim();

                // Append token de forma limpia
                if (string.IsNullOrWhiteSpace(cur))
                    SearchBox.Text = token;
                else
                    SearchBox.Text = cur + " " + token;

                SearchBox.Focus(FocusState.Programmatic);
                TriggerSearchFromHelp(SearchBox.Text);
            };

            if (string.IsNullOrWhiteSpace(note))
                return btn;

            // chip + texto
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
 {
     btn,
     new TextBlock
     {
         Text = note,
         Opacity = 0.75,
         VerticalAlignment = VerticalAlignment.Center,
         TextWrapping = TextWrapping.Wrap
     }
 }
            };
        }

        private UIElement BuildHelpExamples()
        {
            var stack = new StackPanel { Spacing = 12 };

            stack.Children.Add(CreateSection(
                "Ejemplos rápidos",
                "Toca un ejemplo para colocarlo automáticamente en el buscador:"
            ));

            stack.Children.Add(CreateExampleRow("reporte -SEO", "Busca 'reporte' excluyendo la palabra 'SEO'"));
            stack.Children.Add(CreateExampleRow("factura AND 2026", "Debe contener ambos términos"));
            stack.Children.Add(CreateExampleRow("\"estado de cuenta\"", "Frase exacta entre comillas"));

            return stack;
        }
        private UIElement BuildHelpOperators()
        {
            var stack = new StackPanel { Spacing = 12 };

            stack.Children.Add(CreateSection(
                "Operadores lógicos",
                "Combina términos para refinar la búsqueda:"
            ));

            stack.Children.Add(CreateTokenChip("AND", "Ambos términos deben existir"));
            stack.Children.Add(CreateTokenChip("OR", "Cualquiera de los términos"));
            stack.Children.Add(CreateTokenChip("NOT", "Excluye un término"));
            stack.Children.Add(CreateTokenChip("-SEO", "Forma corta para excluir (NOT SEO)"));
            stack.Children.Add(CreateTokenChip("( A OR B )", "Agrupación con paréntesis"));

            return stack;
        }
        private UIElement BuildHelpFilters()
        {
            var stack = new StackPanel { Spacing = 12 };

            stack.Children.Add(CreateSection(
                "Filtros",
                "Limita los resultados por tipo o ubicación:"
            ));

            stack.Children.Add(CreateTokenChip("ext:pdf", "Archivos PDF"));
            stack.Children.Add(CreateTokenChip("ext:docx", "Documentos Word"));
            stack.Children.Add(CreateTokenChip("ext:xlsx", "Excel"));
            stack.Children.Add(CreateTokenChip("type:folder", "Solo carpetas"));
            stack.Children.Add(CreateTokenChip("folder:finanzas", "Carpetas con ese nombre"));

            return stack;
        }
        private UIElement BuildHelpTips()
        {
            var stack = new StackPanel { Spacing = 12 };

            stack.Children.Add(CreateSection(
                "Tips",
                "Consejos para búsquedas más efectivas:"
            ));

            stack.Children.Add(new TextBlock
            {
                Text = "• Usa comillas para buscar frases exactas.",
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(new TextBlock
            {
                Text = "• Usa -palabra para excluir resultados.",
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(new TextBlock
            {
                Text = "• Combina filtros y operadores para búsquedas avanzadas.",
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(CreateExampleRow(
                "reporte AND febrero -SEO ext:pdf",
                "Ejemplo completo combinando todo"
            ));

            return stack;
        }

        private void TriggerSearchFromHelp(string query)
        {
            EnsureSearchDebounce();

            // Si está indexando o no hay índice, no dispares búsqueda
            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }

            if (!App.LocalIndex.HasData)
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                return;
            }

            // Fuerza el mismo comportamiento que cuando el usuario escribe
            _searchDebounceTimer!.Stop();
            _searchDebounceTimer.Start();
        }
        #endregion

        #region ===== Utils (Highlight / Query / Hydration) =====
        private static bool LooksAdvanced(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return false;

            // Frases, OR con |, paréntesis, NOT con - (lo que ya tenías)
            if (q.Contains('"')) return true;
            if (q.Contains('|')) return true;
            if (q.Contains('(') || q.Contains(')')) return true;

            // Operadores de palabra
            var up = q.ToUpperInvariant();
            if (up.Contains(" AND ") || up.Contains(" OR ") || up.Contains(" NOT ")) return true;

            // Exclude
            if (q.StartsWith("-", StringComparison.Ordinal)) return true;
            if (q.Contains(" -", StringComparison.Ordinal)) return true;

            // ✅ NUEVO: comandos con :
            // (no usamos solo q.Contains(':') porque podría activar por rutas tipo C:\)
            string[] cmd = {
    "ext:", "type:", "folder:", "sort:", "limit:", "page:",
    "size:", "date:", "dm:", "year:", "month:", "name:", "path:", "content:", "id:", "status:", "meta:", "author:", "creator:", "access:", "shared:"
};

            for (int i = 0; i < cmd.Length; i++)
            {
                if (up.Contains(cmd[i].ToUpperInvariant(), StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private void UpdateHighlightTerms(string rawQuery, Anfeta.UI.Services.Search.ParsedQuery parsed)
        {
            rawQuery ??= "";
            rawQuery = rawQuery.Trim();

            if (string.IsNullOrWhiteSpace(rawQuery))
            {
                _highlightTerms = new List<string>();
                return;
            }

            // Si no es avanzado: resalta la cadena completa
            if (!LooksAdvanced(rawQuery))
            {
                _highlightTerms = new List<string> { rawQuery };
                return;
            }

            // Avanzado: extrae TextTerm del AST, ignorando lo negado (NOT)
            var list = new List<string>();
            CollectHighlightTerms(parsed.Expr, list, insideNot: false);

            _highlightTerms = list
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(s => s.Length) // frases ganan a palabras
                .ToList();
        }

        private static void CollectHighlightTerms(
            Anfeta.UI.Services.Search.QNode? node,
            List<string> outList,
            bool insideNot)
        {
            if (node is null) return;

            switch (node)
            {
                case Anfeta.UI.Services.Search.TextTerm t:
                    if (!insideNot)
                        outList.Add(t.Pattern);
                    break;

                case Anfeta.UI.Services.Search.Not n:
                    CollectHighlightTerms(n.X, outList, insideNot: true); // NO resaltar lo negado
                    break;

                case Anfeta.UI.Services.Search.And a:
                    CollectHighlightTerms(a.L, outList, insideNot);
                    CollectHighlightTerms(a.R, outList, insideNot);
                    break;

                case Anfeta.UI.Services.Search.Or o:
                    CollectHighlightTerms(o.L, outList, insideNot);
                    CollectHighlightTerms(o.R, outList, insideNot);
                    break;

                    // FieldTerm y otros: NO se resaltan
            }
        }
        private void ApplyHighlightToTextBlock(Microsoft.UI.Xaml.Controls.TextBlock tb, string text)
        {
            tb.Inlines.Clear();
            text ??= "";

            if (_highlightTerms == null || _highlightTerms.Count == 0 || text.Length == 0)
            {
                tb.Text = text;
                return;
            }

            int i = 0;
            while (i < text.Length)
            {
                int bestIndex = -1;
                string? bestNeedle = null;

                foreach (var n in _highlightTerms)
                {
                    var idx = text.IndexOf(n, i, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;

                    if (bestIndex < 0 || idx < bestIndex)
                    {
                        bestIndex = idx;
                        bestNeedle = n;
                        if (bestIndex == i) break;
                    }
                }

                if (bestIndex < 0 || bestNeedle is null)
                {
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text.Substring(i) });
                    break;
                }

                if (bestIndex > i)
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text.Substring(i, bestIndex - i) });

                tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = text.Substring(bestIndex, bestNeedle.Length),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemControlHighlightAccentBrush"]
                });

                i = bestIndex + bestNeedle.Length;
            }
        }

        private void NameText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is not TextBlock tb) return;

            // args.NewValue es el item real
            if (args.NewValue is SearchResultRow row)
            {
                ApplyHighlightToTextBlock(tb, row.Name ?? "");
            }
            else
            {
                // fallback por seguridad (nunca dejes vacío)
                tb.Text = "";
            }
        }

        private static bool NeedsHydration(string path)
        {
            try
            {
                var attrs = File.GetAttributes(path);
                // OFFLINE / RECALL_* suelen indicar “online-only” o pendiente
                var flags = (int)attrs;
                return (flags & FILE_ATTRIBUTE_OFFLINE) != 0
                    || (flags & FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0
                    || (flags & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0;
            }
            catch
            {
                // Si falla leer atributos, tratamos como “no hidratado”
                return true;
            }
        }
        private async Task<bool> EnsureHydratedAsync(string path, CancellationToken ct)
        {
            // Si ya es local, listo
            if (!NeedsHydration(path))
                return true;

            // 1) “Touch”: leer 1 byte para disparar la descarga (Dropbox Smart Sync)
            try
            {
                // Importante: FileShare.ReadWrite para no pelear con Dropbox
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[1];
                _ = await fs.ReadAsync(buffer, 0, 1, ct);
            }
            catch
            {
                // Puede fallar al inicio mientras “baja”, no importa, seguimos a esperar
            }

            // 2) Esperar hasta que deje de ser placeholder
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int timeoutMs = 120_000; // 2 min (ajústalo)
            const int pollMs = 600;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                // si ya no necesita hidratación y existe tamaño “real” (extra safety)
                if (!NeedsHydration(path))
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists)
                            return true;
                    }
                    catch { /* ignore */ }
                }

                await Task.Delay(pollMs, ct);
            }

            return false; // timeout
        }
        private static string SafeFileName(string fullPath)
        {
            fullPath ??= "";

            // Normaliza separadores y quita slash final
            fullPath = fullPath.Trim().TrimEnd('\\', '/');

            // nombre “normal”
            var name = System.IO.Path.GetFileName(fullPath);

            // fallback: si quedó vacío, usa el path mismo o algo visible
            if (string.IsNullOrWhiteSpace(name))
                name = fullPath;

            return name;
        }
        #endregion
        #region ===== Seccion De Exclusiones ===== 
        private sealed class FolderPickItem
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public bool IsChecked { get; set; }
        }
        public sealed class ExcludeNode : INotifyPropertyChanged
        {
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";

            // ✅ Para compatibilidad con lo que ya tenías (aunque ahora uses TreeViewNode.Children)
            public ObservableCollection<ExcludeNode> Children { get; } = new();

            private bool _hasDummyChild;
            public bool HasDummyChild
            {
                get => _hasDummyChild;
                set { if (_hasDummyChild != value) { _hasDummyChild = value; OnPropertyChanged(); } }
            }

            private bool _isChecked;
            public bool IsChecked
            {
                get => _isChecked;
                set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
            }

            private bool _isEnabled = true;
            public bool IsEnabled
            {
                get => _isEnabled;
                set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
            }

            private bool _isLoaded;
            public bool IsLoaded
            {
                get => _isLoaded;
                set { if (_isLoaded != value) { _isLoaded = value; OnPropertyChanged(); } }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        private static TreeViewNode? FindNodeByData(IList<TreeViewNode> nodes, ExcludeNode target)
        {
            foreach (var n in nodes)
            {
                if (ReferenceEquals(n.Content, target))
                    return n;

                var found = FindNodeByData(n.Children, target);
                if (found != null) return found;
            }
            return null;
        }

        //Exclusion en carpetas pero vista
        
        private Microsoft.UI.Xaml.Controls.TreeViewNode MakeExcludeNode(string dirPath)
        {
            var data = new ExcludeNode
            {
                Name = System.IO.Path.GetFileName(dirPath),
                Path = dirPath,
                IsChecked = false,
                IsLoaded = false
            };

            var node = new Microsoft.UI.Xaml.Controls.TreeViewNode
            {
                Content = data
            };

            // ✅ Solo ponemos dummy si realmente hay subcarpetas
            if (HasSubfolders(dirPath))
            {
                node.Children.Add(new Microsoft.UI.Xaml.Controls.TreeViewNode
                {
                    Content = new ExcludeNode { Name = "Cargando...", Path = "__dummy__" }
                });
            }
            else
            {
                data.IsLoaded = true; // no hay nada que cargar
            }

            return node;
        }

        private void LoadExcludedFolders()
        {
            _excludedFolders.Clear();

            var raw = ApplicationData.Current.LocalSettings.Values[LS_ExcludedFolders] as string;
            if (string.IsNullOrWhiteSpace(raw))
                return;

            foreach (var p in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = p.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                    _excludedFolders.Add(path);
            }
        }

        private void SaveExcludedFolders()
        {
            var raw = string.Join("|", _excludedFolders.Distinct(StringComparer.OrdinalIgnoreCase));
            ApplicationData.Current.LocalSettings.Values[LS_ExcludedFolders] = raw;
        }

        private bool IsExcludedPath(string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            if (_excludedFolders.Count == 0) return false;

            // Normaliza target (acepta absolute/relative y / o \)
            var t = target.Trim().Replace('/', '\\').TrimEnd('\\');

            // Si es relativo y tenemos DROPBOX_ROOT, conviértelo a absoluto
            if (!Path.IsPathRooted(t) && !string.IsNullOrWhiteSpace(DROPBOX_ROOT))
            {
                try { t = Path.GetFullPath(Path.Combine(DROPBOX_ROOT, t)); }
                catch { /* ignore */ }
            }
            else
            {
                try { t = Path.GetFullPath(t); }
                catch { /* ignore */ }
            }

            t = t.TrimEnd('\\');

            foreach (var ex in _excludedFolders)
            {
                if (string.IsNullOrWhiteSpace(ex)) continue;

                var e = ex.Trim().Replace('/', '\\').TrimEnd('\\');
                try { e = Path.GetFullPath(e); } catch { /* ignore */ }
                e = e.TrimEnd('\\');

                // match exacto o dentro
                if (string.Equals(t, e, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (t.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        private void RefreshExcludedFoldersUi()
        {
            _excludedFoldersUi.Clear();
            foreach (var p in _excludedFolders)
                _excludedFoldersUi.Add(p);

            ExcludedFoldersList.ItemsSource = _excludedFoldersUi;
            ExcludedHint.Visibility = _excludedFoldersUi.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnAddExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
                {
                    StatusText.Text = "Estado: Configura un root válido antes de excluir.";
                    return;
                }

                // TreeView
                var tv = new Microsoft.UI.Xaml.Controls.TreeView
                {
                    SelectionMode = Microsoft.UI.Xaml.Controls.TreeViewSelectionMode.Single,
                    MaxHeight = 320
                };
                tv.Padding = new Thickness(0);
                tv.Margin = new Thickness(0);
                tv.Resources["TreeViewItemIndentation"] = 18.0;


                // Expand/Collapse con doble click (más fácil que la flechita)
                tv.DoubleTapped += (s, e2) =>
                {
                    if (tv.SelectedNode is TreeViewNode node)
                        node.IsExpanded = !node.IsExpanded;
                };

                // Lazy-load al expandir
                tv.Expanding += (s, e2) =>
                {
                    if (e2.Node?.Content is not ExcludeNode data) return;

                    if (data.IsLoaded) return;

                    // Si no hay dummy, no hay nada que cargar
                    if (e2.Node.Children.Count == 0)
                    {
                        data.IsLoaded = true;
                        return;
                    }

                    // Quitar dummy
                    if (e2.Node.Children.Count == 1 &&
                        e2.Node.Children[0].Content is ExcludeNode d &&
                        d.Path == "__dummy__")
                    {
                        e2.Node.Children.Clear();
                    }

                    try
                    {
                        foreach (var childDir in Directory.EnumerateDirectories(data.Path))
                        {
                            if (IsExcludedPath(childDir)) continue;

                            var childNode = MakeExcludeNode(childDir);

                            // heredar check del padre
                            if (childNode.Content is ExcludeNode childData && data.IsChecked)
                            {
                                childData.IsChecked = true;
                                childData.IsEnabled = false;
                            }

                            e2.Node.Children.Add(childNode);
                        }
                    }
                    catch { }

                    data.IsLoaded = true;
                };

                // Estilo compacto
                tv.ItemContainerStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                       TargetType='TreeViewItem'>
                    <Setter Property='MinHeight' Value='28'/>
                    <Setter Property='Padding' Value='0'/>
                    <Setter Property='Margin' Value='0'/>
                    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                </Style>");

                // Template: checkbox + texto
                tv.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
                <DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                  <Grid MinHeight='28' Margin='0'>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width='Auto'/>
                      <ColumnDefinition Width='Auto'/>
                      <ColumnDefinition Width='*'/>
                    </Grid.ColumnDefinitions>

                    <CheckBox Grid.Column='0'
                              IsChecked='{Binding Content.IsChecked, Mode=TwoWay}'
                              IsEnabled='{Binding Content.IsEnabled}'
                              VerticalAlignment='Center'
                              Margin='0,0,8,0'/>

                    <TextBlock Grid.Column='1'
                               VerticalAlignment='Center'
                               FontSize='13'
                               Text='{Binding Content.Name}'
                               TextTrimming='CharacterEllipsis'
                               Opacity='0.9'/>
                  </Grid>
                </DataTemplate>");


                // Marcar padre => marca/deshabilita hijos (robusto)
                tv.AddHandler(UIElement.TappedEvent, new TappedEventHandler((s, e2) =>
                {
                    var cb = FindAncestor<CheckBox>(e2.OriginalSource as DependencyObject);
                    if (cb == null) return;

                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        TreeViewNode? node = null;
                        ExcludeNode? data = null;

                        if (cb.DataContext is TreeViewNode tvn)
                        {
                            node = tvn;
                            data = tvn.Content as ExcludeNode;
                        }
                        else if (cb.DataContext is ExcludeNode dn)
                        {
                            data = dn;
                            node = FindNodeByData(tv.RootNodes, dn);
                        }

                        if (node == null || data == null) return;
                        if (data.Path == "__dummy__") return;

                        var isChecked = cb.IsChecked == true;

                        // ✅ AQUÍ va lo de data.IsChecked (para que el modelo quede consistente)
                        data.IsChecked = isChecked;
                        data.IsEnabled = true; // el padre siempre se queda habilitado

                        ApplyToChildren(node, isChecked);
                    });
                }), true);



                // Roots (solo nivel 1)
                foreach (var dir in Directory.EnumerateDirectories(DROPBOX_ROOT))
                {
                    if (IsExcludedPath(dir)) continue;
                    tv.RootNodes.Add(MakeExcludeNode(dir));
                }

                var dialog = new ContentDialog
                {
                    Title = "Excluir varias carpetas",
                    Content = tv,
                    PrimaryButtonText = "Agregar seleccionadas",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                // Recolectar seleccionadas desde el TreeView
                var selected = new List<string>();
                foreach (var n in tv.RootNodes)
                    CollectChecked(n, selected);

                if (selected.Count == 0)
                {
                    StatusText.Text = "Estado: No seleccionaste nada.";
                    return;
                }

                // Aplicar a la lista real de exclusiones (sin duplicados)
                foreach (var p in selected)
                {
                    if (_excludedFolders.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    _excludedFolders.Add(p);
                }

                SaveExcludedFolders();
                RefreshExcludedFoldersUi();

                StatusText.Text = $"Estado: Excluidas {selected.Count} carpetas ✅";
                await RefreshCurrentViewAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error excluyendo → {ex.Message}";
            }
        }
        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T wanted) return wanted;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static void CollectChecked(TreeViewNode node, List<string> acc)
        {
            if (node?.Content is ExcludeNode data)
            {
                if (data.IsChecked && !string.IsNullOrWhiteSpace(data.Path) && data.Path != "__dummy__")
                    acc.Add(data.Path);
            }

            foreach (var child in node.Children)
                CollectChecked(child, acc);
        }
        private static bool HasSubfolders(string path)
        {
            try
            {
                return Directory.EnumerateDirectories(path).Any();
            }
            catch
            {
                return false;
            }
        }
        private void ApplyToChildren(TreeViewNode parentNode, bool isChecked)
        {
            foreach (var child in parentNode.Children)
            {
                if (child.Content is ExcludeNode cd)
                {
                    if (cd.Path == "__dummy__") continue;

                    cd.IsChecked = isChecked;
                    cd.IsEnabled = !isChecked;
                }

                ApplyToChildren(child, isChecked);
            }
        }
        


        private async void BtnRemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            if (b.Tag is not string path) return;

            _excludedFolders.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            SaveExcludedFolders();
            RefreshExcludedFoldersUi();

            StatusText.Text = "Estado: Exclusión eliminada ✅";

            await RefreshCurrentViewAsync();
        }
        private async Task RefreshCurrentViewAsync()
        {
            var q = (SearchBox.Text ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(q))
            {
                await RunSearchAsync(q);
                return;
            }

            var folderToShow =
                (!string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
                    ? _currentFolder
                    : DROPBOX_ROOT;

            if (!string.IsNullOrWhiteSpace(folderToShow) && Directory.Exists(folderToShow))
                await BrowseFolderAsync(folderToShow, pushHistory: false);
        }

        #endregion

        #region ===== Seccion De Comandos Predefinidos =====
        private sealed class SavedSearch
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Title { get; set; } = "";
            public string Description { get; set; } = "";
            public string Query { get; set; } = "";
        }

        private readonly ObservableCollection<SavedSearch> _savedSearches = new();
        private void RefreshCommandsSidebarUi()
        {
            if (CommandsSidebarEmptyHint != null)
                CommandsSidebarEmptyHint.Visibility = _savedSearches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        private async void CommandsSidebarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CommandsSidebarList.SelectedItem is SavedSearch cmd)
            {
                SearchBox.Text = cmd.Query;
                await RunSearchAsync(cmd.Query);
                CommandsSidebarList.SelectedItem = null;
            }
        }
        private void BtnQuickSaveCommand_Click(object sender, RoutedEventArgs e)
        {
            // Reutiliza tu dialog pro de guardar comando
            BtnSaveSearch_Click(sender, e);
        }
        private async void CommandsSidebarList_ItemClick(object sender, ItemClickEventArgs e)
        {
        if (e.ClickedItem is SavedSearch cmd)
        {
            SearchBox.Text = cmd.Query;
            await RunSearchAsync(cmd.Query);

            // opcional: para que no quede “seleccionado”
            CommandsSidebarList.SelectedItem = null;
        }
        }
    private void LoadSavedSearches()
        {
            _savedSearches.Clear();

            var raw = ApplicationData.Current.LocalSettings.Values[LS_SavedSearches] as string;
            if (string.IsNullOrWhiteSpace(raw))
                return;

            try
            {
                var list = JsonSerializer.Deserialize<List<SavedSearch>>(raw) ?? new List<SavedSearch>();
                foreach (var it in list)
                {
                    if (string.IsNullOrWhiteSpace(it?.Query)) continue;
                    if (string.IsNullOrWhiteSpace(it.Title)) it.Title = it.Query;
                    _savedSearches.Add(it);
                }
            }
            catch
            {
                // si se corrompe el JSON, mejor no truena la app
                ApplicationData.Current.LocalSettings.Values[LS_SavedSearches] = "";
            }
        }
        private void SaveSavedSearches()
        {
            var list = _savedSearches.ToList();
            var raw = JsonSerializer.Serialize(list);
            ApplicationData.Current.LocalSettings.Values[LS_SavedSearches] = raw;
        }
        private void RefreshSavedSearchesUi()
        {
            // ahora los comandos viven en el sidebar
            if (CommandsSidebarList != null)
            {
                CommandsSidebarList.ItemsSource = null;
                CommandsSidebarList.ItemsSource = _savedSearches;
            }

            RefreshCommandsSidebarUi();
        }
        private void BtnDeleteSidebarCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not SavedSearch cmd) return;

            // 1) quitar de la colección
            _savedSearches.Remove(cmd);

            // 2) persistir
            SaveSavedSearches();

            // 3) quitar selección (evita bugs visuales)
            if (CommandsSidebarList != null)
                CommandsSidebarList.SelectedItem = null;

            // 4) refrescar UI (sidebar + hints)
            RefreshSavedSearchesUi(); // ← este es el importante
        }
        private async void BtnEditSidebarCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not SavedSearch cmd) return;

            // Busca referencia real en la colección (por Id)
            var existing = _savedSearches.FirstOrDefault(x => x.Id == cmd.Id);
            if (existing == null) return;

            // Reutiliza el mismo dialog pro (igual al de guardar, pero precargado)
            var titleBox = new TextBox
            {
                PlaceholderText = "Título",
                Text = existing.Title ?? ""
            };

            var descBox = new TextBox
            {
                PlaceholderText = "Descripción (opcional)",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Text = existing.Description ?? ""
            };

            var queryBox = new TextBox
            {
                Text = existing.Query ?? ""
            };

            var panel = new StackPanel
            {
                Spacing = 8,
                Children =
        {
            new TextBlock { Text = "Editar comando", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            titleBox,
            descBox,
            new TextBlock { Text = "Query:" },
            queryBox
        }
            };

            var dialog = new ContentDialog
            {
                Title = "Editar comando",
                Content = panel,
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var newTitle = (titleBox.Text ?? "").Trim();
            var newDesc = (descBox.Text ?? "").Trim();
            var newQuery = (queryBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(newTitle) || string.IsNullOrWhiteSpace(newQuery))
            {
                StatusText.Text = "Estado: Título y Query son obligatorios.";
                return;
            }

            // evitar duplicado de query en otro comando
            if (_savedSearches.Any(x => x.Id != existing.Id &&
                                        string.Equals(x.Query, newQuery, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = "Estado: Ya existe otro comando con esa búsqueda.";
                return;
            }

            existing.Title = newTitle;
            existing.Description = newDesc;
            existing.Query = newQuery;

            SaveSavedSearches();
            RefreshSavedSearchesUi();

            StatusText.Text = "Estado: Comando actualizado ✅";
        }
        
        private async void BtnSaveSearch_Click(object sender, RoutedEventArgs e)
        {
            var currentQuery = (SearchBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(currentQuery))
            {
                StatusText.Text = "Estado: Escribe una búsqueda antes de guardar.";
                return;
            }

            var titleBox = new TextBox
            {
                PlaceholderText = "Título (ej: Reportes PDF)",
                Text = currentQuery.Length > 24 ? currentQuery.Substring(0, 24) : currentQuery
            };

            var descBox = new TextBox
            {
                PlaceholderText = "Descripción (opcional)",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap
            };

            var queryBox = new TextBox
            {
                Text = currentQuery
            };

            var panel = new StackPanel
            {
                Spacing = 8,
                Children =
        {
            new TextBlock { Text = "Guardar búsqueda como comando", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            titleBox,
            descBox,
            new TextBlock { Text = "Query:" },
            queryBox
        }
            };

            var dialog = new ContentDialog
            {
                Title = "Nuevo comando",
                Content = panel,
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var finalTitle = (titleBox.Text ?? "").Trim();
            var finalQuery = (queryBox.Text ?? "").Trim();
            var finalDesc = (descBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(finalTitle) || string.IsNullOrWhiteSpace(finalQuery))
            {
                StatusText.Text = "Estado: Título y Query son obligatorios.";
                return;
            }

            if (_savedSearches.Any(x => string.Equals(x.Query, finalQuery, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = "Estado: Ya existe un comando con esa búsqueda.";
                return;
            }

            _savedSearches.Add(new SavedSearch
            {
                Title = finalTitle,
                Description = finalDesc,
                Query = finalQuery
            });

            SaveSavedSearches();
            RefreshSavedSearchesUi();

            StatusText.Text = "Estado: Comando guardado 💾";
        }
        
        private void LoadSidebarExpandedStates()
        {
            var ls = ApplicationData.Current.LocalSettings.Values;

            if (ls.TryGetValue(LS_CommandsExpanded, out var c) && c is bool cb)
                CommandsExpander.IsExpanded = cb;

            if (ls.TryGetValue(LS_ExcludedExpanded, out var e) && e is bool eb)
                ExcludedExpander.IsExpanded = eb;

            // guardar cuando cambie
            CommandsExpander.Expanding += (_, __) => SaveSidebarExpandedStates();
            CommandsExpander.Collapsed += (_, __) => SaveSidebarExpandedStates();
            CommandsExpander.Expanding += (_, __) => SaveSidebarExpandedStates();
            ExcludedExpander.Collapsed += (_, __) => SaveSidebarExpandedStates();
        }

        private void SaveSidebarExpandedStates()
        {
            var ls = ApplicationData.Current.LocalSettings.Values;
            ls[LS_CommandsExpanded] = CommandsExpander.IsExpanded;
            ls[LS_ExcludedExpanded] = ExcludedExpander.IsExpanded;
        }

        #endregion
        #region ===== Comandos de voz =====

        // SearchView.xaml.cs (dentro de SearchView)
        public Task ExecuteSearchTextFromExternalAsync(string text)
        {
            // 1) Pon texto en el SearchBox
            _allowProgrammaticSearch = true;
            SearchBox.Text = text ?? "";
            SearchBox.Focus(FocusState.Programmatic);

            // 2) Dispara el MISMO flujo que ya usas en la ayuda/chips
            TriggerSearchFromHelp(SearchBox.Text);

            // 3) Regresa el flag (para que el usuario siga normal)
            _allowProgrammaticSearch = false;

            return Task.CompletedTask;
        }
        // arriba: using Anfeta.UI.Services.Search;
            public Task ExecuteSearchTextAsync(string text)
            {
                return ExecuteSearchTextFromExternalAsync(text);
            }
        private async void VoiceMenu_Config_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VoiceCommandsDialog(_repo, _voiceEngine);

            // si tu dialog necesita XamlRoot:
            dialog.XamlRoot = this.XamlRoot;

            await dialog.ShowAsync();

            // IMPORTANTÍSIMO: cuando cierras dialog, recarga engine (por si guardaron comandos)
            await _voiceEngine.ReloadAsync();
        }
        private void SetListeningUi(bool listening)
        {
            _isListening = listening;

            // ProgressRing
            VoiceRing.IsActive = listening;
            VoiceRing.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;

            // Mic habilitado siempre (para permitir cancelar con segundo click)
            VoiceSplit.IsEnabled = true;

            // Cambio visual cuando está escuchando
            if (listening)
            {
                VoiceSplit.Background = _voiceActiveBg;
                VoiceSplit.Foreground = _voiceActiveFg;
                StatusText.Text = "Estado: 🎙 Escuchando…";
            }
            else
            {
                VoiceSplit.Background = _voiceSplitDefaultBg;
                VoiceSplit.Foreground = _voiceSplitDefaultFg;
                StatusText.Text = "Estado: Listo";
            }
        }
        private async void VoiceSplit_Click(SplitButton sender, SplitButtonClickEventArgs args)
        {
            if (_isListening)
                await CancelVoiceAsync();
            else
                await StartVoiceAsync();

        }
        private async void VoiceMenu_Listen_Click(object sender, RoutedEventArgs e)
        {
            if (_isListening)
                await CancelVoiceAsync();
            else
                await StartVoiceAsync();

        }
        private async Task StartVoiceAsync()
        {
            // Si ya está escuchando, esto evita doble ejecución
            if (_isListening) return;

            _isListening = true;
            _voiceCts?.Dispose();
            _voiceCts = new CancellationTokenSource();

            SetListeningUi(true);
            VoiceDebugText.Text = "🎙️ Escuchando...";

            try
            {
                var res = await _voiceOrchestrator.ListenAndExecuteAsync(this, _voiceCts.Token);

                VoiceDebugText.Text = string.IsNullOrWhiteSpace(res?.Phrase)
                    ? "🎙️ Sin resultado"
                    : (res.Matched
                        ? $"✅ '{res.Phrase}' → {res.CommandName} ({res.Token})"
                        : $"❓ '{res.Phrase}' (sin match)");
            }
            catch (OperationCanceledException)
            {
                VoiceDebugText.Text = "🎙️ Cancelado";
            }
            finally
            {
                SetListeningUi(false);

                _isListening = false;
                _voiceCts?.Dispose();
                _voiceCts = null;
            }
        }

        // Llama esto cuando vuelvas a presionar el botón del mic estando en escucha
        private async Task CancelVoiceAsync()
        {
            if (!_isListening) return;

            try
            {
                _voiceCts?.Cancel();

                // Si ya tienes VoicePostActionService inyectado:
                await _voicePost.StopAllAsync();
            }
            catch { }
            finally
            {
                _isListening = false;

                SetListeningUi(false);
                VoiceDebugText.Text = "🎙️ Cancelado";

                _voiceCts?.Dispose();
                _voiceCts = null;
            }
        }
        private void SetVoiceHeard(string? phrase)
        {
            VoiceDebugText.Text = string.IsNullOrWhiteSpace(phrase)
                ? "Voz: (no se entendió nada)"
                : $"Voz entendió: “{phrase}”";
        }
        #endregion

        #region ===== Acciones Click Derecho =====
        private void CancelRefreshWork()
        {
            try { _refreshCts?.Cancel(); } catch { }
            _refreshCts?.Dispose();
            _refreshCts = new CancellationTokenSource();
        }
        private enum FileChangeKind { Rename, Delete }

        private async Task ApplyFileChangeAsync(FileChangeKind kind, SearchResultRow row, string? newFullPath = null)
        {
            if (row == null) return;

            await _mutLock.WaitAsync();
            try
            {
                CancelRefreshWork(); // ✅ evita carreras de refresh/search/browse

                var oldPath = row.Target; // en tu modelo, Target = ruta completa
                var isFolder = row.IsFolder; // read-only, ok

                // 1) DISCO (source of truth)
                if (kind == FileChangeKind.Delete)
                {
                    if (isFolder) Directory.Delete(oldPath, recursive: true);
                    else File.Delete(oldPath);
                }
                else // Rename
                {
                    if (string.IsNullOrWhiteSpace(newFullPath))
                        throw new ArgumentException("newFullPath requerido");

                    if (isFolder) Directory.Move(oldPath, newFullPath);
                    else File.Move(oldPath, newFullPath);
                }
                
                // 2) ÍNDICE EN MEMORIA
                if (kind == FileChangeKind.Delete)
                {
                    if (isFolder) App.LocalIndex.RemovePrefix(oldPath);
                    else App.LocalIndex.RemoveExact(oldPath);
                }
                else
                {
                    if (isFolder) App.LocalIndex.RenamePrefix(oldPath, newFullPath!);
                    else App.LocalIndex.RenameExact(oldPath, newFullPath!, isFolder: false);
                }

                // 3) PERSISTIR (sin vacío)
                var snapshot = App.LocalIndex.GetAll();
                if (snapshot.Count == 0)
                    throw new InvalidOperationException("Índice quedó vacío: no se persistirá.");

                await LocalIndexPersistence.SaveAsync(DROPBOX_ROOT, snapshot, CancellationToken.None);

                // 4) UI refresh “seguro”
                await RefreshAfterFileChangeAsync(kind, oldPath, newFullPath);
            }
            finally
            {
                _mutLock.Release();
            }
        }
        private async Task ApplyBatchDeleteAsync(List<SearchResultRow> rows)
        {
            if (rows == null || rows.Count == 0) return;

            await _mutLock.WaitAsync();
            try
            {
                CancelRefreshWork();

                // 1) DISCO: borrar todos primero (source of truth)
                foreach (var row in rows)
                {
                    var path = row.Target;
                    var isFolder = row.IsFolder;

                    if (isFolder) Directory.Delete(path, recursive: true);
                    else File.Delete(path);
                }

                // 2) ÍNDICE: aplicar cambios en memoria (sin persistir en cada uno)
                foreach (var row in rows)
                {
                    var path = row.Target;
                    var isFolder = row.IsFolder;

                    if (isFolder) App.LocalIndex.RemovePrefix(path);
                    else App.LocalIndex.RemoveExact(path);
                }

                // 3) PERSISTIR: una sola vez
                var snapshot = App.LocalIndex.GetAll();
                if (snapshot.Count == 0)
                    throw new InvalidOperationException("Índice quedó vacío: no se persistirá.");

                await LocalIndexPersistence.SaveAsync(DROPBOX_ROOT, snapshot, CancellationToken.None);

                // 4) UI refresh: una sola vez
                // (usa el primero como "affectedOldPath" solo para limpiar selección si aplica)
                await RefreshAfterFileChangeAsync(FileChangeKind.Delete, rows[0].Target, null);
            }
            finally
            {
                _mutLock.Release();
            }
        }
        private async Task RefreshAfterFileChangeAsync(FileChangeKind kind, string oldPath, string? newPath)
        {
            // 1) Limpia selección si apuntaba al item afectado
            if (ResultsList.SelectedItem is SearchResultRow sel)
            {
                if (string.Equals(sel.Target, oldPath, StringComparison.OrdinalIgnoreCase))
                    ResultsList.SelectedItem = null;
            }

            // 2) Si renombraste carpeta, actualiza la carpeta actual si estaba dentro
            if (kind == FileChangeKind.Rename && !string.IsNullOrWhiteSpace(newPath))
            {
                var current = _currentFolderPath;

                if (!string.IsNullOrWhiteSpace(current))
                {
                    var oldN = NormalizePath(oldPath);
                    var newN = NormalizePath(newPath);
                    var curN = NormalizePath(current);

                    // caso A: estabas exactamente en la carpeta renombrada
                    if (string.Equals(curN, oldN, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentFolderPath = newN;
                    }
                    // caso B: estabas dentro de esa carpeta (hijo)
                    else
                    {
                        var oldPrefix = EnsureDirPrefix(oldN);
                        if (curN.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var rest = curN.Substring(oldPrefix.Length);
                            _currentFolderPath = EnsureDirPrefix(newN) + rest;
                        }
                    }
                }
            }

            // 3) Rebuild árbol
            LoadFoldersRoot();
            BuildTreeRoot();

            // 4) Volver a carpeta actual si existe, si no root
            var targetFolder = _currentFolderPath;
            if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
                targetFolder = DROPBOX_ROOT;

            await BrowseFolderAsync(targetFolder, pushHistory: false);
        }

        private static string NormalizePath(string p) => (p ?? "").Trim().Replace('/', '\\');

        private static string EnsureDirPrefix(string folder)
        {
            var p = NormalizePath(folder);
            if (!p.EndsWith("\\", StringComparison.Ordinal)) p += "\\";
            return p;
        }

        private async Task<string?> PromptRenameAsync(string currentName)
        {
            var tb = new TextBox
            {
                Text = currentName,
                Width = 320
            };

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Renombrar",
                Content = tb,
                PrimaryButtonText = "Aceptar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? tb.Text : null;
        }
        private SearchResultRow? GetCtxRowFromFlyout(object sender)
        {
            var mfi = sender as MenuFlyoutItem;
            var flyout = mfi?.Parent as MenuFlyout;
            var fe = flyout?.Target as FrameworkElement;
            return fe?.DataContext as SearchResultRow;
        }
        private SearchResultRow? GetCtxRowOrSelected(object sender)
         => GetCtxRowFromFlyout(sender) ?? ResultsList.SelectedItem as SearchResultRow;
        private List<SearchResultRow> GetSelectedRowsOrCtx(object sender)
        {
            // 1) Si hay multi selección, úsala
            var selected = ResultsList.SelectedItems?.Cast<SearchResultRow>().ToList();
            if (selected != null && selected.Count > 0)
                return selected;

            // 2) si no, usa el item del flyout
            var ctx = GetCtxRowOrSelected(sender);
            return ctx != null ? new List<SearchResultRow> { ctx } : new List<SearchResultRow>();
        }
        private async Task<bool> ConfirmOpenManyAsync(int count, int maxToOpen)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Confirmar",
                Content = $"Vas a abrir {Math.Min(count, maxToOpen)} de {count} elementos.\n¿Deseas continuar?",
                PrimaryButtonText = "Abrir",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close
            };

            var res = await dialog.ShowAsync();
            return res == ContentDialogResult.Primary;
        }
        private async Task<bool> ConfirmDeleteAsync(List<SearchResultRow> rows)
        {
            var count = rows.Count;
            if (count <= 0) return false;

            // Muestra máximo 6 nombres para no saturar
            var preview = string.Join("\n", rows.Take(6).Select(r => $"• {r.Name}"));
            if (count > 6) preview += $"\n• … y {count - 6} más";

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Confirmar eliminación",
                Content = $"Vas a eliminar {count} elemento(s):\n\n{preview}\n\n¿Deseas continuar?",
                PrimaryButtonText = "Eliminar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close
            };

            var res = await dialog.ShowAsync();
            return res == ContentDialogResult.Primary;
        }

        #endregion
        #region ===== CAMBIO DE PESTAÑA =====
        private void SetTabTitle(string title)
        {
            title = (title ?? "").Trim();
            if (title.Length > 28) title = title.Substring(0, 28) + "…";
            if (string.IsNullOrWhiteSpace(title)) title = "Buscar";

            TabTitleChanged?.Invoke(this, title);
        }
        public Anfeta.UI.Models.SearchTabState GetTabState()
        {
            return new Anfeta.UI.Models.SearchTabState
            {
                Header = "", // lo controla el TabHeader con TabTitleChanged
                Query = (SearchBox?.Text ?? "").Trim(),
                CurrentFolder = _currentFolderPath ?? ""
            };
        }
        private async Task RunSearchNowAsync(string query)
        {
            // usa el mismo método que ya usas cuando corre el debounce
            await RunSearchAsync(query); // <— si ya existe en tu SearchView
        }
        public async Task RestoreTabStateAsync(SearchTabState s)
        {
            if (s == null) return;

            // ✅ fija carpeta guardada primero (para que el bootstrap la respete)
            _currentFolderPath = (s.CurrentFolder ?? "").Trim();

            // ✅ texto sin disparar lógica de usuario
            _allowProgrammaticSearch = true;
            SearchBox.Text = s.Query ?? "";
            _allowProgrammaticSearch = false;

            // Si hay query -> buscar
            if (!string.IsNullOrWhiteSpace(s.Query))
            {
                await RunSearchImmediateAsync(s.Query);
                return;
            }

            // Si no hay query -> navegar a carpeta guardada
            if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
            {
                await BrowseFolderAsync(_currentFolderPath, pushHistory: false);
                return;
            }

            // fallback
            if (!string.IsNullOrWhiteSpace(DROPBOX_ROOT) && Directory.Exists(DROPBOX_ROOT))
                await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);
        }
        private async Task RunSearchImmediateAsync(string query)
        {
            if (!App.LocalIndex.HasData) return;

            // Cancela trabajos previos igual que haces normalmente
            CancelRefreshWork();

            // Usa tu método que ya filtra y llena Results
            await RunSearchAsync(query); // <-- aquí pon el método REAL que ya tienes
        }
        private void NotifyWorkspaceChanged()
        {
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region ===== XAML handlers pendientes (stubs) =====

        private void PageSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private async void CtxOpen_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            try
            {
                const int MAX_OPEN = 5;

                if (rows.Count > 1)
                {
                    var ok = await ConfirmOpenManyAsync(rows.Count, MAX_OPEN);
                    if (!ok) return;
                }

                var max = Math.Min(rows.Count, MAX_OPEN);
                for (int i = 0; i < max; i++)
                {
                    var r = rows[i];
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = r.Target,
                        UseShellExecute = true
                    });
                }

                StatusText.Text = rows.Count == 1
                    ? "Abierto ✅"
                    : $"Abiertos {Math.Min(rows.Count, MAX_OPEN)} de {rows.Count} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al abrir: {ex.Message}";
            }
        }
        private async void CtxOpenInApp_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var first = rows[0];

            try
            {
                if (first.IsFolder)
                {
                    await BrowseFolderAsync(first.Target, pushHistory: true);
                }
                else
                {
                    var parent = Path.GetDirectoryName(first.Target);
                    if (string.IsNullOrWhiteSpace(parent)) return;

                    await BrowseFolderAsync(parent, pushHistory: true);

                    // si hay varios seleccionados, intenta re-seleccionarlos si están en esa carpeta
                    foreach (var r in rows.Take(50)) // límite para no lag
                    {
                        var match = Results.FirstOrDefault(x =>
                            string.Equals(x.Target, r.Target, StringComparison.OrdinalIgnoreCase));

                        if (match != null)
                            ResultsList.SelectedItems.Add(match);
                    }
                }

                StatusText.Text = "Abierto en ANFETA ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al abrir en ANFETA: {ex.Message}";
            }
        }
        private void CtxCopyName_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var text = string.Join(Environment.NewLine, rows.Select(r => r.Name));

            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            StatusText.Text = rows.Count == 1 ? "Copiado: nombre ✅" : $"Copiados {rows.Count} nombres ✅";
        }
        private void CtxCopyFullPath_Click(object sender, RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);
            if (row == null) return;

            try
            {
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(row.Target);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                StatusText.Text = "Copiado: ruta ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al copiar ruta: {ex.Message}";
            }
        }
        private void CtxOpenPath_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var first = rows[0];

            try
            {
                if (rows.Count == 1)
                {
                    var args = first.IsFolder
                        ? $"\"{first.Target}\""
                        : $"/select,\"{first.Target}\"";

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = args,
                        UseShellExecute = true
                    });

                    StatusText.Text = "Explorer abierto ✅";
                }
                else
                {
                    var folder = first.IsFolder ? first.Target : (Path.GetDirectoryName(first.Target) ?? DROPBOX_ROOT);

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{folder}\"",
                        UseShellExecute = true
                    });

                    StatusText.Text = $"Explorer abierto (primer elemento) ✅";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error Open Path: {ex.Message}";
            }
        }
        private void CtxOpenWeb_Click(object sender, RoutedEventArgs e) { }
        private void CtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            // Copia 1 por línea (como Explorer / Everything)
            var text = string.Join(Environment.NewLine, rows.Select(r => r.Target));

            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            StatusText.Text = rows.Count == 1 ? "Copiado: ruta ✅" : $"Copiadas {rows.Count} rutas ✅";
        }
        private void CtxCopyLink_Click(object sender, RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);
            if (row == null) { StatusText.Text = "DEBUG: row null (copiar link)"; return; }

            // temporal: copia el path (luego lo cambiamos por Dropbox web link real)
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(row.Target);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            StatusText.Text = "Copiado ✅";
        }
        private async void CtxRename_Click(object sender, RoutedEventArgs e)
        {
            var row = GetCtxRowFromFlyout(sender) ?? ResultsList.SelectedItem as SearchResultRow;
            if (row == null) return;

            var newName = await PromptRenameAsync(row.Name);
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, row.Name, StringComparison.Ordinal))
                return;

            var dir = Path.GetDirectoryName(row.Target) ?? DROPBOX_ROOT;
            var newFullPath = Path.Combine(dir, newName.Trim());

            try
            {
                await ApplyFileChangeAsync(FileChangeKind.Rename, row, newFullPath);
                StatusText.Text = "Estado: Renombrado ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al renombrar: {ex.Message}";
            }
        }
        private async void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var ok = await ConfirmDeleteAsync(rows);
            if (!ok) return;

            try
            {
                if (rows.Count == 1)
                    await ApplyFileChangeAsync(FileChangeKind.Delete, rows[0]);
                else
                    await ApplyBatchDeleteAsync(rows);

                StatusText.Text = rows.Count == 1
                    ? "Estado: Eliminado ✅"
                    : $"Estado: Eliminados {rows.Count} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al eliminar: {ex.Message}";
            }
        }
        private void CtxBookmark_Click(object sender, RoutedEventArgs e) { }
        private void BtnDetailsInfo_Click(object sender, RoutedEventArgs e) { }
        #endregion
    }
}
