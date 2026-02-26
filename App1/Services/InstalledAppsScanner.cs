// Services/InstalledAppsScanner.cs
using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Anfeta.UI.Services
{
    public sealed class InstalledAppsScanner
    {
        /// <summary>
        /// Escanea accesos directos (.lnk) del Menú Inicio (usuario + común).
        /// IMPORTANTE:
        /// - Guardamos el .lnk como "launcher" (ExecutablePath = ruta del .lnk),
        ///   NO el target .exe. Así Windows ejecuta bien apps como Discord, Store apps,
        ///   Squirrel/Electron, accesos con argumentos, etc.
        /// </summary>
        public List<LocalAppEntry> ScanStartMenuShortcuts()
        {
            var results = new List<LocalAppEntry>();

            // Start Menu paths
            var userStartMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs"
            );

            var commonStartMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu\Programs"
            );

            // 1) Recolectar .lnk
            var lnkFiles = new List<string>();
            try
            {
                if (Directory.Exists(userStartMenu))
                    lnkFiles.AddRange(Directory.EnumerateFiles(userStartMenu, "*.lnk", SearchOption.AllDirectories));
            }
            catch { /* ignore */ }

            try
            {
                if (Directory.Exists(commonStartMenu))
                    lnkFiles.AddRange(Directory.EnumerateFiles(commonStartMenu, "*.lnk", SearchOption.AllDirectories));
            }
            catch { /* ignore */ }

            // 2) Construir LocalAppEntry por cada .lnk
            foreach (var lnk in lnkFiles)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(lnk)) continue;
                    if (!File.Exists(lnk)) continue;

                    var friendly = Path.GetFileNameWithoutExtension(lnk)?.Trim();
                    if (string.IsNullOrWhiteSpace(friendly)) continue;

                    // AppKey base por friendly (slug)
                    var baseKey = SlugifyKey(friendly);
                    if (string.IsNullOrWhiteSpace(baseKey))
                        continue;

                    // Evitar duplicados por launcher (.lnk)
                    if (results.Any(x => string.Equals(x.ExecutablePath, lnk, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Opcional: intentar leer info del acceso directo para categorizar/diagnóstico
                    ShortcutInfo info;
                    try { info = ResolveShortcutInfo(lnk); }
                    catch { info = new ShortcutInfo(); }

                    // Categoría simple (puedes refinar después)
                    // - Si apunta a explorer.exe con AppsFolder => Store/packaged
                    // - Si no, "otro"
                    var category = "otro";
                    if (!string.IsNullOrWhiteSpace(info.TargetPath))
                    {
                        var t = info.TargetPath.Trim();
                        if (t.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                            (info.Arguments?.IndexOf("AppsFolder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             t.IndexOf("explorer.exe", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            category = "store";
                        }
                    }

                    results.Add(new LocalAppEntry
                    {
                        AppKey = baseKey,
                        FriendlyName = friendly,
                        Category = category,

                        // Guardamos el LAUNCHER real
                        ExecutableName = Path.GetFileName(lnk), // ej: "Discord.lnk"
                        ExecutablePath = lnk,                   // ej: "C:\ProgramData\...\Discord.lnk"

                        Enabled = false,
                        Source = "detected"
                    });
                }
                catch
                {
                    // Ignorar accesos rotos o no resolubles
                }
            }

            // 3) Si hay AppKey repetidas, poner sufijo incremental
            var groups = results
                .GroupBy(x => x.AppKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var g in groups)
            {
                var items = g.ToList();
                if (items.Count <= 1) continue;

                for (int i = 0; i < items.Count; i++)
                {
                    items[i].AppKey = $"{items[i].AppKey}_{i + 1}";
                }
            }

            return results
                .OrderBy(x => x.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string SlugifyKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";

            var s = input.Trim().ToLowerInvariant();
            var sb = new StringBuilder(s.Length);

            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (ch == ' ' || ch == '-' || ch == '_' || ch == '.')
                    sb.Append('_');
            }

            var key = sb.ToString().Trim('_');

            while (key.Contains("__", StringComparison.Ordinal))
                key = key.Replace("__", "_", StringComparison.Ordinal);

            return key;
        }

        // ==========================
        // .LNK -> Info via COM
        // ==========================

        private sealed class ShortcutInfo
        {
            public string? TargetPath { get; set; }
            public string? Arguments { get; set; }
            public string? WorkingDirectory { get; set; }
        }

        private static ShortcutInfo ResolveShortcutInfo(string shortcutPath)
        {
            // CLSID_ShellLink
            var shellLink = (IShellLinkW)new CShellLink();
            var persistFile = (IPersistFile)shellLink;

            persistFile.Load(shortcutPath, 0);

            var sbPath = new StringBuilder(260);
            var data = new WIN32_FIND_DATAW();
            shellLink.GetPath(sbPath, sbPath.Capacity, out data, 0);

            var sbArgs = new StringBuilder(1024);
            shellLink.GetArguments(sbArgs, sbArgs.Capacity);

            var sbWork = new StringBuilder(260);
            shellLink.GetWorkingDirectory(sbWork, sbWork.Capacity);

            return new ShortcutInfo
            {
                TargetPath = sbPath.ToString()?.Trim(),
                Arguments = sbArgs.ToString()?.Trim(),
                WorkingDirectory = sbWork.ToString()?.Trim()
            };
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch,
                out WIN32_FIND_DATAW pfd, uint fFlags);

            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);

            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);

            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);

            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch,
                out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();

            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }
    }
}