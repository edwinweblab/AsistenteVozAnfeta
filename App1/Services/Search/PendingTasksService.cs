using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;
using Anfeta.UI.Models.Search;

namespace Anfeta.UI.Services.Search
{
    public static class PendingTasksService
    {
        public const string LS_PendingTasks = "anfeta_pending_tasks_v1";

        public static event EventHandler? TasksChanged;

        public static List<SavedSearch> LoadTasks()
        {
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values[LS_PendingTasks] as string;
                if (string.IsNullOrWhiteSpace(raw))
                    return new List<SavedSearch>();

                var list = JsonSerializer.Deserialize<List<SavedSearch>>(raw) ?? new List<SavedSearch>();
                foreach (var item in list)
                {
                    item.IsPendingTask = true;
                }
                return list.OrderBy(it => ParseSortDate(it.ScheduledDate)).ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("No se pudieron leer los pendientes guardados. No se reemplazaron los datos.", ex);
            }
        }

        public static void SaveTasks(IEnumerable<SavedSearch> tasks)
        {
            try
            {
                var sorted = tasks.OrderBy(it => ParseSortDate(it.ScheduledDate)).ToList();
                var raw = JsonSerializer.Serialize(sorted);
                ApplicationData.Current.LocalSettings.Values[LS_PendingTasks] = raw;
                NotifyTasksChanged();
            }
            catch (Exception ex) { throw new InvalidOperationException("No se pudo guardar el pendiente. El texto se conserva para volver a intentar.", ex); }
        }

        public static void AddTask(SavedSearch task)
        {
            var tasks = LoadTasks();
            task.IsPendingTask = true;
            tasks.Add(task);
            SaveTasks(tasks);
        }

        public static void NotifyTasksChanged()
        {
            if (TasksChanged == null) return;
            foreach (EventHandler handler in TasksChanged.GetInvocationList())
            {
                try { handler(null, EventArgs.Empty); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pendientes] No se pudo actualizar una vista: " + ex.Message); }
            }
        }

