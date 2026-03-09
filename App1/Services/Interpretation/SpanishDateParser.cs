// Services/Interpretation/SpanishDateParser.cs
using System;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Interpretation
{
    /// <summary>
    /// Parser de fechas en español para el flujo de edición de recordatorios.
    /// Entrada: texto hablado del usuario (ej: "el viernes a las 3", "mañana a las 10").
    /// Salida: (fecha extraída, texto restante sin la parte de fecha).
    /// </summary>
    public static class SpanishDateParser
    {
        // Meses en español (índice 1=enero...12=diciembre)
        private static readonly string[] Meses =
        {
            "", "enero", "febrero", "marzo", "abril", "mayo", "junio",
            "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"
        };

        // Días de la semana
        private static readonly (string name, DayOfWeek dow)[] DiasSemanana =
        {
            ("lunes",     DayOfWeek.Monday),
            ("martes",    DayOfWeek.Tuesday),
            ("miércoles", DayOfWeek.Wednesday),
            ("miercoles", DayOfWeek.Wednesday),
            ("jueves",    DayOfWeek.Thursday),
            ("viernes",   DayOfWeek.Friday),
            ("sábado",    DayOfWeek.Saturday),
            ("sabado",    DayOfWeek.Saturday),
            ("domingo",   DayOfWeek.Sunday)
        };

        /// <summary>
        /// Intenta extraer fecha/hora de texto en español.
        /// Retorna (fecha, textoLimpio) donde textoLimpio es el mensaje sin la parte de fecha.
        /// Si no se detecta fecha, retorna (null, textoOriginal).
        /// </summary>
        public static (DateTime? date, string cleanText) TryParse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return (null, input ?? "");

            var t = input.Trim().ToLowerInvariant();

            // 1. Extraer la hora si existe ("a las X" / "a las X:XX")
            var (timeFound, hours, minutes, textSinHora) = ExtractTime(t);

            // 2. Extraer la fecha base
            DateTime? baseDate = null;
            string textSinFecha = textSinHora;

            // "mañana"
            if (textSinHora.Contains("mañana"))
            {
                baseDate = DateTime.Today.AddDays(1);
                textSinFecha = textSinHora.Replace("mañana", "").Trim();
            }
            // "hoy"
            else if (textSinHora.Contains("hoy"))
            {
                baseDate = DateTime.Today;
                textSinFecha = textSinHora.Replace("hoy", "").Trim();
            }
            // "dentro de X días"
            else
            {
                var daysMatch = Regex.Match(textSinHora, @"dentro de (\d+) d[ií]as?");
                if (daysMatch.Success)
                {
                    baseDate = DateTime.Today.AddDays(int.Parse(daysMatch.Groups[1].Value));
                    textSinFecha = textSinHora.Replace(daysMatch.Value, "").Trim();
                }
            }

            // "dentro de una semana"
            if (baseDate == null && textSinHora.Contains("dentro de una semana"))
            {
                baseDate = DateTime.Today.AddDays(7);
                textSinFecha = textSinHora.Replace("dentro de una semana", "").Trim();
            }

            // "el 16 de marzo" / "el 5 de abril"
            if (baseDate == null)
            {
                var dateMatch = Regex.Match(textSinHora, @"el (\d{1,2}) de ([a-záéíóúñ]+)");
                if (dateMatch.Success)
                {
                    var day = int.Parse(dateMatch.Groups[1].Value);
                    var monthName = dateMatch.Groups[2].Value;
                    var month = Array.FindIndex(Meses, m => m == monthName);
                    if (month > 0 && day >= 1 && day <= 31)
                    {
                        try
                        {
                            var year = DateTime.Today.Year;
                            var candidate = new DateTime(year, month, day);
                            if (candidate < DateTime.Today) candidate = candidate.AddYears(1);
                            baseDate = candidate;
                            textSinFecha = textSinHora.Replace(dateMatch.Value, "").Trim();
                        }
                        catch { /* fecha inválida, ignorar */ }
                    }
                }
            }

            // "el lunes" / "el viernes" / etc.
            if (baseDate == null)
            {
                foreach (var (name, dow) in DiasSemanana)
                {
                    if (textSinHora.Contains($"el {name}") || Regex.IsMatch(textSinHora, $@"^{name}\b"))
                    {
                        baseDate = NextOccurrence(dow);
                        textSinFecha = textSinHora
                            .Replace($"el {name}", "")
                            .Replace(name, "")
                            .Trim();
                        break;
                    }
                }
            }

            // Si solo hay hora sin fecha → usar hoy
            if (baseDate == null && timeFound)
                baseDate = DateTime.Today;

            // Si no se encontró nada → devolver original
            if (baseDate == null)
                return (null, input.Trim());

            // Aplicar hora (default 09:00 si no se especificó)
            var h = timeFound ? hours : 9;
            var m = timeFound ? minutes : 0;
            var finalDate = baseDate.Value.Date.AddHours(h).AddMinutes(m);

            // Limpiar palabras residuales del texto
            var clean = CleanResidual(textSinFecha);

            return (finalDate, clean);
        }

        // ────────────────────────────────────────────────────────────────────
        // HELPERS PRIVADOS
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extrae "a las HH" / "a las HH:MM" del texto.
        /// Interpreta horas ambiguas (1-8) como PM si son horario laboral.
        /// </summary>
        private static (bool found, int hours, int minutes, string remaining) ExtractTime(string text)
        {
            // "a las 10:30" / "a las 10"
            var match = Regex.Match(text, @"a las (\d{1,2})(?::(\d{2}))?");
            if (match.Success)
            {
                var h = int.Parse(match.Groups[1].Value);
                var min = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;

                // Interpretar AM/PM: 1-8 sin especificar → PM (horario laboral)
                if (h >= 1 && h <= 8) h += 12;

                var remaining = text.Replace(match.Value, "").Trim();
                return (true, h, min, remaining);
            }

            return (false, 0, 0, text);
        }

        /// <summary>
        /// Calcula la siguiente ocurrencia de un día de la semana desde hoy.
        /// Si hoy es ese día, retorna la próxima semana.
        /// </summary>
        private static DateTime NextOccurrence(DayOfWeek dow)
        {
            var today = DateTime.Today;
            var diff = ((int)dow - (int)today.DayOfWeek + 7) % 7;
            if (diff == 0) diff = 7;
            return today.AddDays(diff);
        }

        /// <summary>
        /// Limpia preposiciones y artículos residuales del texto restante.
        /// </summary>
        private static string CleanResidual(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            // Eliminar palabras sueltas residuales al inicio/final
            var cleaned = Regex.Replace(text, @"^\s*(para|el|la|los|las|de|en|a|al)\s+", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s+(para|el|la|de|en)\s*$", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();

            return cleaned;
        }
    }
}