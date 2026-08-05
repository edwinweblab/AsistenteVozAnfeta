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

    public sealed class NotionCalendarService
    {
        public string LastDiagnostics { get; private set; } = "";

        private const string CacheFileName =
            "notion_calendar_cache_v10.json";

        private static readonly SemaphoreSlim CacheLock =
            new(1, 1);

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

        private readonly ConcurrentDictionary<string, string> _relatedTitleCache =
            new(StringComparer.OrdinalIgnoreCase);

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

            var schema = await ReadSchemaAsync(
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
                    Project = activity.Project,
                    Status = activity.Status,
                    StatusColor = activity.StatusColor,
                    UpdateText = activity.UpdateText,
                    Description = activity.Description,
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

            return updated;
        }

        public async Task<NotionCalendarActivity>
            UpdateActivityScheduleAsync(
                string token,
                NotionCalendarActivity activity,
                DateTime targetStart,
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
                localStart.Add(duration);

            var startOffset =
                new DateTimeOffset(localStart);

            var endOffset =
                new DateTimeOffset(localEnd);

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
                    Project = activity.Project,
                    Status = activity.Status,
                    StatusColor = activity.StatusColor,
                    UpdateText = activity.UpdateText,
                    Description = activity.Description,
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

            return updated;
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
                await ReadSchemaAsync(
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
                foreach (var key in DayCache.Keys.ToList())
                {
                    DayCache[key].RemoveAll(activity =>
                        changedIds.Contains(activity.PageId));
                }

                foreach (var activity in mapped)
                {
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

                    var restored =
                        JsonSerializer.Deserialize<
                            Dictionary<string, List<NotionCalendarActivity>>>(
                                json);

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

        private static async Task SaveCacheUnsafeAsync(
            CancellationToken cancellationToken)
        {
            var path =
                GetCachePath();

            var json =
                JsonSerializer.Serialize(
                    DayCache,
                    new JsonSerializerOptions
                    {
                        WriteIndented = false
                    });

            await File.WriteAllTextAsync(
                path,
                json,
                cancellationToken);
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
                Project = project,
                Status = status,
                StatusColor = statusColor,
                UpdateText = updateText,
                Description = description,
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