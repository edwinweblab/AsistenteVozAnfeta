using Anfeta.UI.Models.Interpretation;
using System.Linq;
using System.Text;

namespace Anfeta.UI.Services.Interpretation
{
    /// <summary>Construye prompts dinámicos según contexto</summary>
    public sealed class PromptBuilder
    {
        private readonly ContextManager _contextManager;
        private readonly CapabilityRegistry _registry;

        public PromptBuilder(ContextManager contextManager, CapabilityRegistry registry)
        {
            _contextManager = contextManager;
            _registry = registry;
        }

        /// <summary>Construir prompt adaptado al contexto actual</summary>
        public string BuildPrompt(string userMessage)
        {
            var ctx = _contextManager.GetContext();
            var sb = new StringBuilder();

            // Header base
            sb.AppendLine("Eres un asistente que SOLO responde JSON válido. NUNCA agregues texto fuera del JSON.");
            sb.AppendLine();

            // Template según contexto
            if (ctx.CurrentApp != null)
                AppendActiveAppTemplate(sb, ctx.CurrentApp, userMessage);
            else
                AppendNoAppTemplate(sb, userMessage);

            return sb.ToString();
        }

        /// <summary>Template cuando HAY app activa - OPTIMIZADO</summary>
        private void AppendActiveAppTemplate(StringBuilder sb, ActiveApp app, string userMessage)
        {
            var appDef = _registry.GetApp(app.AppKey);
            var friendlyName = appDef?.FriendlyName ?? app.AppKey;

            sb.AppendLine($"CONTEXTO: '{friendlyName}' abierto ({app.Category}).");
            sb.AppendLine($"Capacidades: {string.Join(", ", app.Capabilities)}");
            sb.AppendLine();

            // SOLO 2 ejemplos compactos
            sb.AppendLine("Ejemplos:");

            // Ejemplo 1: Cerrar app
            sb.AppendLine($"U: \"cierra\" -> {{\"plain_text\":\"Cerrando {friendlyName}\",\"interpretation\":{{\"intent\":\"CloseApp\",\"scope\":\"LOCAL\",\"app_key\":\"{app.AppKey}\",\"confidence\":0.95,\"needs_confirmation\":true}}}}");

            // Ejemplo 2: Búsqueda (solo si es navegador)
            if (app.Category == "navegador")
            {
                sb.AppendLine($"U: \"busca python\" -> {{\"plain_text\":\"Buscando python\",\"interpretation\":{{\"intent\":\"WebSearch\",\"scope\":\"LOCAL\",\"app_key\":\"{app.AppKey}\",\"confidence\":0.9,\"params\":{{\"query\":\"python\"}},\"needs_confirmation\":true}}}}");
            }

            sb.AppendLine();
            AppendCompactRules(sb);
            AppendUserCommand(sb, userMessage);
        }

        /// <summary>Template cuando NO hay app activa - ULTRA COMPACTO</summary>
        private void AppendNoAppTemplate(StringBuilder sb, string userMessage)
        {
            var allApps = _registry.GetAllApps();

            sb.AppendLine("Sin apps activas. Usuario puede abrir apps.");
            sb.AppendLine();

            // COMPACTO: 1 línea con todas las apps
            var appsLine = string.Join(", ", allApps.Select(a =>
            {
                var synonyms = a.Synonyms.Count > 0 ? $" [{string.Join("/", a.Synonyms.Take(2))}]" : "";
                return $"{a.AppKey}{synonyms}";
            }));
            sb.AppendLine($"Apps: {appsLine}");
            sb.AppendLine();

            // SOLO 2 ejemplos en 1 línea cada uno
            sb.AppendLine("Ejemplos:");
            sb.AppendLine("U: \"abre chrome\" -> {\"plain_text\":\"Abriendo Chrome\",\"interpretation\":{\"intent\":\"OpenApp\",\"scope\":\"LOCAL\",\"app_key\":\"chrome\",\"confidence\":0.95,\"needs_confirmation\":true}}");
            sb.AppendLine("U: \"cierra\" -> {\"plain_text\":\"No hay apps\",\"interpretation\":{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"confidence\":0.2,\"needs_confirmation\":true}}");
            sb.AppendLine();

            AppendCompactRules(sb);
            AppendUserCommand(sb, userMessage);
        }

        /// <summary>Reglas ultra compactas</summary>
        private static void AppendCompactRules(StringBuilder sb)
        {
            sb.AppendLine("REGLAS: intent (OpenApp|CloseApp|WebSearch|Unknown), scope (LOCAL|API), app_key de lista, NO múltiples opciones, SOLO JSON");
        }

        /// <summary>Agregar comando del usuario</summary>
        private static void AppendUserCommand(StringBuilder sb, string userMessage)
        {
            var escaped = userMessage.Replace("\"", "\\\"");
            sb.AppendLine($"U: \"{escaped}\"");
        }
    }
}