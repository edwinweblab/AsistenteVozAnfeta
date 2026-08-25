using Anfeta.UI.Models.Notion;
using System;
using System.Collections.Generic;

namespace Anfeta.UI.Models.DailyProgress
{
    public sealed class DailyProgressActivityItem
    {
        public NotionCalendarActivity Source { get; init; } = new();

        public string PageId => Source.PageId;
        public string PageUrl => Source.PageUrl;

        public string FullTitle { get; init; } = "";
        public string ShortTitle { get; init; } = "";
        public string Domain { get; init; } = "";
        public string Person { get; init; } = "";

        public string StateCode { get; init; } = "P";
        public string StateLabel { get; init; } = "Pendiente";

        public DateTime Start => Source.Start;
        public DateTime End => Source.End;

        // Fechas inmutables para auditoría y futuro reporte semanal.
        public DateTime OriginalScheduledDate { get; init; }
        public DateTime CurrentScheduledDate { get; init; }
        public int MoveCount { get; init; }
        public IReadOnlyList<DateTime> RouteDates { get; init; } =
            Array.Empty<DateTime>();

        public bool ChecklistScanned => Source.ChecklistScanned;
        public int ChecklistTotal => Source.ChecklistTotal;
        public int ChecklistCompleted => Source.ChecklistCompleted;
        public int ChecklistPercentage => Source.ChecklistPercentage;

        public string ChecklistLabel =>
            ChecklistScanned
                ? $"{ChecklistCompleted}/{ChecklistTotal}"
                : "…";

        // Avance de la fecha seleccionada, derivado de checked=true y del
        // last_edited_time de cada bloque to_do. El total es el checklist
        // completo de la actividad para que Hoy y Total sean comparables.
        public int TodayChecklistCompleted { get; init; }
        public int TodayChecklistTotal { get; init; }
        public int TodayChecklistPercentage { get; init; }

        public string TodayChecklistLabel =>
            ChecklistScanned
                ? $"{TodayChecklistCompleted}/{TodayChecklistTotal} · " +
                  $"{TodayChecklistPercentage}%"
                : "…";

        public string TotalChecklistLabel =>
            ChecklistScanned
                ? $"{ChecklistCompleted}/{ChecklistTotal} · " +
                  $"{ChecklistPercentage}%"
                : "…";

        // Reglas del Feed V2.
        public bool IsLagging { get; init; }
        public bool NeedsChecklistData { get; init; }
        public bool HasVisibleProgress { get; init; }
        public bool HasTodayMovement { get; init; }
        public bool IsReviewMovement { get; init; }
        public bool IsCompletedMovement { get; init; }

        public bool IsReview { get; init; }
        public bool IsCompleted { get; init; }
        public bool IsSuspended { get; init; }

        // Auditoría diaria V3.
        // HistoricalSnapshot = la actividad ya no pertenece al día actual en
        // Notion (por ejemplo, fue movida), pero ANFETA conserva cómo estaba
        // cuando fue observada en esa fecha.
        public bool IsHistoricalSnapshot { get; init; }
        public DateTimeOffset? HistoricalCapturedAt { get; init; }

        // R/Z con checklist real incompleta necesita una advertencia explícita.
        // No se interpreta automáticamente como 100% solo por el estado.
        public bool HasIncompleteChecklistWarning { get; init; }

        // Deltas detectados desde el baseline persistente del día.
        public int ChecklistDeltaToday { get; init; }
        public int WorkedMinutesDeltaToday { get; init; }
        public string MovementLabel { get; init; } = "";

        // Cobertura ponderada por tiempo.
        public int ScheduledMinutes { get; init; }
        public int ProgressMinutes { get; init; }
        public int ProgressPercentage { get; init; }

        public string TimeLabel =>
            $"{Start:HH:mm}–{End:HH:mm}";
    }

    public sealed class DailyProgressPersonSnapshot
    {
        public string Name { get; init; } = "";
        public string Initial { get; init; } = "?";

        public int CoveragePercentage { get; init; }
        public int ScheduledMinutes { get; init; }
        public int ProgressMinutes { get; init; }

        public IReadOnlyList<DailyProgressActivityItem> Lagging { get; init; } =
            Array.Empty<DailyProgressActivityItem>();

        // En V2 esta lista ya significa movimiento detectado HOY,
        // no simplemente "estado actual con algún avance".
        public IReadOnlyList<DailyProgressActivityItem> Progress { get; init; } =
            Array.Empty<DailyProgressActivityItem>();

