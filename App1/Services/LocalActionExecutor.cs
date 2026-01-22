// LocalActionExecutor.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Anfeta.UI.Services
{
    public sealed class LocalActionExecutor
    {
        // Whitelist app_key -> ejecutable
        private static readonly Dictionary<string, string> AppMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = "chrome.exe",
            ["calculadora"] = "calc.exe",
            ["bloc"] = "notepad.exe",
            ["explorador"] = "explorer.exe"
        };

        // Sinónimos -> app_key real
        private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
        {
            // Chrome / navegador
            ["navegador"] = "chrome",
            ["google chrome"] = "chrome",
            ["chrome"] = "chrome",

            // Bloc de notas
            ["bloc de notas"] = "bloc",
            ["notepad"] = "bloc",
            ["notas"] = "bloc",

            // Explorador
            ["explorador"] = "explorador",
            ["archivos"] = "explorador",
            ["file explorer"] = "explorador",
            ["explorer"] = "explorador",

            // Calculadora
            ["calculator"] = "calculadora",
            ["calc"] = "calculadora"
        };

        private static readonly HashSet<string> AllowedIntents = new(StringComparer.OrdinalIgnoreCase)
        {
            "OpenApp",
            "CloseApp",
            "MinimizeAll",
            "SwitchWindow"
        };

        public string? ResolveAppKeyFromSpeech(string speech)
        {
            if (string.IsNullOrWhiteSpace(speech)) return null;

            var t = speech.Trim().ToLowerInvariant();

            // 1) Si contiene directamente una key permitida
            foreach (var key in AppMap.Keys)
            {
                if (t.Contains(key.ToLowerInvariant()))
                    return key;
            }

            // 2) Si contiene algún sinónimo
            foreach (var pair in Synonyms)
            {
                if (t.Contains(pair.Key.ToLowerInvariant()))
                    return pair.Value;
            }

            return null;
        }

        private static string? NormalizeAppKey(string? appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey)) return null;

            var k = appKey.Trim().ToLowerInvariant();

            // Si viene "chrome.exe"
            if (k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                k = k[..^4];

            // Mapear sinónimo -> key real
            if (Synonyms.TryGetValue(k, out var mapped))
                return mapped;

            // Si ya es key válida
            if (AppMap.ContainsKey(k))
                return k;

            // Si el modelo manda algo raro, devolvemos normalizado para diagnóstico
            return k;
        }

        public bool IsAllowedApp(string? appKey)
        {
            var key = NormalizeAppKey(appKey);
            if (string.IsNullOrWhiteSpace(key)) return false;
            return AppMap.ContainsKey(key);
        }

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

            var key = NormalizeAppKey(appKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                message = "Falta app_key para OpenApp.";
                return false;
            }

            if (!AppMap.TryGetValue(key, out var exe))
            {
                message = $"La aplicación '{key}' no está disponible en la lista permitida.";
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });

                message = $"Acción OK: abierto {key}.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Error al ejecutar {key}: {ex.Message}";
                return false;
            }
        }
    }
}
