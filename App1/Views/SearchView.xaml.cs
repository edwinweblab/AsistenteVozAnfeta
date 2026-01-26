using Anfeta.UI.Models;
using Anfeta.UI.Services.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Anfeta.UI.Services.Bookmarks;
using System.Text.Json;
using System.Runtime.InteropServices;

    

namespace Anfeta.UI.Views
{

    public sealed partial class SearchView : Page 

    {
        private string _currentFolder = DROPBOX_ROOT;
        private bool _isBrowsing = false; // false=buscar, true=explorar carpeta 
        private bool _onlyBookmarks = false;
        private enum ViewMode { Explorer, Bookmarks }
        private ViewMode _mode = ViewMode.Explorer;

        // filtros exclusivos de bookmarks (separados)
        private bool _bmOnlyFolders = false;
        private string? _bmExtFilter = null;
        private string _bmSortKey = "name_asc";



        // ===== Dropbox Smart Sync helpers (Windows) =====
        private const int FILE_ATTRIBUTE_OFFLINE = 0x00001000;
        private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
        private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
        private DropboxFileInfo? _selectedInfo;
        private readonly DropboxNotionFilesApi _api = new(new HttpClient());
        private readonly Stack<string> _backStack = new();
        private readonly Stack<string> _forwardStack = new();
        private ObservableCollection<FolderNode> _treeRoots = new(); 



        public ObservableCollection<SearchResultRow> Results { get; } = new();

        private readonly DispatcherTimer _debounceTimer = new();
        private string _pendingQuery = "";

        private CancellationTokenSource? _cts;
        private List<DropboxNode> _raw = new();
        private const string DROPBOX_ROOT = @"C:\Users\nanoc\Dropbox";

        // cache del “índice” local
        private List<SearchResultRow> _localIndex = new();
        private readonly BookmarksService _bookmarksService = new();
        private List<BookmarkItem> _bookmarks = new();



        // filtros (cliente)
        private bool _onlyFolders = false;
        private string? _extFilter = null; // "pdf","docx","xlsx","img"...
        private string _sortKey = "name_asc";

        // colapsable
        private bool _foldersPaneVisible = true;

