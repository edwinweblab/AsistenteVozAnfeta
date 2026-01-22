using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Search
{
    public class LocalPathSearchService
    {
        // Carpetas ignoradas comunes (puedes agregar más)
        private static readonly HashSet<string> IgnoredFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", ".vs", "packages",
            "AppData", "Windows", "Program Files", "Program Files (x86)",
            "$Recycle.Bin", "System Volume Information", "Temp"
        };

        public List<string> GetDefaultRoots()
        {
            // Environment.SpecialFolder evita hardcode de rutas
            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads"
            };

            // Quitar vacíos/duplicados y los que no existan
            return roots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList();
        }

        public Task<List<(string Name, string FullPath)>> SearchAsync(
            string query,
            IEnumerable<string> roots,
            int maxResults = 200,
            int maxDepth = 8,
            CancellationToken ct = default)
        {
            query ??= "";
            query = query.Trim();

            if (query.Length < 2)
                return Task.FromResult(new List<(string, string)>());

            var qLower = query.ToLowerInvariant();
            var rootList = roots?.Where(Directory.Exists).ToList() ?? new List<string>();

            return Task.Run(() =>
            {
                var results = new List<(string Name, string FullPath)>(capacity: Math.Min(maxResults, 200));

                foreach (var root in rootList)
                {
                    ct.ThrowIfCancellationRequested();
                    ScanDirectory(root, depth: 0);
                    if (results.Count >= maxResults) break;
                }

                return results;

                void ScanDirectory(string dir, int depth)
                {
                    ct.ThrowIfCancellationRequested();
                    if (depth > maxDepth) return;

                    // Ignorar por nombre de carpeta
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrWhiteSpace(name) && IgnoredFolderNames.Contains(name))
                        return;

                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(dir);
                    }
                    catch
                    {
                        return; // sin permisos o error
                    }

                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();

                        var fileName = Path.GetFileName(file);
                        if (fileName == null) continue;

                        if (fileName.ToLowerInvariant().Contains(qLower))
                        {
                            results.Add((fileName, file));
                            if (results.Count >= maxResults) return;
                        }
                    }

                    IEnumerable<string> subDirs;
                    try
                    {
                        subDirs = Directory.EnumerateDirectories(dir);
                    }
                    catch
                    {
                        return;
                    }

                    foreach (var sub in subDirs)
                    {
                        if (results.Count >= maxResults) return;
                        ScanDirectory(sub, depth + 1);
                    }
                }
            }, ct);
        }
    }
}