        public IReadOnlyList<DailyProgressActivityItem> AllActivities { get; init; } =
            Array.Empty<DailyProgressActivityItem>();

        public int ReviewCount { get; init; }
        public int CompletedCount { get; init; }
        public int PendingCount { get; init; }
        public int MissingChecklistCount { get; init; }
        public int HistoricalCount { get; init; }
        public int IncompleteChecklistCount { get; init; }
    }

    public sealed class DailyProgressSnapshot
    {
        public DateTime Date { get; init; }

        public IReadOnlyList<DailyProgressPersonSnapshot> People { get; init; } =
            Array.Empty<DailyProgressPersonSnapshot>();

        public int CoveragePercentage { get; init; }
        public int TotalActivities { get; init; }
        public int LaggingCount { get; init; }

        // Transiciones/movimientos detectados en la ventana del Feed.
        public int ReviewCount { get; init; }
        public int CompletedCount { get; init; }
        public int ProgressCount { get; init; }

        public int ScheduledMinutes { get; init; }
        public int ProgressMinutes { get; init; }

        public int MissingChecklistCount { get; init; }
        public int UnassignedCount { get; init; }
        public int HistoricalCount { get; init; }
        public int IncompleteChecklistCount { get; init; }

        public bool LoadedFromCalendarCache { get; init; }

        // Primer baseline del día para comparar checklists/estado.
        public bool HadTrackingBaseline { get; init; }
        public DateTimeOffset TrackingStartedAt { get; init; }

        public string DataNote { get; init; } = "";
    }

    public sealed class WeeklyProgressActivityItem
    {
        public string PageId { get; init; } = "";
        public string PageUrl { get; init; } = "";
        public string Title { get; init; } = "";
        public string Person { get; init; } = "";
        public string Project { get; init; } = "";
        public string StateCode { get; init; } = "P";
        public DateTime ReportDate { get; init; }
        public DateTime OriginalScheduledDate { get; init; }
        public DateTime CurrentScheduledDate { get; init; }
        public int MoveCount { get; init; }
        public IReadOnlyList<DateTime> RouteDates { get; init; } = Array.Empty<DateTime>();
        public int ChecklistCompleted { get; init; }
        public int ChecklistTotal { get; init; }
        public int ChecklistAdvanced { get; init; }
        public int ScheduledMinutes { get; init; }
        public int ProgressMinutes { get; init; }
        public bool IsLagging { get; init; }
        public bool IsReviewMovement { get; init; }
        public bool IsCompletedMovement { get; init; }
        public bool IsFinal { get; init; }
        public bool HasProgress => ChecklistAdvanced > 0 || ProgressMinutes > 0 ||
                                   IsReviewMovement || IsCompletedMovement;
    }

    public sealed class WeeklyProgressPersonSnapshot
    {
        public string Name { get; init; } = "";
        public int CoveragePercentage { get; init; }
        public int ActivityCount { get; init; }
        public int LaggingCount { get; init; }
        public int ReviewCount { get; init; }
        public int CompletedCount { get; init; }
        public int NoProgressCount { get; init; }
        public int MovedCount { get; init; }
        public int ChecklistAdvanced { get; init; }
        public int ScheduledMinutes { get; init; }
        public int ProgressMinutes { get; init; }
        public IReadOnlyList<WeeklyProgressActivityItem> Items { get; init; } =
            Array.Empty<WeeklyProgressActivityItem>();
    }

    public sealed class WeeklyProgressSnapshot
    {
        public DateTime WeekStart { get; init; }
        public DateTime WeekEnd { get; init; }
        public IReadOnlyList<DateTime> MissingDays { get; init; } = Array.Empty<DateTime>();
        public IReadOnlyList<WeeklyProgressPersonSnapshot> People { get; init; } =
            Array.Empty<WeeklyProgressPersonSnapshot>();
        public int CoveragePercentage { get; init; }
        public int ActivityCount { get; init; }
        public int LaggingCount { get; init; }
        public int ReviewCount { get; init; }
        public int CompletedCount { get; init; }
        public int NoProgressCount { get; init; }
        public int MovedCount { get; init; }
        public int ChecklistAdvanced { get; init; }
        public int ScheduledMinutes { get; init; }
        public int ProgressMinutes { get; init; }
        public bool IsComplete => MissingDays.Count == 0;
        public string DataNote { get; init; } = "";
    }
}
