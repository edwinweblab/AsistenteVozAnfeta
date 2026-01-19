using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Anfeta.UI.Services
{
    public sealed class LocalActionExecutor
    {
        private static readonly Dictionary<string, string> AppMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = "chrome.exe",
            ["calculadora"] = "calc.exe",
            ["bloc"] = "notepad.exe",
            ["explorador"] = "explorer.exe"
        };

        private static readonly HashSet<string> AllowedIntents = new(StringComparer.OrdinalIgnoreCase)
        {
            "OpenApp", "CloseApp", "MinimizeAll", "SwitchWindow"
        };

        // NUEVO: consulta si una app está permitida
        public bool IsAllowedApp(string? appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey)) return false;
            return AppMap.ContainsKey(appKey);
        }

        // NUEVO: lista permitidas (para mensajes UI)
        public string GetAllowedAppsMessage()
        {
            return "Solo puedo abrir: " + string.Join(", ", AppMap.Keys) + ".";
        }

        public bool TryExecute(string intent, string scope, string? appKey, out string message)
        {
            if (!string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
            {
                message = "Acción no es LOCAL.";
                return false;
            }

            if (!AllowedIntents.Contains(intent))
            {
                message = $"Intent no permitido: {intent}";
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

            if (!AppMap.TryGetValue(appKey, out var exe))
            {
                message = $"La aplicación '{appKey}' no está disponible en la lista permitida.";
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });

                message = $"Acción OK: abierto {appKey}.";
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
