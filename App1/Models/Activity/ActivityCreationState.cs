// Models/ActivityCreationState.cs
using Anfeta.UI.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Anfeta.UI.Models
{
    public sealed class ActivityCreationState
    {
        public string? Titulo { get; set; }
        public string? Prioridad { get; set; }
        public DateTimeOffset? DueStart { get; set; }
        public DateTimeOffset? DueEnd { get; set; }

        public int? AmbiguousHour { get; set; }
        public DateTimeOffset? AmbiguousBaseDate { get; set; }
        public int? DuracionHoras { get; set; }

        public List<AssigneeInfo>? Assignees { get; set; }
        public string? PendingAssigneeName { get; set; }
        public List<string>? PendingAssigneeNames { get; set; }
        public int CurrentAssigneeIndex { get; set; }
        public List<UserSearchItem>? PendingSearchResults { get; set; }
        public Task<UserSearchResponse>? PendingSearchTask { get; set; }

        public FlowPhase Phase { get; set; } = FlowPhase.Gathering;
        public int CurrentStep { get; set; } = 0;
        public string? FieldBeingCorrected { get; set; }

        public bool HasTitulo => !string.IsNullOrWhiteSpace(Titulo);
        public bool HasPrioridad => !string.IsNullOrWhiteSpace(Prioridad);
        public bool HasDueStart => DueStart.HasValue;
        public bool HasAssignees => Assignees != null && Assignees.Count > 0;
        public bool IsReadyForConfirmation => HasTitulo && HasPrioridad;

        public void Reset()
        {
            Titulo = null;
            Prioridad = null;
            DueStart = null;
            DueEnd = null;
            AmbiguousHour = null;
            AmbiguousBaseDate = null;
            DuracionHoras = null;
            Assignees = null;
            PendingAssigneeName = null;
            PendingAssigneeNames = null;
            CurrentAssigneeIndex = 0;
            PendingSearchResults = null;
            PendingSearchTask = null;
            Phase = FlowPhase.Gathering;
            CurrentStep = 0;
            FieldBeingCorrected = null;
        }
    }

    public enum FlowPhase
    {
        Gathering,
        ClarifyingTime,
        AskingDueEnd,            
        AskingAssignee,
        SearchingAssignee,
        ConfirmingAssignee,
        SelectingFromMultiple,
        Confirming,
        Correcting
    }

    public sealed class AssigneeInfo
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string CollaboratorId { get; set; } = "";
    }

    public sealed class UserSearchItem
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public string CollaboratorId { get; set; } = "";
    }
}