// Services/FastCommandClassifier.cs
using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services
{
    /// <summary>
    /// Clasificación rápida de comandos sin IA (regex + patrones).
    /// Entrada: texto hablado en español.
    /// Salida: (handled, InterpretationResult) — handled=false si no se reconoció.
    /// </summary>
    public sealed class FastCommandClassifier
    {
        private readonly CapabilityRegistry _registry;

        public FastCommandClassifier(CapabilityRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>
        /// Intenta clasificar el comando sin invocar IA.
        /// Retorna (true, result) si se reconoció, (false, null) si no.
        /// </summary>
        public (bool handled, InterpretationResult? result) TryFastClassify(string speech)
        {
            var lower = speech.Trim().ToLowerInvariant();

            // ── CREAR ACTIVIDAD (prioridad alta) ────────────────────────────
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
                    NeedsConfirmation = false
                });
            }

            // ── RECORDATORIOS ────────────────────────────────────────────────
            if (IsRecordatoriosTodayCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "ListRecordatoriosToday",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "recordatorios",
                    Action = "today",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            if (IsRecordatoriosCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "ListRecordatorios",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "recordatorios",
                    Action = "list",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            // ── GOOGLE CALENDAR: eventos de hoy ─────────────────────────────
            if (IsCalendarTodayCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "ListCalendarToday",
                    Scope = "API",
                    Provider = "google",
                    Resource = "calendar",
                    Action = "list",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            // ── GOOGLE CALENDAR: próximos eventos (semana) ──────────────────
            if (IsCalendarWeekCommand(lower))
            {
                var weekStart = DateTime.Today.ToString("yyyy-MM-dd'T'00:00:00'-06:00'");
                var weekEnd = DateTime.Today.AddDays(7).ToString("yyyy-MM-dd'T'23:59:59'-06:00'");

                return (true, new InterpretationResult
                {
                    Intent = "ListCalendarWeek",
                    Scope = "API",
                    Provider = "google",
                    Resource = "calendar",
                    Action = "list",
                    Confidence = 0.95,
                    NeedsConfirmation = false,
                    Params = new Dictionary<string, object>
                    {
                        ["timeMin"] = weekStart,
                        ["timeMax"] = weekEnd,
                        ["maxResults"] = 10
                    }
                });
            }

            // ── ABRIR APP ────────────────────────────────────────────────────
            if (lower.StartsWith("abre ") || lower.StartsWith("abrir "))
            {
                var appName = lower
                    .Replace("abre ", "")
                    .Replace("abrir ", "")
                    .Trim();

                appName = appName
                    .Replace("el ", "")
                    .Replace("la ", "")
                    .Replace("los ", "")
                    .Replace("las ", "")
                    .Trim();

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

            // ── CERRAR APP ───────────────────────────────────────────────────
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

            // ── BUSCAR EN WEB ────────────────────────────────────────────────
            if (lower.StartsWith("busca ") || lower.StartsWith("buscar "))
            {
                var query = lower
                    .Replace("busca ", "")
                    .Replace("buscar ", "")
                    .Trim();

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

            // ── MINIMIZAR TODO ───────────────────────────────────────────────
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

        // ────────────────────────────────────────────────────────────────────
        // DETECTORES PRIVADOS
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Detecta comandos de creación de actividad.
        /// </summary>
        private static bool IsCreateActivityCommand(string lower)
        {
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
                @"^agrega actividad",
                @"^crea una actividad",
                @"^crear una actividad",
                @"^crea la actividad",
                @"^crear la actividad"
            };

            foreach (var pattern in patterns)
                if (Regex.IsMatch(lower, pattern)) return true;

            return false;
        }

        /// <summary>
        /// Detecta comandos de consulta de eventos del calendario para hoy.
        /// </summary>
        private static bool IsCalendarTodayCommand(string lower)
        {
            var patterns = new[]
            {
                "qué tengo hoy",
                "que tengo hoy",
                "eventos de hoy",
                "mis eventos de hoy",
                "calendario de hoy",
                "agenda de hoy",
                "qué hay hoy en mi calendario",
                "que hay hoy en mi calendario",
                "tengo algo hoy",
                "qué hay hoy",
                "que hay hoy"
            };

            foreach (var p in patterns)
                if (lower.Contains(p)) return true;

            return false;
        }

        /// <summary>
        /// Detecta comandos de consulta de próximos eventos (semana).
        /// Nota: se evalúa DESPUÉS de IsCalendarTodayCommand para evitar colisión
        /// en frases que contengan tanto "hoy" como "eventos".
        /// </summary>
        private static bool IsCalendarWeekCommand(string lower)
        {
            // Excluir frases que ya matchearon con hoy
            if (lower.Contains("hoy")) return false;

            var patterns = new[]
            {
                "mis eventos",
                "ver eventos",
                "ver calendario",
                "mostrar eventos",
                "mostrar calendario",
                "próximos eventos",
                "proximos eventos",
                "eventos de la semana",
                "agenda de la semana",
                "qué tengo esta semana",
                "que tengo esta semana",
                "qué hay en mi calendario",
                "que hay en mi calendario"
            };

            foreach (var p in patterns)
                if (lower.Contains(p)) return true;

            return false;
        }

        /// <summary>
        /// Mapea sinónimo hablado a appKey registrada.
        /// Entrada: nombre normalizado de la app (sin artículos).
        /// Salida: appKey o null si no se reconoce.
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

        private static bool IsRecordatoriosTodayCommand(string lower)
        {
            var patterns = new[]
            {
                "recordatorios de hoy",
                "mis recordatorios de hoy",
                "recordatorios para hoy",
                "qué recordatorios tengo hoy",
                "que recordatorios tengo hoy"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }

        private static bool IsRecordatoriosCommand(string lower)
        {
            // Excluir si ya matcheó con hoy
            if (lower.Contains("hoy")) return false;

            var patterns = new[]
            {
                "mis recordatorios",
                "ver recordatorios",
                "mostrar recordatorios",
                "recordatorios pendientes",
                "recordatorios de mañana",
                "tengo recordatorios",
                "recordatorios"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }
    }
}