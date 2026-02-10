using Anfeta.UI.Models;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Bookmarks;
using Anfeta.UI.Services.Search;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;


namespace Anfeta.UI.Views
{

    public sealed partial class SearchView : Page 

    {
        private List<string> _highlightTerms = new();
        private readonly ShellIconService _iconService = new ShellIconService();
        private bool _isBrowsing = false; // false=buscar, true=explorar carpeta 
        private bool _onlyBookmarks = false;

        private enum ViewMode { Explorer, Bookmarks }
        private ViewMode _mode = ViewMode.Explorer;

        // filtros exclusivos de bookmarks (separados)
        private bool _bmOnlyFolders = false;
        private string? _bmExtFilter = null;
        private string _bmSortKey = "name_asc";

        private DispatcherTimer? _searchDebounceTimer;
        private CancellationTokenSource? _searchCts;
        private bool _allowProgrammaticSearch;



        private const string LS_DropboxRoot = "DropboxRoot";

        // ===== Dropbox Smart Sync helpers (Windows) =====
        private const int FILE_ATTRIBUTE_OFFLINE = 0x00001000;
        private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
        private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
        
        private readonly Stack<string> _backStack = new();
        private readonly Stack<string> _forwardStack = new();
        private ObservableCollection<FolderNode> _treeRoots = new();


        // Navegación / Explorador
        private string _currentFolder = "";  // ✅ YA EXISTE
        //Ayuda
        private ContentDialog? _helpDialog;
        private ContentControl? _helpBodyHost;
        private TeachingTip? _helpTip;

        public ObservableCollection<SearchResultRow> Results { get; } = new();

        private readonly DispatcherTimer _debounceTimer = new();
        private string _pendingQuery = "";

        private CancellationTokenSource? _cts;
        // ✅ Root dinámico (ya no const fijo)
        private string DROPBOX_ROOT = "";


        // cache del “índice” local
        private readonly BookmarksService _bookmarksService = new();
        private List<BookmarkItem> _bookmarks = new();



        // filtros (cliente)
        private bool _onlyFolders = false;
        private string? _extFilter = null; // "pdf","docx","xlsx","img"...
        private string _sortKey = "name_asc";

        // colapsable
        private bool _foldersPaneVisible = true;

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
        public SearchView()
        {
            InitializeComponent();
            DropboxIndexCoordinator.StateChanged += OnIndexStateChanged;
            Unloaded += (_, __) => DropboxIndexCoordinator.StateChanged -= OnIndexStateChanged;

            ResultsList.ItemsSource = Results;
            FolderTree.ItemsSource = new ObservableCollection<FolderNode>();
            Loaded += SearchView_Loaded;
            // Debounce 300ms
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(300);
            _debounceTimer.Tick += async (_, __) =>
            {
                _debounceTimer.Stop();
                await RunSearchAsync(_pendingQuery);
            };

            StatusText.Text = "Estado: Dropbox (API)";
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
            // 1) Bookmarks
            try
            {
                _bookmarks = await _bookmarksService.LoadAsync(CancellationToken.None);
                StatusText.Text = $"Estado: Bookmarks cargados ✅ ({_bookmarks.Count})";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error cargando bookmarks → {ex.Message}";
                _bookmarks = new();
            }

            // 2) Leer ruta guardada
            var saved = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;
            var hasValidDropboxRoot = !string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved);

            if (!hasValidDropboxRoot)
            {
                DROPBOX_ROOT = "";
                ResetSearchModuleState();
                StatusText.Text = $"Estado: Bookmarks ✅ ({_bookmarks.Count}) | Configura la ruta en Settings.";
                return;
            }

            DROPBOX_ROOT = saved!.Trim();

            // 3) Si está indexando o aún no hay índice, mostrar estado (sin Sync)
            if (DropboxIndexCoordinator.IsIndexing || !App.LocalIndex.HasData)
            {
                ResetSearchModuleState();
                StatusText.Text = DropboxIndexCoordinator.IsIndexing
                    ? $"Estado: Ruta nueva detectada, indexando…"
                    : $"Estado: Aún no hay índice. Ve a Settings y selecciona la ruta (auto-index).";
                return;
            }

            // 4) Ya hay índice: cargar árbol + root
            LoadFoldersRoot();
            BuildTreeRoot();
            await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);

