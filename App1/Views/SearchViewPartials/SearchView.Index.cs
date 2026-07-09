using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Speech;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using System.Collections.Generic;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private const string LS_NotionToken = "Notion.Token";
        private const string LS_NotionDataSourceId = "Notion.DataSourceId";
        private const string LS_NotionLastSyncUtc = "Notion.LastSyncUtc";
        private DispatcherQueueTimer? _notionChangeTimer;
        private bool _notionSyncRunning;

        private static string FormatUtcLocal(string utcText)
        {
            if (DateTimeOffset.TryParse(utcText, out var dto))
                return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

            return utcText;
        }
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
                // Si la vista ya había iniciado y el índice ya tiene datos,
                // NO salir sin pintar: hay que refrescar la lista visual.
                if (_bootstrappedOnce && App.LocalIndex.HasData)
                {
                    await PaintLoadedIndexAsync();
                    StartNotionChangeWatcher();
                    return;
                }

                var saved = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;
                var savedRoot = (saved ?? string.Empty).Trim();

                if (_bootstrappedOnce &&
                    App.LocalIndex.HasData &&
                    !string.IsNullOrWhiteSpace(savedRoot) &&
                    string.Equals(DROPBOX_ROOT, savedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    await PaintLoadedIndexAsync();
                    StartNotionChangeWatcher();
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
                        StartNotionChangeWatcher();

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
                        await PaintLoadedIndexAsync();
                        StartNotionChangeWatcher();

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



                await PaintLoadedIndexAsync();
                StartNotionChangeWatcher();

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
        private async void BtnRefreshNotion_Click(object sender, RoutedEventArgs e)
        {
            await RefreshNotionIncrementalAsync();
        }

        private async Task RefreshNotionIncrementalAsync(bool automatic = false)
        {
            if (_notionSyncRunning)
                return;

            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var dataSourceId = ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] as string;
            var lastSyncStr = ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] as string;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(dataSourceId))
            {
                NotionSyncInfoText.Text = "Notion no configurado";

                if (!automatic)
                    StatusText.Text = "Estado: Notion no configurado.";

                return;
            }

            _notionSyncRunning = true;
            BtnRefreshNotion.Visibility = Visibility.Collapsed;

            try
            {
                NotionSyncInfoText.Text = automatic
                    ? "Sincronizando Notion..."
                    : "Revisando Notion...";

                if (!automatic)
                    StatusText.Text = "Estado: Revisando cambios de Notion...";

                var syncAnchorUtc = DateTimeOffset.UtcNow;

                List<SearchResultRow> changedItems;

                if (!string.IsNullOrWhiteSpace(lastSyncStr) &&
                    DateTimeOffset.TryParse(lastSyncStr, out var lastSyncUtc))
                {
                    changedItems = await NotionIndexBuilder.BuildManyChangedSinceAsync(
                        token,
                        NotionDataSources.Default,
                        lastSyncUtc.ToUniversalTime(),
                        CancellationToken.None);
                }
                else
                {
                    changedItems = await NotionIndexBuilder.BuildManyAsync(
                        token,
                        NotionDataSources.Default,
                        CancellationToken.None);
                }

                if (changedItems.Count > 0)
                {
                    var current = App.LocalIndex.GetAll().ToList();

                    var changedIds = changedItems
                        .Select(x => !string.IsNullOrWhiteSpace(x.ExternalId) ? x.ExternalId : x.NodeId)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    current.RemoveAll(x =>
                        x.Source == SearchSource.Notion &&
                        changedIds.Contains(!string.IsNullOrWhiteSpace(x.ExternalId) ? x.ExternalId : x.NodeId));

                    current.AddRange(changedItems);

                    App.LocalIndex.Set(current);

                    await PaintLoadedIndexAsync();
                }

                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] =
                    syncAnchorUtc.ToString("O");

                BtnRefreshNotion.Visibility = Visibility.Collapsed;

                NotionSyncInfoText.Text = changedItems.Count > 0
                    ? $"Notion actualizado ✅ {changedItems.Count} cambios"
                    : $"Notion al día ✅ {FormatUtcLocal(syncAnchorUtc.ToString("O"))}";

                StatusText.Text = changedItems.Count > 0
                    ? $"Estado: Notion actualizado automáticamente ✅ Cambios aplicados: {changedItems.Count}"
                    : "Estado: Notion sin cambios ✅";
            }
            catch (Exception ex)
            {
                NotionSyncInfoText.Text = "Notion: revisión falló";

                if (!automatic)
                    StatusText.Text = $"Estado: Error actualizando Notion → {ex.Message}";
            }
            finally
            {
                _notionSyncRunning = false;
                BtnRefreshNotion.Visibility = Visibility.Collapsed;
                BtnRefreshNotion.IsEnabled = true;
            }
        }
        private async Task<bool> TryLoadNotionIndexOnStartupAsync(CancellationToken ct = default)
        {
            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var dataSourceId = ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] as string;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(dataSourceId))
                return false;

            try
            {
                StatusText.Text = "Estado: Sincronizando Notion...";

                var notionItems = await NotionIndexBuilder.BuildManyAsync(
                token,
                NotionDataSources.Default,
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
                NotionSyncInfoText.Text = $"Notion al día ✅ {FormatUtcLocal(DateTimeOffset.UtcNow.ToString("O"))}";

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

            BreadcrumbText.Text = string.IsNullOrWhiteSpace(query)
                ? "Todos los resultados"
                : $"Buscar: {query}";

            await RunLocalSearchAsync(query);

            var all = App.LocalIndex.GetAll().ToList();

            var notionCount = all.Count(x => x.Source == SearchSource.Notion);
            var localCount = all.Count - notionCount;

            if (notionCount > 0 && localCount > 0)
            {
                ModeText.Text = "Modo: Buscar (Local + Notion)";
                StatusText.Text = string.IsNullOrWhiteSpace(query)
                    ? $"Estado: Índice cargado ✅ Local: {localCount} · Notion: {notionCount}"
                    : "Estado: Búsqueda local ✅";
            }
            else if (notionCount > 0)
            {
                ModeText.Text = "Modo: Buscar (Notion)";
                StatusText.Text = $"Estado: Notion cargado ✅ ({notionCount} páginas)";
            }
            else
            {
                ModeText.Text = "Modo: Buscar (Local)";
                StatusText.Text = $"Estado: Índice local cargado ✅ ({localCount} items)";
            }
        }
        #endregion 
        private void StartNotionChangeWatcher()
        {
            if (_notionChangeTimer != null)
                return;

            _notionChangeTimer = DispatcherQueue.CreateTimer();
            _notionChangeTimer.Interval = TimeSpan.FromMinutes(2);

            _notionChangeTimer.Tick += async (_, _) =>
            {
                await CheckNotionChangesAsync();
            };

            _notionChangeTimer.Start();

            _ = CheckNotionChangesAsync();
        }

        private async Task CheckNotionChangesAsync()
        {
            if (_notionSyncRunning)
                return;

            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var dataSourceId = ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] as string;
            var lastSyncStr = ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] as string;

            BtnRefreshNotion.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(dataSourceId) ||
                string.IsNullOrWhiteSpace(lastSyncStr) ||
                !DateTimeOffset.TryParse(lastSyncStr, out var lastSyncUtc))
            {
                NotionSyncInfoText.Text = "";
                return;
            }

            try
            {
                NotionSyncInfoText.Text = "Revisando Notion...";

                var hasChanges = await NotionIndexBuilder.HasAnyChangesSinceAsync(
                    token,
                    NotionDataSources.Default,
                    lastSyncUtc.ToUniversalTime(),
                    CancellationToken.None);

                if (hasChanges)
                {
                    await RefreshNotionIncrementalAsync(automatic: true);
                    return;
                }

                NotionSyncInfoText.Text = $"Notion al día ✅ {FormatUtcLocal(lastSyncStr)}";
            }
            catch
            {
                // No bloqueamos el buscador si falla una revisión silenciosa.
                BtnRefreshNotion.Visibility = Visibility.Collapsed;
                NotionSyncInfoText.Text = "Notion: revisión falló";
            }
        }
        private async void BtnCheckNotionDeleted_Click(object sender, RoutedEventArgs e)
        {
            await CheckNotionDeletedPagesAsync();
        }

        private async Task CheckNotionDeletedPagesAsync()
        {
            if (_notionSyncRunning)
                return;

            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var dataSourceId = ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] as string;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(dataSourceId))
            {
                StatusText.Text = "Estado: Notion no configurado.";
                return;
            }

            _notionSyncRunning = true;

            try
            {
                StatusText.Text = "Estado: Revisando páginas eliminadas en Notion...";

                var freshNotionItems = await NotionIndexBuilder.BuildManyAsync(
                  token,
                  NotionDataSources.Default,
                  CancellationToken.None);

                var freshIds = freshNotionItems
                    .Select(GetNotionRowId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var current = App.LocalIndex.GetAll().ToList();

                var deletedIds = current
                    .Where(x => x.Source == SearchSource.Notion)
                    .Select(GetNotionRowId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Where(x => !freshIds.Contains(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (deletedIds.Count == 0)
                {
                    StatusText.Text = "Estado: No se detectaron páginas eliminadas ✅";
                    return;
                }

                current.RemoveAll(x =>
                    x.Source == SearchSource.Notion &&
                    deletedIds.Contains(GetNotionRowId(x)));

                App.LocalIndex.Set(current);

                await PaintLoadedIndexAsync();

                StatusText.Text = $"Estado: Se quitaron {deletedIds.Count} páginas eliminadas de Notion ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error revisando eliminadas → {ex.Message}";
            }
            finally
            {
                _notionSyncRunning = false;
            }
        }

        private static string GetNotionRowId(SearchResultRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.ExternalId))
                return row.ExternalId.Trim();

            if (!string.IsNullOrWhiteSpace(row.NodeId))
                return row.NodeId.Trim();

            return "";
        }
        private async void BtnFullResyncNotion_Click(object sender, RoutedEventArgs e)
        {
            await FullResyncNotionAsync();
        }

        private async Task FullResyncNotionAsync()
        {
            if (_notionSyncRunning)
                return;

            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var dataSourceId = ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] as string;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(dataSourceId))
            {
                StatusText.Text = "Estado: Notion no configurado.";
                return;
            }

            _notionSyncRunning = true;

            try
            {
                StatusText.Text = "Estado: Resync completo de Notion...";

                var freshNotionItems = await NotionIndexBuilder.BuildManyAsync(
                 token,
                 NotionDataSources.Default,
                 CancellationToken.None);

                var currentWithoutNotion = App.LocalIndex
                    .GetAll()
                    .Where(x => x.Source != SearchSource.Notion)
                    .ToList();

                currentWithoutNotion.AddRange(freshNotionItems);

                App.LocalIndex.Set(currentWithoutNotion);

                var now = DateTimeOffset.UtcNow.ToString("O");
                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] = now;

                BtnRefreshNotion.Visibility = Visibility.Collapsed;
                NotionSyncInfoText.Text = $"Notion al día ✅ {FormatUtcLocal(now)}";

                await PaintLoadedIndexAsync();

                StatusText.Text = $"Estado: Resync Notion completo ✅ ({freshNotionItems.Count} páginas)";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error en resync Notion → {ex.Message}";
            }
            finally
            {
                _notionSyncRunning = false;
            }
        }
    }
}