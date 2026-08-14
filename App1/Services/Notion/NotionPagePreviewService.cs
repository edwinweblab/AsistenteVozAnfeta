using Anfeta.UI.Models.Notion;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
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

        // Body bajo demanda: una vez descargado se reutiliza durante 20 min.
        private static readonly TimeSpan PreviewCacheLifetime =
            TimeSpan.FromMinutes(20);

        private const string HiddenMetadataLabel =
            "Datos internos de ANFETA";

        private static readonly string[] TechnicalMetadataPrefixes =
        {
            "[ANFETA_THREAD_V1]",
            "[ANFETA_REVIEW_SOURCE_V1]",
            "[ANFETA_REVIEW_FLOW_V1]"
        };

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        // Si el usuario pasa varias veces por la misma tarjeta, todos los
        // consumidores esperan una sola descarga del preview.
        private static readonly ConcurrentDictionary<
            string,
            Task<IReadOnlyList<NotionPreviewBlock>>> ActivePreviewLoads =
                new(StringComparer.OrdinalIgnoreCase);

        private sealed record CacheEntry(
            DateTimeOffset StoredAt,
            IReadOnlyList<NotionPreviewBlock> Blocks);

        public Task<IReadOnlyList<NotionPreviewBlock>> GetPagePreviewAsync(
            string token,
            string pageId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "No hay un token de Notion configurado.");

            var normalizedPageId = NormalizeId(pageId);

            if (string.IsNullOrWhiteSpace(normalizedPageId))
                throw new ArgumentException(
                    "La página de Notion no tiene identificador.");

            if (Cache.TryGetValue(normalizedPageId, out var cached) &&
                DateTimeOffset.UtcNow - cached.StoredAt < PreviewCacheLifetime)
            {
                return Task.FromResult(cached.Blocks);
            }

            var task = ActivePreviewLoads.GetOrAdd(
                normalizedPageId,
                _ => GetPagePreviewCoreAsync(
                    token,
                    normalizedPageId,
                    cancellationToken));

            return AwaitSharedPreviewAsync(normalizedPageId, task);
        }

        private static async Task<IReadOnlyList<NotionPreviewBlock>>
            AwaitSharedPreviewAsync(
                string pageId,
                Task<IReadOnlyList<NotionPreviewBlock>> task)
        {
            try
            {
                return await task;
            }
            finally
            {
                if (ActivePreviewLoads.TryGetValue(pageId, out var current) &&
                    ReferenceEquals(current, task))
                {
                    ActivePreviewLoads.TryRemove(pageId, out _);
                }
            }
        }

        private async Task<IReadOnlyList<NotionPreviewBlock>> GetPagePreviewCoreAsync(
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

        private static bool IsTechnicalMetadataBlock(
            JsonElement block)
        {
            var type =
                ReadString(block, "type");

            if (string.IsNullOrWhiteSpace(type) ||
                !block.TryGetProperty(
                    type,
                    out var payload))
            {
                return false;
            }

            var text =
                ReadRichText(payload);

            if (string.Equals(
                    text,
                    HiddenMetadataLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return TechnicalMetadataPrefixes.Any(prefix =>
                       text.StartsWith(
                           prefix,
                           StringComparison.Ordinal)) ||
                   text.StartsWith(
                       "[ANFETA_",
                       StringComparison.Ordinal);
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

                        // Los bloques internos relacionan conversaciones,
                        // alertas y actividades. Se conservan en Notion, pero
                        // nunca deben mostrarse en previews ni recorrerse.
                        if (IsTechnicalMetadataBlock(block))
                            continue;

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
            var isStrikethrough = ReadRichTextStrikethrough(payload);
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
                IsStrikethrough = isStrikethrough,
                Depth = depth,
                Language = language
            };
        }


        private static bool ReadRichTextStrikethrough(JsonElement payload)
        {
            if (!payload.TryGetProperty("rich_text", out var richText) ||
                richText.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var hasText = false;
            foreach (var item in richText.EnumerateArray())
            {
                if (!string.IsNullOrWhiteSpace(ReadString(item, "plain_text")))
                    hasText = true;

                if (!item.TryGetProperty("annotations", out var annotations) ||
                    !annotations.TryGetProperty("strikethrough", out var strike) ||
                    strike.ValueKind != JsonValueKind.True)
                {
                    return false;
                }
            }

            return hasText;
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
