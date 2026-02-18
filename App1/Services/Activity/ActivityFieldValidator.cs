// Services/Activity/ActivityFieldValidator.cs
using System;
using System.Linq;

namespace Anfeta.UI.Services.Activity
{
    /// <summary>
    /// Valida campos de actividad con fuzzy matching y sugerencias
    /// </summary>
    public sealed class ActivityFieldValidator
    {
        private static readonly string[] ValidPrioridad = { "alta", "media", "baja" };

        /// <summary>
        /// Valida prioridad y sugiere corrección si es inválida
        /// Entrada: input - texto del usuario
        /// Salida: resultado de validación
        /// </summary>
        public ValidationResult ValidatePrioridad(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new ValidationResult { Valid = false };

            var lower = input.Trim().ToLowerInvariant()
                .Replace("prioridad", "")
                .Replace("es", "")
                .Replace("la", "")
                .Replace("sí", "")
                .Replace("si", "")
                .Replace(",", "")
                .Trim();

            if (lower.Contains("alt") || lower.Contains("urgent") || lower.Contains("import") || lower.Contains("critic"))
                return new ValidationResult { Valid = true, Normalized = "Alta" };

            if (lower.Contains("medi") || lower.Contains("normal") || lower.Contains("regul"))
                return new ValidationResult { Valid = true, Normalized = "Media" };

            if (lower.Contains("baj") || lower.Contains("poca") || lower.Contains("ninguna") || lower.Contains("puede esperar"))
                return new ValidationResult { Valid = true, Normalized = "Baja" };

            return new ValidationResult { Valid = false, Suggestion = "Media" };
        }

        /// <summary>
        /// Valida fecha (no en pasado, no muy lejana)
        /// Entrada: fecha - DateTimeOffset a validar
        /// Salida: resultado de validación
        /// </summary>
        public DateValidationResult ValidateFecha(DateTimeOffset fecha)
        {
            var now = DateTimeOffset.Now;

            // Si es pasado (con margen de 10 min)
            if (fecha < now.AddMinutes(-10))
            {
                // Si es HOY pero pasado
                if (fecha.Date == now.Date)
                {
                    return new DateValidationResult
                    {
                        Valid = false,
                        Message = $"Son las {now:HH:mm} y dijiste {fecha:HH:mm}. Esa hora ya pasó. ¿Otra hora?"
                    };
                }

                return new DateValidationResult
                {
                    Valid = false,
                    Message = "No puedo crear actividades en el pasado. ¿Otra fecha?"
                };
            }

            // Advertir si es muy lejana (>1 año)
            if (fecha > now.AddYears(1))
            {
                return new DateValidationResult
                {
                    Valid = true,
                    Message = "⚠️ Fecha muy lejana. ¿Estás seguro?"
                };
            }

            return new DateValidationResult { Valid = true };
        }

        /// <summary>
        /// Capitaliza primera letra
        /// </summary>
        private static string CapitalizeFirst(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            if (text.Length == 1)
                return text.ToUpperInvariant();

            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }
    }

    /// <summary>
    /// Resultado de validación de campo
    /// </summary>
    public sealed class ValidationResult
    {
        public bool Valid { get; set; }
        public string? Normalized { get; set; }
        public string? Suggestion { get; set; }
    }

    /// <summary>
    /// Resultado de validación de fecha
    /// </summary>
    public sealed class DateValidationResult
    {
        public bool Valid { get; set; }
        public string? Message { get; set; }
    }
}