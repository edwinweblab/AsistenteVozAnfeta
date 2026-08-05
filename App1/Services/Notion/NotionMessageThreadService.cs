using Anfeta.UI.Models.Notion;
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
    public sealed class NotionMessageThreadService
    {
        private const string NotionBaseUrl =
            "https://api.notion.com/v1/";

        private const string NotionVersion =
            "2026-03-11";

        private const string EntryPrefix =
            "[ANFETA_THREAD_V1]";

        public const string ReviewSourcePrefix =
            "[ANFETA_REVIEW_SOURCE_V1]";

        public const string ReviewFlowPrefix =
            "[ANFETA_REVIEW_FLOW_V1]";

        private const string HiddenMetadataLabel =
            "Datos internos de ANFETA";

        private static readonly string[] TechnicalPrefixes =
        {
            EntryPrefix,
            ReviewSourcePrefix,
            ReviewFlowPrefix
        };

        private sealed record ResolvedAttachmentBlock(
            string FileName,
            string Url,
            string BlockType);

        private static Dictionary<string, object?>
            BuildHiddenMetadataToggle(
                string encoded)
        {
            return new Dictionary<string, object?>
            {
                ["object"] = "block",
                ["type"] = "toggle",
                ["toggle"] = new Dictionary<string, object?>
                {
                    ["rich_text"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = new Dictionary<string, object?>
                            {
                                ["content"] = HiddenMetadataLabel
                            },
                            ["annotations"] = new Dictionary<string, object?>
                            {
                                ["bold"] = false,
                                ["italic"] = false,
                                ["strikethrough"] = false,
                                ["underline"] = false,
                                ["code"] = false,
                                ["color"] = "gray"
                            }
                        }
                    },
                    ["children"] = new object[]
                    {
                        BuildEncodedParagraph(encoded)
                    }
                }
            };
        }

        private static Dictionary<string, object?>
            BuildEncodedParagraph(
                string encoded)
        {
            return new Dictionary<string, object?>
            {
                ["object"] = "block",
                ["type"] = "paragraph",
                ["paragraph"] = new Dictionary<string, object?>
                {
                    ["rich_text"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = new Dictionary<string, object?>
                            {
                                ["content"] = encoded
                            },
                            ["annotations"] = new Dictionary<string, object?>
                            {
                                ["bold"] = false,
                                ["italic"] = false,
                                ["strikethrough"] = false,
                                ["underline"] = false,
                                ["code"] = true,
                                ["color"] = "gray"
                            }
                        }
                    }
                }
            };
        }

        private static bool IsToggleBlock(
            JsonElement block)
        {
            return string.Equals(
                ReadString(block, "type"),
                "toggle",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTechnicalPayload(
            string value)
        {
            return TechnicalPrefixes.Any(prefix =>
                (value ?? string.Empty).StartsWith(
                    prefix,
                    StringComparison.Ordinal));
        }

        private static async Task<IReadOnlyList<JsonElement>>
            ReadToggleChildrenAsync(
                HttpClient http,
                JsonElement toggleBlock,
                CancellationToken cancellationToken)
        {
            if (!IsToggleBlock(toggleBlock))
                return Array.Empty<JsonElement>();

            var blockId =
                ReadString(toggleBlock, "id");

            if (string.IsNullOrWhiteSpace(blockId))
                return Array.Empty<JsonElement>();

            var children =
                new List<JsonElement>();

            string? cursor = null;
            var hasMore = true;

            while (hasMore)
            {
                var url =
                    $"blocks/{NormalizeId(blockId)}/children?page_size=100";

                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    url +=
                        $"&start_cursor={Uri.EscapeDataString(cursor)}";
                }

                using var response =
                    await SendGetAsync(
                        http,
                        url,
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "consultar datos internos",
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
                    results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in results.EnumerateArray())
                        children.Add(child.Clone());
                }

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

            return children;
        }

        private static async Task MigrateVisibleTechnicalBlocksAsync(
            HttpClient http,
            string pageId,
            CancellationToken cancellationToken)
        {
            string? cursor = null;
            var hasMore = true;
            var legacyBlocks =
                new List<(string BlockId, string Raw)>();

            while (hasMore)
            {
                var url =
                    $"blocks/{NormalizeId(pageId)}/children?page_size=100";

                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    url +=
                        $"&start_cursor={Uri.EscapeDataString(cursor)}";
                }

                using var response =
                    await SendGetAsync(
                        http,
                        url,
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return;

                using var document =
                    JsonDocument.Parse(json);

                var root =
                    document.RootElement;

                if (root.TryGetProperty(
                        "results",
                        out var results) &&
                    results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in results.EnumerateArray())
                    {
                        var raw =
                            ReadParagraphPlainText(block);

                        if (!IsTechnicalPayload(raw))
                            continue;

                        var id =
                            ReadString(block, "id");

                        if (!string.IsNullOrWhiteSpace(id))
                            legacyBlocks.Add((id, raw));
                    }
                }

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

            foreach (var legacy in legacyBlocks)
            {
                var payload =
                    new Dictionary<string, object?>
                    {
                        ["children"] = new object[]
                        {
                            BuildHiddenMetadataToggle(legacy.Raw)
                        }
                    };

                using var appendResponse =
                    await SendJsonAsync(
                        http,
                        HttpMethod.Patch,
                        $"blocks/{NormalizeId(pageId)}/children",
                        payload,
                        cancellationToken);

                if (!appendResponse.IsSuccessStatusCode)
                    continue;

                using var archiveResponse =
                    await SendJsonAsync(
                        http,
                        HttpMethod.Patch,
                        $"blocks/{NormalizeId(legacy.BlockId)}",
                        new Dictionary<string, object?>
                        {
                            ["archived"] = true
                        },
                        cancellationToken);
            }
        }

        public async Task<bool> IsPageActiveAsync(
            string token,
            string pageId,
            CancellationToken cancellationToken = default)
        {
            Validate(token, pageId);

            using var http =
                CreateClient(token);

            using var response =
                await SendGetAsync(
                    http,
                    $"pages/{NormalizeId(pageId)}",
                    cancellationToken);

            var json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
                return false;

            using var document =
                JsonDocument.Parse(json);

            var root =
                document.RootElement;

            var archived =
                root.TryGetProperty(
                    "archived",
                    out var archivedValue) &&
                archivedValue.ValueKind ==
                    JsonValueKind.True;

            var inTrash =
                root.TryGetProperty(
                    "in_trash",
                    out var trashValue) &&
                trashValue.ValueKind ==
                    JsonValueKind.True;

            return !archived && !inTrash;
        }

        public async Task<IReadOnlyList<MessageThreadEntry>>
            GetThreadAsync(
                string token,
                string pageId,
                CancellationToken cancellationToken = default)
        {
            Validate(token, pageId);

            using var http =
                CreateClient(token);

            var entries =
                new List<MessageThreadEntry>();

            // Los bloques de archivo se agregan a Notion justo antes de la
            // entrada codificada del hilo. Se conservan temporalmente para
            // relacionarlos por posición aunque Notion no devuelva el nombre.
            var pendingAttachments =
                new List<ResolvedAttachmentBlock>();

            string? cursor = null;
            var hasMore = true;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var url =
                    $"blocks/{NormalizeId(pageId)}/children" +
                    "?page_size=100";

                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    url +=
                        $"&start_cursor=" +
                        $"{Uri.EscapeDataString(cursor)}";
                }

                using var response =
                    await SendGetAsync(
                        http,
                        url,
                        cancellationToken);

                var json =
                    await response.Content
                        .ReadAsStringAsync(
                            cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "consultar el historial",
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
                        var blocksToRead =
                            IsToggleBlock(block)
                                ? await ReadToggleChildrenAsync(
                                    http,
                                    block,
                                    cancellationToken)
                                : new[] { block.Clone() };

                        foreach (var readableBlock in blocksToRead)
                        {
                            var attachmentBlock =
                                TryParseAttachmentBlock(readableBlock);

                            if (attachmentBlock != null)
                            {
                                pendingAttachments.Add(
                                    attachmentBlock);

                                continue;
                            }

                            var entry =
                                TryParseEntry(readableBlock);

                            if (entry == null)
                                continue;

                            if (pendingAttachments.Count > 0)
                            {
                                entry =
                                    entry.Attachments != null &&
                                    entry.Attachments.Count > 0
                                        ? EnrichEntryAttachments(
                                            entry,
                                            pendingAttachments)
                                        : AttachLegacyResolvedAttachments(
                                            entry,
                                            pendingAttachments);

                                pendingAttachments.Clear();
                            }

                            entries.Add(entry);
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

            return entries
                .OrderBy(entry => entry.CreatedAt)
                .ToList();
        }

        public async Task<ReviewFlowMetadata?> GetReviewFlowAsync(
            string token,
            string pageId,
            CancellationToken cancellationToken = default)
        {
            Validate(token, pageId);

            using var http = CreateClient(token);
            ReviewFlowMetadata? latest = null;
            string? cursor = null;
            var hasMore = true;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var url =
                    $"blocks/{NormalizeId(pageId)}/children?page_size=100";

                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    url +=
                        $"&start_cursor={Uri.EscapeDataString(cursor)}";
                }

                using var response = await SendGetAsync(
                    http,
                    url,
                    cancellationToken);

                var json = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "consultar el flujo de revisión",
                        response,
                        json);
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty("results", out var results) &&
                    results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in results.EnumerateArray())
                    {
                        var blocksToRead =
                            IsToggleBlock(block)
                                ? await ReadToggleChildrenAsync(
                                    http,
                                    block,
                                    cancellationToken)
                                : new[] { block.Clone() };

                        foreach (var readableBlock in blocksToRead)
                        {
                            var plainText =
                                ReadParagraphPlainText(readableBlock);

                            if (!plainText.StartsWith(
                                    ReviewFlowPrefix,
                                    StringComparison.Ordinal))
                            {
                                continue;
                            }

                            try
                            {
                                var encoded = plainText.Substring(
                                    ReviewFlowPrefix.Length);

                                var payloadJson = Encoding.UTF8.GetString(
                                    Convert.FromBase64String(encoded));

                                var parsed =
                                    JsonSerializer.Deserialize<ReviewFlowMetadata>(
                                        payloadJson);

                                if (parsed != null &&
                                    (latest == null ||
                                     parsed.UpdatedAt >= latest.UpdatedAt))
                                {
                                    latest = parsed;
                                }
                            }
                            catch
                            {
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

            return latest;
        }

        public async Task SaveReviewFlowAsync(
            string token,
            string pageId,
            ReviewFlowMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            Validate(token, pageId);

            if (metadata == null ||
                string.IsNullOrWhiteSpace(metadata.OriginalPerson) ||
                string.IsNullOrWhiteSpace(metadata.State))
            {
                throw new ArgumentException(
                    "El flujo de revisión no contiene información válida.");
            }

            var json = JsonSerializer.Serialize(metadata);
            var encoded = ReviewFlowPrefix +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var payload = new Dictionary<string, object?>
            {
                ["children"] = new object[]
                {
                    BuildHiddenMetadataToggle(encoded)
                }
            };

            using var http = CreateClient(token);

            using var response =
                await SendJsonAsync(
                    http,
                    HttpMethod.Patch,
                    $"blocks/{NormalizeId(pageId)}/children",
                    payload,
                    cancellationToken);

            var responseJson = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "guardar el flujo de revisión",
                    response,
                    responseJson);
            }

            await MigrateVisibleTechnicalBlocksAsync(
                http,
                pageId,
                cancellationToken);
        }

        public async Task<ReviewAlertSourceLink?>
            GetReviewAlertSourceAsync(
                string token,
                string pageId,
                CancellationToken cancellationToken = default)
        {
            Validate(token, pageId);

            using var http = CreateClient(token);
            string? cursor = null;
            var hasMore = true;
            ReviewAlertSourceLink? threadReference = null;

            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var url =
                    $"blocks/{NormalizeId(pageId)}/children?page_size=100";

                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    url +=
                        $"&start_cursor={Uri.EscapeDataString(cursor)}";
                }

                using var response =
                    await SendGetAsync(
                        http,
                        url,
                        cancellationToken);

                var json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        "consultar la actividad original",
                        response,
                        json);
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty("results", out var results) &&
                    results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in results.EnumerateArray())
                    {
                        var blocksToRead =
                            IsToggleBlock(block)
                                ? await ReadToggleChildrenAsync(
                                    http,
                                    block,
                                    cancellationToken)
                                : new[] { block.Clone() };

                        foreach (var readableBlock in blocksToRead)
                        {
                            var plainText =
                                ReadParagraphPlainText(readableBlock);

                            if (plainText.StartsWith(
                                    ReviewSourcePrefix,
                                    StringComparison.Ordinal))
                            {
                                try
                                {
                                    var encoded = plainText.Substring(
                                        ReviewSourcePrefix.Length);

                                    var payloadJson = Encoding.UTF8.GetString(
                                        Convert.FromBase64String(encoded));

                                    var source =
                                        JsonSerializer.Deserialize<ReviewAlertSourceLink>(
                                            payloadJson);

                                    if (source != null &&
                                        (!string.IsNullOrWhiteSpace(source.PageId) ||
                                         !string.IsNullOrWhiteSpace(source.PageUrl)))
                                    {
                                        return source;
                                    }
                                }
                                catch
                                {
                                }
                            }

                            if (plainText.StartsWith(
                                    "Abrir actividad original:",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                var legacyUrl =
                                    TryReadFirstParagraphLink(readableBlock);

                                if (!string.IsNullOrWhiteSpace(legacyUrl))
                                {
                                    return new ReviewAlertSourceLink
                                    {
                                        PageUrl = legacyUrl
                                    };
                                }
                            }

                            var threadEntry =
                                TryParseEntry(readableBlock);

                            if (threadEntry != null)
                            {
                                threadReference ??=
                                    TryExtractActivityReference(
                                        threadEntry.Text);
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

            return threadReference;
        }

        private static ReviewAlertSourceLink?
            TryExtractActivityReference(
                string? text)
        {
            var value =
                (text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(value))
                return null;

            var lines = value
                .Split(
                    new[] { "\r\n", "\n", "\r" },
                    StringSplitOptions.None)
                .Select(line => line.Trim())
                .ToList();

            var titleLine = lines
                .FirstOrDefault(line =>
                    line.StartsWith(
                        "Actividad:",
                        StringComparison.OrdinalIgnoreCase));

            var title =
                titleLine == null
                    ? string.Empty
                    : titleLine
                        .Substring("Actividad:".Length)
                        .Trim();

            var notionLine = lines
                .FirstOrDefault(line =>
                    line.StartsWith(
                        "Notion:",
                        StringComparison.OrdinalIgnoreCase));

            var urlCandidate =
                notionLine == null
                    ? string.Empty
                    : notionLine
                        .Substring("Notion:".Length)
                        .Trim();

            if (string.IsNullOrWhiteSpace(urlCandidate))
            {
                var match =
                    System.Text.RegularExpressions.Regex.Match(
                        value,
                        @"https://(?:www\.)?notion\.(?:so|site)/[^\s<>()]+",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

                if (match.Success)
                    urlCandidate = match.Value;
            }

            urlCandidate =
                urlCandidate.TrimEnd(
                    '.',
                    ',',
                    ';',
                    ':',
                    ')',
                    ']',
                    '}');

            if (!Uri.TryCreate(
                    urlCandidate,
                    UriKind.Absolute,
                    out var uri))
            {
                return null;
            }

            return new ReviewAlertSourceLink
            {
                PageUrl = uri.AbsoluteUri,
                Title = title
            };
        }

        private static string TryReadFirstParagraphLink(
            JsonElement block)
        {
            if (!block.TryGetProperty("paragraph", out var paragraph) ||
                !paragraph.TryGetProperty("rich_text", out var richText) ||
                richText.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var item in richText.EnumerateArray())
            {
                if (!item.TryGetProperty("text", out var text) ||
                    !text.TryGetProperty("link", out var link) ||
                    link.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var url = ReadString(link, "url");

                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }

            return string.Empty;
        }

        private static string ReadParagraphPlainText(
            JsonElement block)
        {
            if (!block.TryGetProperty("type", out var type) ||
                !string.Equals(
                    type.GetString(),
                    "paragraph",
                    StringComparison.OrdinalIgnoreCase) ||
                !block.TryGetProperty("paragraph", out var paragraph) ||
                !paragraph.TryGetProperty("rich_text", out var richText) ||
                richText.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach (var item in richText.EnumerateArray())
            {
                if (item.TryGetProperty("plain_text", out var plain) &&
                    plain.ValueKind == JsonValueKind.String)
                {
                    builder.Append(plain.GetString());
                }
            }

            return builder.ToString();
        }

        public async Task AppendEntryAsync(
            string token,
            string pageId,
            MessageThreadEntry entry,
            CancellationToken cancellationToken = default)
        {
            Validate(token, pageId);

            if (entry == null ||
                (string.IsNullOrWhiteSpace(entry.Text) &&
                 (entry.Attachments == null ||
                  entry.Attachments.Count == 0)))
            {
                throw new ArgumentException(
                    "La respuesta no contiene texto ni archivos.");
            }

            var encoded =
                EncodeEntry(entry);

            var payload =
                new Dictionary<string, object?>
                {
                    ["children"] =
                        new object[]
                        {
                            BuildHiddenMetadataToggle(encoded)
                        }
                };

            using var http =
                CreateClient(token);

            using var response =
                await SendJsonAsync(
                    http,
                    HttpMethod.Patch,
                    $"blocks/{NormalizeId(pageId)}/children",
                    payload,
                    cancellationToken);

            var json =
                await response.Content
                    .ReadAsStringAsync(
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "guardar el historial",
                    response,
                    json);
            }

            await MigrateVisibleTechnicalBlocksAsync(
                http,
                pageId,
                cancellationToken);
        }

        public async Task UpdateEntryAsync(
            string token,
            string blockId,
            MessageThreadEntry entry,
            CancellationToken cancellationToken = default)
        {
            Validate(token, blockId);

            if (entry == null ||
                string.IsNullOrWhiteSpace(entry.Text))
            {
                throw new ArgumentException(
                    "La respuesta no contiene texto.");
            }

            var encoded = EncodeEntry(entry);

            var payload =
                new Dictionary<string, object?>
                {
                    ["paragraph"] =
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
                                                ["content"] = encoded
                                            }
                                    }
                                }
                        }
                };

            using var http = CreateClient(token);

            using var response =
                await SendJsonAsync(
                    http,
                    HttpMethod.Patch,
                    $"blocks/{NormalizeId(blockId)}",
                    payload,
                    cancellationToken);

            var json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "editar la respuesta",
                    response,
                    json);
            }
        }

        public async Task DeleteEntryAsync(
            string token,
            string blockId,
            CancellationToken cancellationToken = default)
        {
            Validate(token, blockId);

            var payload =
                new Dictionary<string, object?>
                {
                    ["archived"] = true
                };

            using var http = CreateClient(token);

            using var response =
                await SendJsonAsync(
                    http,
                    HttpMethod.Patch,
                    $"blocks/{NormalizeId(blockId)}",
                    payload,
                    cancellationToken);

            var json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "eliminar la respuesta",
                    response,
                    json);
            }
        }

        private static string EncodeEntry(
            MessageThreadEntry entry)
        {
            var payload =
                new StoredThreadEntry
                {
                    Kind =
                        entry.Kind.ToString(),
                    AuthorTag =
                        entry.AuthorTag ?? string.Empty,
                    AuthorName =
                        entry.AuthorName ?? string.Empty,
                    RecipientTag =
                        entry.RecipientTag ?? string.Empty,
                    RecipientName =
                        entry.RecipientName ?? string.Empty,
                    CreatedAt =
                        (entry.CreatedAt == default
                            ? DateTimeOffset.Now
                            : entry.CreatedAt)
                        .ToString("O"),
                    ReferenceEntryId =
                        entry.ReferenceEntryId ?? string.Empty,
                    Text =
                        (entry.Text ?? string.Empty).Trim(),
                    Attachments =
                        (entry.Attachments ??
                         Array.Empty<MessageThreadAttachment>())
                        .Select(item =>
                            new StoredThreadAttachment
                            {
                                FileName = item.FileName ?? string.Empty,
                                FileUploadId = item.FileUploadId ?? string.Empty,
                                BlockType = item.BlockType ?? string.Empty,
                                Url = item.Url ?? string.Empty
                            })
                        .ToList()
                };

            var json =
                JsonSerializer.Serialize(payload);

            return EntryPrefix +
                   Convert.ToBase64String(
                       Encoding.UTF8.GetBytes(json));
        }

        private static MessageThreadEntry?
            TryParseEntry(
                JsonElement block)
        {
            if (!block.TryGetProperty(
                    "type",
                    out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    type.GetString(),
                    "paragraph",
                    StringComparison.OrdinalIgnoreCase) ||
                !block.TryGetProperty(
                    "paragraph",
                    out var paragraph) ||
                !paragraph.TryGetProperty(
                    "rich_text",
                    out var richText) ||
                richText.ValueKind !=
                    JsonValueKind.Array)
            {
                return null;
            }

            var builder =
                new StringBuilder();

            foreach (var item in
                     richText.EnumerateArray())
            {
                if (item.TryGetProperty(
                        "plain_text",
                        out var plain) &&
                    plain.ValueKind ==
                        JsonValueKind.String)
                {
                    builder.Append(
                        plain.GetString());
                }
            }

            var raw =
                builder.ToString();

            if (!raw.StartsWith(
                    EntryPrefix,
                    StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                var base64 =
                    raw.Substring(
                        EntryPrefix.Length);

                var json =
                    Encoding.UTF8.GetString(
                        Convert.FromBase64String(
                            base64));

                var stored =
                    JsonSerializer.Deserialize<
                        StoredThreadEntry>(json);

                if (stored == null ||
                    (string.IsNullOrWhiteSpace(
                         stored.Text) &&
                     (stored.Attachments == null ||
                      stored.Attachments.Count == 0)))
                {
                    return null;
                }

                Enum.TryParse<MessageThreadKind>(
                    stored.Kind,
                    ignoreCase: true,
                    out var kind);

                DateTimeOffset.TryParse(
                    stored.CreatedAt,
                    out var createdAt);

                return new MessageThreadEntry
                {
                    Id =
                        ReadString(block, "id"),
                    Kind = kind,
                    AuthorTag =
                        stored.AuthorTag ??
                        string.Empty,
                    AuthorName =
                        stored.AuthorName ??
                        string.Empty,
                    RecipientTag =
                        stored.RecipientTag ??
                        string.Empty,
                    RecipientName =
                        stored.RecipientName ??
                        string.Empty,
                    CreatedAt =
                        createdAt == default
                            ? DateTimeOffset.Now
                            : createdAt,
                    ReferenceEntryId =
                        stored.ReferenceEntryId ??
                        string.Empty,
                    Text =
                        stored.Text ?? string.Empty,
                    Attachments =
                        (stored.Attachments ??
                         new List<StoredThreadAttachment>())
                        .Select(item =>
                            new MessageThreadAttachment
                            {
                                FileName = item.FileName ?? string.Empty,
                                FileUploadId = item.FileUploadId ?? string.Empty,
                                BlockType = item.BlockType ?? string.Empty,
                                Url = item.Url ?? string.Empty
                            })
                        .ToList()
                };
            }
            catch
            {
                return null;
            }
        }

        private sealed class StoredThreadEntry
        {
            public string Kind { get; set; } =
                string.Empty;

            public string AuthorTag { get; set; } =
                string.Empty;

            public string AuthorName { get; set; } =
                string.Empty;

            public string RecipientTag { get; set; } =
                string.Empty;

            public string RecipientName { get; set; } =
                string.Empty;

            public string CreatedAt { get; set; } =
                string.Empty;

            public string ReferenceEntryId { get; set; } =
                string.Empty;

            public string Text { get; set; } =
                string.Empty;

            public List<StoredThreadAttachment> Attachments { get; set; } =
                new();
        }

        private sealed class StoredThreadAttachment
        {
            public string FileName { get; set; } = string.Empty;
            public string FileUploadId { get; set; } = string.Empty;
            public string BlockType { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
        }

        private static MessageThreadEntry
            AttachLegacyResolvedAttachments(
                MessageThreadEntry entry,
                IReadOnlyList<ResolvedAttachmentBlock> resolved)
        {
            var attachments =
                resolved
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item.Url) ||
                        !string.IsNullOrWhiteSpace(item.FileName))
                    .Select((item, index) =>
                        new MessageThreadAttachment
                        {
                            FileName =
                                !string.IsNullOrWhiteSpace(item.FileName)
                                    ? item.FileName
                                    : $"Adjunto antiguo {index + 1}",
                            BlockType =
                                item.BlockType ?? string.Empty,
                            Url =
                                item.Url ?? string.Empty
                        })
                    .ToList();

            return new MessageThreadEntry
            {
                Id = entry.Id,
                Kind = entry.Kind,
                AuthorTag = entry.AuthorTag,
                AuthorName = entry.AuthorName,
                RecipientTag = entry.RecipientTag,
                RecipientName = entry.RecipientName,
                CreatedAt = entry.CreatedAt,
                ReferenceEntryId = entry.ReferenceEntryId,
                Text = entry.Text,
                Attachments = attachments
            };
        }

        private static MessageThreadEntry
            EnrichEntryAttachments(
                MessageThreadEntry entry,
                IReadOnlyList<ResolvedAttachmentBlock> resolved)
        {
            if (entry.Attachments == null ||
                entry.Attachments.Count == 0)
            {
                return entry;
            }

            IReadOnlyList<ResolvedAttachmentBlock>
                available =
                    resolved.ToList();

            var enriched =
                entry.Attachments
                    .Select(attachment =>
                    {
                        var match =
                            available
                                .FirstOrDefault(item =>
                                    !string.IsNullOrWhiteSpace(
                                        item.FileName) &&
                                    string.Equals(
                                        item.FileName,
                                        attachment.FileName,
                                        StringComparison.OrdinalIgnoreCase))
                            ??
                            available
                                .FirstOrDefault(item =>
                                    string.Equals(
                                        item.BlockType,
                                        attachment.BlockType,
                                        StringComparison.OrdinalIgnoreCase))
                            ??
                            available.FirstOrDefault();

                        if (match != null)
                        {
                            available =
                                available
                                    .Where(item =>
                                        !ReferenceEquals(
                                            item,
                                            match))
                                    .ToList();
                        }

                        return new MessageThreadAttachment
                        {
                            FileName = attachment.FileName,
                            FileUploadId = attachment.FileUploadId,
                            BlockType =
                                !string.IsNullOrWhiteSpace(
                                    attachment.BlockType)
                                    ? attachment.BlockType
                                    : match?.BlockType ??
                                      string.Empty,
                            Url =
                                !string.IsNullOrWhiteSpace(
                                    attachment.Url)
                                    ? attachment.Url
                                    : match?.Url ??
                                      string.Empty
                        };
                    })
                    .ToList();

            return new MessageThreadEntry
            {
                Id = entry.Id,
                Kind = entry.Kind,
                AuthorTag = entry.AuthorTag,
                AuthorName = entry.AuthorName,
                RecipientTag = entry.RecipientTag,
                RecipientName = entry.RecipientName,
                CreatedAt = entry.CreatedAt,
                ReferenceEntryId = entry.ReferenceEntryId,
                Text = entry.Text,
                Attachments = enriched
            };
        }

        private static ResolvedAttachmentBlock?
            TryParseAttachmentBlock(
                JsonElement block)
        {
            var type =
                ReadString(block, "type");

            if (type is not (
                "image" or
                "pdf" or
                "file" or
                "audio" or
                "video"))
            {
                return null;
            }

            if (!block.TryGetProperty(
                    type,
                    out var payload))
            {
                return null;
            }

            var fileName =
                ReadString(payload, "name");

            var sourceType =
                ReadString(payload, "type");

            var url = string.Empty;

            if (string.Equals(
                    sourceType,
                    "file",
                    StringComparison.OrdinalIgnoreCase) &&
                payload.TryGetProperty(
                    "file",
                    out var file))
            {
                url = ReadString(file, "url");
            }
            else if (string.Equals(
                         sourceType,
                         "external",
                         StringComparison.OrdinalIgnoreCase) &&
                     payload.TryGetProperty(
                         "external",
                         out var external))
            {
                url = ReadString(external, "url");
            }
            else if (string.Equals(
                         sourceType,
                         "file_upload",
                         StringComparison.OrdinalIgnoreCase) &&
                     payload.TryGetProperty(
                         "file_upload",
                         out var upload))
            {
                url = ReadString(upload, "url");
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName =
                    TryReadCaption(payload);
            }

            if (string.IsNullOrWhiteSpace(fileName) &&
                Uri.TryCreate(
                    url,
                    UriKind.Absolute,
                    out var uri))
            {
                fileName =
                    System.IO.Path.GetFileName(
                        uri.LocalPath);
            }

            if (string.IsNullOrWhiteSpace(fileName) &&
                string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            return new ResolvedAttachmentBlock(
                fileName,
                url,
                type);
        }

        private static string TryReadCaption(
            JsonElement payload)
        {
            if (!payload.TryGetProperty(
                    "caption",
                    out var caption) ||
                caption.ValueKind !=
                    JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder =
                new StringBuilder();

            foreach (var item in
                     caption.EnumerateArray())
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

        private const int MaxRetryAttempts = 5;

        private static Task<HttpResponseMessage> SendGetAsync(
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

        private static Task<HttpResponseMessage> SendJsonAsync(
            HttpClient http,
            HttpMethod method,
            string requestUri,
            object payload,
            CancellationToken cancellationToken)
        {
            var serialized =
                JsonSerializer.Serialize(payload);

            return NotionRequestCoordinator.SendAsync(
                http,
                () => new HttpRequestMessage(
                    method,
                    requestUri)
                {
                    Content = new StringContent(
                        serialized,
                        Encoding.UTF8,
                        "application/json")
                },
                cancellationToken,
                MaxRetryAttempts);
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
                        TimeSpan.FromSeconds(90)
                };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    token.Trim());

            http.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "Notion-Version",
                    NotionVersion);

            return http;
        }

        private static void Validate(
            string token,
            string pageId)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "No hay un token de Notion configurado.");
            }

            if (string.IsNullOrWhiteSpace(pageId))
            {
                throw new ArgumentException(
                    "El mensaje no tiene identificador de Notion.");
            }
        }

        private static string NormalizeId(
            string value)
        {
            return (value ?? string.Empty)
                .Trim();
        }

        private static string ReadString(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(
                    propertyName,
                    out var value) ||
                value.ValueKind !=
                    JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString() ??
                   string.Empty;
        }

        private static InvalidOperationException
            CreateNotionException(
                string operation,
                HttpResponseMessage response,
                string body)
        {
            var detail = body;

            try
            {
                using var document =
                    JsonDocument.Parse(body);

                var root =
                    document.RootElement;

                var code =
                    ReadString(root, "code");

                var message =
                    ReadString(root, "message");

                detail =
                    string.IsNullOrWhiteSpace(code)
                        ? message
                        : $"{code}: {message}";
            }
            catch
            {
            }

            return new InvalidOperationException(
                $"Notion no pudo {operation} " +
                $"(HTTP {(int)response.StatusCode}): " +
                $"{detail}");
        }
    }
}
