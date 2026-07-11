using global::Dropbox.Api;
using global::Dropbox.Api.Files;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Dropbox
{
    public sealed record DropboxUploadResult(
        string Name,
        string PathDisplay,
        string PathLower,
        long Size,
        DateTime ServerModifiedUtc);

    public sealed class DropboxFileService
    {
        private readonly DropboxAuthService _authService;

        public DropboxFileService(DropboxAuthService authService)
        {
            _authService = authService;
        }

        public async Task CreateFolderAsync(
            string dropboxPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(dropboxPath) || dropboxPath == "/")
                throw new ArgumentException("La ruta remota de la carpeta es inválida.");

            var accessToken = await _authService.GetAccessTokenAsync(cancellationToken);

            using var client = new global::Dropbox.Api.DropboxClient(accessToken);

            try
            {
                await client.Files.CreateFolderV2Async(
                    path: dropboxPath,
                    autorename: false);
            }
            catch (global::Dropbox.Api.ApiException<CreateFolderError> ex)
            {
                if (ex.ErrorResponse.IsPath &&
                    ex.ErrorResponse.AsPath.Value.IsConflict)
                {
                    throw new InvalidOperationException(
                        "Ya existe una carpeta o archivo con ese nombre.");
                }

                throw new InvalidOperationException(
                    $"Dropbox no pudo crear la carpeta: {ex.Message}", ex);
            }
        }

        public async Task<bool> ExistsAsync(
            string dropboxPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(dropboxPath))
                return false;

            var accessToken = await _authService.GetAccessTokenAsync(cancellationToken);
            using var client = new global::Dropbox.Api.DropboxClient(accessToken);

            try
            {
                _ = await client.Files.GetMetadataAsync(dropboxPath);
                return true;
            }
            catch (global::Dropbox.Api.ApiException<GetMetadataError> ex)
            {
                if (ex.ErrorResponse.IsPath &&
                    ex.ErrorResponse.AsPath.Value.IsNotFound)
                {
                    return false;
                }

                throw new InvalidOperationException(
                    $"Dropbox no pudo comprobar si el archivo existe: {ex.Message}", ex);
            }
        }

        public async Task<DropboxUploadResult> UploadFileAsync(
            string localFilePath,
            string dropboxPath,
            bool overwrite,
            bool autorename,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
                throw new FileNotFoundException("No se encontró el archivo que se va a subir.", localFilePath);

            if (string.IsNullOrWhiteSpace(dropboxPath) || dropboxPath == "/")
                throw new ArgumentException("La ruta remota del archivo es inválida.");

            var info = new FileInfo(localFilePath);

            // El endpoint UploadAsync admite archivos de hasta 150 MB.
            // Los archivos mayores se implementarán después con sesiones fragmentadas.
            const long maxSimpleUploadBytes = 150L * 1024L * 1024L;
            if (info.Length > maxSimpleUploadBytes)
            {
                throw new InvalidOperationException(
                    "Este archivo supera 150 MB. La subida por partes se agregará en la siguiente fase.");
            }

            var accessToken = await _authService.GetAccessTokenAsync(cancellationToken);
            using var client = new global::Dropbox.Api.DropboxClient(accessToken);
            await using var stream = new FileStream(
                localFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            WriteMode writeMode = overwrite
                ? (WriteMode)WriteMode.Overwrite.Instance
                : (WriteMode)WriteMode.Add.Instance;

            try
            {
                var metadata = await client.Files.UploadAsync(
                    path: dropboxPath,
                    mode: writeMode,
                    autorename: autorename,
                    mute: false,
                    strictConflict: !overwrite,
                    body: stream);

                return new DropboxUploadResult(
                    Name: metadata.Name ?? Path.GetFileName(localFilePath),
                    PathDisplay: metadata.PathDisplay ?? dropboxPath,
                    PathLower: metadata.PathLower ?? dropboxPath.ToLowerInvariant(),
                    Size: (long)metadata.Size,
                    ServerModifiedUtc: metadata.ServerModified);
            }
            catch (global::Dropbox.Api.ApiException<UploadError> ex)
            {
                // El SDK 7.0.0 no expone IsConflict directamente sobre UploadWriteFailed.
                // Los duplicados ya se detectan antes con ExistsAsync.
                throw new InvalidOperationException(
                    $"Dropbox no pudo subir el archivo: {ex.Message}", ex);
            }
        }
    }
}