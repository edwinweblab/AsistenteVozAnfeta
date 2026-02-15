// Services/Activity/ActivityFieldExtractor.cs
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Anfeta.UI.Models;

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

            var (start, end) = ExtractFechaHora(lower);
            state.DueStart = start;
            state.DueEnd = end;

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
        /// Salida: (inicio, fin) o (null, null)
        /// </summary>
        private (DateTimeOffset? start, DateTimeOffset? end) ExtractFechaHora(string command)
        {
            var now = DateTimeOffset.Now;
            DateTimeOffset? baseDate = null;

            // Detectar día
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
                // Intentar extraer "el 15 de febrero"
                var dateMatch = Regex.Match(command, @"el\s+(\d{1,2})\s+de\s+(\w+)");
                if (dateMatch.Success)
                {
                    if (int.TryParse(dateMatch.Groups[1].Value, out var day))
                    {
                        var monthName = dateMatch.Groups[2].Value.ToLowerInvariant();
                        var month = GetMonthNumber(monthName);

                        if (month > 0)
                        {
                            var year = now.Year;
                            // Si la fecha ya pasó este año, usar el siguiente
                            if (month < now.Month || (month == now.Month && day < now.Day))
                                year++;

                            try
                            {
                                baseDate = new DateTimeOffset(year, month, day, 0, 0, 0, now.Offset);
                            }
                            catch
                            {
                                // Fecha inválida, ignorar
                            }
                        }
                    }
                }
            }

            // Si no hay fecha base, retornar null
            if (!baseDate.HasValue)
                return (null, null);

            // Extraer hora
            var hour = ExtractHora(command);

            if (hour.HasValue)
            {
                var start = baseDate.Value.AddHours(hour.Value);
                var end = start.AddHours(1); // Default: 1 hora de duración
                return (start, end);
            }

            // Si no hay hora específica, usar 9 AM por default
            return (baseDate.Value.AddHours(9), baseDate.Value.AddHours(10));
        }

        /// <summary>
        /// Extrae la hora del comando
        /// Entrada: command - comando en minúsculas
        /// Salida: hora en formato 24h o null
        /// </summary>
        private int? ExtractHora(string command)
        {
            // "a las 5", "5 pm", "17:00", "5:30 de la tarde"

            // Patrón 1: "a las X" o "X pm/am"
            var match1 = Regex.Match(command, @"(?:a las|las)\s+(\d{1,2})(?::(\d{2}))?\s*(pm|am|de la tarde|de la mañana)?");
            if (match1.Success)
            {
                if (int.TryParse(match1.Groups[1].Value, out var hora))
                {
                    var esPM = match1.Groups[3].Value.Contains("pm") ||
                               match1.Groups[3].Value.Contains("tarde");
                    var esAM = match1.Groups[3].Value.Contains("am") ||
                               match1.Groups[3].Value.Contains("mañana");

                    if (esPM && hora < 12)
                        hora += 12;
                    else if (esAM && hora == 12)
                        hora = 0;

                    return hora;
                }
            }

            // Patrón 2: solo número seguido de pm/am
            var match2 = Regex.Match(command, @"(\d{1,2})\s*(pm|am|de la tarde|de la mañana)");
            if (match2.Success)
            {
                if (int.TryParse(match2.Groups[1].Value, out var hora))
                {
                    var esPM = match2.Groups[2].Value.Contains("pm") ||
                               match2.Groups[2].Value.Contains("tarde");

                    if (esPM && hora < 12)
                        hora += 12;

                    return hora;
                }
            }

            // Patrón 3: formato 24h "17:00"
            var match3 = Regex.Match(command, @"(\d{1,2}):(\d{2})");
            if (match3.Success)
            {
                if (int.TryParse(match3.Groups[1].Value, out var hora))
                    return hora;
            }

            // ⬇️ AGREGAR ESTA LÓGICA NUEVA ⬇️

            // Patrón 4: Solo número sin AM/PM (asumir PM si es 1-11, AM si es 12)
            var match4 = Regex.Match(command, @"(?:a las|las)\s+(\d{1,2})(?!\s*(pm|am|tarde|mañana))");
            if (match4.Success)
            {
                if (int.TryParse(match4.Groups[1].Value, out var hora))
                {
                    // Si es 1-11 sin especificar → asumir PM (tarde/noche)
                    if (hora >= 1 && hora <= 11)
                        return hora + 12;

                    // Si es 12 sin especificar → asumir mediodía (12 PM)
                    if (hora == 12)
                        return 12;
                }
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
    }
}