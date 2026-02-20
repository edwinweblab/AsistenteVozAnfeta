// Models/CreateActividadRequest.cs
using System.Collections.Generic;

namespace Anfeta.UI.Models
{
    /// <summary>
    /// Request para crear actividad en Weblab
    /// </summary>
    public sealed class CreateActividadRequest
    {
        // Obligatorio
        public string Titulo { get; set; } = "";

        // Opcionales (el backend usa defaults si son null)
        public string? Status { get; set; }
        public string? Prioridad { get; set; }
        public string? Tipo { get; set; }
        public string? ProyectoId { get; set; }
        public string? Anotaciones { get; set; }
        public string? PasosYLinks { get; set; }
        public string? DueStart { get; set; }  // ISO 8601 string
        public string? DueEnd { get; set; }    // ISO 8601 string

        // Pendientes (opcional)
        public List<PendienteItem>? Pendientes { get; set; }

        // Archivos (opcional)
        public List<string>? ArchivosPaths { get; set; }
        public List<string>? PendienteImagesPaths { get; set; }
        public List<AssigneeInfo>? Assignees { get; set; }
    }

    public sealed class PendienteItem
    {
        public string Text { get; set; } = "";
        public bool Checked { get; set; }
    }
}