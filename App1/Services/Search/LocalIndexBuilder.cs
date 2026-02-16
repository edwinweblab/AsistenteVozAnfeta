using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Search
{
    public static class LocalIndexBuilder
    {
        public static Task<List<SearchResultRow>> BuildAsync(
            string rootPath,
            CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(rootPath))
                    throw new ArgumentException("rootPath vacío");

                if (!Directory.Exists(rootPath))
                    throw new DirectoryNotFoundException($"No existe la ruta: {rootPath}");

                var tmp = new List<SearchResultRow>(capacity: 4096);

                foreach (var dir in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    tmp.Add(new SearchResultRow
                    {
                        Name = Path.GetFileName(dir),
                        Target = dir,
                        Type = "FOLDER",
                        Source = SearchSource.Local
                    });
                }

                foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    var info = new FileInfo(file);

                    tmp.Add(new SearchResultRow
                    {
                        Name = Path.GetFileName(file),
                        Target = file,
                        Type = "FILE",
                        Size = info.Length,
                        ServerModified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        Source = SearchSource.Local
                    });
                }

                return tmp;
            }, ct);
        }
    }
}
