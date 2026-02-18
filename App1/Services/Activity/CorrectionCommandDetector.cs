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

            // Verificar si hay intención de corregir
            if (!lower.Contains("corregir") &&
                !lower.Contains("cambiar") &&
                !lower.Contains("editar") &&
                !lower.Contains("modificar") &&
                !lower.Contains("actualizar") &&
                !lower.Contains("arreglar"))
                return (false, null);

            // TÍTULO
            if (lower.Contains("titulo") ||
                lower.Contains("título") ||
                lower.Contains("el titulo") ||
                lower.Contains("el título") ||
                lower.Contains("nombre") ||
                lower.Contains("el nombre"))
                return (true, "titulo");

            // PRIORIDAD
            if (lower.Contains("prioridad") ||
                lower.Contains("la prioridad"))
                return (true, "prioridad");

            // FECHA DE INICIO
            if (lower.Contains("fecha de inicio") ||
                lower.Contains("fecha inicio") ||
                lower.Contains("inicio") ||
                lower.Contains("el inicio") ||
                lower.Contains("fecha") ||
                lower.Contains("la fecha") ||
                lower.Contains("cuando empieza") ||
                lower.Contains("cuándo empieza") ||
                lower.Contains("hora de inicio") ||
                lower.Contains("hora inicio"))
                return (true, "dueStart");

            // FECHA DE FIN
            if (lower.Contains("fecha de fin") ||
                lower.Contains("fecha fin") ||
                lower.Contains("fin") ||
                lower.Contains("el fin") ||
                lower.Contains("final") ||
                lower.Contains("cuando termina") ||
                lower.Contains("cuándo termina") ||
                lower.Contains("hora de fin") ||
                lower.Contains("hora fin") ||
                lower.Contains("duración") ||
                lower.Contains("duracion"))
                return (true, "dueEnd");

            // ASSIGNEE
            if (lower.Contains("asignado") ||
                lower.Contains("responsable") ||
                lower.Contains("quien") ||
                lower.Contains("quién") ||
                lower.Contains("persona") ||
                lower.Contains("assignee"))
                return (true, "assignee");

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