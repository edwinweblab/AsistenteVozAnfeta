using System;

namespace Anfeta.UI.Services.Activity
{
    public sealed class ActivityEditFieldExtractor
    {
        public string? TryExtractField(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return null;

            var t = userText.Trim().ToLowerInvariant();

            if (t.Contains("titulo") || t.Contains("título") || t.Contains("nombre"))
                return "titulo";

            if (t.Contains("prioridad"))
                return "prioridad";

            if (t.Contains("estado") || t.Contains("status"))
                return "status";

            if (t.Contains("fecha inicio") || t.Contains("inicio") || t.Contains("empieza"))
                return "dueStart";

            if (t.Contains("fecha fin") || t.Contains("fin") || t.Contains("termina"))
                return "dueEnd";

            if (t.Contains("nota") || t.Contains("anotacion") || t.Contains("anotación"))
                return "anotaciones";

            if (t.Contains("pasos") || t.Contains("links") || t.Contains("enlaces"))
                return "pasosYLinks";

            return null;
        }
    }
}