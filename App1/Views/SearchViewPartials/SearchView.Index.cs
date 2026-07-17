using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Dropbox;
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

        private const string LS_DropboxSyncCursor = "Dropbox.SyncCursor";
        private DispatcherQueueTimer? _dropboxChangeTimer;
        private bool _dropboxSyncRunning;

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
            ShowLoadingState(
                "Estado: Cargando archivos y páginas...",
                "ANFETA está preparando el índice local y las páginas de Notion.");

            await _bootstrapLock.WaitAsync();
            try
            {
                // Si la vista ya había iniciado y el índice ya tiene datos,
                // NO salir sin pintar: hay que refrescar la lista visual.
                if (_bootstrappedOnce && App.LocalIndex.HasData)
                {
                    await PaintLoadedIndexAsync();
                    StartNotionChangeWatcher();
                    StartDropboxChangeWatcher();
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
                    StartDropboxChangeWatcher();
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
                        StartDropboxChangeWatcher();

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
                        StartDropboxChangeWatcher();

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
                StartDropboxChangeWatcher();

                _bootstrappedOnce = true;
            }
            finally
            {
                _bootstrapLock.Release();
                HideLoadingState();
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

                ShowLoadingState(
                    "Estado: Sincronizando Dropbox...",
                    "Detecté cambios en la carpeta. Estoy actualizando el índice local.");
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
                StatusText.Text = $"Estado: Error Dropbox → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        #endregion
        #region 
        private async void BtnRefreshNotion_Click(object sender, RoutedEventArgs e)
        {
            await RefreshNotionIncrementalAsync();
        }

        private static bool ShouldReplaceNotionRow(
            SearchResultRow existing,
            SearchResultRow incoming)
        {
            if (string.Equals(
                    existing.DisplayName,
                    incoming.DisplayName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var existingModified =
                ParseNotionModified(existing.ServerModified);

            var incomingModified =
                ParseNotionModified(incoming.ServerModified);

            // Protección breve para cubrir únicamente el retraso inmediato
            // después de renombrar desde ANFETA.
            if (existingModified.HasValue)
            {
                var age =
                    DateTimeOffset.Now -
                    existingModified.Value.ToLocalTime();

                var existingLooksRecentlyEdited =
                    age >= TimeSpan.Zero &&
                    age <= TimeSpan.FromSeconds(45);

                if (existingLooksRecentlyEdited &&
                    (!incomingModified.HasValue ||
                     incomingModified.Value <=
                     existingModified.Value))
                {
                    return false;
                }
            }

            // Fuera de esa protección corta, Notion vuelve a ser
            // la fuente definitiva del título.
            return true;
        }

        private static DateTimeOffset? ParseNotionModified(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (DateTimeOffset.TryParse(
                    value,
                    out var offset))
            {
                return offset;
            }

            if (DateTime.TryParse(
                    value,
                    out var dateTime))
            {
                var local = DateTime.SpecifyKind(
                    dateTime,
                    DateTimeKind.Local);

                return new DateTimeOffset(local);
            }

            return null;
        }

        private static string GetNotionIdentity(
            SearchResultRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.ExternalId))
                return row.ExternalId.Trim();

            if (!string.IsNullOrWhiteSpace(row.NodeId))
                return row.NodeId.Trim();

            return string.Empty;
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

            // Cuando el usuario está viendo solo Dropbox, la revisión automática
            // de Notion debe ser silenciosa: actualiza el índice interno, pero no
            // muestra overlay, no cambia el estado inferior y no repinta la lista.
            var showNotionUi =
                !automatic ||
                _activeSourceScope != SearchSourceScope.Dropbox;

            if (showNotionUi)
            {
                ShowLoadingState(
                    automatic
                        ? "Estado: Sincronizando Notion..."
                        : "Estado: Revisando cambios de Notion...",
                    "Consultando páginas nuevas o modificadas.");
            }

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
                    var overlapAnchor =
                        lastSyncUtc
                            .ToUniversalTime()
                            .Subtract(TimeSpan.FromMinutes(3));

                    changedItems = await NotionIndexBuilder.BuildManyChangedSinceAsync(
                        token,
                        NotionDataSources.Default,
                        overlapAnchor,
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
                    var current =
                        App.LocalIndex.GetAll().ToList();

                    var appliedChanges = 0;

                    foreach (var incoming in changedItems)
                    {
                        var incomingId =
                            GetNotionIdentity(incoming);

                        if (string.IsNullOrWhiteSpace(incomingId))
                            continue;

                        var existingIndex =
                            current.FindIndex(row =>
                                row.Source == SearchSource.Notion &&
                                string.Equals(
                                    GetNotionIdentity(row),
                                    incomingId,
                                    StringComparison.OrdinalIgnoreCase));

                        if (existingIndex < 0)
                        {
                            current.Add(incoming);
                            appliedChanges++;
                            continue;
                        }

                        var existing = current[existingIndex];

                        if (!ShouldReplaceNotionRow(
                                existing,
                                incoming))
                        {
                            continue;
                        }

                        current[existingIndex] = incoming;
                        appliedChanges++;
                    }

                    if (appliedChanges > 0)
                    {
                        App.LocalIndex.Set(current);
                        await PersistCombinedIndexIfPossibleAsync(current);

                        if (_activeSourceScope != SearchSourceScope.Dropbox)
                            await RefreshCurrentViewPreservingScopeAsync();
                    }
                }

                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] =
                    syncAnchorUtc.ToString("O");

                BtnRefreshNotion.Visibility = Visibility.Collapsed;

                NotionSyncInfoText.Text = changedItems.Count > 0
                    ? $"Notion actualizado ✅ {changedItems.Count} cambios"
                    : $"Notion al día ✅ {FormatUtcLocal(syncAnchorUtc.ToString("O"))}";

                if (showNotionUi)
                {
                    StatusText.Text = changedItems.Count > 0
                        ? $"Estado: Notion actualizado automáticamente ✅ Cambios aplicados: {changedItems.Count}"
                        : "Estado: Notion sin cambios ✅";
                }
            }
            catch (Exception ex)
            {
                NotionSyncInfoText.Text = "Notion: revisión falló";

                if (showNotionUi)
                    StatusText.Text = $"Estado: Error actualizando Notion → {ex.Message}";
            }
            finally
            {
                _notionSyncRunning = false;
                BtnRefreshNotion.Visibility = Visibility.Collapsed;
                BtnRefreshNotion.IsEnabled = true;

                if (showNotionUi)
                    HideLoadingState();
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
                UpdateLoadingState(
                    "Estado: Cargando páginas de Notion...",
                    "Consultando las bases conectadas y preparando los resultados.");

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

            await RunLocalSearchAsync(query);

            var all = App.LocalIndex.GetAll().ToList();
            var notionCount = all.Count(x => x.Source == SearchSource.Notion);
            var localCount = all.Count - notionCount;

            // El texto de modo debe respetar el chip global activo y no inferirse
            // del contenido completo del índice. Así una revisión de Notion no
            // cambia visualmente una vista que está filtrada en Dropbox.
            switch (_activeSourceScope)
            {
                case SearchSourceScope.Notion:
                    ModeText.Text = "Modo: Buscar (Notion)";
                    StatusText.Text = string.IsNullOrWhiteSpace(query)
                        ? $"Estado: Notion cargado ✅ ({notionCount} páginas)"
                        : $"Estado: Búsqueda Notion ✅ ({Results.Count} resultados)";
                    break;

                case SearchSourceScope.Dropbox:
                    ModeText.Text = "Modo: Buscar (Dropbox)";
                    StatusText.Text = string.IsNullOrWhiteSpace(query)
                        ? $"Estado: Índice local cargado ✅ ({localCount} items)"
                        : $"Estado: Búsqueda Dropbox ✅ ({Results.Count} resultados)";
                    break;

                default:
                    ModeText.Text = "Modo: Buscar (Local + Notion)";
                    StatusText.Text = string.IsNullOrWhiteSpace(query)
                        ? $"Estado: Índice cargado ✅ Local: {localCount} · Notion: {notionCount}"
                        : $"Estado: Búsqueda lista ✅ ({Results.Count} resultados)";
                    break;
            }
        }
        #endregion 
        #region ===== Dropbox incremental sync =====

        private void StartDropboxChangeWatcher()
        {
            if (_dropboxChangeTimer != null)
                return;

            _dropboxChangeTimer = DispatcherQueue.CreateTimer();
            _dropboxChangeTimer.Interval = TimeSpan.FromMinutes(2);
            _dropboxChangeTimer.Tick += async (_, _) =>
            {
                await CheckDropboxChangesAsync();
            };
            _dropboxChangeTimer.Start();

            // La primera llamada crea el cursor base. No modifica resultados.
            _ = CheckDropboxChangesAsync();
        }

        private void StopDropboxChangeWatcher()
        {
            if (_dropboxChangeTimer == null)
                return;

            _dropboxChangeTimer.Stop();
            _dropboxChangeTimer = null;
        }

        private async Task CheckDropboxChangesAsync()
        {
            if (_dropboxSyncRunning ||
                string.IsNullOrWhiteSpace(DROPBOX_ROOT) ||
                !Directory.Exists(DROPBOX_ROOT))
            {
                return;
            }

            _dropboxSyncRunning = true;

            try
            {
                var cursor = ApplicationData.Current.LocalSettings.Values[
                    LS_DropboxSyncCursor] as string;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var batch = await _dropboxSyncService.GetChangesAsync(cursor, cts.Token);

                ApplicationData.Current.LocalSettings.Values[
                    LS_DropboxSyncCursor] = batch.Cursor;

                if (batch.CursorInitialized)
                {
                    StatusText.Text = "Estado: Dropbox preparado para sincronización automática ✅";
                    return;
                }

                if (batch.Changes.Count == 0)
                    return;

                ShowLoadingState(
                    "Estado: Sincronizando Dropbox...",
                    $"Aplicando {batch.Changes.Count} cambio(s) externos.");

                var summary = await ApplyDropboxRemoteChangesAsync(
                    batch.Changes,
                    cts.Token);

                await RefreshAfterBackgroundIndexChangeAsync(SearchSource.Dropbox);

                StatusText.Text =
                    $"Estado: Dropbox actualizado ✅ " +
                    $"Nuevos/modificados: {summary.Upserted} · " +
                    $"Eliminados: {summary.Deleted}";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Estado: Dropbox tardó demasiado en responder. Se intentará de nuevo automáticamente.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error Dropbox → {ex.Message}";
            }
            finally
            {
                _dropboxSyncRunning = false;
                HideLoadingState();
            }
        }

        private async Task<(int Upserted, int Deleted)> ApplyDropboxRemoteChangesAsync(
            IReadOnlyList<DropboxRemoteChange> changes,
            CancellationToken cancellationToken)
        {
            var snapshot = App.LocalIndex.GetAll().ToList();
            var upserted = 0;
            var deleted = 0;

            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryMapDropboxRemotePathToLocal(change.PathDisplay, out var localPath))
                    continue;

                if (change.IsDeleted)
                {
                    var normalized = NormalizePath(localPath);
                    var prefix = EnsureDirPrefix(normalized);
                    var removed = snapshot.RemoveAll(x =>
                        x.Source != SearchSource.Notion &&
                        (string.Equals(
                            NormalizePath(x.Target),
                            normalized,
                            StringComparison.OrdinalIgnoreCase) ||
                         NormalizePath(x.Target).StartsWith(
                            prefix,
                            StringComparison.OrdinalIgnoreCase)));

                    deleted += removed;
                    continue;
                }

                var existing = snapshot.FirstOrDefault(x =>
                    x.Source != SearchSource.Notion &&
                    string.Equals(
                        NormalizePath(x.Target),
                        NormalizePath(localPath),
                        StringComparison.OrdinalIgnoreCase));

                var isLocalAvailable = change.IsFolder
                    ? Directory.Exists(localPath)
                    : File.Exists(localPath);

                var row = existing ?? new SearchResultRow();
                row.Name = string.IsNullOrWhiteSpace(change.Name)
                    ? Path.GetFileName(localPath)
                    : change.Name;
                row.Target = localPath;
                row.Type = change.IsFolder ? "FOLDER" : "FILE";
                row.Size = change.Size;
                row.ServerModified = change.ServerModifiedUtc.HasValue
                    ? change.ServerModifiedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : string.Empty;
                row.Source = isLocalAvailable
                    ? SearchSource.Local
                    : SearchSource.Dropbox;

                if (existing == null)
                    snapshot.Add(row);

                upserted++;
            }

            if (snapshot.Count > 0)
            {
                App.LocalIndex.Set(snapshot);
                await LocalIndexPersistence.SaveAsync(
                    DROPBOX_ROOT,
                    snapshot,
                    cancellationToken);
            }

            return (upserted, deleted);
        }

        private bool TryMapDropboxRemotePathToLocal(
            string remotePath,
            out string localPath)
        {
            localPath = string.Empty;

            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) ||
                string.IsNullOrWhiteSpace(remotePath))
            {
                return false;
            }

            var relative = remotePath
                .Trim()
                .TrimStart('/', '\\')
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            var candidate = Path.GetFullPath(Path.Combine(DROPBOX_ROOT, relative));
            var root = Path.GetFullPath(DROPBOX_ROOT)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = root + Path.DirectorySeparatorChar;

            if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            localPath = candidate;
            return true;
        }

        private async Task RefreshAfterBackgroundIndexChangeAsync(
            SearchSource changedSource)
        {
            // Un cambio de Dropbox no debe reemplazar una vista exclusiva de Notion.
            if (changedSource == SearchSource.Dropbox &&
                _activeSourceScope == SearchSourceScope.Notion)
            {
                return;
            }

            // Un cambio de Notion no debe reemplazar una vista exclusiva de Dropbox.
            if (changedSource == SearchSource.Notion &&
                _activeSourceScope == SearchSourceScope.Dropbox)
            {
                return;
            }

            await RefreshCurrentViewPreservingScopeAsync();
        }

        private async Task RefreshCurrentViewPreservingScopeAsync()
        {
            var query = (SearchBox?.Text ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(query))
            {
                await RunLocalSearchAsync(query);
                return;
            }

            if (_isBrowsing &&
                !string.IsNullOrWhiteSpace(_currentFolderPath) &&
                Directory.Exists(_currentFolderPath))
            {
                await BrowseFolderAsync(_currentFolderPath, pushHistory: false);
                return;
            }

            await RunLocalSearchAsync(string.Empty);
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

                var overlapAnchor =
                    lastSyncUtc
                        .ToUniversalTime()
                        .Subtract(TimeSpan.FromMinutes(3));

                var hasChanges = await NotionIndexBuilder.HasAnyChangesSinceAsync(
                    token,
                    NotionDataSources.Default,
                    overlapAnchor,
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
                await PersistCombinedIndexIfPossibleAsync(current);

                if (_activeSourceScope != SearchSourceScope.Dropbox)
                    await RefreshCurrentViewPreservingScopeAsync();

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
                await PersistCombinedIndexIfPossibleAsync(currentWithoutNotion);

                var now = DateTimeOffset.UtcNow.ToString("O");
                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] = now;

                BtnRefreshNotion.Visibility = Visibility.Collapsed;
                NotionSyncInfoText.Text = $"Notion al día ✅ {FormatUtcLocal(now)}";

                if (_activeSourceScope != SearchSourceScope.Dropbox)
                    await RefreshCurrentViewPreservingScopeAsync();

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