        public SearchView()
        {
            InitializeComponent();

            ResultsList.ItemsSource = Results;
            FolderTree.ItemsSource = new ObservableCollection<FolderNode>();
            Loaded += SearchView_Loaded;


            // HttpClient simple (luego lo mueves a DI si quieres)
            _api = new DropboxNotionFilesApi(new HttpClient());

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

            var root = new FolderNode
            {
                Name = "Dropbox",
                FullPath = DROPBOX_ROOT
            };

            // Agregamos un “placeholder” para que muestre flechita
            root.Children.Add(new FolderNode { Name = "Cargando…", FullPath = "" });

            FolderTree.ItemsSource = new ObservableCollection<FolderNode> { root };

            // Oculta hint
            EmptyTreeHint.Visibility = Visibility.Collapsed;
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

            // ya cargado
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

                        // si tiene subcarpetas -> dummy para mostrar flecha
                        try
                        {
                            if (Directory.EnumerateDirectories(dir).Any())
                            {
                                child.HasDummyChild = true;
                                child.Children.Add(new FolderNode { Name = "Cargando...", FullPath = "" });
                            }
                        }
                        catch { }

                        // UI thread
                        DispatcherQueue.TryEnqueue(() => node.Children.Add(child));
                    }
                }
                catch { }
            });
        }

        private async void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is not FolderNode node) return;
            if (string.IsNullOrWhiteSpace(node.FullPath)) return;

            await BrowseFolderAsync(node.FullPath);
        }

        private async Task BrowseFolderAsync(string folder, bool pushHistory = true)
        {
            if (!Directory.Exists(folder))
            {
                StatusText.Text = "Estado: Carpeta no existe";
                return;
            }

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

            _isBrowsing = true;
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

                Results.Clear();

                // Carpetas primero
                foreach (var dir in Directory.EnumerateDirectories(folder))
                {
                    Results.Add(new SearchResultRow
                    {
                        Name = Path.GetFileName(dir),
                        Target = dir,
                        Type = "FOLDER",
                        Source = SearchSource.Local
                    });
                }

                // Archivos
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var fi = new FileInfo(file);
                    Results.Add(new SearchResultRow
                    {
                        Name = Path.GetFileName(file),
                        Target = file,
                        Type = "FILE",
                        Size = fi.Length,
                        ServerModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        Source = SearchSource.Local
                    });
                }

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
        }

        // ===== SEARCH (Everything-like) =====
        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // Solo cuando el usuario escribe
            if (args.Reason != Microsoft.UI.Xaml.Controls.AutoSuggestionBoxTextChangeReason.UserInput)
                return;


            _pendingQuery = sender.Text ?? "";

            // reinicia debounce
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            _debounceTimer.Stop();
            _pendingQuery = sender.Text ?? "";
            await RunSearchAsync(_pendingQuery);
        }

        private async Task RunSearchAsync(string query)
        {
            if (_localIndex.Count == 0)
            {
                StatusText.Text = "Estado: Aún no hay índice. Pulsa Sync.";
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

            // Apagar SOLO los otros chips de extensión (NO tocar Bookmarks/Folders)
            if (_extFilter != null)
            {
                if (sender != ChipPdf) ChipPdf.IsChecked = false;
                if (sender != ChipDocx) ChipDocx.IsChecked = false;
                if (sender != ChipXlsx) ChipXlsx.IsChecked = false;
                if (sender != ChipImg) ChipImg.IsChecked = false;
            }

            // --- 3) Decide qué pintar según modo ---
            if (_onlyBookmarks)
            {
                await ShowBookmarksAsync();      // aplica filtros con tus vars actuales
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


        private void ApplyFiltersAndSort()
        {
            IEnumerable<DropboxNode> q = _raw;

            if (_onlyFolders)
                q = q.Where(n => n.IsFolder);

            if (!string.IsNullOrWhiteSpace(_extFilter))
            {
                q = q.Where(n =>
                {
                    var name = n.Name ?? "";
                    var ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

                    if (_extFilter == "img")
                        return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";

                    return ext == _extFilter;
                });
            }

            // sort (por ahora name/path)
            q = _sortKey switch
            {
                "name_desc" => q.OrderByDescending(n => n.Name),
                _ => q.OrderBy(n => n.Name)
            };

            // pintar en UI
            Results.Clear();
            foreach (var n in q)
            {
                var typeNorm = n.IsFolder ? "FOLDER" : "FILE";

                Results.Add(new SearchResultRow
                {
                    NodeId = n.Id,
                    Name = n.Name,
                    Target = n.Path,                 // lo que muestras en lista
                    Source = SearchSource.Dropbox,

                    Type = n.Type,                   // file/folder
                    Size = n.Size,
                    ServerModified = n.ServerModified,
                });



            }

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

        private async void BtnSync_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                StatusText.Text = "Estado: Indexando carpeta local de Dropbox…";

                await BuildLocalIndexAsync();
                LoadFoldersRoot();
                BuildTreeRoot();
                await BrowseFolderAsync(DROPBOX_ROOT);

                StatusText.Text = $"Estado: Index local listo ✅ ({_localIndex.Count} items)";
                await RunLocalSearchAsync(SearchBox.Text ?? "");
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
        private Task RunLocalSearchAsync(string query)
        {
            _mode = ViewMode.Explorer;

            Results.Clear();

            var q = (query ?? "").Trim().ToLowerInvariant();
            IEnumerable<SearchResultRow> items = _localIndex;

            // filtro por texto
            if (!string.IsNullOrWhiteSpace(q))
                items = items.Where(x => (x.Name ?? "").ToLowerInvariant().Contains(q));

            // filtro solo carpetas
            if (_onlyFolders)
                items = items.Where(x => (x.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase));

            // filtro por extensión
            if (!string.IsNullOrWhiteSpace(_extFilter))
            {
                items = items.Where(x =>
                {
                    var ext = Path.GetExtension(x.Name ?? "").TrimStart('.').ToLowerInvariant();
                    if (_extFilter == "img")
                        return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";
                    return ext == _extFilter;
                });
            }

            // sort
            items = _sortKey switch
            {
                "name_desc" => items.OrderByDescending(x => x.Name),
                _ => items.OrderBy(x => x.Name)
            };

            foreach (var it in items.Take(500))
            {
                // ⭐ estrella según bookmarks (usa LocalPath guardado)
                it.IsBookmarked = _bookmarksService.Exists(_bookmarks, it.Target);
                Results.Add(it);
            }

            CountText.Text = $"{Results.Count} resultados";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            return Task.CompletedTask;
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



        private Task ShowBookmarksAsync()
        {
            _mode = ViewMode.Bookmarks;

            Results.Clear();

            // 1) base: TODOS los bookmarks (siempre)
            var list = _bookmarks ?? new List<BookmarkItem>();
            IEnumerable<BookmarkItem> items = list;

            // 2) filtro por texto (usa SearchBox igual que Explorer)
            var q = (SearchBox?.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var qq = q.ToLowerInvariant();
                items = items.Where(b => (b.Title ?? "").ToLowerInvariant().Contains(qq));
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

                Results.Add(new SearchResultRow
                {
                    Name = b.Title ?? "",
                    Target = localPath,                 // 🔥 SIEMPRE LocalPath aquí
                    Type = b.Type ?? "",
                    Size = b.Size,
                    ServerModified = b.Modified ?? "",
                    Source = b.Source,

                    // 🔥 En vez de forzarlo, lo calculamos por existencia real
                    IsBookmarked = !string.IsNullOrWhiteSpace(localPath)
                                   && _bookmarksService.Exists(_bookmarks, localPath)
                });
            }

            BreadcrumbText.Text = "Bookmarks";
            ModeText.Text = "Modo: Bookmarks";
            CountText.Text = $"{Results.Count} bookmarks";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            return Task.CompletedTask;
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


        private async Task BuildLocalIndexAsync()
        {
            await Task.Run(() =>
            {
                var list = new List<SearchResultRow>();

                if (!System.IO.Directory.Exists(DROPBOX_ROOT))
                    throw new Exception($"No existe la ruta: {DROPBOX_ROOT}");

                // Recorrido (puedes limitar profundidad si quieres)
                foreach (var dir in System.IO.Directory.EnumerateDirectories(DROPBOX_ROOT, "*", System.IO.SearchOption.AllDirectories))
                {
                    list.Add(new SearchResultRow
                    {
                        Name = System.IO.Path.GetFileName(dir),
                        Target = dir,
                        Type = "FOLDER",
                        Source = SearchSource.Local
                    });
                }

                foreach (var file in System.IO.Directory.EnumerateFiles(DROPBOX_ROOT, "*", System.IO.SearchOption.AllDirectories))
                {
                    var info = new System.IO.FileInfo(file);

                    list.Add(new SearchResultRow
                    {
                        Name = System.IO.Path.GetFileName(file),
                        Target = file,
                        Type = "FILE",
                        Size = info.Length,
                        ServerModified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        Source = SearchSource.Local
                    });
                }

                _localIndex = list;
            });
        }

        private void BuildTreeRoot()
        {
            _treeRoots.Clear();

            var root = new FolderNode
            {
                Name = "Dropbox",
                FullPath = DROPBOX_ROOT,
                HasDummyChild = true
            };

            // dummy para que aparezca la flechita de expand
            root.Children.Add(new FolderNode { Name = "Cargando...", FullPath = "" });

            _treeRoots.Add(root);

            FolderTree.ItemsSource = _treeRoots;
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
            try
            {
                LoadFoldersRoot();
                StatusText.Text = "Estado: Árbol cargado ✅";

                // opcional: refresca la vista actual si estás navegando
                if (_isBrowsing)
                    await BrowseFolderAsync(_currentFolder);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error → {ex.Message}";
            }
        }
        private async void BtnGoRoot_Click(object sender, RoutedEventArgs e)
        {
            await BrowseFolderAsync(DROPBOX_ROOT);
        }

        private async void BtnOpen_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

        // ===== Sync/Menu (pendiente) =====
        private void MenuExplore_Click(object sender, RoutedEventArgs e) { }
        private void MenuReindex_Click(object sender, RoutedEventArgs e) { }
        private void MenuRecompute_Click(object sender, RoutedEventArgs e) { }

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
