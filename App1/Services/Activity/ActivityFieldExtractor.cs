// Services/Activity/ActivityFieldExtractor.cs
using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Activity
{
    /// <summary>
    /// Extrae campos de actividad desde el comando de voz inicial
    /// </summary>
    public sealed class ActivityFieldExtractor
    {
        /// <summary>
        /// Extrae todos los campos posibles del comando
        /// Entrada: command - "crear actividad enviar reporte con prioridad alta mañana a las 5"
        /// Salida: ActivityCreationState con campos extraídos
        /// </summary>
        public ActivityCreationState ExtractFields(string command)
        {
            var state = new ActivityCreationState();

            if (string.IsNullOrWhiteSpace(command))
                return state;

            var lower = command.Trim().ToLowerInvariant();

            // Extraer campos
            state.Titulo = ExtractTitulo(lower);
            state.Prioridad = ExtractPrioridad(lower);

            // ✅ ACTUALIZADO: Manejar 4 elementos de la tupla
            var (start, end, ambiguousHour, ambiguousBaseDate) = ExtractFechaHora(lower);

            state.DueStart = start;
            state.DueEnd = end;
            state.AmbiguousHour = ambiguousHour;
            state.AmbiguousBaseDate = ambiguousBaseDate;

            return state;
        }

        /// <summary>
        /// Extrae el título de la actividad
        /// Entrada: command - comando en minúsculas
        /// Salida: título extraído o null
        /// </summary>
        private string? ExtractTitulo(string command)
        {
            // Buscar después de "crear actividad", "nueva actividad", etc.
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

                    // Limpiar palabras clave que no son parte del título
                    titulo = Regex.Replace(titulo, @"\s+(con\s+)?prioridad\s+(alta|media|baja).*", "", RegexOptions.IgnoreCase);
                    titulo = Regex.Replace(titulo, @"\s+para\s+(hoy|mañana|pasado mañana).*", "", RegexOptions.IgnoreCase);
                    titulo = Regex.Replace(titulo, @"\s+(el|a las|en)\s+\d+.*", "", RegexOptions.IgnoreCase);

                    if (!string.IsNullOrWhiteSpace(titulo))
                        return titulo.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// Extrae la prioridad
        /// Entrada: command - comando en minúsculas
        /// Salida: "Alta", "Media", "Baja" o null
        /// </summary>
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

        /// <summary>
        /// Extrae fecha y hora del comando
        /// Entrada: command - comando en minúsculas
        /// Salida: (inicio, fin, hora ambigua, fecha base)
        /// </summary>
        private (DateTimeOffset? start, DateTimeOffset? end, int? ambiguousHour, DateTimeOffset? ambiguousBaseDate) ExtractFechaHora(string command)
        {
            var now = DateTimeOffset.Now;
            DateTimeOffset? baseDate = null;

            if (command.Contains("hoy"))
            {
                baseDate = now.Date;
            }
            else if (command.Contains("mañana"))
            {
                baseDate = now.Date.AddDays(1);
            }
            else if (command.Contains("pasado mañana"))
            {
                baseDate = now.Date.AddDays(2);
            }
            else
            {
                // ✅ PATRÓN MEJORADO: detecta CON o SIN "el"
                var dateMatch = Regex.Match(command, @"(?:el\s+)?(\d{1,2})\s+de\s+(\w+)(?:\s+(?:del|de)\s+(\d{4}))?");
                if (dateMatch.Success)
                {
                    if (int.TryParse(dateMatch.Groups[1].Value, out var day))
                    {
                        var monthName = dateMatch.Groups[2].Value.ToLowerInvariant();
                        var month = GetMonthNumber(monthName);

                        if (month > 0)
                        {
                            // ✅ Detectar año si está presente
                            var year = now.Year;
                            if (dateMatch.Groups[3].Success && int.TryParse(dateMatch.Groups[3].Value, out var parsedYear))
                            {
                                year = parsedYear;
                            }
                            else if (month < now.Month || (month == now.Month && day < now.Day))
                            {
                                year++;
                            }

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

            if (!baseDate.HasValue)
                return (null, null, null, null);

            var duracion = ExtractDuracion(command);

            var (hour, needsClarification) = ExtractHora(command);

            if (needsClarification && hour.HasValue)
            {
                return (null, null, hour.Value, baseDate.Value);
            }

            if (hour.HasValue)
            {
                var start = baseDate.Value.AddHours(hour.Value);

                if (duracion.HasValue)
                {
                    var end = start.AddHours(duracion.Value);
                    return (start, end, null, null);
                }

                return (start, null, null, null);
            }

            var defaultStart = baseDate.Value.AddHours(9);

            if (duracion.HasValue)
            {
                var defaultEnd = defaultStart.AddHours(duracion.Value);
                return (defaultStart, defaultEnd, null, null);
            }

            return (defaultStart, null, null, null);
        }

        /// <summary>
        /// Extrae hora y detecta si necesita clarificación AM/PM
        /// Soporta: "3 pm", "3:00 pm", "5 y media", "5:30", "después de mediodía"
        /// </summary>
        private (int? hora, bool needsClarification) ExtractHora(string command)
        {
            int? horaBase = null;
            int minutos = 0;
            bool hasExplicitAMPM = false;
            bool isPM = false;

            // ========================================
            // NORMALIZAR: Unificar variaciones de "mediodía"
            // ========================================
            command = command.Replace("después de mediodía", "despuesmediodia")
                             .Replace("despues de mediodia", "despuesmediodia")
                             .Replace("después del mediodía", "despuesmediodia")
                             .Replace("despues del mediodia", "despuesmediodia")
                             .Replace("antes de mediodía", "antesmediodia")
                             .Replace("antes de mediodia", "antesmediodia")
                             .Replace("antes del mediodía", "antesmediodia")
                             .Replace("antes del mediodia", "antesmediodia");

            // ========================================
            // CASO 1A: Hora con minutos + PM/AM: "3:00 pm", "5:30 despuesmediodia"
            // ========================================
            var match1 = Regex.Match(command, @"(\d{1,2}):(\d{2})\s*(pm|am|de la tarde|de la mañana|despuesmediodia|antesmediodia)");
            if (match1.Success)
            {
                horaBase = int.Parse(match1.Groups[1].Value);
                minutos = int.Parse(match1.Groups[2].Value);
                hasExplicitAMPM = true;

                var indicator = match1.Groups[3].Value;
                isPM = indicator.Contains("pm") || indicator.Contains("tarde") || indicator.Contains("despues");
            }

            // ========================================
            // CASO 1B: Hora con minutos SIN PM/AM: "3:30", "5:45"
            // ========================================
            if (!hasExplicitAMPM)
            {
                var match2 = Regex.Match(command, @"(\d{1,2}):(\d{2})(?!\s*(pm|am|tarde|mañana))");
                if (match2.Success)
                {
                    horaBase = int.Parse(match2.Groups[1].Value);
                    minutos = int.Parse(match2.Groups[2].Value);

                    // Formato 24h (14:00, 17:30)
                    if (horaBase >= 13 && horaBase <= 23)
                    {
                        return (horaBase.Value, false);
                    }

                    // Hora ambigua con minutos (3:30, 11:45)
                    if (horaBase >= 1 && horaBase <= 12)
                    {
                        return (horaBase.Value, true);
                    }
                }
            }

            // ========================================
            // CASO 2: Hora con PM/AM SIN minutos: "3 pm", "5 despuesmediodia"
            // ========================================
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

            // ========================================
            // CASO 3: "y media" = :30
            // ========================================
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

            // ========================================
            // CASO 4: "y cuarto" = :15
            // ========================================
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

            // ========================================
            // CASO 5: "menos cuarto" = :45
            // ========================================
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

            // ========================================
            // CASO 6: Solo "despuesmediodia" sin hora
            // ========================================
            if (horaBase == null && command.Contains("despuesmediodia"))
            {
                return (14, false); // Default 2 PM
            }

            // ========================================
            // CASO 7: Solo "antesmediodia" sin hora
            // ========================================
            if (horaBase == null && command.Contains("antesmediodia"))
            {
                return (10, false); // Default 10 AM
            }

            // ========================================
            // CASO 8: Hora simple sin indicador: "a las 3"
            // ========================================
            if (horaBase == null)
            {
                var matchSimple = Regex.Match(command, @"(?:a las|las)\s+(\d{1,2})(?!\s*(pm|am|tarde|mañana|:))");
                if (matchSimple.Success)
                {
                    horaBase = int.Parse(matchSimple.Groups[1].Value);
                    minutos = 0;
                }
            }

            // ========================================
            // CALCULAR HORA FINAL
            // ========================================

            if (horaBase == null)
                return (null, false);

            // Si tiene AM/PM explícito
            if (hasExplicitAMPM)
            {
                int horaFinal;

                if (isPM)
                {
                    horaFinal = (horaBase == 12) ? 12 : horaBase.Value + 12;
                }
                else // AM
                {
                    horaFinal = (horaBase == 12) ? 0 : horaBase.Value;
                }

                return (horaFinal, false);
            }

            // Hora ambigua (1-12 sin AM/PM)
            if (horaBase >= 1 && horaBase <= 12)
            {
                return (horaBase.Value, true);
            }

            // Formato 24h
            if (horaBase >= 0 && horaBase <= 23)
            {
                return (horaBase.Value, false);
            }

            return (null, false);
        }

        private int? ExtractDuracion(string command)
        {
            // "durante 2 horas", "por 3 horas"
            var match1 = Regex.Match(command, @"(?:durante|por)\s+(\d+)\s+horas?");
            if (match1.Success && int.TryParse(match1.Groups[1].Value, out var horas1))
                return horas1;

            // "de 3 a 5" (rango de horas)
            var match2 = Regex.Match(command, @"de\s+(\d{1,2})\s+a\s+(\d{1,2})");
            if (match2.Success)
            {
                if (int.TryParse(match2.Groups[1].Value, out var inicio) &&
                    int.TryParse(match2.Groups[2].Value, out var fin))
                {
                    if (fin > inicio)
                        return fin - inicio;
                    if (fin < inicio)
                        return (fin + 12) - inicio;
                }
            }

            // "2 horas", "tres horas"
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
                if (duracion.HasValue)
                    return duracion.Value;
            }

            return null;
        }

        /// <summary>
        /// Convierte nombre de mes a número
        /// </summary>
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

            // SOLO detectar cuando dice explícitamente "asignar"/"asígnale"
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