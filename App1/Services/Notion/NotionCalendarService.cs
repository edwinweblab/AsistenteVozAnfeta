using Anfeta.UI.Models.Notion;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    public sealed class NotionCalendarService
    {
        public string LastDiagnostics { get; private set; } = "";

        private const string CacheFileName =
            "notion_calendar_cache_v1.json";

        private static readonly SemaphoreSlim CacheLock =
            new(1, 1);

        private static readonly Dictionary<string, List<NotionCalendarActivity>>
            DayCache =
                new(StringComparer.Ordinal);

        private static bool _cacheLoaded;

        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";
        private const string RevisionesDataSourceId =
            "2eeabd7d-91b7-8193-a131-000b08cd54e2";

        private const int MaxRetryAttempts = 4;

        private static readonly string[] DateAliases =
        {
            "Fecha POR Hacer (Trabajando)",
            "Fecha por hacer",
            "Fecha POR Hacer",
            "Fecha de Inicio"
        };

        private static readonly string[] PersonAliases =
        {
            "Asignee/Ejecutor Principal",
            "Assignee / Ejecutor Principal",
            "Equipo weblab",
            "Fórmula Persona",
            "Asignado por"
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
            "Estado de trabajo",
            "Estado texto Actualización Proyecto",
            "Seguimiento Estado Proyecto",
            "Estatus IA"
        };

        private readonly ConcurrentDictionary<string, string> _relatedTitleCache =
            new(StringComparer.OrdinalIgnoreCase);

        private sealed record SchemaInfo(
            IReadOnlyList<string> DateProperties,
            string TitleProperty);

        public async Task<IReadOnlyList<NotionCalendarActivity>> GetDayAsync(
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

                if (ActivityOverlapsDay(
                        activity,
                        localDate.Date))
                {
                    activities.Add(activity);
                }
            }

            LastDiagnostics =
                $"Fechas: {string.Join(" | ", schema.DateProperties.Take(5))} · " +
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
                return DayCache.TryGetValue(
                        key,
                        out var cached)
                    ? cached
                        .OrderBy(x => x.Person)
                        .ThenBy(x => x.Start)
                        .ThenBy(x => x.Title)
                        .ToList()
                    : null;
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

        public async Task<bool> RefreshChangedSinceAsync(
            string token,
            DateTimeOffset changedAfterUtc,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            await EnsureCacheLoadedAsync(
                cancellationToken);

            using var http =
                CreateClient(token);

            var schema =
                await ReadSchemaAsync(
                    http,
                    cancellationToken);

            var changedPages =
                await QueryChangedPagesAsync(
                    http,
                    changedAfterUtc,
                    cancellationToken);

            if (changedPages.Count == 0)
                return false;

            var mapped =
                new List<NotionCalendarActivity>();

            var changedIds =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var page in changedPages)
            {
                cancellationToken.ThrowIfCancellationRequested();

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
                DayCache[key] =
                    activities.ToList();

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
                            DayCache[item.Key] = item.Value ?? new();
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
            CancellationToken cancellationToken)
        {
            var results = new List<JsonElement>();
            string? cursor = null;
            var hasMore = true;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

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
                        "Descargando Revisiones",
                        results.Count,
                        0,
                        $"Descargando lote {batchNumber} · {results.Count} páginas recibidas..."));

                var payload = new Dictionary<string, object?>
                {
                    ["page_size"] = 100
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
                    out _))
            {
                return null;
            }

            if (end <= start)
                end = start.AddHours(1);

            var title = ExtractPropertyText(
                FindProperty(props, schema.TitleProperty));

            if (string.IsNullOrWhiteSpace(title))
                title = "Actividad sin título";

            var people = await ReadPersonsAsync(
                http,
                props,
                title,
                cancellationToken);

            var project = ReadByAliases(
                props,
                ProjectAliases);

            var status = ReadByAliases(
                props,
                StatusAliases);

            var pageId = ReadString(page, "id");
            var pageUrl = ReadString(page, "url");

            return new NotionCalendarActivity
            {
                PageId = pageId,
                PageUrl = pageUrl,
                Title = title,
                Person = people,
                Project = project,
                Status = status,
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
            foreach (var alias in PersonAliases)
            {
                var prop = FindPropertyByAlias(
                    props,
                    alias);

                if (prop.ValueKind != JsonValueKind.Object)
                    continue;

                var type = ReadString(prop, "type");

                if (type == "people")
                {
                    var people = ExtractPeople(prop);
                    if (!string.IsNullOrWhiteSpace(people))
                        return NormalizePersonLabel(people);
                }

                if (type == "relation")
                {
                    var relations = ExtractRelationIds(prop);

                    var names = new List<string>();

                    foreach (var id in relations.Take(5))
                    {
                        var relatedTitle = await ResolveRelatedTitleAsync(
                            http,
                            id,
                            cancellationToken);

                        if (!string.IsNullOrWhiteSpace(relatedTitle))
                            names.Add(relatedTitle);
                    }

                    if (names.Count > 0)
                        return NormalizePersonLabel(
                            string.Join(", ", names));
                }

                var text = ExtractPropertyText(prop);
                if (!string.IsNullOrWhiteSpace(text))
                    return NormalizePersonLabel(text);
            }

            return DetectPersonFromTitle(title);
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
            var candidates = new List<(string Name, int Score)>();

            foreach (var property in properties.EnumerateObject())
            {
                var normalized = Normalize(property.Name);
                var type = ReadString(property.Value, "type");

                var isDateCapable =
                    type.Equals("date", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("formula", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("rollup", StringComparison.OrdinalIgnoreCase);

                if (!isDateCapable)
                    continue;

                var score = 0;

                foreach (var alias in DateAliases)
                {
                    var normalizedAlias = Normalize(alias);

                    if (normalized == normalizedAlias)
                        score = Math.Max(score, 100);
                    else if (normalized.Contains(normalizedAlias) ||
                             normalizedAlias.Contains(normalized))
                        score = Math.Max(score, 80);
                }

                if (normalized.Contains("fecha por hacer"))
                    score = Math.Max(score, 90);

                if (normalized.Contains("trabaj"))
                    score += 10;

                if (normalized.Contains("seguimiento"))
                    score = Math.Max(score, 35);

                if (normalized.Contains("inicio"))
                    score = Math.Max(score, 25);

                if (score > 0)
                    candidates.Add((property.Name, score));
            }

            // En caso de que el nombre haya cambiado, se agregan las demás
            // propiedades de fecha al final como respaldo.
            foreach (var property in properties.EnumerateObject())
            {
                var type = ReadString(property.Value, "type");

                if (type.Equals("date", StringComparison.OrdinalIgnoreCase) &&
                    candidates.All(x =>
                        !string.Equals(
                            x.Name,
                            property.Name,
                            StringComparison.Ordinal)))
                {
                    candidates.Add((property.Name, 1));
                }
            }

            return candidates
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Name)
                .Select(x => x.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
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
                : "Sin asignar";
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

                ("iisaia", "Isaias"),
                ("isaias", "Isaias"),
                ("isai", "Isaias"),

                ("eedua", "Eduardo"),
                ("eduardo", "Eduardo"),
                ("edua", "Eduardo"),

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

                ("nnetf", "Neftali"),
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

        private static async Task<HttpResponseMessage> SendGetWithRetryAsync(
            HttpClient http,
            string requestUri,
            CancellationToken cancellationToken)
        {
            return await SendWithRetryAsync(
                async () => await http.GetAsync(
                    requestUri,
                    cancellationToken),
                cancellationToken);
        }

        private static async Task<HttpResponseMessage> SendPostWithRetryAsync(
            HttpClient http,
            string requestUri,
            string json,
            CancellationToken cancellationToken)
        {
            return await SendWithRetryAsync(
                async () =>
                {
                    using var content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json");

                    return await http.PostAsync(
                        requestUri,
                        content,
                        cancellationToken);
                },
                cancellationToken);
        }

        private static async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<Task<HttpResponseMessage>> send,
            CancellationToken cancellationToken)
        {
            Exception? lastException = null;

            for (var attempt = 1;
                 attempt <= MaxRetryAttempts;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var response = await send();

                    var numeric = (int)response.StatusCode;
                    var retry =
                        response.StatusCode ==
                            HttpStatusCode.TooManyRequests ||
                        numeric == 529 ||
                        numeric >= 500;

                    if (!retry || attempt == MaxRetryAttempts)
                        return response;

                    response.Dispose();

                    await Task.Delay(
                        TimeSpan.FromSeconds(
                            Math.Min(
                                12,
                                Math.Pow(2, attempt - 1))),
                        cancellationToken);
                }
                catch (Exception ex)
                    when (attempt < MaxRetryAttempts &&
                          ex is HttpRequestException or TaskCanceledException)
                {
                    lastException = ex;

                    await Task.Delay(
                        TimeSpan.FromSeconds(
                            Math.Min(
                                12,
                                Math.Pow(2, attempt - 1))),
                        cancellationToken);
                }
            }

            throw new HttpRequestException(
                "Notion no respondió después de varios intentos.",
                lastException);
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
