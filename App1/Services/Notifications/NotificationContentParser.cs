using System;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Notifications
{
    public sealed record ParsedNotificationInfo(
        string Domain,
        string PriorityLabel,
        string PriorityCode,
        bool IsUrgent,
        string CleanTitle,
        string ToastTitle,
        string SpokenSummary);

    public static class NotificationContentParser
    {
        private static readonly Regex DomainPattern = new(
            @"(?<![\w@])(?:https?://)?(?:www\.)?" +
            @"(?<domain>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+" +
            @"(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|" +
            @"com|mx|org|net|io|co|app|dev))" +
            @"(?=$|[/:?#\s)\]}>.,;!])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex PriorityPattern = new(
            @"(?<![\p{L}\p{Nd}_])(?<tag>jjohn|nneft|kkarl|bbria|ggena|iisai|iisaia|eemma|aandr|ssote|eedua|aacal)\s*(?<v>001|002|003|00)(?![\p{L}\p{Nd}_])|" +
            @"(?<![\p{L}\p{Nd}_.:\-/])(?<v>001|002|003|00)\s*(?<tag>jjohn|nneft|kkarl|bbria|ggena|iisai|iisaia|eemma|aandr|ssote|eedua|aacal)(?![\p{L}\p{Nd}_])|" +
            @"(?<![\p{L}\p{Nd}_.:\-/])(?<v>001|002|003|00)(?:prt[a-z0-9_-]*)?(?![\p{L}\p{Nd}_.:\-/])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static ParsedNotificationInfo Parse(string? rawTitle, string? senderName = null)
        {
            var text = (rawTitle ?? string.Empty).Trim();

            // 1. Extraer Dominio
            var domainMatch = DomainPattern.Match(text);
            var domain = domainMatch.Success
                ? domainMatch.Groups["domain"].Value.Trim().TrimEnd('.').ToLowerInvariant()
                : string.Empty;

            // 2. Extraer Prioridad (00 = Urgente, 001 = Primaria, 002 = Secundaria, 003 = Recordatorio)
            var prioMatch = PriorityPattern.Match(text);
            var prioCode = prioMatch.Success ? prioMatch.Groups["v"].Value.ToLowerInvariant() : "";

            var isUrgent = prioCode == "00" ||
                           text.Contains("urgente", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("00 · urgente", StringComparison.OrdinalIgnoreCase);

            string priorityLabel;
            if (isUrgent)
            {
                priorityLabel = "Urgente";
                prioCode = "00";
            }
            else
            {
                priorityLabel = prioCode switch
                {
                    "001" => "Primaria",
                    "002" => "Secundaria",
                    "003" => "Recordatorio",
                    _ => "Actividad"
                };
            }

            // 3. Limpiar Asunto / Acción concreta
            var clean = CleanTitle(text, domain);

            // 4. Formar ToastTitle
            string toastTitle;
            var domainPart = !string.IsNullOrEmpty(domain) ? $" [{domain}]" : "";

            if (isUrgent)
            {
                toastTitle = $"🚨{domainPart} · Actividad Urgente (00)";
            }
            else if (priorityLabel is "Primaria" or "Secundaria")
            {
                var icon = priorityLabel == "Primaria" ? "⭐" : "🔹";
                toastTitle = $"{icon}{domainPart} · {priorityLabel}";
            }
            else
            {
                toastTitle = !string.IsNullOrEmpty(domain)
                    ? $"📌 [{domain}] · Nueva actividad"
                    : "📌 Nueva actividad asignada";
            }

            // 5. Formar SpokenSummary (TTS)
            // Diseñado exactamente según lo pedido por John:
            // "Tienes una nueva de voydeplaya.com... si es secundario o primaria o urgente... revisar sitio caído"
            var spokenSummary = BuildSpokenText(senderName, domain, priorityLabel, isUrgent, clean);

            return new ParsedNotificationInfo(
                Domain: domain,
                PriorityLabel: priorityLabel,
                PriorityCode: prioCode,
                IsUrgent: isUrgent,
                CleanTitle: clean,
                ToastTitle: toastTitle,
                SpokenSummary: spokenSummary);
        }

        private static string CleanTitle(string text, string domain)
        {
            var clean = text;

            // Limpieza de URLs completas
            clean = Regex.Replace(clean, @"https?://\S+", " ");

            // Limpieza de GUIDs / IDs hex
            clean = Regex.Replace(clean, @"\b[0-9a-f]{32}\b", " ", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\b\d{4}-\d{2}-\d{2}\b", " ");

            // Remover códigos de usuarios de Notion
            clean = Regex.Replace(clean, @"(?i)\b(jjohn|nneft|kkarl|bbria|ggena|iisai|iisaia|eemma|aandr|ssote|eedua|aacal)\b", " ");

            // Remover códigos de fases de actividad
            clean = Regex.Replace(clean, @"(?i)\b(prtuzrevision|rtuzrevision|zrevision|sprtuz|aprtuz|prtuz|rtuz|ztuz|tuz)\b", " ");

            // Remover categorías y tags internos
            clean = Regex.Replace(clean, @"(?i)\b(wwebs|sseo|aads|aapli|pprog|ccobr|bbibl|prtuzCOBRAR|prtuzPAGAR)\b", " ");

            // Remover códigos de prioridad si están al inicio o separados
            clean = Regex.Replace(clean, @"(?i)\b(001|002|003|00)\b", " ");

            // Remover extensiones de archivo
            clean = Regex.Replace(clean, @"\.(pdf|docx?|xlsx?|png|jpg|jpeg)\b", " ", RegexOptions.IgnoreCase);

            // Si se detectó dominio, eliminarlo de la frase para evitar repetición
            if (!string.IsNullOrEmpty(domain))
            {
                clean = Regex.Replace(clean, Regex.Escape(domain), " ", RegexOptions.IgnoreCase);
            }

            // Limpieza de signos sobrantes
            clean = Regex.Replace(clean, @"[\[\](){}_*#|~`>]+", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '—', '·', ':');

            if (clean.Length > 0)
            {
                // Capitalizar primera letra
                clean = char.ToUpper(clean[0]) + (clean.Length > 1 ? clean[1..] : "");
            }
            else
            {
                clean = !string.IsNullOrEmpty(domain) ? $"Actividad en {domain}" : "Nueva actividad";
            }

            return clean;
        }

        private static string BuildSpokenText(
            string? senderName,
            string domain,
            string priorityLabel,
            bool isUrgent,
            string cleanTitle)
        {
            var senderPart = !string.IsNullOrWhiteSpace(senderName) ? $" de {senderName.Trim()}" : "";
            var domainPart = !string.IsNullOrEmpty(domain) ? $" en {domain}" : "";

            string intro;
            if (isUrgent)
            {
                intro = $"Atención: Actividad urgente{senderPart}{domainPart}.";
            }
            else if (priorityLabel is "Primaria" or "Secundaria")
            {
                intro = $"Nueva actividad {priorityLabel.ToLowerInvariant()}{senderPart}{domainPart}.";
            }
            else
            {
                intro = $"Nueva actividad{senderPart}{domainPart}.";
            }

            var spoken = $"{intro} {cleanTitle}";

            // Truncar con puntuación si es excesivamente largo
            if (spoken.Length > 140)
            {
                var dotIdx = spoken.IndexOf('.', 60);
                if (dotIdx > 0 && dotIdx < 140)
                    spoken = spoken[..(dotIdx + 1)];
                else
                    spoken = spoken[..135].Trim() + "…";
            }

            return spoken;
        }
    }
}
