// Services/Activity/CorrectionCommandDetector.cs
using System;

namespace Anfeta.UI.Services.Activity
{
    /// <summary>
    /// Detecta comandos especiales: corrección, confirmación, cancelación, reinicio
    /// </summary>
    public sealed class CorrectionCommandDetector
    {
        /// <summary>
        /// Detecta si el usuario quiere corregir un campo
        /// Entrada: text - texto del usuario
        /// Salida: (es corrección, nombre del campo)
        /// </summary>
        public (bool isCorrection, string? field) Detect(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return (false, null);

            var lower = text.Trim().ToLowerInvariant();

            // Patrones de corrección
            if (!lower.Contains("corregir") &&
                !lower.Contains("cambiar") &&
                !lower.Contains("editar") &&
                !lower.Contains("modificar"))
                return (false, null);

            // Mapear a campos
            if (lower.Contains("titulo") || lower.Contains("título"))
                return (true, "titulo");

            if (lower.Contains("prioridad"))
                return (true, "prioridad");

            if (lower.Contains("fecha") || lower.Contains("inicio"))
                return (true, "dueStart");

            if (lower.Contains("fin") || lower.Contains("final"))
                return (true, "dueEnd");

            // Usuario dijo "corregir" pero no especificó campo
            return (true, null);
        }

        /// <summary>
        /// Detecta confirmación
        /// Entrada: text - texto del usuario
        /// Salida: true si es confirmación
        /// </summary>
        public bool IsConfirmation(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var lower = text.Trim().ToLowerInvariant();

            return lower == "confirmar" ||
                   lower == "confirmo" ||
                   lower == "confirmado" ||
                   lower == "sí" ||
                   lower == "si" ||
                   lower == "ok" ||
                   lower == "okay" ||
                   lower == "vale" ||
                   lower == "adelante" ||
                   lower == "continuar" ||
                   lower == "crear" ||
                   lower == "proceder";
        }

        /// <summary>
        /// Detecta cancelación (MEJORADO)
        /// Entrada: text - texto del usuario
        /// Salida: true si es cancelación
        /// </summary>
        public bool IsCancellation(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var lower = text.Trim().ToLowerInvariant();

            // Cancelación explícita
            if (lower == "cancelar" ||
                lower == "cancela" ||
                lower == "cancelado" ||
                lower == "abortar" ||
                lower == "aborta" ||
                lower == "abortado" ||
                lower == "no" ||
                lower == "nope" ||
                lower == "salir" ||
                lower == "detener" ||
                lower == "parar" ||
                lower == "terminar")
                return true;

            // Frases completas de cancelación
            if (lower.Contains("cancelar actividad") ||
                lower.Contains("cancela actividad") ||
                lower.Contains("cancelar la actividad") ||
                lower.Contains("cancela la actividad") ||
                lower.Contains("no quiero") ||
                lower.Contains("ya no") ||
                lower.Contains("mejor no") ||
                lower.Contains("déjalo") ||
                lower.Contains("dejalo") ||
                lower.Contains("olvídalo") ||
                lower.Contains("olvidalo"))
                return true;

            return false;
        }

        /// <summary>
        /// Detecta reinicio
        /// Entrada: text - texto del usuario
        /// Salida: true si es reinicio
        /// </summary>
        public bool IsRestart(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var lower = text.Trim().ToLowerInvariant();

            return lower.Contains("reiniciar") ||
                   lower.Contains("empezar de nuevo") ||
                   lower.Contains("empezar otra vez") ||
                   lower.Contains("comenzar de nuevo") ||
                   lower.Contains("reset") ||
                   lower.Contains("volver a empezar");
        }
    }
}