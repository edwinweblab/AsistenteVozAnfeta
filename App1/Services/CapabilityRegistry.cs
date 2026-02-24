using Anfeta.UI.Data;
using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Anfeta.UI.Services
{
    public sealed class CapabilityRegistry
    {
        private readonly LocalAppsRepository _localAppsRepo;
        private readonly Dictionary<string, AppCapability> _registry;

        public CapabilityRegistry(LocalAppsRepository localAppsRepo)
        {
            _localAppsRepo = localAppsRepo;
            _registry = new Dictionary<string, AppCapability>(StringComparer.OrdinalIgnoreCase);

            Reload();
        }

        public void Reload()
        {
            _registry.Clear();

            // 1) API fija (no local)
            _registry["weblab"] = new AppCapability
            {
                AppKey = "weblab",
                ExecutableName = null,
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
            };

            // 2) Apps locales desde BD (solo enabled=1 para ejecución)
            var apps = _localAppsRepo.GetAll().Where(a => a.Enabled).ToList();

            foreach (var a in apps)
            {
                var synonyms = _localAppsRepo.GetSynonyms(a.AppKey);

                _registry[a.AppKey] = new AppCapability
                {
                    AppKey = a.AppKey,
                    // preferimos ruta completa si existe, si no, exe name
                    ExecutableName = !string.IsNullOrWhiteSpace(a.ExecutablePath) ? a.ExecutablePath : a.ExecutableName,
                    Category = a.Category,
                    FriendlyName = a.FriendlyName,
                    Capabilities = new List<string> { "abrir" },
                    Synonyms = synonyms
                };
            }
        }

        public AppCapability? GetApp(string appKey)
            => _registry.TryGetValue(appKey, out var app) ? app : null;

        public bool IsRegistered(string appKey)
            => _registry.ContainsKey(appKey);

        public List<AppCapability> GetAllApps()
            => _registry.Values.ToList();

        public string GetAllowedAppsMessage()
        {
            // Solo mostrar apps LOCALES habilitadas (no weblab)
            var names = _registry.Values
                .Where(a => !string.Equals(a.Category, "api", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.FriendlyName);

            return "Solo puedo abrir: " + string.Join(", ", names) + ".";
        }

        public List<AppCapability> GetAppsByCategory(string category)
            => _registry.Values
                .Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }
}