            StatusText.Text = $"Estado: Index local listo ✅ ({App.LocalIndex.Count} items)";
        }


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
                await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);

                StatusText.Text = $"Estado: Index local listo ✅ ({App.LocalIndex.Count} items)";
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

        private void LoadFoldersRoot()
        {
            if (!Directory.Exists(DROPBOX_ROOT))
                throw new Exception($"No existe la ruta: {DROPBOX_ROOT}");

            BuildTreeRoot();
        }

        // ===== Colapsable =====
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

        private async Task BrowseFolderAsync(string folder, bool pushHistory = true)
        {
            if (!Directory.Exists(folder))
            {
                StatusText.Text = "Estado: Carpeta no existe";
                return;
            }

            // ✅ Entramos a modo Explorer (browse)
            _mode = ViewMode.Explorer;
            _isBrowsing = true;

            // ✅ IMPORTANTÍSIMO: al navegar carpeta, salimos del “modo búsqueda”
            // (evita residuos visuales y highlight aplicándose donde no toca)

            // ⚠️ OJO: si NO quieres resetear los chips al navegar, comenta estas 3 líneas:
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

        // ===== SEARCH (Everything-like) =====
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
                    StatusText.Text = "Estado: Aún no hay índice. Ve a Settings y selecciona la ruta (auto-index).";
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

            if (!App.LocalIndex.HasData)
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: No hay índice. Ve a Settings y selecciona la ruta (auto-index).";
                return;
            }

            _searchDebounceTimer!.Stop();
            _searchDebounceTimer.Start();
        }

        private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            _debounceTimer.Stop();
            _pendingQuery = sender.Text ?? "";
            await RunSearchAsync(_pendingQuery);
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
                StatusText.Text = "Estado: No hay índice. Ve a Settings y selecciona la ruta (auto-index).";
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

        private void FinishUi()
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;

            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = $"{Results.Count} resultados";
        }

        // ===== Filters =====
        private async void ChipFilter_Click(object sender, RoutedEventArgs e)
        {
            // --- 1) Flags ---
            if (sender == ChipBookmarks)
                _onlyBookmarks = ChipBookmarks.IsChecked == true;

            if (sender == ChipFolders)
                _onlyFolders = ChipFolders.IsChecked == true;

            // --- 2) Extensión (solo una a la vez) ---
            if (sender == ChipPdf) _extFilter = ChipPdf.IsChecked == true ? "pdf" : null;
            else if (sender == ChipDocx) _extFilter = ChipDocx.IsChecked == true ? "docx" : null;
            else if (sender == ChipXlsx) _extFilter = ChipXlsx.IsChecked == true ? "xlsx" : null;
            else if (sender == ChipImg) _extFilter = ChipImg.IsChecked == true ? "img" : null;
            else if (sender == ChipUrl) _extFilter = ChipUrl.IsChecked == true ? "url" : null; // ✅ NUEVO

            // Apagar SOLO los otros chips de extensión (NO tocar Bookmarks/Folders)
            if (_extFilter != null)
            {
                if (sender != ChipPdf) ChipPdf.IsChecked = false;
                if (sender != ChipDocx) ChipDocx.IsChecked = false;
                if (sender != ChipXlsx) ChipXlsx.IsChecked = false;
                if (sender != ChipImg) ChipImg.IsChecked = false;
                if (sender != ChipUrl) ChipUrl.IsChecked = false; // ✅ NUEVO
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
        // ===== Results interactions (por ahora UI/Details) =====
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
        private async Task RunLocalSearchAsync(string query)
        {
            _mode = ViewMode.Explorer;
            Results.Clear();

            var rawQuery = (query ?? "").Trim();
            IEnumerable<SearchResultRow> items = App.LocalIndex.GetAll();


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
                    items = items.Where(x => (x.Name ?? "").ToLowerInvariant().Contains(q));
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

            await Task.CompletedTask;

        }
        private async Task RunLocalSearchAsync(string query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            _mode = ViewMode.Explorer;
            Results.Clear();

            var rawQuery = (query ?? "").Trim();
            IEnumerable<SearchResultRow> items = App.LocalIndex.GetAll();

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
                    items = items.Where(x => (x.Name ?? "").ToLowerInvariant().Contains(q));
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

            await Task.CompletedTask;
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
        //SECCION AYUDA
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
        private UIElement CreateExampleRow(string example, string? note = null, bool run = true,bool replace=true)
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
                StatusText.Text = "Estado: No hay índice. Ve a Settings y selecciona la ruta (auto-index).";
                return;
            }

            // Fuerza el mismo comportamiento que cuando el usuario escribe
            _searchDebounceTimer!.Stop();
            _searchDebounceTimer.Start();
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

        private async Task EnterBookmarksModeAsync()
        {
            // reset visual de chips para no confundir
            _bmExtFilter = null;
            _bmOnlyFolders = false;
            _bmSortKey = "name_asc";

            ChipPdf.IsChecked = false;
            ChipDocx.IsChecked = false;
            ChipXlsx.IsChecked = false;
            ChipImg.IsChecked = false;
            ChipRecent.IsChecked = false;
            ChipFolders.IsChecked = false;

            await ShowBookmarksAsync();
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

        // ===== Paging/Tree (pendiente) =====
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

        private void PageSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

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
                StatusText.Text = "Estado: No hay índice. Ve a Settings y selecciona la ruta (auto-index).";
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
                StatusText.Text = "Estado: No hay índice. Ve a Settings y selecciona la ruta (auto-index).";
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

        private async void BtnOpen_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();
        // ===== Context actions (pendiente) =====
        private async void CtxOpen_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();
        private void CtxOpenWeb_Click(object sender, RoutedEventArgs e) { }
        private void CtxCopyPath_Click(object sender, RoutedEventArgs e) { }
        private void CtxCopyLink_Click(object sender, RoutedEventArgs e) { }
        private void CtxRename_Click(object sender, RoutedEventArgs e) { }
        private void CtxDelete_Click(object sender, RoutedEventArgs e) { }
        private void CtxBookmark_Click(object sender, RoutedEventArgs e) { }

        // ===== Details actions (pendiente) =====
        private void BtnDetailsInfo_Click(object sender, RoutedEventArgs e) { }
    }
}
