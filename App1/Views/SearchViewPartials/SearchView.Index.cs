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
using System.Diagnostics;
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
        private DispatcherQueueTimer? _notionNoticeTimer;

        private static readonly object CompletedReminderCleanupLock =
            new();

        private static readonly HashSet<string>
            CompletedReminderCleanupQueued =
                new(StringComparer.OrdinalIgnoreCase);

        // Compatibilidad con versiones anteriores: el cursor antes se guardaba
        // en LocalSettings, pero puede superar el límite de tamaño permitido
        // por ApplicationData.LocalSettings. A partir de esta versión se
        // persiste como archivo dentro de LocalFolder.
        private const string LS_DropboxSyncCursor = "Dropbox.SyncCursor";
        private const string DropboxSyncCursorFileName =
            "dropbox_sync_cursor.txt";

        private DispatcherQueueTimer? _dropboxChangeTimer;
        private bool _dropboxSyncRunning;

        // Watcher local: detecta cambios reales en la carpeta configurada
        // aunque sea una carpeta normal de Windows y no dependa de Dropbox API.
        private FileSystemWatcher? _localFileWatcher;
        private string _localFileWatcherRoot = string.Empty;
        private readonly object _localFsPendingLock = new();
        private readonly Dictionary<string, bool> _localFsPendingChanges =
            new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _localFsDebounceCts;

        // ===== Performance Cache v1 =====
        // El índice es global (App.LocalIndex), por lo que el bootstrap y las
        // comprobaciones automáticas también deben coordinarse globalmente.
        private static readonly SemaphoreSlim SharedBootstrapLock = new(1, 1);
        private static readonly SemaphoreSlim SharedNotionSyncGate = new(1, 1);
        private static readonly SemaphoreSlim SharedNotionProbeGate = new(1, 1);
        private static readonly SemaphoreSlim SharedDropboxSyncGate = new(1, 1);
        private static readonly SemaphoreSlim SharedLocalReindexGate = new(1, 1);
        private static readonly SemaphoreSlim SharedLocalFsChangeGate = new(1, 1);
        private static readonly SemaphoreSlim SharedCobrosCalendarRepairGate = new(1, 1);
        private static readonly object SharedRuntimeStateLock = new();

        // Migración puntual del calendario de Cobros. Al subir esta versión,
        // se reconstruye únicamente "Cobrar y pagar" para reemplazar fechas
        // antiguas que quedaron persistidas en LocalIndex antes de que
        // ScheduledDate usara Due Fecha Recordatorio.
        private const string LS_CobrosCalendarMappingVersion =
            "Search.Cobros.CalendarMappingVersion";
        private const int CobrosCalendarMappingVersion = 1;

        private static bool SharedBootstrapCompleted;
        private static string SharedBootstrapRoot = string.Empty;
        private static DateTimeOffset SharedLastNotionProbeUtc = DateTimeOffset.MinValue;
        private static DateTimeOffset SharedLastDropboxProbeUtc = DateTimeOffset.MinValue;

        private static string NormalizeSharedRoot(string? root)
            => (root ?? string.Empty).Trim().TrimEnd('\\', '/');

        private static bool IsSharedBootstrapReady(string? root)
        {
            var normalized = NormalizeSharedRoot(root);

            lock (SharedRuntimeStateLock)
            {
                return SharedBootstrapCompleted &&
                       string.Equals(
                           SharedBootstrapRoot,
                           normalized,
                           StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void MarkSharedBootstrapReady(string? root)
        {
            lock (SharedRuntimeStateLock)
            {
                SharedBootstrapRoot = NormalizeSharedRoot(root);
                SharedBootstrapCompleted = true;
            }
        }

        private static bool ReserveSharedProbe(
            ref DateTimeOffset lastProbeUtc,
            TimeSpan minimumInterval)
        {
            lock (SharedRuntimeStateLock)
            {
                var now = DateTimeOffset.UtcNow;

                if (now - lastProbeUtc < minimumInterval)
                    return false;

                lastProbeUtc = now;
                return true;
            }
        }

        private async Task PaintSharedIndexForThisViewAsync()
        {
            _bootstrappedOnce = true;

            if (DeferInitialIndexPaint)
            {
                _deferredIndexPaintPending = true;
                return;
            }

            if (App.LocalIndex.HasData)
                await PaintLoadedIndexAsync();

            StartNotionChangeWatcher();
            StartDropboxChangeWatcher();

            // No bloquea el arranque. Si el índice persistido todavía trae
            // fechas viejas de Cobros, se repara una sola vez en background.
            _ = EnsureCobrosCalendarIndexMappingAsync();
        }

        private async Task<bool> EnsureCobrosCalendarIndexMappingAsync(
            bool force = false)
        {
            var values =
                ApplicationData.Current.LocalSettings.Values;

            var savedVersion =
                values[LS_CobrosCalendarMappingVersion] is int version
                    ? version
                    : 0;

            if (!force &&
                savedVersion >= CobrosCalendarMappingVersion)
            {
                return false;
            }

            await SharedCobrosCalendarRepairGate.WaitAsync();

            try
            {
                savedVersion =
                    values[LS_CobrosCalendarMappingVersion] is int lockedVersion
                        ? lockedVersion
                        : 0;

                if (!force &&
                    savedVersion >= CobrosCalendarMappingVersion)
                {
                    return false;
                }

                var token =
                    values[LS_NotionToken] as string;

                if (string.IsNullOrWhiteSpace(token))
                    return false;

                var source =
                    NotionDataSources.Default
                        .FirstOrDefault(item =>
                            string.Equals(
                                item.Name,
                                "Cobrar y pagar",
                                StringComparison.OrdinalIgnoreCase));

                if (source == null ||
                    string.IsNullOrWhiteSpace(source.DataSourceId))
                {
                    return false;
                }

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(6));

                var freshCobros =
                    await NotionIndexBuilder.BuildAsync(
                        token,
                        source.DataSourceId,
                        cts.Token,
                        maxItems: null,
                        lastEditedAfterUtc: null,
                        sourceName: source.Name);

                var current =
                    App.LocalIndex.GetAll().ToList();

                current.RemoveAll(row =>
                    row.Source == SearchSource.Notion &&
                    string.Equals(
                        row.ExternalSourceName,
                        source.Name,
                        StringComparison.OrdinalIgnoreCase));

                current.AddRange(freshCobros);

                App.LocalIndex.Set(current);

                await PersistCombinedIndexIfPossibleAsync(
                    current);

                values[LS_CobrosCalendarMappingVersion] =
                    CobrosCalendarMappingVersion;

                // Elimina inmediatamente la posición visual anterior y vuelve
                // a dibujar Cobros usando el nuevo ScheduledDate.
                if (_calendarViewActive)
                {
                    _calendarCobroOverlayCache.Clear();
                    _calendarCobroCacheIndexVersion =
                        App.LocalIndex.Version;

                    RefreshCalendarExternalOverlaysIfNeeded(
                        force: true);
                }

                if (!DeferInitialIndexPaint &&
                    _activeSourceScope != SearchSourceScope.Dropbox)
                {
                    await RefreshCurrentViewPreservingScopeAsync();
                }

                Debug.WriteLine(
                    $"[COBROS_CALENDAR_REPAIR] " +
                    $"{freshCobros.Count} fila(s) reconstruidas.");

                return true;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine(
                    "[COBROS_CALENDAR_REPAIR] Cancelado por tiempo de espera.");

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[COBROS_CALENDAR_REPAIR] {ex.Message}");

                return false;
            }
            finally
            {
                SharedCobrosCalendarRepairGate.Release();
            }
        }

        private static string FormatUtcLocal(string utcText)
        {
            if (DateTimeOffset.TryParse(utcText, out var dto))
                return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

            return utcText;
        }

        private void ShowNotionSyncNotice(
            string message,
            bool isError = false,
            int visibleSeconds = 5)
        {
            if (NotionSyncNotice == null ||
                NotionSyncInfoText == null)
            {
                return;
            }

            _notionNoticeTimer?.Stop();
            _notionNoticeTimer = null;

            var clean = (message ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
            {
                HideNotionSyncNotice();
                return;
            }

            NotionSyncInfoText.Text = clean;
            NotionSyncNotice.Visibility = Visibility.Visible;

            if (NotionSyncNoticeIcon != null)
            {
                NotionSyncNoticeIcon.Glyph =
                    isError
                        ? "\uE783"
                        : "\uE73E";

                NotionSyncNoticeIcon.Foreground =
                    new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        isError
                            ? Windows.UI.Color.FromArgb(255, 255, 142, 142)
                            : Windows.UI.Color.FromArgb(255, 116, 214, 138));
            }

            NotionSyncNotice.Background =
                new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    isError
                        ? Windows.UI.Color.FromArgb(45, 180, 52, 52)
                        : Windows.UI.Color.FromArgb(36, 40, 167, 69));

            NotionSyncNotice.BorderBrush =
                new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    isError
                        ? Windows.UI.Color.FromArgb(90, 220, 70, 70)
                        : Windows.UI.Color.FromArgb(85, 40, 167, 69));

            _notionNoticeTimer =
                DispatcherQueue.CreateTimer();

            _notionNoticeTimer.Interval =
                TimeSpan.FromSeconds(
                    Math.Max(2, visibleSeconds));

            _notionNoticeTimer.IsRepeating = false;
            _notionNoticeTimer.Tick += (_, _) =>
            {
                HideNotionSyncNotice();
            };

            _notionNoticeTimer.Start();
        }

        private void HideNotionSyncNotice()
        {
            _notionNoticeTimer?.Stop();
            _notionNoticeTimer = null;

            if (NotionSyncInfoText != null)
                NotionSyncInfoText.Text = string.Empty;

            if (NotionSyncNotice != null)
                NotionSyncNotice.Visibility =
                    Visibility.Collapsed;
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
                if (!App.LocalIndex.HasData)
                {
                    ResetSearchModuleState();
                    StatusText.Text = "Estado: Ruta nueva detectada, indexando...";
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(DropboxIndexCoordinator.LastError))
            {
                StatusText.Text =
                    $"Estado: Error indexando -> {DropboxIndexCoordinator.LastError}";
                return;
            }

            if (!DropboxIndexCoordinator.IsReady || !App.LocalIndex.HasData)
                return;

            var root = DropboxIndexCoordinator.RootPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                StatusText.Text =
                    "Estado: Ruta invalida. Configura de nuevo en Settings.";
                return;
            }

            // El índice global ya contiene los datos. Un cambio de estado no
            // vuelve a recorrer la carpeta completa ni destruye la vista actual.
            DROPBOX_ROOT = root;

            if (string.IsNullOrWhiteSpace(_currentFolderPath))
                _currentFolderPath = root;

            // Si la ruta cambió desde Settings, el watcher local se mueve
            // inmediatamente a la carpeta nueva sin reiniciar ANFETA.
            StartLocalFileWatcher();

            if (!DeferInitialIndexPaint)
                await PaintLoadedIndexAsync();

            StatusText.Text =
                $"Estado: Índice compartido listo ({App.LocalIndex.Count} items)";
        }

        private async Task EnsureIndexBootstrappedAsync()
        {
            var savedRoot =
                (ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string
                 ?? string.Empty).Trim();

            // Fast path: otra pestaña ya dejó el índice listo. No mostramos
            // overlay, no reindexamos Dropbox y no descargamos Notion otra vez.
            if (IsSharedBootstrapReady(savedRoot))
            {
                DROPBOX_ROOT = savedRoot;

                if (!string.IsNullOrWhiteSpace(savedRoot) &&
                    Directory.Exists(savedRoot) &&
                    string.IsNullOrWhiteSpace(_currentFolderPath))
                {
                    _currentFolderPath = savedRoot;
                }

                await PaintSharedIndexForThisViewAsync();
                return;
            }

            await _bootstrapLock.WaitAsync();

            try
            {
                // Otra llamada de esta misma vista pudo terminar mientras esperaba.
                if (IsSharedBootstrapReady(savedRoot))
                {
                    DROPBOX_ROOT = savedRoot;
                    await PaintSharedIndexForThisViewAsync();
                    return;
                }

                await SharedBootstrapLock.WaitAsync();

                try
                {
                    // Revisión después de esperar a la pestaña que estaba haciendo
                    // el bootstrap global.
                    if (IsSharedBootstrapReady(savedRoot))
                    {
                        DROPBOX_ROOT = savedRoot;
                        await PaintSharedIndexForThisViewAsync();
                        return;
                    }

                    var perf = Stopwatch.StartNew();

                    if (!DeferInitialIndexPaint)
                    {
                        ShowLoadingState(
                            "Estado: Cargando caché de ANFETA...",
                            "Mostrando primero la información guardada; los cambios se comprobarán en segundo plano.");
                    }

                    var backgroundLocalReindexNeeded = false;

                    // 1) Cargar el índice persistido UNA SOLA VEZ por proceso.
                    if (!App.LocalIndex.HasData)
                    {
                        var (ok, cachedRoot, cachedItems) =
                            await LocalIndexPersistence.TryLoadAsync(
                                CancellationToken.None);

                        if (ok && cachedItems != null && cachedItems.Count > 0)
                        {
                            if (!string.IsNullOrWhiteSpace(savedRoot) &&
                                Directory.Exists(savedRoot) &&
                                string.Equals(
                                    NormalizeSharedRoot(cachedRoot),
                                    NormalizeSharedRoot(savedRoot),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                App.LocalIndex.Set(cachedItems);
                                DropboxIndexCoordinator.MarkReady(savedRoot);
                            }
                            else
                            {
                                // Si la ruta local cambió o no existe, todavía es
                                // válido reutilizar las páginas Notion del caché.
                                var notionOnly = cachedItems
                                    .Where(item => item.Source == SearchSource.Notion)
                                    .ToList();

                                if (notionOnly.Count > 0)
                                    App.LocalIndex.Set(notionOnly);
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(savedRoot))
                    {
                        DROPBOX_ROOT = savedRoot;

                        if (Directory.Exists(savedRoot))
                        {
                            if (string.IsNullOrWhiteSpace(_currentFolderPath))
                                _currentFolderPath = savedRoot;

                            var hasLocalRows = App.LocalIndex
                                .GetAll()
                                .Any(item => item.Source != SearchSource.Notion);

                            // Primera ejecución sin caché local: sí hay que construir
                            // el índice. Las siguientes ejecuciones usan caché.
                            if (!hasLocalRows &&
                                !DropboxIndexCoordinator.IsIndexing)
                            {
                                await ReindexCurrentRootAsync(background: false);
                            }
                            else if (hasLocalRows &&
                                     !DropboxIndexCoordinator.IsIndexing)
                            {
                                var lastIndexedStr =
                                    ApplicationData.Current.LocalSettings.Values[
                                        LS_LastIndexedUtc] as string;

                                DateTimeOffset? lastIndexedUtc = null;

                                if (!string.IsNullOrWhiteSpace(lastIndexedStr) &&
                                    DateTimeOffset.TryParse(
                                        lastIndexedStr,
                                        out var parsed))
                                {
                                    lastIndexedUtc = parsed.ToUniversalTime();
                                }

                                var folderLastWriteUtc =
                                    Directory.GetLastWriteTimeUtc(savedRoot);

                                backgroundLocalReindexNeeded =
                                    lastIndexedUtc == null ||
                                    folderLastWriteUtc >
                                        lastIndexedUtc.Value.UtcDateTime;
                            }
                        }
                    }

                    // 2) Si el caché ya contiene Notion, NO hacer BuildManyAsync.
                    // El watcher hará únicamente la actualización incremental.
                    var hasCachedNotion = App.LocalIndex
                        .GetAll()
                        .Any(item => item.Source == SearchSource.Notion);

                    if (!hasCachedNotion)
                    {
                        await TryLoadNotionIndexOnStartupAsync(
                            CancellationToken.None);
                    }

                    CommandsSidebarList.ItemsSource = _savedSearches;
                    RefreshCommandsSidebarUi();

                    MarkSharedBootstrapReady(savedRoot);

                    await PaintSharedIndexForThisViewAsync();

                    perf.Stop();
                    Debug.WriteLine(
                        $"[PERF] Shared index bootstrap: {perf.ElapsedMilliseconds} ms · " +
                        $"items={App.LocalIndex.Count} · cachedNotion={hasCachedNotion}");

                    // El reindex local por cambios del filesystem ya no bloquea
                    // el arranque. Se conserva el snapshot visible mientras corre.
                    if (backgroundLocalReindexNeeded)
                    {
                        _ = ReindexCurrentRootAsync(background: true);
                    }

                    if (!App.LocalIndex.HasData && !DeferInitialIndexPaint)
                    {
                        StatusText.Text =
                            string.IsNullOrWhiteSpace(savedRoot)
                                ? "Estado: No hay índice cargado. Configura Dropbox o Notion en Settings."
                                : "Estado: No hay índice cargado. Revisa la configuración en Settings.";
                    }
                }
                finally
                {
                    SharedBootstrapLock.Release();
                }
            }
            finally
            {
                _bootstrappedOnce = true;
                _bootstrapLock.Release();

                if (!DeferInitialIndexPaint)
                    HideLoadingState();
            }
        }

        private async Task ReindexCurrentRootAsync(bool background = false)
        {
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) ||
                !Directory.Exists(DROPBOX_ROOT))
            {
                return;
            }

            var gateTaken = background
                ? await SharedLocalReindexGate.WaitAsync(0)
                : await SharedLocalReindexGate.WaitAsync(
                    TimeSpan.FromMinutes(10));

            if (!gateTaken)
                return;

            try
            {
                _autoReindexCts?.Cancel();
                _autoReindexCts = new CancellationTokenSource();
                var ct = _autoReindexCts.Token;

                if (!background)
                {
                    ShowLoadingState(
                        "Estado: Sincronizando Dropbox...",
                        "Construyendo el índice local inicial.");

                    DropboxIndexCoordinator.StartIndexing(DROPBOX_ROOT);
                }

                var perf = Stopwatch.StartNew();
                var localRows =
                    await LocalIndexBuilder.BuildAsync(DROPBOX_ROOT, ct);

                if (localRows == null || localRows.Count == 0)
                {
                    if (!background)
                    {
                        StatusText.Text =
                            "Estado: Reindex produjo 0 items. Conservo el índice anterior.";
                        DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);
                    }

                    return;
                }

                // Nunca borrar el índice visible mientras se reconstruye Dropbox.
                // Al terminar mezclamos el local nuevo con el snapshot Notion más
                // reciente, incluyendo cambios que hayan llegado durante el build.
                var sharedSnapshot = App.LocalIndex.GetAll();

                var notionRows = sharedSnapshot
                    .Where(item => item.Source == SearchSource.Notion)
                    .ToList();

                var localTargets = localRows
                    .Select(item => NormalizePath(item.Target))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var remoteOnlyDropboxRows = sharedSnapshot
                    .Where(item =>
                        item.Source == SearchSource.Dropbox &&
                        !localTargets.Contains(
                            NormalizePath(item.Target)))
                    .ToList();

                localRows.AddRange(remoteOnlyDropboxRows);
                localRows.AddRange(notionRows);
                App.LocalIndex.Set(localRows);

                await LocalIndexPersistence.SaveAsync(
                    DROPBOX_ROOT,
                    localRows,
                    ct);

                ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] =
                    DateTimeOffset.UtcNow.ToString("O");

                if (!background)
                    DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);

                perf.Stop();
                Debug.WriteLine(
                    $"[PERF] Local index rebuild: {perf.ElapsedMilliseconds} ms · " +
                    $"local={localRows.Count - notionRows.Count - remoteOnlyDropboxRows.Count} · " +
                    $"remoteOnly={remoteOnlyDropboxRows.Count} · notion={notionRows.Count}");

                if (!DeferInitialIndexPaint)
                {
                    await RefreshAfterBackgroundIndexChangeAsync(
                        SearchSource.Dropbox);

                    StatusText.Text = background
                        ? $"Estado: Índice local actualizado en segundo plano ✅ ({App.LocalIndex.Count})"
                        : $"Estado: Reindex listo ({App.LocalIndex.Count} items)";
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!background)
                    DropboxIndexCoordinator.MarkError(DROPBOX_ROOT, ex.Message);

                if (!DeferInitialIndexPaint)
                    StatusText.Text = $"Estado: Error Dropbox → {ex.Message}";
            }
            finally
            {
                SharedLocalReindexGate.Release();

                if (!background)
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
            var sameContent =
                string.Equals(existing.DisplayName, incoming.DisplayName, StringComparison.Ordinal) &&
                string.Equals(existing.Description ?? string.Empty, incoming.Description ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(existing.ProjectUpdateStatus ?? string.Empty, incoming.ProjectUpdateStatus ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(existing.ScheduledDate ?? string.Empty, incoming.ScheduledDate ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(existing.ServerModified ?? string.Empty, incoming.ServerModified ?? string.Empty, StringComparison.Ordinal);

            if (sameContent)
                return false;

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

        private static void QueueCompletedReminderTrashCleanup(
            string token,
            IEnumerable<SearchResultRow> rows)
        {
            if (string.IsNullOrWhiteSpace(token) ||
                rows == null)
            {
                return;
            }

            var ids =
                rows
                    .Where(NotionIndexBuilder
                        .IsCompletedReminderNotification)
                    .Select(GetNotionIdentity)
                    .Where(id =>
                        !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (ids.Count == 0)
                return;

            var queuedIds =
                new List<string>();

            lock (CompletedReminderCleanupLock)
            {
                foreach (var id in ids)
                {
                    if (CompletedReminderCleanupQueued.Add(id))
                        queuedIds.Add(id);
                }
            }

            if (queuedIds.Count == 0)
                return;

            _ = Task.Run(
                async () =>
                {
                    var service =
                        new NotionPageActionsService();

                    foreach (var id in queuedIds)
                    {
                        try
                        {
                            using var cts =
                                new CancellationTokenSource(
                                    TimeSpan.FromMinutes(2));

                            await service.MovePageToTrashAsync(
                                token,
                                id,
                                cts.Token);
                        }
                        catch
                        {
                            // La página se mantiene oculta localmente. La
                            // siguiente sincronización volverá a intentar.
                        }
                        finally
                        {
                            lock (CompletedReminderCleanupLock)
                            {
                                CompletedReminderCleanupQueued.Remove(
                                    id);
                            }
                        }
                    }
                });
        }

        private async Task<int>
            RemoveCompletedReminderNotificationsFromLocalIndexAsync(
                string token,
                IEnumerable<string>? additionalPageIds = null)
        {
            var current =
                App.LocalIndex.GetAll().ToList();

            var completedRows =
                current
                    .Where(NotionIndexBuilder
                        .IsCompletedReminderNotification)
                    .ToList();

            QueueCompletedReminderTrashCleanup(
                token,
                completedRows);

            var ids =
                completedRows
                    .Select(GetNotionIdentity)
                    .Concat(
                        additionalPageIds ??
                        Array.Empty<string>())
                    .Where(id =>
                        !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            if (ids.Count == 0)
                return 0;

            var removed =
                current.RemoveAll(row =>
                    row.Source == SearchSource.Notion &&
                    ids.Contains(
                        GetNotionIdentity(row)));

            if (removed <= 0)
                return 0;

            App.LocalIndex.Set(current);

            await PersistCombinedIndexIfPossibleAsync(
                current);

            return removed;
        }

        private static List<SearchResultRow>
            FilterCompletedReminderNotifications(
                string token,
                IEnumerable<SearchResultRow> rows)
        {
            var snapshot =
                (rows ??
                 Array.Empty<SearchResultRow>())
                    .ToList();

            QueueCompletedReminderTrashCleanup(
                token,
                snapshot);

            return snapshot
                .Where(row =>
                    !NotionIndexBuilder
                        .IsCompletedReminderNotification(row))
                .ToList();
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
                ShowNotionSyncNotice(
                    "Notion no configurado",
                    isError: true,
                    visibleSeconds: 7);

                if (!automatic)
                    StatusText.Text = "Estado: Notion no configurado.";

                return;
            }

            var sharedSyncTaken = automatic
                ? await SharedNotionSyncGate.WaitAsync(0)
                : await SharedNotionSyncGate.WaitAsync(
                    TimeSpan.FromMinutes(10));

            if (!sharedSyncTaken)
                return;

            _notionSyncRunning = true;
            BtnRefreshNotion.Visibility = Visibility.Collapsed;

            // Cuando el usuario está viendo solo Dropbox, la revisión automática
            // de Notion debe ser silenciosa: actualiza el índice interno, pero no
            // muestra overlay, no cambia el estado inferior y no repinta la lista.
            var showNotionUi =
                !automatic ||
                _activeSourceScope != SearchSourceScope.Dropbox;

            // La sincronización de Notion usa únicamente el estado inferior
            // y el aviso compacto. No mostramos el overlay central.

            try
            {
                if (!automatic)
                    StatusText.Text = "Estado: Revisando cambios de Notion...";

                var syncAnchorUtc = DateTimeOffset.UtcNow;

                List<SearchResultRow> changedItems;
                DateTimeOffset calendarChangeAnchorUtc =
                    syncAnchorUtc.Subtract(
                        TimeSpan.FromMinutes(3));

                if (!string.IsNullOrWhiteSpace(lastSyncStr) &&
                    DateTimeOffset.TryParse(lastSyncStr, out var lastSyncUtc))
                {
                    var overlapAnchor =
                        lastSyncUtc
                            .ToUniversalTime()
                            .Subtract(TimeSpan.FromMinutes(3));

                    calendarChangeAnchorUtc =
                        overlapAnchor;

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

                var completedChangedIds =
                    changedItems
                        .Where(NotionIndexBuilder
                            .IsCompletedReminderNotification)
                        .Select(GetNotionIdentity)
                        .Where(id =>
                            !string.IsNullOrWhiteSpace(id))
                        .ToHashSet(
                            StringComparer.OrdinalIgnoreCase);

                QueueCompletedReminderTrashCleanup(
                    token,
                    changedItems);

                changedItems =
                    changedItems
                        .Where(row =>
                            !NotionIndexBuilder
                                .IsCompletedReminderNotification(row))
                        .ToList();

                await RemoveCompletedReminderNotificationsFromLocalIndexAsync(
                    token,
                    completedChangedIds);

                var cobrosChanged =
                    changedItems.Any(item =>
                        string.Equals(
                            item.ExternalSourceName,
                            "Cobrar y pagar",
                            StringComparison.OrdinalIgnoreCase));

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

                        // Si cambió Due Fecha Recordatorio, quita de inmediato
                        // el cobro del día anterior y lo pinta en el día nuevo.
                        if (cobrosChanged &&
                            _calendarViewActive)
                        {
                            _calendarCobroOverlayCache.Clear();
                            _calendarCobroCacheIndexVersion =
                                App.LocalIndex.Version;

                            RefreshCalendarExternalOverlaysIfNeeded(
                                force: true);
                        }

                        if (_activeSourceScope != SearchSourceScope.Dropbox)
                            await RefreshCurrentViewPreservingScopeAsync();
                    }
                }

                var revisionesChanged =
                    changedItems.Any(item =>
                        string.Equals(
                            item.ExternalSourceName,
                            "Revisiones",
                            StringComparison.OrdinalIgnoreCase));

                if (revisionesChanged)
                {
                    await RefreshCalendarAfterNotionChangesAsync(
                        calendarChangeAnchorUtc);
                }

                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] =
                    syncAnchorUtc.ToString("O");

                BtnRefreshNotion.Visibility = Visibility.Collapsed;

                if (showNotionUi)
                {
                    StatusText.Text = changedItems.Count > 0
                        ? $"Estado: Notion actualizado automáticamente ✅ Cambios aplicados: {changedItems.Count}"
                        : "Estado: Notion sin cambios ✅";
                }
            }
            catch (Exception ex)
            {
                ShowNotionSyncNotice(
                    "Error al revisar Notion",
                    isError: true,
                    visibleSeconds: 8);

                if (showNotionUi)
                    StatusText.Text = $"Estado: Error actualizando Notion → {ex.Message}";
            }
            finally
            {
                _notionSyncRunning = false;
                BtnRefreshNotion.Visibility = Visibility.Collapsed;
                BtnRefreshNotion.IsEnabled = true;
                SharedNotionSyncGate.Release();

                // No hay overlay central que cerrar para Notion.
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

                notionItems =
                    FilterCompletedReminderNotifications(
                        token,
                        notionItems);

                var currentWithoutNotion = App.LocalIndex
                    .GetAll()
                    .Where(x => x.Source != SearchSource.Notion)
                    .ToList();

                currentWithoutNotion.AddRange(notionItems);

                App.LocalIndex.Set(currentWithoutNotion);

                // La primera descarga completa se guarda para que la siguiente
                // ejecución arranque desde caché y solo consulte cambios.
                await PersistCombinedIndexIfPossibleAsync(
                    currentWithoutNotion);

                // Esta descarga completa ya usa el mapeo vigente de Cobros.
                ApplicationData.Current.LocalSettings.Values[
                    LS_CobrosCalendarMappingVersion] =
                    CobrosCalendarMappingVersion;

                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] =
                    DateTimeOffset.UtcNow.ToString("O");

                StatusText.Text = $"Estado: Notion cargado ✅ ({notionItems.Count} páginas)";
                // Revisión automática silenciosa.

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

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    LS_NotionToken] as string ??
                string.Empty;

            await RemoveCompletedReminderNotificationsFromLocalIndexAsync(
                token);

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

            _lastPaintedIndexVersion = App.LocalIndex.Version;
        }
        #endregion 
        #region ===== Dropbox incremental sync =====

        private async Task<string?> LoadDropboxSyncCursorAsync()
        {
            try
            {
                var file =
                    await ApplicationData.Current.LocalFolder
                        .TryGetItemAsync(
                            DropboxSyncCursorFileName)
                    as StorageFile;

                if (file != null)
                {
                    var saved =
                        (await FileIO.ReadTextAsync(file))
                        ?.Trim();

                    if (!string.IsNullOrWhiteSpace(saved))
                        return saved;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DROPBOX_CURSOR] No se pudo leer el cursor desde archivo: {ex.Message}");
            }

            // Migración desde versiones anteriores:
            // si todavía existe un cursor pequeño en LocalSettings, se reutiliza
            // una vez y se mueve al archivo para no volver a topar el límite.
            var values =
                ApplicationData.Current.LocalSettings.Values;

            var legacyCursor =
                values[LS_DropboxSyncCursor] as string;

            if (string.IsNullOrWhiteSpace(legacyCursor))
                return null;

            try
            {
                await SaveDropboxSyncCursorAsync(
                    legacyCursor);

                values.Remove(
                    LS_DropboxSyncCursor);

                Debug.WriteLine(
                    "[DROPBOX_CURSOR] Cursor migrado de LocalSettings a LocalFolder.");
            }
            catch (Exception ex)
            {
                // Aunque la migración falle, todavía podemos usar el cursor
                // recuperado durante esta ejecución.
                Debug.WriteLine(
                    $"[DROPBOX_CURSOR] No se pudo migrar el cursor: {ex.Message}");
            }

            return legacyCursor;
        }

        private static async Task SaveDropboxSyncCursorAsync(
            string? cursor)
        {
            var clean =
                (cursor ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return;

            var file =
                await ApplicationData.Current.LocalFolder
                    .CreateFileAsync(
                        DropboxSyncCursorFileName,
                        CreationCollisionOption.ReplaceExisting);

            await FileIO.WriteTextAsync(
                file,
                clean);
        }

        private void StartLocalFileWatcher()
        {
            if (DeferInitialIndexPaint ||
                string.IsNullOrWhiteSpace(DROPBOX_ROOT) ||
                !Directory.Exists(DROPBOX_ROOT))
            {
                return;
            }

            var normalizedRoot =
                NormalizePath(DROPBOX_ROOT);

            if (_localFileWatcher != null &&
                string.Equals(
                    _localFileWatcherRoot,
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopLocalFileWatcher();

            try
            {
                var watcher =
                    new FileSystemWatcher(DROPBOX_ROOT)
                    {
                        IncludeSubdirectories = true,
                        Filter = "*",
                        NotifyFilter =
                            NotifyFilters.FileName |
                            NotifyFilters.DirectoryName |
                            NotifyFilters.LastWrite |
                            NotifyFilters.Size |
                            NotifyFilters.CreationTime,
                        InternalBufferSize = 64 * 1024,
                        EnableRaisingEvents = false
                    };

                watcher.Created += LocalFileWatcher_Created;
                watcher.Changed += LocalFileWatcher_Changed;
                watcher.Deleted += LocalFileWatcher_Deleted;
                watcher.Renamed += LocalFileWatcher_Renamed;
                watcher.Error += LocalFileWatcher_Error;

                watcher.EnableRaisingEvents = true;

                _localFileWatcher = watcher;
                _localFileWatcherRoot = normalizedRoot;

                Debug.WriteLine(
                    $"[LOCAL_FS] Watcher activo: {DROPBOX_ROOT}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[LOCAL_FS] No se pudo iniciar watcher: {ex.Message}");
            }
        }

        private void StopLocalFileWatcher()
        {
            try
            {
                _localFsDebounceCts?.Cancel();
            }
            catch
            {
            }

            _localFsDebounceCts = null;

            lock (_localFsPendingLock)
            {
                _localFsPendingChanges.Clear();
            }

            if (_localFileWatcher == null)
            {
                _localFileWatcherRoot = string.Empty;
                return;
            }

            try
            {
                _localFileWatcher.EnableRaisingEvents = false;

                _localFileWatcher.Created -= LocalFileWatcher_Created;
                _localFileWatcher.Changed -= LocalFileWatcher_Changed;
                _localFileWatcher.Deleted -= LocalFileWatcher_Deleted;
                _localFileWatcher.Renamed -= LocalFileWatcher_Renamed;
                _localFileWatcher.Error -= LocalFileWatcher_Error;

                _localFileWatcher.Dispose();
            }
            catch
            {
            }
            finally
            {
                _localFileWatcher = null;
                _localFileWatcherRoot = string.Empty;
            }
        }

        private void LocalFileWatcher_Created(
            object sender,
            FileSystemEventArgs e)
        {
            QueueLocalFileSystemChange(
                e.FullPath,
                upsert: true);
        }

        private void LocalFileWatcher_Changed(
            object sender,
            FileSystemEventArgs e)
        {
            // Los cambios de carpeta generan mucho ruido. Para una carpeta
            // solo necesitamos Created/Deleted/Renamed; Changed se usa para
            // actualizar archivos modificados y moverlos arriba por fecha.
            if (File.Exists(e.FullPath))
            {
                QueueLocalFileSystemChange(
                    e.FullPath,
                    upsert: true);
            }
        }

        private void LocalFileWatcher_Deleted(
            object sender,
            FileSystemEventArgs e)
        {
            QueueLocalFileSystemChange(
                e.FullPath,
                upsert: false);
        }

        private void LocalFileWatcher_Renamed(
            object sender,
            RenamedEventArgs e)
        {
            QueueLocalFileSystemChange(
                e.OldFullPath,
                upsert: false);

            QueueLocalFileSystemChange(
                e.FullPath,
                upsert: true);
        }

        private void LocalFileWatcher_Error(
            object sender,
            ErrorEventArgs e)
        {
            var message =
                e.GetException()?.Message ??
                "El watcher local perdió eventos.";

            Debug.WriteLine(
                $"[LOCAL_FS] Error: {message}");

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!DeferInitialIndexPaint)
                {
                    StatusText.Text =
                        "Estado: Cambio local grande detectado; " +
                        "revisando carpeta en segundo plano...";
                }

                // FileSystemWatcher puede desbordar su buffer en copias masivas.
                // En ese caso hacemos una reconstrucción segura una sola vez.
                _ = ReindexCurrentRootAsync(
                    background: true);
            });
        }

        private void QueueLocalFileSystemChange(
            string? path,
            bool upsert)
        {
            var clean =
                (path ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean) ||
                !IsPathInsideCurrentLocalRoot(clean))
            {
                return;
            }

            CancellationTokenSource debounce;

            lock (_localFsPendingLock)
            {
                // La última operación sobre una misma ruta gana.
                _localFsPendingChanges[clean] =
                    upsert;

                try
                {
                    _localFsDebounceCts?.Cancel();
                }
                catch
                {
                }

                _localFsDebounceCts =
                    new CancellationTokenSource();

                debounce =
                    _localFsDebounceCts;
            }

            _ = DebounceLocalFileSystemChangesAsync(
                debounce);
        }

        private async Task DebounceLocalFileSystemChangesAsync(
            CancellationTokenSource debounce)
        {
            try
            {
                await Task.Delay(
                    650,
                    debounce.Token);

                Dictionary<string, bool> batch;

                lock (_localFsPendingLock)
                {
                    if (!ReferenceEquals(
                            _localFsDebounceCts,
                            debounce))
                    {
                        return;
                    }

                    batch =
                        new Dictionary<string, bool>(
                            _localFsPendingChanges,
                            StringComparer.OrdinalIgnoreCase);

                    _localFsPendingChanges.Clear();
                    _localFsDebounceCts = null;
                }

                if (batch.Count == 0)
                    return;

                DispatcherQueue.TryEnqueue(
                    async () =>
                    {
                        await ApplyLocalFileSystemChangesAsync(
                            batch);
                    });
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                try
                {
                    debounce.Dispose();
                }
                catch
                {
                }
            }
        }

        private bool IsPathInsideCurrentLocalRoot(
            string path)
        {
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT))
                return false;

            try
            {
                var root =
                    Path.GetFullPath(DROPBOX_ROOT)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);

                var candidate =
                    Path.GetFullPath(path);

                return string.Equals(
                           candidate,
                           root,
                           StringComparison.OrdinalIgnoreCase) ||
                       candidate.StartsWith(
                           root + Path.DirectorySeparatorChar,
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async Task ApplyLocalFileSystemChangesAsync(
            IReadOnlyDictionary<string, bool> changes)
        {
            if (changes == null ||
                changes.Count == 0 ||
                string.IsNullOrWhiteSpace(DROPBOX_ROOT) ||
                !Directory.Exists(DROPBOX_ROOT))
            {
                return;
            }

            if (!await SharedLocalFsChangeGate.WaitAsync(0))
            {
                // Otra pestaña de SearchView puede estar aplicando exactamente
                // el mismo evento sobre el índice compartido. Esperamos a que
                // termine y solo repintamos esta vista para que la pestaña
                // visible no se quede atrás.
                await SharedLocalFsChangeGate.WaitAsync();

                try
                {
                    if (!DeferInitialIndexPaint)
                    {
                        await RefreshAfterBackgroundIndexChangeAsync(
                            SearchSource.Dropbox);
                    }
                }
                finally
                {
                    SharedLocalFsChangeGate.Release();
                }

                return;
            }

            try
            {
                var snapshot =
                    App.LocalIndex.GetAll().ToList();

                var changedCount = 0;

                // Primero eliminaciones/renombres viejos. Esto evita dejar
                // rutas fantasma cuando un archivo o carpeta cambió de nombre.
                foreach (var change in changes
                    .Where(pair => !pair.Value))
                {
                    changedCount +=
                        RemoveLocalPathFromSnapshot(
                            snapshot,
                            change.Key);
                }

                foreach (var change in changes
                    .Where(pair => pair.Value))
                {
                    var path =
                        change.Key;

                    if (!File.Exists(path) &&
                        !Directory.Exists(path))
                    {
                        continue;
                    }

                    var rows =
                        await Task.Run(
                            () => BuildLocalRowsForChangedPath(
                                path));

                    foreach (var row in rows)
                    {
                        var normalized =
                            NormalizePath(row.Target);

                        var existingIndex =
                            snapshot.FindIndex(item =>
                                item.Source != SearchSource.Notion &&
                                string.Equals(
                                    NormalizePath(item.Target),
                                    normalized,
                                    StringComparison.OrdinalIgnoreCase));

                        if (existingIndex >= 0)
                            snapshot[existingIndex] = row;
                        else
                            snapshot.Add(row);

                        changedCount++;
                    }
                }

                if (changedCount <= 0)
                    return;

                App.LocalIndex.Set(snapshot);

                await LocalIndexPersistence.SaveAsync(
                    DROPBOX_ROOT,
                    snapshot,
                    CancellationToken.None);

                ApplicationData.Current.LocalSettings.Values[
                    LS_LastIndexedUtc] =
                    DateTimeOffset.UtcNow.ToString("O");

                if (!DeferInitialIndexPaint)
                {
                    await RefreshAfterBackgroundIndexChangeAsync(
                        SearchSource.Dropbox);

                    StatusText.Text =
                        changedCount == 1
                            ? "Estado: Cambio local detectado y actualizado ✅"
                            : $"Estado: {changedCount} cambios locales actualizados ✅";
                }

                Debug.WriteLine(
                    $"[LOCAL_FS] Cambios aplicados: {changedCount}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[LOCAL_FS] Error aplicando cambios: {ex.Message}");

                if (!DeferInitialIndexPaint)
                {
                    StatusText.Text =
                        $"Estado: Error actualizando carpeta local → {ex.Message}";
                }
            }
            finally
            {
                SharedLocalFsChangeGate.Release();
            }
        }

        private static int RemoveLocalPathFromSnapshot(
            List<SearchResultRow> snapshot,
            string path)
        {
            if (snapshot == null ||
                string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            var normalized =
                NormalizePath(path);

            var prefix =
                EnsureDirPrefix(normalized);

            return snapshot.RemoveAll(row =>
                row.Source != SearchSource.Notion &&
                (string.Equals(
                     NormalizePath(row.Target),
                     normalized,
                     StringComparison.OrdinalIgnoreCase) ||
                 NormalizePath(row.Target).StartsWith(
                     prefix,
                     StringComparison.OrdinalIgnoreCase)));
        }

        private static List<SearchResultRow>
            BuildLocalRowsForChangedPath(
                string path)
        {
            var rows =
                new List<SearchResultRow>();

            if (File.Exists(path))
            {
                TryAddLocalFileRow(
                    rows,
                    path);

                return rows;
            }

            if (!Directory.Exists(path))
                return rows;

            // Incluye la propia carpeta creada/renombrada.
            rows.Add(
                new SearchResultRow
                {
                    Name = Path.GetFileName(
                        path.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar)),
                    Target = path,
                    Type = "FOLDER",
                    Source = SearchSource.Local
                });

            try
            {
                foreach (var directory in
                    Directory.EnumerateDirectories(
                        path,
                        "*",
                        SearchOption.AllDirectories))
                {
                    rows.Add(
                        new SearchResultRow
                        {
                            Name = Path.GetFileName(directory),
                            Target = directory,
                            Type = "FOLDER",
                            Source = SearchSource.Local
                        });
                }
            }
            catch
            {
                // Una subcarpeta puede desaparecer mientras se enumera.
            }

            try
            {
                foreach (var file in
                    Directory.EnumerateFiles(
                        path,
                        "*",
                        SearchOption.AllDirectories))
                {
                    TryAddLocalFileRow(
                        rows,
                        file);
                }
            }
            catch
            {
                // Archivos temporales pueden desaparecer durante una copia.
            }

            return rows;
        }

        private static void TryAddLocalFileRow(
            List<SearchResultRow> rows,
            string file)
        {
            try
            {
                var info =
                    new FileInfo(file);

                if (!info.Exists)
                    return;

                rows.Add(
                    new SearchResultRow
                    {
                        Name = info.Name,
                        Target = info.FullName,
                        Type = "FILE",
                        Size = info.Length,
                        ServerModified =
                            info.LastWriteTime.ToString(
                                "yyyy-MM-dd HH:mm"),
                        Source = SearchSource.Local
                    });
            }
            catch
            {
                // Si el archivo aún se está copiando, otro Changed volverá a
                // entrar al debounce y se intentará de nuevo.
            }
        }

        private void StartDropboxChangeWatcher()
        {
            if (DeferInitialIndexPaint)
                return;

            // Este watcher es independiente de Dropbox API. Funciona también
            // si la ruta configurada es una carpeta local normal de Windows.
            StartLocalFileWatcher();

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
            StopLocalFileWatcher();

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

            if (!ReserveSharedProbe(
                    ref SharedLastDropboxProbeUtc,
                    TimeSpan.FromSeconds(30)))
            {
                return;
            }

            if (!await SharedDropboxSyncGate.WaitAsync(0))
                return;

            _dropboxSyncRunning = true;

            try
            {
                var cursor =
                    await LoadDropboxSyncCursorAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var batch = await _dropboxSyncService.GetChangesAsync(cursor, cts.Token);

                // El cursor de Dropbox puede crecer bastante. Se guarda como
                // archivo para evitar el límite por valor de LocalSettings.
                await SaveDropboxSyncCursorAsync(
                    batch.Cursor);

                // Limpieza defensiva por si una versión anterior dejó aquí
                // un cursor pequeño.
                ApplicationData.Current.LocalSettings.Values.Remove(
                    LS_DropboxSyncCursor);

                if (batch.CursorInitialized)
                {
                    StatusText.Text = "Estado: Dropbox preparado para sincronización automática ✅";
                    return;
                }

                if (batch.Changes.Count == 0)
                    return;

                // Sin overlay central: los cambios de Dropbox se aplican en
                // segundo plano y la interfaz permanece utilizable.
                StatusText.Text =
                    $"Estado: Aplicando {batch.Changes.Count} cambio(s) de Dropbox...";

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
                SharedDropboxSyncGate.Release();
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
            if (DeferInitialIndexPaint || _notionChangeTimer != null)
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

        private void StopNotionChangeWatcher()
        {
            if (_notionChangeTimer == null)
                return;

            _notionChangeTimer.Stop();
            _notionChangeTimer = null;
        }

        private async Task CheckNotionChangesAsync()
        {
            if (_notionSyncRunning)
                return;

            if (!ReserveSharedProbe(
                    ref SharedLastNotionProbeUtc,
                    TimeSpan.FromSeconds(30)))
            {
                return;
            }

            if (!await SharedNotionProbeGate.WaitAsync(0))
                return;

            try
            {
                var token =
                    ApplicationData.Current.LocalSettings.Values[
                        LS_NotionToken] as string;

                var dataSourceId =
                    ApplicationData.Current.LocalSettings.Values[
                        LS_NotionDataSourceId] as string;

                var lastSyncStr =
                    ApplicationData.Current.LocalSettings.Values[
                        LS_NotionLastSyncUtc] as string;

                BtnRefreshNotion.Visibility = Visibility.Collapsed;

                if (string.IsNullOrWhiteSpace(token) ||
                    string.IsNullOrWhiteSpace(dataSourceId) ||
                    string.IsNullOrWhiteSpace(lastSyncStr) ||
                    !DateTimeOffset.TryParse(
                        lastSyncStr,
                        out var lastSyncUtc))
                {
                    HideNotionSyncNotice();
                    return;
                }

                var overlapAnchor =
                    lastSyncUtc
                        .ToUniversalTime()
                        .Subtract(TimeSpan.FromMinutes(3));

                var hasChanges =
                    await NotionIndexBuilder.HasAnyChangesSinceAsync(
                        token,
                        NotionDataSources.Default,
                        overlapAnchor,
                        CancellationToken.None);

                if (hasChanges)
                {
                    await RefreshNotionIncrementalAsync(automatic: true);
                    return;
                }

                ShowNotionSyncNotice(
                    "Notion al día",
                    visibleSeconds: 3);
            }
            catch
            {
                BtnRefreshNotion.Visibility = Visibility.Collapsed;
                ShowNotionSyncNotice(
                    "Error al revisar Notion",
                    isError: true,
                    visibleSeconds: 8);
            }
            finally
            {
                SharedNotionProbeGate.Release();
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

                freshNotionItems =
                    FilterCompletedReminderNotifications(
                        token,
                        freshNotionItems);

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

                freshNotionItems =
                    FilterCompletedReminderNotifications(
                        token,
                        freshNotionItems);

                var currentWithoutNotion = App.LocalIndex
                    .GetAll()
                    .Where(x => x.Source != SearchSource.Notion)
                    .ToList();

                currentWithoutNotion.AddRange(freshNotionItems);

                App.LocalIndex.Set(currentWithoutNotion);
                await PersistCombinedIndexIfPossibleAsync(currentWithoutNotion);

                ApplicationData.Current.LocalSettings.Values[
                    LS_CobrosCalendarMappingVersion] =
                    CobrosCalendarMappingVersion;

                var now = DateTimeOffset.UtcNow.ToString("O");
                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] = now;

                BtnRefreshNotion.Visibility = Visibility.Collapsed;
                ShowNotionSyncNotice(
                    "Notion al día",
                    visibleSeconds: 4);

                if (_activeSourceScope != SearchSourceScope.Dropbox)
                    await RefreshCurrentViewPreservingScopeAsync();

                await RebuildCalendarCacheAfterFullSyncAsync();

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