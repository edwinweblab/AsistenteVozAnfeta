// Services/FastCommandClassifier.cs
using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services
{
    /// <summary>
    /// Clasificación rápida de comandos sin IA
    /// </summary>
    public sealed class FastCommandClassifier
    {
        private readonly CapabilityRegistry _registry;

        public FastCommandClassifier(CapabilityRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>
        /// Clasificación rápida sin IA (regex + patrones)
        /// </summary>
        public (bool handled, InterpretationResult? result) TryFastClassify(string speech)
        {
            var lower = speech.Trim().ToLowerInvariant();

            // ✅ NUEVO: CreateActivity (PRIORIDAD ALTA)
            if (IsCreateActivityCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "CreateActivity",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "actividades",
                    Action = "create",
                    Confidence = 0.95,
                    NeedsConfirmation = false,
                });
            }

            // OpenApp: "abre chrome", "abre calculadora"
            if (lower.StartsWith("abre ") || lower.StartsWith("abrir "))
            {
                var appName = lower.Replace("abre ", "").Replace("abrir ", "").Trim();
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

            // CloseApp: "cierra", "cerrar"
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

            // WebSearch: "busca python", "buscar react"
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

            // MinimizeAll: "minimiza todo"
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

            return (false, null);
        }

        /// <summary>
        /// Detecta comando de crear actividad
        /// </summary>
        private bool IsCreateActivityCommand(string lower)
        {
            // Patrones directos
            var patterns = new[]
            {
                @"^crear actividad",
                @"^crea actividad",
                @"^nueva actividad",
                @"^registrar actividad",
                @"^agregar actividad",
                @"^añadir actividad",
                @"^crear tarea",
                @"^nueva tarea",
                @"^agendar actividad",
                @"^agrega actividad"
            };

            foreach (var pattern in patterns)
            {
                if (Regex.IsMatch(lower, pattern))
                    return true;
            }

            // Variaciones con artículos
            if (lower.StartsWith("crea una actividad") ||
                lower.StartsWith("crear una actividad") ||
                lower.StartsWith("crea la actividad") ||
                lower.StartsWith("crear la actividad"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Mapea sinónimo a appKey
        /// </summary>
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