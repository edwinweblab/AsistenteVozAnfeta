using System;
using System.Globalization;

namespace Anfeta.UI.Services.Activity
{
    public sealed class ActivityEditFieldValidator
    {
        public (bool Ok, string? Normalized, string Message) Validate(string field, string value)
        {
            if (string.IsNullOrWhiteSpace(field))
                return (false, null, "Campo inválido.");

            if (string.IsNullOrWhiteSpace(value))
                return (false, null, "El valor no puede estar vacío.");

            switch (field)
            {
                case "prioridad":
                    return ValidatePrioridad(value);

                case "status":
                    return ValidateStatus(value);

                case "dueStart":
                case "dueEnd":
                    return ValidateDate(value);

                default:
                    return (true, value.Trim(), "OK");
            }
        }

        private (bool Ok, string? Normalized, string Message) ValidatePrioridad(string value)
        {
            var v = value.Trim().ToLowerInvariant();

            if (v == "alta") return (true, "Alta", "OK");
            if (v == "media") return (true, "Media", "OK");
            if (v == "baja") return (true, "Baja", "OK");

            return (false, null, "Prioridad inválida. Usa: Alta, Media o Baja.");
        }

        private (bool Ok, string? Normalized, string Message) ValidateStatus(string value)
        {
            var v = value.Trim().ToLowerInvariant();

            if (v == "pendiente") return (true, "Pendiente", "OK");
            if (v == "en progreso") return (true, "En progreso", "OK");
            if (v == "completada") return (true, "Completada", "OK");
            if (v == "cancelada") return (true, "Cancelada", "OK");

            return (false, null, "Estado inválido. Usa por ejemplo: Pendiente, En progreso, Completada o Cancelada.");
        }

        private (bool Ok, string? Normalized, string Message) ValidateDate(string value)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.GetCultureInfo("es-MX"), DateTimeStyles.AssumeLocal, out var dt))
            {
                return (true, dt.ToString("yyyy-MM-ddTHH:mm:sszzz"), "OK");
            }

            return (false, null, "No pude entender la fecha. Intenta algo como: 2026-03-10 15:00");
        }
    }
}