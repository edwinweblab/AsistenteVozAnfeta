using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Search
{
    public sealed class DropboxNotionFilesApi
    {
        private readonly HttpClient _http;

        private const string BASE_URL = "https://wlserver-production.up.railway.app";
        private const string ROOT = "/api/dropbox/notion-files";

        public DropboxNotionFilesApi(HttpClient httpClient)
        {
            _http = httpClient;
        }

        // =========================
        // SEARCH
        // =========================
        public async Task<List<DropboxNode>> SearchAsync(string query, bool includeNotion, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<DropboxNode>();

            var endpoint = includeNotion ? "/buscar" : "/search";
            var url = $"{BASE_URL}{ROOT}{endpoint}?q={Uri.EscapeDataString(query)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var json = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)res.StatusCode}: {json}");

            return ParseNodes(json);
        }

        // =========================
        // ENSURE LINK (SOBRECARGAS)
        // - para que puedas llamar: EnsureLinkAsync(id, ct)
        // - o EnsureLinkAsync(id, pathLower, ct)
        // =========================
        public Task<string> EnsureLinkAsync(string id, CancellationToken ct)
            => EnsureLinkAsync(id, null, ct);

        public async Task<string> EnsureLinkAsync(string id, string? pathLower, CancellationToken ct)
        {
            // si no hay ni id ni path, no hay nada que hacer
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(pathLower))
                return "";

            // Query param extra por compatibilidad (algunos backends leen req.query)
            var url = $"{BASE_URL}{ROOT}/ensure-link";
            if (!string.IsNullOrWhiteSpace(id))
                url += $"?id={Uri.EscapeDataString(id)}";

            // Body con varios nombres por si el backend usa otro campo
            var payload = new
            {
                id = string.IsNullOrWhiteSpace(id) ? null : id,
                nodeId = string.IsNullOrWhiteSpace(id) ? null : id,   // fallback por si esperan nodeId
                path = string.IsNullOrWhiteSpace(pathLower) ? null : pathLower,
                pathLower = string.IsNullOrWhiteSpace(pathLower) ? null : pathLower
            };

            var bodyJson = JsonSerializer.Serialize(payload);
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var res = await _http.PostAsync(url, content, ct);
            var json = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)res.StatusCode}: {json}");

            // respuestas posibles:
            // { success:true, data:{ sharedLink:"..." } }
            // { success:true, data:{ sharedLink:{ url:"...", rawUrl:"..." } } }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGet(root, "data", out var data) || data.ValueKind != JsonValueKind.Object)
                return "";

            if (!TryGet(data, "sharedLink", out var sl))
                return "";

            if (sl.ValueKind == JsonValueKind.String)
                return sl.GetString() ?? "";

            if (sl.ValueKind == JsonValueKind.Object)
            {
                var u = GetString(sl, "url") ?? GetString(sl, "rawUrl");
                return u ?? "";
            }

            return "";
        }

        // =========================
        // PARSE (SEARCH RESULTS)
        // =========================
        private static List<DropboxNode> ParseNodes(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement dataEl;

            // { success:true, data:[...] }  o  { ok:true, data:[...] }  o  { data:[...] }  o  [ ... ]
            if (root.ValueKind == JsonValueKind.Array)
                dataEl = root;
            else if (TryGet(root, "data", out var tmp))
                dataEl = tmp;
            else
                dataEl = root;

            // Por si viene { data: { items: [...] } }
            if (dataEl.ValueKind == JsonValueKind.Object &&
                TryGet(dataEl, "items", out var itemsEl) &&
                itemsEl.ValueKind == JsonValueKind.Array)
            {
                dataEl = itemsEl;
            }

            var list = new List<DropboxNode>();

            if (dataEl.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in dataEl.EnumerateArray())
            {
                // tu API trae _id (mongo)
                var id = GetString(item, "_id")
                         ?? GetString(item, "id")
                         ?? GetString(item, "nodeId")
                         ?? "";

                var name = GetString(item, "name")
                           ?? GetString(item, "filename")
                           ?? GetString(item, "title")
                           ?? "";

                var pathLower = GetString(item, "pathLower")
                                ?? GetString(item, "path_lower")
                                ?? "";

                var searchablePath = GetString(item, "searchablePath")
                                     ?? GetString(item, "path")
                                     ?? GetString(item, "path_display")
                                     ?? "";

                // usa searchablePath si existe, si no pathLower
                var path = !string.IsNullOrWhiteSpace(searchablePath) ? searchablePath : pathLower;

                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                {
                    var parts = path.Replace("\\", "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
                    name = parts.Length > 0 ? parts[^1] : path;
                }

                var typeRaw = GetString(item, "type") ?? GetString(item, ".tag") ?? "file";
                var type = (typeRaw ?? "file").ToLowerInvariant();
                var isFolder = type == "folder";

                long size = 0;
                if (TryGet(item, "size", out var szEl) && szEl.ValueKind == JsonValueKind.Number)
                    size = szEl.GetInt64();

                // mimeType puede venir null => queda ""
                var mime = GetString(item, "mimeType") ?? "";

                var modified = GetString(item, "serverModified")
                               ?? GetString(item, "clientModified")
                               ?? "";

                // sharedLink: { sharedLink: { url:"...", rawUrl:"..." } }
                string sharedUrl = "";
                if (TryGet(item, "sharedLink", out var slEl) && slEl.ValueKind == JsonValueKind.Object)
                {
                    var u = GetString(slEl, "url") ?? GetString(slEl, "rawUrl");
                    if (!string.IsNullOrWhiteSpace(u)) sharedUrl = u;
                }

                if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(name))
                    continue;

                list.Add(new DropboxNode(
                    Id: id,
                    Name: name,
                    Path: path,
                    PathLower: pathLower,
                    IsFolder: isFolder,
                    Type: type,
                    Size: size,
                    MimeType: mime,
                    ServerModified: modified,
                    SharedLink: sharedUrl
                ));
            }

            return list;
        }

        private static bool TryGet(JsonElement obj, string prop, out JsonElement value)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out value))
                return true;

            value = default;
            return false;
        }

        private static string? GetString(JsonElement obj, string prop)
        {
            if (TryGet(obj, prop, out var el))
            {
                if (el.ValueKind == JsonValueKind.String) return el.GetString();
                if (el.ValueKind == JsonValueKind.Number) return el.ToString();
                if (el.ValueKind == JsonValueKind.Null) return null;
            }
            return null;
        }
    }

    public sealed record DropboxNode(
        string Id,
        string Name,
        string Path,
        string PathLower,
        bool IsFolder,
        string Type,
        long Size,
        string MimeType,
        string ServerModified,
        string SharedLink
    );
}
