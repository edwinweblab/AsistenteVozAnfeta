using global::Dropbox.Api;
using global::Dropbox.Api.Files;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Dropbox
{
    public sealed record DropboxRemoteChange(
        string Name,
        string PathDisplay,
        string PathLower,
        bool IsDeleted,
        bool IsFolder,
        long Size,
        DateTime? ServerModifiedUtc);

    public sealed record DropboxSyncBatch(
        string Cursor,
        bool CursorInitialized,
        IReadOnlyList<DropboxRemoteChange> Changes);

    /// <summary>
    /// Consulta cambios incrementales de Dropbox mediante list_folder/cursor.
    /// La primera ejecución solo crea el cursor base; las siguientes devuelven
    /// altas, modificaciones, movimientos y eliminaciones.
    /// </summary>
    public sealed class DropboxSyncService
    {
        private readonly DropboxAuthService _authService;

        public DropboxSyncService(DropboxAuthService authService)
        {
            _authService = authService;
        }

        public async Task<DropboxSyncBatch> GetChangesAsync(
            string? savedCursor,
            CancellationToken cancellationToken = default)
        {
            var accessToken = await _authService.GetAccessTokenAsync(cancellationToken);
            using var client = new DropboxClient(accessToken);

            if (string.IsNullOrWhiteSpace(savedCursor))
            {
                // No recorremos todo Dropbox para crear el cursor inicial.
                // En cuentas grandes ListFolderAsync(recursive: true) puede tardar
                // varios minutos. Este endpoint obtiene directamente el cursor
                // del estado actual sin devolver decenas de miles de entradas.
                var latest = await client.Files.ListFolderGetLatestCursorAsync(
                    path: string.Empty,
                    recursive: true,
                    includeDeleted: true);

                return new DropboxSyncBatch(
                    Cursor: latest.Cursor,
                    CursorInitialized: true,
                    Changes: Array.Empty<DropboxRemoteChange>());
            }

            var changes = new List<DropboxRemoteChange>();
            var currentCursor = savedCursor.Trim();
            var more = true;

            while (more)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await client.Files.ListFolderContinueAsync(currentCursor);
                currentCursor = page.Cursor;
                more = page.HasMore;

                foreach (var entry in page.Entries)
                {
                    var change = ConvertEntry(entry);
                    if (change != null)
                        changes.Add(change);
                }
            }

            // Dropbox puede devolver varios eventos para una misma ruta dentro
            // del mismo lote. Conservamos el último estado conocido de cada ruta.
            var compacted = changes
                .Where(x => !string.IsNullOrWhiteSpace(x.PathLower))
                .GroupBy(x => x.PathLower, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .ToList();

            return new DropboxSyncBatch(
                Cursor: currentCursor,
                CursorInitialized: false,
                Changes: compacted);
        }

        private static DropboxRemoteChange? ConvertEntry(Metadata entry)
        {
            if (entry == null)
                return null;

            if (entry.IsDeleted)
            {
                var deleted = entry.AsDeleted;
                return new DropboxRemoteChange(
                    Name: deleted.Name ?? string.Empty,
                    PathDisplay: deleted.PathDisplay ?? deleted.PathLower ?? string.Empty,
                    PathLower: deleted.PathLower ?? string.Empty,
                    IsDeleted: true,
                    IsFolder: false,
                    Size: 0,
                    ServerModifiedUtc: null);
            }

            if (entry.IsFolder)
            {
                var folder = entry.AsFolder;
                return new DropboxRemoteChange(
                    Name: folder.Name ?? string.Empty,
                    PathDisplay: folder.PathDisplay ?? folder.PathLower ?? string.Empty,
                    PathLower: folder.PathLower ?? string.Empty,
                    IsDeleted: false,
                    IsFolder: true,
                    Size: 0,
                    ServerModifiedUtc: null);
            }

            if (entry.IsFile)
            {
                var file = entry.AsFile;
                return new DropboxRemoteChange(
                    Name: file.Name ?? string.Empty,
                    PathDisplay: file.PathDisplay ?? file.PathLower ?? string.Empty,
                    PathLower: file.PathLower ?? string.Empty,
                    IsDeleted: false,
                    IsFolder: false,
                    Size: (long)file.Size,
                    ServerModifiedUtc: file.ServerModified);
            }

            return null;
        }
    }
}
