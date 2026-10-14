using System;
using System.Collections.Generic;
using System.Linq;

namespace Anfeta.UI.Services.Notion
{
    internal static class NotionChecklistHistory
    {
        internal static NotionChecklistStats PreserveCompletionDates(
            NotionChecklistStats current, NotionChecklistStats previous)
        {
            if (current.CompletedItems == null || previous.CompletedItems == null)
                return current;
            var known = previous.CompletedItems
                .Where(item => !string.IsNullOrWhiteSpace(item.BlockId))
                .GroupBy(item => item.BlockId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var items = current.CompletedItems.Select(item =>
            {
                var hasOld = known.TryGetValue(item.BlockId, out var old);
                var created = item.CreatedAt != DateTimeOffset.MinValue
                    ? item.CreatedAt
                    : (hasOld && old!.CreatedAt != DateTimeOffset.MinValue ? old!.CreatedAt : DateTimeOffset.MinValue);

                if (hasOld && old!.CompletedAt != DateTimeOffset.MinValue && !string.IsNullOrWhiteSpace(old.DateKey))
                {
                    return item with { CompletedAt = old.CompletedAt, DateKey = old.DateKey, CreatedAt = created };
                }
                return item with { CreatedAt = created };
            }).ToList();
            return current with
            {
                CompletedItems = items,
                CompletedByDate = items.GroupBy(item => item.DateKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
