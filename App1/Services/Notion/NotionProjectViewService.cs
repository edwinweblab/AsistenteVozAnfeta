using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Notion
{
    public sealed record NotionProjectViewInfo(
        string Id,
        string Name,
        string Url,
        string Type,
        string FilterJson,
        string ParentDatabaseId);

    public sealed class NotionProjectViewService
    {
        private sealed class PersistedIndex
        {
            public Dictionary<string, NotionProjectViewInfo>
                ViewsById
            { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);

            public DateTimeOffset UpdatedAt { get; set; }
        }

        private static readonly ConcurrentDictionary<string, NotionProjectViewInfo>
            ViewDetailCache =
                new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, NotionProjectViewInfo>
            ExactNameCache =
                new(StringComparer.OrdinalIgnoreCase);

        private static readonly SemaphoreSlim PersistentIndexGate =
            new(1, 1);

        private static bool _persistentIndexLoaded;

        // Notion aplica rate limit promedio. Trabajamos en lotes pequeños:
        // suficiente para acelerar, sin lanzar cientos de requests a la vez.
        private const int RetrieveBatchSize = 3;

        private const string PersistentIndexFileName =
            "notion_project_view_index_v1.json";

        private sealed record ChildDatabaseBlockInfo(
            string Id,
            string Title);

        private sealed class NotionProjectDatabaseAccessException
            : InvalidOperationException
        {
            public string DatabaseId { get; }

            public NotionProjectDatabaseAccessException(
                string databaseId,
                string message)
                : base(message)
            {
                DatabaseId = databaseId ?? string.Empty;
            }
        }

        /// <summary>
        /// Busca una vista por nombre EXACTO dentro de UN database/container
        /// concreto. Es la ruta usada por N Proyecto cuando ya conocemos el
        /// database_id real del selector de VS zPROYECTOS.
        /// </summary>
        public async Task<NotionProjectViewInfo?>
            FindExactViewByDatabaseIdAsync(
                string token,
                string databaseId,
                string exactName,
                CancellationToken cancellationToken = default,
                bool forceRefresh = false,
                IProgress<string>? progress = null)
        {
            token =
                (token ?? string.Empty).Trim();

            databaseId =
                (databaseId ?? string.Empty).Trim();

            exactName =
                NormalizeViewName(exactName);

            ValidateInputs(
                token,
                databaseId);

            if (string.IsNullOrWhiteSpace(exactName))
                return null;

            await EnsurePersistentIndexLoadedAsync(
                cancellationToken);

            // Antes de tocar la red, reutilizamos SOLO una vista cuyo parent
            // sea exactamente el database de VS zPROYECTOS. Esto permite
            // aprovechar el índice construido en búsquedas anteriores sin
            // aceptar una vista homónima de otra linked database.
            var scopedCached =
                TryFindScopedCachedExactView(
                    databaseId,
                    exactName);

            if (!forceRefresh && scopedCached != null)
            {
                progress?.Report(
                    $"Vista verificada en caché de VS zPROYECTOS ✅ · " +
                    scopedCached.Name);

                return WithDatabaseViewUrl(
                    scopedCached,
                    databaseId);
            }

            using var http =
                CreateClient(token);

            progress?.Report(
                $"VS zPROYECTOS · buscando {exactName} solo en sus vistas…");

            var database =
                new ChildDatabaseBlockInfo(
                    databaseId,
                    "VS zPROYECTOS");

            try
            {
                var selected =
                    await FindExactViewByDatabaseIdCoreAsync(
                        http,
                        database,
                        exactName,
                        cancellationToken,
                        forceRefresh,
                        progress);

                if (selected == null)
                    return null;

                return WithDatabaseViewUrl(
                    selected,
                    databaseId);
            }
            catch (NotionProjectDatabaseAccessException ex)
            {
                // Clic derecho fuerza relistado. Si Notion no permite listar
                // el database privado pero ya existe un detalle verificado de
                // esa misma vista/parent en el índice local, seguimos pudiendo
                // abrirlo sin caer en las ~968 linked views del data source.
                if (scopedCached != null)
                {
                    progress?.Report(
                        "Notion no permitió relistar VS zPROYECTOS; " +
                        "se usó la vista verificada guardada localmente ✅");

                    return WithDatabaseViewUrl(
                        scopedCached,
                        databaseId);
                }

                throw new InvalidOperationException(
                    "ANFETA llegó al database correcto de VS zPROYECTOS, " +
                    "pero la integración/token de Notion no tiene acceso a ese bloque. " +
                    "Comparte VS zPROYECTOS con la MISMA conexión de Notion que usa ANFETA " +
                    "(••• / Compartir → Conexiones o Agregar conexiones) y vuelve a pulsar N Proyecto. " +
                    "No se hará fallback a las ~968 linked Views.",
                    ex);
            }
        }

        private static NotionProjectViewInfo WithDatabaseViewUrl(
            NotionProjectViewInfo selected,
            string databaseId)
        {
            return new NotionProjectViewInfo(
                selected.Id,
                selected.Name,
                BuildDatabaseViewUrl(
                    databaseId,
                    selected.Id),
                selected.Type,
                selected.FilterJson,
                databaseId);
        }

        private static NotionProjectViewInfo?
            TryFindScopedCachedExactView(
                string databaseId,
                string exactName)
        {
            return ViewDetailCache.Values
                .FirstOrDefault(view =>
                    view != null &&
                    ProjectViewNameMatches(
                        view.Name,
                        exactName) &&
                    NotionIdsEqual(
                        view.ParentDatabaseId,
                        databaseId));
        }

        private static bool NotionIdsEqual(
            string left,
            string right)
        {
            static string Compact(string value) =>
                (value ?? string.Empty)
                    .Trim()
                    .Replace("-", string.Empty);

            return string.Equals(
                Compact(left),
                Compact(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDatabaseViewUrl(
            string databaseId,
            string viewId)
        {
            static string Compact(string value) =>
                (value ?? string.Empty)
                    .Trim()
                    .Replace("-", string.Empty);

            return
                $"https://app.notion.com/p/{Compact(databaseId)}" +
                $"?v={Compact(viewId)}&source=copy_link";
        }

        /// <summary>
        /// Busca una vista por nombre EXACTO, pero restringiendo primero la
        /// búsqueda a los child_database que realmente viven dentro de la
        /// página raíz indicada.
        ///
        /// Flujo:
        /// PageId raíz -> bloques hijos -> child_database -> List Views usando
        /// database_id -> recuperar únicamente esas vistas -> nombre exacto.
        ///
        /// A diferencia de data_source_id, database_id NO expande la búsqueda
        /// a linked views del resto del workspace.
        /// </summary>
        public async Task<NotionProjectViewInfo?>
            FindExactLinkedViewByNameInPageAsync(
                string token,
                string rootPageId,
                string exactName,
                CancellationToken cancellationToken = default,
                bool forceRefresh = false,
                IProgress<string>? progress = null)
        {
            token =
                (token ?? string.Empty).Trim();

            rootPageId =
                (rootPageId ?? string.Empty).Trim();

            exactName =
                NormalizeViewName(exactName);

            ValidateInputs(
                token,
                rootPageId);

            if (string.IsNullOrWhiteSpace(exactName))
                return null;

            await EnsurePersistentIndexLoadedAsync(
                cancellationToken);

            using var http =
                CreateClient(token);

            progress?.Report(
                "Resolviendo VS zPROYECTOS desde sus bloques hijos…");

            var childDatabases =
                await FindChildDatabaseBlocksAsync(
                    http,
                    rootPageId,
                    cancellationToken,
                    progress);

            if (childDatabases.Count == 0)
            {
                throw new InvalidOperationException(
                    "La página VS zPROYECTOS no expuso ningún bloque " +
                    "child_database accesible para la integración de Notion.");
            }

            progress?.Report(
                childDatabases.Count == 1
                    ? $"Database de VS zPROYECTOS resuelto ✅ · " +
                      $"{childDatabases[0].Title}"
                    : $"VS zPROYECTOS contiene {childDatabases.Count} " +
                      "database blocks accesibles; buscando el dominio solo dentro de ellos…");

            var successfulDatabaseQueries = 0;
            Exception? lastDatabaseError = null;

            foreach (var childDatabase in childDatabases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var selected =
                        await FindExactViewByDatabaseIdCoreAsync(
                            http,
                            childDatabase,
                            exactName,
                            cancellationToken,
                            forceRefresh,
                            progress);

                    successfulDatabaseQueries++;

                    if (selected != null)
                        return selected;
                }
                catch (InvalidOperationException ex)
                {
                    lastDatabaseError = ex;

                    // Puede haber más de un database embebido en la página.
                    // Si uno no permite List Views, se continúa con el resto
                    // sin ampliar jamás la búsqueda al data source global.
                    progress?.Report(
                        $"No se pudo consultar {childDatabase.Title}: " +
                        ex.Message);
                }
            }

            if (successfulDatabaseQueries == 0 &&
                lastDatabaseError != null)
            {
                throw new InvalidOperationException(
                    "Se encontró el database de VS zPROYECTOS, pero Notion " +
                    "no permitió listar sus vistas. " +
                    lastDatabaseError.Message,
                    lastDatabaseError);
            }

            progress?.Report(
                $"No se encontró una vista llamada exactamente {exactName} " +
                "dentro de VS zPROYECTOS.");

            return null;
        }

        private static async Task<IReadOnlyList<ChildDatabaseBlockInfo>>
            FindChildDatabaseBlocksAsync(
                HttpClient http,
                string rootBlockId,
                CancellationToken cancellationToken,
                IProgress<string>? progress)
        {
            const int MaxDepth = 6;
            const int MaxContainers = 250;

            var found =
                new List<ChildDatabaseBlockInfo>();

            var foundIds =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            var visited =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            var queue =
                new Queue<(string BlockId, int Depth)>();

            queue.Enqueue(
                (rootBlockId, 0));

            var inspectedContainers = 0;

            while (queue.Count > 0 &&
                   inspectedContainers < MaxContainers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current =
                    queue.Dequeue();

                if (!visited.Add(current.BlockId))
                    continue;

                inspectedContainers++;

                string? cursor = null;
                var hasMore = true;

                while (hasMore)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var endpoint =
                        "blocks/" +
                        Uri.EscapeDataString(current.BlockId) +
                        "/children?page_size=100";

                    if (!string.IsNullOrWhiteSpace(cursor))
                    {
                        endpoint +=
                            "&start_cursor=" +
                            Uri.EscapeDataString(cursor);
                    }

                    using var response =
                        await GetWithRetryAsync(
                            http,
                            endpoint,
                            cancellationToken);

                    var json =
                        await response.Content
                            .ReadAsStringAsync(
                                cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw CreateException(
                            "leer los bloques hijos de VS zPROYECTOS",
                            response,
                            json);
                    }

                    using var document =
                        JsonDocument.Parse(json);

                    var root =
                        document.RootElement;

                    if (root.TryGetProperty(
                            "results",
                            out var results) &&
                        results.ValueKind ==
                            JsonValueKind.Array)
                    {
                        foreach (var block in
                                 results.EnumerateArray())
                        {
                            var blockId =
                                GetString(
                                    block,
                                    "id");

                            var blockType =
                                GetString(
                                    block,
                                    "type");

                            if (string.IsNullOrWhiteSpace(blockId))
                                continue;

                            if (string.Equals(
                                    blockType,
                                    "child_database",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                var title =
                                    string.Empty;

                                if (block.TryGetProperty(
                                        "child_database",
                                        out var childDatabase) &&
                                    childDatabase.ValueKind ==
                                        JsonValueKind.Object)
                                {
                                    title =
                                        GetString(
                                            childDatabase,
                                            "title");
                                }

                                if (string.IsNullOrWhiteSpace(title))
                                    title = "Database sin título";

                                if (foundIds.Add(blockId))
                                {
                                    found.Add(
                                        new ChildDatabaseBlockInfo(
                                            blockId,
                                            title));

                                    progress?.Report(
                                        $"Database encontrado en VS zPROYECTOS · {title}");
                                }

                                // No se entra a las filas de un database.
                                continue;
                            }

                            var hasChildren =
                                block.TryGetProperty(
                                    "has_children",
                                    out var hasChildrenProperty) &&
                                hasChildrenProperty.ValueKind ==
                                    JsonValueKind.True;

                            // Recorremos contenedores visuales de la MISMA página
                            // (columnas, toggles, callouts, synced blocks, etc.),
                            // pero no abrimos subpáginas porque eso ampliaría el
                            // alcance definido para VS zPROYECTOS.
                            if (hasChildren &&
                                current.Depth < MaxDepth &&
                                !string.Equals(
                                    blockType,
                                    "child_page",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                queue.Enqueue(
                                    (blockId, current.Depth + 1));
                            }
                        }
                    }

                    hasMore =
                        root.TryGetProperty(
                            "has_more",
                            out var more) &&
                        more.ValueKind ==
                            JsonValueKind.True;

                    cursor =
                        root.TryGetProperty(
                            "next_cursor",
                            out var next) &&
                        next.ValueKind ==
                            JsonValueKind.String
                            ? next.GetString()
                            : null;

                    if (string.IsNullOrWhiteSpace(cursor))
                        hasMore = false;
                }
            }

            return found;
        }

        private async Task<NotionProjectViewInfo?>
            FindExactViewByDatabaseIdCoreAsync(
                HttpClient http,
                ChildDatabaseBlockInfo database,
                string exactName,
                CancellationToken cancellationToken,
                bool forceRefresh,
                IProgress<string>? progress)
        {
            var exactKey =
                $"database:{database.Id}|{exactName}";

            if (!forceRefresh &&
                ExactNameCache.TryGetValue(
                    exactKey,
                    out var exactCached))
            {
                progress?.Report(
                    $"Vista encontrada en caché de VS zPROYECTOS ✅ · " +
                    exactCached.Name);

                return exactCached;
            }

            string? cursor = null;
            var hasMore = true;
            var pageNumber = 0;
            var inspected = 0;
            var indexDirty = false;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                pageNumber++;

                var endpoint =
                    "views?database_id=" +
                    Uri.EscapeDataString(database.Id) +
                    "&page_size=100";

                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    endpoint +=
                        "&start_cursor=" +
                        Uri.EscapeDataString(cursor);
                }

                progress?.Report(
                    $"VS zPROYECTOS · página {pageNumber} · " +
                    $"{inspected} vistas revisadas…");

                using var response =
                    await GetWithRetryAsync(
                        http,
                        endpoint,
                        cancellationToken);

                var json =
                    await response.Content
                        .ReadAsStringAsync(
                            cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode ==
                        HttpStatusCode.NotFound)
                    {
                        throw new NotionProjectDatabaseAccessException(
                            database.Id,
                            $"Notion respondió 404 al listar las vistas de " +
                            $"{database.Title} ({database.Id}). El database " +
                            "no existe para ese token o no está compartido con la conexión.");
                    }

                    throw CreateException(
                        $"listar las vistas de {database.Title}",
                        response,
                        json);
                }

                using var document =
                    JsonDocument.Parse(json);

                var root =
                    document.RootElement;

                var viewIds =
                    new List<string>();

                if (root.TryGetProperty(
                        "results",
                        out var results) &&
                    results.ValueKind ==
                        JsonValueKind.Array)
                {
                    foreach (var result in
                             results.EnumerateArray())
                    {
                        var id =
                            GetString(
                                result,
                                "id");

                        if (string.IsNullOrWhiteSpace(id) &&
                            result.TryGetProperty(
                                "view",
                                out var nested) &&
                            nested.ValueKind ==
                                JsonValueKind.Object)
                        {
                            id =
                                GetString(
                                    nested,
                                    "id");
                        }

                        if (!string.IsNullOrWhiteSpace(id))
                            viewIds.Add(id);
                    }
                }

                var unknownIds =
                    new List<string>();

                foreach (var id in viewIds)
                {
                    if (!forceRefresh &&
                        ViewDetailCache.TryGetValue(
                            id,
                            out var cachedView))
                    {
                        inspected++;

                        if (ProjectViewNameMatches(
                                cachedView.Name,
                                exactName))
                        {
                            ExactNameCache[exactKey] =
                                cachedView;

                            progress?.Report(
                                $"Vista exacta encontrada en VS zPROYECTOS ✅ · " +
                                cachedView.Name);

                            return cachedView;
                        }
                    }
                    else
                    {
                        unknownIds.Add(id);
                    }
                }

                for (var index = 0;
                     index < unknownIds.Count;
                     index += RetrieveBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var batch =
                        unknownIds
                            .Skip(index)
                            .Take(RetrieveBatchSize)
                            .ToList();

                    var tasks =
                        batch.Select(
                            id =>
                                RetrieveViewSafeAsync(
                                    http,
                                    id,
                                    cancellationToken))
                            .ToArray();

                    var views =
                        await Task.WhenAll(tasks);

                    foreach (var view in views)
                    {
                        inspected++;

                        if (view == null)
                            continue;

                        ViewDetailCache[view.Id] =
                            view;

                        indexDirty = true;

                        if (ProjectViewNameMatches(
                                view.Name,
                                exactName))
                        {
                            ExactNameCache[exactKey] =
                                view;

                            if (indexDirty)
                            {
                                await SavePersistentIndexAsync(
                                    CancellationToken.None);
                            }

                            progress?.Report(
                                $"Vista exacta encontrada en VS zPROYECTOS ✅ · " +
                                view.Name);

                            return view;
                        }
                    }

                    progress?.Report(
                        $"VS zPROYECTOS · {inspected} vistas revisadas…");

                    await Task.Delay(
                        250,
                        cancellationToken);
                }

                if (indexDirty)
                {
                    await SavePersistentIndexAsync(
                        CancellationToken.None);

                    indexDirty = false;
                }

                hasMore =
                    root.TryGetProperty(
                        "has_more",
                        out var more) &&
                    more.ValueKind ==
                        JsonValueKind.True;

                cursor =
                    root.TryGetProperty(
                        "next_cursor",
                        out var next) &&
                    next.ValueKind ==
                        JsonValueKind.String
                        ? next.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(cursor))
                    hasMore = false;
            }

            return null;
        }

        /// <summary>
        /// Busca una linked View por nombre EXACTO usando data_source_id.
        ///
        /// - No necesita un database_id del linked database.
        /// - Procesa la paginación de List Views página por página.
        /// - Recupera detalles solo en lotes pequeños.
        /// - Se DETIENE apenas encuentra el name exacto.
        /// - Persiste los detalles recuperados para que otros dominios
        ///   aprovechen el trabajo ya realizado.
        /// </summary>
        public async Task<NotionProjectViewInfo?>
            FindExactLinkedViewByNameAsync(
                string token,
                string dataSourceId,
                string exactName,
                CancellationToken cancellationToken = default,
                bool forceRefresh = false,
                IProgress<string>? progress = null)
        {
            token =
                (token ?? string.Empty).Trim();

            dataSourceId =
                (dataSourceId ?? string.Empty).Trim();

            exactName =
                NormalizeViewName(exactName);

            ValidateInputs(
                token,
                dataSourceId);

            if (string.IsNullOrWhiteSpace(exactName))
                return null;

            await EnsurePersistentIndexLoadedAsync(
                cancellationToken);

            var exactKey =
                $"{dataSourceId}|{exactName}";

            if (!forceRefresh &&
                ExactNameCache.TryGetValue(
                    exactKey,
                    out var exactCached))
            {
                progress?.Report(
                    $"Vista encontrada en caché ✅ · {exactCached.Name}");

                return exactCached;
            }

            // Antes de tocar la red, revisar TODAS las vistas persistidas
            // de búsquedas anteriores.
            if (!forceRefresh)
            {
                var persistedMatch =
                    ViewDetailCache.Values
                        .FirstOrDefault(view =>
                            NamesEqual(
                                view.Name,
                                exactName));

                if (persistedMatch != null)
                {
                    ExactNameCache[exactKey] =
                        persistedMatch;

                    progress?.Report(
                        $"Vista encontrada en índice local ✅ · {persistedMatch.Name}");

                    return persistedMatch;
                }
            }

            using var http =
                CreateClient(token);

            string? cursor = null;
            var hasMore = true;
            var pageNumber = 0;
            var inspected = 0;
            var indexDirty = false;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                pageNumber++;

                var endpoint =
                    "views?data_source_id=" +
                    Uri.EscapeDataString(dataSourceId) +
                    "&page_size=100";

                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    endpoint +=
                        "&start_cursor=" +
                        Uri.EscapeDataString(cursor);
                }

                progress?.Report(
                    $"Buscando {exactName} · página {pageNumber} · " +
                    $"{inspected} vistas revisadas…");

                using var response =
                    await GetWithRetryAsync(
                        http,
                        endpoint,
                        cancellationToken);

                var json =
                    await response.Content
                        .ReadAsStringAsync(
                            cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateException(
                        "listar las linked views",
                        response,
                        json);
                }

                using var document =
                    JsonDocument.Parse(json);

                var root =
                    document.RootElement;

                var pageIds =
                    new List<string>();

                if (root.TryGetProperty(
                        "results",
                        out var results) &&
                    results.ValueKind ==
                        JsonValueKind.Array)
                {
                    foreach (var result in
                             results.EnumerateArray())
                    {
                        var id =
                            GetString(
                                result,
                                "id");

                        if (string.IsNullOrWhiteSpace(id) &&
                            result.TryGetProperty(
                                "view",
                                out var nested) &&
                            nested.ValueKind ==
                                JsonValueKind.Object)
                        {
                            id =
                                GetString(
                                    nested,
                                    "id");
                        }

                        if (!string.IsNullOrWhiteSpace(id))
                            pageIds.Add(id);
                    }
                }

                // 1) Revisar detalles ya conocidos, sin requests.
                var unknownIds =
                    new List<string>();

                foreach (var id in pageIds)
                {
                    if (!forceRefresh &&
                        ViewDetailCache.TryGetValue(
                            id,
                            out var cachedView))
                    {
                        inspected++;

                        if (NamesEqual(
                                cachedView.Name,
                                exactName))
                        {
                            ExactNameCache[exactKey] =
                                cachedView;

                            progress?.Report(
                                $"Vista exacta encontrada ✅ · {cachedView.Name}");

                            return cachedView;
                        }
                    }
                    else
                    {
                        unknownIds.Add(id);
                    }
                }

                // 2) Recuperar desconocidas en lotes de 3 y EARLY STOP.
                for (var index = 0;
                     index < unknownIds.Count;
                     index += RetrieveBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var batch =
                        unknownIds
                            .Skip(index)
                            .Take(RetrieveBatchSize)
                            .ToList();

                    var tasks =
                        batch.Select(
                            id =>
                                RetrieveViewSafeAsync(
                                    http,
                                    id,
                                    cancellationToken))
                        .ToArray();

                    var views =
                        await Task.WhenAll(tasks);

                    foreach (var view in views)
                    {
                        inspected++;

                        if (view == null)
                            continue;

                        ViewDetailCache[view.Id] =
                            view;

                        indexDirty = true;

                        if (NamesEqual(
                                view.Name,
                                exactName))
                        {
                            ExactNameCache[exactKey] =
                                view;

                            if (indexDirty)
                            {
                                await SavePersistentIndexAsync(
                                    CancellationToken.None);
                            }

                            progress?.Report(
                                $"Vista exacta encontrada ✅ · {view.Name}");

                            return view;
                        }
                    }

                    progress?.Report(
                        $"Buscando {exactName} · {inspected} vistas revisadas…");

                    // Pausa pequeña para convivir mejor con rate limit.
                    await Task.Delay(
                        250,
                        cancellationToken);
                }

                if (indexDirty)
                {
                    await SavePersistentIndexAsync(
                        CancellationToken.None);

                    indexDirty = false;
                }

                hasMore =
                    root.TryGetProperty(
                        "has_more",
                        out var more) &&
                    more.ValueKind ==
                        JsonValueKind.True;

                cursor =
                    root.TryGetProperty(
                        "next_cursor",
                        out var next) &&
                    next.ValueKind ==
                        JsonValueKind.String
                        ? next.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(cursor))
                    hasMore = false;
            }

            progress?.Report(
                $"No se encontró una vista llamada exactamente {exactName}.");

            return null;
        }

        private static async Task EnsurePersistentIndexLoadedAsync(
            CancellationToken cancellationToken)
        {
            if (_persistentIndexLoaded)
                return;

            await PersistentIndexGate.WaitAsync(
                cancellationToken);

            try
            {
                if (_persistentIndexLoaded)
                    return;

                try
                {
                    var folder =
                        ApplicationData.Current.LocalFolder;

                    var item =
                        await folder.TryGetItemAsync(
                            PersistentIndexFileName);

                    if (item is StorageFile file)
                    {
                        var json =
                            await FileIO.ReadTextAsync(
                                file);

                        var persisted =
                            JsonSerializer.Deserialize<PersistedIndex>(
                                json);

                        if (persisted?.ViewsById != null)
                        {
                            foreach (var pair in
                                     persisted.ViewsById)
                            {
                                if (pair.Value != null &&
                                    !string.IsNullOrWhiteSpace(
                                        pair.Value.Id))
                                {
                                    ViewDetailCache[pair.Key] =
                                        pair.Value;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Índice opcional. Si está corrupto, se reconstruye.
                }

                _persistentIndexLoaded = true;
            }
            finally
            {
                PersistentIndexGate.Release();
            }
        }

        private static async Task SavePersistentIndexAsync(
            CancellationToken cancellationToken)
        {
            await PersistentIndexGate.WaitAsync(
                cancellationToken);

            try
            {
                try
                {
                    var folder =
                        ApplicationData.Current.LocalFolder;

                    var file =
                        await folder.CreateFileAsync(
                            PersistentIndexFileName,
                            CreationCollisionOption.ReplaceExisting);

                    var persisted =
                        new PersistedIndex
                        {
                            ViewsById =
                                ViewDetailCache
                                    .ToDictionary(
                                        pair => pair.Key,
                                        pair => pair.Value,
                                        StringComparer.OrdinalIgnoreCase),
                            UpdatedAt =
                                DateTimeOffset.Now
                        };

                    var json =
                        JsonSerializer.Serialize(
                            persisted);

                    await FileIO.WriteTextAsync(
                        file,
                        json);
                }
                catch
                {
                    // No convertir un fallo de cache en fallo de Notion.
                }
            }
            finally
            {
                PersistentIndexGate.Release();
            }
        }

        private static async Task<NotionProjectViewInfo?>
            RetrieveViewSafeAsync(
                HttpClient http,
                string viewId,
                CancellationToken cancellationToken)
        {
            if (ViewDetailCache.TryGetValue(
                    viewId,
                    out var cached))
            {
                return cached;
            }

            try
            {
                using var itemCts =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(
                            cancellationToken);

                itemCts.CancelAfter(
                    TimeSpan.FromSeconds(18));

                using var response =
                    await GetWithRetryAsync(
                        http,
                        "views/" +
                        Uri.EscapeDataString(viewId),
                        itemCts.Token);

                var json =
                    await response.Content
                        .ReadAsStringAsync(
                            itemCts.Token);

                if (!response.IsSuccessStatusCode)
                    return null;

                using var document =
                    JsonDocument.Parse(json);

                return ParseFullView(
                    document.RootElement);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static NotionProjectViewInfo?
            ParseFullView(
                JsonElement view)
        {
            if (view.ValueKind !=
                JsonValueKind.Object)
            {
                return null;
            }

            var id =
                GetString(
                    view,
                    "id");

            if (string.IsNullOrWhiteSpace(id))
                return null;

            var name =
                GetString(
                    view,
                    "name");

            var url =
                GetString(
                    view,
                    "url");

            var type =
                GetString(
                    view,
                    "type");

            var filterJson =
                view.TryGetProperty(
                    "filter",
                    out var filter) &&
                filter.ValueKind !=
                    JsonValueKind.Null &&
                filter.ValueKind !=
                    JsonValueKind.Undefined
                    ? filter.GetRawText()
                    : string.Empty;

            var parentDatabaseId =
                string.Empty;

            if (view.TryGetProperty(
                    "parent",
                    out var parent) &&
                parent.ValueKind ==
                    JsonValueKind.Object)
            {
                parentDatabaseId =
                    GetString(
                        parent,
                        "database_id");
            }

            return new NotionProjectViewInfo(
                id,
                name,
                url,
                type,
                filterJson,
                parentDatabaseId);
        }

        // VS zPROYECTOS usa en varias vistas la nomenclatura
        // "tartamuda": se repite la primera letra del dominio.
        // Ejemplo real:
        //   dominio      = dmiesculturacorporal.com
        //   nombre vista = ddmiesculturacorporal.com
        //
        // La asociacion sigue siendo estricta: solo se aceptan
        // 1) el dominio exacto, o
        // 2) exactamente una repeticion de su primera letra.
        // No se usa Contains ni coincidencia parcial.
        private static bool ProjectViewNameMatches(
            string viewName,
            string domain)
        {
            var normalizedView =
                NormalizeViewName(viewName);

            var normalizedDomain =
                NormalizeViewName(domain);

            if (string.IsNullOrWhiteSpace(normalizedView) ||
                string.IsNullOrWhiteSpace(normalizedDomain))
            {
                return false;
            }

            if (string.Equals(
                    normalizedView,
                    normalizedDomain,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var stutteredDomain =
                normalizedDomain[0] + normalizedDomain;

            return string.Equals(
                normalizedView,
                stutteredDomain,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool NamesEqual(
            string left,
            string right)
        {
            return string.Equals(
                NormalizeViewName(left),
                NormalizeViewName(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeViewName(
            string value)
        {
            var normalized =
                (value ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

            if (normalized.StartsWith(
                    "www.",
                    StringComparison.OrdinalIgnoreCase))
            {
                normalized =
                    normalized.Substring(4);
            }

            return normalized.TrimEnd('.');
        }

        private static void ValidateInputs(
            string token,
            string id)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(
                    "No se encontró el data_source_id requerido.");
            }
        }

        private static HttpClient CreateClient(
            string token)
        {
            var http =
                new HttpClient
                {
                    BaseAddress =
                        new Uri(
                            "https://api.notion.com/v1/"),
                    Timeout =
                        TimeSpan.FromSeconds(30)
                };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    token.Trim());

            http.DefaultRequestHeaders.Add(
                "Notion-Version",
                "2026-03-11");

            return http;
        }

        private static string GetString(
            JsonElement element,
            string propertyName)
        {
            return
                element.TryGetProperty(
                    propertyName,
                    out var property) &&
                property.ValueKind ==
                    JsonValueKind.String
                    ? property.GetString() ??
                      string.Empty
                    : string.Empty;
        }

        private static async Task<HttpResponseMessage>
            GetWithRetryAsync(
                HttpClient http,
                string endpoint,
                CancellationToken cancellationToken)
        {
            for (var attempt = 0;
                 attempt < 5;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response =
                    await http.GetAsync(
                        endpoint,
                        cancellationToken);

                if (response.IsSuccessStatusCode)
                    return response;

                var retryable =
                    response.StatusCode ==
                        HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500;

                if (!retryable ||
                    attempt >= 4)
                {
                    return response;
                }

                var delay =
                    response.Headers.RetryAfter?.Delta ??
                    TimeSpan.FromMilliseconds(
                        850 * (attempt + 1));

                response.Dispose();

                await Task.Delay(
                    delay,
                    cancellationToken);
            }

            throw new InvalidOperationException(
                "No se pudo completar la consulta de vistas de Notion.");
        }

        private static Exception CreateException(
            string operation,
            HttpResponseMessage response,
            string responseBody)
        {
            var detail =
                (responseBody ?? string.Empty)
                    .Trim();

            if (detail.Length > 600)
            {
                detail =
                    detail.Substring(
                        0,
                        600);
            }

            return new InvalidOperationException(
                $"No se pudo {operation}. " +
                $"Notion respondió " +
                $"{(int)response.StatusCode} " +
                $"{response.ReasonPhrase}. " +
                detail);
        }
    }
}