        public static DateTime ParseSortDate(string dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return DateTime.MaxValue;

            var trimmed = dateStr.Trim();
            if (DateTime.TryParseExact(trimmed, "dd/MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ||
                DateTime.TryParseExact(trimmed, "d/M", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                return new DateTime(DateTime.Today.Year, dt.Month, dt.Day);
            }
            if (DateTime.TryParse(trimmed, out var parsed))
                return parsed;

            return DateTime.MaxValue;
        }
    }

    public static class QuickTaskParser
    {
        private static readonly Regex PriorityRegex = new(
            @"\b(p1|p2|p3|p4|urgente|alta|media|baja)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TagRegex = new(
            @"(?<![\w/])([#@][\wáéíóúÁÉÍÓÚñÑ]+)",
            RegexOptions.Compiled);

        private static readonly Regex DateExplicitRegex = new(
            @"\b(\d{1,2})[/.-](\d{1,2})\b",
            RegexOptions.Compiled);

        private static readonly Regex DateWordRegex = new(
            @"\b(pasado mañana|hoy|mañana|lunes|martes|miércoles|miercoles|jueves|viernes|sábado|sabado|domingo)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RelativeTimeRegex = new(@"\ben\s+(\d+|un|una|uno|dos|tres|cuatro|cinco|diez|quince|veinte|treinta)\s+(minutos?|horas?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TimeRegex = new(@"\b(?:a las?\s+)?(\d{1,2}):(\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static SavedSearch Parse(string input, DateTime? clock = null)
        {
            var now = clock ?? DateTime.Now;
            if (string.IsNullOrWhiteSpace(input))
            {
                return new SavedSearch
                {
                    Title = "",
                    ScheduledDate = now.ToString("yyyy-MM-dd"),
                    IsPendingTask = true
                };
            }

            var text = input.Trim();
            var urls = new List<string>();
            text = Regex.Replace(text, @"https?://[^\s<>""']+", match => { urls.Add(match.Value); return "\uE000URL" + (urls.Count - 1) + "\uE001"; }, RegexOptions.IgnoreCase);
            string priority = "";
            string tag = "";
            string scheduledDate = "";

            // 1. Prioridad
            var prioMatch = PriorityRegex.Match(text);
            if (prioMatch.Success)
            {
                var val = prioMatch.Value.ToLowerInvariant();
                priority = val switch
                {
                    "urgente" => "P1",
                    "alta" => "P2",
                    "media" => "P3",
                    "baja" => "P4",
                    _ => val.ToUpperInvariant()
                };
                text = PriorityRegex.Replace(text, "").Trim();
            }

            // 2. Persona / Tag (#John, @Neftalí)
            var tagMatch = TagRegex.Match(text);
            if (tagMatch.Success)
            {
                tag = tagMatch.Value;
                text = TagRegex.Replace(text, "").Trim();
            }

            // 3. Fecha
            var relative = RelativeTimeRegex.Match(text);
            if (relative.Success)
            {
                var words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["un"]=1,["una"]=1,["uno"]=1,["dos"]=2,["tres"]=3,["cuatro"]=4,["cinco"]=5,["diez"]=10,["quince"]=15,["veinte"]=20,["treinta"]=30 };
                var number = int.TryParse(relative.Groups[1].Value, out var numeric) ? numeric : words[relative.Groups[1].Value];
                if (number < 1 || number > 10080) throw new FormatException("Usa un intervalo entre 1 y 10080 minutos u horas.");
                var due = relative.Groups[2].Value.StartsWith("hora", StringComparison.OrdinalIgnoreCase) ? now.AddHours(number) : now.AddMinutes(number);
                scheduledDate = due.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                text = text.Remove(relative.Index, relative.Length).Trim();
            }
            var wordDateMatch = DateWordRegex.Match(text);
            if (string.IsNullOrWhiteSpace(scheduledDate) && wordDateMatch.Success)
            {
                var w = wordDateMatch.Value.ToLowerInvariant();
                scheduledDate = ResolveRelativeDate(w, now.Date);
                text = DateWordRegex.Replace(text, "").Trim();
            }
            else if (string.IsNullOrWhiteSpace(scheduledDate))
            {
                var explMatch = DateExplicitRegex.Match(text);
                if (explMatch.Success)
                {
                    if (int.TryParse(explMatch.Groups[1].Value, out var day) &&
                        int.TryParse(explMatch.Groups[2].Value, out var month))
                    {
                        scheduledDate = new DateTime(now.Year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        text = DateExplicitRegex.Replace(text, "").Trim();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(scheduledDate))
            {
                scheduledDate = now.ToString("yyyy-MM-dd");
            }
            var time = TimeRegex.Match(text);
            if (!relative.Success && time.Success)
            {
                var hour = int.Parse(time.Groups[1].Value); var minute = int.Parse(time.Groups[2].Value);
                if (hour > 23 || minute > 59) throw new FormatException("La hora debe estar entre 00:00 y 23:59.");
                scheduledDate = DateTime.Parse(scheduledDate, CultureInfo.InvariantCulture).Date.AddHours(hour).AddMinutes(minute).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                text = text.Remove(time.Index, time.Length).Trim();
            }

            // Limpieza de espacios múltiples
            text = Regex.Replace(text, @"\s+", " ").Trim();
            for (var index = 0; index < urls.Count; index++) text = text.Replace("\uE000URL" + index + "\uE001", urls[index]);

            return new SavedSearch
            {
                Title = text,
                Query = tag,
                Tag = tag,
                Priority = priority,
                ScheduledDate = scheduledDate,
                IsPendingTask = true
            };
        }

        private static string ResolveRelativeDate(string keyword, DateTime today)
        {
            switch (keyword)
            {
                case "hoy":
                    return today.ToString("yyyy-MM-dd");
                case "mañana":
                    return today.AddDays(1).ToString("yyyy-MM-dd");
                case "pasado mañana":
                    return today.AddDays(2).ToString("yyyy-MM-dd");
                case "lunes":
                    return NextDayOfWeek(today, DayOfWeek.Monday).ToString("yyyy-MM-dd");
                case "martes":
                    return NextDayOfWeek(today, DayOfWeek.Tuesday).ToString("yyyy-MM-dd");
                case "miércoles":
                case "miercoles":
                    return NextDayOfWeek(today, DayOfWeek.Wednesday).ToString("yyyy-MM-dd");
                case "jueves":
                    return NextDayOfWeek(today, DayOfWeek.Thursday).ToString("yyyy-MM-dd");
                case "viernes":
                    return NextDayOfWeek(today, DayOfWeek.Friday).ToString("yyyy-MM-dd");
                case "sábado":
                case "sabado":
                    return NextDayOfWeek(today, DayOfWeek.Saturday).ToString("yyyy-MM-dd");
                case "domingo":
                    return NextDayOfWeek(today, DayOfWeek.Sunday).ToString("yyyy-MM-dd");
                default:
                    return today.ToString("yyyy-MM-dd");
            }
        }

        private static DateTime NextDayOfWeek(DateTime from, DayOfWeek targetDay)
        {
            int start = (int)from.DayOfWeek;
            int target = (int)targetDay;
            int daysToAdd = (target - start + 7) % 7;
            if (daysToAdd == 0) daysToAdd = 7; // Próxima semana si es el mismo día
            return from.AddDays(daysToAdd);
        }
    }
}
