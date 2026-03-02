// Models/Recordatorio.cs
using System;

namespace Anfeta.UI.Models.Weblab
{
    // Modelo de recordatorio según API Weblab
    // Campos: userId (phone), mensaje, fechaHora, duracionMinutos, tipo, activo, enviado
    public sealed record Recordatorio(
        string Id,
        string UserId,
        string Mensaje,
        DateTime FechaHora,
        int DuracionMinutos,
        string Tipo,
        bool Activo,
        bool Enviado,
        string? RevisionId,
        string? ActividadId,
        string? GoogleEventId,
        string? GoogleHtmlLink,
        string? Timezone);
}