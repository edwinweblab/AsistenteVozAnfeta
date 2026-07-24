using System;

namespace Anfeta.UI.Models.Notion
{
    public enum NotionPreviewBlockKind
    {
        Paragraph,
        Heading1,
        Heading2,
        Heading3,
        BulletedListItem,
        NumberedListItem,
        ToDo,
        Quote,
        Callout,
        Divider,
        Code,
        Bookmark,
        LinkPreview,
        Image,
        Pdf,
        File,
        Audio,
        Video,
        ChildPage,
        ChildDatabase,
        Toggle,
        Equation,
        TableRow,
        Embed,
        Unsupported
    }

    public sealed class NotionPreviewBlock
    {
        public string Id { get; init; } = string.Empty;
        public NotionPreviewBlockKind Kind { get; init; }
        public string Text { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string Caption { get; init; } = string.Empty;
        public bool IsChecked { get; init; }
        public int Depth { get; init; }
        public string Language { get; init; } = string.Empty;
    }
}
