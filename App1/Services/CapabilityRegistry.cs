// Services/CapabilityRegistry.cs
using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Anfeta.UI.Services
{
    /// <summary>
    /// Catálogo centralizado de apps + APIs
    /// </summary>
    public sealed class CapabilityRegistry
    {
        private readonly Dictionary<string, AppCapability> _registry;

        public CapabilityRegistry()
        {
            _registry = new Dictionary<string, AppCapability>(StringComparer.OrdinalIgnoreCase)
            {
                // ✅ NUEVO: Weblab API
                ["weblab"] = new AppCapability
                {
                    AppKey = "weblab",
                    ExecutableName = null, // No es ejecutable local
                    Category = "api",
                    FriendlyName = "Weblab API",
                    Capabilities = new List<string>
                    {
                        "crear_actividad",
                        "listar_actividades",
                        "buscar_actividades",
                        "crear_recordatorio",
                        "listar_recordatorios"
                    },
                    Synonyms = new List<string>
                    {
                        "actividades",
                        "tareas",
                        "recordatorios",
                        "weblab"
                    }
                },

                ["chrome"] = new AppCapability
                {
                    AppKey = "chrome",
                    ExecutableName = "chrome.exe",
                    Category = "navegador",
                    FriendlyName = "Chrome",
                    Capabilities = new List<string> { "buscar_web", "navegar", "cerrar", "minimizar" },
                    Synonyms = new List<string> { "navegador", "browser", "internet", "web" }
                },

                ["calculadora"] = new AppCapability
                {
                    AppKey = "calculadora",
                    ExecutableName = "calc.exe",
                    Category = "utilidad",
                    FriendlyName = "Calculadora",
                    Capabilities = new List<string> { "calcular", "cerrar", "minimizar" },
                    Synonyms = new List<string> { "calcular", "cuentas", "matemáticas", "calc" }
                },

                ["bloc"] = new AppCapability
                {
                    AppKey = "bloc",
                    ExecutableName = "notepad.exe",
                    Category = "editor",
                    FriendlyName = "Bloc de Notas",
                    Capabilities = new List<string> { "editar_texto", "cerrar", "minimizar" },
                    Synonyms = new List<string> { "notepad", "notas", "editor", "texto", "blog" }
                },

                ["explorador"] = new AppCapability
                {
                    AppKey = "explorador",
                    ExecutableName = "explorer.exe",
                    Category = "gestor_archivos",
                    FriendlyName = "Explorador",
                    Capabilities = new List<string> { "navegar_archivos", "cerrar", "minimizar" },
                    Synonyms = new List<string> { "archivos", "carpetas", "explorer", "explorar" }
                }
            };
        }

        /// <summary>
        /// Obtener app por key
        /// </summary>
        public AppCapability? GetApp(string appKey)
        {
            return _registry.TryGetValue(appKey, out var app) ? app : null;
        }

        /// <summary>
        /// Verificar si app está registrada
        /// </summary>
        public bool IsRegistered(string appKey)
        {
            return _registry.ContainsKey(appKey);
        }

        /// <summary>
        /// Listar todas las apps registradas
        /// </summary>
        public List<AppCapability> GetAllApps()
        {
            return _registry.Values.ToList();
        }

        /// <summary>
        /// Obtener mensaje de apps permitidas
        /// </summary>
        public string GetAllowedAppsMessage()
        {
            var names = _registry.Values.Select(a => a.FriendlyName);
            return "Solo puedo abrir: " + string.Join(", ", names) + ".";
        }

        /// <summary>
        /// Obtener apps por categoría
        /// </summary>
        public List<AppCapability> GetAppsByCategory(string category)
        {
            return _registry.Values
                .Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}