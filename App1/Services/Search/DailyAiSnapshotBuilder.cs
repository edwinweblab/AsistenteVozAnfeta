using Anfeta.UI.Models.DailyAi;
using Anfeta.UI.Models.DailyProgress;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Search;

public sealed class DailyAiSnapshotBuilder
{
    private static readonly Regex AreaToken = new(
        @"(?<![\p{L}\p{Nd}_])(?<area>sseo|wwebs|aads|aapli|pprog|ddise|rrede|mmaps)(?![\p{L}\p{Nd}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public DailyAiSnapshot Build(DailyProgressSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var activities = source.People.SelectMany(person => person.AllActivities)
            // Los históricos se conservan en Avance Diario para auditoría, pero
            // no deben contaminar las métricas operativas de la fecha actual.
            .Where(item => !item.IsHistoricalSnapshot)
            .GroupBy(item => item.PageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(ToActivity)
            .OrderBy(item => item.Start).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var projects = activities.GroupBy(ProjectKey, StringComparer.OrdinalIgnoreCase)
            .Select(BuildProject).OrderByDescending(project => project.IsCritical)
            .ThenBy(project => project.ProjectName, StringComparer.CurrentCultureIgnoreCase).ToList();

        var people = activities.GroupBy(item => string.IsNullOrWhiteSpace(item.Person) ? "Sin asignar" : item.Person,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var originals = group.Select(item => source.People.FirstOrDefault(person =>
                    string.Equals(person.Name, item.Person, StringComparison.OrdinalIgnoreCase)))
                    .Where(person => person != null).Cast<DailyProgressPersonSnapshot>().ToList();
                var scheduled = originals.Select(x => x.ScheduledMinutes).DefaultIfEmpty().Max();
                var progress = originals.Select(x => x.ProgressMinutes).DefaultIfEmpty().Max();
                return new DailyAiPersonSnapshot(group.Key, group.Count(), group.Count(x => x.IsCompleted),
                    group.Count(IsPending), group.Count(x => x.IsLagging),
                    group.Select(ProjectKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(), scheduled, progress,
                    scheduled <= 0 ? 0 : Math.Clamp((int)Math.Round(progress * 100d / scheduled), 0, 100));
            }).OrderByDescending(person => person.LaggingActivities).ThenByDescending(person => person.ActivitiesToday)
            .ThenBy(person => person.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var areas = activities.GroupBy(item => item.Area, StringComparer.OrdinalIgnoreCase).Select(group =>
            new DailyAiAreaSnapshot(group.Key, group.Select(ProjectKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                group.Count(), group.Count(x => x.IsCompleted), group.Count(x => x.IsLagging),
                WeightedPercentage(group.Sum(x => x.ChecksTodayDone), group.Sum(x => x.ChecksTodayTotal))))
            .OrderByDescending(area => area.ActivitiesCount).ThenBy(area => area.Name).ToList();

        var metrics = new DailyAiMetrics(projects.Count, activities.Count, activities.Count(x => x.IsCompleted),
            activities.Count(IsPending), activities.Count(x => x.IsLagging), projects.Count(x => x.IsInReview),
            projects.Count(x => x.IsSuspended), activities.Count(x => x.IsUnassigned),
            activities.Count(x => x.ChecksTotal <= 0));

        var generatedAt = DateTimeOffset.Now;
        var fingerprintInput = JsonSerializer.Serialize(new { Date = source.Date.Date, metrics, projects, people, areas });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
        return new DailyAiSnapshot(source.Date.Date, generatedAt, metrics, projects, people, areas, activities,
            source.DataNote ?? string.Empty, fingerprint);
    }

    private static DailyAiActivitySnapshot ToActivity(DailyProgressActivityItem item)
    {
        var title = item.FullTitle.Length > 0 ? item.FullTitle : item.Source.Title;
        var person = string.IsNullOrWhiteSpace(item.Person) ? "Sin asignar" : item.Person.Trim();
        return new DailyAiActivitySnapshot(item.PageId, item.PageUrl, title,
            string.IsNullOrWhiteSpace(item.Source.Project) ? "Sin proyecto" : item.Source.Project.Trim(),
            item.Domain?.Trim() ?? string.Empty, ResolveArea(title), person, item.StateCode, item.StateLabel,
            item.Start, item.End, item.TodayChecklistCompleted, item.TodayChecklistTotal,
            item.TodayChecklistPercentage, item.ChecklistCompleted, item.ChecklistTotal, item.ChecklistPercentage,
            item.IsLagging, item.IsReviewMovement || item.IsReview, item.IsSuspended, item.IsCompletedMovement || item.IsCompleted,
            IsUnassigned(person), item.IsHistoricalSnapshot);
    }

    private static DailyAiProjectSnapshot BuildProject(IGrouping<string, DailyAiActivitySnapshot> group)
    {
        var rows = group.ToList();
        var lagging = rows.Count(x => x.IsLagging);
        var review = rows.Count(x => x.IsInReview);
        var unassigned = rows.Count(x => x.IsUnassigned);
        var suspended = rows.All(x => x.IsSuspended);
        var reasons = new List<string>();
        if (lagging > 0) reasons.Add($"{lagging} actividad(es) rezagada(s)");
        if (review > 0) reasons.Add($"{review} actividad(es) en revisión");
        if (unassigned > 0) reasons.Add($"{unassigned} actividad(es) sin responsable");
        if (!suspended && rows.Any(IsPending) && rows.Sum(x => x.ChecksTodayTotal) > 0 &&
            rows.Sum(x => x.ChecksTodayDone) == 0)
            reasons.Add("Sin avance de checklist registrado hoy");
        var last = rows.Where(x => x.ChecksTodayDone > 0).OrderByDescending(x => x.End).FirstOrDefault();
        return new DailyAiProjectSnapshot(group.Key,
            rows.Select(x => x.Domain).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            rows.GroupBy(x => x.Area).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "S/T",
            rows.Where(x => !x.IsUnassigned).Select(x => x.Person).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            rows.Count, rows.Count(x => x.IsCompleted), rows.Count(IsPending), rows.Sum(x => x.ChecksTodayDone),
            rows.Sum(x => x.ChecksTodayTotal), WeightedPercentage(rows.Sum(x => x.ChecksTodayDone), rows.Sum(x => x.ChecksTodayTotal)),
            rows.Sum(x => x.ChecksTotalDone), rows.Sum(x => x.ChecksTotal),
            WeightedPercentage(rows.Sum(x => x.ChecksTotalDone), rows.Sum(x => x.ChecksTotal)),
            last == null ? string.Empty : $"{last.End:HH:mm} · {last.Title}", reasons.Count > 0, lagging > 0,
            review > 0, suspended, unassigned > 0, reasons);
    }

    private static bool IsPending(DailyAiActivitySnapshot item) =>
        !item.IsCompleted && !item.IsInReview && !item.IsSuspended;

    private static bool IsUnassigned(string person) => string.IsNullOrWhiteSpace(person) ||
        person.Contains("sin asignar", StringComparison.OrdinalIgnoreCase);

    private static string ProjectKey(DailyAiActivitySnapshot item) => !string.IsNullOrWhiteSpace(item.ProjectName) &&
        !string.Equals(item.ProjectName, "Sin proyecto", StringComparison.OrdinalIgnoreCase) ? item.ProjectName :
        !string.IsNullOrWhiteSpace(item.Domain) ? item.Domain : "Sin proyecto";

    private static int WeightedPercentage(int done, int total) => total <= 0 ? 0 :
        Math.Clamp((int)Math.Round(done * 100d / total), 0, 100);

    private static string ResolveArea(string title) => AreaToken.Match(title ?? string.Empty).Groups["area"].Value.ToLowerInvariant() switch
    {
        "sseo" => "SEO", "wwebs" => "WEB", "aads" => "ADS", "aapli" => "APLICACIÓN",
        "pprog" => "PROGRAMACIÓN", "ddise" => "DISEÑO", "rrede" => "REDES", "mmaps" => "MAPS", _ => "S/T"
    };
}
