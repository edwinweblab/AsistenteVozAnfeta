// LocalActionExecutor.cs
using System;
using System.Diagnostics;
using System.IO;

namespace Anfeta.UI.Services
{
    public sealed class LocalActionExecutor
    {
        private readonly CapabilityRegistry _registry;

        // Blacklist mínima (seguridad)
        private static readonly string[] BlockedExeNames =
        {
            "cmd.exe",
            "powershell.exe",
            "pwsh.exe",
            "regedit.exe",
            "taskmgr.exe",
            "msiexec.exe"
        };

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

            var key = NormalizeAppKey(appKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                message = "Falta app_key para OpenApp.";
                return false;
            }

            var appDef = _registry.GetApp(key);
            if (appDef == null)
            {
                message = $"La aplicación '{key}' no está disponible.";
                return false;
            }

            // Puede ser ruta completa o solo nombre exe (según registry)
            var exeOrPath = (appDef.ExecutableName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(exeOrPath))
            {
                message = $"La aplicación '{appDef.FriendlyName}' no tiene ejecutable configurado.";
                return false;
            }

            // Determinar nombre de ejecutable para blacklist (si es ruta, se toma el filename)
            var exeNameOnly = GetExeNameOnly(exeOrPath);

            if (IsBlocked(exeNameOnly))
            {
                message = $"Por seguridad no puedo ejecutar '{exeNameOnly}'.";
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exeOrPath,   // ruta completa o exe name
                    UseShellExecute = true
                });

                message = $"Acción OK: abierto {appDef.FriendlyName}.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Error al ejecutar {key}: {ex.Message}";
                return false;
            }
        }

        private static string? NormalizeAppKey(string? appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey))
                return null;

            return appKey.Trim().ToLowerInvariant();
        }

        private static string GetExeNameOnly(string exeOrPath)
        {
            // Si es ruta: C:\...\WINWORD.EXE -> winword.exe
            // Si ya es exe: chrome.exe -> chrome.exe
            try
            {
                var name = Path.GetFileName(exeOrPath);
                return string.IsNullOrWhiteSpace(name) ? exeOrPath.ToLowerInvariant() : name.ToLowerInvariant();
            }
            catch
            {
                return exeOrPath.ToLowerInvariant();
            }
        }

        private static bool IsBlocked(string exeNameOnly)
        {
            foreach (var b in BlockedExeNames)
            {
                if (exeNameOnly.Equals(b, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}