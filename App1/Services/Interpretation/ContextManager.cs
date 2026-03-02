using Anfeta.UI.Models.Interpretation;
using System;
using System.Diagnostics;
using System.Linq;

namespace Anfeta.UI.Services.Interpretation
{
    /// <summary>Gestiona estado actual + historial de comandos</summary>
    public sealed class ContextManager
    {
        private readonly CapabilityRegistry _registry;
        private CommandContext _context = new();
        private const int MaxHistorySize = 5;
        private const int ContextTimeoutSeconds = 300; // 5 min

        public ContextManager(CapabilityRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Obtener contexto actual</summary>
        public CommandContext GetContext()
        {
            CleanupStaleContext();
            return _context;
        }

        /// <summary>Actualizar app activa</summary>
        public void SetActiveApp(string appKey)
        {
            var appDef = _registry.GetApp(appKey);
            if (appDef == null)
            {
                Debug.WriteLine($"[ContextManager] App '{appKey}' no registrada");
                return;
            }

            _context.CurrentApp = new ActiveApp
            {
                AppKey = appKey,
                Category = appDef.Category,
                OpenedAt = DateTime.UtcNow,
                Capabilities = appDef.Capabilities
            };

            _context.LastActivityTime = DateTime.UtcNow;
            Debug.WriteLine($"[ContextManager] App activa: {appKey} (categoría: {appDef.Category})");
        }

        /// <summary>Limpiar app activa</summary>
        public void ClearActiveApp()
        {
            _context.CurrentApp = null;
            Debug.WriteLine("[ContextManager] App activa limpiada");
        }

        /// <summary>Agregar comando al historial</summary>
        public void AddToHistory(string intent, string? appKey)
        {
            _context.RecentCommands.Add(new CommandHistoryEntry
            {
                Intent = intent,
                AppKey = appKey,
                ExecutedAt = DateTime.UtcNow
            });

            if (_context.RecentCommands.Count > MaxHistorySize)
                _context.RecentCommands.RemoveAt(0);

            _context.LastActivityTime = DateTime.UtcNow;
            Debug.WriteLine($"[ContextManager] Historial: {intent} ({appKey ?? "null"})");
        }

        /// <summary>Limpiar contexto obsoleto (>5 min inactividad)</summary>
        private void CleanupStaleContext()
        {
            var elapsed = (DateTime.UtcNow - _context.LastActivityTime).TotalSeconds;
            if (elapsed > ContextTimeoutSeconds)
            {
                Debug.WriteLine($"[ContextManager] Contexto obsoleto ({elapsed:F0}s) -> limpieza");
                _context.CurrentApp = null;
                _context.RecentCommands.Clear();
                _context.LastActivityTime = DateTime.UtcNow;
            }
        }

        /// <summary>Detectar patrón en historial</summary>
        public string? DetectPattern()
        {
            if (_context.RecentCommands.Count < 2) return null;

            var recent = _context.RecentCommands.TakeLast(3).ToList();
            var navegadorIntents = recent.Count(c =>
                c.AppKey == "chrome" ||
                c.Intent.Contains("Search", StringComparison.OrdinalIgnoreCase));

            if (navegadorIntents >= 2)
                return "navegacion_web";

            return null;
        }
    }
}