using System;
using System.Collections.Generic;

namespace Anfeta.UI.Models.Notion
{
    public enum MessageThreadKind
    {
        Message,
        System
    }

    public sealed class MessageThreadAttachment
    {
        public string FileName { get; init; } = string.Empty;
        public string FileUploadId { get; init; } = string.Empty;
        public string BlockType { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;

        public bool IsImage =>
            string.Equals(
                BlockType,
                "image",
                StringComparison.OrdinalIgnoreCase);
    }

    public sealed class MessageThreadEntry
    {
        public string Id { get; init; } = string.Empty;
        public MessageThreadKind Kind { get; init; }
        public string AuthorTag { get; init; } = string.Empty;
        public string AuthorName { get; init; } = string.Empty;
        public string RecipientTag { get; init; } = string.Empty;
        public string RecipientName { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public string Text { get; init; } = string.Empty;
        public IReadOnlyList<MessageThreadAttachment> Attachments { get; init; } =
            Array.Empty<MessageThreadAttachment>();
    }
}
