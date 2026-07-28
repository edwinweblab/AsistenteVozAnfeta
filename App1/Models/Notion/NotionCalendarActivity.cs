using System;

namespace Anfeta.UI.Models.Notion
{
    public sealed class NotionCalendarActivity
    {
        public string PageId { get; set; } = "";
        public string PageUrl { get; set; } = "";
        public string Title { get; set; } = "";
        public string Person { get; set; } = "";
        public string OriginalPerson { get; set; } = "";
        public bool IsCompletedForReview { get; set; }
        public string Project { get; set; } = "";
        public string Status { get; set; } = "";
        public string StatusColor { get; set; } = "";
        public string Description { get; set; } = "";
        public string DatePropertyName { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public string TimeLabel =>
            $"{Start:HH:mm} – {End:HH:mm}";
    }
}
