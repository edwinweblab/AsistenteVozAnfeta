using Anfeta.UI.Models.Notion;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Notion
{
    public sealed record NotionCalendarProgress(
        string Stage,
        int Current,
        int Total,
        string Detail)
    {
        public int Percentage =>
            Total <= 0
                ? 0
                : Math.Clamp(
                    (int)Math.Round(Current * 100d / Total),
                    0,
                    100);
    }

    public sealed record NotionCalendarWarmupResult(
        bool HadSavedToday,
        bool Updated,
        int TodayCount,
        string Message);

    public sealed record NotionChecklistStats(
        int Total,
        int Completed)
    {
        public int Pending =>
            Math.Max(0, Total - Completed);

        public bool HasChecklist =>
            Total > 0;
    }

    public sealed record NotionCalendarScheduleUpdateResult(
        NotionCalendarActivity Activity,
        bool AuditLogWritten);

    public sealed record NotionActivityWorkUpdateResult(
        NotionCalendarActivity Activity,
        bool AuditLogWritten);

    public sealed class NotionCalendarService
    {
        public string LastDiagnostics { get; private set; } = "";

        // IDs detectados por la última actualización incremental.
        // SearchView los usa para refrescar solo esos checklist.
        public IReadOnlyList<string> LastChangedPageIds { get; private set; } =
            Array.Empty<string>();

        private const string CacheFileName =
            "notion_calendar_cache_v13.json";

        private static readonly SemaphoreSlim CacheLock =
            new(1, 1);

        // La caché del calendario se modifica con mucha frecuencia (checklist,
        // drag, cambios incrementales). Las escrituras a disco se agrupan para
        // no serializar el JSON completo decenas de veces seguidas.
        private static readonly SemaphoreSlim CacheWriteLock =
            new(1, 1);
        private static readonly object CacheSaveScheduleLock = new();
        private static CancellationTokenSource? _cacheSaveDebounceCts;

        // El esquema de Revisiones cambia muy pocas veces. No hace falta pedirlo
        // otra vez en cada comprobación incremental.
        private static readonly SemaphoreSlim SchemaCacheLock =
            new(1, 1);
        private static SchemaInfo? _cachedSchema;
        private static DateTimeOffset _schemaCachedAtUtc =
            DateTimeOffset.MinValue;
        private static readonly TimeSpan SchemaCacheLifetime =
            TimeSpan.FromMinutes(20);

        private static readonly Dictionary<string, List<NotionCalendarActivity>>
            DayCache =
                new(StringComparer.Ordinal);

        private static bool _cacheLoaded;

        private static readonly object StartupWarmupLock = new();
        private static Task<NotionCalendarWarmupResult>? _startupWarmupTask;

        // Evita que pestañas o refrescos simultáneos descarguen el mismo día
        // completo varias veces. Los consumidores comparten la tarea activa.
        private static readonly ConcurrentDictionary<
            string,
            Task<IReadOnlyList<NotionCalendarActivity>>> ActiveDayLoads =
                new(StringComparer.Ordinal);

        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";
        private const string RevisionesDataSourceId =
            "2eeabd7d-91b7-8193-a131-000b08cd54e2";

        private const int MaxRetryAttempts = 4;

        private static readonly Regex AuxiliaryMessageTitlePattern = new(
            @"^\s*\d{4}-\d{2}-\d{2}[ T]\d{2}[:\-]\d{2}\s+" +
            @"(?:jjohn|kkarl|iisai|iisaia|eedua|aacal|aandr|eemma|bbria|ggena|nneft|__all__)" +
            @"(?:\s+de:[a-z0-9_-]+)?(?:\s+\[(?:RESPUESTA|TERMINADO)\])?(?:\s+|$)",
            RegexOptions.Compiled |
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

        private static readonly string[] DateAliases =
        {
            // Única fuente válida para el calendario.
            // Debe ser una propiedad editable de tipo date.
            "Fecha POR Hacer",
            "Fecha por hacer"
        };

        private static readonly string[] ActivityCreatedDateAliases =
        {
            "Fecha de inicio de actividad (Creacion)",
            "Fecha de inicio de actividad (Creación)",
            "Fecha inicio actividad (Creacion)",
            "Fecha inicio actividad (Creación)"
        };

        private static readonly string[] InternalDeadlineDateAliases =
        {
            "Fecha límite interna PM",
            "Fecha limite interna PM",
            "Fecha límite interna pm",
            "Fecha limite interna pm"
        };

        private static readonly string[] PersonAliases =
        {
            "Asignee/Ejecutor Principal",
            "Assignee / Ejecutor Principal",
            "Equipo weblab",
            "Fórmula Persona",
            "Asignado por"
        };

        private static readonly Dictionary<string, string[]>
            WorkspacePersonLookup =
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["John"] = new[] { "jjohn", "john" },
                    ["Karla"] = new[] { "kkarl", "karla", "karl" },
                    ["Isaias"] = new[] { "iisai", "isaias", "isai" },
                    ["Sotelo"] = new[]
                    {
                        "ssote", "eedua", "sotelo",
                        "eduardo", "sote", "edua"
                    },
                    ["Acalli"] = new[] { "aacal", "acalli", "acal" },
                    ["Andrade"] = new[] { "aandr", "andrade", "andr" },
                    ["Emmanuel"] = new[]
                    {
                        "eemma", "emmanuel", "emanuel", "emma"
                    },
                    ["Brian"] = new[] { "bbria", "brian", "bria" },
                    ["Genaro"] = new[] { "ggena", "genaro", "gena" },
                    ["Neftali"] = new[] { "nneft", "neftali", "neft" }
                };

        private static readonly string[] ProjectAliases =
        {
            "Fórmula Agrupar Proyectos",
            "Fórmula Proyecto",
            "Proyectos",
            "Texto proyecto",
            "dominio"
        };

        private static readonly string[] StatusAliases =
        {
            "(bien) Estado opcion multiple revisiones",
            "Estado opcion multiple revisiones",
            "Estado opción múltiple revisiones",
            "Estado de trabajo",
            "Estado texto Actualización Proyecto",
            "Seguimiento Estado Proyecto",
            "Estatus IA"
        };

        private static readonly string[] UpdateTextAliases =
        {
            "Estado texto Actualización Proyecto",
            "Estado Texto Actualización Proyecto",
            "Estado texto actualización proyecto",
            "Estado texto",
            "Estado actualización",
            "Estado de actualización",
            "Actualización Proyecto",
            "Seguimiento Estado Proyecto",
            "Seguimiento",
            "Última actualización",
            "Ultima actualizacion"
        };

        private static readonly string[] DescriptionAliases =
        {
            "Descripción",
            "Descripcion",
            "Actividad",
            "Notas",
            "Comentario",
            "Resumen"
        };

        private static readonly string[] AuditLogAliases =
        {
            "Audit_FTF_Log",
            "Audit FTF Log",
            "Audit FTF",
            "FTF Audit Log"
        };

        private const string WorkLogPrefix =
            "[ANFETA_WORKLOG_V1]";

        private sealed class StoredActivityWorkLog
        {
            public int EstimateMinutes { get; set; }
            public Dictionary<string, int> MinutesByDate { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly string[] AutomationLockAliases =
        {
            "Bloqueada_ANFETA",
            "Bloqueada ANFETA",
            "Bloquear ANFETA",
            "Bloqueada para automatización",
            "Bloqueada para automatizacion"
        };

        private readonly ConcurrentDictionary<string, string> _relatedTitleCache =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, NotionChecklistStats>
            _checklistStatsCache =
                new(StringComparer.OrdinalIgnoreCase);

        // Candidatos de proyecto por tipo + mes lógico.
        // Se cachean unos minutos para que abrir varias tarjetas del mismo
        // proyecto no vuelva a consultar Notion cada vez.
        private sealed record ProjectCandidateCacheEntry(
            DateTimeOffset StoredAt,
            IReadOnlyList<NotionCalendarActivity> Activities);

        private static readonly ConcurrentDictionary<
            string,
            ProjectCandidateCacheEntry> ProjectCandidateCache =
                new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan ProjectCandidateCacheLifetime =
            TimeSpan.FromMinutes(15);

        private sealed record SchemaInfo(
            IReadOnlyList<string> DateProperties,
            string TitleProperty);

        public Task<NotionCalendarWarmupResult> StartStartupWarmupAsync(
            string token,
            DateTimeOffset? changedAfterUtc,
            CancellationToken cancellationToken = default)
        {
            lock (StartupWarmupLock)
            {
                if (_startupWarmupTask == null ||
                    _startupWarmupTask.IsCanceled ||
                    _startupWarmupTask.IsFaulted)
                {
                    _startupWarmupTask =
                        WarmupStartupCoreAsync(
                            token,
                            changedAfterUtc,
                            cancellationToken);
                }

                return _startupWarmupTask;
            }
        }

        private async Task<NotionCalendarWarmupResult> WarmupStartupCoreAsync(
            string token,
            DateTimeOffset? changedAfterUtc,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return new NotionCalendarWarmupResult(
                    false,
                    false,
                    0,
                    "Calendario sin token de Notion.");
            }

            await EnsureCacheLoadedAsync(cancellationToken);

            var savedToday =
                await TryGetCachedDayAsync(
                    DateTime.Today,
                    cancellationToken);

            var hadSavedToday =
                savedToday != null;

            var updated = false;

            if (hadSavedToday &&
                changedAfterUtc.HasValue)
            {
                updated =
                    await RefreshChangedSinceAsync(
                        token,
                        changedAfterUtc.Value
                            .ToUniversalTime()
                            .Subtract(TimeSpan.FromMinutes(3)),
                        cancellationToken);
            }
            else
            {
                // Solo cuando todavía no existe caché útil se construye Hoy.
                // En aperturas posteriores se usa actualización incremental.
                await GetDayAsync(
                    token,
                    DateTime.Today,
                    progress: null,
                    cancellationToken,
                    forceRefresh: true);

                updated = true;
            }

            // No se precargan Ayer y Mañana durante el arranque.
            // Cada día se obtiene bajo demanda para no competir con el índice
            // principal del Buscador ni consumir solicitudes innecesarias.
            var currentToday =
                await TryGetCachedDayAsync(
                    DateTime.Today,
                    cancellationToken);

            return new NotionCalendarWarmupResult(
                hadSavedToday,
                updated,
                currentToday?.Count ?? 0,
                updated
                    ? "Calendario actualizado con los cambios recientes."
                    : "La versión guardada ya estaba al día.");
        }

        public Task<IReadOnlyList<NotionCalendarActivity>> GetDayAsync(
            string token,
            DateTime localDate,
            IProgress<NotionCalendarProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false)
        {
            var key =
                $"{localDate.Date:yyyy-MM-dd}|{forceRefresh}";

            var task = ActiveDayLoads.GetOrAdd(
                key,
                _ => GetDayCoordinatedAsync(
                    token,
                    localDate,
                    progress,
                    cancellationToken,
                    forceRefresh));

            return AwaitSharedDayLoadAsync(key, task);
        }

        private static async Task<IReadOnlyList<NotionCalendarActivity>>
            AwaitSharedDayLoadAsync(
                string key,
                Task<IReadOnlyList<NotionCalendarActivity>> task)
        {
            try
            {
                return await task;
            }
            finally
            {
                if (ActiveDayLoads.TryGetValue(key, out var current) &&
                    ReferenceEquals(current, task))
                {
                    ActiveDayLoads.TryRemove(key, out _);
                }
            }
        }

        private async Task<IReadOnlyList<NotionCalendarActivity>>
            GetDayCoordinatedAsync(
                string token,
                DateTime localDate,
                IProgress<NotionCalendarProgress>? progress,
                CancellationToken cancellationToken,
                bool forceRefresh)
        {
            using var fullSyncLease =
                await NotionRequestCoordinator.EnterFullSyncAsync(
                    cancellationToken);

            return await GetDayCoreAsync(
                token,
                localDate,
                progress,
                cancellationToken,
                forceRefresh);
        }

        private async Task<IReadOnlyList<NotionCalendarActivity>> GetDayCoreAsync(
            string token,
            DateTime localDate,
            IProgress<NotionCalendarProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");

            await EnsureCacheLoadedAsync(
                cancellationToken);

            if (!forceRefresh)
            {
                var cached =
                    await TryGetCachedDayAsync(
                        localDate,
                        cancellationToken);

                if (cached != null)
                    return cached;
            }

            using var http = CreateClient(token);

            progress?.Report(
                new NotionCalendarProgress(
                    "Consultando estructura",
                    0,
                    1,
                    "Identificando las propiedades de Revisiones..."));

            var schema = await GetSchemaCachedAsync(
                http,
                cancellationToken);

            progress?.Report(
                new NotionCalendarProgress(
                    "Consultando actividades",
                    0,
                    1,
                    schema.DateProperties.Count > 0
                        ? $"Fechas candidatas: {string.Join(" · ", schema.DateProperties.Take(4))}"
                        : "No se detectaron propiedades de fecha."));

            if (schema.DateProperties.Count == 0)
            {
                throw new InvalidOperationException(
                    "No se encontró ninguna propiedad de fecha utilizable en Revisiones.");
            }

            var pages = await QueryDayPagesAsync(
                http,
                localDate.Date,
                schema.DateProperties[0],
                progress,
                cancellationToken);

            progress?.Report(
                new NotionCalendarProgress(
                    "Analizando páginas",
                    0,
                    pages.Count,
                    $"Se encontraron {pages.Count} páginas para revisar."));

            var activities = new List<NotionCalendarActivity>();
            var pagesWithDate = 0;
            var hydratedPages = 0;
            var parseFailures = 0;

            var datePropertyUsage =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

            for (var pageIndex = 0;
                 pageIndex < pages.Count;
                 pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var queryPage = pages[pageIndex];

                progress?.Report(
                    new NotionCalendarProgress(
                        "Analizando páginas",
                        pageIndex + 1,
                        pages.Count,
                        $"Revisando página {pageIndex + 1} de {pages.Count} · encontradas: {activities.Count}"));

                var page = queryPage;

                // La consulta de la base normalmente ya devuelve todas las propiedades.
                // Solo se reconsulta la página cuando las propiedades candidatas ni
                // siquiera vienen presentes; una fecha vacía no requiere otra petición.
                if (!HasAnyCandidateProperty(
                        page,
                        schema.DateProperties))
                {
                    var pageId = ReadString(
                        queryPage,
                        "id");

                    if (!string.IsNullOrWhiteSpace(pageId))
                    {
                        progress?.Report(
                            new NotionCalendarProgress(
                                "Leyendo propiedades",
                                pageIndex + 1,
                                pages.Count,
                                $"Consultando detalles de la página {pageIndex + 1}..."));

                        var hydrated =
                            await ReadPageAsync(
                                http,
                                pageId,
                                cancellationToken);

                        if (hydrated.HasValue)
                        {
                            page = hydrated.Value;
                            hydratedPages++;
                        }
                    }
                }

                if (TryReadCalendarDate(
                        page,
                        schema.DateProperties,
                        out _,
                        out _,
                        out _))
                {
                    pagesWithDate++;
                }

                var activity = await MapPageAsync(
                    http,
                    page,
                    schema,
                    cancellationToken);

                if (activity == null)
                {
                    parseFailures++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(
                        activity.DatePropertyName))
                {
                    datePropertyUsage[
                        activity.DatePropertyName] =
                            datePropertyUsage.TryGetValue(
                                activity.DatePropertyName,
                                out var usage)
                                ? usage + 1
                                : 1;
                }

                if (ActivityOverlapsDay(
                        activity,
                        localDate.Date))
                {
                    activities.Add(activity);
                }
            }

            var dateUsageText =
                datePropertyUsage.Count == 0
                    ? "sin propiedad"
                    : string.Join(
                        " | ",
                        datePropertyUsage
                            .OrderByDescending(item => item.Value)
                            .Select(item =>
                                $"{item.Key}: {item.Value}"));

            var peopleUsageText =
                activities.Count == 0
                    ? "sin actividades"
                    : string.Join(
                        " | ",
                        activities
                            .GroupBy(activity =>
                                string.IsNullOrWhiteSpace(activity.Person)
                                    ? "Sin asignar"
                                    : activity.Person,
                                StringComparer.OrdinalIgnoreCase)
                            .OrderBy(group => group.Key)
                            .Select(group =>
                                $"{group.Key}: {group.Count()}"));

            LastDiagnostics =
                $"Fecha: Fecha POR Hacer · Asignación: Assignee > tag activo de respaldo · " +
                $"Uso real: {dateUsageText} · " +
                $"Día por persona: {peopleUsageText} · " +
                $"Páginas: {pages.Count} · " +
                $"Con fecha: {pagesWithDate} · " +
                $"Reconsultadas: {hydratedPages} · " +
                $"No interpretadas: {parseFailures}";

            progress?.Report(
                new NotionCalendarProgress(
                    "Completado",
                    pages.Count,
                    pages.Count,
                    $"Listo: {activities.Count} actividades para el día seleccionado."));

            var ordered = activities
                .OrderBy(x => x.Person)
                .ThenBy(x => x.Start)
                .ThenBy(x => x.Title)
                .ToList();

            await SetCachedDayAsync(
                localDate,
                ordered,
                cancellationToken);

            return ordered;
        }

        public async Task<IReadOnlyList<NotionCalendarActivity>?> TryGetCachedDayAsync(
            DateTime day,
            CancellationToken cancellationToken = default)
        {
            await EnsureCacheLoadedAsync(
                cancellationToken);

            var key =
                day.Date.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

            await CacheLock.WaitAsync(
                cancellationToken);

            try
            {
                if (!DayCache.TryGetValue(
                        key,
                        out var cached))
                {
                    return null;
                }

                var removed = cached.RemoveAll(
                    IsAuxiliaryCalendarActivity);

                if (removed > 0)
                {
                    await SaveCacheUnsafeAsync(
                        cancellationToken);
                }

                return cached
                    .OrderBy(x => x.Person)
                    .ThenBy(x => x.Start)
                    .ThenBy(x => x.Title)
                    .ToList();
            }
            finally
            {
                CacheLock.Release();
            }
        }

        public async Task PreloadDayAsync(
            string token,
            DateTime day,
            CancellationToken cancellationToken = default)
        {
            var cached =
                await TryGetCachedDayAsync(
                    day,
                    cancellationToken);

            if (cached != null)
                return;

            await GetDayAsync(
                token,
                day,
                progress: null,
                cancellationToken,
                forceRefresh: true);
        }

        /// <summary>
        /// Obtiene un conjunto reducido de páginas candidatas para construir
        /// la vista "Proyecto relacionado" de ANFETA.
        ///
        /// Primero intenta filtrar por título usando tipo de proyecto + token
        /// de mes (ej. 2608AGOS). Si la base no acepta ese filtro, usa un rango
        /// de fechas amplio alrededor del mes como respaldo. El filtro final
        /// por tipo + dominio + mes se realiza en SearchView.Calendar.
        /// </summary>
        public async Task<IReadOnlyList<NotionCalendarActivity>>
            GetProjectCandidateActivitiesAsync(
                string token,
                string projectTypeToken,
                string monthTag,
                DateTime projectMonth,
                CancellationToken cancellationToken = default,
                bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            projectTypeToken =
                (projectTypeToken ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            monthTag =
                (monthTag ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(projectTypeToken))
                return Array.Empty<NotionCalendarActivity>();

            var monthStart =
                new DateTime(
                    projectMonth.Year,
                    projectMonth.Month,
                    1);

            var cacheKey =
                $"{monthStart:yyyy-MM}|{projectTypeToken}|{monthTag}";

            if (!forceRefresh &&
                ProjectCandidateCache.TryGetValue(
                    cacheKey,
                    out var cached) &&
                DateTimeOffset.UtcNow - cached.StoredAt <
                    ProjectCandidateCacheLifetime)
            {
                return cached.Activities
                    .ToList();
            }

            using var fullSyncLease =
                await NotionRequestCoordinator.EnterFullSyncAsync(
                    cancellationToken);

            using var http =
                CreateClient(token);

            var schema =
                await GetSchemaCachedAsync(
                    http,
                    cancellationToken);

            if (schema.DateProperties.Count == 0)
            {
                throw new InvalidOperationException(
                    "No se encontró la propiedad Fecha POR Hacer para buscar actividades relacionadas.");
            }

            List<JsonElement>? pages = null;

            // Ruta preferida: filtra por el título, que representa el proyecto
            // lógico y no depende de que una actividad haya sido movida de día.
            if (!string.IsNullOrWhiteSpace(schema.TitleProperty))
            {
                pages =
                    await TryQueryProjectTitleCandidatesAsync(
                        http,
                        schema.TitleProperty,
                        projectTypeToken,
                        monthTag,
                        cancellationToken);
            }

            if (pages == null ||
                pages.Count == 0)
            {
                // Respaldo conservador: mes anterior + mes objetivo + dos meses
                // posteriores. Esto cubre actividades arrastradas a días futuros
                // sin descargar las miles de páginas completas de Revisiones.
                pages =
                    await QueryDateRangePagesAsync(
                        http,
                        monthStart.AddMonths(-1),
                        monthStart.AddMonths(3),
                        schema.DateProperties[0],
                        cancellationToken);
            }

            var mapped =
                new List<NotionCalendarActivity>();

            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var activity =
                    await MapPageAsync(
                        http,
                        page,
                        schema,
                        cancellationToken);

                if (activity == null ||
                    string.IsNullOrWhiteSpace(activity.PageId))
                {
                    continue;
                }

                mapped.Add(activity);
            }

            var ordered =
                mapped
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(activity => activity.Start)
                    .ThenBy(activity => activity.Title)
                    .ToList();

            ProjectCandidateCache[cacheKey] =
                new ProjectCandidateCacheEntry(
                    DateTimeOffset.UtcNow,
                    ordered);

            return ordered;
        }


        public async Task<NotionChecklistStats> GetChecklistStatsAsync(
            string token,
            string pageId,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(pageId))
            {
                return new NotionChecklistStats(0, 0);
            }

            if (!forceRefresh &&
                _checklistStatsCache.TryGetValue(
                    pageId,
                    out var cached))
            {
                await ApplyChecklistStatsToCacheAsync(
                    pageId,
                    cached,
                    cancellationToken);

                return cached;
            }

            using var http = CreateClient(token);

            var stats =
                await ReadChecklistStatsRecursiveAsync(
                    http,
                    pageId,
                    depth: 0,
                    cancellationToken);

            _checklistStatsCache[pageId] = stats;

            await ApplyChecklistStatsToCacheAsync(
                pageId,
                stats,
                cancellationToken);

            return stats;
        }

        private static async Task ApplyChecklistStatsToCacheAsync(
            string pageId,
            NotionChecklistStats stats,
            CancellationToken cancellationToken)
        {
            await EnsureCacheLoadedAsync(
                cancellationToken);

            await CacheLock.WaitAsync(
                cancellationToken);

            try
            {
                var changed = false;

                foreach (var day in DayCache.Values)
                {
                    foreach (var activity in day.Where(activity =>
                                 string.Equals(
                                     activity.PageId,
                                     pageId,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        if (activity.ChecklistScanned &&
                            activity.ChecklistTotal == stats.Total &&
                            activity.ChecklistCompleted == stats.Completed)
                        {
                            continue;
                        }

                        activity.ChecklistScanned = true;
                        activity.ChecklistTotal = stats.Total;
                        activity.ChecklistCompleted = stats.Completed;
                        changed = true;
                    }
                }

                if (changed)
                {
                    await SaveCacheUnsafeAsync(
                        cancellationToken);
                }
            }
            finally
            {
                CacheLock.Release();
            }
        }

        private static async Task<NotionChecklistStats>
            ReadChecklistStatsRecursiveAsync(
                HttpClient http,
                string blockId,
                int depth,
                CancellationToken cancellationToken)
        {
            if (depth > 8 ||
                string.IsNullOrWhiteSpace(blockId))
            {
                return new NotionChecklistStats(0, 0);
            }

            var total = 0;
            var completed = 0;
            string? cursor = null;
            var hasMore = true;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requestUri =
                    $"blocks/{blockId}/children?page_size=100" +
                    (string.IsNullOrWhiteSpace(cursor)
                        ? string.Empty
                        : $"&start_cursor={Uri.EscapeDataString(cursor)}");

                using var response =
                    await SendGetWithRetryAsync(
                        http,
                        requestUri,
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "leer la checklist de la actividad",
                        response,
                        json);
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty(
                        "results",
                        out var blocks) &&
                    blocks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in blocks.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (IsArchivedOrTrashedBlock(block))
                            continue;

                        var type = ReadString(block, "type");

                        // Los bloques sincronizados pueden repetir contenido de
                        // otras páginas. No se recorren para evitar contar dos
                        // veces la misma checklist.
                        if (type.Equals(
                                "synced_block",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var plainText =
                            ReadBlockPlainText(block, type);

                        // La metadata interna de ANFETA tampoco forma parte del
                        // trabajo operativo de la actividad.
                        if (plainText.Contains(
                                "[ANFETA_",
                                StringComparison.OrdinalIgnoreCase) ||
                            plainText.Contains(
                                "Datos internos de ANFETA",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var struckThrough =
                            IsBlockRichTextStruckThrough(
                                block,
                                type);

                        if (type.Equals(
                                "to_do",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            // Un checklist tachado suele pertenecer a una
                            // sección histórica ya cerrada y no debe alterar el
                            // porcentaje operativo actual.
                            if (!struckThrough)
                            {
                                total++;

                                if (block.TryGetProperty(
                                        "to_do",
                                        out var toDo) &&
                                    toDo.ValueKind == JsonValueKind.Object &&
                                    toDo.TryGetProperty(
                                        "checked",
                                        out var checkedValue) &&
                                    checkedValue.ValueKind == JsonValueKind.True)
                                {
                                    completed++;
                                }
                            }
                        }

                        var hasChildren =
                            block.TryGetProperty(
                                "has_children",
                                out var hasChildrenValue) &&
                            hasChildrenValue.ValueKind == JsonValueKind.True;

                        if (!hasChildren ||
                            type.Equals(
                                "child_page",
                                StringComparison.OrdinalIgnoreCase) ||
                            type.Equals(
                                "child_database",
                                StringComparison.OrdinalIgnoreCase) ||
                            IsCompletedChecklistContainer(
                                type,
                                plainText,
                                struckThrough))
                        {
                            continue;
                        }

                        var childId = ReadString(block, "id");

                        var childStats =
                            await ReadChecklistStatsRecursiveAsync(
                                http,
                                childId,
                                depth + 1,
                                cancellationToken);

                        total += childStats.Total;
                        completed += childStats.Completed;
                    }
                }

                hasMore =
                    root.TryGetProperty(
                        "has_more",
                        out var more) &&
                    more.ValueKind == JsonValueKind.True;

                cursor =
                    root.TryGetProperty(
                        "next_cursor",
                        out var nextCursor) &&
                    nextCursor.ValueKind == JsonValueKind.String
                        ? nextCursor.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(cursor))
                    hasMore = false;
            }

            return new NotionChecklistStats(
                total,
                completed);
        }

        private static string ReadBlockPlainText(
            JsonElement block,
            string type)
        {
            if (string.IsNullOrWhiteSpace(type) ||
                !block.TryGetProperty(type, out var payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty(
                    "rich_text",
                    out var richText) ||
                richText.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            return string.Concat(
                richText.EnumerateArray()
                    .Select(item => ReadString(item, "plain_text")))
                .Trim();
        }

        private static bool IsArchivedOrTrashedBlock(
            JsonElement block)
        {
            var archived =
                block.TryGetProperty(
                    "archived",
                    out var archivedValue) &&
                archivedValue.ValueKind == JsonValueKind.True;

            var inTrash =
                block.TryGetProperty(
                    "in_trash",
                    out var trashValue) &&
                trashValue.ValueKind == JsonValueKind.True;

            return archived || inTrash;
        }

        private static bool IsBlockRichTextStruckThrough(
            JsonElement block,
            string type)
        {
            if (string.IsNullOrWhiteSpace(type) ||
                !block.TryGetProperty(type, out var payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty(
                    "rich_text",
                    out var richText) ||
                richText.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var hasText = false;

            foreach (var item in richText.EnumerateArray())
            {
                var plain =
                    ReadString(item, "plain_text");

                if (string.IsNullOrWhiteSpace(plain))
                    continue;

                hasText = true;

                if (!item.TryGetProperty(
                        "annotations",
                        out var annotations) ||
                    annotations.ValueKind != JsonValueKind.Object ||
                    !annotations.TryGetProperty(
                        "strikethrough",
                        out var struck) ||
                    struck.ValueKind != JsonValueKind.True)
                {
                    return false;
                }
            }

            return hasText;
        }

        private static bool IsCompletedChecklistContainer(
            string type,
            string plainText,
            bool struckThrough)
        {
            var canContainWork =
                type.Equals(
                    "toggle",
                    StringComparison.OrdinalIgnoreCase) ||
                type.Equals(
                    "heading_1",
                    StringComparison.OrdinalIgnoreCase) ||
                type.Equals(
                    "heading_2",
                    StringComparison.OrdinalIgnoreCase) ||
                type.Equals(
                    "heading_3",
                    StringComparison.OrdinalIgnoreCase) ||
                type.Equals(
                    "callout",
                    StringComparison.OrdinalIgnoreCase);

            if (!canContainWork)
                return false;

            if (struckThrough)
                return true;

            var normalized =
                Normalize(plainText);

            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            var completedTokens = new[]
            {
                "terminado",
                "terminada",
                "completado",
                "completada",
                "finalizado",
                "finalizada",
                "realizado",
                "realizada",
                "historico",
                "historial",
                "cerrado",
                "cerrada"
            };

            return completedTokens.Any(token =>
                normalized.Contains(
                    token,
                    StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> UpdateActivityAutomationLockAsync(
            string token,
            NotionCalendarActivity activity,
            bool locked,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (activity == null ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                throw new InvalidOperationException(
                    "La actividad no contiene un identificador de Notion.");
            }

            using var http = CreateClient(token);

            var page =
                await ReadPageAsync(
                    http,
                    activity.PageId,
                    cancellationToken);

            if (!page.HasValue ||
                !page.Value.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "No se pudieron leer las propiedades actuales de la actividad.");
            }

            var propertyName =
                FindCheckboxPropertyNameByAliases(
                    properties,
                    AutomationLockAliases);

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new InvalidOperationException(
                    "No existe la propiedad checkbox Bloqueada_ANFETA en la base Revisiones. " +
                    "Créala como tipo Casilla de verificación y vuelve a intentar.");
            }

            var payload =
                new Dictionary<string, object?>
                {
                    ["properties"] =
                        new Dictionary<string, object?>
                        {
                            [propertyName] =
                                new Dictionary<string, object?>
                                {
                                    ["checkbox"] = locked
                                }
                        }
                };

            using var response =
                await SendPatchWithRetryAsync(
                    http,
                    $"pages/{activity.PageId}",
                    JsonSerializer.Serialize(payload),
                    cancellationToken);

            var responseJson =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    locked
                        ? "bloquear la actividad"
                        : "desbloquear la actividad",
                    response,
                    responseJson);
            }

            activity.IsAutomationLocked = locked;

            await EnsureCacheLoadedAsync(
                cancellationToken);

            await CacheLock.WaitAsync(
                cancellationToken);

            try
            {
                foreach (var day in DayCache.Values)
                {
                    foreach (var cached in day.Where(item =>
                                 string.Equals(
                                     item.PageId,
                                     activity.PageId,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        cached.IsAutomationLocked = locked;
                    }
                }

                await SaveCacheUnsafeAsync(
                    cancellationToken);
            }
            finally
            {
                CacheLock.Release();
            }

            return locked;
        }

        public async Task<string> UpdateActivityTitleAsync(
            string token,
            string pageId,
            string newTitle,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (string.IsNullOrWhiteSpace(pageId))
            {
                throw new InvalidOperationException(
                    "La actividad no contiene un identificador de Notion.");
            }

            newTitle = (newTitle ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(newTitle))
            {
                throw new InvalidOperationException(
                    "El título de la actividad no puede quedar vacío.");
            }

            using var http = CreateClient(token);

            var page =
                await ReadPageAsync(
                    http,
                    pageId,
                    cancellationToken);

            if (!page.HasValue ||
                !page.Value.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "No se pudieron leer las propiedades actuales de la actividad.");
            }

            var titlePropertyName =
                properties
                    .EnumerateObject()
                    .FirstOrDefault(property =>
                        ReadString(property.Value, "type")
                            .Equals(
                                "title",
                                StringComparison.OrdinalIgnoreCase))
                    .Name ??
                string.Empty;

            if (string.IsNullOrWhiteSpace(titlePropertyName))
            {
                throw new InvalidOperationException(
                    "No se encontró la propiedad de título de la página.");
            }

            var payload =
                new Dictionary<string, object?>
                {
                    ["properties"] =
                        new Dictionary<string, object?>
                        {
                            [titlePropertyName] =
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "title",
                                    ["title"] =
                                        new object[]
                                        {
                                            new Dictionary<string, object?>
                                            {
                                                ["type"] = "text",
                                                ["text"] =
                                                    new Dictionary<string, object?>
                                                    {
                                                        ["content"] = newTitle
                                                    }
                                            }
                                        }
                                }
                        }
                };

            using var response =
                await SendPatchWithRetryAsync(
                    http,
                    $"pages/{pageId}",
                    JsonSerializer.Serialize(payload),
                    cancellationToken);

            var responseJson =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "actualizar el título de la actividad",
                    response,
                    responseJson);
            }

            await CacheLock.WaitAsync(cancellationToken);

            try
            {
                foreach (var day in DayCache.Values)
                {
                    foreach (var activity in day.Where(activity =>
                                 string.Equals(
                                     activity.PageId,
                                     pageId,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        activity.Title = newTitle;
                    }
                }

                await SaveCacheUnsafeAsync(cancellationToken);
            }
            finally
            {
                CacheLock.Release();
            }

            return newTitle;
        }

        public async Task<bool> UpdateActivityAssigneeAsync(
            string token,
            string pageId,
            string targetPerson,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (string.IsNullOrWhiteSpace(pageId))
            {
                throw new InvalidOperationException(
                    "La actividad no contiene un identificador de Notion.");
            }

            targetPerson =
                NormalizePersonLabel(targetPerson);

            if (string.IsNullOrWhiteSpace(targetPerson) ||
                string.Equals(
                    targetPerson,
                    "Sin asignar",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "No se pudo identificar al nuevo responsable.");
            }

            using var http = CreateClient(token);

            var page = await ReadPageAsync(
                http,
                pageId,
                cancellationToken);

            if (!page.HasValue ||
                !page.Value.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "No se pudieron leer las propiedades actuales de la actividad.");
            }

            var propertyName = string.Empty;
            var assigneePropertyValue = default(JsonElement);

            foreach (var alias in PersonAliases)
            {
                var normalizedAlias = Normalize(alias);

                foreach (var property in
                         properties.EnumerateObject())
                {
                    if (Normalize(property.Name) !=
                        normalizedAlias)
                    {
                        continue;
                    }

                    propertyName = property.Name;
                    assigneePropertyValue = property.Value;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(propertyName))
                    break;
            }

            var updatedInNotion = false;

            if (!string.IsNullOrWhiteSpace(propertyName))
            {
                var propertyType = ReadString(
                    assigneePropertyValue,
                    "type");

                Dictionary<string, object?>? propertyValue =
                    null;

                if (propertyType.Equals(
                        "people",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var userId =
                        await ResolveWorkspaceUserIdAsync(
                            http,
                            targetPerson,
                            cancellationToken);

                    if (!string.IsNullOrWhiteSpace(userId))
                    {
                        propertyValue =
                            new Dictionary<string, object?>
                            {
                                ["people"] =
                                    new object[]
                                    {
                                        new Dictionary<string, object?>
                                        {
                                            ["id"] = userId
                                        }
                                    }
                            };
                    }
                }
                else if (propertyType.Equals(
                             "rich_text",
                             StringComparison.OrdinalIgnoreCase))
                {
                    propertyValue =
                        new Dictionary<string, object?>
                        {
                            ["rich_text"] =
                                new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["type"] = "text",
                                        ["text"] =
                                            new Dictionary<string, object?>
                                            {
                                                ["content"] =
                                                    targetPerson
                                            }
                                    }
                                }
                        };
                }
                else if (propertyType.Equals(
                             "select",
                             StringComparison.OrdinalIgnoreCase))
                {
                    propertyValue =
                        new Dictionary<string, object?>
                        {
                            ["select"] =
                                new Dictionary<string, object?>
                                {
                                    ["name"] = targetPerson
                                }
                        };
                }

                if (propertyValue != null)
                {
                    var payload =
                        new Dictionary<string, object?>
                        {
                            ["properties"] =
                                new Dictionary<string, object?>
                                {
                                    [propertyName] =
                                        propertyValue
                                }
                        };

                    using var response =
                        await SendPatchWithRetryAsync(
                            http,
                            $"pages/{pageId}",
                            JsonSerializer.Serialize(payload),
                            cancellationToken);

                    // Si la propiedad resulta ser de solo lectura o el
                    // usuario no puede asignarse con esta integración, no se
                    // cancela el flujo completo. El título ya contiene el tag
                    // correcto y Notion podrá recalcular la propiedad.
                    updatedInNotion =
                        response.IsSuccessStatusCode;
                }
            }

            await CacheLock.WaitAsync(cancellationToken);

            try
            {
                foreach (var day in DayCache.Values)
                {
                    foreach (var activity in day.Where(activity =>
                                 string.Equals(
                                     activity.PageId,
                                     pageId,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        activity.Person = targetPerson;
                    }
                }

                await SaveCacheUnsafeAsync(cancellationToken);
            }
            finally
            {
                CacheLock.Release();
            }

            // Si Assignee es fórmula, rollup o relación no editable, el título
            // actualizado seguirá provocando su recálculo en Notion. La caché
            // local se corrige de inmediato para no conservar al revisor.
            return updatedInNotion;
        }

        private static async Task<string> ResolveWorkspaceUserIdAsync(
            HttpClient http,
            string targetPerson,
            CancellationToken cancellationToken)
        {
            var normalizedTarget =
                NormalizePersonLabel(targetPerson);

            var lookupTokens =
                WorkspacePersonLookup.TryGetValue(
                    normalizedTarget,
                    out var configured)
                    ? configured
                    : new[] { normalizedTarget };

            var normalizedTokens =
                lookupTokens
                    .Append(normalizedTarget)
                    .Select(NormalizeIdentityToken)
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            string? cursor = null;

            do
            {
                var requestUri =
                    "users?page_size=100" +
                    (string.IsNullOrWhiteSpace(cursor)
                        ? string.Empty
                        : $"&start_cursor={Uri.EscapeDataString(cursor)}");

                using var response =
                    await SendGetWithRetryAsync(
                        http,
                        requestUri,
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return string.Empty;

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty(
                        "results",
                        out var users) &&
                    users.ValueKind == JsonValueKind.Array)
                {
                    foreach (var user in users.EnumerateArray())
                    {
                        if (!ReadString(user, "type").Equals(
                                "person",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var name =
                            NormalizeIdentityToken(
                                ReadString(user, "name"));

                        var email = string.Empty;

                        if (user.TryGetProperty(
                                "person",
                                out var personData) &&
                            personData.ValueKind ==
                                JsonValueKind.Object)
                        {
                            email = ReadString(
                                personData,
                                "email");
                        }

                        var emailLocal =
                            NormalizeIdentityToken(
                                email.Split('@')
                                    .FirstOrDefault() ??
                                string.Empty);

                        var matches =
                            normalizedTokens.Any(token =>
                                string.Equals(
                                    token,
                                    name,
                                    StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(
                                    token,
                                    emailLocal,
                                    StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(
                                    token,
                                    StringComparison.OrdinalIgnoreCase) ||
                                emailLocal.StartsWith(
                                    token,
                                    StringComparison.OrdinalIgnoreCase));

                        if (matches)
                            return ReadString(user, "id");
                    }
                }

                var hasMore =
                    root.TryGetProperty(
                        "has_more",
                        out var more) &&
                    more.ValueKind == JsonValueKind.True;

                cursor =
                    hasMore &&
                    root.TryGetProperty(
                        "next_cursor",
                        out var next) &&
                    next.ValueKind == JsonValueKind.String
                        ? next.GetString()
                        : null;
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            return string.Empty;
        }

        private static string NormalizeIdentityToken(
            string value)
        {
            return Normalize(value)
                .Replace(" ", string.Empty);
        }

        public async Task<NotionCalendarActivity> MoveActivityToDateAsync(
            string token,
            NotionCalendarActivity activity,
            DateTime targetDate,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (activity == null ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                throw new InvalidOperationException(
                    "La actividad no contiene un identificador de Notion.");
            }

            if (activity.IsAutomationLocked)
            {
                throw new InvalidOperationException(
                    "La actividad está bloqueada para automatizaciones. Desbloquéala antes de moverla.");
            }

            using var http =
                CreateClient(token);

            var page =
                await ReadPageAsync(
                    http,
                    activity.PageId,
                    cancellationToken);

            if (!page.HasValue ||
                !page.Value.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "No se pudieron leer las propiedades actuales de la actividad.");
            }

            var propertyName =
                activity.DatePropertyName;

            if (string.IsNullOrWhiteSpace(propertyName) ||
                !properties.TryGetProperty(
                    propertyName,
                    out var currentProperty) ||
                !ReadString(currentProperty, "type")
                    .Equals(
                        "date",
                        StringComparison.OrdinalIgnoreCase))
            {
                propertyName =
                    DateAliases.FirstOrDefault(alias =>
                        properties.EnumerateObject().Any(property =>
                            Normalize(property.Name) == Normalize(alias) &&
                            ReadString(property.Value, "type")
                                .Equals(
                                    "date",
                                    StringComparison.OrdinalIgnoreCase)))
                    ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new InvalidOperationException(
                    "No se encontró una propiedad de fecha editable. " +
                    "La fecha visible podría provenir de una fórmula o rollup.");
            }

            var duration =
                activity.End > activity.Start
                    ? activity.End - activity.Start
                    : TimeSpan.FromHours(1);

            var newStart =
                targetDate.Date.Add(activity.Start.TimeOfDay);

            var newEnd =
                newStart.Add(duration);

            var startOffset =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        newStart,
                        DateTimeKind.Local));

            var endOffset =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        newEnd,
                        DateTimeKind.Local));

            var payload =
                new Dictionary<string, object?>
                {
                    ["properties"] =
                        new Dictionary<string, object?>
                        {
                            [propertyName] =
                                new Dictionary<string, object?>
                                {
                                    ["date"] =
                                        new Dictionary<string, object?>
                                        {
                                            ["start"] =
                                                startOffset.ToString("O"),
                                            ["end"] =
                                                endOffset.ToString("O")
                                        }
                                }
                        }
                };

            using var response =
                await SendPatchWithRetryAsync(
                    http,
                    $"pages/{activity.PageId}",
                    JsonSerializer.Serialize(payload),
                    cancellationToken);

            var responseJson =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "mover la actividad",
                    response,
                    responseJson);
            }

            var updated =
                new NotionCalendarActivity
                {
                    PageId = activity.PageId,
                    PageUrl = activity.PageUrl,
                    Title = activity.Title,
                    Person = activity.Person,
                    OriginalPerson = activity.OriginalPerson,
                    ReviewAssignee = activity.ReviewAssignee,
                    ReviewState = activity.ReviewState,
                    ReviewSubmittedAt = activity.ReviewSubmittedAt,
                    ReviewUpdatedAt = activity.ReviewUpdatedAt,
                    ReviewUpdatedBy = activity.ReviewUpdatedBy,
                    ReviewNote = activity.ReviewNote,
                    IsReviewMirror = activity.IsReviewMirror,
                    IsCompletedForReview =
                        activity.IsCompletedForReview,
                    IsAutomationLocked =
                        activity.IsAutomationLocked,
                    ChecklistScanned =
                        activity.ChecklistScanned,
                    ChecklistTotal =
                        activity.ChecklistTotal,
                    ChecklistCompleted =
                        activity.ChecklistCompleted,
                    Project = activity.Project,
                    Status = activity.Status,
                    StatusColor = activity.StatusColor,
                    UpdateText = activity.UpdateText,
                    Description = activity.Description,
                    EstimatedWorkMinutes = activity.EstimatedWorkMinutes,
                    WorkedMinutes = activity.WorkedMinutes,
                    WorkLogDetail = activity.WorkLogDetail,
                    ActivityCreatedDate = activity.ActivityCreatedDate,
                    InternalDeadlineDate = activity.InternalDeadlineDate,
                    DatePropertyName = propertyName,
                    Start = newStart,
                    End = newEnd
                };

            await RemoveActivityFromCacheAsync(
                activity.PageId,
                cancellationToken);

            foreach (var day in EnumerateActivityDays(updated))
            {
                var key =
                    day.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

                await CacheLock.WaitAsync(
                    cancellationToken);

                try
                {
                    if (!DayCache.TryGetValue(
                            key,
                            out var list))
                    {
                        list =
                            new List<NotionCalendarActivity>();

                        DayCache[key] = list;
                    }

                    list.RemoveAll(item =>
                        string.Equals(
                            item.PageId,
                            updated.PageId,
                            StringComparison.OrdinalIgnoreCase));

                    list.Add(updated);

                    await SaveCacheUnsafeAsync(
                        cancellationToken);
                }
                finally
                {
                    CacheLock.Release();
                }
            }

            // Una fecha/hora modificada puede cambiar el orden visible de
            // cualquier proyecto relacionado. El cache local de candidatos se
            // invalida; SearchView conserva su cache de checklist actual y no
            // necesita volver a descargarlo inmediatamente.
            ProjectCandidateCache.Clear();

            return updated;
        }


        public async Task<NotionActivityWorkUpdateResult>
            RegisterActivityWorkAsync(
                string token,
                NotionCalendarActivity activity,
                DateTime workDate,
                int workedMinutes,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (activity == null ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                throw new InvalidOperationException(
                    "La actividad no contiene un identificador de Notion.");
            }

            if (workedMinutes <= 0)
            {
                throw new InvalidOperationException(
                    "El tiempo trabajado debe ser mayor a cero.");
            }

            using var http =
                CreateClient(token);

            var page =
                await ReadPageAsync(
                    http,
                    activity.PageId,
                    cancellationToken);

            if (!page.HasValue ||
                !page.Value.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "No se pudieron leer las propiedades actuales de la actividad.");
            }

            var auditPropertyName = string.Empty;
            var auditPropertyValue = default(JsonElement);

            foreach (var alias in AuditLogAliases)
            {
                var normalizedAlias =
                    Normalize(alias);

                foreach (var property in properties.EnumerateObject())
                {
                    if (Normalize(property.Name) != normalizedAlias)
                        continue;

                    auditPropertyName = property.Name;
                    auditPropertyValue = property.Value;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(auditPropertyName))
                    break;
            }

            if (string.IsNullOrWhiteSpace(auditPropertyName) ||
                !ReadString(auditPropertyValue, "type")
                    .Equals(
                        "rich_text",
                        StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Para contabilizar horas entre días se necesita la propiedad " +
                    "rich_text Audit_FTF_Log en la base Revisiones.");
            }

            var existing =
                ExtractPropertyText(
                    auditPropertyValue)
                    .Trim();

            var fallbackEstimate =
                activity.EstimatedWorkMinutes > 0
                    ? activity.EstimatedWorkMinutes
                    : Math.Max(
                        1,
                        (int)Math.Round(
                            (activity.End > activity.Start
                                ? activity.End - activity.Start
                                : TimeSpan.FromHours(1))
                            .TotalMinutes));

            var state =
                ReadLatestActivityWorkLog(
                    existing,
                    fallbackEstimate)
                ?? new StoredActivityWorkLog
                {
                    EstimateMinutes = fallbackEstimate
                };

            if (state.EstimateMinutes <= 0)
                state.EstimateMinutes = fallbackEstimate;

            var key =
                workDate.Date.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

            state.MinutesByDate ??=
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

            state.MinutesByDate[key] =
                state.MinutesByDate.TryGetValue(
                    key,
                    out var previous)
                    ? Math.Max(0, previous) + workedMinutes
                    : workedMinutes;

            var totalWorked =
                state.MinutesByDate.Values
                    .Where(value => value > 0)
                    .Sum();

            // Nunca muestra una estimación inferior al tiempo ya registrado.
            state.EstimateMinutes =
                Math.Max(
                    state.EstimateMinutes,
                    totalWorked);

            var encoded =
                WorkLogPrefix +
                Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(state)));

            var separator =
                string.IsNullOrWhiteSpace(existing)
                    ? string.Empty
                    : "\n";

            var nextLog =
                existing +
                separator +
                encoded;

            if (nextLog.Length > 1900)
            {
                // El estado más reciente siempre queda completo al final.
                var keep =
                    Math.Max(
                        0,
                        1900 - encoded.Length - 1);

                var tail =
                    keep > 0 && existing.Length > keep
                        ? existing.Substring(
                            existing.Length - keep)
                        : existing;

                nextLog =
                    string.IsNullOrWhiteSpace(tail)
                        ? encoded
                        : tail + "\n" + encoded;

                if (nextLog.Length > 1900)
                    nextLog = encoded;
            }

            var payload =
                new Dictionary<string, object?>
                {
                    ["properties"] =
                        new Dictionary<string, object?>
                        {
                            [auditPropertyName] =
                                new Dictionary<string, object?>
                                {
                                    ["rich_text"] =
                                        new object[]
                                        {
                                            new Dictionary<string, object?>
                                            {
                                                ["type"] = "text",
                                                ["text"] =
                                                    new Dictionary<string, object?>
                                                    {
                                                        ["content"] = nextLog
                                                    }
                                            }
                                        }
                                }
                        }
                };

            using var response =
                await SendPatchWithRetryAsync(
                    http,
                    $"pages/{activity.PageId}",
                    JsonSerializer.Serialize(payload),
                    cancellationToken);

            var responseJson =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "registrar tiempo trabajado",
                    response,
                    responseJson);
            }

            activity.EstimatedWorkMinutes =
                state.EstimateMinutes;
            activity.WorkedMinutes =
                totalWorked;
            activity.WorkLogDetail =
                BuildActivityWorkLogDetail(state);

            await EnsureCacheLoadedAsync(
                cancellationToken);

            await CacheLock.WaitAsync(
                cancellationToken);

            try
            {
                foreach (var day in DayCache.Values)
                {
                    foreach (var cached in day.Where(item =>
                                 string.Equals(
                                     item.PageId,
                                     activity.PageId,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        cached.EstimatedWorkMinutes =
                            activity.EstimatedWorkMinutes;
                        cached.WorkedMinutes =
                            activity.WorkedMinutes;
                        cached.WorkLogDetail =
                            activity.WorkLogDetail;
                    }
                }

                await SaveCacheUnsafeAsync(
                    cancellationToken);
            }
            finally
            {
                CacheLock.Release();
            }

            return new NotionActivityWorkUpdateResult(
                activity,
                true);
        }

        private static StoredActivityWorkLog?
            ReadLatestActivityWorkLog(
                string? auditLog,
                int fallbackEstimateMinutes)
        {
            var raw =
                auditLog ?? string.Empty;

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var lines =
                raw.Split(
                    new[] { "\r\n", "\n", "\r" },
                    StringSplitOptions.RemoveEmptyEntries);

            for (var index = lines.Length - 1;
                 index >= 0;
                 index--)
            {
                var line =
                    lines[index].Trim();

                var prefixIndex =
                    line.IndexOf(
                        WorkLogPrefix,
                        StringComparison.Ordinal);

                if (prefixIndex < 0)
                    continue;

                var encoded =
                    line.Substring(
                        prefixIndex + WorkLogPrefix.Length)
                        .Trim();

                try
                {
                    var json =
                        Encoding.UTF8.GetString(
                            Convert.FromBase64String(encoded));

                    var state =
                        JsonSerializer.Deserialize<
                            StoredActivityWorkLog>(json);

                    if (state == null)
                        continue;

                    state.MinutesByDate =
                        state.MinutesByDate == null
                            ? new Dictionary<string, int>(
                                StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, int>(
                                state.MinutesByDate,
                                StringComparer.OrdinalIgnoreCase);

                    if (state.EstimateMinutes <= 0)
                    {
                        state.EstimateMinutes =
                            Math.Max(
                                1,
                                fallbackEstimateMinutes);
                    }

                    return state;
                }
                catch
                {
                }
            }

            return null;
        }

        private static string BuildActivityWorkLogDetail(
            StoredActivityWorkLog? state)
        {
            if (state?.MinutesByDate == null ||
                state.MinutesByDate.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                " · ",
                state.MinutesByDate
                    .Where(item => item.Value > 0)
                    .OrderBy(item => item.Key)
                    .Select(item =>
                    {
                        var label =
                            DateTime.TryParseExact(
                                item.Key,
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var date)
                                ? date.ToString(
                                    "dd/MM",
                                    CultureInfo.InvariantCulture)
                                : item.Key;

                        return $"{label}: " +
                               FormatWorkMinutes(item.Value);
                    }));
        }

        private static string FormatWorkMinutes(
            int totalMinutes)
        {
            totalMinutes =
                Math.Max(
                    0,
                    totalMinutes);

            var hours =
                totalMinutes / 60;

            var minutes =
                totalMinutes % 60;

            if (hours > 0 && minutes > 0)
                return $"{hours}H {minutes}M";

            if (hours > 0)
                return $"{hours}H";

            return $"{minutes}M";
        }

        public async Task<NotionCalendarActivity>
            UpdateActivityScheduleAsync(
                string token,
                NotionCalendarActivity activity,
                DateTime targetStart,
                CancellationToken cancellationToken = default)
        {
            var result =
                await UpdateActivityScheduleWithAuditAsync(
                    token,
                    activity,
                    targetStart,
                    targetEnd: null,
                    auditLog: null,
                    cancellationToken: cancellationToken);

            return result.Activity;
        }

        public async Task<NotionCalendarScheduleUpdateResult>
            UpdateActivityScheduleWithAuditAsync(
                string token,
                NotionCalendarActivity activity,
                DateTime targetStart,
                DateTime? targetEnd,
                string? auditLog,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (activity == null ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                throw new InvalidOperationException(
                    "La actividad no contiene un identificador de Notion.");
            }

            if (activity.IsAutomationLocked)
            {
                throw new InvalidOperationException(
                    "La actividad está bloqueada para automatizaciones. Desbloquéala antes de moverla.");
            }

            using var http =
                CreateClient(token);

            var page =
                await ReadPageAsync(
                    http,
                    activity.PageId,
                    cancellationToken);

            if (!page.HasValue ||
                !page.Value.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "No se pudieron leer las propiedades actuales de la actividad.");
            }

            var propertyName =
                activity.DatePropertyName;

            if (string.IsNullOrWhiteSpace(propertyName) ||
                !properties.TryGetProperty(
                    propertyName,
                    out var currentProperty) ||
                !ReadString(currentProperty, "type")
                    .Equals(
                        "date",
                        StringComparison.OrdinalIgnoreCase))
            {
                propertyName =
                    DateAliases.FirstOrDefault(alias =>
                        properties.EnumerateObject().Any(property =>
                            Normalize(property.Name) ==
                                Normalize(alias) &&
                            ReadString(
                                property.Value,
                                "type")
                                .Equals(
                                    "date",
                                    StringComparison.OrdinalIgnoreCase)))
                    ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new InvalidOperationException(
                    "No se encontró una propiedad de fecha editable. " +
                    "La fecha visible podría provenir de una fórmula o rollup.");
            }

            var duration =
                activity.End > activity.Start
                    ? activity.End - activity.Start
                    : TimeSpan.FromHours(1);

            var localStart =
                DateTime.SpecifyKind(
                    targetStart,
                    DateTimeKind.Local);

            var localEnd =
                targetEnd.HasValue &&
                targetEnd.Value > targetStart
                    ? DateTime.SpecifyKind(
                        targetEnd.Value,
                        DateTimeKind.Local)
                    : localStart.Add(duration);

            var startOffset =
                new DateTimeOffset(localStart);

            var endOffset =
                new DateTimeOffset(localEnd);

            var propertiesPayload =
                new Dictionary<string, object?>
                {
                    [propertyName] =
                        new Dictionary<string, object?>
                        {
                            ["date"] =
                                new Dictionary<string, object?>
                                {
                                    ["start"] =
                                        startOffset.ToString("O"),
                                    ["end"] =
                                        endOffset.ToString("O")
                                }
                        }
                };

            var auditLogWritten = false;

            if (!string.IsNullOrWhiteSpace(auditLog))
            {
                foreach (var alias in AuditLogAliases)
                {
                    var normalizedAlias =
                        Normalize(alias);

                    var auditPropertyName = string.Empty;
                    var auditPropertyValue = default(JsonElement);

                    foreach (var property in
                             properties.EnumerateObject())
                    {
                        if (Normalize(property.Name) !=
                            normalizedAlias)
                        {
                            continue;
                        }

                        auditPropertyName = property.Name;
                        auditPropertyValue = property.Value;
                        break;
                    }

                    // No se accede a JsonProperty.Name sobre un valor default.
                    // Ese acceso era la causa de: "Operation is not valid due
                    // to the current state of the object" cuando Audit_FTF_Log
                    // no existía en la base.
                    if (string.IsNullOrWhiteSpace(
                            auditPropertyName))
                    {
                        continue;
                    }

                    var auditType =
                        ReadString(
                            auditPropertyValue,
                            "type");

                    if (!auditType.Equals(
                            "rich_text",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    var existing =
                        ExtractPropertyText(
                            auditPropertyValue)
                            .Trim();

                    var nextLog =
                        string.IsNullOrWhiteSpace(existing)
                            ? auditLog.Trim()
                            : $"{existing}\n{auditLog.Trim()}";

                    if (nextLog.Length > 1900)
                    {
                        nextLog =
                            nextLog.Substring(
                                nextLog.Length - 1900);
                    }

                    propertiesPayload[
                        auditPropertyName] =
                            new Dictionary<string, object?>
                            {
                                ["rich_text"] =
                                    new object[]
                                    {
                                        new Dictionary<string, object?>
                                        {
                                            ["type"] = "text",
                                            ["text"] =
                                                new Dictionary<string, object?>
                                                {
                                                    ["content"] =
                                                        nextLog
                                                }
                                        }
                                    }
                            };

                    auditLogWritten = true;
                    break;
                }
            }

            var payload =
                new Dictionary<string, object?>
                {
                    ["properties"] =
                        propertiesPayload
                };

            using var response =
                await SendPatchWithRetryAsync(
                    http,
                    $"pages/{activity.PageId}",
                    JsonSerializer.Serialize(payload),
                    cancellationToken);

            var responseJson =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "cambiar la hora de la actividad",
                    response,
                    responseJson);
            }

            var updated =
                new NotionCalendarActivity
                {
                    PageId = activity.PageId,
                    PageUrl = activity.PageUrl,
                    Title = activity.Title,
                    Person = activity.Person,
                    OriginalPerson = activity.OriginalPerson,
                    ReviewAssignee = activity.ReviewAssignee,
                    ReviewState = activity.ReviewState,
                    ReviewSubmittedAt = activity.ReviewSubmittedAt,
                    ReviewUpdatedAt = activity.ReviewUpdatedAt,
                    ReviewUpdatedBy = activity.ReviewUpdatedBy,
                    ReviewNote = activity.ReviewNote,
                    IsReviewMirror = activity.IsReviewMirror,
                    IsCompletedForReview =
                        activity.IsCompletedForReview,
                    IsAutomationLocked =
                        activity.IsAutomationLocked,
                    ChecklistScanned =
                        activity.ChecklistScanned,
                    ChecklistTotal =
                        activity.ChecklistTotal,
                    ChecklistCompleted =
                        activity.ChecklistCompleted,
                    Project = activity.Project,
                    Status = activity.Status,
                    StatusColor = activity.StatusColor,
                    UpdateText = activity.UpdateText,
                    Description = activity.Description,
                    EstimatedWorkMinutes = activity.EstimatedWorkMinutes,
                    WorkedMinutes = activity.WorkedMinutes,
                    WorkLogDetail = activity.WorkLogDetail,
                    ActivityCreatedDate = activity.ActivityCreatedDate,
                    InternalDeadlineDate = activity.InternalDeadlineDate,
                    DatePropertyName = propertyName,
                    Start = localStart,
                    End = localEnd
                };

            await RemoveActivityFromCacheAsync(
                activity.PageId,
                cancellationToken);

            foreach (var day in EnumerateActivityDays(updated))
            {
                var key =
                    day.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

                await CacheLock.WaitAsync(
                    cancellationToken);

                try
                {
                    if (!DayCache.TryGetValue(
                            key,
                            out var list))
                    {
                        list =
                            new List<NotionCalendarActivity>();

                        DayCache[key] = list;
                    }

                    list.RemoveAll(item =>
                        string.Equals(
                            item.PageId,
                            updated.PageId,
                            StringComparison.OrdinalIgnoreCase));

                    list.Add(updated);

                    await SaveCacheUnsafeAsync(
                        cancellationToken);
                }
                finally
                {
                    CacheLock.Release();
                }
            }

            // Una modificación de horario puede afectar la posición de
            // actividades dentro de cualquier proyecto relacionado. Invalida
            // únicamente candidatos de proyecto; la caché diaria ya quedó
            // actualizada arriba.
            ProjectCandidateCache.Clear();

            return new NotionCalendarScheduleUpdateResult(
                updated,
                auditLogWritten);
        }

        private static async Task RemoveActivityFromCacheAsync(
            string pageId,
            CancellationToken cancellationToken)
        {
            await EnsureCacheLoadedAsync(
                cancellationToken);

            await CacheLock.WaitAsync(
                cancellationToken);

            try
            {
                foreach (var key in DayCache.Keys.ToList())
                {
                    DayCache[key].RemoveAll(activity =>
                        string.Equals(
                            activity.PageId,
                            pageId,
                            StringComparison.OrdinalIgnoreCase));
                }

                await SaveCacheUnsafeAsync(
                    cancellationToken);
            }
            finally
            {
                CacheLock.Release();
            }
        }

        public async Task<bool> RefreshChangedSinceAsync(
            string token,
            DateTimeOffset changedAfterUtc,
            CancellationToken cancellationToken = default,
            IProgress<NotionCalendarProgress>? progress = null)
        {
            LastChangedPageIds = Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(token))
                return false;

            // Evita que el warmup, el botón Actualizar y otras pestañas
            // ejecuten sincronizaciones de calendario al mismo tiempo.
            using var fullSyncLease =
                await NotionRequestCoordinator.EnterFullSyncAsync(
                    cancellationToken);

            await EnsureCacheLoadedAsync(
                cancellationToken);

            progress?.Report(
                new NotionCalendarProgress(
                    "Comprobando cambios",
                    0,
                    0,
                    $"Buscando cambios desde {changedAfterUtc.ToLocalTime():dd/MM HH:mm}..."));

            using var http =
                CreateClient(token);

            progress?.Report(
                new NotionCalendarProgress(
                    "Consultando estructura",
                    0,
                    1,
                    "Verificando las propiedades de Revisiones..."));

            var schema =
                await GetSchemaCachedAsync(
                    http,
                    cancellationToken);

            var changedPages =
                await QueryChangedPagesAsync(
                    http,
                    changedAfterUtc,
                    progress,
                    cancellationToken);

            if (changedPages.Count == 0)
            {
                progress?.Report(
                    new NotionCalendarProgress(
                        "Completado",
                        1,
                        1,
                        "No se encontraron cambios nuevos en Notion."));

                return false;
            }

            var mapped =
                new List<NotionCalendarActivity>();

            var changedIds =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            for (var pageIndex = 0;
                 pageIndex < changedPages.Count;
                 pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = changedPages[pageIndex];

                progress?.Report(
                    new NotionCalendarProgress(
                        "Analizando cambios",
                        pageIndex + 1,
                        changedPages.Count,
                        $"Procesando cambio {pageIndex + 1} de {changedPages.Count}..."));

                var pageId =
                    ReadString(page, "id");

                if (!string.IsNullOrWhiteSpace(pageId))
                    changedIds.Add(pageId);

                var activity =
                    await MapPageAsync(
                        http,
                        page,
                        schema,
                        cancellationToken);

                if (activity != null)
                    mapped.Add(activity);
            }

            progress?.Report(
                new NotionCalendarProgress(
                    "Aplicando cambios",
                    changedPages.Count,
                    changedPages.Count,
                    $"Actualizando la caché con {mapped.Count} actividad(es)..."));

            await CacheLock.WaitAsync(
                cancellationToken);

            try
            {
                // Mantiene el último porcentaje visible mientras se relee
                // únicamente el body de las páginas modificadas.
                var previousChecklist =
                    DayCache.Values
                        .SelectMany(day => day)
                        .Where(activity =>
                            activity != null &&
                            changedIds.Contains(activity.PageId) &&
                            activity.ChecklistScanned)
                        .GroupBy(
                            activity => activity.PageId,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group =>
                            {
                                var item = group.First();
                                return new NotionChecklistStats(
                                    item.ChecklistTotal,
                                    item.ChecklistCompleted);
                            },
                            StringComparer.OrdinalIgnoreCase);

                foreach (var key in DayCache.Keys.ToList())
                {
                    DayCache[key].RemoveAll(activity =>
                        changedIds.Contains(activity.PageId));
                }

                foreach (var activity in mapped)
                {
                    if (previousChecklist.TryGetValue(
                            activity.PageId,
                            out var priorStats))
                    {
                        activity.ChecklistScanned = true;
                        activity.ChecklistTotal = priorStats.Total;
                        activity.ChecklistCompleted = priorStats.Completed;
                    }

                    foreach (var day in EnumerateActivityDays(activity))
                    {
                        var key =
                            day.ToString(
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture);

                        if (!DayCache.TryGetValue(
                                key,
                                out var list))
                        {
                            list =
                                new List<NotionCalendarActivity>();

                            DayCache[key] = list;
                        }

                        list.RemoveAll(x =>
                            string.Equals(
                                x.PageId,
                                activity.PageId,
                                StringComparison.OrdinalIgnoreCase));

                        list.Add(activity);
                    }
                }

                await SaveCacheUnsafeAsync(
                    cancellationToken);
            }
            finally
            {
                CacheLock.Release();
            }

            // Un cambio en título, fecha, persona o proyecto puede mover una
            // página de un grupo lógico a otro. La siguiente apertura del
            // proyecto vuelve a resolver sus candidatos.
            ProjectCandidateCache.Clear();

            LastChangedPageIds =
                changedIds
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            progress?.Report(
                new NotionCalendarProgress(
                    "Completado",
                    changedPages.Count,
                    changedPages.Count,
                    $"Se aplicaron {mapped.Count} actividad(es) actualizadas."));

            return true;
        }

        public void ClearCache()
        {
            ProjectCandidateCache.Clear();

            lock (CacheSaveScheduleLock)
            {
                _cacheSaveDebounceCts?.Cancel();
                _cacheSaveDebounceCts = null;
            }

            CacheLock.Wait();

            try
            {
                DayCache.Clear();
                _cacheLoaded = true;

                var path =
                    GetCachePath();

                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
            finally
            {
                CacheLock.Release();
            }
        }

        private static IEnumerable<DateTime> EnumerateActivityDays(
            NotionCalendarActivity activity)
        {
            var start =
                activity.Start.Date;

            var end =
                activity.End > activity.Start
                    ? activity.End.AddTicks(-1).Date
                    : activity.Start.Date;

            for (var day = start;
                 day <= end;
                 day = day.AddDays(1))
            {
                yield return day;
            }
        }

        private static async Task SetCachedDayAsync(
            DateTime day,
            IReadOnlyList<NotionCalendarActivity> activities,
            CancellationToken cancellationToken)
        {
            await EnsureCacheLoadedAsync(
                cancellationToken);

            var key =
                day.Date.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

            await CacheLock.WaitAsync(
                cancellationToken);

            try
            {
                var incoming =
                    activities
                        .Where(activity =>
                            !IsAuxiliaryCalendarActivity(activity))
                        .ToList();

                // Una consulta válida sin actividades sí debe guardarse,
                // pero nunca sustituimos una caché poblada cuando la llamada
                // fue cancelada antes de completar este método.
                cancellationToken.ThrowIfCancellationRequested();

                DayCache[key] =
                    incoming;

                await SaveCacheUnsafeAsync(
                    cancellationToken);
            }
            finally
            {
                CacheLock.Release();
            }
        }

        private static async Task EnsureCacheLoadedAsync(
            CancellationToken cancellationToken)
        {
            if (_cacheLoaded)
                return;

            await CacheLock.WaitAsync(
                cancellationToken);

            try
            {
                if (_cacheLoaded)
                    return;

                var path =
                    GetCachePath();

                if (File.Exists(path))
                {
                    var json =
                        await File.ReadAllTextAsync(
                            path,
                            cancellationToken);

                    // La deserialización de varios días ya no ocurre sobre el
                    // hilo de UI que abrió el calendario.
                    var restored =
                        await Task.Run(
                            () =>
                                JsonSerializer.Deserialize<
                                    Dictionary<string, List<NotionCalendarActivity>>>(
                                    json),
                            cancellationToken);

                    if (restored != null)
                    {
                        DayCache.Clear();

                        foreach (var item in restored)
                        {
                            DayCache[item.Key] =
                                (item.Value ?? new())
                                .Where(activity =>
                                    !IsAuxiliaryCalendarActivity(activity))
                                .ToList();
                        }
                    }
                }

                _cacheLoaded = true;
            }
            catch
            {
                DayCache.Clear();
                _cacheLoaded = true;
            }
            finally
            {
                CacheLock.Release();
            }
        }

        private static Task SaveCacheUnsafeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScheduleCacheSave();
            return Task.CompletedTask;
        }

        private static void ScheduleCacheSave()
        {
            CancellationTokenSource next;

            lock (CacheSaveScheduleLock)
            {
                _cacheSaveDebounceCts?.Cancel();

                next =
                    new CancellationTokenSource();

                _cacheSaveDebounceCts = next;
            }

            _ = PersistCacheAfterDelayAsync(next);
        }

        private static async Task PersistCacheAfterDelayAsync(
            CancellationTokenSource owner)
        {
            try
            {
                // Agrupa cambios cercanos: especialmente los porcentajes de
                // checklist que antes escribían el archivo completo uno a uno.
                await Task.Delay(
                    TimeSpan.FromSeconds(1.5),
                    owner.Token);

                Dictionary<string, List<NotionCalendarActivity>>
                    snapshot;

                await CacheLock.WaitAsync(
                    owner.Token);

                try
                {
                    snapshot =
                        DayCache.ToDictionary(
                            item => item.Key,
                            item => item.Value.ToList(),
                            StringComparer.Ordinal);
                }
                finally
                {
                    CacheLock.Release();
                }

                var json =
                    await Task.Run(
                        () =>
                            JsonSerializer.Serialize(
                                snapshot,
                                new JsonSerializerOptions
                                {
                                    WriteIndented = false
                                }),
                        owner.Token);

                await CacheWriteLock.WaitAsync(
                    owner.Token);

                try
                {
                    await File.WriteAllTextAsync(
                        GetCachePath(),
                        json,
                        owner.Token);
                }
                finally
                {
                    CacheWriteLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // La caché en RAM sigue siendo válida. Una escritura posterior
                // volverá a intentar persistirla.
            }
            finally
            {
                lock (CacheSaveScheduleLock)
                {
                    if (ReferenceEquals(
                            _cacheSaveDebounceCts,
                            owner))
                    {
                        _cacheSaveDebounceCts = null;
                    }
                }

                owner.Dispose();
            }
        }

        private static string GetCachePath()
        {
            return Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                CacheFileName);
        }

        private static HttpClient CreateClient(string token)
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(NotionBaseUrl),
                Timeout = TimeSpan.FromSeconds(120)
            };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    token.Trim());

            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "Notion-Version",
                NotionVersion);

            return http;
        }

        private static async Task<SchemaInfo> GetSchemaCachedAsync(
            HttpClient http,
            CancellationToken cancellationToken)
        {
            var now =
                DateTimeOffset.UtcNow;

            if (_cachedSchema != null &&
                now - _schemaCachedAtUtc <
                    SchemaCacheLifetime)
            {
                return _cachedSchema;
            }

            await SchemaCacheLock.WaitAsync(
                cancellationToken);

            try
            {
                now = DateTimeOffset.UtcNow;

                if (_cachedSchema != null &&
                    now - _schemaCachedAtUtc <
                        SchemaCacheLifetime)
                {
                    return _cachedSchema;
                }

                _cachedSchema =
                    await ReadSchemaAsync(
                        http,
                        cancellationToken);

                _schemaCachedAtUtc =
                    DateTimeOffset.UtcNow;

                return _cachedSchema;
            }
            finally
            {
                SchemaCacheLock.Release();
            }
        }

        private static async Task<SchemaInfo> ReadSchemaAsync(
            HttpClient http,
            CancellationToken cancellationToken)
        {
            using var response = await SendGetWithRetryAsync(
                http,
                $"data_sources/{RevisionesDataSourceId}",
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw CreateNotionException(
                    "consultar el esquema de Revisiones",
                    response,
                    json);

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Notion no devolvió las propiedades de Revisiones.");
            }

            var dateProperties = FindDatePropertyCandidates(
                properties);

            var titleProperty = properties
                .EnumerateObject()
                .FirstOrDefault(x =>
                    ReadString(x.Value, "type")
                        .Equals(
                            "title",
                            StringComparison.OrdinalIgnoreCase))
                .Name ?? string.Empty;

            return new SchemaInfo(
                dateProperties,
                titleProperty);
        }

        private static async Task<List<JsonElement>> QueryChangedPagesAsync(
            HttpClient http,
            DateTimeOffset changedAfterUtc,
            IProgress<NotionCalendarProgress>? progress,
            CancellationToken cancellationToken)
        {
            var results = new List<JsonElement>();
            string? cursor = null;
            var hasMore = true;
            var batchNumber = 0;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchNumber++;

                progress?.Report(
                    new NotionCalendarProgress(
                        "Descargando cambios",
                        results.Count,
                        0,
                        $"Consultando lote {batchNumber} · {results.Count} cambio(s) recibidos..."));

                var payload = new Dictionary<string, object?>
                {
                    ["page_size"] = 100,
                    ["filter"] = new Dictionary<string, object?>
                    {
                        ["timestamp"] = "last_edited_time",
                        ["last_edited_time"] =
                            new Dictionary<string, object?>
                            {
                                ["after"] =
                                    changedAfterUtc
                                        .ToUniversalTime()
                                        .AddSeconds(-2)
                                        .ToString("O")
                            }
                    },
                    ["sorts"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["timestamp"] = "last_edited_time",
                            ["direction"] = "ascending"
                        }
                    }
                };

                if (!string.IsNullOrWhiteSpace(cursor))
                    payload["start_cursor"] = cursor;

                using var response =
                    await SendPostWithRetryAsync(
                        http,
                        $"data_sources/{RevisionesDataSourceId}/query",
                        JsonSerializer.Serialize(payload),
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "consultar cambios del calendario",
                        response,
                        json);
                }

                using var document =
                    JsonDocument.Parse(json);

                var root =
                    document.RootElement;

                if (root.TryGetProperty(
                        "results",
                        out var pages) &&
                    pages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in pages.EnumerateArray())
                        results.Add(page.Clone());
                }

                hasMore =
                    root.TryGetProperty(
                        "has_more",
                        out var more) &&
                    more.ValueKind == JsonValueKind.True;

                cursor =
                    root.TryGetProperty(
                        "next_cursor",
                        out var nextCursor) &&
                    nextCursor.ValueKind == JsonValueKind.String
                        ? nextCursor.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(cursor))
                    hasMore = false;
            }

            return results;
        }

        private static async Task<List<JsonElement>?>
            TryQueryProjectTitleCandidatesAsync(
                HttpClient http,
                string titlePropertyName,
                string projectTypeToken,
                string monthTag,
                CancellationToken cancellationToken)
        {
            try
            {
                var results =
                    new List<JsonElement>();

                string? cursor = null;
                var hasMore = true;

                while (hasMore)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var filters =
                        new List<object>
                        {
                            new Dictionary<string, object?>
                            {
                                ["property"] = titlePropertyName,
                                ["title"] =
                                    new Dictionary<string, object?>
                                    {
                                        ["contains"] =
                                            projectTypeToken
                                    }
                            }
                        };

                    if (!string.IsNullOrWhiteSpace(monthTag))
                    {
                        filters.Add(
                            new Dictionary<string, object?>
                            {
                                ["property"] = titlePropertyName,
                                ["title"] =
                                    new Dictionary<string, object?>
                                    {
                                        ["contains"] =
                                            monthTag
                                    }
                            });
                    }

                    var payload =
                        new Dictionary<string, object?>
                        {
                            ["page_size"] = 100,
                            ["filter"] =
                                filters.Count == 1
                                    ? filters[0]
                                    : new Dictionary<string, object?>
                                    {
                                        ["and"] =
                                            filters.ToArray()
                                    }
                        };

                    if (!string.IsNullOrWhiteSpace(cursor))
                        payload["start_cursor"] = cursor;

                    using var response =
                        await SendPostWithRetryAsync(
                            http,
                            $"data_sources/{RevisionesDataSourceId}/query",
                            JsonSerializer.Serialize(payload),
                            cancellationToken);

                    var json =
                        await response.Content.ReadAsStringAsync(
                            cancellationToken);

                    // Algunas configuraciones antiguas de la base pueden no
                    // aceptar el filtro de title. En ese caso se activa el
                    // respaldo por rango de fechas, sin convertirlo en error.
                    if (!response.IsSuccessStatusCode)
                        return null;

                    using var document =
                        JsonDocument.Parse(json);

                    var root =
                        document.RootElement;

                    if (root.TryGetProperty(
                            "results",
                            out var pages) &&
                        pages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var page in pages.EnumerateArray())
                            results.Add(page.Clone());
                    }

                    hasMore =
                        root.TryGetProperty(
                            "has_more",
                            out var more) &&
                        more.ValueKind == JsonValueKind.True;

                    cursor =
                        root.TryGetProperty(
                            "next_cursor",
                            out var nextCursor) &&
                        nextCursor.ValueKind ==
                            JsonValueKind.String
                            ? nextCursor.GetString()
                            : null;

                    if (string.IsNullOrWhiteSpace(cursor))
                        hasMore = false;
                }

                return results;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<List<JsonElement>>
            QueryDateRangePagesAsync(
                HttpClient http,
                DateTime localStartDate,
                DateTime localEndDateExclusive,
                string datePropertyName,
                CancellationToken cancellationToken)
        {
            var results =
                new List<JsonElement>();

            string? cursor = null;
            var hasMore = true;

            var localStart =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        localStartDate.Date,
                        DateTimeKind.Local));

            var localEnd =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        localEndDateExclusive.Date,
                        DateTimeKind.Local));

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload =
                    new Dictionary<string, object?>
                    {
                        ["page_size"] = 100,
                        ["filter"] =
                            new Dictionary<string, object?>
                            {
                                ["and"] =
                                    new object[]
                                    {
                                        new Dictionary<string, object?>
                                        {
                                            ["property"] =
                                                datePropertyName,
                                            ["date"] =
                                                new Dictionary<string, object?>
                                                {
                                                    ["on_or_after"] =
                                                        localStart.ToString("O")
                                                }
                                        },
                                        new Dictionary<string, object?>
                                        {
                                            ["property"] =
                                                datePropertyName,
                                            ["date"] =
                                                new Dictionary<string, object?>
                                                {
                                                    ["before"] =
                                                        localEnd.ToString("O")
                                                }
                                        }
                                    }
                            }
                    };

                if (!string.IsNullOrWhiteSpace(cursor))
                    payload["start_cursor"] = cursor;

                using var response =
                    await SendPostWithRetryAsync(
                        http,
                        $"data_sources/{RevisionesDataSourceId}/query",
                        JsonSerializer.Serialize(payload),
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "consultar actividades relacionadas del proyecto",
                        response,
                        json);
                }

                using var document =
                    JsonDocument.Parse(json);

                var root =
                    document.RootElement;

                if (root.TryGetProperty(
                        "results",
                        out var pages) &&
                    pages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in pages.EnumerateArray())
                        results.Add(page.Clone());
                }

                hasMore =
                    root.TryGetProperty(
                        "has_more",
                        out var more) &&
                    more.ValueKind == JsonValueKind.True;

                cursor =
                    root.TryGetProperty(
                        "next_cursor",
                        out var nextCursor) &&
                    nextCursor.ValueKind ==
                        JsonValueKind.String
                        ? nextCursor.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(cursor))
                    hasMore = false;
            }

            return results;
        }

        private static async Task<List<JsonElement>> QueryDayPagesAsync(
            HttpClient http,
            DateTime localDate,
            string datePropertyName,
            IProgress<NotionCalendarProgress>? progress,
            CancellationToken cancellationToken)
        {
            var results = new List<JsonElement>();
            string? cursor = null;
            var hasMore = true;
            var batchNumber = 0;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchNumber++;

                progress?.Report(
                    new NotionCalendarProgress(
                        "Descargando actividades del día",
                        results.Count,
                        0,
                        $"Lote {batchNumber} · {results.Count} actividades recibidas para {localDate:dd/MM/yyyy}..."));

                var localDayStart =
                    new DateTimeOffset(
                        DateTime.SpecifyKind(
                            localDate.Date,
                            DateTimeKind.Local));

                var localDayEnd =
                    localDayStart.AddDays(1);

                var payload = new Dictionary<string, object?>
                {
                    ["page_size"] = 100,
                    ["filter"] =
                        new Dictionary<string, object?>
                        {
                            ["and"] =
                                new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["property"] = datePropertyName,
                                        ["date"] =
                                            new Dictionary<string, object?>
                                            {
                                                ["on_or_after"] =
                                                    localDayStart.ToString("O")
                                            }
                                    },
                                    new Dictionary<string, object?>
                                    {
                                        ["property"] = datePropertyName,
                                        ["date"] =
                                            new Dictionary<string, object?>
                                            {
                                                ["before"] =
                                                    localDayEnd.ToString("O")
                                            }
                                    }
                                }
                        }
                };

                if (!string.IsNullOrWhiteSpace(cursor))
                    payload["start_cursor"] = cursor;

                using var response = await SendPostWithRetryAsync(
                    http,
                    $"data_sources/{RevisionesDataSourceId}/query",
                    JsonSerializer.Serialize(payload),
                    cancellationToken);

                var json = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                    throw CreateNotionException(
                        "consultar el calendario de Revisiones",
                        response,
                        json);

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty("results", out var pages) &&
                    pages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in pages.EnumerateArray())
                        results.Add(page.Clone());
                }

                hasMore =
                    root.TryGetProperty("has_more", out var more) &&
                    more.ValueKind == JsonValueKind.True;

                cursor =
                    root.TryGetProperty("next_cursor", out var nextCursor) &&
                    nextCursor.ValueKind == JsonValueKind.String
                        ? nextCursor.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(cursor))
                    hasMore = false;
            }

            return results;
        }

        private static bool IsAuxiliaryCalendarNotificationTitle(
            string? title)
        {
            return !string.IsNullOrWhiteSpace(title) &&
                   AuxiliaryMessageTitlePattern.IsMatch(
                       title.Trim());
        }

        private static bool IsAuxiliaryCalendarActivity(
            NotionCalendarActivity? activity)
        {
            return activity == null ||
                   IsAuxiliaryCalendarNotificationTitle(
                       activity.Title);
        }

        private async Task<NotionCalendarActivity?> MapPageAsync(
            HttpClient http,
            JsonElement page,
            SchemaInfo schema,
            CancellationToken cancellationToken)
        {
            if (!page.TryGetProperty("properties", out var props) ||
                props.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryReadCalendarDate(
                    page,
                    schema.DateProperties,
                    out var start,
                    out var end,
                    out var datePropertyName))
            {
                return null;
            }

            if (end <= start)
                end = start.AddHours(1);

            var title = ExtractPropertyText(
                FindProperty(props, schema.TitleProperty));

            if (string.IsNullOrWhiteSpace(title))
                title = "Actividad sin título";

            if (IsAuxiliaryCalendarNotificationTitle(title))
                return null;

            var people = await ReadPersonsAsync(
                http,
                props,
                title,
                cancellationToken);

            var project = ReadByAliases(
                props,
                ProjectAliases);

            var (status, statusColor) =
                ReadPreferredCalendarStatus(
                    props);

            var updateText = ReadProjectUpdateText(
                props);

            var description = ReadByAliases(
                props,
                DescriptionAliases);

            var auditLogText =
                ReadByAliases(
                    props,
                    AuditLogAliases);

            var workLog =
                ReadLatestActivityWorkLog(
                    auditLogText,
                    Math.Max(
                        1,
                        (int)Math.Round(
                            (end - start).TotalMinutes)));

            var hasActivityCreatedDate =
                TryReadControlDateByAliases(
                    props,
                    ActivityCreatedDateAliases,
                    out var activityCreatedDate);

            var hasInternalDeadlineDate =
                TryReadControlDateByAliases(
                    props,
                    InternalDeadlineDateAliases,
                    out var internalDeadlineDate);

            var isAutomationLocked =
                ReadCheckboxByAliases(
                    props,
                    AutomationLockAliases);

            var pageId = ReadString(page, "id");
            var pageUrl = ReadString(page, "url");

            return new NotionCalendarActivity
            {
                PageId = pageId,
                PageUrl = pageUrl,
                Title = title,
                Person = people,
                OriginalPerson = people,
                IsCompletedForReview = IsCompletedReviewStatus(status),
                IsAutomationLocked = isAutomationLocked,
                Project = project,
                Status = status,
                StatusColor = statusColor,
                UpdateText = updateText,
                Description = description,
                EstimatedWorkMinutes =
                    workLog?.EstimateMinutes ?? 0,
                WorkedMinutes =
                    workLog?.MinutesByDate?.Values.Sum() ?? 0,
                WorkLogDetail =
                    BuildActivityWorkLogDetail(workLog),
                ActivityCreatedDate = hasActivityCreatedDate
                    ? activityCreatedDate
                    : null,
                InternalDeadlineDate = hasInternalDeadlineDate
                    ? internalDeadlineDate
                    : null,
                DatePropertyName = datePropertyName,
                Start = start,
                End = end
            };
        }

        private async Task<string> ReadPersonsAsync(
            HttpClient http,
            JsonElement props,
            string title,
            CancellationToken cancellationToken)
        {
            // La vista de Notion ya agrupa por “Assignee/Ejecutor Principal”.
            // Esa propiedad representa la asignación efectiva y debe tener
            // prioridad cuando el título conserva tags históricos adicionales.
            foreach (var alias in PersonAliases)
            {
                var property =
                    FindPropertyByAlias(
                        props,
                        alias);

                if (property.ValueKind !=
                    JsonValueKind.Object)
                {
                    continue;
                }

                var type =
                    ReadString(
                        property,
                        "type");

                if (type.Equals(
                        "people",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var person =
                        NormalizePersonLabel(
                            ExtractPeople(property));

                    if (!string.IsNullOrWhiteSpace(person) &&
                        !string.Equals(
                            person,
                            "Sin asignar",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return person;
                    }
                }

                if (type.Equals(
                        "relation",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var relatedNames =
                        new List<string>();

                    foreach (var relatedId in
                             ExtractRelationIds(property))
                    {
                        var relatedTitle =
                            await ResolveRelatedTitleAsync(
                                http,
                                relatedId,
                                cancellationToken);

                        if (!string.IsNullOrWhiteSpace(
                                relatedTitle))
                        {
                            relatedNames.Add(
                                relatedTitle);
                        }
                    }

                    var person =
                        NormalizePersonLabel(
                            string.Join(
                                ", ",
                                relatedNames));

                    if (!string.IsNullOrWhiteSpace(person) &&
                        !string.Equals(
                            person,
                            "Sin asignar",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return person;
                    }
                }

                var text =
                    ExtractPropertyText(
                        property);

                var normalized =
                    NormalizePersonLabel(
                        text);

                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !string.Equals(
                        normalized,
                        "Sin asignar",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return normalized;
                }
            }

            // Respaldo para páginas antiguas o mientras Notion termina de
            // recalcular Assignee después de editar el título.
            var activePerson =
                DetectActivePersonFromTitle(
                    title);

            return string.IsNullOrWhiteSpace(activePerson)
                ? "Sin asignar"
                : activePerson;
        }

        private async Task<string> ResolveRelatedTitleAsync(
            HttpClient http,
            string pageId,
            CancellationToken cancellationToken)
        {
            if (_relatedTitleCache.TryGetValue(
                    pageId,
                    out var cached))
            {
                return cached;
            }

            using var response = await SendGetWithRetryAsync(
                http,
                $"pages/{pageId}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return string.Empty;

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("properties", out var props) ||
                props.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var property in props.EnumerateObject())
            {
                if (ReadString(property.Value, "type") == "title")
                {
                    var title = ExtractPropertyText(property.Value);

                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        _relatedTitleCache[pageId] = title;
                        return title;
                    }
                }
            }

            return string.Empty;
        }

        private static bool TryReadControlDateByAliases(
            JsonElement props,
            IEnumerable<string> aliases,
            out DateTime value)
        {
            value = default;

            foreach (var alias in aliases)
            {
                var property =
                    FindPropertyByAlias(
                        props,
                        alias);

                if (property.ValueKind != JsonValueKind.Object)
                    continue;

                if (TryReadDateRange(
                        property,
                        out var start,
                        out _))
                {
                    value = start.Date;
                    return true;
                }

                // Soporta también propiedades nativas created_time /
                // last_edited_time por si la base cambia el tipo del campo.
                foreach (var propertyName in new[]
                         {
                             "created_time",
                             "last_edited_time"
                         })
                {
                    if (!property.TryGetProperty(
                            propertyName,
                            out var raw) ||
                        raw.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (TryParseNotionDate(
                            raw.GetString() ?? string.Empty,
                            out var parsed))
                    {
                        value = parsed.Date;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryReadDateRange(
            JsonElement prop,
            out DateTime start,
            out DateTime end)
        {
            start = default;
            end = default;

            if (prop.ValueKind != JsonValueKind.Object)
                return false;

            JsonElement date = default;

            if (prop.TryGetProperty("date", out var directDate) &&
                directDate.ValueKind == JsonValueKind.Object)
            {
                date = directDate;
            }
            else if (prop.TryGetProperty("formula", out var formula) &&
                     formula.ValueKind == JsonValueKind.Object &&
                     formula.TryGetProperty("date", out var formulaDate) &&
                     formulaDate.ValueKind == JsonValueKind.Object)
            {
                date = formulaDate;
            }
            else if (prop.TryGetProperty("rollup", out var rollup) &&
                     rollup.ValueKind == JsonValueKind.Object)
            {
                var rollupType = ReadString(rollup, "type");

                if (rollupType == "date" &&
                    rollup.TryGetProperty("date", out var rollupDate) &&
                    rollupDate.ValueKind == JsonValueKind.Object)
                {
                    date = rollupDate;
                }
                else if (rollupType == "array" &&
                         rollup.TryGetProperty("array", out var array) &&
                         array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        if (TryReadDateRange(item, out start, out end))
                            return true;
                    }
                }
            }

            if (date.ValueKind != JsonValueKind.Object)
                return false;

            var startRaw = ReadString(date, "start");
            var endRaw = ReadString(date, "end");

            if (!TryParseNotionDate(startRaw, out start))
                return false;

            if (!TryParseNotionDate(endRaw, out end))
                end = start.AddHours(1);

            return true;
        }

        private static bool TryParseNotionDate(
            string raw,
            out DateTime value)
        {
            value = default;

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces |
                    DateTimeStyles.AssumeLocal,
                    out var offset))
            {
                value = offset.LocalDateTime;
                return true;
            }

            if (DateTime.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces |
                    DateTimeStyles.AssumeLocal,
                    out var dateTime))
            {
                value = dateTime;
                return true;
            }

            return false;
        }

        private static bool ActivityOverlapsDay(
            NotionCalendarActivity activity,
            DateTime day)
        {
            var dayStart = day.Date;
            var dayEnd = dayStart.AddDays(1);

            return activity.Start < dayEnd &&
                   activity.End > dayStart;
        }

        private static bool HasAnyCandidateProperty(
            JsonElement page,
            IReadOnlyList<string> candidateNames)
        {
            if (!page.TryGetProperty("properties", out var props) ||
                props.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return candidateNames.Any(name =>
                props.TryGetProperty(name, out _));
        }

        private static bool TryReadCalendarDate(
            JsonElement page,
            IReadOnlyList<string> candidateNames,
            out DateTime start,
            out DateTime end,
            out string propertyUsed)
        {
            start = default;
            end = default;
            propertyUsed = string.Empty;

            if (!page.TryGetProperty("properties", out var props) ||
                props.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var candidate in candidateNames)
            {
                if (!props.TryGetProperty(candidate, out var property))
                    continue;

                if (TryReadDateRange(
                        property,
                        out start,
                        out end))
                {
                    propertyUsed = candidate;
                    return true;
                }
            }

            return false;
        }

        private static async Task<JsonElement?> ReadPageAsync(
            HttpClient http,
            string pageId,
            CancellationToken cancellationToken)
        {
            using var response = await SendGetWithRetryAsync(
                http,
                $"pages/{pageId}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static (string Name, string Color)
            ReadPreferredCalendarStatus(
                JsonElement props)
        {
            // Prioridad absoluta a la propiedad mostrada en Notion:
            // “(bien) Estado opcion multiple revisiones”.
            foreach (var property in props.EnumerateObject())
            {
                var normalizedName =
                    Normalize(property.Name);

                var isPreferred =
                    normalizedName ==
                        "bien estado opcion multiple revisiones" ||
                    normalizedName ==
                        "estado opcion multiple revisiones";

                if (!isPreferred)
                    continue;

                var name =
                    ExtractPropertyText(
                        property.Value);

                var color =
                    ReadPropertyOptionColor(
                        property.Value);

                if (string.IsNullOrWhiteSpace(color))
                    color = MapStatusNameToNotionColor(name);

                if (!string.IsNullOrWhiteSpace(name) ||
                    !string.IsNullOrWhiteSpace(color))
                {
                    return (name, color);
                }
            }

            // El color del calendario no debe inferirse desde el título
            // zREVISION ni desde otros estados. Si la propiedad solicitada
            // no existe o está vacía, se conserva un estado neutro.
            return (
                string.Empty,
                string.Empty);
        }

        private static string ReadPropertyOptionColor(
            JsonElement property)
        {
            if (property.ValueKind != JsonValueKind.Object)
                return string.Empty;

            var type = ReadString(property, "type");

            if ((type == "status" || type == "select") &&
                property.TryGetProperty(type, out var selected) &&
                selected.ValueKind == JsonValueKind.Object)
            {
                var directColor = ReadString(selected, "color");

                if (!string.IsNullOrWhiteSpace(directColor))
                    return directColor;
            }

            if (type == "multi_select" &&
                property.TryGetProperty("multi_select", out var options) &&
                options.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in options.EnumerateArray())
                {
                    var color = ReadString(option, "color");

                    if (!string.IsNullOrWhiteSpace(color))
                        return color;
                }
            }

            if (type == "rollup" &&
                property.TryGetProperty("rollup", out var rollup) &&
                rollup.ValueKind == JsonValueKind.Object &&
                ReadString(rollup, "type") == "array" &&
                rollup.TryGetProperty("array", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var color = ReadPropertyOptionColor(item);

                    if (!string.IsNullOrWhiteSpace(color))
                        return color;
                }
            }

            return MapStatusNameToNotionColor(
                ExtractPropertyText(property));
        }

        private static bool IsCompletedReviewStatus(
            string status)
        {
            var normalized = Normalize(status);

            return
                normalized.Contains("pendiente") &&
                normalized.Contains("cobrar") ||
                normalized.Contains("cobrado") &&
                normalized.Contains("terminado");
        }

        private static string MapStatusNameToNotionColor(
            string status)
        {
            var normalized = Normalize(status);

            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            if (normalized.Contains("suspe") && normalized.Contains("pago"))
                return "yellow";

            if (normalized.Contains("arrancar") && normalized.Contains("asignar"))
                return "red";

            if (normalized.Contains("prtuz") && normalized.Contains("por hacer"))
                return "purple";

            if (normalized.Contains("revisar") && normalized.Contains("revisiones"))
                return "blue";

            if (normalized.Contains("terminado") &&
                normalized.Contains("rev") &&
                normalized.Contains("cobro"))
            {
                return "blue";
            }

            if (normalized.Contains("pendiente") && normalized.Contains("cobrar"))
                return "green";

            if (normalized.Contains("cobrado") && normalized.Contains("terminado"))
                return "green";

            if (normalized.Contains("terminado"))
                return "green";

            return string.Empty;
        }

        private static string ReadColorByAliases(
            JsonElement props,
            IEnumerable<string> aliases)
        {
            foreach (var alias in aliases)
            {
                var property =
                    FindPropertyByAlias(
                        props,
                        alias);

                if (property.ValueKind !=
                    JsonValueKind.Object)
                {
                    continue;
                }

                var type =
                    ReadString(
                        property,
                        "type");

                if ((type == "status" ||
                     type == "select") &&
                    property.TryGetProperty(
                        type,
                        out var selected) &&
                    selected.ValueKind ==
                        JsonValueKind.Object)
                {
                    var color =
                        ReadString(
                            selected,
                            "color");

                    if (!string.IsNullOrWhiteSpace(color))
                        return color;
                }

                if (type == "multi_select" &&
                    property.TryGetProperty(
                        "multi_select",
                        out var options) &&
                    options.ValueKind ==
                        JsonValueKind.Array)
                {
                    foreach (var option in
                             options.EnumerateArray())
                    {
                        var color =
                            ReadString(
                                option,
                                "color");

                        if (!string.IsNullOrWhiteSpace(color))
                            return color;
                    }
                }
            }

            return string.Empty;
        }

        private static string ReadProjectUpdateText(
            JsonElement props)
        {
            foreach (var alias in UpdateTextAliases)
            {
                var value =
                    ExtractPropertyText(
                        FindPropertyByAlias(
                            props,
                            alias));

                if (IsUsefulUpdateText(value))
                    return value.Trim();
            }

            var candidates =
                new List<(string Value, int Score)>();

            foreach (var property in props.EnumerateObject())
            {
                var name =
                    Normalize(property.Name);

                var type =
                    ReadString(
                        property.Value,
                        "type");

                if (type == "checkbox")
                    continue;

                if (type == "formula" &&
                    property.Value.TryGetProperty(
                        "formula",
                        out var formula) &&
                    ReadString(formula, "type") ==
                        "boolean")
                {
                    continue;
                }

                var score = 0;

                if (name.Contains("estado"))
                    score += 4;

                if (name.Contains("texto"))
                    score += 4;

                if (name.Contains("actualizacion"))
                    score += 5;

                if (name.Contains("seguimiento"))
                    score += 4;

                if (name.Contains("proyecto"))
                    score += 2;

                if (name.Contains("comentario"))
                    score += 1;

                if (score < 6)
                    continue;

                var value =
                    ExtractPropertyText(
                        property.Value);

                if (IsUsefulUpdateText(value))
                    candidates.Add((value.Trim(), score));
            }

            return candidates
                .OrderByDescending(item => item.Score)
                .Select(item => item.Value)
                .FirstOrDefault() ??
                string.Empty;
        }

        private static bool IsUsefulUpdateText(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var clean = value.Trim();

            return !string.Equals(
                       clean,
                       "Sí",
                       StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(
                       clean,
                       "No",
                       StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(
                       clean,
                       "True",
                       StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(
                       clean,
                       "False",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool ReadCheckboxByAliases(
            JsonElement props,
            IEnumerable<string> aliases)
        {
            foreach (var alias in aliases)
            {
                var property =
                    FindPropertyByAlias(
                        props,
                        alias);

                if (property.ValueKind != JsonValueKind.Object)
                    continue;

                var type =
                    ReadString(
                        property,
                        "type");

                if (type.Equals(
                        "checkbox",
                        StringComparison.OrdinalIgnoreCase) &&
                    property.TryGetProperty(
                        "checkbox",
                        out var checkbox) &&
                    (checkbox.ValueKind == JsonValueKind.True ||
                     checkbox.ValueKind == JsonValueKind.False))
                {
                    return checkbox.GetBoolean();
                }

                if (type.Equals(
                        "formula",
                        StringComparison.OrdinalIgnoreCase) &&
                    property.TryGetProperty(
                        "formula",
                        out var formula) &&
                    formula.ValueKind == JsonValueKind.Object &&
                    ReadString(formula, "type").Equals(
                        "boolean",
                        StringComparison.OrdinalIgnoreCase) &&
                    formula.TryGetProperty(
                        "boolean",
                        out var boolean) &&
                    (boolean.ValueKind == JsonValueKind.True ||
                     boolean.ValueKind == JsonValueKind.False))
                {
                    return boolean.GetBoolean();
                }
            }

            return false;
        }

        private static string FindCheckboxPropertyNameByAliases(
            JsonElement properties,
            IEnumerable<string> aliases)
        {
            foreach (var alias in aliases)
            {
                var normalizedAlias = Normalize(alias);

                foreach (var property in properties.EnumerateObject())
                {
                    if (Normalize(property.Name) != normalizedAlias)
                        continue;

                    if (ReadString(
                            property.Value,
                            "type")
                        .Equals(
                            "checkbox",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Name;
                    }
                }
            }

            return string.Empty;
        }

        private static string ReadByAliases(
            JsonElement props,
            IEnumerable<string> aliases)
        {
            foreach (var alias in aliases)
            {
                var value = ExtractPropertyText(
                    FindPropertyByAlias(props, alias));

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static JsonElement FindProperty(
            JsonElement props,
            string exactName)
        {
            foreach (var property in props.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        exactName,
                        StringComparison.Ordinal))
                {
                    return property.Value;
                }
            }

            return default;
        }

        private static JsonElement FindPropertyByAlias(
            JsonElement props,
            string alias)
        {
            var normalizedAlias = Normalize(alias);

            foreach (var property in props.EnumerateObject())
            {
                if (Normalize(property.Name) == normalizedAlias)
                    return property.Value;
            }

            return default;
        }

        private static IReadOnlyList<string> FindDatePropertyCandidates(
            JsonElement properties)
        {
            var result =
                new List<string>();

            foreach (var alias in DateAliases)
            {
                var normalizedAlias =
                    Normalize(alias);

                foreach (var property in
                         properties.EnumerateObject())
                {
                    if (Normalize(property.Name) !=
                            normalizedAlias)
                    {
                        continue;
                    }

                    // No se aceptan fórmulas ni rollups. La fecha del
                    // calendario sale únicamente del campo editable
                    // “Fecha POR Hacer”.
                    if (!ReadString(
                            property.Value,
                            "type")
                        .Equals(
                            "date",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!result.Contains(
                            property.Name,
                            StringComparer.Ordinal))
                    {
                        result.Add(property.Name);
                    }

                    break;
                }
            }

            return result;
        }

        private static string FindPropertyName(
            JsonElement properties,
            IEnumerable<string> aliases,
            string expectedType)
        {
            foreach (var alias in aliases)
            {
                var normalizedAlias = Normalize(alias);

                foreach (var property in properties.EnumerateObject())
                {
                    if (Normalize(property.Name) == normalizedAlias &&
                        ReadString(property.Value, "type")
                            .Equals(
                                expectedType,
                                StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Name;
                    }
                }
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (ReadString(property.Value, "type")
                        .Equals(
                            expectedType,
                            StringComparison.OrdinalIgnoreCase) &&
                    Normalize(property.Name).Contains("fecha por hacer"))
                {
                    return property.Name;
                }
            }

            return string.Empty;
        }

        private static string ExtractPropertyText(JsonElement prop)
        {
            if (prop.ValueKind != JsonValueKind.Object)
                return string.Empty;

            var type = ReadString(prop, "type");

            return type switch
            {
                "title" => JoinPlainText(prop, "title"),
                "rich_text" => JoinPlainText(prop, "rich_text"),
                "select" => ExtractNamedObject(prop, "select"),
                "status" => ExtractNamedObject(prop, "status"),
                "multi_select" => ExtractMultiSelect(prop),
                "people" => ExtractPeople(prop),
                "formula" => ExtractFormula(prop),
                "rollup" => ExtractRollup(prop),
                "url" => ReadString(prop, "url"),
                "email" => ReadString(prop, "email"),
                "number" => prop.TryGetProperty(
                    "number",
                    out var number)
                        ? number.GetRawText()
                        : string.Empty,
                _ => string.Empty
            };
        }

        private static string JoinPlainText(
            JsonElement prop,
            string arrayName)
        {
            if (!prop.TryGetProperty(arrayName, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            return string.Concat(
                    array.EnumerateArray()
                        .Select(x => ReadString(x, "plain_text")))
                .Trim();
        }

        private static string ExtractNamedObject(
            JsonElement prop,
            string name)
        {
            return prop.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.Object
                ? ReadString(value, "name")
                : string.Empty;
        }

        private static string ExtractMultiSelect(JsonElement prop)
        {
            if (!prop.TryGetProperty(
                    "multi_select",
                    out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            return string.Join(
                ", ",
                array.EnumerateArray()
                    .Select(x => ReadString(x, "name"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string ExtractPeople(JsonElement prop)
        {
            if (!prop.TryGetProperty("people", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            return string.Join(
                ", ",
                array.EnumerateArray()
                    .Select(person =>
                    {
                        var name = ReadString(person, "name");

                        if (string.IsNullOrWhiteSpace(name) &&
                            person.TryGetProperty(
                                "person",
                                out var personData))
                        {
                            name = ReadString(personData, "email");
                        }

                        return name;
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static List<string> ExtractRelationIds(JsonElement prop)
        {
            if (!prop.TryGetProperty("relation", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            return array.EnumerateArray()
                .Select(x => ReadString(x, "id"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private static string ExtractFormula(JsonElement prop)
        {
            if (!prop.TryGetProperty("formula", out var formula) ||
                formula.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var type = ReadString(formula, "type");

            return type switch
            {
                "string" => ReadString(formula, "string"),
                "number" => formula.TryGetProperty(
                    "number",
                    out var number)
                        ? number.GetRawText()
                        : string.Empty,
                "boolean" => formula.TryGetProperty(
                    "boolean",
                    out var boolean)
                        ? boolean.GetBoolean().ToString()
                        : string.Empty,
                _ => string.Empty
            };
        }

        private static string ExtractRollup(JsonElement prop)
        {
            if (!prop.TryGetProperty("rollup", out var rollup) ||
                rollup.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var type = ReadString(rollup, "type");

            if (type == "array" &&
                rollup.TryGetProperty("array", out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                return string.Join(
                    ", ",
                    array.EnumerateArray()
                        .Select(ExtractPropertyText)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            return type switch
            {
                "number" => rollup.TryGetProperty(
                    "number",
                    out var number)
                        ? number.GetRawText()
                        : string.Empty,
                _ => string.Empty
            };
        }

        private static string NormalizePersonLabel(string value)
        {
            var parts = (value ?? string.Empty)
                .Split(
                    new[] { ',', ';', '|' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeSinglePerson)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return parts.Count > 0
                ? string.Join(", ", parts)
                : string.Empty;
        }

        private static string NormalizeSinglePerson(string value)
        {
            var clean = (value ?? string.Empty).Trim();

            var emailIndex = clean.IndexOf('@');
            if (emailIndex > 0)
                clean = clean.Substring(0, emailIndex);

            var detected = DetectPersonFromTitle(clean);

            return detected != "Sin asignar"
                ? detected
                : clean;
        }

        private static string DetectActivePersonFromTitle(
            string title)
        {
            var value =
                title ?? string.Empty;

            var activeTags =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["jjohn"] = "John",
                    ["kkarl"] = "Karla",
                    ["iisai"] = "Isaias",
                    ["ssote"] = "Sotelo",
                    ["eedua"] = "Sotelo",
                    ["aacal"] = "Acalli",
                    ["aandr"] = "Andrade",
                    ["eemma"] = "Emmanuel",
                    ["bbria"] = "Brian",
                    ["ggena"] = "Genaro",
                    ["nneft"] = "Neftali"
                };

            var matches =
                Regex.Matches(
                    value,
                    @"(?<![\p{L}\p{Nd}_])(?<tag>[a-z]{5})(?<suffix>\d*)(?![\p{L}\p{Nd}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            // Normalmente solo existe uno. Si hubiera más de uno por
            // historial, el último representa la asignación actual.
            for (var index = matches.Count - 1;
                 index >= 0;
                 index--)
            {
                var tag =
                    matches[index]
                        .Groups["tag"]
                        .Value;

                if (activeTags.TryGetValue(
                        tag,
                        out var person))
                {
                    return person;
                }
            }

            return string.Empty;
        }

        private static bool ContainsKnownPersonTag(
            string title)
        {
            var value = title ?? string.Empty;

            var tags = new[]
            {
                "jjohn", "john",
                "kkarl", "karl",
                "iisai", "isai",
                "ssote", "sote", "eedua", "edua",
                "aacal", "acal",
                "aandr", "andr",
                "eemma", "emma",
                "bbria", "bria",
                "ggena", "gena",
                "nneft", "neft"
            };

            return tags.Any(tag =>
                Regex.IsMatch(
                    value,
                    $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(tag)}\d*(?![\p{{L}}\p{{Nd}}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant));
        }

        private static string DetectPersonFromTitle(string title)
        {
            var normalized = Normalize(title)
                .Replace(" ", string.Empty);

            var aliases = new (string Alias, string Person)[]
            {
                ("jjohn", "John"),
                ("john", "John"),

                ("kkarl", "Karla"),
                ("karla", "Karla"),
                ("karl", "Karla"),

                ("iisai", "Isaias"),
                ("isaias", "Isaias"),
                ("isai", "Isaias"),

                ("ssote", "Sotelo"),
                ("sotelo", "Sotelo"),
                ("sote", "Sotelo"),
                ("eedua", "Sotelo"),
                ("eduardo", "Sotelo"),
                ("edua", "Sotelo"),

                ("aacal", "Acalli"),
                ("acalli", "Acalli"),
                ("acali", "Acalli"),
                ("acal", "Acalli"),

                ("aandr", "Andrade"),
                ("andrade", "Andrade"),
                ("andr", "Andrade"),

                ("eemma", "Emmanuel"),
                ("emmanuel", "Emmanuel"),
                ("emanuel", "Emmanuel"),
                ("emma", "Emmanuel"),

                ("bbria", "Brian"),
                ("brian", "Brian"),
                ("bria", "Brian"),

                ("ggena", "Genaro"),
                ("genaro", "Genaro"),
                ("gena", "Genaro"),

                ("nneft", "Neftali"),
                ("neftali", "Neftali"),
                ("neft", "Neftali")
            };

            foreach (var (alias, person) in aliases)
            {
                if (normalized.Contains(alias))
                    return person;
            }

            return "Sin asignar";
        }

        private static string Normalize(string value)
        {
            var normalized = (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Normalize(NormalizationForm.FormD);

            var builder = new StringBuilder();

            foreach (var character in normalized)
            {
                var category = CharUnicodeInfo
                    .GetUnicodeCategory(character);

                if (category ==
                    UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(
                    char.IsLetterOrDigit(character)
                        ? character
                        : ' ');
            }

            return string.Join(
                " ",
                builder.ToString()
                    .Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ReadString(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                return string.Empty;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty
            };
        }

        private static Task<HttpResponseMessage> SendGetWithRetryAsync(
            HttpClient http,
            string requestUri,
            CancellationToken cancellationToken)
        {
            return NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    requestUri),
                cancellationToken,
                MaxRetryAttempts);
        }

        private static Task<HttpResponseMessage> SendPostWithRetryAsync(
            HttpClient http,
            string requestUri,
            string json,
            CancellationToken cancellationToken)
        {
            return NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    HttpMethod.Post,
                    requestUri)
                {
                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json")
                },
                cancellationToken,
                MaxRetryAttempts);
        }

        private static Task<HttpResponseMessage> SendPatchWithRetryAsync(
            HttpClient http,
            string requestUri,
            string json,
            CancellationToken cancellationToken)
        {
            return NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    HttpMethod.Patch,
                    requestUri)
                {
                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json")
                },
                cancellationToken,
                MaxRetryAttempts);
        }

        private static InvalidOperationException CreateNotionException(
            string operation,
            HttpResponseMessage response,
            string body)
        {
            var detail = body;

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                var code = ReadString(root, "code");
                var message = ReadString(root, "message");

                detail = string.IsNullOrWhiteSpace(code)
                    ? message
                    : $"{code}: {message}";
            }
            catch
            {
            }

            return new InvalidOperationException(
                $"Notion no pudo {operation} " +
                $"(HTTP {(int)response.StatusCode}): {detail}");
        }
    }
}