using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Speech;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using System.Linq;
using static Anfeta.UI.Helpers.AppSettingsKeys; 

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private const string LS_NotionToken = "Notion.Token";
        private const string LS_NotionDataSourceId = "Notion.DataSourceId";
        private const string LS_NotionLastSyncUtc = "Notion.LastSyncUtc";
        #region ===== Index Coordinator (Auto-index) =====

        private void OnIndexStateChanged()
        {
            DispatcherQueue.TryEnqueue(() => { _ = ApplyIndexStateAsync(); });
        }

        private async Task ApplyIndexStateAsync()
        {
            if (DropboxIndexCoordinator.IsIndexing)
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: Ruta nueva detectada, indexando...";
                return;
            }

            if (!string.IsNullOrWhiteSpace(DropboxIndexCoordinator.LastError))
            {
                ResetSearchModuleState();
                StatusText.Text = $"Estado: Error indexando -> {DropboxIndexCoordinator.LastError}";
                return;
            }

            if (DropboxIndexCoordinator.IsReady && App.LocalIndex.HasData)
            {
                var root = DropboxIndexCoordinator.RootPath ?? "";

                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    ResetSearchModuleState();
                    StatusText.Text = "Estado: Ruta invalida. Configura de nuevo en Settings.";
                    return;
                }

                ResetSearchModuleState();

                DROPBOX_ROOT = root;
                _currentFolder = "";
                _currentFolderPath = DROPBOX_ROOT;
                _backStack.Clear();
                _forwardStack.Clear();

                LoadFoldersRoot();
                BuildTreeRoot();

                await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);

                StatusText.Text = $"Estado: Index local listo ({App.LocalIndex.Count} items)";
            }
        }

        private async Task EnsureIndexBootstrappedAsync()
        {
            await _bootstrapLock.WaitAsync();

            try
            {
                if (_bootstrappedOnce && App.LocalIndex.HasData)
                    return;

                var saved = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;
                var savedRoot = (saved ?? string.Empty).Trim();

                if (_bootstrappedOnce &&
                    App.LocalIndex.HasData &&
                    !string.IsNullOrWhiteSpace(savedRoot) &&
                    string.Equals(DROPBOX_ROOT, savedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // ─────────────────────────────────────────────
                // CASO 1: No hay carpeta local configurada
                // Intentar cargar Notion solo
                // ─────────────────────────────────────────────
                if (string.IsNullOrWhiteSpace(savedRoot))
                {
                    ResetSearchModuleState();

                    var notionLoaded = await TryLoadNotionIndexOnStartupAsync(CancellationToken.None);

                    CommandsSidebarList.ItemsSource = _savedSearches;
                    RefreshCommandsSidebarUi();

                    if (notionLoaded && App.LocalIndex.HasData)
                    {
                        await PaintLoadedIndexAsync();

                        _bootstrappedOnce = true;
                        return;
                    }

                    StatusText.Text = "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                    _bootstrappedOnce = true;
                    return;
                }

                DROPBOX_ROOT = savedRoot;

                // ─────────────────────────────────────────────
                // CASO 2: Hay carpeta configurada, pero no existe
                // Intentar cargar Notion solo
                // ─────────────────────────────────────────────
                if (!Directory.Exists(DROPBOX_ROOT))
                {
                    ResetSearchModuleState();

                    var notionLoaded = await TryLoadNotionIndexOnStartupAsync(CancellationToken.None);

                    CommandsSidebarList.ItemsSource = _savedSearches;
                    RefreshCommandsSidebarUi();

                    if (notionLoaded && App.LocalIndex.HasData)
                    {
                        StatusText.Text = $"Estado: Notion cargado ✅ ({App.LocalIndex.Count} páginas)";
                        ModeText.Text = "Modo: Buscar (Notion)";
                        _bootstrappedOnce = true;
                        return;
                    }

                    StatusText.Text = "Estado: La carpeta configurada ya no existe. Ve a Settings y selecciona otra ruta.";
                    _bootstrappedOnce = true;
                    return;
                }

                // ─────────────────────────────────────────────
                // CASO 3: Intentar cargar índice local desde caché
                // ─────────────────────────────────────────────
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

                // ─────────────────────────────────────────────
                // CASO 4: Si hay índice local, validar si requiere reindexar
                // ─────────────────────────────────────────────
                if (App.LocalIndex.HasData && !DropboxIndexCoordinator.IsIndexing)
                {
                    var lastIndexedStr = ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] as string;

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
                        await ReindexCurrentRootAsync();
                }

                // ─────────────────────────────────────────────
                // CASO 5: Cargar Notion encima del índice local
                // Esto evita que al recargar se pierdan páginas de Notion
                // ─────────────────────────────────────────────
                await TryLoadNotionIndexOnStartupAsync(CancellationToken.None);

                // ─────────────────────────────────────────────
                // CASO 6: Si no hay nada, mostrar aviso
                // ─────────────────────────────────────────────
                if (DropboxIndexCoordinator.IsIndexing || !App.LocalIndex.HasData)
                {
                    ResetSearchModuleState();

                    StatusText.Text = DropboxIndexCoordinator.IsIndexing
                        ? "Estado: Ruta nueva detectada, indexando..."
                        : "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";

                    _bootstrappedOnce = true;
                    return;
                }

                // ─────────────────────────────────────────────
                // CASO 7: Cargar explorador local
                // ─────────────────────────────────────────────
                LoadFoldersRoot();
                BuildTreeRoot();

                var startFolder =
                    !string.IsNullOrWhiteSpace(_currentFolderPath) &&
                    Directory.Exists(_currentFolderPath)
                        ? _currentFolderPath
                        : DROPBOX_ROOT;

                await BrowseFolderAsync(startFolder, pushHistory: false);

                CommandsSidebarList.ItemsSource = _savedSearches;
                RefreshCommandsSidebarUi();

                var hasNotion = App.LocalIndex
                    .GetAll()
                    .Any(x => x.Source == SearchSource.Notion);

                ModeText.Text = hasNotion
                    ? "Modo: Buscar (Local + Notion)"
                    : "Modo: Buscar (Local)";

                await PaintLoadedIndexAsync();

                _bootstrappedOnce = true;
            }
            finally
            {
                _bootstrapLock.Release();
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

                StatusText.Text = "Estado: Detecté cambios en la carpeta. Reindexando...";
                DropboxIndexCoordinator.StartIndexing(DROPBOX_ROOT);
                App.LocalIndex.Clear();

                var list = await LocalIndexBuilder.BuildAsync(DROPBOX_ROOT, ct);

                if (list == null || list.Count == 0)
                {
                    StatusText.Text = "Estado: Reindex produjo 0 items. Conservo el índice anterior.";
                    DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);
                    return;
                }

                App.LocalIndex.Set(list);
                await LocalIndexPersistence.SaveAsync(DROPBOX_ROOT, list, ct);

                ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] =
                    DateTimeOffset.UtcNow.ToString("O");

                DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);
                StatusText.Text = $"Estado: Reindex listo ({App.LocalIndex.Count} items)";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DropboxIndexCoordinator.MarkError(DROPBOX_ROOT, ex.Message);
                StatusText.Text = $"Estado: Error reindexando -> {ex.Message}";
            }
        }

        #endregion
        #region
        private async Task<bool> TryLoadNotionIndexOnStartupAsync(CancellationToken ct = default)
        {
            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var dataSourceId = ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] as string;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(dataSourceId))
                return false;

            try
            {
                StatusText.Text = "Estado: Sincronizando Notion...";

                var notionItems = await NotionIndexBuilder.BuildAsync(
                    token,
                    dataSourceId,
                    ct);

                var currentWithoutNotion = App.LocalIndex
                    .GetAll()
                    .Where(x => x.Source != SearchSource.Notion)
                    .ToList();

                currentWithoutNotion.AddRange(notionItems);

                App.LocalIndex.Set(currentWithoutNotion);

                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] =
                    DateTimeOffset.UtcNow.ToString("O");

                StatusText.Text = $"Estado: Notion cargado ✅ ({notionItems.Count} páginas)";

                return notionItems.Count > 0;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: No se pudo cargar Notion → {ex.Message}";
                return false;
            }
        }
        private async Task PaintLoadedIndexAsync()
        {
            if (!App.LocalIndex.HasData)
                return;

            var query = (SearchBox?.Text ?? string.Empty).Trim();

            // Si hay texto escrito, se busca ese texto.
            // Si está vacío, muestra el índice cargado normal.
            await RunLocalSearchAsync(query);

            var all = App.LocalIndex.GetAll().ToList();

            var notionCount = all.Count(x => x.Source == SearchSource.Notion);
            var localCount = all.Count - notionCount;

            BreadcrumbText.Text = string.IsNullOrWhiteSpace(query)
                ? "Todos los resultados"
                : $"Buscar: {query}";

            var hasNotion = notionCount > 0;
            var hasLocal = localCount > 0;

            if (hasNotion && hasLocal)
            {
                ModeText.Text = "Modo: Buscar (Local + Notion)";
                StatusText.Text = string.IsNullOrWhiteSpace(query)
                    ? $"Estado: Índice cargado ✅ Local: {localCount} · Notion: {notionCount}"
                    : $"Estado: Búsqueda local ✅";
            }
            else if (hasNotion)
            {
                ModeText.Text = "Modo: Buscar (Notion)";
                StatusText.Text = string.IsNullOrWhiteSpace(query)
                    ? $"Estado: Notion cargado ✅ ({notionCount} páginas)"
                    : $"Estado: Notion cargado ✅ ({notionCount} páginas)";
            }
            else
            {
                ModeText.Text = "Modo: Buscar (Local)";
                StatusText.Text = string.IsNullOrWhiteSpace(query)
                    ? $"Estado: Índice local cargado ✅ ({localCount} items)"
                    : $"Estado: Búsqueda local ✅";
            }
        }
        #endregion
    }
}