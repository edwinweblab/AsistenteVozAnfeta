using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Catálogo final de Plantillas ANFETA.
    ///
    /// REGLA DEFINITIVA:
    /// Solo se usan páginas cuya propiedad "Plantilla Fases" sea EXACTAMENTE:
    /// "Plantilla Fase1".
    ///
    /// Las views WEB/SEO/ADS/etc. solo sirven para resolver el data_source_id
    /// correcto. No se ejecutan como query.
    ///
    /// Después de obtener una sola vez Plantilla Fase1:
    /// - WEB  = título contiene token exacto wwebs
    /// - SEO  = título contiene token exacto sseo
    /// - ADS  = título contiene token exacto aads
    /// - COBRO = título contiene token exacto ccobr
    /// - BIBL = título contiene token exacto bbibl
    /// - ACTIVIDAD RÁPIDA = familia [ttipo-actividad] / [ttipo-actividad-rrapi rapido]
    ///
    /// El catálogo Fase1 completo se persiste localmente.
    /// </summary>
    public sealed class NotionTemplateCatalogService
    {
        private const string NotionBaseUrl =
            "https://api.notion.com/v1/";

        private const string NotionVersion =
            "2026-03-11";

        private const string RequiredTemplatePhase =
            "Plantilla Fase1";

        private const string CacheFileName =
            "notion_template_phase1_catalog_v2.json";

        // Fase1 debe ser mucho más pequeña que todo Plantilla Fases.
        // Dejamos un margen amplio sin permitir un recorrido accidental enorme.
        private const int HardPhase1Limit = 400;

        private static readonly SemaphoreSlim CacheGate =
            new(1, 1);

        private static bool _cacheLoaded;

        private static string _dataSourceId =
            string.Empty;

        private static List<NotionQuickTemplateItem>
            _phase1Templates =
                new();

        private sealed class PersistedCatalog
        {
            public string DataSourceId { get; set; } =
                string.Empty;

            public DateTimeOffset UpdatedAtUtc { get; set; }

            public List<NotionQuickTemplateItem> Items { get; set; } =
                new();
        }

        public async Task<IReadOnlyList<NotionQuickTemplateItem>>
            GetTemplatesForViewAsync(
                string token,
                string sourceViewUrl,
                string projectToken,
                bool forceRefresh = false,
                IProgress<string>? progress = null,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            await EnsureCacheLoadedAsync(
                cancellationToken);

            // Después de la primera lectura de Fase1, todas las categorías
            // salen 100% local.
            if (!forceRefresh &&
                _phase1Templates.Count > 0)
            {
                var cachedCategory =
                    FilterCategory(
                        _phase1Templates,
                        projectToken);

                progress?.Report(
                    $"Plantilla Fase1 desde caché ✅ · " +
                    $"{cachedCategory.Count} de {_phase1Templates.Count}");

                return cachedCategory;
            }

            using var http =
                CreateClient(token);

            if (string.IsNullOrWhiteSpace(_dataSourceId) ||
                forceRefresh)
            {
                var viewId =
                    ExtractViewId(
                        sourceViewUrl);

                if (string.IsNullOrWhiteSpace(viewId))
                {
                    throw new InvalidOperationException(
                        "El enlace de Plantillas no contiene ?v=VIEW_ID.");
                }

                progress?.Report(
                    "Resolviendo data source desde la view…");

                _dataSourceId =
                    await ReadDataSourceIdFromViewAsync(
                        http,
                        viewId,
                        cancellationToken);

                if (string.IsNullOrWhiteSpace(_dataSourceId))
                {
                    throw new InvalidOperationException(
                        "La view respondió sin data_source_id.");
                }
            }

            progress?.Report(
                $"Consultando SOLO {RequiredTemplatePhase}…");

            var exactPhaseFilter =
                await BuildExactPhase1FilterAsync(
                    http,
                    _dataSourceId,
                    cancellationToken);

            var phase1 =
                await QueryExactPhase1Async(
                    http,
                    _dataSourceId,
                    exactPhaseFilter,
                    progress,
                    cancellationToken);

            _phase1Templates =
                phase1
                    .Where(item =>
                        item != null &&
                        !string.IsNullOrWhiteSpace(item.PageId) &&
                        !string.IsNullOrWhiteSpace(item.Title))
                    .GroupBy(
                        item => item.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            await SaveCacheAsync(
                CancellationToken.None);

            var category =
                FilterCategory(
                    _phase1Templates,
                    projectToken);

            progress?.Report(
                $"{RequiredTemplatePhase} lista ✅ · " +
                $"{_phase1Templates.Count} totales · " +
                $"{category.Count} de esta categoría");

            return category;
        }

        public async Task<IReadOnlyList<NotionQuickTemplateItem>>
            TryGetCachedForViewAsync(
                string sourceViewUrl,
                CancellationToken cancellationToken = default)
        {
            await EnsureCacheLoadedAsync(
                cancellationToken);

            if (_phase1Templates.Count == 0)
                return Array.Empty<NotionQuickTemplateItem>();

            var viewId =
                ExtractViewId(
                    sourceViewUrl);

            var projectToken =
                GetProjectTokenForKnownView(
                    viewId);

            return string.IsNullOrWhiteSpace(projectToken)
                ? Array.Empty<NotionQuickTemplateItem>()
                : FilterCategory(
                    _phase1Templates,
                    projectToken);
        }

        private static async Task<string>
            ReadDataSourceIdFromViewAsync(
                HttpClient http,
                string viewId,
                CancellationToken cancellationToken)
        {
            using var response =
                await http.GetAsync(
                    $"views/{NormalizeId(viewId)}",
                    cancellationToken);

            var json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"No se pudo leer la metadata de la view " +
                    $"(HTTP {(int)response.StatusCode}): {TrimError(json)}");
            }

            using var document =
                JsonDocument.Parse(json);

            return ReadString(
                document.RootElement,
                "data_source_id");
        }

        private static async Task<JsonElement>
            BuildExactPhase1FilterAsync(
                HttpClient http,
                string dataSourceId,
                CancellationToken cancellationToken)
        {
            using var response =
                await http.GetAsync(
                    $"data_sources/{NormalizeId(dataSourceId)}",
                    cancellationToken);

            var json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"No se pudo leer el esquema de Plantillas " +
                    $"(HTTP {(int)response.StatusCode}): {TrimError(json)}");
            }

            using var document =
                JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Notion no devolvió las propiedades del data source.");
            }

            string propertyName =
                string.Empty;

            string propertyType =
                string.Empty;

            foreach (var property in properties.EnumerateObject())
            {
                if (!IsTemplatePhasePropertyName(
                        property.Name))
                {
                    continue;
                }

                propertyName =
                    property.Name;

                propertyType =
                    ReadString(
                        property.Value,
                        "type");

                break;
            }

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new InvalidOperationException(
                    "No encontré la propiedad 'Plantilla Fases'.");
            }

            object filterObject =
                propertyType switch
                {
                    "select" =>
                        new Dictionary<string, object?>
                        {
                            ["property"] = propertyName,
                            ["select"] =
                                new Dictionary<string, object?>
                                {
                                    ["equals"] =
                                        RequiredTemplatePhase
                                }
                        },

                    "status" =>
                        new Dictionary<string, object?>
                        {
                            ["property"] = propertyName,
                            ["status"] =
                                new Dictionary<string, object?>
                                {
                                    ["equals"] =
                                        RequiredTemplatePhase
                                }
                        },

                    "multi_select" =>
                        new Dictionary<string, object?>
                        {
                            ["property"] = propertyName,
                            ["multi_select"] =
                                new Dictionary<string, object?>
                                {
                                    ["contains"] =
                                        RequiredTemplatePhase
                                }
                        },

                    "rich_text" =>
                        new Dictionary<string, object?>
                        {
                            ["property"] = propertyName,
                            ["rich_text"] =
                                new Dictionary<string, object?>
                                {
                                    ["equals"] =
                                        RequiredTemplatePhase
                                }
                        },

                    _ =>
                        throw new InvalidOperationException(
                            $"'Plantilla Fases' es tipo '{propertyType}'. " +
                            "No se hará una consulta amplia; agrega soporte " +
                            "explícito para ese tipo.")
                };

            using var filterDocument =
                JsonDocument.Parse(
                    JsonSerializer.Serialize(
                        filterObject));

            return filterDocument.RootElement.Clone();
        }

        private static async Task<List<NotionQuickTemplateItem>>
            QueryExactPhase1Async(
                HttpClient http,
                string dataSourceId,
                JsonElement filter,
                IProgress<string>? progress,
                CancellationToken cancellationToken)
        {
            var results =
                new List<NotionQuickTemplateItem>();

            string? cursor =
                null;

            var hasMore =
                true;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload =
                    new Dictionary<string, object?>
                    {
                        ["page_size"] = 100,
                        ["filter"] = filter
                    };

                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    payload["start_cursor"] =
                        cursor;
                }

                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        $"data_sources/{NormalizeId(dataSourceId)}/query")
                    {
                        Content =
                            new StringContent(
                                JsonSerializer.Serialize(payload),
                                Encoding.UTF8,
                                "application/json")
                    };

                using var response =
                    await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseContentRead,
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"No se pudo consultar {RequiredTemplatePhase} " +
                        $"(HTTP {(int)response.StatusCode}): {TrimError(json)}");
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
                    {
                        if (IsArchivedOrTrashed(page))
                            continue;

                        var pageId =
                            ReadString(
                                page,
                                "id");

                        var title =
                            ReadPageTitle(
                                page);

                        if (string.IsNullOrWhiteSpace(pageId) ||
                            string.IsNullOrWhiteSpace(title))
                        {
                            continue;
                        }

                        results.Add(
                            new NotionQuickTemplateItem(
                                pageId,
                                title,
                                ReadString(
                                    page,
                                    "url")));

                        if (results.Count >= HardPhase1Limit)
                        {
                            throw new InvalidOperationException(
                                $"{RequiredTemplatePhase} superó el límite de " +
                                $"{HardPhase1Limit} páginas. Se canceló porque " +
                                "ya no sería el conjunto esperado.");
                        }
                    }
                }

                progress?.Report(
                    $"{RequiredTemplatePhase} · {results.Count} detectadas…");

                hasMore =
                    root.TryGetProperty(
                        "has_more",
                        out var more) &&
                    more.ValueKind == JsonValueKind.True;

                cursor =
                    root.TryGetProperty(
                        "next_cursor",
                        out var next) &&
                    next.ValueKind == JsonValueKind.String
                        ? next.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(cursor))
                    hasMore = false;
            }

            return results;
        }

        private static List<NotionQuickTemplateItem>
            FilterCategory(
                IEnumerable<NotionQuickTemplateItem> source,
                string projectToken)
        {
            var token =
                (projectToken ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(token))
                return new List<NotionQuickTemplateItem>();

            return source
                .Where(item =>
                    item != null &&
                    (token == "rrapi"
                        ? IsQuickActivityTemplateTitle(item.Title)
                        : token == "aacce-ccorre"
                            ? IsAccessTemplateTitle(item.Title, "ccorre")
                            : token == "aacce-ddomi"
                                ? IsAccessTemplateTitle(item.Title, "ddomi")
                        : TitleContainsExactToken(
                            item.Title,
                            token)))
                .OrderBy(item =>
                    ExtractOrder(
                        item.Title) ??
                    double.MaxValue)
                .ThenBy(
                    item => item.Title,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsQuickActivityTemplateTitle(
            string title)
        {
            return !string.IsNullOrWhiteSpace(title) &&
                Regex.IsMatch(
                    title,
                    @"\[ttipo-actividad(?:-rrapi\s+rapido)?\]",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);
        }

        private static bool IsAccessTemplateTitle(
            string title,
            string targetToken)
        {
            if (string.IsNullOrWhiteSpace(title) ||
                !TitleContainsExactToken(title, "aacce"))
                return false;

            // Las plantillas reales no comparten todos los tokens intermedios:
            // dominio usa aacce/ccont/ddomi y correo usa aacce/ccorr.
            return targetToken.Equals("ddomi", StringComparison.OrdinalIgnoreCase)
                ? TitleContainsExactToken(title, "ddomi")
                : TitleContainsExactToken(title, "ccorr") ||
                  TitleContainsExactToken(title, "ccorre");
        }

        private static bool TitleContainsExactToken(
            string title,
            string token)
        {
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            return Regex.IsMatch(
                title,
                $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(token)}(?![\p{{L}}\p{{Nd}}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private static double? ExtractOrder(
            string title)
        {
            var match =
                Regex.Match(
                    title ?? string.Empty,
                    @"(?<![\d.])(?<n>\d{1,3}\.\d{1,3})(?![\d.])",
                    RegexOptions.CultureInvariant);

            if (!match.Success)
                return null;

            return double.TryParse(
                match.Groups["n"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
                    ? value
                    : null;
        }

        private static string GetProjectTokenForKnownView(
            string viewId)
        {
            return (viewId ?? string.Empty)
                .Trim()
                .ToLowerInvariant() switch
            {
                "393abd7d91b780869898000c6b7bfcea" =>
                    "wwebs",

                "393abd7d91b7800bab7e000ce5ee4e1d" =>
                    "sseo",

                "393abd7d91b7806182c8000c86c9b308" =>
                    "aads",

                "3aeabd7d91b780d4bbc4000c27a2ad82" =>
                    "ccobr",

                "393abd7d91b780209653000cd91dff96" =>
                    "bbibl",

                "393abd7d91b7803e9921000c068624c5" =>
                    "rrapi",

                _ =>
                    string.Empty
            };
        }

        private static bool IsTemplatePhasePropertyName(
            string value)
        {
            var compact =
                Regex.Replace(
                        value ?? string.Empty,
                        @"[^\p{L}\p{Nd}]",
                        string.Empty)
                    .ToLowerInvariant();

            return compact is
                "plantillafases" or
                "plantillafase" or
                "plantillasfases";
        }

        private static string ReadPageTitle(
            JsonElement page)
        {
            if (!page.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (!string.Equals(
                        ReadString(
                            property.Value,
                            "type"),
                        "title",
                        StringComparison.OrdinalIgnoreCase) ||
                    !property.Value.TryGetProperty(
                        "title",
                        out var titleArray) ||
                    titleArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var builder =
                    new StringBuilder();

                foreach (var item in titleArray.EnumerateArray())
                {
                    var plain =
                        ReadString(
                            item,
                            "plain_text");

                    if (!string.IsNullOrWhiteSpace(plain))
                        builder.Append(plain);
                }

                return builder
                    .ToString()
                    .Trim();
            }

            return string.Empty;
        }

        private static bool IsArchivedOrTrashed(
            JsonElement page)
        {
            var archived =
                page.TryGetProperty(
                    "archived",
                    out var archivedValue) &&
                archivedValue.ValueKind == JsonValueKind.True;

            var inTrash =
                page.TryGetProperty(
                    "in_trash",
                    out var trashValue) &&
                trashValue.ValueKind == JsonValueKind.True;

            return archived || inTrash;
        }

        private static string ExtractViewId(
            string sourceViewUrl)
        {
            var match =
                Regex.Match(
                    sourceViewUrl ?? string.Empty,
                    @"[?&]v=(?<id>[0-9a-fA-F]{32})",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            return match.Success
                ? match.Groups["id"].Value
                : string.Empty;
        }

        private static async Task EnsureCacheLoadedAsync(
            CancellationToken cancellationToken)
        {
            if (_cacheLoaded)
                return;

            await CacheGate.WaitAsync(
                cancellationToken);

            try
            {
                if (_cacheLoaded)
                    return;

                try
                {
                    var path =
                        Path.Combine(
                            ApplicationData.Current.LocalFolder.Path,
                            CacheFileName);

                    if (File.Exists(path))
                    {
                        var json =
                            await File.ReadAllTextAsync(
                                path,
                                cancellationToken);

                        var stored =
                            JsonSerializer.Deserialize<PersistedCatalog>(
                                json);

                        if (stored != null)
                        {
                            _dataSourceId =
                                stored.DataSourceId ??
                                string.Empty;

                            _phase1Templates =
                                (stored.Items ??
                                 new List<NotionQuickTemplateItem>())
                                    .Where(item =>
                                        item != null &&
                                        !string.IsNullOrWhiteSpace(item.PageId))
                                    .ToList();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }

                _cacheLoaded = true;
            }
            finally
            {
                CacheGate.Release();
            }
        }

        private static async Task SaveCacheAsync(
            CancellationToken cancellationToken)
        {
            await CacheGate.WaitAsync(
                cancellationToken);

            try
            {
                try
                {
                    var stored =
                        new PersistedCatalog
                        {
                            DataSourceId =
                                _dataSourceId,

                            UpdatedAtUtc =
                                DateTimeOffset.UtcNow,

                            Items =
                                _phase1Templates.ToList()
                        };

                    var path =
                        Path.Combine(
                            ApplicationData.Current.LocalFolder.Path,
                            CacheFileName);

                    await File.WriteAllTextAsync(
                        path,
                        JsonSerializer.Serialize(stored),
                        cancellationToken);
                }
                catch
                {
                }
            }
            finally
            {
                CacheGate.Release();
            }
        }

        private static HttpClient CreateClient(
            string token)
        {
            var http =
                new HttpClient
                {
                    BaseAddress =
                        new Uri(NotionBaseUrl),

                    Timeout =
                        TimeSpan.FromSeconds(18)
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

        private static string NormalizeId(
            string value) =>
            (value ?? string.Empty)
                .Trim();

        private static string ReadString(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(
                    propertyName,
                    out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString() ??
                   string.Empty;
        }

        private static string TrimError(
            string value)
        {
            var text =
                (value ?? string.Empty)
                    .Trim();

            return text.Length <= 600
                ? text
                : text.Substring(0, 600);
        }
    }
}
