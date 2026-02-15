// Models/ActivityCreationState.cs
using System;

namespace Anfeta.UI.Models
{
    /// <summary>
    /// Estado del flujo de creación de actividad
    /// </summary>
    public sealed class ActivityCreationState
    {
        // Datos de la actividad (SOLO LO ESENCIAL)
        public string? Titulo { get; set; }
        public string? Prioridad { get; set; }
        public DateTimeOffset? DueStart { get; set; }
        public DateTimeOffset? DueEnd { get; set; }

        // Control del flujo
        public FlowPhase Phase { get; set; } = FlowPhase.Gathering;
        public int CurrentStep { get; set; } = 0;
        public string? FieldBeingCorrected { get; set; }

        // Validación (SOLO campos que preguntamos)
        public bool HasTitulo => !string.IsNullOrWhiteSpace(Titulo);
        public bool HasPrioridad => !string.IsNullOrWhiteSpace(Prioridad);
        public bool HasDueStart => DueStart.HasValue;

        // Helper
        public bool IsReadyForConfirmation => HasTitulo && HasPrioridad;

        public void Reset()
        {
            Titulo = null;
            Prioridad = null;
            DueStart = null;
            DueEnd = null;
            Phase = FlowPhase.Gathering;
            CurrentStep = 0;
            FieldBeingCorrected = null;
        }
    }

    /// <summary>
    /// Fase actual del flujo de creación
    /// </summary>
    public enum FlowPhase
    {
        Gathering,   // Recopilando datos paso a paso
        Confirming,  // Mostrando resumen para confirmación
        Correcting   // Editando un campo específico
    }
}