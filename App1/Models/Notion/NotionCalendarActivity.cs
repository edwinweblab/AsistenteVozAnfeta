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
        public string ReviewAssignee { get; set; } = "";
        public string ReviewState { get; set; } = "";
        public DateTimeOffset? ReviewSubmittedAt { get; set; }
        public DateTimeOffset? ReviewUpdatedAt { get; set; }
        public string ReviewUpdatedBy { get; set; } = "";
        public string ReviewNote { get; set; } = "";
        public bool IsReviewMirror { get; set; }
        public bool IsCompletedForReview { get; set; }

        public bool HasReviewFlow =>
            !string.IsNullOrWhiteSpace(ReviewState);

        public bool IsPendingReview =>
            string.Equals(
                ReviewState,
                "pending",
                StringComparison.OrdinalIgnoreCase);

        public bool IsReturnedForCorrections =>
            string.Equals(
                ReviewState,
                "returned",
                StringComparison.OrdinalIgnoreCase);

        public bool IsApprovedReview =>
            string.Equals(
                ReviewState,
                "approved",
                StringComparison.OrdinalIgnoreCase);

        public string ReviewBadgeLabel =>
            IsPendingReview
                ? IsReviewMirror
                    ? $"Realizada · revisión con {ReviewAssignee}"
                    : $"Para revisar · realizada por {OriginalPerson}"
                : IsReturnedForCorrections
                    ? $"Correcciones solicitadas por {ReviewAssignee}"
                    : IsApprovedReview
                        ? $"Aprobada por {ReviewAssignee}"
                        : string.Empty;
        public string Project { get; set; } = "";
        public string Status { get; set; } = "";
        public string StatusColor { get; set; } = "";
        public string UpdateText { get; set; } = "";
        public string Description { get; set; } = "";
        public string DatePropertyName { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public string TimeLabel =>
            $"{Start:HH:mm} – {End:HH:mm}";
    }
}
