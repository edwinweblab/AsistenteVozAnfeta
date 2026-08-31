using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        /// <summary>
        /// Abre una página de Notion con fallback automático al navegador.
        ///
        /// No se confía únicamente en QueryUriSupportAsync porque Windows puede
        /// conservar una asociación notion:// obsoleta después de desinstalar
        /// Notion Desktop. Antes de lanzar el protocolo se valida que el
        /// ejecutable registrado todavía exista.
        /// </summary>
        private async Task<bool> OpenNotionPageWithFallbackAsync(
            string? rawUrl,
            string desktopSuccessStatus,
            string browserSuccessStatus,
            string failureStatus,
            string invalidUrlStatus,
            bool allowBrowserFallback = true)
        {
            if (!TryBuildNotionOpenUris(
                    rawUrl,
                    out var webUri,
                    out var desktopUri))
            {
                StatusText.Text =
                    $"Estado: {invalidUrlStatus}.";
                return false;
            }

            var desktopOpened = false;

            if (IsNotionDesktopProtocolHandlerUsable())
            {
                try
                {
                    var support =
                        await Launcher.QueryUriSupportAsync(
                            desktopUri,
                            LaunchQuerySupportType.Uri);

                    if (support ==
                        LaunchQuerySupportStatus.Available)
                    {
                        var launchAccepted =
                            await Launcher.LaunchUriAsync(
                                desktopUri);

                        // LaunchUriAsync == true solo confirma que Windows
                        // aceptó la solicitud. No garantiza que Notion haya
                        // iniciado realmente. Se espera brevemente un proceso
                        // activo antes de mostrar éxito de Desktop.
                        if (launchAccepted)
                        {
                            desktopOpened =
                                await WaitForNotionDesktopProcessAsync(
                                    TimeSpan.FromSeconds(4));
                        }
                    }
                }
                catch
                {
                    desktopOpened = false;
                }
            }

            if (desktopOpened)
            {
                StatusText.Text =
                    $"Estado: {desktopSuccessStatus} ✅";
                return true;
            }

            if (!allowBrowserFallback)
            {
                StatusText.Text =
                    $"Estado: {failureStatus}.";
                return false;
            }

            var browserOpened = false;

            try
            {
                browserOpened =
                    await Launcher.LaunchUriAsync(
                        webUri);
            }
            catch
            {
                browserOpened = false;
            }

            if (browserOpened)
            {
                StatusText.Text =
                    $"Estado: {browserSuccessStatus} ✅";
                return true;
            }

            StatusText.Text =
                $"Estado: {failureStatus}.";

            await ShowNotionOpenFailureDialogAsync(
                webUri,
                failureStatus);

            return false;
        }

        // Shared with the floating reminder's N button. Never opens a browser here:
        // callers retain their existing web fallback and error UI.
        internal static async Task<bool> TryOpenNotionDesktopOnlyAsync(string rawUrl)
        {
            if (!TryBuildNotionOpenUris(rawUrl, out _, out var desktopUri) ||
                !IsNotionDesktopProtocolHandlerUsable())
                return false;
            try
            {
                if (await Launcher.QueryUriSupportAsync(desktopUri, LaunchQuerySupportType.Uri) != LaunchQuerySupportStatus.Available)
                    return false;
                return await Launcher.LaunchUriAsync(desktopUri) &&
                    await WaitForNotionDesktopProcessAsync(TimeSpan.FromSeconds(4));
            }
            catch { return false; }
        }

        private static bool TryBuildNotionOpenUris(
            string? rawUrl,
            out Uri webUri,
            out Uri desktopUri)
        {
            webUri = null!;
            desktopUri = null!;

            var clean =
                (rawUrl ?? string.Empty).Trim();

            if (!Uri.TryCreate(
                    clean,
                    UriKind.Absolute,
                    out var parsed))
            {
                return false;
            }

            Uri normalizedWebUri;

            if (string.Equals(
                    parsed.Scheme,
                    "notion",
                    StringComparison.OrdinalIgnoreCase))
            {
                normalizedWebUri =
                    new UriBuilder(parsed)
                    {
                        Scheme = Uri.UriSchemeHttps,
                        Port = -1
                    }.Uri;
            }
            else if (string.Equals(
                         parsed.Scheme,
                         Uri.UriSchemeHttps,
                         StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         parsed.Scheme,
                         Uri.UriSchemeHttp,
                         StringComparison.OrdinalIgnoreCase))
            {
                normalizedWebUri = parsed;
            }
            else
            {
                return false;
            }

            var host =
                normalizedWebUri.Host.Trim();

            var isNotionHost =
                string.Equals(
                    host,
                    "notion.so",
                    StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(
                    ".notion.so",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    host,
                    "notion.com",
                    StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(
                    ".notion.com",
                    StringComparison.OrdinalIgnoreCase);

            if (!isNotionHost)
                return false;

            webUri =
                new UriBuilder(normalizedWebUri)
                {
                    Scheme = Uri.UriSchemeHttps,
                    Port = -1
                }.Uri;

            desktopUri =
                new UriBuilder(webUri)
                {
                    Scheme = "notion",
                    Port = -1
                }.Uri;

            return true;
        }

        /// <summary>
        /// Comprueba que notion:// no sea únicamente una asociación obsoleta.
        /// El valor true requiere un registro activo de instalación y que el
        /// comando del protocolo apunte a un ejecutable completo.
        /// </summary>
        private static bool IsNotionDesktopProtocolHandlerUsable()
        {
            try
            {
                // No se buscan ejecutables sueltos por AppData. Una
                // desinstalación incompleta puede dejar Notion.exe y el
                // protocolo registrados aunque la aplicación ya no funcione.
                // Se exige simultáneamente:
                // 1) comando real del protocolo;
                // 2) ejecutable completo;
                // 3) registro activo de desinstalación de Notion.
                if (!HasActiveNotionUninstallRegistration())
                    return false;

                foreach (var command in
                         ReadNotionProtocolCommands())
                {
                    if (TryResolveRegisteredExecutable(
                            command,
                            out var executablePath) &&
                        IsNotionExecutableInstallationComplete(
                            executablePath))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ante una validación inconclusa se prefiere el navegador para
                // evitar mostrar un éxito falso de Notion Desktop.
            }

            return false;
        }

        private static bool HasActiveNotionUninstallRegistration()
        {
            const string uninstallPath =
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

            try
            {
                if (HasNotionUninstallEntry(
                        Registry.CurrentUser,
                        uninstallPath))
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                using var localMachine64 =
                    RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine,
                        RegistryView.Registry64);

                if (HasNotionUninstallEntry(
                        localMachine64,
                        uninstallPath))
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                using var localMachine32 =
                    RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine,
                        RegistryView.Registry32);

                if (HasNotionUninstallEntry(
                        localMachine32,
                        uninstallPath))
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool HasNotionUninstallEntry(
            RegistryKey root,
            string uninstallPath)
        {
            using var uninstallRoot =
                root.OpenSubKey(
                    uninstallPath,
                    writable: false);

            if (uninstallRoot == null)
                return false;

            foreach (var subKeyName in
                     uninstallRoot.GetSubKeyNames())
            {
                try
                {
                    using var appKey =
                        uninstallRoot.OpenSubKey(
                            subKeyName,
                            writable: false);

                    var displayName =
                        appKey?.GetValue(
                            "DisplayName") as string;

                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !displayName.Contains(
                            "Notion",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var uninstallCommand =
                        appKey?.GetValue(
                            "UninstallString") as string;

                    var quietUninstallCommand =
                        appKey?.GetValue(
                            "QuietUninstallString") as string;

                    if (!string.IsNullOrWhiteSpace(
                            uninstallCommand) ||
                        !string.IsNullOrWhiteSpace(
                            quietUninstallCommand))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Una entrada dañada no invalida las demás.
                }
            }

            return false;
        }

        private static async Task<bool>
            WaitForNotionDesktopProcessAsync(
                TimeSpan timeout)
        {
            var started =
                DateTime.UtcNow;

            while (DateTime.UtcNow - started < timeout)
            {
                if (IsNotionDesktopProcessRunning())
                    return true;

                await Task.Delay(250);
            }

            return IsNotionDesktopProcessRunning();
        }

        private static bool IsNotionDesktopProcessRunning()
        {
            Process[] processes =
                Array.Empty<Process>();

            try
            {
                processes =
                    Process.GetProcessesByName(
                        "Notion");

                return processes.Any(process =>
                {
                    try
                    {
                        return !process.HasExited;
                    }
                    catch
                    {
                        return false;
                    }
                });
            }
            catch
            {
                return false;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        private static IEnumerable<string>
            ReadNotionProtocolCommands()
        {
            var commands =
                new List<string>();

            TryReadRegistryCommand(
                Registry.CurrentUser,
                @"Software\Classes\notion\shell\open\command",
                commands);

            TryReadRegistryCommand(
                Registry.ClassesRoot,
                @"notion\shell\open\command",
                commands);

            return commands;
        }

        private static void TryReadRegistryCommand(
            RegistryKey root,
            string subKeyPath,
            ICollection<string> destination)
        {
            try
            {
                using var key =
                    root.OpenSubKey(
                        subKeyPath,
                        writable: false);

                var command =
                    key?.GetValue(null) as string;

                if (!string.IsNullOrWhiteSpace(command))
                    destination.Add(command);
            }
            catch
            {
            }
        }

        private static bool TryResolveRegisteredExecutable(
            string command,
            out string executablePath)
        {
            executablePath = string.Empty;

            var expanded =
                Environment.ExpandEnvironmentVariables(
                    (command ?? string.Empty).Trim());

            if (string.IsNullOrWhiteSpace(expanded))
                return false;

            string candidate;

            if (expanded.StartsWith(
                    "\"",
                    StringComparison.Ordinal))
            {
                var closingQuote =
                    expanded.IndexOf(
                        '"',
                        1);

                if (closingQuote <= 1)
                    return false;

                candidate =
                    expanded.Substring(
                        1,
                        closingQuote - 1);
            }
            else
            {
                var match =
                    Regex.Match(
                        expanded,
                        @"^(?<exe>.+?\.exe)(?:\s|$)",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);

                if (!match.Success)
                    return false;

                candidate =
                    match.Groups["exe"].Value.Trim();
            }

            candidate =
                candidate.Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            // No se aceptan lanzadores genéricos del sistema como prueba de
            // que Notion siga instalado.
            var fileName =
                Path.GetFileName(candidate);

            if (string.Equals(
                    fileName,
                    "explorer.exe",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    fileName,
                    "rundll32.exe",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    fileName,
                    "ApplicationFrameHost.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            executablePath =
                Path.GetFullPath(candidate);

            return true;
        }

        private static bool IsNotionExecutableInstallationComplete(
            string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !File.Exists(executablePath))
            {
                return false;
            }

            var fileName =
                Path.GetFileName(executablePath);

            if (string.Equals(
                    fileName,
                    "Notion.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(
                    fileName,
                    "Update.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var installRoot =
                Path.GetDirectoryName(executablePath);

            if (string.IsNullOrWhiteSpace(installRoot) ||
                !Directory.Exists(installRoot))
            {
                return false;
            }

            // En instalaciones tipo Squirrel, Update.exe puede permanecer en
            // la raíz mientras Notion.exe vive dentro de una carpeta app-*.
            // Exigir el ejecutable real evita considerar como instalada una
            // desinstalación incompleta que dejó únicamente el actualizador.
            if (File.Exists(
                    Path.Combine(
                        installRoot,
                        "Notion.exe")))
            {
                return true;
            }

            foreach (var appFolder in
                     Directory.EnumerateDirectories(
                         installRoot,
                         "app-*",
                         SearchOption.TopDirectoryOnly))
            {
                if (File.Exists(
                        Path.Combine(
                            appFolder,
                            "Notion.exe")))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task ShowNotionOpenFailureDialogAsync(
            Uri webUri,
            string failureStatus)
        {
            try
            {
                var dialog =
                    new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = failureStatus,
                        Content =
                            "Notion Desktop no está disponible y el navegador " +
                            "tampoco pudo abrir el enlace. Puedes copiarlo para " +
                            "abrirlo manualmente.",
                        PrimaryButtonText = "Copiar enlace",
                        CloseButtonText = "Cerrar",
                        DefaultButton =
                            ContentDialogButton.Primary
                    };

                if (await dialog.ShowAsync() !=
                    ContentDialogResult.Primary)
                {
                    return;
                }

                var package =
                    new DataPackage();

                package.SetText(
                    webUri.AbsoluteUri);

                Clipboard.SetContent(package);

                StatusText.Text =
                    "Estado: Enlace de Notion copiado ✅";
            }
            catch
            {
                // El error principal ya permanece visible en StatusText.
            }
        }
    }
}
