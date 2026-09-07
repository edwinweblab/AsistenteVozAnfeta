using System;
using System.Collections.Generic;

namespace Anfeta.UI.Models.DailyAi;

public sealed record DailyAiMetrics(
    int TotalProjects,
    int TotalActivities,
    int CompletedToday,
    int PendingToday,
    int LaggingActivities,
    int ProjectsInReview,
    int SuspendedProjects,
    int UnassignedActivities,
    int MissingChecklistActivities);

public sealed record DailyAiActivitySnapshot(
    string PageId,
    string PageUrl,
    string Title,
    string ProjectName,
    string Domain,
    string Area,
    string Person,
    string StateCode,
    string StateLabel,
    DateTime Start,
    DateTime End,
    int ChecksTodayDone,
    int ChecksTodayTotal,
    int ProgressTodayPct,
    int ChecksTotalDone,
    int ChecksTotal,
    int ProgressTotalPct,
    bool IsLagging,
    bool IsInReview,
    bool IsSuspended,
    bool IsCompleted,
    bool IsUnassigned,
    bool IsHistorical);

public sealed record DailyAiProjectSnapshot(
    string ProjectName,
    string Domain,
    string Area,
    IReadOnlyList<string> ResponsiblePeople,
    int ActivitiesToday,
    int CompletedToday,
    int PendingToday,
    int ChecksTodayDone,
    int ChecksTodayTotal,
    int ProgressTodayPct,
    int ChecksTotalDone,
    int ChecksTotal,
    int ProgressTotalPct,
    string LastProgressToday,
    bool IsCritical,
    bool IsLagging,
    bool IsInReview,
    bool IsSuspended,
    bool IsUnassigned,
    IReadOnlyList<string> CriticalReasons);

public sealed record DailyAiPersonSnapshot(
    string Name,
    int ActivitiesToday,
    int CompletedToday,
    int PendingToday,
    int LaggingActivities,
    int ProjectsCount,
    int ScheduledMinutes,
    int ProgressMinutes,
    int WorkloadPct);

public sealed record DailyAiAreaSnapshot(
    string Name,
    int ProjectsCount,
    int ActivitiesCount,
    int CompletedCount,
    int LaggingCount,
    int AverageProgressToday);

public sealed record DailyAiSnapshot(
    DateTime Date,
    DateTimeOffset GeneratedAt,
    DailyAiMetrics Metrics,
    IReadOnlyList<DailyAiProjectSnapshot> Projects,
    IReadOnlyList<DailyAiPersonSnapshot> People,
    IReadOnlyList<DailyAiAreaSnapshot> Areas,
    IReadOnlyList<DailyAiActivitySnapshot> Activities,
    string DataNote,
    string Fingerprint);

public sealed record DailyAiNarrative(
    string Summary,
    IReadOnlyList<string> AttentionItems,
    IReadOnlyList<string> PositiveSignals,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<string> WorkloadObservations);

public sealed record DailyAiAssistantResult(
    string Answer,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<string> DayPlan,
    string WhatsAppMessage,
    string EmailMessage);
