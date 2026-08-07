using Anfeta.UI.Models.Weblab;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Notion
{
    public static class NotionIndexBuilder
    {
        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";
        private const int HttpTimeoutSeconds = 30;

        // Un solo recorrido del índice puede estar activo a la vez, incluso
        // cuando existen varias pestañas de SearchView.
        private static readonly SemaphoreSlim IndexBuildGate =
            new(1, 1);


        private static readonly Regex CompletedReminderNotificationPattern =
            new(
                @"^\s*(?:\[Revisiones\]\s*)?" +
                @"\d{4}-\d{2}-\d{2}[ T]\d{2}[:\-]\d{2}\s+" +
                @"(?:jjohn|kkarl|iisai|iisaia|eedua|aacal|aandr|eemma|bbria|ggena|nneft|__all__)\b" +
                @".*(?:^|\s)\[TERMINADO\](?:\s|$)",
                RegexOptions.Compiled |
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant |
                RegexOptions.Singleline);

        /// <summary>
        /// Identifica únicamente páginas de notificación de Revisiones que
        /// siguen el formato fecha + destinatario y contienen [TERMINADO].
        /// No afecta actividades normales cuyo título use la palabra terminado.
        /// </summary>
        public static bool IsCompletedReminderNotification(
            SearchResultRow? row)
        {
            if (row == null ||
                row.Source != SearchSource.Notion ||
                !string.Equals(
                    row.ExternalSourceName,
                    "Revisiones",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var title =
                (row.Name ?? string.Empty).Trim();

            return CompletedReminderNotificationPattern.IsMatch(
                title);
        }

        private static bool IsArchivedOrInTrash(
            JsonElement page)
        {
            var archived =
                page.TryGetProperty(
                    "archived",
                    out var archivedValue) &&
                archivedValue.ValueKind ==
                    JsonValueKind.True;

            var inTrash =
                page.TryGetProperty(
                    "in_trash",
                    out var trashValue) &&
                trashValue.ValueKind ==
                    JsonValueKind.True;

            return archived || inTrash;
        }

        public static async Task<List<SearchResultRow>> BuildAsync(
            string token,
            string dataSourceId,
            CancellationToken ct = default,
            int? maxItems = null,
            DateTimeOffset? lastEditedAfterUtc = null,
            string sourceName = "Notion")
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token de Notion vacío.");

            if (string.IsNullOrWhiteSpace(dataSourceId))
                throw new ArgumentException("Data Source ID de Notion vacío.");

            await IndexBuildGate.WaitAsync(ct);

            try
            {
                using var fullSyncLease =
                    await NotionRequestCoordinator.EnterFullSyncAsync(ct);

                var results = new List<SearchResultRow>();
                string? nextCursor = null;
                bool hasMore;

                using var http = new HttpClient
                {
                    BaseAddress = new Uri(NotionBaseUrl),
                    Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds)
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

                    using var response =
                        await NotionRequestCoordinator.SendAsync(
                            http,
                            () => new HttpRequestMessage(
                                HttpMethod.Post,
                                $"data_sources/{dataSourceId.Trim()}/query")
                            {
                                Content = new StringContent(
                                    jsonBody,
                                    Encoding.UTF8,
                                    "application/json")
                            },
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

                            if (IsArchivedOrInTrash(page))
                                continue;

                            results.Add(
                                MapPageToSearchRow(
                                    page,
                                    sourceName));

                            if (maxItems.HasValue &&
                                results.Count >= maxItems.Value)
                            {
                                break;
                            }
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
            finally
            {
                IndexBuildGate.Release();
            }
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
        private static SearchResultRow MapPageToSearchRow(JsonElement page, string sourceName)
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
            string scheduledDate = "";
            string projectUpdateStatus = "";

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

                var normalizedSourceName =
                    NormalizePropertyName(sourceName);

                var isCobrarPagarSource =
                    normalizedSourceName.Contains(
                        "cobrar",
                        StringComparison.OrdinalIgnoreCase) &&
                    normalizedSourceName.Contains(
                        "pagar",
                        StringComparison.OrdinalIgnoreCase);

                // BD COBRAR Y PAGAR usa su propia fecha calendarizable.
                scheduledDate =
                    isCobrarPagarSource
                        ? GetPropTextByFlexibleAliases(
                            props,
                            "Due Fecha Recordatorio",
                            "Due Fecha recordatorio",
                            "Fecha Recordatorio",
                            "Fecha de Recordatorio")
                        : GetPropTextByFlexibleAliases(
                            props,
                            "Fecha POR Hacer (Trabajando)",
                            "Fecha por hacer",
                            "Fecha POR Hacer",
                            "Fecha de Inicio");

                projectUpdateStatus =
                    GetProjectUpdateStatus(props, sourceName);
            }

            if (string.IsNullOrWhiteSpace(title))
                title = $"Página Notion {ShortId(pageId)}";

            var searchParts = new[]
            {
                sourceName,
                title,
                description,
                projectUpdateStatus,
                scheduledDate
            };

            return new SearchResultRow
            {
                NodeId = pageId,
                ExternalId = pageId,
                ExternalUrl = pageUrl,
                ExternalSourceName = sourceName,
                Name = FormatDisplayName(title, sourceName),
                Target = pageUrl,
                Type = "NOTION_PAGE",
                Size = 0,
                ServerModified = FormatDate(lastEdited),
                Source = SearchSource.Notion,
                Description = description,
                ProjectUpdateStatus = projectUpdateStatus,
                ScheduledDate = scheduledDate,
                SearchText = string.Join(" ", searchParts.Where(x => !string.IsNullOrWhiteSpace(x)))
            };
        }

        private static string FormatDisplayName(string title, string sourceName)
        {
            var cleanTitle = string.IsNullOrWhiteSpace(title)
                ? "Página Notion"
                : title.Trim();

            if (string.IsNullOrWhiteSpace(sourceName))
                return cleanTitle;

            if (cleanTitle.StartsWith($"[{sourceName}]", StringComparison.OrdinalIgnoreCase))
                return cleanTitle;

            return $"[{sourceName}] {cleanTitle}";
        }
        private static string GetProjectUpdateStatus(
            JsonElement props,
            string sourceName)
        {
            var normalizedSource =
                NormalizePropertyName(
                    sourceName);

            var preferredNames =
                normalizedSource.Contains("cobrar") ||
                normalizedSource.Contains("pagar")
                    ? new[]
                    {
                        "Estado texto Actualización Proyecto",
                        "Estado texto",
                        "Estado actualización",
                        "Estado de actualización",
                        "Seguimiento",
                        "Última actualización",
                        "Actualización"
                    }
                    : new[]
                    {
                        "Estado texto Actualización Proyecto",
                        "Estado Texto Actualización Proyecto",
                        "Estado texto actualización proyecto",
                        "Estado texto",
                        "Estado actualización",
                        "Estado de actualización",
                        "Seguimiento Estado Proyecto",
                        "Seguimiento",
                        "Última actualización"
                    };

            foreach (var preferred in preferredNames)
            {
                var normalizedPreferred =
                    NormalizePropertyName(
                        preferred);

                foreach (var property in
                         props.EnumerateObject())
                {
                    if (NormalizePropertyName(
                            property.Name) !=
                        normalizedPreferred)
                    {
                        continue;
                    }

                    var text =
                        ExtractUsefulStatusText(
                            property.Value);

                    if (IsUsefulProjectStatus(text))
                        return text.Trim();
                }
            }

            var candidates =
                new List<(string Text, int Score)>();

            foreach (var property in
                     props.EnumerateObject())
            {
                var name =
                    NormalizePropertyName(
                        property.Name);

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

                if (normalizedSource.Contains("cobrar") ||
                    normalizedSource.Contains("pagar"))
                {
                    if (name.Contains("cobrar") ||
                        name.Contains("pagar") ||
                        name.Contains("cobro") ||
                        name.Contains("pago"))
                    {
                        score += 3;
                    }
                }

                if (score < 6)
                    continue;

                var text =
                    ExtractUsefulStatusText(
                        property.Value);

                if (IsUsefulProjectStatus(text))
                    candidates.Add((text.Trim(), score));
            }

            return candidates
                .OrderByDescending(item => item.Score)
                .Select(item => item.Text)
                .FirstOrDefault() ??
                string.Empty;
        }

        private static string ExtractUsefulStatusText(
            JsonElement property)
        {
            var type =
                GetString(
                    property,
                    "type");

            if (type == "checkbox")
                return string.Empty;

            if (type == "formula" &&
                property.TryGetProperty(
                    "formula",
                    out var formula) &&
                GetString(formula, "type") ==
                    "boolean")
            {
                return string.Empty;
            }

            return ExtractPropertyText(property);
        }

        private static bool IsUsefulProjectStatus(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var clean = text.Trim();

            return !string.Equals(clean, "Sí", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "No", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "True", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "False", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "Sin asignar", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "Sin estado", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "Vacío", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "Vacio", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "-", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(clean, "—", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPropTextByFlexibleAliases(
            JsonElement props,
            params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                var exact = GetPropText(props, alias);

                if (!string.IsNullOrWhiteSpace(exact))
                    return exact;
            }

            var normalizedAliases = aliases
                .Select(NormalizePropertyName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            foreach (var property in props.EnumerateObject())
            {
                var normalizedName =
                    NormalizePropertyName(property.Name);

                var matches =
                    normalizedAliases.Any(alias =>
                        normalizedName == alias ||
                        normalizedName.Contains(alias) ||
                        alias.Contains(normalizedName));

                if (!matches &&
                    normalizedAliases.Any(alias =>
                        alias.Contains("fecha por hacer") &&
                        normalizedName.Contains("fecha por hacer")))
                {
                    matches = true;
                }

                if (!matches)
                    continue;

                var value = ExtractPropertyText(property.Value);

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static string NormalizePropertyName(string value)
        {
            var text = (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Normalize(NormalizationForm.FormD);

            var builder = new StringBuilder();

            foreach (var ch in text)
            {
                var category =
                    System.Globalization.CharUnicodeInfo
                        .GetUnicodeCategory(ch);

                if (category ==
                    System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(
                    char.IsLetterOrDigit(ch)
                        ? ch
                        : ' ');
            }

            return string.Join(
                " ",
                builder.ToString()
                    .Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries));
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
                "rollup" => ExtractRollup(prop),
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

        private static string ExtractRollup(JsonElement prop)
        {
            if (!prop.TryGetProperty(
                    "rollup",
                    out var rollup) ||
                rollup.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var type = GetString(rollup, "type");

            if (type == "date")
                return ExtractDate(rollup);

            if (type == "array" &&
                rollup.TryGetProperty(
                    "array",
                    out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                return string.Join(
                    " ",
                    array.EnumerateArray()
                        .Select(ExtractPropertyText)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            if (type == "number")
                return ExtractNumber(rollup, "number");

            return string.Empty;
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
        public static async Task<List<SearchResultRow>> BuildManyAsync(
            string token,
            IEnumerable<NotionDataSourceConfig> dataSources,
            CancellationToken ct = default,
            int? maxItemsPerSource = null,
            DateTimeOffset? lastEditedAfterUtc = null)
        {
            var all = new List<SearchResultRow>();

            foreach (var source in dataSources.Where(x =>
                         x.Enabled &&
                         !string.IsNullOrWhiteSpace(x.DataSourceId)))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var rows = await BuildAsync(
                        token,
                        source.DataSourceId,
                        ct,
                        maxItemsPerSource,
                        lastEditedAfterUtc,
                        source.Name);

                    all.AddRange(rows);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Error consultando la base de Notion '{source.Name}': {ex.Message}",
                        ex);
                }
            }

            return all;
        }
        public static async Task<bool> HasAnyChangesSinceAsync(
            string token,
            IEnumerable<NotionDataSourceConfig> dataSources,
            DateTimeOffset lastSyncUtc,
            CancellationToken ct = default)
        {
            foreach (var source in dataSources.Where(x =>
                         x.Enabled &&
                         !string.IsNullOrWhiteSpace(x.DataSourceId)))
            {
                ct.ThrowIfCancellationRequested();

                var changes = await BuildAsync(
                    token,
                    source.DataSourceId,
                    ct,
                    maxItems: 1,
                    lastEditedAfterUtc: lastSyncUtc,
                    sourceName: source.Name);

                if (changes.Count > 0)
                    return true;
            }

            return false;
        }
        public static Task<List<SearchResultRow>> BuildManyChangedSinceAsync(
            string token,
            IEnumerable<NotionDataSourceConfig> dataSources,
            DateTimeOffset lastSyncUtc,
            CancellationToken ct = default,
            int? maxItemsPerSource = null)
        {
            return BuildManyAsync(
                token,
                dataSources,
                ct,
                maxItemsPerSource,
                lastEditedAfterUtc: lastSyncUtc);
        }
    }
}