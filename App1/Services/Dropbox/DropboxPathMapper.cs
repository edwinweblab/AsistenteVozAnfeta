using System;
using System.IO;

namespace Anfeta.UI.Services.Dropbox
{
    public sealed class DropboxPathMapper
    {
        public bool TryToDropboxPath(
            string dropboxRoot,
            string localPath,
            out string dropboxPath,
            out string error)
        {
            dropboxPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(dropboxRoot))
            {
                error = "No hay una carpeta raíz de Dropbox configurada.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(localPath))
            {
                error = "No se pudo resolver la carpeta destino.";
                return false;
            }

            var root = NormalizeLocalPath(dropboxRoot);
            var target = NormalizeLocalPath(localPath);

            if (!IsSameOrChild(root, target))
            {
                error = "La ruta seleccionada no pertenece a la carpeta Dropbox configurada.";
                return false;
            }

            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
            {
                dropboxPath = string.Empty;
                return true;
            }

            var relative = Path.GetRelativePath(root, target)
                .Replace('\\', '/')
                .Trim('/');

            dropboxPath = "/" + relative;
            return true;
        }

        public string CombineDropboxPath(string parentDropboxPath, string childName)
        {
            var parent = (parentDropboxPath ?? string.Empty).Trim().TrimEnd('/');
            var child = (childName ?? string.Empty).Trim().Trim('/');

            return string.IsNullOrWhiteSpace(parent)
                ? "/" + child
                : parent + "/" + child;
        }

        public bool IsInsideDropboxRoot(string dropboxRoot, string localPath)
        {
            if (string.IsNullOrWhiteSpace(dropboxRoot) ||
                string.IsNullOrWhiteSpace(localPath))
                return false;

            return IsSameOrChild(
                NormalizeLocalPath(dropboxRoot),
                NormalizeLocalPath(localPath));
        }

        private static bool IsSameOrChild(string root, string target)
        {
            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
                return true;

            var prefix = root.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal)
                    ? root
                    : root + Path.DirectorySeparatorChar;

            return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLocalPath(string value)
        {
            var full = Path.GetFullPath(value.Trim());
            return full.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }
}
