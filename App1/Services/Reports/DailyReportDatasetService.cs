using Anfeta.UI.Models.DailyAi;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Reports;

public sealed record DailyReportDatasetPaths(string JsonPath, string CsvPath, string OutputDirectory);

public sealed class DailyReportDatasetService
{
    public async Task<DailyReportDatasetPaths> ExportAsync(DailyAiSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = await ApplicationData.Current.LocalFolder.CreateFolderAsync("DailyReports", CreationCollisionOption.OpenIfExists);
        var folder = await root.CreateFolderAsync(snapshot.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CreationCollisionOption.OpenIfExists);
        var stem = "anfeta_daily_" + snapshot.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var json = await folder.CreateFileAsync(stem + ".json", CreationCollisionOption.ReplaceExisting);
        var csv = await folder.CreateFileAsync(stem + ".csv", CreationCollisionOption.ReplaceExisting);
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(json.Path, JsonSerializer.Serialize(snapshot, options), new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(csv.Path, BuildCsv(snapshot), new UTF8Encoding(true), cancellationToken);
        return new DailyReportDatasetPaths(json.Path, csv.Path, folder.Path);
    }

    private static string BuildCsv(DailyAiSnapshot snapshot)
    {
        static string Q(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Date,Project,Domain,Area,Person,Title,State,Start,End,ChecksTodayDone,ChecksTodayTotal,ProgressTodayPct,ChecksTotalDone,ChecksTotal,ProgressTotalPct,IsLagging,IsReview,IsSuspended,IsCompleted,IsUnassigned\r\n");
        foreach (var item in snapshot.Activities)
            sb.AppendLine(string.Join(',', new object?[] { snapshot.Date.ToString("yyyy-MM-dd"), item.ProjectName, item.Domain,
                item.Area, item.Person, item.Title, item.StateLabel, item.Start.ToString("O"), item.End.ToString("O"),
                item.ChecksTodayDone, item.ChecksTodayTotal, item.ProgressTodayPct, item.ChecksTotalDone, item.ChecksTotal,
                item.ProgressTotalPct, item.IsLagging, item.IsInReview, item.IsSuspended, item.IsCompleted, item.IsUnassigned }.Select(Q)));
        return sb.ToString();
    }
}
