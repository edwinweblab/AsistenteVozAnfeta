// Services/LocalActionExecutor.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Anfeta.UI.Services.Interpretation
{
    public sealed class LocalActionExecutor
    {
        private readonly CapabilityRegistry _registry;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;

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

        /// <summary>
        /// Verificar si app está permitida
        /// (Tu registry SOLO carga apps enabled=1, así que si existe aquí, está permitida)
        /// </summary>
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

        /// <summary>
        /// Ejecutar acción local
        /// Soporta:
        /// - .lnk (recomendado para apps del menú inicio como Discord)
        /// - ruta .exe completa
        /// - nombre .exe (si está en PATH o Windows lo resuelve)
        /// </summary>
        public bool TryExecute(string intent, string scope, string? appKey, out string message)
        {
            message = "";

            if (!string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
            {
                message = "Acción no es LOCAL.";
                return false;
            }

            if (!string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(intent, "CloseApp", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(intent, "MinimizeApp", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(intent, "MinimizeAll", StringComparison.OrdinalIgnoreCase))
            {
                message = $"Intent LOCAL reconocido pero no implementado aún: {intent}";
                return false;
            }

            if (string.Equals(intent, "MinimizeAll", StringComparison.OrdinalIgnoreCase))
            {
                return TryMinimizeAll(out message);
            }

            var key = NormalizeAppKey(appKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                message = $"No especificaste qué aplicación {intent.ToLowerInvariant()}.";
                return false;
            }

            var appDef = _registry.GetApp(key);
            if (appDef == null)
            {
                message = $"La aplicación '{key}' no está permitida o no existe.";
                return false;
            }

            // En tu CapabilityRegistry, ExecutableName ya contiene:
            // - EjecutablePath (ruta completa) si existe
            // - Si no, ExecutableName
            var target = (appDef.ExecutableName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                message = $"La aplicación '{appDef.FriendlyName}' no tiene ejecutable configurado.";
                return false;
            }

            // Si es .lnk, Windows resuelve target+args+workdir
            var isLink = target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

            // Blacklist solo aplica a EXE directos (no a .lnk)
            if (!isLink)
            {
                var exeNameOnly = GetExeNameOnly(target);
                if (IsBlocked(exeNameOnly))
                {
                    message = $"Por seguridad no puedo ejecutar '{exeNameOnly}'.";
                    return false;
                }
            }

            try
            {
                if (string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    message = $"Acción OK: abierto {appDef.FriendlyName}.";
                    return true;
                }

                if (string.Equals(intent, "CloseApp", StringComparison.OrdinalIgnoreCase))
                {
                    return TryCloseApp(target, appDef.FriendlyName, out message);
                }

                if (string.Equals(intent, "MinimizeApp", StringComparison.OrdinalIgnoreCase))
                {
                    return TryMinimizeApp(target, appDef.FriendlyName, out message);
                }

                message = "Acción no reconocida.";
                return false;
            }
            catch (Exception ex)
            {
                // Tip: Win32Exception suele dar mensaje útil cuando el target no se puede iniciar.
                message = $"Error al ejecutar {intent} para {appDef.FriendlyName}: {ex.Message}";
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
            try
            {
                var name = Path.GetFileName(exeOrPath);
                return string.IsNullOrWhiteSpace(name)
                    ? exeOrPath.Trim().ToLowerInvariant()
                    : name.Trim().ToLowerInvariant();
            }
            catch
            {
                return exeOrPath.Trim().ToLowerInvariant();
            }
        }

        private bool TryCloseApp(string target, string friendlyName, out string message)
        {
            var exeNameOnly = GetExeNameOnly(target);
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeNameOnly));

            if (processes.Length == 0)
            {
                message = $"No encontré ninguna instancia de {friendlyName} abierta.";
                return false;
            }

            foreach (var p in processes)
            {
                try
                {
                    if (!p.CloseMainWindow())
                    {
                        p.Kill();
                    }
                }
                catch { /* Ignorar errores al cerrar instancias individuales */ }
            }

            message = $"Acción OK: cerrando {friendlyName}.";
            return true;
        }

        private bool TryMinimizeApp(string target, string friendlyName, out string message)
        {
            var exeNameOnly = GetExeNameOnly(target);
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeNameOnly));

            if (processes.Length == 0)
            {
                message = $"No encontré ninguna instancia de {friendlyName} abierta.";
                return false;
            }

            bool minimizedAtLeastOne = false;
            foreach (var p in processes)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, SW_MINIMIZE);
                    minimizedAtLeastOne = true;
                }
            }

            if (minimizedAtLeastOne)
            {
                message = $"Acción OK: minimizando {friendlyName}.";
                return true;
            }

            message = $"No pude minimizar la ventana de {friendlyName}.";
            return false;
        }

        private bool TryMinimizeAll(out string message)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType != null)
                {
                    object? shell = Activator.CreateInstance(shellType);
                    shellType.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                }

                message = "Acción OK: minimizando todo.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Error al minimizar todo: {ex.Message}";
                return false;
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