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
    /// Feed de Avance Diario V2.
    ///
    /// Reglas acordadas:
    /// - Rezago: P/PRTUZ + terminó su bloque + checklist REAL < 50%.
    ///   Sin checklist NO se acusa como rezago; queda como dato faltante.
    /// - Cobertura: avance ponderado por minutos agendados.
    /// - R/Z del Feed: movimientos del día, no simplemente estados históricos.
    /// - Checklist "hecho hoy": delta contra un baseline persistente.
    /// - Cache-first: abrir el Feed no descarga bodies.
    /// </summary>
    public sealed class NotionDailyProgressService
    {
        private const string TrackingFileName =
            "daily_progress_tracking_v2.json";

        private static readonly SemaphoreSlim TrackingGate =
            new(1, 1);

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

        public async Task<DailyProgressSnapshot> BuildAsync(
            NotionCalendarService calendarService,
            string token,
            DateTime day,
            bool forceRefresh = false,
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
                forceRefresh
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
                        forceRefresh: forceRefresh);
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

            var referenceTime =
                day == DateTime.Today
                    ? DateTime.Now
                    : day < DateTime.Today
                        ? day.AddDays(1).AddTicks(-1)
                        : day;

            progress?.Report(
                "Preparando seguimiento de cambios del día…");

            var tracking =
                await PrepareTrackingContextAsync(
                    day,
                    operational,
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

                        return CreateActivityItem(
                            activity,
                            baseline,
                            tracking.NewlyAddedPageIds.Contains(
                                activity.PageId),
                            referenceTime,
                            day);
                    })
                    .ToList();

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
                            assignedUnique),

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
                            assignedUnique),

                    ProgressMinutes =
                        GetProgressMinutes(
                            assignedUnique),

                    MissingChecklistCount =
                        assignedUnique.Count(item =>
                            item.NeedsChecklistData),

                    UnassignedCount =
                        unique.Count(item =>
                            IsOnlyUnassigned(
                                item.Person)),

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
            PersistedActivityBaseline? baseline,
            bool baselineWasCreatedNow,
            DateTime referenceTime,
            DateTime selectedDay)
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

            var deadlineReached =
                selectedDay < DateTime.Today ||
                (selectedDay == DateTime.Today &&
                 activity.End <= referenceTime);

            // REGLA GENARO:
            // rezago solo cuando existe checklist real y no llegó al 50%.
            // No tener checklist no se interpreta como 0%; se reporta aparte.
            var hasRealChecklist =
                activity.ChecklistScanned &&
                activity.ChecklistTotal > 0;

            var isLagging =
                state.Code == "P" &&
                deadlineReached &&
                hasRealChecklist &&
                activity.ChecklistPercentage < 50;

            var needsChecklistData =
                state.Code == "P" &&
                deadlineReached &&
                !hasRealChecklist;

            var checklistDelta =
                !baselineWasCreatedNow &&
                baseline != null &&
                hasRealChecklist
                    ? Math.Max(
                        0,
                        activity.ChecklistCompleted -
                        baseline.ChecklistCompleted)
                    : 0;

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

            var observedMovement =
                checklistDelta > 0 ||
                workedDelta > 0 ||
                isReviewMovement ||
                isCompletedMovement;

            // Regla operativa de Genaro:
            // - P con menos de 50% => rezagada.
            // - P con al menos 50% => sí tuvo avance suficiente.
            // - R/Z => actividad con avance.
            //
            // Esto permite que el Feed sea útil incluso en la PRIMERA apertura
            // del día, cuando todavía no existe un delta histórico.
            var currentProgressQualifies =
                isReview ||
                isCompleted ||
                (hasRealChecklist &&
                 activity.ChecklistPercentage >= 50);

            var hasTodayMovement =
                !isLagging &&
                (observedMovement ||
                 currentProgressQualifies);

            var duration =
                activity.End > activity.Start
                    ? activity.End - activity.Start
                    : TimeSpan.FromMinutes(15);

            var scheduledMinutes =
                Math.Max(
                    1,
                    (int)Math.Round(
                        duration.TotalMinutes));

            var progressRatio =
                CalculateActivityProgressRatio(
                    activity,
                    state.Code);

            var progressMinutes =
                isSuspended
                    ? 0
                    : Math.Clamp(
                        (int)Math.Round(
                            scheduledMinutes *
                            progressRatio),
                        0,
                        scheduledMinutes);

            var movementLabel =
                BuildMovementLabel(
                    checklistDelta,
                    workedDelta,
                    isReviewMovement,
                    isCompletedMovement,
                    observedMovement,
                    currentProgressQualifies,
                    activity.ChecklistScanned &&
                    activity.ChecklistTotal > 0
                        ? activity.ChecklistPercentage
                        : Math.Clamp(
                            (int)Math.Round(
                                progressRatio * 100d),
                            0,
                            100));

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
                    string.IsNullOrWhiteSpace(
                        activity.Person)
                            ? "Sin asignar"
                            : activity.Person.Trim(),

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
                            progressRatio * 100d),
                        0,
                        100)
            };
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
                                active),

                        ScheduledMinutes =
                            GetScheduledMinutes(
                                active),

                        ProgressMinutes =
                            GetProgressMinutes(
                                active),

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
                                item.NeedsChecklistData)
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

            var reference =
                new DateTimeOffset(
                    referenceTime);

            var windowStart =
                reference.AddHours(-15);

            return timestamps.Any(timestamp =>
                timestamp.LocalDateTime.Date ==
                    selectedDay.Date &&
                timestamp >= windowStart &&
                timestamp <= reference);
        }

        private static string BuildMovementLabel(
            int checklistDelta,
            int workedDelta,
            bool reviewMovement,
            bool completedMovement,
            bool observedMovement,
            bool currentProgressQualifies,
            int currentProgressPercentage)
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
                parts.Add("pasó a R");

            if (completedMovement)
                parts.Add("pasó a Z");

            // Primera lectura del día: todavía no hay delta, pero Genaro sí
            // quiere ver R/Z y actividades con >=50% como "con avance".
            // Lo etiquetamos como estado actual, sin inventar cuándo cambió.
            if (!observedMovement &&
                currentProgressQualifies)
            {
                parts.Add(
                    $"avance actual {currentProgressPercentage}%");
            }

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

                if (dayTracking == null)
                {
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
                    $"{source} · baseline diario creado " +
                    $"{tracking.FirstCapturedAtUtc.ToLocalTime():HH:mm}. " +
                    "Avance Hoy ya muestra R/Z y actividades con >=50% actual; " +
                    "desde la siguiente lectura también mostrará deltas exactos. " +
                    $"Sin checklist verificable: {noChecklist} · sin asignar: {unassigned}.";
            }

            return
                $"{source} · seguimiento desde " +
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

            return value.Length <= 92
                ? value
                : value.Substring(0, 89).TrimEnd() + "…";
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
