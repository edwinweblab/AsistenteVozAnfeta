using Anfeta.UI.Models.DailyProgress;
using Anfeta.UI.Models.Notion;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Notion
{
    /// <summary>
    /// Feed de Avance Diario V3.
    ///
    /// Reglas acordadas:
    /// - Rezago: P/PRTUZ + terminó su bloque + avance Hoy < 33%.
    ///   Un 33% o más cuenta como avance (umbral inclusivo).
    ///   Sin checklist NO se acusa como rezago; queda como dato faltante.
    /// - Cobertura: SOLO avance observado hoy, ponderado por minutos agendados.
    ///   El progreso histórico que ya existía al iniciar el seguimiento no suma.
    /// - R/Z del Feed: movimientos del día, no simplemente estados históricos.
    /// - Checklist "hecho hoy": checked=true + last_edited_time en la fecha.
    /// - Baseline/snapshot: respaldo y auditoría; no es la fuente principal de H.
    /// </summary>
    public sealed class NotionDailyProgressService
    {
        private const string TrackingFileName =
            "daily_progress_tracking_v2.json";

        private const string DailySnapshotFileName =
            "daily_progress_snapshots_v1.json";

        private static readonly SemaphoreSlim DailySnapshotGate =
            new(1, 1);

        private sealed class PersistedDailySnapshotStore
        {
            public Dictionary<string, Dictionary<string, PersistedDailyActivitySnapshot>> Days { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PersistedDailyActivitySnapshot
        {
            public string PageId { get; set; } = "";
            public DateTime ReportDate { get; set; }
            public string PageUrl { get; set; } = "";
            public string Title { get; set; } = "";
            public string Person { get; set; } = "";
            public string Status { get; set; } = "";
            public string Project { get; set; } = "";
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public DateTime OriginalScheduledDate { get; set; }
            public DateTime CurrentScheduledDate { get; set; }
            public int MoveCount { get; set; }
            public List<DateTime> RouteDates { get; set; } = new();
            public bool ChecklistScanned { get; set; }
            public int ChecklistTotal { get; set; }
            public int ChecklistCompleted { get; set; }
            public int TodayChecklistCompleted { get; set; }
            public bool IsLagging { get; set; }
            public bool IsReviewMovement { get; set; }
            public bool IsCompletedMovement { get; set; }
            public int ProgressMinutes { get; set; }
            public DateTimeOffset CapturedAtUtc { get; set; }
            public bool IsFinal { get; set; }
            public DateTimeOffset? ClosedAtUtc { get; set; }
            public string ReviewState { get; set; } = "";
            public DateTimeOffset? ReviewSubmittedAt { get; set; }
            public DateTimeOffset? ReviewUpdatedAt { get; set; }
        }

        // Jornada de captura: desde las 09:00 hasta el final del día.
        // La ventana ejecutiva 09:30–18:00 se aplica a los KPIs de cobertura;
        // las tarjetas posteriores siguen visibles para no perder trabajo real.
        private const int DailyProgressStartHour = 9;
        private const int ExecutiveWindowStartHour = 9;
        private const int ExecutiveWindowStartMinute = 30;
        private const int ExecutiveWindowEndHour = 18;

        private static readonly SemaphoreSlim TrackingGate =
            new(1, 1);

        private static DateTime GetDailyProgressWindowStart(
            DateTime day) =>
            day.Date.AddHours(DailyProgressStartHour);

        private static DateTime GetDailyProgressWindowEnd(
            DateTime day) =>
            day.Date.AddDays(1);

        // Comparte el mismo historial que SearchView.Calendar.
        private const string CalendarMoveHistoryFileName =
            "calendar_move_history_v1.json";

        private static readonly SemaphoreSlim CalendarMoveHistoryGate =
            new(1, 1);

        private sealed class PersistedCalendarMoveHistoryEntry
        {
            public string PageId { get; set; } = string.Empty;
            public DateTime SourceDate { get; set; }
            public DateTime LastSourceDate { get; set; }
            public DateTime TargetDate { get; set; }
            public DateTimeOffset MovedAt { get; set; }
            public string Reason { get; set; } = string.Empty;
            public int MoveCount { get; set; }
            public List<DateTime> RouteDates { get; set; } = new();
        }

        private static readonly string[] KnownPersonOrder =
        {
            "Neftali",
            "Karla",
            "Isaias",
            "Andrade",
            "Brian",
            "Genaro",
            "John",
            "Sotelo",
            "Acalli",
            "Emmanuel",
            "Sin asignar"
        };

        // Fuente única del Feed para que nombre visible y tag interno
        // JAMÁS creen dos personas distintas.
        private static readonly IReadOnlyDictionary<string, string>
            FeedPersonAliases =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["nneft"] = "Neftali",
                    ["nnetf"] = "Neftali",
                    ["neft"] = "Neftali",
                    ["netf"] = "Neftali",
                    ["neftali"] = "Neftali",

                    ["kkarl"] = "Karla",
                    ["karl"] = "Karla",
                    ["karla"] = "Karla",

                    ["iisai"] = "Isaias",
                    ["isai"] = "Isaias",
                    ["isaias"] = "Isaias",

                    ["aandr"] = "Andrade",
                    ["andr"] = "Andrade",
                    ["andrade"] = "Andrade",

                    ["bbria"] = "Brian",
                    ["bria"] = "Brian",
                    ["brian"] = "Brian",

                    ["ggena"] = "Genaro",
                    ["gena"] = "Genaro",
                    ["genaro"] = "Genaro",

                    ["jjohn"] = "John",
                    ["john"] = "John",

                    ["ssote"] = "Sotelo",
                    ["sote"] = "Sotelo",
                    ["eedua"] = "Sotelo",
                    ["edua"] = "Sotelo",
                    ["sotelo"] = "Sotelo",

                    ["aacal"] = "Acalli",
                    ["acal"] = "Acalli",
                    ["acalli"] = "Acalli",

                    ["eemma"] = "Emmanuel",
                    ["emma"] = "Emmanuel",
                    ["emmanuel"] = "Emmanuel"
                };

        private static readonly Regex DomainPattern = new(
            @"(?<![\w@])(?:https?://)?(?:www\.)?" +
            @"(?<domain>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+" +
            @"(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|" +
            @"com|mx|org|net|io|co|app|dev))" +
            @"(?=$|[/:?#\s)\]}>.,;!])",
            RegexOptions.Compiled |
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

        private sealed class PersistedTrackingStore
        {
            public Dictionary<string, PersistedDayTracking> Days { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PersistedDayTracking
        {
            public string DateKey { get; set; } = "";
            public DateTimeOffset FirstCapturedAtUtc { get; set; }
            public Dictionary<string, PersistedActivityBaseline> Baselines { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PersistedActivityBaseline
        {
            public int ChecklistCompleted { get; set; }
            public int ChecklistTotal { get; set; }
            public int WorkedMinutes { get; set; }
            public string StateCode { get; set; } = "P";
        }

        private sealed class TrackingContext
        {
            public bool HadExistingDay { get; init; }
            public DateTimeOffset FirstCapturedAtUtc { get; init; }
            public Dictionary<string, PersistedActivityBaseline> Baselines { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> NewlyAddedPageIds { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Baseline local usado por las cards del calendario para calcular
        /// únicamente el avance observado dentro del día seleccionado.
        ///
        /// NO consulta Notion ni bodies. Reutiliza daily_progress_tracking_v2.json.
        /// </summary>
        public sealed record CalendarCardChecklistBaseline(
            int Completed,
            int Total,
            bool EstablishedBeforeThisRead,
            DateTimeOffset TrackingStartedAt);

        public static async Task<IReadOnlyDictionary<
            string,
            CalendarCardChecklistBaseline>>
            GetCalendarCardChecklistBaselinesAsync(
                DateTime day,
                IEnumerable<NotionCalendarActivity> activities,
                CancellationToken cancellationToken = default)
        {
            var unique =
                (activities ??
                 Enumerable.Empty<NotionCalendarActivity>())
                    .Where(activity =>
                        activity != null &&
                        !activity.IsReviewMirror &&
                        !string.IsNullOrWhiteSpace(
                            activity.PageId))
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            if (unique.Count == 0)
            {
                return new Dictionary<
                    string,
                    CalendarCardChecklistBaseline>(
                        StringComparer.OrdinalIgnoreCase);
            }

            var tracking =
                await PrepareTrackingContextAsync(
                    day.Date,
                    unique,
                    cancellationToken);

            var result =
                new Dictionary<
                    string,
                    CalendarCardChecklistBaseline>(
                        StringComparer.OrdinalIgnoreCase);

            foreach (var activity in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!tracking.Baselines.TryGetValue(
                        activity.PageId,
                        out var baseline) ||
                    baseline == null)
                {
                    continue;
                }

                result[activity.PageId] =
                    new CalendarCardChecklistBaseline(
                        Math.Max(
                            0,
                            baseline.ChecklistCompleted),
                        Math.Max(
                            0,
                            baseline.ChecklistTotal),
                        tracking.HadExistingDay &&
                        !tracking.NewlyAddedPageIds.Contains(
                            activity.PageId),
                        tracking.FirstCapturedAtUtc
                            .ToLocalTime());
            }

            return result;
        }

        private static List<DateTime> NormalizeMovementRoute(
            PersistedCalendarMoveHistoryEntry? entry)
        {
            if (entry == null)
                return new List<DateTime>();

            var route =
                (entry.RouteDates ??
                 new List<DateTime>())
                    .Where(date =>
                        date != default)
                    .Select(date =>
                        date.Date)
                    .ToList();

            if (route.Count == 0)
            {
                if (entry.SourceDate != default)
                    route.Add(entry.SourceDate.Date);

                if (entry.TargetDate != default &&
                    (route.Count == 0 ||
                     route[^1] != entry.TargetDate.Date))
                {
                    route.Add(entry.TargetDate.Date);
                }
            }

            if (entry.SourceDate != default &&
                (route.Count == 0 ||
                 route[0] != entry.SourceDate.Date))
            {
                route.Insert(
                    0,
                    entry.SourceDate.Date);
            }

            if (entry.TargetDate != default &&
                (route.Count == 0 ||
                 route[^1] != entry.TargetDate.Date))
            {
                route.Add(
                    entry.TargetDate.Date);
            }

            var normalized =
                new List<DateTime>();

            foreach (var date in route)
            {
                if (normalized.Count == 0 ||
                    normalized[^1] != date.Date)
                {
                    normalized.Add(
                        date.Date);
                }
            }

            return normalized;
        }

        private static async Task<Dictionary<
            string,
            PersistedCalendarMoveHistoryEntry>>
            ReadCalendarMoveHistoryAsync(
                CancellationToken cancellationToken)
        {
            try
            {
                var path =
                    Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        CalendarMoveHistoryFileName);

                if (!File.Exists(path))
                {
                    return new Dictionary<
                        string,
                        PersistedCalendarMoveHistoryEntry>(
                            StringComparer.OrdinalIgnoreCase);
                }

                var json =
                    await File.ReadAllTextAsync(
                        path,
                        cancellationToken);

                var restored =
                    JsonSerializer.Deserialize<
                        Dictionary<
                            string,
                            PersistedCalendarMoveHistoryEntry>>(
                                json);

                var result =
                    new Dictionary<
                        string,
                        PersistedCalendarMoveHistoryEntry>(
                            StringComparer.OrdinalIgnoreCase);

                if (restored == null)
                    return result;

                foreach (var pair in restored)
                {
                    if (string.IsNullOrWhiteSpace(
                            pair.Key) ||
                        pair.Value == null)
                    {
                        continue;
                    }

                    var entry =
                        pair.Value;

                    entry.PageId =
                        pair.Key;

                    entry.SourceDate =
                        entry.SourceDate.Date;

                    entry.TargetDate =
                        entry.TargetDate.Date;

                    entry.LastSourceDate =
                        entry.LastSourceDate == default
                            ? entry.SourceDate
                            : entry.LastSourceDate.Date;

                    entry.RouteDates =
                        NormalizeMovementRoute(
                            entry);

                    if (entry.MoveCount <= 0)
                    {
                        entry.MoveCount =
                            Math.Max(
                                1,
                                entry.RouteDates.Count - 1);
                    }

                    result[pair.Key] =
                        entry;
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new Dictionary<
                    string,
                    PersistedCalendarMoveHistoryEntry>(
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        private static async Task SaveCalendarMoveHistoryAsync(
            IReadOnlyDictionary<
                string,
                PersistedCalendarMoveHistoryEntry> history,
            CancellationToken cancellationToken)
        {
            var path =
                Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    CalendarMoveHistoryFileName);

            var json =
                JsonSerializer.Serialize(
                    history,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            await File.WriteAllTextAsync(
                path,
                json,
                cancellationToken);
        }

        public static async Task RegisterCalendarMovementAsync(
            string pageId,
            DateTime sourceDate,
            DateTime targetDate,
            string reason,
            CancellationToken cancellationToken = default)
        {
            pageId =
                (pageId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(pageId))
                return;

            var source =
                sourceDate.Date;

            var target =
                targetDate.Date;

            await CalendarMoveHistoryGate.WaitAsync(
                cancellationToken);

            try
            {
                var history =
                    await ReadCalendarMoveHistoryAsync(
                        cancellationToken);

                if (target <= source)
                {
                    if (history.Remove(pageId))
                    {
                        await SaveCalendarMoveHistoryAsync(
                            history,
                            CancellationToken.None);
                    }

                    return;
                }

                history.TryGetValue(
                    pageId,
                    out var previous);

                var continuesExistingRoute =
                    previous != null &&
                    previous.TargetDate.Date == source &&
                    previous.SourceDate.Date < source;

                if (continuesExistingRoute)
                {
                    var route =
                        NormalizeMovementRoute(
                            previous!);

                    if (route.Count == 0 ||
                        route[^1] != source)
                    {
                        route.Add(
                            source);
                    }

                    if (route[^1] != target)
                    {
                        route.Add(
                            target);
                    }

                    previous!.LastSourceDate =
                        source;

                    previous.TargetDate =
                        target;

                    previous.MovedAt =
                        DateTimeOffset.Now;

                    previous.Reason =
                        (reason ?? string.Empty).Trim();

                    previous.RouteDates =
                        route;

                    previous.MoveCount =
                        Math.Max(
                            previous.MoveCount + 1,
                            route.Count - 1);

                    history[pageId] =
                        previous;
                }
                else
                {
                    history[pageId] =
                        new PersistedCalendarMoveHistoryEntry
                        {
                            PageId =
                                pageId,
                            SourceDate =
                                source,
                            LastSourceDate =
                                source,
                            TargetDate =
                                target,
                            MovedAt =
                                DateTimeOffset.Now,
                            Reason =
                                (reason ?? string.Empty).Trim(),
                            MoveCount =
                                1,
                            RouteDates =
                                new List<DateTime>
                                {
                                    source,
                                    target
                                }
                        };
                }

                await SaveCalendarMoveHistoryAsync(
                    history,
                    CancellationToken.None);
            }
            finally
            {
                CalendarMoveHistoryGate.Release();
            }
        }

        private static PersistedCalendarMoveHistoryEntry?
            GetValidCarryOverMovement(
                NotionCalendarActivity activity,
                IReadOnlyDictionary<
                    string,
                    PersistedCalendarMoveHistoryEntry> history)
        {
            if (activity == null ||
                string.IsNullOrWhiteSpace(
                    activity.PageId) ||
                history == null ||
                !history.TryGetValue(
                    activity.PageId,
                    out var movement) ||
                movement == null)
            {
                return null;
            }

            if (movement.TargetDate.Date !=
                    activity.Start.Date ||
                movement.TargetDate.Date <=
                    movement.SourceDate.Date)
            {
                return null;
            }

            movement.RouteDates =
                NormalizeMovementRoute(
                    movement);

            return movement;
        }

        private static string BuildCarryOverMovementLabel(
            PersistedCalendarMoveHistoryEntry? movement)
        {
            if (movement == null)
                return string.Empty;

            var days =
                Math.Max(
                    1,
                    (movement.TargetDate.Date -
                     movement.SourceDate.Date).Days);

            var label =
                days == 1
                    ? "↪ De ayer"
                    : $"↪ De hace {days} días";

            var route =
                NormalizeMovementRoute(
                    movement);

            var moveCount =
                Math.Max(
                    movement.MoveCount,
                    route.Count - 1);

            if (moveCount > 1)
            {
                label +=
                    $" · {moveCount} movimientos";
            }

            label +=
                $" · {movement.SourceDate:dd/MM}→" +
                $"{movement.TargetDate:dd/MM}";

            return label;
        }

        private static async Task<PersistedDailySnapshotStore>
            ReadDailySnapshotStoreAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, DailySnapshotFileName);
            if (!File.Exists(path))
                return new PersistedDailySnapshotStore();

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                return JsonSerializer.Deserialize<PersistedDailySnapshotStore>(json) ??
                       new PersistedDailySnapshotStore();
            }
            catch (OperationCanceledException) { throw; }
            catch { return new PersistedDailySnapshotStore(); }
        }

        private static async Task WriteAuditFileAtomicallyAsync(
            string path,
            string json,
            CancellationToken cancellationToken)
        {
            var temporaryPath = path + ".tmp";

            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    cancellationToken);

                File.Move(
                    temporaryPath,
                    path,
                    overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        private static async Task<IReadOnlyList<PersistedDailyActivitySnapshot>>
            ReadDailySnapshotsAsync(DateTime day, CancellationToken cancellationToken)
        {
            await DailySnapshotGate.WaitAsync(cancellationToken);
            try
            {
                var store = await ReadDailySnapshotStoreAsync(cancellationToken);
                var key = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return store.Days.TryGetValue(key, out var snapshots)
                    ? snapshots.Values.ToList()
                    : Array.Empty<PersistedDailyActivitySnapshot>();
            }
            finally { DailySnapshotGate.Release(); }
        }

        private static async Task SaveDailySnapshotsAsync(
            DateTime day,
            IReadOnlyList<DailyProgressActivityItem> currentItems,
            CancellationToken cancellationToken)
        {
            await DailySnapshotGate.WaitAsync(cancellationToken);
            try
            {
                var store = await ReadDailySnapshotStoreAsync(cancellationToken);
                var key = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!store.Days.TryGetValue(key, out var snapshots))
                {
                    snapshots = new Dictionary<string, PersistedDailyActivitySnapshot>(
                        StringComparer.OrdinalIgnoreCase);
                    store.Days[key] = snapshots;
                }

                var capturedAt = DateTimeOffset.UtcNow;
                var localNow = DateTime.Now;
                var shouldFinalize =
                    day.Date < DateTime.Today ||
                    (day.Date == DateTime.Today &&
                     localNow.TimeOfDay >= TimeSpan.FromHours(18.5));

                foreach (var item in currentItems.Where(item => !item.IsHistoricalSnapshot))
                {
                    // Una fecha cerrada se congela en la primera fotografía.
                    // Consultarla después no debe reescribirla con el estado
                    // actual de una página que cambió posteriormente.
                    if (snapshots.TryGetValue(item.PageId, out var existing) &&
                        (existing.IsFinal || day.Date < DateTime.Today))
                    {
                        continue;
                    }

                    snapshots[item.PageId] = new PersistedDailyActivitySnapshot
                    {
                        PageId = item.PageId,
                        ReportDate = day.Date,
                        PageUrl = item.PageUrl,
                        Title = item.Source.Title,
                        Person = item.Source.Person,
                        Status = item.Source.Status,
                        Project = item.Source.Project,
                        Start = item.Start,
                        End = item.End,
                        OriginalScheduledDate = item.OriginalScheduledDate,
                        CurrentScheduledDate = item.CurrentScheduledDate,
                        MoveCount = item.MoveCount,
                        RouteDates = item.RouteDates.ToList(),
                        ChecklistScanned = item.ChecklistScanned,
                        ChecklistTotal = item.ChecklistTotal,
                        ChecklistCompleted = item.ChecklistCompleted,
                        TodayChecklistCompleted = item.TodayChecklistCompleted,
                        IsLagging = item.IsLagging,
                        IsReviewMovement = item.IsReviewMovement,
                        IsCompletedMovement = item.IsCompletedMovement,
                        ProgressMinutes = item.ProgressMinutes,
                        CapturedAtUtc = capturedAt,
                        IsFinal = shouldFinalize,
                        ClosedAtUtc = shouldFinalize
                            ? capturedAt
                            : null,
                        ReviewState = item.Source.ReviewState,
                        ReviewSubmittedAt = item.Source.ReviewSubmittedAt,
                        ReviewUpdatedAt = item.Source.ReviewUpdatedAt
                    };
                }

                // Conserva cuatro meses de auditoría local sin crecimiento
                // indefinido. El cron/backend y el reporte semanal permanecen
                // deliberadamente fuera de este cierre.
                var retentionStart = day.Date.AddDays(-120);
                foreach (var oldKey in store.Days.Keys
                             .Where(value =>
                                 DateTime.TryParseExact(
                                     value,
                                     "yyyy-MM-dd",
                                     CultureInfo.InvariantCulture,
                                     DateTimeStyles.None,
                                     out var storedDay) &&
                                 storedDay.Date < retentionStart)
                             .ToList())
                {
                    store.Days.Remove(oldKey);
                }

                var json = JsonSerializer.Serialize(store);
                await WriteAuditFileAtomicallyAsync(
                    Path.Combine(ApplicationData.Current.LocalFolder.Path, DailySnapshotFileName),
                    json,
                    cancellationToken);
            }
            finally { DailySnapshotGate.Release(); }
        }

        public async Task<WeeklyProgressSnapshot> BuildWeeklyAuditAsync(
            DateTime anchorDay,
            CancellationToken cancellationToken = default)
        {
            var anchor = anchorDay.Date;
            var mondayOffset = ((int)anchor.DayOfWeek + 6) % 7;
            var weekStart = anchor.AddDays(-mondayOffset);
            var weekEnd = weekStart.AddDays(6);
            var auditedEnd = weekEnd < DateTime.Today ? weekEnd : DateTime.Today;

            PersistedDailySnapshotStore store;
            await DailySnapshotGate.WaitAsync(cancellationToken);
            try
            {
                store = await ReadDailySnapshotStoreAsync(cancellationToken);
            }
            finally
            {
                DailySnapshotGate.Release();
            }

            var missingDays = new List<DateTime>();
            var items = new List<WeeklyProgressActivityItem>();

            for (var day = weekStart; day <= auditedEnd; day = day.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!store.Days.TryGetValue(key, out var daySnapshots))
                {
                    missingDays.Add(day);
                    continue;
                }

                foreach (var saved in daySnapshots.Values)
                {
                    var reportDate = saved.ReportDate == default ? day : saved.ReportDate.Date;
                    var start = saved.Start;
                    var scheduled = Math.Max(0, (int)Math.Round((saved.End - start).TotalMinutes));
                    var original = saved.OriginalScheduledDate == default
                        ? start.Date
                        : saved.OriginalScheduledDate.Date;
                    var current = saved.CurrentScheduledDate == default
                        ? start.Date
                        : saved.CurrentScheduledDate.Date;

                    foreach (var person in SplitPeople(saved.Person))
                    {
                        items.Add(new WeeklyProgressActivityItem
                        {
                            PageId = saved.PageId,
                            PageUrl = saved.PageUrl,
                            Title = saved.Title,
                            Person = person,
                            Project = saved.Project,
                            StateCode = string.IsNullOrWhiteSpace(saved.Status) ? "P" : saved.Status,
                            ReportDate = reportDate,
                            OriginalScheduledDate = original,
                            CurrentScheduledDate = current,
                            MoveCount = saved.MoveCount,
                            RouteDates = saved.RouteDates != null
                                ? saved.RouteDates.Select(value => value.Date).ToList()
                                : new List<DateTime>(),
                            ChecklistCompleted = saved.ChecklistCompleted,
                            ChecklistTotal = saved.ChecklistTotal,
                            ChecklistAdvanced = saved.TodayChecklistCompleted,
                            ScheduledMinutes = scheduled,
                            ProgressMinutes = Math.Max(0, saved.ProgressMinutes),
                            IsLagging = saved.IsLagging,
                            IsReviewMovement = saved.IsReviewMovement,
                            IsCompletedMovement = saved.IsCompletedMovement,
                            IsFinal = saved.IsFinal
                        });
                    }
                }
            }

            WeeklyProgressPersonSnapshot Summarize(string name, List<WeeklyProgressActivityItem> source)
            {
                var scheduled = source.Sum(item => item.ScheduledMinutes);
                var progress = source.Sum(item => item.ProgressMinutes);
                return new WeeklyProgressPersonSnapshot
                {
                    Name = name,
                    ActivityCount = source.Select(item => item.PageId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    LaggingCount = source.Count(item => item.IsLagging),
                    ReviewCount = source.Count(item => item.IsReviewMovement),
                    CompletedCount = source.Count(item => item.IsCompletedMovement),
                    NoProgressCount = source.Count(item => !item.HasProgress && !item.IsFinal),
                    MovedCount = source.Where(item => item.MoveCount > 0).Select(item => item.PageId)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    ChecklistAdvanced = source.Sum(item => item.ChecklistAdvanced),
                    ScheduledMinutes = scheduled,
                    ProgressMinutes = progress,
                    CoveragePercentage = scheduled > 0
                        ? Math.Clamp((int)Math.Round(progress * 100d / scheduled), 0, 100)
                        : 0,
                    Items = source.OrderBy(item => item.ReportDate).ThenBy(item => item.Title).ToList()
                };
            }

            var people = items.GroupBy(item => item.Person, StringComparer.OrdinalIgnoreCase)
                .Select(group => Summarize(group.Key, group.ToList()))
                .OrderBy(person => Array.FindIndex(KnownPersonOrder,
                    known => string.Equals(known, person.Name, StringComparison.OrdinalIgnoreCase)) is var index && index >= 0
                        ? index : int.MaxValue)
                .ThenBy(person => person.Name)
                .ToList();
            var scheduledTotal = people.Sum(person => person.ScheduledMinutes);
            var progressTotal = people.Sum(person => person.ProgressMinutes);

            return new WeeklyProgressSnapshot
            {
                WeekStart = weekStart,
                WeekEnd = weekEnd,
                MissingDays = missingDays,
                People = people,
                ActivityCount = items.Select(item => item.PageId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                LaggingCount = items.Count(item => item.IsLagging),
                ReviewCount = items.Count(item => item.IsReviewMovement),
                CompletedCount = items.Count(item => item.IsCompletedMovement),
                NoProgressCount = items.Count(item => !item.HasProgress && !item.IsFinal),
                MovedCount = items.Where(item => item.MoveCount > 0).Select(item => item.PageId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ChecklistAdvanced = items.Sum(item => item.ChecklistAdvanced),
                ScheduledMinutes = scheduledTotal,
                ProgressMinutes = progressTotal,
                CoveragePercentage = scheduledTotal > 0
                    ? Math.Clamp((int)Math.Round(progressTotal * 100d / scheduledTotal), 0, 100)
                    : 0,
                DataNote = missingDays.Count == 0
                    ? "Reporte semanal local · fotografías disponibles"
                    : $"Reporte semanal parcial · faltan {missingDays.Count} fotografía(s)"
            };
        }

        private static NotionCalendarActivity RestoreSnapshotActivity(
            PersistedDailyActivitySnapshot snapshot) => new()
            {
                PageId = snapshot.PageId,
                PageUrl = snapshot.PageUrl,
                Title = snapshot.Title,
                Person = snapshot.Person,
                Status = snapshot.Status,
                Project = snapshot.Project,
                Start = snapshot.Start,
                End = snapshot.End,
                ChecklistScanned = snapshot.ChecklistScanned,
                ChecklistTotal = snapshot.ChecklistTotal,
                ChecklistCompleted = snapshot.ChecklistCompleted,
                ReviewState = snapshot.ReviewState,
                ReviewSubmittedAt = snapshot.ReviewSubmittedAt,
                ReviewUpdatedAt = snapshot.ReviewUpdatedAt
            };

        public async Task<DailyProgressSnapshot> BuildAsync(
            NotionCalendarService calendarService,
            string token,
            DateTime day,
            bool forceRefresh = false,
            bool requireFreshDay = false,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (calendarService == null)
                throw new ArgumentNullException(nameof(calendarService));

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "Configura primero el token de Notion.");

            day = day.Date;

            progress?.Report(
                "Leyendo el día desde la caché del calendario…");

            var cached =
                requireFreshDay
                    ? null
                    : await calendarService.TryGetCachedDayAsync(
                        day,
                        cancellationToken);

            var fromCache =
                cached != null;

            IReadOnlyList<NotionCalendarActivity> activities;

            if (cached != null)
            {
                activities = cached;
            }
            else
            {
                progress?.Report(
                    forceRefresh
                        ? "Actualizando actividades del día…"
                        : "El día no estaba en caché · cargándolo una sola vez…");

                activities =
                    await calendarService.GetDayAsync(
                        token,
                        day,
                        progress: null,
                        cancellationToken: cancellationToken,
                        forceRefresh: requireFreshDay);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var operational =
                activities
                    .Where(activity =>
                        activity != null &&
                        !activity.IsReviewMirror &&
                        !string.IsNullOrWhiteSpace(activity.PageId))
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(activity => activity.Start)
                    .ThenBy(activity => activity.Title)
                    .ToList();

            // La fuente principal de H es el detalle actual de los bloques
            // to_do. El servicio conserva el desglose por last_edited_time;
            // el baseline permanece únicamente como respaldo/auditoría.
            progress?.Report(
                "Leyendo fechas de edición de las checklists…");

            var checklistRows =
                await Task.WhenAll(
                    operational.Select(async activity =>
                        new
                        {
                            activity.PageId,
                            Stats = await calendarService.GetChecklistStatsAsync(
                                token,
                                activity.PageId,
                                cancellationToken,
                                forceRefresh &&
                                calendarService.LastChangedPageIds.Contains(
                                    activity.PageId,
                                    StringComparer.OrdinalIgnoreCase))
                        }));

            var checklistByPage =
                checklistRows.ToDictionary(
                    item => item.PageId,
                    item => item.Stats,
                    StringComparer.OrdinalIgnoreCase);

            // Avance Diario trabaja con una ventana fija 06:00 → 00:00.
            // Para días pasados el corte es medianoche; para hoy se usa la
            // hora real. Antes de las 06:00 todavía no existe avance del día.
            var referenceTime =
                day == DateTime.Today
                    ? DateTime.Now
                    : day < DateTime.Today
                        ? GetDailyProgressWindowEnd(day).AddTicks(-1)
                        : GetDailyProgressWindowStart(day);

            progress?.Report(
                "Preparando seguimiento de cambios del día…");

            var tracking =
                await PrepareTrackingContextAsync(
                    day,
                    operational,
                    cancellationToken);

            progress?.Report(
                "Leyendo movimientos reales hechos por ANFETA…");

            var movementHistory =
                await ReadCalendarMoveHistoryAsync(
                    cancellationToken);

            progress?.Report(
                $"Clasificando {operational.Count} actividades con reglas V2…");

            var baseItems =
                operational
                    .Select(activity =>
                    {
                        tracking.Baselines.TryGetValue(
                            activity.PageId,
                            out var baseline);

                        var carryOver =
                            GetValidCarryOverMovement(
                                activity,
                                movementHistory);

                        checklistByPage.TryGetValue(
                            activity.PageId,
                            out var checklistStats);

                        return CreateActivityItem(
                            activity,
                            checklistStats,
                            baseline,
                            tracking.NewlyAddedPageIds.Contains(
                                activity.PageId),
                            referenceTime,
                            day,
                            carryOver);
                    })
                    .ToList();

            // Guarda la fotografía antes de mezclar históricos. Si mañana la
            // actividad cambia de fecha o estado, este registro permanece.
            await SaveDailySnapshotsAsync(
                day,
                baseItems,
                cancellationToken);

            var savedSnapshots =
                await ReadDailySnapshotsAsync(day, cancellationToken);

            var currentPageIds =
                baseItems.Select(item => item.PageId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var saved in savedSnapshots.Where(item =>
                         !currentPageIds.Contains(item.PageId)))
            {
                var restored = RestoreSnapshotActivity(saved);
                var completedByDate = new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] =
                        saved.TodayChecklistCompleted
                };

                baseItems.Add(
                    CreateActivityItem(
                        restored,
                        new NotionChecklistStats(
                            saved.ChecklistTotal,
                            saved.ChecklistCompleted,
                            completedByDate),
                        baseline: null,
                        baselineWasCreatedNow: true,
                        referenceTime: referenceTime,
                        selectedDay: day,
                        carryOverMovement: null,
                        historicalSnapshot: saved));
            }

            var people =
                BuildPeople(
                    baseItems);

            var unique =
                baseItems
                    .GroupBy(
                        item => item.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            // Los KPIs ejecutivos evalúan trabajo ASIGNADO.
            // "Sin asignar" sigue visible como tarjeta, pero no baja artificialmente
            // la cobertura del equipo ni infla su total de horas.
            var assignedUnique =
                unique
                    .Where(item =>
                        !IsOnlyUnassigned(
                            item.Person))
                    .ToList();

            var executiveAssignedUnique =
                assignedUnique
                    .Where(IsInsideExecutiveWindow)
                    .ToList();

            var trackingStarted =
                tracking.FirstCapturedAtUtc
                    .ToLocalTime();

            var snapshot =
                new DailyProgressSnapshot
                {
                    Date = day,
                    People = people,

                    CoveragePercentage =
                        CalculateCoverage(
                            executiveAssignedUnique),

                    CurrentProgressPercentage =
                        CalculateCurrentProgress(
                            executiveAssignedUnique),

                    TotalActivities =
                        unique.Count,

                    LaggingCount =
                        assignedUnique.Count(item =>
                            item.IsLagging),

                    ReviewCount =
                        assignedUnique.Count(item =>
                            item.IsReviewMovement),

                    CompletedCount =
                        assignedUnique.Count(item =>
                            item.IsCompletedMovement),

                    ProgressCount =
                        assignedUnique.Count(item =>
                            item.HasTodayMovement),

                    ScheduledMinutes =
                        GetScheduledMinutes(
                            executiveAssignedUnique),

                    ProgressMinutes =
                        GetProgressMinutes(
                            executiveAssignedUnique),

                    MissingChecklistCount =
                        assignedUnique.Count(item =>
                            item.NeedsChecklistData),

                    UnassignedCount =
                        unique.Count(item =>
                            IsOnlyUnassigned(
                                item.Person)),

                    HistoricalCount =
                        unique.Count(item => item.IsHistoricalSnapshot),

                    IncompleteChecklistCount =
                        unique.Count(item => item.HasIncompleteChecklistWarning),

                    LoadedFromCalendarCache =
                        fromCache,

                    HadTrackingBaseline =
                        tracking.HadExistingDay,

                    TrackingStartedAt =
                        trackingStarted,

                    DataNote =
                        BuildDataNote(
                            assignedUnique,
                            unique,
                            fromCache,
                            tracking)
                };

            progress?.Report(
                $"Avance listo · {snapshot.TotalActivities} actividades · " +
                $"{snapshot.People.Count} personas");

            return snapshot;
        }

        private static DailyProgressActivityItem CreateActivityItem(
            NotionCalendarActivity activity,
            NotionChecklistStats? checklistStats,
            PersistedActivityBaseline? baseline,
            bool baselineWasCreatedNow,
            DateTime referenceTime,
            DateTime selectedDay,
            PersistedCalendarMoveHistoryEntry? carryOverMovement,
            PersistedDailyActivitySnapshot? historicalSnapshot = null)
        {
            var state =
                ClassifyState(
                    activity);

            var isSuspended =
                state.Code == "SP";

            var isCompleted =
                state.Code == "Z";

            var isReview =
                state.Code == "R";

            var operationalWindowStart =
                GetDailyProgressWindowStart(selectedDay);

            var deadlineReached =
                selectedDay < DateTime.Today ||
                (selectedDay == DateTime.Today &&
                 referenceTime >= operationalWindowStart &&
                 activity.End <= referenceTime);

            // REGLA GENARO:
            // rezago usa exclusivamente el avance de la fecha seleccionada.
            // >= 33% significa que sí hubo avance; por eso rezago es < 33%.
            // No tener checklist no se interpreta como 0%; se reporta aparte.
            var hasRealChecklist =
                activity.ChecklistScanned &&
                activity.ChecklistTotal > 0;

            var hasEditedDateData =
                checklistStats?.CompletedByDate != null;

            var completedOnSelectedDay =
                hasRealChecklist
                    ? checklistStats?.GetCompletedOn(selectedDay) ?? 0
                    : 0;

            var todayChecklistPercentage =
                hasRealChecklist
                    ? Math.Clamp(
                        (int)Math.Round(
                            completedOnSelectedDay * 100d /
                            Math.Max(1, activity.ChecklistTotal)),
                        0,
                        100)
                    : 0;

            var isLagging =
                state.Code == "P" &&
                deadlineReached &&
                hasRealChecklist &&
                todayChecklistPercentage < 33;

            if (historicalSnapshot != null)
                isLagging = historicalSnapshot.IsLagging;

            var needsChecklistData =
                state.Code == "P" &&
                deadlineReached &&
                !hasRealChecklist;

            var baselineChecklistDelta =
                !baselineWasCreatedNow &&
                baseline != null &&
                hasRealChecklist
                    ? Math.Max(
                        0,
                        activity.ChecklistCompleted -
                        baseline.ChecklistCompleted)
                    : 0;

            var checklistDelta =
                hasEditedDateData
                    ? completedOnSelectedDay
                    : baselineChecklistDelta;

            var workedDelta =
                !baselineWasCreatedNow &&
                baseline != null
                    ? Math.Max(
                        0,
                        activity.WorkedMinutes -
                        baseline.WorkedMinutes)
                    : 0;

            var changedToReview =
                !baselineWasCreatedNow &&
                baseline != null &&
                !string.Equals(
                    baseline.StateCode,
                    "R",
                    StringComparison.OrdinalIgnoreCase) &&
                state.Code == "R";

            // ReviewSubmittedAt/UpdatedAt sí trae timestamp real.
            // Esto permite recuperar un R reciente incluso en el primer baseline.
            var reviewTimestampMovement =
                state.Code == "R" &&
                HasReviewMovementInWindow(
                    activity,
                    selectedDay,
                    referenceTime);

            var isReviewMovement =
                changedToReview ||
                reviewTimestampMovement;

            if (historicalSnapshot != null)
                isReviewMovement = historicalSnapshot.IsReviewMovement;

            // Z no tiene timestamp confiable en el modelo actual.
            // Solo lo afirmamos cuando ANFETA observó la transición.
            var isCompletedMovement =
                !baselineWasCreatedNow &&
                baseline != null &&
                !string.Equals(
                    baseline.StateCode,
                    "Z",
                    StringComparison.OrdinalIgnoreCase) &&
                state.Code == "Z";

            // Un flujo aprobado sí aporta un timestamp real para Z. Para una
            // Z manual sin metadata solo vale la transición observada.
            var approvedTimestampMovement =
                state.Code == "Z" &&
                activity.IsApprovedReview &&
                IsTimestampInWindow(
                    activity.ReviewUpdatedAt,
                    selectedDay,
                    referenceTime);

            isCompletedMovement =
                isCompletedMovement ||
                approvedTimestampMovement;

            if (historicalSnapshot != null)
                isCompletedMovement = historicalSnapshot.IsCompletedMovement;

            var observedMovement =
                checklistDelta > 0 ||
                workedDelta > 0 ||
                isReviewMovement ||
                isCompletedMovement;

            // Progreso ACTUAL disponible para RTUZ/ZREVISION aunque ANFETA no
            // haya presenciado la transición a revisión en esta ejecución.
            var currentProgressRatio =
                CalculateActivityProgressRatio(
                    activity,
                    state.Code);

            // REGLA 20/08:
            // Avance Hoy NO puede salir del porcentaje histórico actual.
            // Solo cuenta algo que ANFETA haya podido observar dentro del día:
            // delta de checklist, delta de tiempo o transición R/Z de hoy.
            // Una actividad puede seguir rezagada y aun así haber avanzado hoy,
            // por lo que ambos indicadores pueden coexistir.
            var hasTodayMovement =
                !isSuspended &&
                (observedMovement ||
                 (isReview && currentProgressRatio > 0d));

            var duration =
                activity.End > activity.Start
                    ? activity.End - activity.Start
                    : TimeSpan.FromMinutes(15);

            var scheduledMinutes =
                Math.Max(
                    1,
                    (int)Math.Round(
                        duration.TotalMinutes));

            var checklistTodayRatio =
                hasRealChecklist &&
                checklistDelta > 0
                    ? Math.Clamp(
                        checklistDelta * 1d /
                        Math.Max(1, activity.ChecklistTotal),
                        0d,
                        1d)
                    : 0d;

            var workedDenominator =
                activity.EstimatedWorkMinutes > 0
                    ? activity.EstimatedWorkMinutes
                    : scheduledMinutes;

            var workedTodayRatio =
                workedDelta > 0
                    ? Math.Clamp(
                        workedDelta * 1d /
                        Math.Max(1, workedDenominator),
                        0d,
                        1d)
                    : 0d;

            // Llegar hoy a R o Z sí es un hito diario observable. Para la
            // cobertura del día se considera completado el bloque agendado.
            var transitionTodayRatio =
                isReviewMovement ||
                isCompletedMovement
                    ? 1d
                    : 0d;

            // RTUZ/ZREVISION representa trabajo operativo real aun cuando la
            // transición ocurrió antes de abrir el reporte. En ese estado se
            // usa su porcentaje actual (checklist o tiempo) y no se espera a Z.
            var reviewCurrentRatio =
                isReview
                    ? currentProgressRatio
                    : 0d;

            var todayProgressRatio =
                hasTodayMovement
                    ? Math.Max(
                        reviewCurrentRatio > 0d
                            ? reviewCurrentRatio
                            : transitionTodayRatio,
                        Math.Max(
                            checklistTodayRatio,
                            workedTodayRatio))
                    : 0d;

            var progressMinutes =
                isSuspended
                    ? 0
                    : Math.Clamp(
                        (int)Math.Round(
                            scheduledMinutes *
                            todayProgressRatio),
                        0,
                        scheduledMinutes);

            if (historicalSnapshot != null)
                progressMinutes = historicalSnapshot.ProgressMinutes;

            var todayMovementLabel =
                BuildMovementLabel(
                    checklistDelta,
                    workedDelta,
                    isReviewMovement,
                    isCompletedMovement);

            var carryOverLabel =
                BuildCarryOverMovementLabel(
                    carryOverMovement);

            var movementLabel =
                string.Join(
                    " · ",
                    new[]
                    {
                        carryOverLabel,
                        todayMovementLabel
                    }
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value)));

            return new DailyProgressActivityItem
            {
                Source = activity,
                FullTitle =
                    activity.Title ??
                    string.Empty,
                ShortTitle =
                    BuildShortTitle(
                        activity.Title),
                Domain =
                    ExtractDomain(
                        activity.Title),
                Person =
                    ResolveProgressPerson(
                        activity,
                        isReview),

                StateCode =
                    state.Code,
                StateLabel =
                    state.Label,

                IsLagging =
                    isLagging,
                NeedsChecklistData =
                    needsChecklistData,

                // Compatibilidad visual: en V2 "visible" ya significa movimiento
                // detectado en el día.
                HasVisibleProgress =
                    hasTodayMovement,
                HasTodayMovement =
                    hasTodayMovement,
                IsReviewMovement =
                    isReviewMovement,
                IsCompletedMovement =
                    isCompletedMovement,

                IsReview =
                    isReview,
                IsCompleted =
                    isCompleted,
                IsSuspended =
                    isSuspended,

                IsHistoricalSnapshot =
                    historicalSnapshot != null,
                HistoricalCapturedAt =
                    historicalSnapshot?.CapturedAtUtc.ToLocalTime(),
                OriginalScheduledDate =
                    historicalSnapshot?.OriginalScheduledDate != default
                        ? historicalSnapshot!.OriginalScheduledDate.Date
                        : NormalizeMovementRoute(carryOverMovement)
                            .FirstOrDefault(activity.Start.Date),
                CurrentScheduledDate =
                    historicalSnapshot?.CurrentScheduledDate != default
                        ? historicalSnapshot!.CurrentScheduledDate.Date
                        : activity.Start.Date,
                MoveCount =
                    historicalSnapshot?.MoveCount ??
                    carryOverMovement?.MoveCount ?? 0,
                RouteDates =
                    historicalSnapshot?.RouteDates?.Count > 0
                        ? historicalSnapshot.RouteDates
                            .Select(value => value.Date)
                            .ToList()
                        : NormalizeMovementRoute(carryOverMovement),
                HasIncompleteChecklistWarning =
                    (isReview || isCompleted) &&
                    hasRealChecklist &&
                    activity.ChecklistCompleted < activity.ChecklistTotal,

                TodayChecklistCompleted =
                    completedOnSelectedDay,
                TodayChecklistTotal =
                    hasRealChecklist
                        ? activity.ChecklistTotal
                        : 0,
                TodayChecklistPercentage =
                    todayChecklistPercentage,

                ChecklistDeltaToday =
                    checklistDelta,
                WorkedMinutesDeltaToday =
                    workedDelta,
                MovementLabel =
                    movementLabel,

                ScheduledMinutes =
                    scheduledMinutes,
                ProgressMinutes =
                    progressMinutes,
                ProgressPercentage =
                    Math.Clamp(
                        (int)Math.Round(
                            currentProgressRatio * 100d),
                        0,
                        100)
            };
        }

        private static string ResolveProgressPerson(
            NotionCalendarActivity activity,
            bool isReview)
        {
            var current = string.IsNullOrWhiteSpace(activity.Person)
                ? "Sin asignar"
                : CanonicalFeedPerson(activity.Person.Trim());

            if (!isReview)
                return current;

            var original = CanonicalFeedPerson(activity.OriginalPerson);
            if (!string.IsNullOrWhiteSpace(original) &&
                !string.Equals(original, "Sin asignar", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(original, current, StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }

            // Cuando RTUZ se aplica directamente en Notion, el responsable
            // puede cambiar al revisor antes de que ANFETA alcance a guardar
            // OriginalPerson. El título conserva la ruta de tags; el último es
            // el responsable actual y el anterior distinto es quien ejecutó.
            var tagPeople = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["jjohn"] = "John", ["john"] = "John",
                ["kkarl"] = "Karla", ["karl"] = "Karla",
                ["iisai"] = "Isaias", ["isai"] = "Isaias",
                ["ssote"] = "Sotelo", ["sote"] = "Sotelo",
                ["eedua"] = "Sotelo", ["edua"] = "Sotelo",
                ["aacal"] = "Acalli", ["acal"] = "Acalli",
                ["aandr"] = "Andrade", ["andr"] = "Andrade",
                ["eemma"] = "Emmanuel", ["emma"] = "Emmanuel",
                ["bbria"] = "Brian", ["bria"] = "Brian",
                ["ggena"] = "Genaro", ["gena"] = "Genaro",
                ["nneft"] = "Neftali", ["neft"] = "Neftali"
            };

            var matches = Regex.Matches(
                activity.Title ?? string.Empty,
                @"(?<![\p{L}\p{Nd}_])(?<tag>[a-z]{4,5})\d*(?![\p{L}\p{Nd}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            for (var index = matches.Count - 1; index >= 0; index--)
            {
                var tag = matches[index].Groups["tag"].Value;
                if (tagPeople.TryGetValue(tag, out var person) &&
                    !string.Equals(person, current, StringComparison.OrdinalIgnoreCase))
                {
                    return person;
                }
            }

            return current;
        }

        private static IReadOnlyList<DailyProgressPersonSnapshot>
            BuildPeople(
                IReadOnlyList<DailyProgressActivityItem> items)
        {
            var expanded =
                new List<(string Person, DailyProgressActivityItem Item)>();

            foreach (var item in items)
            {
                var people =
                    SplitPeople(
                        item.Person);

                if (people.Count == 0)
                    people.Add("Sin asignar");

                foreach (var person in people)
                {
                    expanded.Add(
                        (CanonicalFeedPerson(person), item));
                }
            }

            return expanded
                .GroupBy(
                    row =>
                        CanonicalFeedPerson(
                            row.Person),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var all =
                        group
                            .Select(row => row.Item)
                            .GroupBy(
                                item => item.PageId,
                                StringComparer.OrdinalIgnoreCase)
                            .Select(page => page.First())
                            .OrderBy(item => item.Start)
                            .ToList();

                    var active =
                        all
                            .Where(item =>
                                !item.IsSuspended)
                            .ToList();

                    var executiveActive =
                        active
                            .Where(IsInsideExecutiveWindow)
                            .ToList();

                    var lagging =
                        active
                            .Where(item =>
                                item.IsLagging)
                            .OrderBy(item =>
                                item.ChecklistPercentage)
                            .ThenBy(item =>
                                item.Start)
                            .ToList();

                    var progress =
                        active
                            .Where(item =>
                                item.HasTodayMovement)
                            .OrderByDescending(item =>
                                item.IsCompletedMovement)
                            .ThenByDescending(item =>
                                item.IsReviewMovement)
                            .ThenByDescending(item =>
                                item.ChecklistDeltaToday)
                            .ThenBy(item =>
                                item.Start)
                            .ToList();

                    var name =
                        CanonicalFeedPerson(
                            group.Key);

                    return new DailyProgressPersonSnapshot
                    {
                        Name =
                            name,

                        Initial =
                            string.IsNullOrWhiteSpace(name)
                                ? "?"
                                : name.Substring(
                                    0,
                                    1)
                                    .ToUpperInvariant(),

                        CoveragePercentage =
                            CalculateCoverage(
                                executiveActive),

                        CurrentProgressPercentage =
                            CalculateCurrentProgress(
                                executiveActive),

                        ScheduledMinutes =
                            GetScheduledMinutes(
                                executiveActive),

                        ProgressMinutes =
                            GetProgressMinutes(
                                executiveActive),

                        Lagging =
                            lagging,

                        Progress =
                            progress,

                        AllActivities =
                            all,

                        // En V2 son movimientos del día.
                        ReviewCount =
                            active.Count(item =>
                                item.IsReviewMovement),

                        CompletedCount =
                            active.Count(item =>
                                item.IsCompletedMovement),

                        PendingCount =
                            active.Count(item =>
                                item.StateCode == "P"),

                        MissingChecklistCount =
                            active.Count(item =>
                                item.NeedsChecklistData),

                        HistoricalCount =
                            all.Count(item => item.IsHistoricalSnapshot),

                        IncompleteChecklistCount =
                            all.Count(item => item.HasIncompleteChecklistWarning)
                    };
                })
                .OrderBy(person =>
                {
                    var index =
                        Array.FindIndex(
                            KnownPersonOrder,
                            known =>
                                string.Equals(
                                    known,
                                    person.Name,
                                    StringComparison.OrdinalIgnoreCase));

                    return index < 0
                        ? int.MaxValue
                        : index;
                })
                .ThenBy(person =>
                    person.Name)
                .ToList();
        }

        private static int CalculateCoverage(
            IReadOnlyList<DailyProgressActivityItem> items)
        {
            var active =
                (items ??
                 Array.Empty<DailyProgressActivityItem>())
                    .Where(item =>
                        !item.IsSuspended)
                    .ToList();

            var scheduled =
                GetScheduledMinutes(
                    active);

            if (scheduled <= 0)
                return 0;

            var progress =
                GetProgressMinutes(
                    active);

            return Math.Clamp(
                (int)Math.Round(
                    progress * 100d /
                    scheduled),
                0,
                100);
        }

        private static bool IsInsideExecutiveWindow(
            DailyProgressActivityItem item)
        {
            var day = item.Start.Date;
            var windowStart = day
                .AddHours(ExecutiveWindowStartHour)
                .AddMinutes(ExecutiveWindowStartMinute);
            var windowEnd = day.AddHours(ExecutiveWindowEndHour);

            return item.End > windowStart && item.Start < windowEnd;
        }

        private static int CalculateCurrentProgress(
            IReadOnlyList<DailyProgressActivityItem> items)
        {
            var active = (items ?? Array.Empty<DailyProgressActivityItem>())
                .Where(item => !item.IsSuspended)
                .ToList();

            var totalWeight = active.Sum(item => Math.Max(1, item.ScheduledMinutes));
            if (totalWeight <= 0)
                return 0;

            var weighted = active.Sum(item =>
                Math.Max(1, item.ScheduledMinutes) *
                Math.Clamp(item.ProgressPercentage, 0, 100));

            return Math.Clamp(
                (int)Math.Round(weighted * 1d / totalWeight),
                0,
                100);
        }

        private static int GetScheduledMinutes(
            IReadOnlyList<DailyProgressActivityItem> items)
        {
            return (items ??
                    Array.Empty<DailyProgressActivityItem>())
                .Where(item =>
                    !item.IsSuspended)
                .Sum(item =>
                    Math.Max(
                        0,
                        item.ScheduledMinutes));
        }

        private static int GetProgressMinutes(
            IReadOnlyList<DailyProgressActivityItem> items)
        {
            return (items ??
                    Array.Empty<DailyProgressActivityItem>())
                .Where(item =>
                    !item.IsSuspended)
                .Sum(item =>
                    Math.Max(
                        0,
                        item.ProgressMinutes));
        }

        private static double CalculateActivityProgressRatio(
            NotionCalendarActivity activity,
            string stateCode)
        {
            // Si existe checklist real, manda sobre cualquier interpretación.
            // Así un R 13/15 vale 87%, justo como pidió la referencia.
            if (activity.ChecklistScanned &&
                activity.ChecklistTotal > 0)
            {
                return Math.Clamp(
                    activity.ChecklistCompleted * 1d /
                    activity.ChecklistTotal,
                    0d,
                    1d);
            }

            // Si ANFETA tiene tiempo trabajado persistido, es la segunda fuente.
            if (activity.EstimatedWorkMinutes > 0)
            {
                return Math.Clamp(
                    activity.WorkedMinutes * 1d /
                    activity.EstimatedWorkMinutes,
                    0d,
                    1d);
            }

            // Sin checklist ni worklog:
            // R y Z sí prueban que la actividad avanzó operativamente.
            if (stateCode is "R" or "Z")
                return 1d;

            return 0d;
        }

        private static (string Code, string Label)
            ClassifyState(
                NotionCalendarActivity activity)
        {
            var title =
                activity.Title ??
                string.Empty;

            var status =
                Normalize(
                    activity.Status);

            // Primero el token más específico.
            if (HasStateToken(
                    title,
                    "sprtuzrevision") ||
                status.Contains("suspend"))
            {
                return ("SP", "Suspendida");
            }

            if (HasStateToken(
                    title,
                    "prtuzrevision") ||
                status.Contains("prtuz por hacer"))
            {
                return ("P", "Pendiente");
            }

            if (HasStateToken(
                    title,
                    "zrevision") ||
                activity.IsApprovedReview ||
                activity.IsCompletedForReview ||
                status.Contains("terminado") ||
                status.Contains("finalizado") ||
                status.Contains("completado"))
            {
                return ("Z", "Terminada");
            }

            if (HasStateToken(
                    title,
                    "rtuzrevision") ||
                activity.IsPendingReview ||
                status.Contains("revisar revisiones"))
            {
                return ("R", "Revisión");
            }

            return ("P", "Pendiente");
        }

        private static bool HasStateToken(
            string title,
            string token)
        {
            return Regex.IsMatch(
                title ?? string.Empty,
                $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(token)}(?![\p{{L}}\p{{Nd}}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private static bool HasReviewMovementInWindow(
            NotionCalendarActivity activity,
            DateTime selectedDay,
            DateTime referenceTime)
        {
            var timestamps =
                new[]
                {
                    activity.ReviewSubmittedAt,
                    activity.ReviewUpdatedAt
                }
                .Where(value =>
                    value.HasValue)
                .Select(value =>
                    value!.Value.ToLocalTime())
                .ToList();

            if (timestamps.Count == 0)
                return false;

            var localWindowStart =
                GetDailyProgressWindowStart(selectedDay);

            var localWindowEnd =
                GetDailyProgressWindowEnd(selectedDay);

            var windowStart =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        localWindowStart,
                        DateTimeKind.Local));

            var windowEnd =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        localWindowEnd,
                        DateTimeKind.Local));

            var reference =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        referenceTime,
                        DateTimeKind.Local));

            // Antes de las 06:00 no existe un movimiento válido para
            // "hoy". Después de medianoche tampoco se mezcla con este día.
            if (reference < windowStart)
                return false;

            var effectiveEnd =
                reference < windowEnd
                    ? reference
                    : windowEnd;

            return timestamps.Any(timestamp =>
                timestamp >= windowStart &&
                timestamp < windowEnd &&
                timestamp <= effectiveEnd);
        }

        private static bool IsTimestampInWindow(
            DateTimeOffset? timestamp,
            DateTime selectedDay,
            DateTime referenceTime)
        {
            if (!timestamp.HasValue)
                return false;

            var local = timestamp.Value.ToLocalTime();
            var start = new DateTimeOffset(DateTime.SpecifyKind(
                GetDailyProgressWindowStart(selectedDay), DateTimeKind.Local));
            var end = new DateTimeOffset(DateTime.SpecifyKind(
                GetDailyProgressWindowEnd(selectedDay), DateTimeKind.Local));
            var reference = new DateTimeOffset(DateTime.SpecifyKind(
                referenceTime, DateTimeKind.Local));

            if (reference < start)
                return false;

            var effectiveEnd = reference < end ? reference : end;
            return local >= start && local < end && local <= effectiveEnd;
        }

        private static string BuildMovementLabel(
            int checklistDelta,
            int workedDelta,
            bool reviewMovement,
            bool completedMovement)
        {
            var parts =
                new List<string>();

            if (checklistDelta > 0)
            {
                parts.Add(
                    $"+{checklistDelta} check" +
                    (checklistDelta == 1 ? "" : "s") +
                    " hoy");
            }

            if (workedDelta > 0)
            {
                parts.Add(
                    $"+{FormatMinutesCompact(workedDelta)} trabajados");
            }

            if (reviewMovement)
                parts.Add("pasó a R hoy");

            if (completedMovement)
                parts.Add("pasó a Z hoy");

            // Sin movimiento observado no agregamos ninguna etiqueta.
            // El estado/checklist histórico puede seguir mostrándose en la card,
            // pero nunca se presenta como trabajo realizado hoy.
            return string.Join(
                " · ",
                parts);
        }

        private static string FormatMinutesCompact(
            int minutes)
        {
            minutes =
                Math.Max(
                    0,
                    minutes);

            var hours =
                minutes / 60;

            var remainder =
                minutes % 60;

            if (hours > 0 &&
                remainder > 0)
            {
                return $"{hours}H {remainder}M";
            }

            if (hours > 0)
                return $"{hours}H";

            return $"{remainder}M";
        }

        private static bool IsOnlyUnassigned(
            string? people)
        {
            var split =
                SplitPeople(
                    people);

            return split.Count == 0 ||
                   split.All(person =>
                       string.Equals(
                           person,
                           "Sin asignar",
                           StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<TrackingContext>
            PrepareTrackingContextAsync(
                DateTime day,
                IReadOnlyList<NotionCalendarActivity> activities,
                CancellationToken cancellationToken)
        {
            await TrackingGate.WaitAsync(
                cancellationToken);

            try
            {
                var store =
                    await ReadTrackingStoreAsync(
                        cancellationToken);

                var dateKey =
                    day.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

                var hadExistingDay =
                    store.Days.TryGetValue(
                        dateKey,
                        out var dayTracking);

                // La jornada operativa empieza a las 06:00. Si ANFETA fue
                // abierta entre 00:00 y 05:59, ese baseline NO puede cruzar
                // a la jornada nueva. En la primera lectura desde las 06:00
                // se vuelve a capturar el estado actual y el contador parte
                // de cero. Mientras todavía sean menos de las 06:00, cada
                // lectura mantiene el baseline al día para no acumular cambios
                // nocturnos que después parezcan trabajo de hoy.
                var localNow =
                    DateTime.Now;

                var operationalWindowStart =
                    GetDailyProgressWindowStart(day);

                var operationalWindowEnd =
                    GetDailyProgressWindowEnd(day);

                var shouldResetForOperationalWindow =
                    false;

                if (dayTracking != null &&
                    day.Date == DateTime.Today)
                {
                    var capturedLocal =
                        dayTracking.FirstCapturedAtUtc
                            .ToLocalTime()
                            .LocalDateTime;

                    shouldResetForOperationalWindow =
                        localNow < operationalWindowStart ||
                        capturedLocal < operationalWindowStart ||
                        capturedLocal >= operationalWindowEnd;
                }

                if (dayTracking == null ||
                    shouldResetForOperationalWindow)
                {
                    hadExistingDay = false;

                    dayTracking =
                        new PersistedDayTracking
                        {
                            DateKey =
                                dateKey,
                            FirstCapturedAtUtc =
                                DateTimeOffset.UtcNow
                        };

                    store.Days[dateKey] =
                        dayTracking;
                }

                var newlyAdded =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (var activity in activities)
                {
                    if (string.IsNullOrWhiteSpace(
                            activity.PageId))
                    {
                        continue;
                    }

                    if (dayTracking.Baselines.ContainsKey(
                            activity.PageId))
                    {
                        continue;
                    }

                    var state =
                        ClassifyState(
                            activity);

                    dayTracking.Baselines[
                        activity.PageId] =
                        new PersistedActivityBaseline
                        {
                            ChecklistCompleted =
                                Math.Max(
                                    0,
                                    activity.ChecklistCompleted),
                            ChecklistTotal =
                                Math.Max(
                                    0,
                                    activity.ChecklistTotal),
                            WorkedMinutes =
                                Math.Max(
                                    0,
                                    activity.WorkedMinutes),
                            StateCode =
                                state.Code
                        };

                    newlyAdded.Add(
                        activity.PageId);
                }

                // Conserva un historial corto; suficiente para Feed/diagnóstico.
                var cutoff =
                    day.AddDays(-14);

                foreach (var staleKey in
                         store.Days.Keys
                             .Where(key =>
                                 DateTime.TryParseExact(
                                     key,
                                     "yyyy-MM-dd",
                                     CultureInfo.InvariantCulture,
                                     DateTimeStyles.None,
                                     out var parsed) &&
                                 parsed.Date < cutoff.Date)
                             .ToList())
                {
                    store.Days.Remove(
                        staleKey);
                }

                await SaveTrackingStoreAsync(
                    store,
                    CancellationToken.None);

                return new TrackingContext
                {
                    HadExistingDay =
                        hadExistingDay,

                    FirstCapturedAtUtc =
                        dayTracking.FirstCapturedAtUtc,

                    Baselines =
                        dayTracking.Baselines,

                    NewlyAddedPageIds =
                        newlyAdded
                };
            }
            finally
            {
                TrackingGate.Release();
            }
        }

        private static async Task<PersistedTrackingStore>
            ReadTrackingStoreAsync(
                CancellationToken cancellationToken)
        {
            try
            {
                var path =
                    Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        TrackingFileName);

                if (!File.Exists(path))
                    return new PersistedTrackingStore();

                var json =
                    await File.ReadAllTextAsync(
                        path,
                        cancellationToken);

                var store =
                    JsonSerializer.Deserialize<PersistedTrackingStore>(
                        json);

                return store ??
                       new PersistedTrackingStore();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new PersistedTrackingStore();
            }
        }

        private static async Task SaveTrackingStoreAsync(
            PersistedTrackingStore store,
            CancellationToken cancellationToken)
        {
            try
            {
                var path =
                    Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        TrackingFileName);

                var json =
                    JsonSerializer.Serialize(
                        store);

                await File.WriteAllTextAsync(
                    path,
                    json,
                    cancellationToken);
            }
            catch
            {
                // El tracking mejora precisión, pero nunca debe tumbar el Feed.
            }
        }

        private static string BuildDataNote(
            IReadOnlyList<DailyProgressActivityItem> assigned,
            IReadOnlyList<DailyProgressActivityItem> all,
            bool fromCache,
            TrackingContext tracking)
        {
            var checksToday =
                assigned.Sum(item =>
                    item.ChecklistDeltaToday);

            var noChecklist =
                assigned.Count(item =>
                    item.NeedsChecklistData);

            var unassigned =
                all.Count(item =>
                    IsOnlyUnassigned(
                        item.Person));

            var source =
                fromCache
                    ? "Caché del calendario"
                    : "Día cargado desde Notion";

            if (!tracking.HadExistingDay)
            {
                return
                    $"{source} · jornada 06:00–00:00 · baseline creado " +
                    $"{tracking.FirstCapturedAtUtc.ToLocalTime():HH:mm}. " +
                    "Avance Hoy inicia en 0 y solo sube con cambios observados dentro de esta jornada " +
                    "(checklist, tiempo o transición R/Z). " +
                    $"Sin checklist verificable: {noChecklist} · sin asignar: {unassigned}.";
            }

            return
                $"{source} · jornada 06:00–00:00 · seguimiento desde " +
                $"{tracking.FirstCapturedAtUtc.ToLocalTime():HH:mm} · " +
                $"+{checksToday} checks detectados hoy · " +
                $"sin checklist verificable: {noChecklist} · " +
                $"sin asignar: {unassigned}.";
        }

        private static string ExtractDomain(
            string? title)
        {
            var match =
                DomainPattern.Match(
                    title ?? string.Empty);

            return match.Success
                ? match.Groups["domain"]
                    .Value
                    .Trim()
                    .TrimEnd('.')
                    .ToLowerInvariant()
                : "Sin dominio";
        }

        private static string BuildShortTitle(
            string? title)
        {
            var value =
                title ?? string.Empty;

            var domain =
                ExtractDomain(value);

            value =
                Regex.Replace(
                    value,
                    @"(?<![\p{L}\p{Nd}_])(?:prtuzrevision|rtuzrevision|zrevision)(?![\p{L}\p{Nd}_])",
                    " ",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            value =
                Regex.Replace(
                    value,
                    @"(?<![\p{L}\p{Nd}_])(?:wwebs|sseo|aads|aapli|pprog|ccobr|bbibl)(?![\p{L}\p{Nd}_])",
                    " ",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            value =
                Regex.Replace(
                    value,
                    @"\(\s*\d{4}[A-ZÁÉÍÓÚÑ]{3,10}\s*\)",
                    " ",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            value =
                Regex.Replace(
                    value,
                    @"(?<![\p{L}\p{Nd}_])(?:jjohn|kkarl|iisai|ssote|eedua|aacal|aandr|eemma|bbria|ggena|nneft)\d*(?![\p{L}\p{Nd}_])",
                    " ",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            if (!string.Equals(
                    domain,
                    "Sin dominio",
                    StringComparison.OrdinalIgnoreCase))
            {
                value =
                    Regex.Replace(
                        value,
                        Regex.Escape(domain),
                        " ",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);
            }

            value =
                Regex.Replace(
                    value,
                    @"\s+",
                    " ")
                .Trim(' ', '-', '—', '·');

            if (string.IsNullOrWhiteSpace(value))
                return "Actividad";

            // La vista decide cómo envolver el texto según el ancho disponible.
            // Recortarlo aquí dejaba huecos grandes dentro de las tarjetas.
            return value;
        }

        private static List<string> SplitPeople(
            string? people)
        {
            var raw =
                (people ?? string.Empty)
                    .Trim();

            if (string.IsNullOrWhiteSpace(raw) ||
                IsUnassignedVisualToken(raw))
            {
                return new List<string>
                {
                    "Sin asignar"
                };
            }

            // Preferir primero aliases conocidos completos.
            // Después, si vienen varios responsables, separar.
            var pieces =
                Regex.Split(
                    raw,
                    @"[\s,;|/]+")
                .Where(value =>
                    !string.IsNullOrWhiteSpace(value))
                .Select(CanonicalFeedPerson)
                .Where(value =>
                    !string.IsNullOrWhiteSpace(value))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pieces.Count > 1)
            {
                pieces.RemoveAll(value =>
                    string.Equals(
                        value,
                        "Sin asignar",
                        StringComparison.OrdinalIgnoreCase));
            }

            return pieces.Count > 0
                ? pieces
                : new List<string>
                {
                    "Sin asignar"
                };
        }

        private static string CanonicalFeedPerson(
            string? value)
        {
            var raw =
                (value ?? string.Empty)
                    .Trim();

            if (string.IsNullOrWhiteSpace(raw) ||
                IsUnassignedVisualToken(raw))
            {
                return "Sin asignar";
            }

            var key =
                NormalizePersonKey(
                    raw);

            if (string.IsNullOrWhiteSpace(key) ||
                key is
                    "sin" or
                    "sinasignar" or
                    "sinpersona" or
                    "ninguno" or
                    "none" or
                    "null" or
                    "unassigned")
            {
                return "Sin asignar";
            }

            // Primero exacto.
            if (FeedPersonAliases.TryGetValue(
                    key,
                    out var exact))
            {
                return exact;
            }

            // Luego tolerante: tags con sufijos/prefijos heredados.
            foreach (var alias in FeedPersonAliases)
            {
                if (key.Contains(
                        alias.Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return alias.Value;
                }
            }

            // Último respaldo: si ya es un nombre conocido, respetarlo.
            var known =
                KnownPersonOrder.FirstOrDefault(person =>
                    string.Equals(
                        NormalizePersonKey(person),
                        key,
                        StringComparison.OrdinalIgnoreCase));

            return !string.IsNullOrWhiteSpace(known)
                ? known
                : raw;
        }

        private static string NormalizePersonDisplay(
            string? value) =>
            CanonicalFeedPerson(
                value);

        private static string NormalizePersonKey(
            string value)
        {
            var normalized =
                (value ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant()
                    .Normalize(
                        NormalizationForm.FormD);

            var builder =
                new StringBuilder();

            foreach (var character in normalized)
            {
                var category =
                    CharUnicodeInfo.GetUnicodeCategory(
                        character);

                if (category ==
                    UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(
                        character);
                }
            }

            return Regex.Replace(
                builder.ToString(),
                @"\d+$",
                string.Empty);
        }

        private static bool IsUnassignedVisualToken(
            string value)
        {
            var trimmed =
                (value ?? string.Empty)
                    .Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
                return true;

            return trimmed.All(character =>
                character is
                    '-' or
                    '—' or
                    '–' or
                    '_' or
                    '·');
        }

        private static string BuildDataNote(
            IReadOnlyList<DailyProgressActivityItem> items,
            bool fromCache)
        {
            var scanned =
                items.Count(item =>
                    item.ChecklistScanned);

            return
                $"{(fromCache ? "Caché del calendario" : "Día cargado desde Notion")} · " +
                $"checklist disponible {scanned}/{items.Count}. " +
                "Este bloque muestra el estado actual; el historial exacto de checks " +
                "marcados hoy se agregará con snapshots.";
        }

        private static string Normalize(
            string? value)
        {
            var normalized =
                (value ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant()
                    .Normalize(
                        NormalizationForm.FormD);

            var builder =
                new StringBuilder();

            foreach (var character in normalized)
            {
                var category =
                    CharUnicodeInfo
                        .GetUnicodeCategory(
                            character);

                if (category ==
                    UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(
                    char.IsLetterOrDigit(character)
                        ? character
                        : ' ');
            }

            return string.Join(
                " ",
                builder
                    .ToString()
                    .Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries));
        }

    }
}
