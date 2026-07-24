using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Anfeta.UI.Services
{
    public sealed class ShellIconService
    {
        private readonly ConcurrentDictionary<string, IconSource> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Iconos fallback (ponlos como assets si quieres)
        private static IconSource FolderIcon => new SymbolIconSource { Symbol = Symbol.Folder };
        private static IconSource FileIcon => new SymbolIconSource { Symbol = Symbol.Document };

        public IconSource GetIcon(string type, string nameOrPath)
        {
            // carpeta
            if (string.Equals(type, "FOLDER", StringComparison.OrdinalIgnoreCase))
                return FolderIcon;

            // por extensión
            var ext = Path.GetExtension(nameOrPath ?? "").TrimStart('.');
            if (string.IsNullOrWhiteSpace(ext))
                return FileIcon;

            var key = $"ext:{ext}";
            return _cache.GetOrAdd(key, _ => IconFromExt(ext));
        }

        private IconSource IconFromExt(string ext)
        {
            // ✅ estable: mapeo manual (puedes ampliar)
            ext = ext.ToLowerInvariant();

            if (ext is "pdf") return new SymbolIconSource { Symbol = Symbol.Library };
            if (ext is "doc" or "docx") return new SymbolIconSource { Symbol = Symbol.Page };
            if (ext is "xls" or "xlsx") return new SymbolIconSource { Symbol = Symbol.Calculator };
            if (ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp") return new SymbolIconSource { Symbol = Symbol.Pictures };
            if (ext is "zip" or "rar" or "7z") return new SymbolIconSource { Symbol = Symbol.Folder };
            if (ext is "mp4" or "mkv" or "avi") return new SymbolIconSource { Symbol = Symbol.Video };
            if (ext is "mp3" or "wav" or "flac") return new SymbolIconSource { Symbol = Symbol.MusicInfo };
            if (ext is "url") return new SymbolIconSource { Symbol = Symbol.World };


            return FileIcon;
        }

        public async Task<ImageSource?> GetThumbnailAsync(
            string path,
            uint requestedSize,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path))
            {
                return null;
            }

            var extension = Path.GetExtension(path)
                .ToLowerInvariant();

            if (extension is not (
                ".png" or ".jpg" or ".jpeg" or
                ".webp" or ".gif" or ".bmp"))
            {
                return null;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = await StorageFile.GetFileFromPathAsync(path);

                using var thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.PicturesView,
                    Math.Max(64u, requestedSize),
                    ThumbnailOptions.UseCurrentScale);

                cancellationToken.ThrowIfCancellationRequested();

                if (thumbnail == null || thumbnail.Size == 0)
                    return null;

                var bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical
                };

                await bitmap.SetSourceAsync(thumbnail);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
