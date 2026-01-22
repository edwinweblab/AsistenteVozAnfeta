using System;
using System.IO;

namespace Anfeta.UI.Services.Dropbox
{
    public class DropboxLocalFolderService
    {
        public record DropboxLocalFolderResult(
            bool Found,
            string? Path,
            string Message
        );

        /// <summary>
        /// Detecta rutas típicas de Dropbox Desktop y valida que existan.
        /// MVP: busca en %USERPROFILE%\Dropbox
        /// </summary>
        public DropboxLocalFolderResult Detect()
        {
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                // Ruta típica
                var candidate = System.IO.Path.Combine(userProfile, "Dropbox");

                if (Directory.Exists(candidate))
                {
                    // Señal fuerte de Dropbox Desktop
                    var cacheFolder = System.IO.Path.Combine(candidate, ".dropbox.cache");

                    var msg = Directory.Exists(cacheFolder)
                        ? "Dropbox local detectado (cache OK)"
                        : "Dropbox local detectado";

                    return new DropboxLocalFolderResult(true, candidate, msg);
                }

                return new DropboxLocalFolderResult(false, null, "Dropbox local no encontrado");
            }
            catch (Exception ex)
            {
                return new DropboxLocalFolderResult(false, null, $"Error detectando Dropbox local: {ex.Message}");
            }
        }
    }
}
