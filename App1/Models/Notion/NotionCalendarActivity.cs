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

        // Checkbox nativo de Notion: Bloqueada_ANFETA.
        // Cuando está activo, las automatizaciones no pueden mover la actividad.
        public bool IsAutomationLocked { get; set; }

        // Avance calculado desde los bloques nativos "to_do" del contenido
        // de la página de Notion. Los bloques sincronizados y la metadata
        // técnica de ANFETA no se cuentan.
        public bool ChecklistScanned { get; set; }
        public int ChecklistTotal { get; set; }
        public int ChecklistCompleted { get; set; }

        // Comentarios abiertos recuperados desde la API de Notion. Se guardan
        // con la misma cache incremental del checklist para no consultar cada
        // card de forma independiente.
        public int CommentCount { get; set; }
        public string LatestCommentText { get; set; } = "";

        public bool HasComments =>
            CommentCount > 0;

        public int ChecklistPending =>
            Math.Max(0, ChecklistTotal - ChecklistCompleted);

        public bool HasChecklist =>
            ChecklistTotal > 0;

        public int ChecklistPercentage =>
            ChecklistTotal <= 0
                ? 0
                : Math.Clamp(
                    (int)Math.Round(
                        ChecklistCompleted * 100d / ChecklistTotal),
                    0,
                    100);

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

        // Fechas de control para mostrar antigüedad y presupuesto visual
        // dentro del calendario. Se leen directamente desde Notion.
        public DateTime? ActivityCreatedDate { get; set; }
        public DateTime? InternalDeadlineDate { get; set; }

        public bool HasActivityDayRange =>
            ActivityCreatedDate.HasValue &&
            InternalDeadlineDate.HasValue;

        public int ActivityBudgetDays
        {
            get
            {
                if (!HasActivityDayRange)
                    return 0;

                return Math.Max(
                    1,
                    (InternalDeadlineDate!.Value.Date -
                     ActivityCreatedDate!.Value.Date).Days + 1);
            }
        }

        public int ActivityElapsedDays
        {
            get
            {
                if (!ActivityCreatedDate.HasValue)
                    return 0;

                var today = DateTime.Today;
                var start = ActivityCreatedDate.Value.Date;

                return today < start
                    ? 0
                    : Math.Max(1, (today - start).Days + 1);
            }
        }

        public bool IsActivityOverdue =>
            HasActivityDayRange &&
            ActivityElapsedDays > ActivityBudgetDays;

        public string DatePropertyName { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public TimeSpan EstimatedDuration =>
            End > Start
                ? End - Start
                : TimeSpan.Zero;

        // Control de tiempo trabajado persistido por ANFETA en Audit_FTF_Log.
        // EstimatedWorkMinutes conserva la estimación original aunque la
        // actividad continúe otro día con un bloque de menor duración.
        public int EstimatedWorkMinutes { get; set; }
        public int WorkedMinutes { get; set; }
        public string WorkLogDetail { get; set; } = "";

        public int RemainingWorkMinutes =>
            Math.Max(
                0,
                Math.Max(
                    EstimatedWorkMinutes,
                    WorkedMinutes) -
                WorkedMinutes);

        public int WorkPercentage =>
            EstimatedWorkMinutes <= 0
                ? 0
                : Math.Clamp(
                    (int)Math.Round(
                        WorkedMinutes * 100d /
                        EstimatedWorkMinutes),
                    0,
                    100);

        public bool HasWorkLog =>
            WorkedMinutes > 0;

        public string WorkProgressLabel
        {
            get
            {
                if (!HasWorkLog)
                    return string.Empty;

                var estimated =
                    EstimatedWorkMinutes > 0
                        ? EstimatedWorkMinutes
                        : WorkedMinutes;

                return $"⏱ {FormatMinutes(WorkedMinutes)} / " +
                       $"{FormatMinutes(estimated)} · " +
                       $"resta {FormatMinutes(RemainingWorkMinutes)}";
            }
        }

        private static string FormatMinutes(
            int totalMinutes)
        {
            totalMinutes = Math.Max(0, totalMinutes);

            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;

            if (hours > 0 && minutes > 0)
                return $"{hours}H {minutes}M";

            if (hours > 0)
                return $"{hours}H";

            return $"{minutes}M";
        }

        public string EstimatedDurationLabel
        {
            get
            {
                var duration = EstimatedDuration;

                if (duration <= TimeSpan.Zero)
                    return "0H";

                var totalMinutes =
                    Math.Max(1, (int)Math.Round(duration.TotalMinutes));
                var hours = totalMinutes / 60;
                var minutes = totalMinutes % 60;

                if (hours > 0 && minutes > 0)
                    return $"{hours}H {minutes}M";

                if (hours > 0)
                    return $"{hours}H";

                return $"{minutes}M";
            }
        }

        public string TimeLabel =>
            $"{Start:HH:mm} – {End:HH:mm} · {EstimatedDurationLabel}";
    }
}
