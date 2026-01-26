using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Bookmarks
{
    public sealed class BookmarksService
    {
        private readonly string _filePath;

        public BookmarksService(string? filePath = null)
        {
            _filePath = filePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Anfeta",
                "bookmarks.json"
            );
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        // 🔥 normaliza rutas para que "C:\a\b\" == "c:\A\B"
        private static string NormPath(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "";

            var s = p.Trim();

            // intenta dejarlo como ruta completa
            try { s = Path.GetFullPath(s); } catch { /* ignore */ }

            // unifica separadores
            s = s.Replace('/', '\\');

            // quita "\" al final (importante para carpetas)
            s = s.TrimEnd('\\');

            // Windows: comparación case-insensitive
            return s.ToLowerInvariant();
        }

        public async Task<List<BookmarkItem>> LoadAsync(CancellationToken ct)
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new List<BookmarkItem>();

                var json = await File.ReadAllTextAsync(_filePath, ct);
                var items = JsonSerializer.Deserialize<List<BookmarkItem>>(json, JsonOpts) ?? new List<BookmarkItem>();

                // dedup también al cargar
                return items
                    .GroupBy(x => NormPath(x.LocalPath), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
                    .ToList();
            }
            catch
            {
                return new List<BookmarkItem>();
            }
        }

        public async Task SaveAsync(List<BookmarkItem> items, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            var dedup = items
                .GroupBy(x => NormPath(x.LocalPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
                .ToList();

            // ✅ MUY IMPORTANTE: sincroniza la lista en memoria
            items.Clear();
            items.AddRange(dedup);

            var json = JsonSerializer.Serialize(dedup, JsonOpts);
            await File.WriteAllTextAsync(_filePath, json, ct);
        }

        public bool Exists(List<BookmarkItem> items, string localPath)
        {
            var key = NormPath(localPath);
            return items.Any(x => NormPath(x.LocalPath) == key);
        }

        public void RemoveByPath(List<BookmarkItem> items, string localPath)
        {
            var key = NormPath(localPath);
            items.RemoveAll(x => NormPath(x.LocalPath) == key);
        }
    }
}
