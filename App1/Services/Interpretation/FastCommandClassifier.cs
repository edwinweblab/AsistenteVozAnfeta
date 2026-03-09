// Services/FastCommandClassifier.cs
using Anfeta.UI.Models.Interpretation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Interpretation
{
    /// Clasificación rápida de comandos sin IA (regex + patrones).
    /// Entrada: texto hablado en español.
    /// Salida: (handled, InterpretationResult) — handled=false si no se reconoció.
    public sealed class FastCommandClassifier
    {
        private readonly CapabilityRegistry _registry;

        public FastCommandClassifier(CapabilityRegistry registry)
        {
            _registry = registry;
        }

        /// Intenta clasificar el comando sin invocar IA.
        /// Entrada: speech = texto reconocido.
        /// Salida: (true, result) si se reconoció, (false, null) si no.
        public (bool handled, InterpretationResult? result) TryFastClassify(string speech)
        {
            var lower = speech.Trim().ToLowerInvariant();

            // ── CREAR ACTIVIDAD (prioridad alta) ────────────────────────────────
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

            // ── CREAR RECORDATORIO ───────────────────────────────────────────────
            if (IsCreateRecordatorioCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "CreateRecordatorio",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "recordatorios",
                    Action = "create",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            // ── REPORTES: comprobatoria ──────────────────────────────────────────
            // Anclas únicas: "comprobatoria", "cómo voy hoy", "reporte del día".
            // No choca con recordatorios (distintas palabras ancla).
            // No choca con calendario (no contiene "eventos", "agenda", "qué tengo hoy").
            if (IsComprobatoriaCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "GetComprobatoria",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "reportes",
                    Action = "comprobatoria",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            // ── REPORTES: rezagadas ──────────────────────────────────────────────
            // Ancla: "rezagad" — palabra completamente única en el clasificador.
            if (IsRezagadasCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "GetRezagadas",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "reportes",
                    Action = "rezagadas",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            // ── REPORTES: revisiones por fecha ──────────────────────────────────
            // Ancla: "revisiones de hoy" / "revisiones de ayer".
            // Fecha resuelta aquí para no necesitar SpanishDateParser en el clasificador.
            // Para otras fechas (lunes, miércoles, etc.) se delega a IA.
            if (IsRevisionesPorFechaHoyCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "GetRevisionesPorFecha",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "reportes",
                    Action = "revisiones-por-fecha",
                    Confidence = 0.95,
                    NeedsConfirmation = false,
                    Params = new Dictionary<string, object>
                    {
                        ["date"] = DateTime.Today.ToString("yyyy-MM-dd")
                    }
                });
            }

            if (IsRevisionesPorFechaAyerCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "GetRevisionesPorFecha",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "reportes",
                    Action = "revisiones-por-fecha",
                    Confidence = 0.95,
                    NeedsConfirmation = false,
                    Params = new Dictionary<string, object>
                    {
                        ["date"] = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd")
                    }
                });
            }

            // ── REPORTES: últimos eventos de auditoría ───────────────────────────
            // Anclas conservadoras: "últimas acciones", "qué ha pasado", "últimos cambios".
            // Se verifica DESPUÉS de comprobatoria/rezagadas/revisiones para evitar
            // que frases más específicas caigan aquí primero.
            // No contiene "eventos" solo — evita colisión con IsCalendarWeekCommand.
            if (IsReportesUltimosCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "GetUltimos",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "reportes",
                    Action = "ultimos",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            // ── RECORDATORIOS ────────────────────────────────────────────────────
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

            if (IsRecordatoriosTomorrowCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "ListRecordatoriosTomorrow",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "recordatorios",
                    Action = "tomorrow",
                    Confidence = 0.95,
                    NeedsConfirmation = false
                });
            }

            if (IsRecordatoriosPendingCommand(lower))
            {
                return (true, new InterpretationResult
                {
                    Intent = "ListRecordatoriosPending",
                    Scope = "API",
                    Provider = "weblab",
                    Resource = "recordatorios",
                    Action = "pending",
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

            // ── GOOGLE CALENDAR: eventos de hoy ─────────────────────────────────
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

            // ── GOOGLE CALENDAR: próximos eventos (semana) ──────────────────────
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

            // ── ABRIR APP ────────────────────────────────────────────────────────
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

            // ── CERRAR APP ───────────────────────────────────────────────────────
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

            // ── BUSCAR EN WEB ────────────────────────────────────────────────────
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

            // ── MINIMIZAR TODO ───────────────────────────────────────────────────
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

        // ────────────────────────────────────────────────────────────────────────
        // DETECTORES — REPORTES
        // ────────────────────────────────────────────────────────────────────────

        /// Detecta solicitud de comprobatoria del usuario en sesión.
        /// Anclas: "comprobatoria", "cómo voy hoy", "reporte del día", "mi reporte".
        /// Excluye frases de recordatorios y calendario — palabras ancla completamente distintas.
        private static bool IsComprobatoriaCommand(string lower)
        {
            var patterns = new[]
            {
                "comprobatoria",
                "cómo voy hoy",
                "como voy hoy",
                "mi reporte de hoy",
                "reporte del día",
                "reporte del dia",
                "muéstrame mi reporte",
                "muestrame mi reporte",
                "ver mi reporte",
                "enséñame mi reporte",
                "enseñame mi reporte",
                "cómo estoy hoy",
                "como estoy hoy",
                // Frases adicionales
                "quiero ver mi comprobatoria",
                "muéstrame mi comprobatoria",
                "muestrame mi comprobatoria",
                "ver comprobatoria",
                "dame mi comprobatoria"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }

        /// Detecta solicitud de tareas rezagadas.
        /// Ancla: "rezagad" (cubre rezagada/rezagadas) o "tareas atrasadas".
        /// Palabra completamente única — sin riesgo de cruce con otros detectores.
        private static bool IsRezagadasCommand(string lower)
        {
            if (lower.Contains("rezagad") ||
                lower.Contains("tareas atrasadas") ||
                lower.Contains("actividades atrasadas"))
                return true;

            // Frases adicionales explícitas
            var patterns = new[]
            {
                "quiero ver mis tareas rezagadas",
                "muéstrame mis rezagadas",
                "muestrame mis rezagadas",
                "ver mis rezagadas",
                "dame mis rezagadas"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;

            return false;
        }

        /// Detecta solicitud de revisiones del día actual.
        /// Ancla: "revisiones" + "hoy". No choca con calendario ("eventos", "agenda").
        private static bool IsRevisionesPorFechaHoyCommand(string lower)
        {
            var patterns = new[]
            {
                "revisiones de hoy",
                "mis revisiones de hoy",
                "ver revisiones de hoy",
                "cuántas revisiones tengo hoy",
                "cuantas revisiones tengo hoy",
                "revisiones para hoy",
                "mis revisiones hoy",
                // Frases adicionales
                "quiero ver mis revisiones de hoy",
                "muéstrame mis revisiones de hoy",
                "muestrame mis revisiones de hoy",
                "dame mis revisiones de hoy"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }

        /// Detecta solicitud de revisiones de ayer.
        /// Ancla: "revisiones" + "ayer".
        private static bool IsRevisionesPorFechaAyerCommand(string lower)
        {
            var patterns = new[]
            {
                "revisiones de ayer",
                "mis revisiones de ayer",
                "ver revisiones de ayer",
                "revisiones del día de ayer",
                "revisiones del dia de ayer"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }

        /// Detecta solicitud de últimos eventos de auditoría.
        /// Anclas conservadoras: "últimas acciones", "qué ha pasado", "últimos cambios".
        /// Se evalúa al final de reportes para no interceptar frases más específicas.
        /// No contiene "eventos" solo — evita colisión con IsCalendarWeekCommand.
        private static bool IsReportesUltimosCommand(string lower)
        {
            var patterns = new[]
            {
                "últimas acciones",
                "ultimas acciones",
                "qué ha pasado",
                "que ha pasado",
                "últimos cambios",
                "ultimos cambios",
                "actividad reciente del equipo",
                "qué pasó recientemente",
                "que paso recientemente",
                // Frases adicionales
                "quiero ver qué ha pasado",
                "quiero ver que ha pasado",
                "muéstrame qué ha pasado",
                "muestrame que ha pasado",
                "muéstrame lo último",
                "muestrame lo ultimo",
                "quiero ver lo último",
                "quiero ver lo ultimo",
                "quiero ver las últimas acciones",
                "quiero ver las ultimas acciones",
                "ver últimas acciones",
                "ver ultimas acciones"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }

        // ────────────────────────────────────────────────────────────────────────
        // DETECTORES — EXISTENTES (sin cambios)
        // ────────────────────────────────────────────────────────────────────────

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

        private static bool IsCalendarWeekCommand(string lower)
        {
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
            if (lower.Contains("hoy")) return false;
            if (lower.Contains("mañana")) return false;
            if (lower.Contains("pendientes")) return false;

            var patterns = new[]
            {
                "mis recordatorios",
                "ver recordatorios",
                "mostrar recordatorios",
                "muestrame mis recordatorios",
                "muéstrame mis recordatorios",
                "muéstrame los recordatorios",
                "muestrame los recordatorios",
                "lista de recordatorios",
                "listar recordatorios",
                "tengo recordatorios",
                "cuáles son mis recordatorios",
                "cuales son mis recordatorios",
                "qué recordatorios tengo",
                "que recordatorios tengo",
                "recordatorios"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }

        private static bool IsRecordatoriosPendingCommand(string lower)
        {
            var patterns = new[]
            {
                "recordatorios pendientes",
                "mis recordatorios pendientes",
                "qué recordatorios tengo pendientes",
                "que recordatorios tengo pendientes",
                "recordatorios sin completar"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }

        private static bool IsRecordatoriosTomorrowCommand(string lower)
        {
            var patterns = new[]
            {
                "recordatorios de mañana",
                "mis recordatorios de mañana",
                "recordatorios para mañana",
                "qué recordatorios tengo mañana",
                "que recordatorios tengo mañana"
            };
            foreach (var p in patterns)
                if (lower.Contains(p)) return true;
            return false;
        }

        private static bool IsCreateRecordatorioCommand(string lower)
        {
            var patterns = new[]
            {
                "recuérdame", "recuerdame", "pon un recordatorio", "crea un recordatorio",
                "agregar recordatorio", "añadir recordatorio", "nuevo recordatorio",
                "agenda un recordatorio", "programa un recordatorio"
            };
            foreach (var p in patterns)
                if (lower.StartsWith(p) || lower.Contains(p)) return true;
            return false;
        }
    }
}