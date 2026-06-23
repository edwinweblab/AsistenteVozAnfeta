using Anfeta.UI.Models.Weblab;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Notion
{
    public static class NotionIndexBuilder
    {
        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";

        public static async Task<List<SearchResultRow>> BuildAsync(
        string token,
        string dataSourceId,
        CancellationToken ct = default,
        int? maxItems = null,
        DateTimeOffset? lastEditedAfterUtc = null) 
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token de Notion vacío.");

            if (string.IsNullOrWhiteSpace(dataSourceId))
                throw new ArgumentException("Data Source ID de Notion vacío.");

            var results = new List<SearchResultRow>();
            string? nextCursor = null;
            bool hasMore;

            using var http = new HttpClient
            {
                BaseAddress = new Uri(NotionBaseUrl),
                Timeout = TimeSpan.FromSeconds(90)
            };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Trim());

            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "Notion-Version",
                NotionVersion);

            do
            {
                ct.ThrowIfCancellationRequested();

                var remaining = maxItems.HasValue
                    ? Math.Max(0, maxItems.Value - results.Count)
                    : 100;

                if (maxItems.HasValue && remaining <= 0)
                    break;

                var pageSize = maxItems.HasValue
                    ? Math.Min(100, remaining)
                    : 100;

                var payload = new Dictionary<string, object?>
                {
                    ["page_size"] = pageSize
                };

                if (lastEditedAfterUtc.HasValue)
                {
                    var afterUtc = lastEditedAfterUtc.Value
                        .ToUniversalTime()
                        .AddSeconds(-2) // margen pequeño para no perder cambios cercanos
                        .ToString("O");

                    payload["filter"] = new Dictionary<string, object?>
                    {
                        ["timestamp"] = "last_edited_time",
                        ["last_edited_time"] = new Dictionary<string, object?>
                        {
                            ["after"] = afterUtc
                        }
                    };

                    payload["sorts"] = new[]
                    {
        new Dictionary<string, object?>
        {
            ["timestamp"] = "last_edited_time",
            ["direction"] = "ascending"
        }
    };
                }

                if (!string.IsNullOrWhiteSpace(nextCursor))
                    payload["start_cursor"] = nextCursor;

                var jsonBody = JsonSerializer.Serialize(payload);

                using var content = new StringContent(
                    jsonBody,
                    Encoding.UTF8,
                    "application/json");

                using var response = await http.PostAsync(
                    $"data_sources/{dataSourceId.Trim()}/query",
                    content,
                    ct);

                var json = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Error Notion {(int)response.StatusCode}: {json}");

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("results", out var pages) &&
                    pages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in pages.EnumerateArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        results.Add(MapPageToSearchRow(page));

                        if (maxItems.HasValue && results.Count >= maxItems.Value)
                            break;
                    }
                }

                hasMore =
                    root.TryGetProperty("has_more", out var hasMoreEl) &&
                    hasMoreEl.ValueKind == JsonValueKind.True;

                nextCursor =
                    root.TryGetProperty("next_cursor", out var cursorEl) &&
                    cursorEl.ValueKind == JsonValueKind.String
                        ? cursorEl.GetString()
                        : null;

            } while (hasMore && !string.IsNullOrWhiteSpace(nextCursor));

            return results;
        }
        public static Task<List<SearchResultRow>> BuildChangedSinceAsync(
    string token,
    string dataSourceId,
    DateTimeOffset lastSyncUtc,
    CancellationToken ct = default,
    int? maxItems = null)
        {
            return BuildAsync(
                token,
                dataSourceId,
                ct,
                maxItems,
                lastEditedAfterUtc: lastSyncUtc);
        }

        public static async Task<bool> HasChangesSinceAsync(
            string token,
            string dataSourceId,
            DateTimeOffset lastSyncUtc,
            CancellationToken ct = default)
        {
            var changes = await BuildChangedSinceAsync(
                token,
                dataSourceId,
                lastSyncUtc,
                ct,
                maxItems: 1);

            return changes.Count > 0;
        }
        private static SearchResultRow MapPageToSearchRow(JsonElement page)
        {
            var pageId = GetString(page, "id");
            var pageUrl = GetString(page, "url");
            var lastEdited = GetString(page, "last_edited_time");

            JsonElement props = default;
            var hasProps =
                page.TryGetProperty("properties", out props) &&
                props.ValueKind == JsonValueKind.Object;

            string title = "";
            string description = "";

            if (hasProps)
            {
                title = GetPropText(props, "TITULO PRiNCIPAL");

                if (string.IsNullOrWhiteSpace(title))
                    title = GetPropText(props, "Nombre Sync");

                if (string.IsNullOrWhiteSpace(title))
                    title = GetPropText(props, "Dominio");

                if (string.IsNullOrWhiteSpace(title))
                    title = GetPropText(props, "ID");

                description = GetPropText(props, "Descripción / Notas");
            }

            if (string.IsNullOrWhiteSpace(title))
                title = $"Página Notion {ShortId(pageId)}";

            var searchParts = new[]
            {
                title,
                description,
                hasProps ? GetPropText(props, "Nombre Sync") : "",
                hasProps ? GetPropText(props, "Texto proyecto") : "",
                hasProps ? GetPropText(props, "Dominio") : "",
                hasProps ? GetPropText(props, "Estado entregable") : "",
                hasProps ? GetPropText(props, "Estado operativo") : "",
                hasProps ? GetPropText(props, "Estado de Cobro") : "",
                hasProps ? GetPropText(props, "Tipo de Proyecto") : "",
                hasProps ? GetPropText(props, "TAGS Keywords (buscador tartamudo)") : "",
                hasProps ? GetPropText(props, "tags etiquetas") : "",
                hasProps ? GetPropText(props, "Comentario") : "",
                hasProps ? GetPropText(props, "Drive File ID") : "",
                hasProps ? GetPropText(props, "Drive_URL") : "",
                hasProps ? GetPropText(props, "Formula Proyecto/Revisión") : "",
                hasProps ? GetPropText(props, "Actividad Programada") : "",
                hasProps ? GetPropText(props, "Assignee/ Ejecutor Principal") : "",
                pageId,
                pageUrl
            };

            return new SearchResultRow
            {
                NodeId = pageId,
                ExternalId = pageId,
                ExternalUrl = pageUrl,
                Name = title.Trim(),
                Target = pageUrl,
                Type = "NOTION_PAGE",
                Size = 0,
                ServerModified = FormatDate(lastEdited),
                Source = SearchSource.Notion,
                Description = description,
                SearchText = string.Join(" ", searchParts.Where(x => !string.IsNullOrWhiteSpace(x)))
            };
        }

        private static string GetPropText(JsonElement props, string propName)
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (string.Equals(prop.Name, propName, StringComparison.Ordinal))
                    return ExtractPropertyText(prop.Value);
            }

            return "";
        }

        private static string ExtractPropertyText(JsonElement prop)
        {
            var type = GetString(prop, "type");

            return type switch
            {
                "title" => JoinPlainText(prop, "title"),
                "rich_text" => JoinPlainText(prop, "rich_text"),
                "select" => ExtractNamedObject(prop, "select"),
                "status" => ExtractNamedObject(prop, "status"),
                "multi_select" => ExtractMultiSelect(prop),
                "url" => GetString(prop, "url"),
                "email" => GetString(prop, "email"),
                "phone_number" => GetString(prop, "phone_number"),
                "date" => ExtractDate(prop),
                "number" => ExtractNumber(prop, "number"),
                "checkbox" => ExtractCheckbox(prop),
                "formula" => ExtractFormula(prop),
                "unique_id" => ExtractUniqueId(prop),
                "people" => ExtractPeople(prop),
                "files" => ExtractFiles(prop),
                "last_edited_time" => FormatDate(GetString(prop, "last_edited_time")),
                "created_time" => FormatDate(GetString(prop, "created_time")),
                _ => ""
            };
        }

        private static string JoinPlainText(JsonElement prop, string arrayName)
        {
            if (!prop.TryGetProperty(arrayName, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return "";

            var parts = new List<string>();

            foreach (var item in arr.EnumerateArray())
            {
                var text = GetString(item, "plain_text");
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }

            return string.Join("", parts).Trim();
        }

        private static string ExtractNamedObject(JsonElement prop, string objectName)
        {
            if (!prop.TryGetProperty(objectName, out var obj) ||
                obj.ValueKind != JsonValueKind.Object)
                return "";

            return GetString(obj, "name");
        }

        private static string ExtractMultiSelect(JsonElement prop)
        {
            if (!prop.TryGetProperty("multi_select", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return "";

            var names = new List<string>();

            foreach (var item in arr.EnumerateArray())
            {
                var name = GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }

            return string.Join(" ", names);
        }

        private static string ExtractDate(JsonElement prop)
        {
            if (!prop.TryGetProperty("date", out var date) ||
                date.ValueKind != JsonValueKind.Object)
                return "";

            var start = GetString(date, "start");
            var end = GetString(date, "end");

            if (string.IsNullOrWhiteSpace(end))
                return start;

            return $"{start} - {end}";
        }

        private static string ExtractNumber(JsonElement prop, string name)
        {
            if (!prop.TryGetProperty(name, out var number) ||
                number.ValueKind != JsonValueKind.Number)
                return "";

            return number.GetRawText();
        }

        private static string ExtractCheckbox(JsonElement prop)
        {
            if (!prop.TryGetProperty("checkbox", out var check))
                return "";

            return check.ValueKind == JsonValueKind.True ? "Sí" : "No";
        }

        private static string ExtractFormula(JsonElement prop)
        {
            if (!prop.TryGetProperty("formula", out var formula) ||
                formula.ValueKind != JsonValueKind.Object)
                return "";

            var formulaType = GetString(formula, "type");

            return formulaType switch
            {
                "string" => GetString(formula, "string"),
                "number" => ExtractNumber(formula, "number"),
                "boolean" => GetBoolString(formula, "boolean"),
                "date" => ExtractDate(formula),
                _ => ""
            };
        }

        private static string ExtractUniqueId(JsonElement prop)
        {
            if (!prop.TryGetProperty("unique_id", out var unique) ||
                unique.ValueKind != JsonValueKind.Object)
                return "";

            var prefix = GetString(unique, "prefix");

            string number = "";
            if (unique.TryGetProperty("number", out var num) &&
                num.ValueKind == JsonValueKind.Number)
                number = num.GetRawText();

            return $"{prefix}{number}";
        }

        private static string ExtractPeople(JsonElement prop)
        {
            if (!prop.TryGetProperty("people", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return "";

            var people = new List<string>();

            foreach (var item in arr.EnumerateArray())
            {
                var name = GetString(item, "name");

                if (string.IsNullOrWhiteSpace(name) &&
                    item.TryGetProperty("person", out var person) &&
                    person.ValueKind == JsonValueKind.Object)
                {
                    name = GetString(person, "email");
                }

                if (!string.IsNullOrWhiteSpace(name))
                    people.Add(name);
            }

            return string.Join(" ", people);
        }

        private static string ExtractFiles(JsonElement prop)
        {
            if (!prop.TryGetProperty("files", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return "";

            var files = new List<string>();

            foreach (var item in arr.EnumerateArray())
            {
                var name = GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    files.Add(name);
            }

            return string.Join(" ", files);
        }

        private static string GetString(JsonElement obj, string propertyName)
        {
            if (!obj.TryGetProperty(propertyName, out var value))
                return "";

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";

            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();

            return "";
        }

        private static string GetBoolString(JsonElement obj, string propertyName)
        {
            if (!obj.TryGetProperty(propertyName, out var value))
                return "";

            return value.ValueKind == JsonValueKind.True ? "Sí" : "No";
        }

        private static string FormatDate(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso))
                return "";

            if (DateTimeOffset.TryParse(iso, out var dto))
                return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

            return iso;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return "";

            return id.Length <= 8 ? id : id[..8];
        }
    }
}