using System;
using System.Diagnostics;

namespace Anfeta.UI.Services
{
    public sealed class LocalActionExecutor
    {
        private readonly CapabilityRegistry _registry;

        public LocalActionExecutor(CapabilityRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Verificar si app está permitida</summary>
        public bool IsAllowedApp(string? appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey)) return false;
            return _registry.IsRegistered(appKey);
        }

        /// <summary>Mensaje de apps permitidas</summary>
        public string GetAllowedAppsMessage()
        {
            return _registry.GetAllowedAppsMessage();
        }

        /// <summary>Ejecutar acción local</summary>
        public bool TryExecute(string intent, string scope, string? appKey, out string message)
        {
            if (!string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
            {
                message = "Acción no es LOCAL.";
                return false;
            }

            if (!string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase))
            {
                message = $"Intent LOCAL reconocido pero no implementado aún: {intent}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(appKey))
            {
                message = "Falta app_key para OpenApp.";
                return false;
            }

            var appDef = _registry.GetApp(appKey);
            if (appDef == null)
            {
                message = $"La aplicación '{appKey}' no está disponible.";
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = appDef.ExecutableName,
                    UseShellExecute = true
                });
                message = $"Acción OK: abierto {appDef.FriendlyName}.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Error al ejecutar {appKey}: {ex.Message}";
                return false;
            }
        }
    }
}