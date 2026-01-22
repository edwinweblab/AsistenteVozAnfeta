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


namespace Anfeta.UI.Views
{
    public sealed partial class SearchView : Page 

    {
        private DropboxFileInfo? _selectedInfo;
        private readonly DropboxNotionFilesApi _api = new(new HttpClient());

        public ObservableCollection<SearchResultRow> Results { get; } = new();

        private readonly DispatcherTimer _debounceTimer = new();
        private string _pendingQuery = "";

        private CancellationTokenSource? _cts;
        private List<DropboxNode> _raw = new();

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
            // Cancela request anterior
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            // UI states
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            Results.Clear();
            EmptyResultsHint.Visibility = Visibility.Collapsed;
            CountText.Text = "…";

            BreadcrumbText.Text = string.IsNullOrWhiteSpace(query) ? "/" : $"Buscar: {query}";
            ModeText.Text = "Modo: Buscar";

            if (string.IsNullOrWhiteSpace(query))
            {
                _raw = new List<DropboxNode>();
                ApplyFiltersAndSort();
                FinishUi();
                return;
            }

            try
            {
                var includeNotion = ToggleNotion.IsChecked == true;

                StatusText.Text = includeNotion
                    ? "Estado: Buscando (Dropbox + Notion)…"
                    : "Estado: Buscando (Dropbox)…";

                var nodes = await _api.SearchAsync(query, includeNotion, _cts.Token);
                _raw = nodes;

                ApplyFiltersAndSort();

                StatusText.Text = includeNotion
                    ? "Estado: Dropbox + Notion OK"
                    : "Estado: Dropbox OK";
            }
            catch (OperationCanceledException)
            {
                // normal al teclear rápido
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error API → {ex.Message}";
                _raw = new List<DropboxNode>();
                ApplyFiltersAndSort();
            }
            finally
            {
                FinishUi();
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
        private void ChipFilter_Click(object sender, RoutedEventArgs e)
        {
            // reset rápido: solo uno de extension a la vez (para simplificar)
            if (sender == ChipPdf) _extFilter = ChipPdf.IsChecked == true ? "pdf" : null;
            else if (sender == ChipDocx) _extFilter = ChipDocx.IsChecked == true ? "docx" : null;
            else if (sender == ChipXlsx) _extFilter = ChipXlsx.IsChecked == true ? "xlsx" : null;
            else if (sender == ChipImg) _extFilter = ChipImg.IsChecked == true ? "img" : null;

            // Solo carpetas
            if (sender == ChipFolders)
                _onlyFolders = ChipFolders.IsChecked == true;

            // Si activaste un chip de extension, apaga los otros para que no se contradigan
            if (_extFilter != null)
            {
                if (sender != ChipPdf) ChipPdf.IsChecked = false;
                if (sender != ChipDocx) ChipDocx.IsChecked = false;
                if (sender != ChipXlsx) ChipXlsx.IsChecked = false;
                if (sender != ChipImg) ChipImg.IsChecked = false;

                // deja el que prendiste
                if (sender == ChipPdf) ChipPdf.IsChecked = true;
                if (sender == ChipDocx) ChipDocx.IsChecked = true;
                if (sender == ChipXlsx) ChipXlsx.IsChecked = true;
                if (sender == ChipImg) ChipImg.IsChecked = true;
            }

            // Recientes (sin metadata aún) = visual por ahora
            // ChipRecent no hace filtro real hasta que tengamos Modified
            ApplyFiltersAndSort();
            FinishUi();
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SortCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
                _sortKey = tag;

            ApplyFiltersAndSort();
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
                    PathLower = n.PathLower,
                    Source = SearchSource.Dropbox,

                    Type = n.Type,                   // file/folder
                    Size = n.Size,
                    MimeType = n.MimeType,
                    ServerModified = n.ServerModified,
                    SharedLink = n.SharedLink
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

            DetailsMeta.Text =
                $"Tipo: {(string.IsNullOrWhiteSpace(row.Type) ? "—" : row.Type.ToUpperInvariant())}\n" +
                $"Tamaño: {(row.Size > 0 ? $"{row.Size / 1024:N0} KB" : "—")}\n" +
                $"Modificado: {(!string.IsNullOrWhiteSpace(row.ServerModified) ? row.ServerModified : "—")}\n" +
                $"Mime: {(!string.IsNullOrWhiteSpace(row.MimeType) ? row.MimeType : "—")}\n" +
                $"Id: {row.NodeId}";

            // Notion relacionado (si no tienes aún, déjalo en —)
            DetailsNotion.Text = "—";

            if ((row.Type ?? "").Equals("folder", StringComparison.OrdinalIgnoreCase))
                StatusText.Text = "Estado: Es carpeta (usa acciones de navegación) 📁";
            else
                StatusText.Text = "Estado: Seleccionado ✅";
        }


        private void ResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;

            // 1) Si ya viene sharedLink del search, úsalo
            if (!string.IsNullOrWhiteSpace(row.SharedLink))
            {
                StatusText.Text = "Estado: Abriendo shared link…";
                OpenUrl(row.SharedLink);
                StatusText.Text = "Estado: Abierto ✅";
                return;
            }

            // 2) Si no hay sharedLink, abre en Dropbox Web por ruta
            // Usa row.Target como PathLower (en tu app lo estás guardando ahí)
            var path = row.Target;

            // Si es carpeta (o no parece archivo), abre folder
            if ((row.Type ?? "").Equals("folder", StringComparison.OrdinalIgnoreCase) || !LooksLikeFile(path))
            {
                var urlFolder = BuildDropboxWebUrl(path);
                StatusText.Text = "Estado: Abriendo carpeta en Dropbox…";
                OpenUrl(urlFolder);
                StatusText.Text = "Estado: Abierto ✅";
                return;
            }

            // Archivo: abre preview
            var urlPreview = BuildDropboxPreviewUrl(path);
            StatusText.Text = "Estado: Abriendo preview en Dropbox…";
            OpenUrl(urlPreview);
            StatusText.Text = "Estado: Abierto ✅";
        }






