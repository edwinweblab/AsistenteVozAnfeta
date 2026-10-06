using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Anfeta.UI.Services.Notion
{
    public sealed record NotionCompletedChecklistItem(
        string BlockId,
        string Text,
        DateTimeOffset CompletedAt,
        string DateKey);

    public sealed record NotionPendingChecklistItem(string BlockId, string Text);

    public sealed record NotionChecklistStats(
        int Total,
        int Completed,
        IReadOnlyDictionary<string, int>? CompletedByDate = null,
        int CommentCount = 0,
        string LatestCommentText = "",
        IReadOnlyList<NotionCompletedChecklistItem>? CompletedItems = null,
        IReadOnlyList<NotionPendingChecklistItem>? PendingItems = null)
    {
        public int Pending =>
            Math.Max(0, Total - Completed);

        public bool HasChecklist =>
            Total > 0;

        public int GetCompletedOn(DateTime day)
        {
            var key = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return CompletedByDate != null &&
                   CompletedByDate.TryGetValue(key, out var completed)
                ? Math.Clamp(completed, 0, Total)
                : 0;
        }

        public IReadOnlyList<NotionCompletedChecklistItem> GetCompletedItemsOn(DateTime day)
        {
            var key = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (CompletedItems == null || CompletedItems.Count == 0)
                return Array.Empty<NotionCompletedChecklistItem>();

            return CompletedItems
                .Where(item => string.Equals(item.DateKey, key, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

}
