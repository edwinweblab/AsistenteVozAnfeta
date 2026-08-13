using Anfeta.UI.Models.Weblab;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Dropbox UX: ruta relativa + Explorador fiable =====

        /// <summary>
        /// Calcula una ruta SOLO visual a partir de la raíz Dropbox configurada.
        /// Nunca reemplaza SearchResultRow.Target: las operaciones internas siguen
        /// trabajando con la ruta local absoluta original.
        /// </summary>
        private void ApplyDropboxDisplayMetadata(SearchResultRow row)
        {
            if (row == null)
                return;

            if (row.Source == SearchSource.Notion)
            {
                row.DropboxRelativePath = string.Empty;
                row.DropboxPathColumn = string.Empty;
                return;
            }

            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) ||
                string.IsNullOrWhiteSpace(row.Target))
            {
                row.DropboxRelativePath = string.Empty;
                row.DropboxPathColumn =
                    row.Source == SearchSource.Dropbox
                        ? "Dropbox"
                        : string.Empty;
                return;
            }

            try
            {
                var root = NormalizeDropboxUxPath(DROPBOX_ROOT);
                var target = NormalizeDropboxUxPath(row.Target);

                if (!IsSameOrChildDropboxUx(root, target))
                {
                    row.DropboxRelativePath = string.Empty;
                    row.DropboxPathColumn =
                        row.Source == SearchSource.Dropbox
                            ? "Dropbox"
                            : string.Empty;
                    return;
                }

                if (string.Equals(
                        root,
                        target,
                        StringComparison.OrdinalIgnoreCase))
                {
                    row.DropboxRelativePath = "Dropbox";
                    row.DropboxPathColumn = "Dropbox";
                    return;
                }

                var relative = Path.GetRelativePath(root, target)
                    .Replace('/', '\\')
                    .Trim('\\');

                var displayFull = string.IsNullOrWhiteSpace(relative)
                    ? "Dropbox"
                    : $"Dropbox\\{relative}";

                string displayColumn;

                if (row.IsFolder)
                {
                    // Para una carpeta mostramos su propia ubicación desde Dropbox.
                    displayColumn = displayFull;
                }
                else
                {
                    // Estilo Everything: en la columna Path se muestra la carpeta
                    // contenedora, mientras Detalles muestra la ruta relativa completa.
                    var relativeParent = Path.GetDirectoryName(relative)
                        ?.Replace('/', '\\')
                        .Trim('\\');

                    displayColumn = string.IsNullOrWhiteSpace(relativeParent)
                        ? "Dropbox"
                        : $"Dropbox\\{relativeParent}";
                }

                row.DropboxRelativePath = displayFull;
                row.DropboxPathColumn = displayColumn;
            }
            catch
            {
                // La ruta visual jamás debe romper la lista de resultados.
                row.DropboxRelativePath = string.Empty;
                row.DropboxPathColumn =
                    row.Source == SearchSource.Dropbox
                        ? "Dropbox"
                        : string.Empty;
            }
        }

        private static string NormalizeDropboxUxPath(string value)
        {
            var full = Path.GetFullPath((value ?? string.Empty).Trim());

            return full.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static bool IsSameOrChildDropboxUx(
            string root,
            string target)
        {
            if (string.Equals(
                    root,
                    target,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = root.EndsWith(
                    Path.DirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            return target.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Versión robusta de "Abrir en Explorador".
        /// - Carpeta local: abre la carpeta.
        /// - Archivo local: abre Explorer y lo selecciona.
        /// - Elemento Dropbox todavía no disponible localmente: abre la carpeta
        ///   existente más cercana, en lugar de fallar silenciosamente.
        /// </summary>
        private async void CtxOpenPathReliable_Click(
            object sender,
            RoutedEventArgs e)
        {
            var rows = GetExplorerSelectedRows();

            if (rows.Count == 0)
            {
                StatusText.Text = "Estado: Selecciona un archivo o carpeta de Dropbox.";
                return;
            }

            if (rows.Any(row => row.Source == SearchSource.Notion))
            {
                StatusText.Text =
                    "Estado: Abrir en Explorador aplica a Dropbox/local, no a páginas de Notion.";
                return;
            }

            var row = rows[0];
            var target = (row.Target ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(target))
            {
                StatusText.Text = "Estado: El resultado no tiene una ruta local válida.";
                return;
            }

            try
            {
                if (Directory.Exists(target))
                {
                    StartExplorer($"\"{target}\"");
                    StatusText.Text = "Estado: Carpeta abierta en Explorador ✅";
                    return;
                }

                if (File.Exists(target))
                {
                    StartExplorer($"/select,\"{target}\"");
                    StatusText.Text = "Estado: Archivo localizado en Explorador ✅";
                    return;
                }

                var existingFolder = FindNearestExistingDropboxFolder(target);

                if (string.IsNullOrWhiteSpace(existingFolder))
                {
                    StatusText.Text =
                        "Estado: La ruta todavía no está disponible localmente en Dropbox.";
                    return;
                }

                // Primero intenta explorer.exe; si Windows lo rechaza por cualquier
                // motivo, se usa Launcher como segundo camino.
                try
                {
                    StartExplorer($"\"{existingFolder}\"");
                }
                catch
                {
                    var storageFolder =
                        await StorageFolder.GetFolderFromPathAsync(existingFolder);

                    var launched = await Launcher.LaunchFolderAsync(storageFolder);

                    if (!launched)
                        throw new InvalidOperationException(
                            "Windows no pudo abrir la carpeta disponible más cercana.");
                }

                StatusText.Text =
                    "Estado: El elemento aún no está local; se abrió su carpeta Dropbox más cercana ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir en Explorador → {ex.Message}";
            }
        }

        private List<SearchResultRow> GetExplorerSelectedRows()
        {
            var rows = ResultsList?.SelectedItems?.OfType<SearchResultRow>()
                .Distinct()
                .ToList() ?? new List<SearchResultRow>();

            if (rows.Count == 0 &&
                ResultsList?.SelectedItem is SearchResultRow selectedListRow)
            {
                rows.Add(selectedListRow);
            }

            if (rows.Count == 0 &&
                ResultsThumbnailGrid?.SelectedItem is SearchResultRow selectedGridRow)
            {
                rows.Add(selectedGridRow);
            }

            return rows;
        }

        private string FindNearestExistingDropboxFolder(string target)
        {
            string? candidate;

            try
            {
                candidate = Path.GetDirectoryName(target);
            }
            catch
            {
                candidate = null;
            }

            while (!string.IsNullOrWhiteSpace(candidate))
            {
                if (Directory.Exists(candidate))
                    return candidate;

                var parent = Path.GetDirectoryName(candidate);

                if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
                    break;

                candidate = parent;
            }

            if (!string.IsNullOrWhiteSpace(DROPBOX_ROOT) &&
                Directory.Exists(DROPBOX_ROOT))
            {
                return DROPBOX_ROOT;
            }

            return string.Empty;
        }

        private static void StartExplorer(string arguments)
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = arguments,
                    UseShellExecute = true
                });
        }

        #endregion
    }
}