        private void BtnDetailsLink_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;

            // Si ya hay sharedLink úsalo
            if (!string.IsNullOrWhiteSpace(row.SharedLink))
            {
                StatusText.Text = "Estado: Abriendo shared link…";
                OpenUrl(row.SharedLink);
                StatusText.Text = "Estado: Abierto ✅";
                return;
            }

            // Si no hay sharedLink -> abre por ruta
            var path = row.Target;

            var url = ((row.Type ?? "").Equals("folder", StringComparison.OrdinalIgnoreCase) || !LooksLikeFile(path))
                ? BuildDropboxWebUrl(path)
                : BuildDropboxPreviewUrl(path);

            StatusText.Text = "Estado: Abriendo en Dropbox Web…";
            OpenUrl(url);
            StatusText.Text = "Estado: Abierto ✅";
        }



        private void ResultsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // El ContextFlyout ya existe en XAML, no hace falta lógica aquí por ahora
        }

        private void BtnStar_Click(object sender, RoutedEventArgs e)
        {
            // Pendiente: guardar bookmark
        }
        private static string BuildDropboxWebUrl(string pathLower)
        {
            if (string.IsNullOrWhiteSpace(pathLower))
                return "https://www.dropbox.com/home";

            // Asegura que empiece con "/"
            var p = pathLower.StartsWith("/") ? pathLower : "/" + pathLower;

            // Encode solo lo necesario para URL (espacios, etc.)
            // Ojo: NO uses Uri.EscapeDataString en toda la ruta porque rompe los "/"
            string EncodePath(string s) =>
                string.Join("/", s.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(Uri.EscapeDataString)
                ).Insert(0, "/");

            // Si termina en "/" asumimos carpeta
            var isFolder = p.EndsWith("/");

            // si no termina en /, puede ser archivo o carpeta; lo decidimos por extensión más tarde
            // aquí solo construimos con parent + preview cuando detectemos archivo
            return "https://www.dropbox.com/home" + EncodePath(p);
        }

        private static string BuildDropboxPreviewUrl(string pathLower)
        {
            if (string.IsNullOrWhiteSpace(pathLower))
                return "https://www.dropbox.com/home";

            var p = pathLower.StartsWith("/") ? pathLower : "/" + pathLower;

            // nombre archivo
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "https://www.dropbox.com/home";

            var fileName = parts[^1];
            var parent = "/" + string.Join("/", parts.Take(parts.Length - 1));

            string EncodePath(string s) =>
                "/" + string.Join("/", s.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                        .Select(Uri.EscapeDataString));

            var parentEncoded = EncodePath(parent);
            var fileEncoded = Uri.EscapeDataString(fileName);

            return $"https://www.dropbox.com/home{parentEncoded}?preview={fileEncoded}";
        }

        private static bool LooksLikeFile(string pathLowerOrName)
        {
            if (string.IsNullOrWhiteSpace(pathLowerOrName)) return false;
            var ext = System.IO.Path.GetExtension(pathLowerOrName);
            return !string.IsNullOrWhiteSpace(ext); // si tiene extensión, lo tratamos como archivo
        }

        private static void OpenUrl(string url)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }


        // ===== Paging/Tree (pendiente) =====
        private void BtnPrevPage_Click(object sender, RoutedEventArgs e) { }
        private void BtnNextPage_Click(object sender, RoutedEventArgs e) { }
        private void PageSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args) { }
        private void BtnRefreshTree_Click(object sender, RoutedEventArgs e) { }
        private void BtnGoRoot_Click(object sender, RoutedEventArgs e) { }

        // ===== Sync/Menu (pendiente) =====
        private void BtnSync_Click(object sender, RoutedEventArgs e) { }
        private void MenuExplore_Click(object sender, RoutedEventArgs e) { }
        private void MenuReindex_Click(object sender, RoutedEventArgs e) { }
        private void MenuRecompute_Click(object sender, RoutedEventArgs e) { }

        // ===== Context actions (pendiente) =====
        private void CtxOpen_Click(object sender, RoutedEventArgs e) { }
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
