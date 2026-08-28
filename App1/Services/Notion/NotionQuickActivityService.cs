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
    public sealed record NotionQuickTemplateItem(
        string PageId,
        string Title,
        string PageUrl);

    public sealed record NotionQuickPropertyOption(
        string Id,
        string Name);

    public sealed record NotionQuickPropertyCatalog(
        string PropertyName,
        string PropertyType,
        IReadOnlyList<NotionQuickPropertyOption> Options);

    public sealed record NotionQuickActivityResult(
        string PageId,
        string PageUrl,
        string TitleProperty,
        string DateProperty,
        bool BodyApplied = false,
        string TemplatePageId = "");

    public sealed record NotionQuickTemplateBatchRequest(
        string TemplatePageId,
        string Title,
        DateTime Start,
        DateTime End,
        IReadOnlyDictionary<string, string>? PropertyValues = null);

    public sealed record NotionQuickTemplateBatchProgress(
        int Current,
        int Total,
        string Title);

    public sealed record NotionQuickTemplateBatchItemResult(
        string TemplatePageId,
        string Title,
        bool Success,
        string CreatedPageId = "",
        string CreatedPageUrl = "",
        bool BodyApplied = false,
        string Error = "");

    /// <summary>
    /// Catálogo y creación de actividades rápidas basadas en páginas reales
    /// de Notion. El catálogo se obtiene consultando la vista configurada para
    /// WEB/SEO/ADS/etc. y la creación usa esa página como template_id para que
    /// Notion copie su BODY completo (checklists, toggles, synced blocks, etc.).
    /// </summary>
    public sealed class NotionQuickActivityService
    {
        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";

        private sealed record QuickSchema(
            string TitleProperty,
            string DateProperty,
            IReadOnlyDictionary<string, string> PropertyTypes);

        private sealed record TemplateCatalogCacheEntry(
            DateTimeOffset StoredAtUtc,
            IReadOnlyList<NotionQuickTemplateItem> Items);

        private static readonly ConcurrentDictionary<string, TemplateCatalogCacheEntry>
            TemplateCatalogCache = new(StringComparer.OrdinalIgnoreCase);

        private sealed record PropertyCatalogCacheEntry(
            DateTimeOffset StoredAtUtc,
            NotionQuickPropertyCatalog Catalog);

        private static readonly ConcurrentDictionary<string, PropertyCatalogCacheEntry>
            PropertyCatalogCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan PropertyCatalogCacheLifetime =
            TimeSpan.FromMinutes(30);

        // Cachea el data source real de cada página plantilla y su esquema.
        // Acceso Dominio y Acceso Correo viven en bases propias; no debemos
        // intentar leer sus selects/relaciones desde Revisiones.
        private sealed record PropertySchemaCacheEntry(
            DateTimeOffset StoredAtUtc,
            JsonElement Properties);

        private static readonly ConcurrentDictionary<string, string>
            PageDataSourceCache =
                new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, PropertySchemaCacheEntry>
            PropertySchemaCache =
                new(StringComparer.OrdinalIgnoreCase);

        // Genaro: el catálogo se busca UNA sola vez por vista y después se usa
        // desde disco/memoria. Solo "Actualizar catálogo" vuelve a consultar
        // esa vista exacta.
        private const string TemplateCatalogCacheFileName =
            "notion_quick_template_catalog_v4.json";

        private sealed class PersistedTemplateCatalog
        {
            public Dictionary<string, PersistedTemplateCatalogEntry> Views { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PersistedTemplateCatalogEntry
        {
            public DateTimeOffset UpdatedAtUtc { get; set; }
            public List<NotionQuickTemplateItem> Items { get; set; } = new();
        }

        private static readonly SemaphoreSlim TemplateCatalogPersistenceGate =
            new(1, 1);

        private static bool _templateCatalogPersistenceLoaded;

        private static async Task EnsureTemplateCatalogPersistenceLoadedAsync(
            CancellationToken cancellationToken)
        {
            if (_templateCatalogPersistenceLoaded)
                return;

            await TemplateCatalogPersistenceGate.WaitAsync(
                cancellationToken);

            try
            {
                if (_templateCatalogPersistenceLoaded)
                    return;

                try
                {
                    var path = Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        TemplateCatalogCacheFileName);

                    if (File.Exists(path))
                    {
                        var json = await File.ReadAllTextAsync(
                            path,
                            cancellationToken);

                        var persisted =
                            JsonSerializer.Deserialize<PersistedTemplateCatalog>(
                                json);

                        if (persisted?.Views != null)
                        {
                            foreach (var pair in persisted.Views)
                            {
                                if (string.IsNullOrWhiteSpace(pair.Key) ||
                                    pair.Value == null ||
                                    pair.Value.Items == null ||
                                    pair.Value.Items.Count == 0)
                                {
                                    continue;
                                }

                                TemplateCatalogCache[pair.Key] =
                                    new TemplateCatalogCacheEntry(
                                        pair.Value.UpdatedAtUtc,
                                        pair.Value.Items.ToList());
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // La caché es opcional. Si se corrompe se reconstruye
                    // únicamente cuando el usuario vuelva a actualizar esa vista.
                }

                _templateCatalogPersistenceLoaded = true;
            }
            finally
            {
                TemplateCatalogPersistenceGate.Release();
            }
        }

        private static async Task SaveTemplateCatalogPersistenceAsync(
            CancellationToken cancellationToken)
        {
            await TemplateCatalogPersistenceGate.WaitAsync(
                cancellationToken);

            try
            {
                try
                {
                    var snapshot =
                        new PersistedTemplateCatalog
                        {
                            Views = TemplateCatalogCache
                                .Where(item =>
                                    item.Value.Items != null &&
                                    item.Value.Items.Count > 0)
                                .ToDictionary(
                                    item => item.Key,
                                    item => new PersistedTemplateCatalogEntry
                                    {
                                        UpdatedAtUtc = item.Value.StoredAtUtc,
                                        Items = item.Value.Items.ToList()
                                    },
                                    StringComparer.OrdinalIgnoreCase)
                        };

                    var json =
                        JsonSerializer.Serialize(snapshot);

                    var path = Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        TemplateCatalogCacheFileName);

                    await File.WriteAllTextAsync(
                        path,
                        json,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Un fallo guardando la caché nunca invalida el catálogo
                    // que ya está disponible en memoria.
                }
            }
            finally
            {
                TemplateCatalogPersistenceGate.Release();
            }
        }

        // Compatibilidad con llamadas existentes.
        public Task<IReadOnlyList<NotionQuickTemplateItem>>
            GetTemplatesFromViewAsync(
                string token,
                string sourceViewUrl,
                bool forceRefresh,
                CancellationToken cancellationToken)
        {
            return GetTemplatesFromViewAsync(
                token,
                sourceViewUrl,
                forceRefresh,
                localResolver: null,
                cancellationToken);
        }

        /// <summary>
        /// Catálogo SCOPED: consulta ÚNICAMENTE la view_id exacta de WEB/SEO/ADS/etc.
        /// No consulta el data source completo de Revisiones.
        ///
        /// La View Query de Notion devuelve PageIds. Para evitar el patrón N+1,
        /// ANFETA intenta resolver título/URL desde App.LocalIndex mediante
        /// localResolver. Solo una página que todavía no esté en el índice se
        /// hidrata individualmente desde Notion.
        /// </summary>
        public async Task<IReadOnlyList<NotionQuickTemplateItem>>
            GetTemplatesFromViewAsync(
                string token,
                string sourceViewUrl,
                bool forceRefresh,
                Func<string, NotionQuickTemplateItem?>? localResolver,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            var viewId = ExtractViewId(sourceViewUrl);

            if (string.IsNullOrWhiteSpace(viewId))
            {
                throw new InvalidOperationException(
                    "No se pudo obtener el identificador de la vista de plantillas.");
            }

            await EnsureTemplateCatalogPersistenceLoadedAsync(
                cancellationToken);

            // SIN TTL: una vista que ya fue aprendida se abre al instante incluso
            // después de reiniciar ANFETA. El usuario decide cuándo refrescarla.
            if (!forceRefresh &&
                TemplateCatalogCache.TryGetValue(
                    viewId,
                    out var cached) &&
                cached.Items != null &&
                cached.Items.Count > 0)
            {
                return cached.Items.ToList();
            }

            // En refresh conservamos la copia anterior como respaldo. Nunca se
            // borra antes de que la consulta nueva haya terminado correctamente.
            TemplateCatalogCache.TryGetValue(
                viewId,
                out var previousCatalog);

            using var http = CreateCatalogClient(token);

            var items =
                new List<NotionQuickTemplateItem>();

            var seen =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            // IMPORTANTE:
            // No se hace GET /views + POST /data_sources/{id}/query.
            // Eso vuelve a pasar por el data source grande de Revisiones.
            //
            // Aquí usamos DIRECTAMENTE el view_id ya confirmado en el enlace:
            // POST /views/{view_id}/queries
            //
            // Por lo tanto SEO solo consulta SEO, WEB solo WEB, etc.
            // Catálogo interactivo: NO pasa por NotionRequestCoordinator.
            // RequestGate es global y puede estar ocupado por checklist,
            // revisión o refresh del calendario. Esta búsqueda ya está
            // restringida a UNA view_id exacta.
            using var firstResponse =
                await SendCatalogRequestDirectAsync(
                    http,
                    () => new HttpRequestMessage(
                        HttpMethod.Post,
                        $"views/{NormalizeId(viewId)}/queries")
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(
                                new Dictionary<string, object?>
                                {
                                    ["page_size"] = 25
                                }),
                            Encoding.UTF8,
                            "application/json")
                    },
                    cancellationToken);

            var firstJson =
                await firstResponse.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!firstResponse.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "consultar la vista específica de plantillas",
                    firstResponse,
                    firstJson);
            }

            using var firstDocument =
                JsonDocument.Parse(firstJson);

            var firstRoot =
                firstDocument.RootElement;

            var queryId =
                ReadString(
                    firstRoot,
                    "id");

            await AppendScopedTemplateResultsAsync(
                http,
                firstRoot,
                items,
                seen,
                localResolver,
                cancellationToken);

            var hasMore =
                ReadBoolean(
                    firstRoot,
                    "has_more");

            var cursor =
                ReadNullableString(
                    firstRoot,
                    "next_cursor");

            // Normalmente cada vista de plantillas cabe en la primera página.
            // Si algún día supera 100, solo pagina ESA MISMA vista.
            while (hasMore &&
                   !string.IsNullOrWhiteSpace(queryId) &&
                   !string.IsNullOrWhiteSpace(cursor))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requestUri =
                    $"views/{NormalizeId(viewId)}/queries/" +
                    $"{Uri.EscapeDataString(queryId)}" +
                    $"?start_cursor={Uri.EscapeDataString(cursor)}" +
                    "&page_size=25";

                using var response =
                    await SendCatalogRequestDirectAsync(
                        http,
                        () => new HttpRequestMessage(
                            HttpMethod.Get,
                            requestUri),
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "paginar la vista específica de plantillas",
                        response,
                        json);
                }

                using var document =
                    JsonDocument.Parse(json);

                var root =
                    document.RootElement;

                await AppendScopedTemplateResultsAsync(
                    http,
                    root,
                    items,
                    seen,
                    localResolver,
                    cancellationToken);

                hasMore =
                    ReadBoolean(
                        root,
                        "has_more");

                cursor =
                    ReadNullableString(
                        root,
                        "next_cursor");
            }

            var snapshot =
                items.ToList();

            TemplateCatalogCache[viewId] =
                new TemplateCatalogCacheEntry(
                    DateTimeOffset.UtcNow,
                    snapshot);

            // Guardado persistente: siguientes aperturas/reinicios = 0 requests.
            await SaveTemplateCatalogPersistenceAsync(
                CancellationToken.None);

            return snapshot;
        }

        private static async Task AppendScopedTemplateResultsAsync(
            HttpClient http,
            JsonElement root,
            List<NotionQuickTemplateItem> items,
            HashSet<string> seen,
            Func<string, NotionQuickTemplateItem?>? localResolver,
            CancellationToken cancellationToken)
        {
            if (!root.TryGetProperty(
                    "results",
                    out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var missing =
                new List<string>();

            foreach (var result in results.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(
                        ReadString(result, "object"),
                        "page",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pageId =
                    ReadString(
                        result,
                        "id");

                if (string.IsNullOrWhiteSpace(pageId) ||
                    !seen.Add(pageId))
                {
                    continue;
                }

                var local =
                    localResolver?.Invoke(pageId);

                if (local != null &&
                    !string.IsNullOrWhiteSpace(local.Title))
                {
                    items.Add(local);
                    continue;
                }

                missing.Add(pageId);
            }

            // Normalmente App.LocalIndex ya tiene estas páginas. Si falta
            // alguna plantilla nueva, se hidrata DIRECTO en lotes de 3:
            // mismo principio de N Proyecto, sin RequestGate global.
            const int catalogHydrationBatchSize = 3;

            for (var index = 0;
                 index < missing.Count;
                 index += catalogHydrationBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch =
                    missing
                        .Skip(index)
                        .Take(catalogHydrationBatchSize)
                        .ToList();

                var tasks =
                    batch.Select(
                        pageId =>
                            ReadCatalogPageDirectAsync(
                                http,
                                pageId,
                                cancellationToken))
                    .ToArray();

                var pages = await Task.WhenAll(tasks);

                for (var pageIndex = 0;
                     pageIndex < batch.Count;
                     pageIndex++)
                {
                    var hydrated = pages[pageIndex];

                    if (!hydrated.HasValue)
                        continue;

                    var title =
                        ReadPageTitle(
                            hydrated.Value);

                    if (string.IsNullOrWhiteSpace(title))
                        title = "Plantilla sin título";

                    items.Add(
                        new NotionQuickTemplateItem(
                            batch[pageIndex],
                            title,
                            ReadString(
                                hydrated.Value,
                                "url")));
                }

                if (index + catalogHydrationBatchSize < missing.Count)
                {
                    await Task.Delay(
                        120,
                        cancellationToken);
                }
            }
        }

        private static async Task<HttpResponseMessage>
            SendCatalogRequestDirectAsync(
                HttpClient http,
                Func<HttpRequestMessage> requestFactory,
                CancellationToken cancellationToken)
        {
            Exception? lastException = null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var request = requestFactory();

                try
                {
                    var response =
                        await http.SendAsync(
                            request,
                            HttpCompletionOption.ResponseContentRead,
                            cancellationToken);

                    var retryable =
                        response.StatusCode == HttpStatusCode.TooManyRequests ||
                        (int)response.StatusCode >= 500;

                    if (!retryable || attempt >= 1)
                        return response;

                    var delay =
                        response.Headers.RetryAfter?.Delta ??
                        TimeSpan.FromMilliseconds(500);

                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;

                    if (attempt >= 1)
                        throw;

                    await Task.Delay(
                        TimeSpan.FromMilliseconds(450),
                        cancellationToken);
                }
            }

            throw new InvalidOperationException(
                "No se pudo consultar la vista de plantillas.",
                lastException);
        }

        private static async Task<JsonElement?>
            ReadCatalogPageDirectAsync(
                HttpClient http,
                string pageId,
                CancellationToken cancellationToken)
        {
            try
            {
                using var response =
                    await SendCatalogRequestDirectAsync(
                        http,
                        () => new HttpRequestMessage(
                            HttpMethod.Get,
                            $"pages/{NormalizeId(pageId)}"),
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
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

        public async Task<string> GetPageDataSourceIdAsync(
            string token,
            string pageId,
            CancellationToken cancellationToken = default)
        {
            var cleanPageId = NormalizeId(pageId);

            if (string.IsNullOrWhiteSpace(cleanPageId))
                return string.Empty;

            if (PageDataSourceCache.TryGetValue(
                    cleanPageId,
                    out var cached) &&
                !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            using var http = CreateClient(token);

            using var response = await NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"pages/{cleanPageId}"),
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return string.Empty;

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty(
                    "parent",
                    out var parent) ||
                parent.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var dataSourceId =
                ReadString(parent, "data_source_id");

            if (string.IsNullOrWhiteSpace(dataSourceId))
                dataSourceId = ReadString(parent, "database_id");

            dataSourceId = NormalizeId(dataSourceId);

            if (!string.IsNullOrWhiteSpace(dataSourceId))
                PageDataSourceCache[cleanPageId] = dataSourceId;

            return dataSourceId;
        }

        private static async Task<JsonElement?> GetPropertySchemaAsync(
            HttpClient http,
            string dataSourceId,
            CancellationToken cancellationToken)
        {
            var cleanId = NormalizeId(dataSourceId);

            if (PropertySchemaCache.TryGetValue(
                    cleanId,
                    out var cached) &&
                DateTimeOffset.UtcNow - cached.StoredAtUtc <
                    PropertyCatalogCacheLifetime)
            {
                return cached.Properties.Clone();
            }

            using var schemaResponse =
                await NotionRequestCoordinator.SendAsync(
                    http,
                    () => new HttpRequestMessage(
                        HttpMethod.Get,
                        $"data_sources/{cleanId}"),
                    cancellationToken);

            var schemaJson =
                await schemaResponse.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!schemaResponse.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "consultar opciones de propiedades",
                    schemaResponse,
                    schemaJson);
            }

            using var schemaDocument = JsonDocument.Parse(schemaJson);

            if (!schemaDocument.RootElement.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var clone = properties.Clone();

            PropertySchemaCache[cleanId] =
                new PropertySchemaCacheEntry(
                    DateTimeOffset.UtcNow,
                    clone);

            return clone;
        }

        public async Task<NotionQuickPropertyCatalog> GetPropertyCatalogAsync(
            string token,
            string dataSourceId,
            IEnumerable<string> propertyAliases,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Configura primero el token de Notion.");

            if (string.IsNullOrWhiteSpace(dataSourceId))
                throw new InvalidOperationException("No se pudo identificar la base de Revisiones.");

            var aliases = (propertyAliases ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count == 0)
                return new NotionQuickPropertyCatalog(string.Empty, string.Empty, Array.Empty<NotionQuickPropertyOption>());

            var cacheKey = $"{NormalizeId(dataSourceId)}|{string.Join("|", aliases.Select(Normalize))}";

            if (!forceRefresh &&
                PropertyCatalogCache.TryGetValue(cacheKey, out var cached) &&
                DateTimeOffset.UtcNow - cached.StoredAtUtc < PropertyCatalogCacheLifetime)
            {
                return cached.Catalog;
            }

            using var http = CreateClient(token);

            var propertiesResult =
                await GetPropertySchemaAsync(
                    http,
                    dataSourceId,
                    cancellationToken);

            if (!propertiesResult.HasValue)
            {
                return new NotionQuickPropertyCatalog(
                    string.Empty,
                    string.Empty,
                    Array.Empty<NotionQuickPropertyOption>());
            }

            var properties = propertiesResult.Value;

            JsonProperty? matchedProperty = null;
            foreach (var alias in aliases)
            {
                var wanted = Normalize(alias);

                foreach (var item in properties.EnumerateObject())
                {
                    if (Normalize(item.Name) == wanted)
                    {
                        matchedProperty = item;
                        break;
                    }
                }

                if (matchedProperty.HasValue)
                    break;

                foreach (var item in properties.EnumerateObject())
                {
                    if (Normalize(item.Name).StartsWith(
                            wanted,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matchedProperty = item;
                        break;
                    }
                }

                if (matchedProperty.HasValue)
                    break;

                foreach (var item in properties.EnumerateObject())
                {
                    if (Normalize(item.Name).Contains(
                            wanted,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matchedProperty = item;
                        break;
                    }
                }

                if (matchedProperty.HasValue)
                    break;
            }

            if (!matchedProperty.HasValue)
                return new NotionQuickPropertyCatalog(string.Empty, string.Empty, Array.Empty<NotionQuickPropertyOption>());

            var propertyName = matchedProperty.Value.Name;
            var property = matchedProperty.Value.Value;
            var type = ReadString(property, "type");
            var options = new List<NotionQuickPropertyOption>();

            if ((type.Equals("select", StringComparison.OrdinalIgnoreCase) ||
                 type.Equals("status", StringComparison.OrdinalIgnoreCase) ||
                 type.Equals("multi_select", StringComparison.OrdinalIgnoreCase)) &&
                property.TryGetProperty(type, out var optionConfig) &&
                optionConfig.ValueKind == JsonValueKind.Object &&
                optionConfig.TryGetProperty("options", out var optionArray) &&
                optionArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in optionArray.EnumerateArray())
                {
                    var name = ReadString(option, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                        options.Add(new NotionQuickPropertyOption(ReadString(option, "id"), name));
                }
            }
            else if (type.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
                     property.TryGetProperty("relation", out var relation) &&
                     relation.ValueKind == JsonValueKind.Object)
            {
                var relatedDataSourceId = ReadString(relation, "data_source_id");
                if (string.IsNullOrWhiteSpace(relatedDataSourceId))
                    relatedDataSourceId = ReadString(relation, "database_id");

                // Algunas relaciones antiguas de Notion no exponen el destino
                // en el schema aunque la UI sí muestre "BD CLIENTES (bien)".
                // Para CLIENTES usamos la fuente conocida por ANFETA.
                if (Normalize(propertyName).Contains(
                        "clientes",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var clientsSource = NotionDataSources.Default
                        .FirstOrDefault(source => source.Name.Equals(
                            "Clientes",
                            StringComparison.OrdinalIgnoreCase));

                    if (clientsSource != null &&
                        !string.IsNullOrWhiteSpace(clientsSource.DataSourceId))
                    {
                        relatedDataSourceId = clientsSource.DataSourceId;
                    }
                }

                if (!string.IsNullOrWhiteSpace(relatedDataSourceId))
                {
                    string? cursor = null;
                    var hasMore = true;
                    // Un selector inicial no necesita descargar toda la base.
                    // Una página (100 clientes) mantiene la apertura del modal rápida.
                    const int maximumRelationOptions = 100;

                    while (hasMore && options.Count < maximumRelationOptions)
                    {
                        var payload = new Dictionary<string, object?> { ["page_size"] = 100 };
                        if (!string.IsNullOrWhiteSpace(cursor))
                            payload["start_cursor"] = cursor;

                        using var response = await NotionRequestCoordinator.SendAsync(
                            http,
                            () => new HttpRequestMessage(
                                HttpMethod.Post,
                                $"data_sources/{NormalizeId(relatedDataSourceId)}/query")
                            {
                                Content = new StringContent(
                                    JsonSerializer.Serialize(payload),
                                    Encoding.UTF8,
                                    "application/json")
                            },
                            cancellationToken);

                        var json = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode)
                            break;

                        using var document = JsonDocument.Parse(json);
                        var root = document.RootElement;
                        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var page in results.EnumerateArray())
                            {
                                var id = ReadString(page, "id");
                                var name = ReadPageTitle(page);
                                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                                    options.Add(new NotionQuickPropertyOption(id, name));

                                if (options.Count >= maximumRelationOptions)
                                    break;
                            }
                        }

                        hasMore = ReadBoolean(root, "has_more");
                        cursor = ReadNullableString(root, "next_cursor");
                        if (string.IsNullOrWhiteSpace(cursor))
                            hasMore = false;
                    }
                }
            }

            var catalog = new NotionQuickPropertyCatalog(
                propertyName,
                type,
                options
                    .GroupBy(item => string.IsNullOrWhiteSpace(item.Id) ? item.Name : item.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList());

            PropertyCatalogCache[cacheKey] = new PropertyCatalogCacheEntry(DateTimeOffset.UtcNow, catalog);
            return catalog;
        }

        public async Task SaveTemplatesAsync(
            string sourceViewUrl,
            IEnumerable<NotionQuickTemplateItem> items,
            CancellationToken cancellationToken = default)
        {
            var viewId =
                ExtractViewId(
                    sourceViewUrl);

            if (string.IsNullOrWhiteSpace(viewId))
                return;

            var snapshot =
                (items ?? Enumerable.Empty<NotionQuickTemplateItem>())
                    .Where(item =>
                        item != null &&
                        !string.IsNullOrWhiteSpace(item.PageId))
                    .GroupBy(
                        item => item.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            await EnsureTemplateCatalogPersistenceLoadedAsync(
                cancellationToken);

            TemplateCatalogCache[viewId] =
                new TemplateCatalogCacheEntry(
                    DateTimeOffset.UtcNow,
                    snapshot);

            await SaveTemplateCatalogPersistenceAsync(
                CancellationToken.None);
        }

        public async Task<IReadOnlyList<NotionQuickTemplateItem>>
            TryGetSavedTemplatesAsync(
                string sourceViewUrl,
                CancellationToken cancellationToken = default)
        {
            var viewId = ExtractViewId(sourceViewUrl);

            if (string.IsNullOrWhiteSpace(viewId))
                return Array.Empty<NotionQuickTemplateItem>();

            await EnsureTemplateCatalogPersistenceLoadedAsync(
                cancellationToken);

            return TemplateCatalogCache.TryGetValue(
                    viewId,
                    out var cached) &&
                cached.Items != null
                    ? cached.Items.ToList()
                    : Array.Empty<NotionQuickTemplateItem>();
        }

        public void ClearTemplateCatalogCache(string sourceViewUrl)
        {
            var viewId = ExtractViewId(sourceViewUrl);

            if (!string.IsNullOrWhiteSpace(viewId))
            {
                TemplateCatalogCache.TryRemove(viewId, out _);
            }
        }

        /// <summary>
        /// Crea una página nueva en Revisiones utilizando la página elegida
        /// como template_id. Notion aplica el BODY en segundo plano; el método
        /// espera de forma acotada y vuelve a fijar Título + Fecha POR Hacer al
        /// final para que los valores variables elegidos por el usuario ganen.
        /// </summary>
        public async Task<NotionQuickActivityResult> CreateFromTemplateAsync(
            string token,
            string dataSourceId,
            string templatePageId,
            string title,
            DateTime start,
            DateTime end,
            IReadOnlyDictionary<string, string>? propertyValues = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(templatePageId))
            {
                throw new InvalidOperationException(
                    "Selecciona una plantilla válida antes de crear la actividad.");
            }

            ValidateCreationInput(
                token,
                dataSourceId,
                ref title,
                ref start,
                ref end);

            using var http = CreateClient(token);

            var schema = await ReadSchemaAsync(
                http,
                dataSourceId,
                cancellationToken);

            return await CreateFromTemplateCoreAsync(
                http,
                schema,
                dataSourceId,
                templatePageId,
                title,
                start,
                end,
                propertyValues,
                cancellationToken);
        }

        /// <summary>
        /// Crea un lote de Plantilla Fase1 de forma SECUENCIAL.
        ///
        /// Importante:
        /// - Lee el esquema de Revisiones UNA sola vez para todo el lote.
        /// - Cada item conserva su template_id y por lo tanto su BODY propio.
        /// - Un fallo no cancela las demás plantillas.
        /// - No ejecuta refresh del calendario; eso se hace UNA sola vez al final
        ///   desde SearchView.Calendar.
        /// </summary>
        public async Task<IReadOnlyList<NotionQuickTemplateBatchItemResult>>
            CreateBatchFromTemplatesAsync(
                string token,
                string dataSourceId,
                IEnumerable<NotionQuickTemplateBatchRequest> requests,
                IProgress<NotionQuickTemplateBatchProgress>? progress = null,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (string.IsNullOrWhiteSpace(dataSourceId))
            {
                throw new InvalidOperationException(
                    "No se pudo identificar la base de Revisiones.");
            }

            var pending =
                (requests ??
                 Enumerable.Empty<NotionQuickTemplateBatchRequest>())
                    .Where(item =>
                        item != null &&
                        !string.IsNullOrWhiteSpace(item.TemplatePageId))
                    // La misma plantilla fuente solo puede formar una copia
                    // dentro de ESTA ejecución. Evita doble clic / selección
                    // duplicada en el ListView.
                    .GroupBy(
                        item => NormalizeId(item.TemplatePageId),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            if (pending.Count == 0)
            {
                return Array.Empty<NotionQuickTemplateBatchItemResult>();
            }

            using var http = CreateClient(token);

            // Ahorro importante en creación masiva:
            // antes CreateFromTemplateAsync volvía a leer el schema en cada item.
            var schema =
                await ReadSchemaAsync(
                    http,
                    dataSourceId,
                    cancellationToken);

            var results =
                new List<NotionQuickTemplateBatchItemResult>(
                    pending.Count);

            for (var index = 0;
                 index < pending.Count;
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item =
                    pending[index];

                progress?.Report(
                    new NotionQuickTemplateBatchProgress(
                        index + 1,
                        pending.Count,
                        item.Title));

                var title =
                    item.Title;

                var start =
                    item.Start;

                var end =
                    item.End;

                try
                {
                    ValidateCreationInput(
                        token,
                        dataSourceId,
                        ref title,
                        ref start,
                        ref end);

                    var created =
                        await CreateFromTemplateCoreAsync(
                            http,
                            schema,
                            dataSourceId,
                            item.TemplatePageId,
                            title,
                            start,
                            end,
                            item.PropertyValues,
                            cancellationToken);

                    results.Add(
                        new NotionQuickTemplateBatchItemResult(
                            NormalizeId(item.TemplatePageId),
                            title,
                            true,
                            created.PageId,
                            created.PageUrl,
                            created.BodyApplied,
                            string.Empty));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Una plantilla mala no aborta las demás.
                    results.Add(
                        new NotionQuickTemplateBatchItemResult(
                            NormalizeId(item.TemplatePageId),
                            title,
                            false,
                            string.Empty,
                            string.Empty,
                            false,
                            ex.Message));
                }
            }

            return results;
        }

        private static async Task<NotionQuickActivityResult>
            CreateFromTemplateCoreAsync(
                HttpClient http,
                QuickSchema schema,
                string dataSourceId,
                string templatePageId,
                string title,
                DateTime start,
                DateTime end,
                IReadOnlyDictionary<string, string>? propertyValues,
                CancellationToken cancellationToken)
        {
            var sourceHasBody =
                await PageHasBodyAsync(
                    http,
                    templatePageId,
                    cancellationToken);

            var properties =
                BuildCreationProperties(
                    schema,
                    title,
                    start,
                    end,
                    propertyValues);

            var payload =
                new Dictionary<string, object?>
                {
                    ["parent"] =
                        new Dictionary<string, object?>
                        {
                            ["type"] = "data_source_id",
                            ["data_source_id"] =
                                NormalizeId(dataSourceId)
                        },
                    ["properties"] = properties,
                    ["template"] =
                        new Dictionary<string, object?>
                        {
                            ["type"] = "template_id",
                            ["template_id"] =
                                NormalizeId(templatePageId)
                        }
                };

            using var response =
                await NotionRequestCoordinator.SendAsync(
                    http,
                    () =>
                        new HttpRequestMessage(
                            HttpMethod.Post,
                            "pages")
                        {
                            Content =
                                new StringContent(
                                    JsonSerializer.Serialize(
                                        payload),
                                    Encoding.UTF8,
                                    "application/json")
                        },
                    cancellationToken);

            var json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "crear la actividad desde plantilla",
                    response,
                    json);
            }

            using var document =
                JsonDocument.Parse(json);

            var pageId =
                ReadString(
                    document.RootElement,
                    "id");

            var pageUrl =
                ReadString(
                    document.RootElement,
                    "url");

            var bodyApplied =
                !sourceHasBody;

            if (!string.IsNullOrWhiteSpace(pageId) &&
                sourceHasBody)
            {
                bodyApplied =
                    await WaitForTemplateBodyAsync(
                        http,
                        pageId,
                        cancellationToken);
            }

            // Notion aplica el template de forma asíncrona.
            // Reafirmamos solamente título + Fecha POR Hacer.
            if (!string.IsNullOrWhiteSpace(pageId))
            {
                await PatchFinalPropertiesAsync(
                    http,
                    pageId,
                    properties,
                    cancellationToken);

                // Notion puede terminar de aplicar el template después de que el
                // BODY ya apareció y volver a colocar la fecha/hora del template.
                // Una segunda reafirmación estabiliza TODOS los elementos del lote.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(1200),
                    cancellationToken);

                await PatchFinalPropertiesAsync(
                    http,
                    pageId,
                    properties,
                    cancellationToken);
            }

            return new NotionQuickActivityResult(
                pageId,
                pageUrl,
                schema.TitleProperty,
                schema.DateProperty,
                bodyApplied,
                NormalizeId(templatePageId));
        }


        // Se conserva por compatibilidad con cualquier llamada vieja. No copia
        // BODY y solo debe usarse cuando explícitamente no hay plantilla fuente.
        public async Task<NotionQuickActivityResult> CreateAsync(
            string token,
            string dataSourceId,
            string title,
            DateTime start,
            DateTime end,
            CancellationToken cancellationToken = default)
        {
            ValidateCreationInput(
                token,
                dataSourceId,
                ref title,
                ref start,
                ref end);

            using var http = CreateClient(token);

            var schema = await ReadSchemaAsync(
                http,
                dataSourceId,
                cancellationToken);

            var payload = new Dictionary<string, object?>
            {
                ["parent"] = new Dictionary<string, object?>
                {
                    ["type"] = "data_source_id",
                    ["data_source_id"] = NormalizeId(dataSourceId)
                },
                ["properties"] = BuildCreationProperties(
                    schema,
                    title,
                    start,
                    end)
            };

            using var response = await NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(HttpMethod.Post, "pages")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        Encoding.UTF8,
                        "application/json")
                },
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "crear la actividad rápida",
                    response,
                    json);
            }

            using var document = JsonDocument.Parse(json);

            return new NotionQuickActivityResult(
                ReadString(document.RootElement, "id"),
                ReadString(document.RootElement, "url"),
                schema.TitleProperty,
                schema.DateProperty);
        }

        private static async Task AppendTemplateResultsAsync(
            HttpClient http,
            JsonElement root,
            List<NotionQuickTemplateItem> items,
            HashSet<string> seen,
            CancellationToken cancellationToken)
        {
            if (!root.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var result in results.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(
                        ReadString(result, "object"),
                        "page",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pageId = ReadString(result, "id");

                if (string.IsNullOrWhiteSpace(pageId) ||
                    !seen.Add(pageId))
                {
                    continue;
                }

                var page = result.Clone();
                var title = ReadPageTitle(page);
                var pageUrl = ReadString(page, "url");

                if (string.IsNullOrWhiteSpace(title))
                {
                    var hydrated = await ReadPageAsync(
                        http,
                        pageId,
                        cancellationToken);

                    if (hydrated.HasValue)
                    {
                        page = hydrated.Value;
                        title = ReadPageTitle(page);
                        pageUrl = ReadString(page, "url");
                    }
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = "Plantilla sin título";
                }

                items.Add(
                    new NotionQuickTemplateItem(
                        pageId,
                        title,
                        pageUrl));
            }
        }

        private static async Task<JsonElement?> ReadPageAsync(
            HttpClient http,
            string pageId,
            CancellationToken cancellationToken)
        {
            using var response = await NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"pages/{NormalizeId(pageId)}"),
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static string ReadPageTitle(JsonElement page)
        {
            if (!page.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var property in properties.EnumerateObject())
            {
                var value = property.Value;

                if (!string.Equals(
                        ReadString(value, "type"),
                        "title",
                        StringComparison.OrdinalIgnoreCase) ||
                    !value.TryGetProperty("title", out var titleArray) ||
                    titleArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var builder = new StringBuilder();

                foreach (var item in titleArray.EnumerateArray())
                {
                    var plain = ReadString(item, "plain_text");

                    if (!string.IsNullOrWhiteSpace(plain))
                    {
                        builder.Append(plain);
                        continue;
                    }

                    if (item.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.Object)
                    {
                        builder.Append(ReadString(text, "content"));
                    }
                }

                return builder.ToString().Trim();
            }

            return string.Empty;
        }

        private static async Task<bool> PageHasBodyAsync(
            HttpClient http,
            string pageId,
            CancellationToken cancellationToken)
        {
            using var response = await NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"blocks/{NormalizeId(pageId)}/children?page_size=1"),
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // No se bloquea la creación por no poder previsualizar el body.
                // Notion igualmente intentará aplicar template_id.
                return true;
            }

            using var document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty(
                       "results",
                       out var results) &&
                   results.ValueKind == JsonValueKind.Array &&
                   results.GetArrayLength() > 0;
        }

        private static async Task<bool> WaitForTemplateBodyAsync(
            HttpClient http,
            string pageId,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 24;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await PageHasBodyAsync(
                        http,
                        pageId,
                        cancellationToken))
                {
                    return true;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(750),
                    cancellationToken);
            }

            return false;
        }

        private static async Task PatchFinalPropertiesAsync(
            HttpClient http,
            string pageId,
            Dictionary<string, object?> properties,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>
            {
                ["properties"] = properties
            };

            using var response = await NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    HttpMethod.Patch,
                    $"pages/{NormalizeId(pageId)}")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        Encoding.UTF8,
                        "application/json")
                },
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "aplicar los datos finales de la actividad",
                    response,
                    json);
            }
        }

        private static Dictionary<string, object?> BuildCreationProperties(
            QuickSchema schema,
            string title,
            DateTime start,
            DateTime end,
            IReadOnlyDictionary<string, string>? propertyValues = null)
        {
            var localStart = DateTime.SpecifyKind(start, DateTimeKind.Local);
            var localEnd = DateTime.SpecifyKind(end, DateTimeKind.Local);

            var result = new Dictionary<string, object?>
            {
                [schema.TitleProperty] = new Dictionary<string, object?>
                {
                    ["title"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = new Dictionary<string, object?>
                            {
                                ["content"] = title
                            }
                        }
                    }
                },
                [schema.DateProperty] = new Dictionary<string, object?>
                {
                    ["date"] = new Dictionary<string, object?>
                    {
                        ["start"] = new DateTimeOffset(localStart).ToString("O"),
                        ["end"] = new DateTimeOffset(localEnd).ToString("O")
                    }
                }
            };

            foreach (var pair in propertyValues ??
                     new Dictionary<string, string>())
            {
                var wanted = Normalize(pair.Key);
                var actualName = schema.PropertyTypes.Keys.FirstOrDefault(name =>
                    Normalize(name) == wanted) ??
                    schema.PropertyTypes.Keys.FirstOrDefault(name =>
                        Normalize(name).StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) ??
                    schema.PropertyTypes.Keys.FirstOrDefault(name =>
                        Normalize(name).Contains(wanted, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(actualName) ||
                    string.IsNullOrWhiteSpace(pair.Value))
                    continue;

                var type = schema.PropertyTypes[actualName];
                result[actualName] = type switch
                {
                    "url" => new Dictionary<string, object?> { ["url"] = pair.Value.Trim() },
                    "select" => new Dictionary<string, object?>
                    {
                        ["select"] = new Dictionary<string, object?> { ["name"] = pair.Value.Trim() }
                    },
                    "status" => new Dictionary<string, object?>
                    {
                        ["status"] = new Dictionary<string, object?> { ["name"] = pair.Value.Trim() }
                    },
                    "multi_select" => new Dictionary<string, object?>
                    {
                        ["multi_select"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = pair.Value.Trim()
                            }
                        }
                    },
                    "rich_text" => new Dictionary<string, object?>
                    {
                        ["rich_text"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = new Dictionary<string, object?> { ["content"] = pair.Value.Trim() }
                            }
                        }
                    },
                    "relation" => new Dictionary<string, object?>
                    {
                        ["relation"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = NormalizeId(pair.Value)
                            }
                        }
                    },
                    _ => result.TryGetValue(actualName, out var existing) ? existing : null
                };

                if (result[actualName] == null)
                    result.Remove(actualName);
            }

            return result;
        }

        private static void ValidateCreationInput(
            string token,
            string dataSourceId,
            ref string title,
            ref DateTime start,
            ref DateTime end)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (string.IsNullOrWhiteSpace(dataSourceId))
            {
                throw new InvalidOperationException(
                    "No se pudo identificar la base de Revisiones.");
            }

            title = (title ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                throw new InvalidOperationException(
                    "El título de la actividad está vacío.");
            }

            if (end <= start)
            {
                end = start.AddHours(1);
            }
        }

        private static async Task<QuickSchema> ReadSchemaAsync(
            HttpClient http,
            string dataSourceId,
            CancellationToken cancellationToken)
        {
            using var response = await NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"data_sources/{NormalizeId(dataSourceId)}"),
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "consultar el esquema de Revisiones",
                    response,
                    json);
            }

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Notion no devolvió las propiedades de Revisiones.");
            }

            var titleProperty = properties
                .EnumerateObject()
                .FirstOrDefault(property =>
                    ReadString(property.Value, "type")
                        .Equals("title", StringComparison.OrdinalIgnoreCase))
                .Name ?? string.Empty;

            var dateProperty = properties
                .EnumerateObject()
                .Where(property =>
                    ReadString(property.Value, "type")
                        .Equals("date", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(property =>
                    Normalize(property.Name) == "fecha por hacer")
                .ThenByDescending(property =>
                    Normalize(property.Name).Contains(
                        "fecha por hacer",
                        StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Name)
                .FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(titleProperty))
            {
                throw new InvalidOperationException(
                    "No se encontró la propiedad de título en Revisiones.");
            }

            if (string.IsNullOrWhiteSpace(dateProperty))
            {
                throw new InvalidOperationException(
                    "No se encontró la propiedad editable Fecha POR Hacer en Revisiones.");
            }

            var propertyTypes = properties.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ReadString(property.Value, "type"),
                StringComparer.OrdinalIgnoreCase);

            return new QuickSchema(titleProperty, dateProperty, propertyTypes);
        }

        private static string ExtractViewId(string sourceViewUrl)
        {
            if (!Uri.TryCreate(
                    (sourceViewUrl ?? string.Empty).Trim(),
                    UriKind.Absolute,
                    out var uri))
            {
                return string.Empty;
            }

            var query = uri.Query.TrimStart('?');

            foreach (var pair in query.Split(
                         '&',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);

                if (parts.Length != 2 ||
                    !string.Equals(
                        Uri.UnescapeDataString(parts[0]),
                        "v",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return Uri.UnescapeDataString(parts[1]).Trim();
            }

            return string.Empty;
        }

        private static HttpClient CreateCatalogClient(
            string token)
        {
            var http =
                new HttpClient
                {
                    BaseAddress = new Uri(NotionBaseUrl),
                    Timeout = TimeSpan.FromSeconds(14)
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

        private static HttpClient CreateClient(string token)
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(NotionBaseUrl),
                Timeout = TimeSpan.FromSeconds(90)
            };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Trim());

            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "Notion-Version",
                NotionVersion);

            return http;
        }

        private static string NormalizeId(string value) =>
            (value ?? string.Empty).Trim();

        private static string Normalize(string value)
        {
            var normalized = (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Normalize(NormalizationForm.FormD);

            var builder = new StringBuilder();

            foreach (var character in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(character);

                if (category == UnicodeCategory.NonSpacingMark)
                    continue;

                builder.Append(
                    char.IsLetterOrDigit(character)
                        ? character
                        : ' ');
            }

            return string.Join(
                " ",
                builder.ToString().Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool ReadBoolean(
            JsonElement element,
            string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }

        private static string ReadNullableString(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString() ?? string.Empty;
        }

        private static string ReadString(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString() ?? string.Empty;
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
