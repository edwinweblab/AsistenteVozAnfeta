using Anfeta.UI.Services.Notion;

var today = new DateTime(2026, 10, 6);
var yesterday = new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(-6));
var now = yesterday.AddDays(1);
NotionCompletedChecklistItem Item(string id, string text, DateTimeOffset time) =>
    new(id, text, time, time.ToString("yyyy-MM-dd"));
NotionChecklistStats Stats(params NotionCompletedChecklistItem[] items) =>
    new(4, items.Length, CompletedItems: items);
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS: " + name);
}

var previous = Stats(Item("old", "Old text", yesterday));
var current = Stats(Item("OLD", "Edited text", now), Item("new", "New task", now)) with
{
    PendingItems = new[] { new NotionPendingChecklistItem("pending", "Still pending") }
};
var merged = NotionChecklistHistory.PreserveCompletionDates(current, previous);
Check(merged.GetCompletedItemsOn(today).Select(i => i.BlockId).SequenceEqual(new[] { "new" }),
    "Editing an already completed task does not count it again today");
Check(merged.GetCompletedItemsOn(today.AddDays(-1)).Single().Text == "Edited text",
    "Historical view keeps the original date and displays the latest text");
Check(merged.GetCompletedOn(today) == 1 && merged.GetCompletedOn(today.AddDays(-1)) == 1,
    "Daily counts agree with the dated items");
Check(merged.PendingItems?.Single().BlockId == "pending" && merged.Pending == 2,
    "Merging history preserves pending items and overall counts");
var uncheckedTask = NotionChecklistHistory.PreserveCompletionDates(Stats(), merged);
Check(uncheckedTask.GetCompletedItemsOn(today).Count == 0,
    "Unchecking removes the task from completed lists");
var completedAgain = NotionChecklistHistory.PreserveCompletionDates(Stats(Item("old", "Done again", now)), uncheckedTask);
Check(completedAgain.GetCompletedItemsOn(today).Count == 1,
    "Completing again after an observed uncheck records the new day");
var firstImport = NotionChecklistHistory.PreserveCompletionDates(current, new NotionChecklistStats(0, 0));
Check(firstImport.GetCompletedItemsOn(today).Count == 2,
    "First import uses the dates supplied by Notion");
var noDate = Stats(new NotionCompletedChecklistItem("old", "Old", DateTimeOffset.MinValue, ""));
Check(NotionChecklistHistory.PreserveCompletionDates(Stats(Item("old", "Dated", now)), noDate)
    .GetCompletedItemsOn(today).Count == 1, "Unknown old dates do not overwrite valid new dates");
