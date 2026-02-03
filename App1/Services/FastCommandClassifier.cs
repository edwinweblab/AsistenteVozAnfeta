using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Anfeta.UI.Services
{
    // Elimina la clase anidada redundante para evitar CS0542
    public sealed class FastCommandClassifier
    {
        private readonly CapabilityRegistry _registry;

        public FastCommandClassifier(CapabilityRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Clasificación rápida sin IA (regex)</summary>
        /// <summary>Clasificación rápida sin IA (regex + patrones)</summary>
        public (bool handled, InterpretationResult? result) TryFastClassify(string speech)
        {
            var lower = speech.Trim().ToLowerInvariant();

            // OpenApp obvio: "abre chrome", "abre calculadora"
            if (lower.StartsWith("abre ") || lower.StartsWith("abrir "))
            {
                var appName = lower.Replace("abre ", "").Replace("abrir ", "").Trim();

                // Remover artículos (el, la, los, las)
                appName = appName.Replace("el ", "").Replace("la ", "")
                                 .Replace("los ", "").Replace("las ", "").Trim();

                var appKey = MapSynonymToAppKey(appName);

                if (appKey != null)
                {
                    return (true, new InterpretationResult
                    {
                        Intent = "OpenApp",
                        Scope = "LOCAL",
                        AppKey = appKey,
                        Confidence = 0.95,
                        NeedsConfirmation = false
                    });
                }
            }

            // CloseApp: "cierra", "cerrar", "ciérralo"
            if (lower == "cierra" || lower == "cerrar" ||
                lower == "ciérralo" || lower == "cierra esto" ||
                lower.StartsWith("cierra ") || lower.StartsWith("cerrar "))
            {
                return (true, new InterpretationResult
                {
                    Intent = "CloseApp",
                    Scope = "LOCAL",
                    Confidence = 0.9,
                    NeedsConfirmation = false
                });
            }

            // WebSearch obvio: "busca python", "buscar react"
            if (lower.StartsWith("busca ") || lower.StartsWith("buscar "))
            {
                var query = lower.Replace("busca ", "").Replace("buscar ", "").Trim();

                if (!string.IsNullOrWhiteSpace(query))
                {
                    return (true, new InterpretationResult
                    {
                        Intent = "WebSearch",
                        Scope = "LOCAL",
                        Confidence = 0.9,
                        NeedsConfirmation = false,
                        Params = new Dictionary<string, object> { ["query"] = query }
                    });
                }
            }

            // MinimizeAll: "minimiza todo", "minimizar todo"
            if (lower.Contains("minimiza todo") || lower.Contains("minimizar todo"))
            {
                return (true, new InterpretationResult
                {
                    Intent = "MinimizeAll",
                    Scope = "LOCAL",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            // Comandos ambiguos → IA
            return (false, null);
        }
        private string? MapSynonymToAppKey(string synonym)
        {
            var allApps = _registry.GetAllApps();
            foreach (var app in allApps)
            {
                if (app.AppKey.Equals(synonym, StringComparison.OrdinalIgnoreCase))
                    return app.AppKey;

                if (app.Synonyms.Any(s => s.Equals(synonym, StringComparison.OrdinalIgnoreCase)))
                    return app.AppKey;
            }
            return null;
        }
    }
}
