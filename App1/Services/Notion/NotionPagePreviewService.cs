using Anfeta.UI.Models.Notion;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Notion
{
    public sealed class NotionPagePreviewService
    {
        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";
        private const int MaxBlocks = 250;
        private const int MaxDepth = 3;
        private const int MaxRetryAttempts = 4;

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        private sealed record CacheEntry(
            DateTimeOffset StoredAt,
            IReadOnlyList<NotionPreviewBlock> Blocks);

        public async Task<IReadOnlyList<NotionPreviewBlock>> GetPagePreviewAsync(
            string token,
            string pageId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "No hay un token de Notion configurado.");

            pageId = NormalizeId(pageId);

            if (string.IsNullOrWhiteSpace(pageId))
                throw new ArgumentException(
                    "La página de Notion no tiene identificador.");

            if (Cache.TryGetValue(pageId, out var cached) &&
                DateTimeOffset.UtcNow - cached.StoredAt < TimeSpan.FromMinutes(5))
            {
                return cached.Blocks;
            }

            using var http = CreateClient(token);
            var blocks = new List<NotionPreviewBlock>();

            await ReadChildrenRecursiveAsync(
                http,
                pageId,
                depth: 0,
                blocks,
                cancellationToken);

            var result = blocks
                .Take(MaxBlocks)
                .ToList();

            Cache[pageId] = new CacheEntry(
                DateTimeOffset.UtcNow,
                result);

            return result;
        }

        private static HttpClient CreateClient(string token)
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(NotionBaseUrl),
                Timeout = TimeSpan.FromSeconds(90)
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

        private static async Task ReadChildrenRecursiveAsync(
            HttpClient http,
            string blockId,
            int depth,
            List<NotionPreviewBlock> output,
            CancellationToken cancellationToken)
        {
            if (depth > MaxDepth || output.Count >= MaxBlocks)
                return;

            string? cursor = null;
            var hasMore = true;

            while (hasMore && output.Count < MaxBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var url =
                    $"blocks/{NormalizeId(blockId)}/children?page_size=100";

                if (!string.IsNullOrWhiteSpace(cursor))
                    url += $"&start_cursor={Uri.EscapeDataString(cursor)}";

                using var response =
                    await SendGetWithRetryAsync(
                        http,
                        url,
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "consultar el contenido de la página",
                        response,
                        json);
                }

                using var document =
                    JsonDocument.Parse(json);

                var root = document.RootElement;

                if (root.TryGetProperty("results", out var results) &&
                    results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in results.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var mapped =
                            MapBlock(block, depth);

                        if (mapped != null)
                            output.Add(mapped);

                        if (output.Count >= MaxBlocks)
                            break;

                        var hasChildren =
                            block.TryGetProperty(
                                "has_children",
                                out var hasChildrenElement) &&
                            hasChildrenElement.ValueKind ==
                                JsonValueKind.True;

                        if (hasChildren &&
                            depth < MaxDepth)
                        {
                            var childId =
                                ReadString(block, "id");

                            if (!string.IsNullOrWhiteSpace(childId))
                            {
                                await ReadChildrenRecursiveAsync(
                                    http,
                                    childId,
                                    depth + 1,
                                    output,
                                    cancellationToken);
                            }
                        }
                    }
                }

                hasMore =
                    root.TryGetProperty("has_more", out var more) &&
                    more.ValueKind == JsonValueKind.True;

                cursor =
                    root.TryGetProperty("next_cursor", out var next) &&
                    next.ValueKind == JsonValueKind.String
                        ? next.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(cursor))
                    hasMore = false;
            }
        }

        private static NotionPreviewBlock? MapBlock(
            JsonElement block,
            int depth)
        {
            var id = ReadString(block, "id");
            var type = ReadString(block, "type");

            if (string.IsNullOrWhiteSpace(type))
                return null;

            if (!block.TryGetProperty(type, out var payload))
            {
                return new NotionPreviewBlock
                {
                    Id = id,
                    Kind = NotionPreviewBlockKind.Unsupported,
                    Text = $"Bloque no compatible: {type}",
                    Depth = depth
                };
            }

            var text = ReadRichText(payload);
            var caption = ReadCaption(payload);
            var url = ReadFileUrl(payload);

            // Los siguientes bloques son contenedores estructurales.
            // Sus hijos se consultan recursivamente, pero el contenedor no se dibuja.
            if (type is "column_list" or
                "column" or
                "synced_block" or
                "table" or
                "template" or
                "breadcrumb")
            {
                return null;
            }

            var kind = type switch
            {
                "paragraph" => NotionPreviewBlockKind.Paragraph,
                "heading_1" => NotionPreviewBlockKind.Heading1,
                "heading_2" => NotionPreviewBlockKind.Heading2,
                "heading_3" => NotionPreviewBlockKind.Heading3,
                "bulleted_list_item" => NotionPreviewBlockKind.BulletedListItem,
                "numbered_list_item" => NotionPreviewBlockKind.NumberedListItem,
                "to_do" => NotionPreviewBlockKind.ToDo,
                "quote" => NotionPreviewBlockKind.Quote,
                "callout" => NotionPreviewBlockKind.Callout,
                "divider" => NotionPreviewBlockKind.Divider,
                "code" => NotionPreviewBlockKind.Code,
                "bookmark" => NotionPreviewBlockKind.Bookmark,
                "link_preview" => NotionPreviewBlockKind.LinkPreview,
                "image" => NotionPreviewBlockKind.Image,
                "pdf" => NotionPreviewBlockKind.Pdf,
                "file" => NotionPreviewBlockKind.File,
                "audio" => NotionPreviewBlockKind.Audio,
                "video" => NotionPreviewBlockKind.Video,
                "child_page" => NotionPreviewBlockKind.ChildPage,
                "child_database" => NotionPreviewBlockKind.ChildDatabase,
                "toggle" => NotionPreviewBlockKind.Toggle,
                "equation" => NotionPreviewBlockKind.Equation,
                "table_row" => NotionPreviewBlockKind.TableRow,
                "embed" => NotionPreviewBlockKind.Embed,
                "link_to_page" => NotionPreviewBlockKind.ChildPage,
                _ => NotionPreviewBlockKind.Unsupported
            };

            if (type is "bookmark" or "link_preview")
                url = ReadString(payload, "url");

            if (type is "child_page" or "child_database")
                text = ReadString(payload, "title");

            if (type == "equation")
                text = ReadString(payload, "expression");

            if (type == "embed")
                url = ReadString(payload, "url");

            if (type == "table_row")
                text = ReadTableRow(payload);

            if (type == "link_to_page" &&
                string.IsNullOrWhiteSpace(text))
            {
                text = "Página relacionada";
            }

            if ((type is "child_page" or "child_database") &&
                string.Equals(
                    text,
                    "Untitled",
                    StringComparison.OrdinalIgnoreCase))
            {
                text = type == "child_database"
                    ? "Base relacionada"
                    : "Página relacionada";
            }

            var isChecked =
                type == "to_do" &&
                payload.TryGetProperty("checked", out var checkedElement) &&
                checkedElement.ValueKind == JsonValueKind.True;

            var language =
                type == "code"
                    ? ReadString(payload, "language")
                    : string.Empty;

            if (kind == NotionPreviewBlockKind.Unsupported &&
                string.IsNullOrWhiteSpace(text))
            {
                // Los bloques desconocidos sin texto no aportan información
                // y solo ensucian la vista previa.
                return null;
            }

            return new NotionPreviewBlock
            {
                Id = id,
                Kind = kind,
                Text = text,
                Url = url,
                Caption = caption,
                IsChecked = isChecked,
                Depth = depth,
                Language = language
            };
        }

        private static string ReadRichText(JsonElement payload)
        {
            if (!payload.TryGetProperty("rich_text", out var richText) ||
                richText.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var parts = new List<string>();

            foreach (var item in richText.EnumerateArray())
            {
                var plainText =
                    ReadString(item, "plain_text");

                if (!string.IsNullOrWhiteSpace(plainText))
                    parts.Add(plainText);
            }

            return string.Concat(parts).Trim();
        }

        private static string ReadTableRow(JsonElement payload)
        {
            if (!payload.TryGetProperty("cells", out var cells) ||
                cells.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var values = new List<string>();

            foreach (var cell in cells.EnumerateArray())
            {
                if (cell.ValueKind != JsonValueKind.Array)
                    continue;

                var cellText = string.Concat(
                    cell.EnumerateArray()
                        .Select(x => ReadString(x, "plain_text")))
                    .Trim();

                values.Add(cellText);
            }

            return string.Join("  |  ", values);
        }

        private static string ReadCaption(JsonElement payload)
        {
            if (!payload.TryGetProperty("caption", out var caption) ||
                caption.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            return string.Concat(
                caption.EnumerateArray()
                    .Select(x => ReadString(x, "plain_text")))
                .Trim();
        }

        private static string ReadFileUrl(JsonElement payload)
        {
            var type = ReadString(payload, "type");

            if (type == "external" &&
                payload.TryGetProperty("external", out var external))
            {
                return ReadString(external, "url");
            }

            if (type == "file" &&
                payload.TryGetProperty("file", out var file))
            {
                return ReadString(file, "url");
            }

            if (type == "file_upload" &&
                payload.TryGetProperty("file_upload", out var upload))
            {
                return ReadString(upload, "url");
            }

            return string.Empty;
        }

        private static async Task<HttpResponseMessage> SendGetWithRetryAsync(
            HttpClient http,
            string requestUri,
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
                    var response =
                        await http.GetAsync(
                            requestUri,
                            cancellationToken);

                    if (!ShouldRetry(response.StatusCode) ||
                        attempt == MaxRetryAttempts)
                    {
                        return response;
                    }

                    var delay =
                        GetRetryDelay(response, attempt);

                    response.Dispose();

                    await Task.Delay(
                        delay,
                        cancellationToken);
                }
                catch (TaskCanceledException ex)
                    when (!cancellationToken.IsCancellationRequested &&
                          attempt < MaxRetryAttempts)
                {
                    lastException = ex;
                    await Task.Delay(
                        GetExponentialDelay(attempt),
                        cancellationToken);
                }
                catch (HttpRequestException ex)
                    when (attempt < MaxRetryAttempts)
                {
                    lastException = ex;
                    await Task.Delay(
                        GetExponentialDelay(attempt),
                        cancellationToken);
                }
            }

            throw new HttpRequestException(
                "Notion no respondió después de varios intentos.",
                lastException);
        }

        private static bool ShouldRetry(HttpStatusCode statusCode)
        {
            var numeric = (int)statusCode;

            return statusCode == HttpStatusCode.TooManyRequests ||
                   numeric == 529 ||
                   numeric >= 500;
        }

        private static TimeSpan GetRetryDelay(
            HttpResponseMessage response,
            int attempt)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta &&
                delta > TimeSpan.Zero)
            {
                return delta;
            }

            if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
            {
                var wait = date - DateTimeOffset.UtcNow;

                if (wait > TimeSpan.Zero)
                    return wait;
            }

            return GetExponentialDelay(attempt);
        }

        private static TimeSpan GetExponentialDelay(int attempt)
        {
            var seconds =
                Math.Min(12, Math.Pow(2, attempt - 1));

            return TimeSpan.FromSeconds(seconds);
        }

        private static string NormalizeId(string value)
            => (value ?? string.Empty).Trim();

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
                using var document =
                    JsonDocument.Parse(body);

                var root = document.RootElement;
                var code = ReadString(root, "code");
                var message = ReadString(root, "message");

                detail = string.IsNullOrWhiteSpace(code)
                    ? message
                    : $"{code}: {message}";
            }
            catch
            {
                // Conserva el cuerpo original si Notion no devolvió JSON.
            }

            return new InvalidOperationException(
                $"Notion no pudo {operation} " +
                $"(HTTP {(int)response.StatusCode}): {detail}");
        }
    }
}
