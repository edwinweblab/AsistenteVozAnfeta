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

        private sealed record ResolvedAttachmentBlock(
            string FileName,
            string Url,
            string BlockType);

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
                    await http.GetAsync(
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
                        var attachmentBlock =
                            TryParseAttachmentBlock(block);

                        if (attachmentBlock != null)
                        {
                            pendingAttachments.Add(
                                attachmentBlock);

                            continue;
                        }

                        var entry =
                            TryParseEntry(block);

                        if (entry == null)
                            continue;

                        if (entry.Attachments != null &&
                            entry.Attachments.Count > 0)
                        {
                            entry =
                                EnrichEntryAttachments(
                                    entry,
                                    pendingAttachments);

                            pendingAttachments.Clear();
                        }

                        entries.Add(entry);
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
                            new Dictionary<string, object?>
                            {
                                ["object"] = "block",
                                ["type"] = "paragraph",
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
                                                            ["content"] =
                                                                encoded
                                                        }
                                                }
                                            }
                                    }
                            }
                        }
                };

            using var http =
                CreateClient(token);

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Patch,
                    $"blocks/{NormalizeId(pageId)}/children")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        Encoding.UTF8,
                        "application/json")
                };

            using var response =
                await http.SendAsync(
                    request,
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
