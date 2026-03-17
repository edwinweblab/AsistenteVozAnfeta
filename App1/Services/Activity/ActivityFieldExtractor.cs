// Services/Activity/ActivityFieldExtractor.cs
using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Activity
{
    /// Extrae campos de actividad desde el comando de voz inicial.
    public sealed class ActivityFieldExtractor
    {
        /// Extrae todos los campos posibles del comando.
        /// Entrada: command — "crear actividad enviar reporte con prioridad alta mañana a las 5"
        /// Salida: ActivityCreationState con campos extraídos.
        public ActivityCreationState ExtractFields(string command)
        {
            var state = new ActivityCreationState();

            if (string.IsNullOrWhiteSpace(command))
                return state;

            var lower = command.Trim().ToLowerInvariant();

            state.Titulo = ExtractTitulo(lower);
            state.Prioridad = ExtractPrioridad(lower);

            var (start, end, ambiguousHour, ambiguousBaseDate) = ExtractFechaHora(lower);

            state.DueStart = start;
            state.DueEnd = end;
            state.AmbiguousHour = ambiguousHour;
            state.AmbiguousBaseDate = ambiguousBaseDate;

            return state;
        }

        /// Extrae solo la hora de un texto, usando una fecha base explícita.
        /// Útil cuando el usuario responde solo con una hora ("a las 5 de la tarde")
        /// sin mencionar fecha, y la fecha se conoce del contexto (DueStart.Date).
        /// Entrada: command en minúsculas, baseDate — fecha base para construir el DateTimeOffset.
        /// Salida: DateTimeOffset con fecha base + hora extraída, o null si no se pudo.
        public DateTimeOffset? ExtractTimeWithBase(string command, DateTimeOffset baseDate)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            var lower = command.Trim().ToLowerInvariant();
            var (horaDecimal, needsClarification) = ExtractHora(lower);

            if (!horaDecimal.HasValue || needsClarification) return null;

            return baseDate.Date.Add(TimeSpan.FromHours(horaDecimal.Value));
        }

        /// Extrae el título de la actividad.
        /// Entrada: command en minúsculas.
        /// Salida: título extraído o null.
        private string? ExtractTitulo(string command)
        {
            var patterns = new[]
            {
                @"crear actividad\s+(.+)",
                @"crea actividad\s+(.+)",
                @"nueva actividad\s+(.+)",
                @"crear tarea\s+(.+)",
                @"nueva tarea\s+(.+)"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(command, pattern);
                if (match.Success && match.Groups.Count > 1)
                {
                    var titulo = match.Groups[1].Value.Trim();

                    titulo = Regex.Replace(titulo, @"\s+(con\s+)?prioridad\s+(alta|media|baja).*", "", RegexOptions.IgnoreCase);
                    titulo = Regex.Replace(titulo, @"\s+para\s+(hoy|mañana|pasado mañana).*", "", RegexOptions.IgnoreCase);
                    titulo = Regex.Replace(titulo, @"\s+(el|a las|en)\s+\d+.*", "", RegexOptions.IgnoreCase);

                    if (!string.IsNullOrWhiteSpace(titulo))
                        return titulo.Trim();
                }
            }

            return null;
        }

        /// Extrae la prioridad.
        /// Salida: "Alta", "Media", "Baja" o null.
        private string? ExtractPrioridad(string command)
        {
            if (command.Contains("prioridad alta") || command.Contains("alta prioridad") || command.Contains("urgente"))
                return "Alta";

            if (command.Contains("prioridad media") || command.Contains("media prioridad") || command.Contains("normal"))
                return "Media";

            if (command.Contains("prioridad baja") || command.Contains("baja prioridad"))
                return "Baja";

            return null;
        }

        /// Extrae fecha y hora del comando.
        /// FIX 1: agrega detección de días de semana (lunes–domingo).
        /// Entrada: command en minúsculas.
        /// Salida: (inicio, fin, hora ambigua, fecha base para clarificación).
        private (DateTimeOffset? start, DateTimeOffset? end, int? ambiguousHour, DateTimeOffset? ambiguousBaseDate) ExtractFechaHora(string command)
        {
            var now = DateTimeOffset.Now;
            DateTimeOffset? baseDate = null;

            if (command.Contains("hoy"))
            {
                baseDate = now.Date;
            }
            else if (command.Contains("pasado mañana"))
            {
                baseDate = now.Date.AddDays(2);
            }
            else if (command.Contains("mañana"))
            {
                baseDate = now.Date.AddDays(1);
            }
            else
            {
                // Detectar día de semana: "el viernes", "para el lunes", "este martes"
                var diasSemana = new Dictionary<string, DayOfWeek>
                {
                    ["lunes"] = DayOfWeek.Monday,
                    ["martes"] = DayOfWeek.Tuesday,
                    ["miércoles"] = DayOfWeek.Wednesday,
                    ["miercoles"] = DayOfWeek.Wednesday,
                    ["jueves"] = DayOfWeek.Thursday,
                    ["viernes"] = DayOfWeek.Friday,
                    ["sábado"] = DayOfWeek.Saturday,
                    ["sabado"] = DayOfWeek.Saturday,
                    ["domingo"] = DayOfWeek.Sunday
                };

                foreach (var kvp in diasSemana)
                {
                    if (command.Contains(kvp.Key))
                    {
                        var targetDay = kvp.Value;
                        var daysAhead = ((int)targetDay - (int)now.DayOfWeek + 7) % 7;

                        // Si el día mencionado es hoy, ir al próximo (no el mismo día)
                        if (daysAhead == 0)
                            daysAhead = 7;

                        baseDate = now.Date.AddDays(daysAhead);
                        break;
                    }
                }

                // Si no matcheó día de semana, buscar fecha exacta: "el 20 de marzo"
                if (!baseDate.HasValue)
                {
                    var dateMatch = Regex.Match(command, @"(?:el\s+)?(\d{1,2})\s+de\s+(\w+)(?:\s+(?:del|de)\s+(\d{4}))?");
                    if (dateMatch.Success)
                    {
                        if (int.TryParse(dateMatch.Groups[1].Value, out var day))
                        {
                            var monthName = dateMatch.Groups[2].Value.ToLowerInvariant();
                            var month = GetMonthNumber(monthName);

                            if (month > 0)
                            {
                                var year = now.Year;
                                if (dateMatch.Groups[3].Success && int.TryParse(dateMatch.Groups[3].Value, out var parsedYear))
                                    year = parsedYear;
                                else if (month < now.Month || (month == now.Month && day < now.Day))
                                    year++;

                                try
                                {
                                    baseDate = new DateTimeOffset(year, month, day, 0, 0, 0, now.Offset);
                                }
                                catch
                                {
                                    // Fecha inválida (ej: 31 de febrero)
                                }
                            }
                        }
                    }
                }
            }

            if (!baseDate.HasValue)
                return (null, null, null, null);

            var duracion = ExtractDuracion(command);
            var (horaDecimal, needsClarification) = ExtractHora(command);

            // hora ambigua: guardar solo la parte entera para la pregunta AM/PM
            if (needsClarification && horaDecimal.HasValue)
            {
                var horaEntera = (int)horaDecimal.Value;
                return (null, null, horaEntera, baseDate.Value);
            }

            if (horaDecimal.HasValue)
            {
                var start = baseDate.Value.Add(TimeSpan.FromHours(horaDecimal.Value));

                if (duracion.HasValue)
                    return (start, start.AddHours(duracion.Value), null, null);

                return (start, null, null, null);
            }

            var defaultStart = baseDate.Value.AddHours(9);

            if (duracion.HasValue)
                return (defaultStart, defaultStart.AddHours(duracion.Value), null, null);

            return (defaultStart, null, null, null);
        }

        /// Extrae hora y detecta si necesita clarificación AM/PM.
        /// FIX 2: normaliza "a m"/"p m" con espacio o punto antes de evaluar patrones.
        /// FIX 3: retorna double? para preservar los minutos como fracción decimal.
        ///         Ejemplo: 8:30 PM → 20.5 (AddHours(20.5) = 20:30).
        /// Soporta: "3 pm", "8:00 a m", "8:00 a.m.", "5 y media", "a las 3", mediodía.
        private (double? hora, bool needsClarification) ExtractHora(string command)
        {
            int? horaBase = null;
            int minutos = 0;
            bool hasExplicitAMPM = false;
            bool isPM = false;

            // ── FIX 2: normalizar variantes de AM/PM con espacio o punto ────────────
            // El STT puede transcribir "AM" como "a m", "A.M.", "a.m.", etc.
            command = Regex.Replace(command, @"\ba\s*\.\s*m\s*\.?\b", "am", RegexOptions.IgnoreCase);
            command = Regex.Replace(command, @"\bp\s*\.\s*m\s*\.?\b", "pm", RegexOptions.IgnoreCase);
            command = Regex.Replace(command, @"\ba\s+m\b", "am", RegexOptions.IgnoreCase);
            command = Regex.Replace(command, @"\bp\s+m\b", "pm", RegexOptions.IgnoreCase);

            // ── Normalizar variaciones de "mediodía" ─────────────────────────────────
            command = command
                .Replace("después de mediodía", "despuesmediodia")
                .Replace("despues de mediodia", "despuesmediodia")
                .Replace("después del mediodía", "despuesmediodia")
                .Replace("despues del mediodia", "despuesmediodia")
                .Replace("antes de mediodía", "antesmediodia")
                .Replace("antes de mediodia", "antesmediodia")
                .Replace("antes del mediodía", "antesmediodia")
                .Replace("antes del mediodia", "antesmediodia");

            // CASO 1A: Hora con minutos + AM/PM: "3:00 pm", "8:00 am", "5:30 de la tarde"
            var match1 = Regex.Match(command, @"(\d{1,2}):(\d{2})\s*(pm|am|de la tarde|de la mañana|despuesmediodia|antesmediodia)");
            if (match1.Success)
            {
                horaBase = int.Parse(match1.Groups[1].Value);
                minutos = int.Parse(match1.Groups[2].Value);
                hasExplicitAMPM = true;

                var indicator = match1.Groups[3].Value;
                isPM = indicator.Contains("pm") || indicator.Contains("tarde") || indicator.Contains("despues");
            }

            // CASO 1B: Hora con minutos SIN AM/PM: "3:30", "5:45"
            if (!hasExplicitAMPM)
            {
                var match2 = Regex.Match(command, @"(\d{1,2}):(\d{2})(?!\s*(pm|am|tarde|mañana))");
                if (match2.Success)
                {
                    horaBase = int.Parse(match2.Groups[1].Value);
                    minutos = int.Parse(match2.Groups[2].Value);

                    // Formato 24h (14:00, 17:30)
                    if (horaBase >= 13 && horaBase <= 23)
                        return (horaBase.Value + minutos / 60.0, false);

                    // Hora ambigua (3:30, 11:45)
                    if (horaBase >= 1 && horaBase <= 12)
                        return (horaBase.Value, true); // Minutos se guardan al resolver AM/PM
                }
            }

            // CASO 2: Hora con AM/PM SIN minutos: "3 pm", "5 de la tarde"
            if (!hasExplicitAMPM)
            {
                var match3 = Regex.Match(command, @"(\d{1,2})\s*(pm|am|de la tarde|de la mañana|despuesmediodia|antesmediodia)");
                if (match3.Success)
                {
                    horaBase = int.Parse(match3.Groups[1].Value);
                    minutos = 0;
                    hasExplicitAMPM = true;

                    var indicator = match3.Groups[2].Value;
                    isPM = indicator.Contains("pm") || indicator.Contains("tarde") || indicator.Contains("despues");
                }
            }

            // CASO 3: "y media" = :30
            if (!hasExplicitAMPM && horaBase == null)
            {
                var matchMedia = Regex.Match(command, @"(\d{1,2})\s*y\s*media\s*(pm|am|de la tarde|de la mañana|despuesmediodia|antesmediodia)?");
                if (matchMedia.Success)
                {
                    horaBase = int.Parse(matchMedia.Groups[1].Value);
                    minutos = 30;

                    if (matchMedia.Groups[2].Success && !string.IsNullOrWhiteSpace(matchMedia.Groups[2].Value))
                    {
                        hasExplicitAMPM = true;
                        var indicator = matchMedia.Groups[2].Value;
                        isPM = indicator.Contains("pm") || indicator.Contains("tarde") || indicator.Contains("despues");
                    }
                }
            }

            // CASO 4: "y cuarto" = :15
            if (!hasExplicitAMPM && horaBase == null)
            {
                var matchCuarto = Regex.Match(command, @"(\d{1,2})\s*y\s*cuarto\s*(pm|am|de la tarde|de la mañana|despuesmediodia|antesmediodia)?");
                if (matchCuarto.Success)
                {
                    horaBase = int.Parse(matchCuarto.Groups[1].Value);
                    minutos = 15;

                    if (matchCuarto.Groups[2].Success && !string.IsNullOrWhiteSpace(matchCuarto.Groups[2].Value))
                    {
                        hasExplicitAMPM = true;
                        var indicator = matchCuarto.Groups[2].Value;
                        isPM = indicator.Contains("pm") || indicator.Contains("tarde") || indicator.Contains("despues");
                    }
                }
            }

            // CASO 5: "menos cuarto" = :45
            if (!hasExplicitAMPM && horaBase == null)
            {
                var matchMenosCuarto = Regex.Match(command, @"(\d{1,2})\s*menos\s*cuarto\s*(pm|am|de la tarde|de la mañana|despuesmediodia|antesmediodia)?");
                if (matchMenosCuarto.Success)
                {
                    horaBase = int.Parse(matchMenosCuarto.Groups[1].Value) - 1;
                    if (horaBase < 1) horaBase = 12;
                    minutos = 45;

                    if (matchMenosCuarto.Groups[2].Success && !string.IsNullOrWhiteSpace(matchMenosCuarto.Groups[2].Value))
                    {
                        hasExplicitAMPM = true;
                        var indicator = matchMenosCuarto.Groups[2].Value;
                        isPM = indicator.Contains("pm") || indicator.Contains("tarde") || indicator.Contains("despues");
                    }
                }
            }

            // CASO 6: Solo "despuesmediodia" sin hora → default 2 PM
            if (horaBase == null && command.Contains("despuesmediodia"))
                return (14.0, false);

            // CASO 7: Solo "antesmediodia" sin hora → default 10 AM
            if (horaBase == null && command.Contains("antesmediodia"))
                return (10.0, false);

            // CASO 8: Hora simple sin indicador: "a las 3", "las 5"
            if (horaBase == null)
            {
                var matchSimple = Regex.Match(command, @"(?:a las|las)\s+(\d{1,2})(?!\s*(pm|am|tarde|mañana|:))");
                if (matchSimple.Success)
                {
                    horaBase = int.Parse(matchSimple.Groups[1].Value);
                    minutos = 0;
                }
            }

            if (horaBase == null)
                return (null, false);

            // Calcular hora final con minutos incluidos como fracción decimal
            if (hasExplicitAMPM)
            {
                int horaFinal;
                if (isPM)
                    horaFinal = horaBase == 12 ? 12 : horaBase.Value + 12;
                else
                    horaFinal = horaBase == 12 ? 0 : horaBase.Value;

                return (horaFinal + minutos / 60.0, false);
            }

            // Hora ambigua (1-12 sin AM/PM) — retornar solo la hora entera para la pregunta
            if (horaBase >= 1 && horaBase <= 12)
                return (horaBase.Value, true);

            // Formato 24h
            if (horaBase >= 0 && horaBase <= 23)
                return (horaBase.Value + minutos / 60.0, false);

            return (null, false);
        }

        private int? ExtractDuracion(string command)
        {
            var match1 = Regex.Match(command, @"(?:durante|por)\s+(\d+)\s+horas?");
            if (match1.Success && int.TryParse(match1.Groups[1].Value, out var horas1))
                return horas1;

            var match2 = Regex.Match(command, @"de\s+(\d{1,2})\s+a\s+(\d{1,2})");
            if (match2.Success)
            {
                if (int.TryParse(match2.Groups[1].Value, out var inicio) &&
                    int.TryParse(match2.Groups[2].Value, out var fin))
                {
                    if (fin > inicio) return fin - inicio;
                    if (fin < inicio) return (fin + 12) - inicio;
                }
            }

            var match3 = Regex.Match(command, @"(\d+|una|dos|tres|cuatro|cinco)\s+horas?");
            if (match3.Success)
            {
                var num = match3.Groups[1].Value.ToLowerInvariant();
                var duracion = num switch
                {
                    "una" => 1,
                    "dos" => 2,
                    "tres" => 3,
                    "cuatro" => 4,
                    "cinco" => 5,
                    _ => int.TryParse(num, out var n) ? n : (int?)null
                };
                if (duracion.HasValue) return duracion.Value;
            }

            return null;
        }

        private int GetMonthNumber(string monthName)
        {
            return monthName switch
            {
                "enero" => 1,
                "febrero" => 2,
                "marzo" => 3,
                "abril" => 4,
                "mayo" => 5,
                "junio" => 6,
                "julio" => 7,
                "agosto" => 8,
                "septiembre" or "setiembre" => 9,
                "octubre" => 10,
                "noviembre" => 11,
                "diciembre" => 12,
                _ => 0
            };
        }

        public List<string> ExtractAssigneeNames(string command)
        {
            var names = new List<string>();
            var lower = command.Trim().ToLowerInvariant();

            var patterns = new[]
            {
                @"(?:asignar|asígnala|asignala|asígnale|asignale)\s+a\s+(.+?)(?:\s+prioridad|\s+mañana|\s+hoy|\s+el\s+\d+|$)",
            };

            foreach (var pattern in patterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(lower, pattern);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var rawNames = match.Groups[1].Value.Trim();

                        rawNames = System.Text.RegularExpressions.Regex.Replace(
                            rawNames,
                            @"\s+(prioridad|mañana|hoy|pasado|con|alta|media|baja).*",
                            "",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        );

                        if (!string.IsNullOrWhiteSpace(rawNames))
                        {
                            var splits = rawNames.Split(new[] { " y ", ", ", "," }, StringSplitOptions.RemoveEmptyEntries);

                            foreach (var name in splits)
                            {
                                var cleaned = name.Trim();
                                if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length > 1)
                                {
                                    cleaned = char.ToUpper(cleaned[0]) + cleaned.Substring(1);
                                    if (!names.Contains(cleaned))
                                        names.Add(cleaned);
                                }
                            }
                        }
                    }
                }
            }

            return names;
        }
    }
}