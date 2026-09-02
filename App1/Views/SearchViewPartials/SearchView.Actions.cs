using Anfeta.UI.Models;
using Anfeta.UI.Models.Notion;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Speech;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Acciones del Menú Contextual =====

        private async void CtxOpen_Click(
            object sender,
            RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0)
                return;

            const int MAX_OPEN = 5;

            if (rows.Count > 1)
            {
                var confirmed = await ConfirmOpenManyAsync(
                    rows.Count,
                    MAX_OPEN);

                if (!confirmed)
                    return;
            }

            var max = Math.Min(rows.Count, MAX_OPEN);
            var opened = 0;
            var failed = 0;
            string? lastError = null;

            for (var index = 0; index < max; index++)
            {
                var row = rows[index];

                try
                {
                    if (IsNotionRow(row))
                    {
                        var notionOpened = await OpenNotionDesktopAsync(
                            row,
                            allowBrowserFallback: true);

                        if (notionOpened)
                            opened++;
                        else
                            failed++;

                        continue;
                    }

                    var target = GetRowTarget(row);

                    if (string.IsNullOrWhiteSpace(target))
                    {
                        failed++;
                        continue;
                    }

                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = target,
                            UseShellExecute = true
                        });

                    opened++;
                }
                catch (Exception ex)
                {
                    failed++;
                    lastError = ex.Message;
                }
            }

            // Para una sola página de Notion, el helper ya indicó si
            // se abrió en Desktop o en el navegador. No se reemplaza ese
            // mensaje por un "Abierto" genérico.
            if (rows.Count == 1 &&
                IsNotionRow(rows[0]))
            {
                return;
            }

            if (failed == 0)
            {
                StatusText.Text = opened == 1
                    ? "Estado: Abierto ✅"
                    : $"Estado: Abiertos {opened} elemento(s) ✅";
            }
            else
            {
                StatusText.Text =
                    $"Estado: Abiertos {opened} · Fallaron {failed}" +
                    (string.IsNullOrWhiteSpace(lastError)
                        ? string.Empty
                        : $" · Último: {lastError}");
            }
        }

        private async void CtxOpenInApp_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var first = rows[0];

            try
            {
                if (first.IsFolder)
                {
                    await BrowseFolderAsync(first.Target, pushHistory: true);
                }
                else
                {
                    var parent = Path.GetDirectoryName(first.Target);
                    if (string.IsNullOrWhiteSpace(parent)) return;

                    await BrowseFolderAsync(parent, pushHistory: true);

                    foreach (var r in rows.Take(50))
                    {
                        var match = Results.FirstOrDefault(x =>
                            string.Equals(x.Target, r.Target, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                            ResultsList.SelectedItems.Add(match);
                    }
                }

                StatusText.Text = "Abierto en ANFETA ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al abrir en ANFETA: {ex.Message}";
            }
        }

        private void CtxCopyName_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var text = string.Join(
                Environment.NewLine,
                rows.Select(GetCopyableRowName));
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            StatusText.Text = rows.Count == 1 ? "Copiado: nombre ✅" : $"Copiados {rows.Count} nombres ✅";
        }

        private static string GetCopyableRowName(
            SearchResultRow row)
        {
            var name =
                (row?.DisplayName ??
                 row?.Name ??
                 string.Empty).Trim();

            if (row == null ||
                row.Source != SearchSource.Notion)
            {
                return name;
            }

            var source =
                (row.ExternalSourceName ??
                 string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(source))
            {
                var prefix = $"[{source}]";

                if (name.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return name
                        .Substring(prefix.Length)
                        .TrimStart();
                }
            }

            if (name.StartsWith("[", StringComparison.Ordinal))
            {
                var closingBracket = name.IndexOf(']');

                if (closingBracket >= 0 &&
                    closingBracket + 1 < name.Length)
                {
                    return name
                        .Substring(closingBracket + 1)
                        .TrimStart();
                }
            }

            return name;
        }

        private void CtxCopyFullPath_Click(object sender, RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);
            if (row == null) return;

            try
            {
                var target = GetRowTarget(row);

                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(target);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

                StatusText.Text = IsNotionRow(row)
                    ? "Copiado: URL de Notion ✅"
                    : "Copiado: ruta ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al copiar ruta: {ex.Message}";
            }
        }

        private void CtxOpenPath_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            if (rows.Any(IsNotionRow))
            {
                StatusText.Text = "Estado: 'Abrir en Explorador Local' no aplica para páginas de Notion. Usa 'Abrir'.";
                return;
            }

            var first = rows[0];

            try
            {
                if (rows.Count == 1)
                {
                    var args = first.IsFolder
                        ? $"\"{first.Target}\""
                        : $"/select,\"{first.Target}\"";

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = args,
                        UseShellExecute = true
                    });

                    StatusText.Text = "Explorer abierto ✅";
                }
                else
                {
                    var folder = first.IsFolder
                        ? first.Target
                        : (Path.GetDirectoryName(first.Target) ?? DROPBOX_ROOT);

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{folder}\"",
                        UseShellExecute = true
                    });

                    StatusText.Text = "Explorer abierto (primer elemento) ✅";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error Open Path: {ex.Message}";
            }
        }

        private void CtxOpenWeb_Click(object sender, RoutedEventArgs e)
        {
            CtxOpenPath_Click(sender, e);
        }

        private void CtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var text = string.Join(Environment.NewLine, rows.Select(GetRowTarget));

            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            var hasNotion = rows.Any(IsNotionRow);

            StatusText.Text = rows.Count == 1
                ? hasNotion ? "Copiado: URL de Notion ✅" : "Copiado: ruta ✅"
                : hasNotion ? $"Copiados {rows.Count} enlaces/rutas ✅" : $"Copiadas {rows.Count} rutas ✅";
        }

        private void CtxCopyLink_Click(object sender, RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);
            if (row == null)
            {
                StatusText.Text = "DEBUG: row null (copiar link)";
                return;
            }

            var target = GetRowTarget(row);

            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(target);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            StatusText.Text = IsNotionRow(row)
                ? "Copiado: link de Notion ✅"
                : "Copiado ✅";
        }

        private void CtxCopyDomain_Click(
            object sender,
            RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);

            if (row == null)
            {
                StatusText.Text =
                    "Estado: Selecciona un resultado.";
                return;
            }

            var domain = TryExtractFirstDomain(row);

            if (string.IsNullOrWhiteSpace(domain))
            {
                StatusText.Text =
                    "Estado: No se encontró un dominio en este resultado.";
                return;
            }

            var package =
                new Windows.ApplicationModel.DataTransfer
                    .DataPackage();

            package.SetText(domain);

            Windows.ApplicationModel.DataTransfer
                .Clipboard.SetContent(package);

            StatusText.Text =
                $"Estado: Dominio copiado ✅ {domain}";
        }

        private async void CtxCopyContent_Click(
            object sender,
            RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);
            if (row == null)
            {
                StatusText.Text = "Estado: Selecciona un resultado para copiar su contenido.";
                return;
            }

            try
            {
                string content = string.Empty;

                if (IsNotionRow(row))
                {
                    var pageId = (row.ExternalId ?? string.Empty).Trim();

                    // Si ya está cargada la vista previa para esta misma página, usamos el contenido actual
                    if (NotionPreviewContent != null &&
                        !string.IsNullOrWhiteSpace(pageId) &&
                        string.Equals(_activePreviewPageId, pageId, StringComparison.OrdinalIgnoreCase))
                    {
                        var lines = new List<string>();
                        CollectNotionPreviewText(NotionPreviewContent, lines);
                        content = string.Join(
                            Environment.NewLine,
                            lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()));
                    }

                    // Si no está cargada aún, obtenemos los bloques de Notion directamente
                    if (string.IsNullOrWhiteSpace(content) &&
                        !string.IsNullOrWhiteSpace(pageId) &&
                        _notionPreviewService != null)
                    {
                        var token = (ApplicationData.Current.LocalSettings.Values["Notion.Token"] as string ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            StatusText.Text = "Obteniendo contenido de Notion...";
                            var blocks = await _notionPreviewService.GetPagePreviewAsync(token, pageId, CancellationToken.None);
                            if (blocks != null && blocks.Count > 0)
                            {
                                var blockLines = new List<string>();
                                foreach (var b in blocks)
                                {
                                    if (string.IsNullOrWhiteSpace(b.Text) && string.IsNullOrWhiteSpace(b.Url)) continue;
                                    var prefix = b.Kind switch
                                    {
                                        NotionPreviewBlockKind.Heading1 => "# ",
                                        NotionPreviewBlockKind.Heading2 => "## ",
                                        NotionPreviewBlockKind.Heading3 => "### ",
                                        NotionPreviewBlockKind.BulletedListItem => "• ",
                                        NotionPreviewBlockKind.NumberedListItem => "1. ",
                                        NotionPreviewBlockKind.ToDo => b.IsChecked ? "[x] " : "[ ] ",
                                        NotionPreviewBlockKind.Quote => "> ",
                                        NotionPreviewBlockKind.Callout => "💡 ",
                                        _ => ""
                                    };
                                    var txt = !string.IsNullOrWhiteSpace(b.Text) ? b.Text : b.Url;
                                    blockLines.Add($"{prefix}{txt}");
                                }
                                content = string.Join(Environment.NewLine, blockLines);
                            }
                        }
                    }

                    // Fallback a Description si no se obtuvieron bloques
                    if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(row.Description))
                    {
                        content = row.Description.Trim();
                    }
                }
                else
                {
                    // Archivo local
                    var path = row.FullPath ?? row.Target ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        var fileInfo = new FileInfo(path);
                        if (fileInfo.Length <= 5 * 1024 * 1024) // < 5MB
                        {
                            content = await File.ReadAllTextAsync(path);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(row.Description))
                    {
                        content = row.Description.Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    StatusText.Text = "Estado: No hay contenido de texto disponible para copiar.";
                    if (NotionPreviewStatus != null)
                    {
                        NotionPreviewStatus.Text = "No hay contenido cargado para copiar.";
                    }
                    return;
                }

                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(content);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();

                StatusText.Text = $"Contenido copiado al portapapeles ✅ ({content.Length:N0} caracteres)";
                if (NotionPreviewStatus != null)
                {
                    NotionPreviewStatus.Text = $"Contenido copiado ✅ · {content.Length:N0} caracteres";
                }
                if (BtnCopyNotionPreviewContent != null)
                {
                    BtnCopyNotionPreviewContent.Content = "Copiado ✓";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al copiar contenido: {ex.Message}";
            }
        }

        private async void CtxOpenDomain_Click(
            object sender,
            RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);

            if (row == null)
            {
                StatusText.Text =
                    "Estado: Selecciona un resultado.";
                return;
            }

            var domain = TryExtractFirstDomain(row);

            if (string.IsNullOrWhiteSpace(domain))
            {
                StatusText.Text =
                    "Estado: No se encontró un dominio en este resultado.";
                return;
            }

            try
            {
                var uri = new Uri($"https://{domain}");
                var opened = await Launcher.LaunchUriAsync(uri);

                StatusText.Text = opened
                    ? $"Estado: Dominio abierto ✅ {domain}"
                    : $"Estado: No se pudo abrir {domain}.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: Error abriendo dominio → {ex.Message}";
            }
        }

        private static string TryExtractFirstDomain(
            SearchResultRow row)
        {
            // Buscar únicamente en el nombre visible del resultado.
            // No se toma el dominio desde URL, descripción, estado o ruta.
            var candidates = new[]
            {
                row?.DisplayName,
                row?.Name
            };

            const string pattern =
                @"(?<![\w@])(?:https?://)?(?:www\.)?" +
                @"(?<domain>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+" +
                @"(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|" +
                @"com|mx|org|net|io|co|app|dev))" +
                @"(?=$|[/:?#\s)\]}>.,;!])";

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                var match = Regex.Match(
                    candidate,
                    pattern,
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

                if (match.Success)
                {
                    return match.Groups["domain"]
                        .Value
                        .Trim()
                        .TrimEnd('.')
                        .ToLowerInvariant();
                }
            }

            return string.Empty;
        }

        private async void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0)
                return;

            var notionRows = rows.Where(IsNotionRow).ToList();
            var localRows = rows.Where(x => !IsNotionRow(x)).ToList();

            if (notionRows.Count > 0 && localRows.Count > 0)
            {
                StatusText.Text =
                    "Estado: No se pueden mezclar páginas de Notion y archivos locales en la misma eliminación.";
                return;
            }

            if (notionRows.Count > 0)
            {
                await MoveNotionPagesToTrashAsync(notionRows);
                return;
            }

            var ok = await ConfirmDeleteAsync(localRows);
            if (!ok)
                return;

            try
            {
                if (localRows.Count == 1)
                    await ApplyFileChangeAsync(FileChangeKind.Delete, localRows[0]);
                else
                    await ApplyBatchDeleteAsync(localRows);

                StatusText.Text = localRows.Count == 1
                    ? "Estado: Eliminado ✅"
                    : $"Estado: Eliminados {localRows.Count} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al eliminar: {ex.Message}";
            }
        }

        private async Task MoveNotionPagesToTrashAsync(
            List<SearchResultRow> rows)
        {
            var validRows = rows
                .Where(x => IsNotionRow(x) &&
                            !string.IsNullOrWhiteSpace(x.ExternalId))
                .ToList();

            if (validRows.Count == 0)
            {
                StatusText.Text =
                    "Estado: No se encontraron páginas válidas de Notion.";
                return;
            }

            var preview = string.Join(
                "\n",
                validRows.Take(6).Select(x => $"• {x.DisplayName}"));

            if (validRows.Count > 6)
                preview += $"\n• … y {validRows.Count - 6} más";

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = validRows.Count == 1
                    ? "Mover página a la papelera"
                    : $"Mover {validRows.Count} páginas a la papelera",
                Content =
                    $"{preview}\n\n" +
                    "Las páginas dejarán de aparecer en sus bases y en ANFETA, " +
                    "pero podrán recuperarse desde la papelera de Notion.",
                PrimaryButtonText = "Mover a papelera",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var token = GetSavedNotionToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura y guarda primero el token de Notion.";
                return;
            }

            ShowLoadingState(
                "Estado: Moviendo páginas a la papelera...",
                $"{validRows.Count} página(s) de Notion");

            var service = new NotionPageActionsService();
            var removedIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            var success = 0;
            var failed = 0;
            string? lastError = null;

            try
            {
                foreach (var row in validRows)
                {
                    try
                    {
                        StatusText.Text =
                            $"Estado: Moviendo a papelera → {row.DisplayName}";

                        using var cts =
                            new CancellationTokenSource(TimeSpan.FromSeconds(45));

                        await service.MovePageToTrashAsync(
                            token,
                            row.ExternalId,
                            cts.Token);

                        removedIds.Add(row.ExternalId);
                        success++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        lastError = ex.Message;
                    }
                }

                if (removedIds.Count > 0)
                    await RemoveNotionRowsFromIndexAsync(removedIds);

                StatusText.Text = failed == 0
                    ? $"Estado: Movidas a papelera ✅ ({success})"
                    : $"Estado: Movidas ✅ ({success}) · Fallaron ❌ ({failed})" +
                      (string.IsNullOrWhiteSpace(lastError)
                          ? string.Empty
                          : $" · Último: {lastError}");
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async void CtxCreateDropboxFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveDropboxDestination(sender, out var destinationLocal, out var destinationRemote, out var error))
            {
                StatusText.Text = $"Estado: {error}";
                return;
            }

            var folderName = await PromptCreateDropboxFolderAsync(destinationLocal);
            if (string.IsNullOrWhiteSpace(folderName))
                return;

            if (!TryValidateDropboxFolderName(folderName, out error))
            {
                StatusText.Text = $"Estado: {error}";
                return;
            }

            var cleanName = folderName.Trim();
            var remoteFolderPath = _dropboxPathMapper.CombineDropboxPath(
                destinationRemote,
                cleanName);

            var expectedLocalPath = Path.Combine(destinationLocal, cleanName);

            try
            {
                ShowLoadingState(
                    "Estado: Creando carpeta en Dropbox...",
                    $"Destino: {destinationLocal}");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await _dropboxFileService.CreateFolderAsync(remoteFolderPath, cts.Token);

                var appearedLocally = await WaitForLocalFolderAsync(
                    expectedLocalPath,
                    TimeSpan.FromSeconds(20));

                await AddCreatedFolderToIndexAsync(expectedLocalPath);
                await RefreshDropboxFolderUiAsync(destinationLocal);

                StatusText.Text = appearedLocally
                    ? "Estado: Carpeta creada en Dropbox ✅"
                    : "Estado: Carpeta creada en Dropbox ✅ Esperando sincronización local...";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Estado: Dropbox tardó demasiado en responder.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error creando carpeta → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private bool TryResolveDropboxDestination(
            object sender,
            out string localFolder,
            out string dropboxFolder,
            out string error)
        {
            localFolder = string.Empty;
            dropboxFolder = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
            {
                error = "Configura primero la carpeta raíz de Dropbox.";
                return false;
            }

            var row = GetCtxRowOrSelected(sender);

            if (row != null && IsNotionRow(row))
            {
                error = "Esta acción no aplica para páginas de Notion.";
                return false;
            }

            if (row != null)
            {
                localFolder = row.IsFolder
                    ? row.Target
                    : Path.GetDirectoryName(row.Target) ?? string.Empty;
            }
            else
            {
                localFolder =
                    !string.IsNullOrWhiteSpace(_currentFolderPath)
                        ? _currentFolderPath
                        : !string.IsNullOrWhiteSpace(_currentFolder)
                            ? _currentFolder
                            : DROPBOX_ROOT;
            }

            if (string.IsNullOrWhiteSpace(localFolder) || !Directory.Exists(localFolder))
            {
                error = "No se encontró una carpeta local válida como destino.";
                return false;
            }

            if (!_dropboxPathMapper.TryToDropboxPath(
                    DROPBOX_ROOT,
                    localFolder,
                    out dropboxFolder,
                    out error))
            {
                return false;
            }

            return true;
        }

        private async Task<string?> PromptCreateDropboxFolderAsync(string destinationLocal)
        {
            var nameBox = new TextBox
            {
                Width = 360,
                PlaceholderText = "Nombre de la carpeta"
            };

            var pathText = new TextBlock
            {
                Text = $"Se creará dentro de:\n{destinationLocal}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            };

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(pathText);
            content.Children.Add(nameBox);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Crear carpeta en Dropbox",
                Content = content,
                PrimaryButtonText = "Crear",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };

            nameBox.TextChanged += (_, __) =>
            {
                dialog.IsPrimaryButtonEnabled =
                    !string.IsNullOrWhiteSpace(nameBox.Text);
            };

            dialog.Opened += (_, __) =>
            {
                nameBox.Focus(FocusState.Programmatic);
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary
                ? nameBox.Text
                : null;
        }

        private static bool TryValidateDropboxFolderName(
            string folderName,
            out string error)
        {
            error = string.Empty;
            var clean = (folderName ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
            {
                error = "Escribe un nombre para la carpeta.";
                return false;
            }

            if (clean is "." or "..")
            {
                error = "Ese nombre de carpeta no es válido.";
                return false;
            }

            if (clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                clean.Contains('/') ||
                clean.Contains('\\'))
            {
                error = "El nombre contiene caracteres no permitidos.";
                return false;
            }

            if (clean.EndsWith(".", StringComparison.Ordinal) ||
                clean.EndsWith(" ", StringComparison.Ordinal))
            {
                error = "El nombre no puede terminar con punto o espacio.";
                return false;
            }

            var reserved = new[]
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };

            if (reserved.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                error = "Ese nombre está reservado por Windows.";
                return false;
            }

            return true;
        }

        private static async Task<bool> WaitForLocalFolderAsync(
            string localPath,
            TimeSpan timeout)
        {
            var started = DateTime.UtcNow;

            while (DateTime.UtcNow - started < timeout)
            {
                if (Directory.Exists(localPath))
                    return true;

                await Task.Delay(500);
            }

            return Directory.Exists(localPath);
        }

        private async Task AddCreatedFolderToIndexAsync(string localPath)
        {
            var snapshot = App.LocalIndex.GetAll();

            if (!snapshot.Any(x =>
                    x.Source != SearchSource.Notion &&
                    string.Equals(x.Target, localPath, StringComparison.OrdinalIgnoreCase)))
            {
                snapshot.Add(new SearchResultRow
                {
                    Name = Path.GetFileName(localPath),
                    Target = localPath,
                    Type = "FOLDER",
                    Source = SearchSource.Local
                });

                App.LocalIndex.Set(snapshot);
                await LocalIndexPersistence.SaveAsync(
                    DROPBOX_ROOT,
                    snapshot,
                    CancellationToken.None);
            }
        }

        private async Task RefreshDropboxFolderUiAsync(string parentFolder)
        {
            BuildTreeRoot();

            if (_isBrowsing &&
                string.Equals(_currentFolder, parentFolder, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(parentFolder))
            {
                await BrowseFolderAsync(parentFolder, pushHistory: false);
                return;
            }

            var query = (SearchBox.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(query))
                await RunSearchAsync(query);
        }

        #region ===== Pantallazo de resultados =====

        private async void BtnCaptureResults_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (ResultsViewHost == null ||
                ResultsViewHost.ActualWidth < 2 ||
                ResultsViewHost.ActualHeight < 2)
            {
                StatusText.Text =
                    "Estado: El área de resultados todavía no está lista para capturarse.";
                return;
            }

            if (Results.Count == 0)
            {
                var emptyDialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "No hay resultados para capturar",
                    Content =
                        "Realiza una búsqueda o abre una carpeta antes de tomar el pantallazo.",
                    CloseButtonText = "Cerrar",
                    DefaultButton = ContentDialogButton.Close
                };

                await emptyDialog.ShowAsync();
                return;
            }

            var queryTitle =
                BuildResultsCaptureTitle();

            StorageFile? captureFile = null;

            try
            {
                BtnCaptureResults.IsEnabled = false;

                ShowLoadingState(
                    "Estado: Preparando pantallazo...",
                    "Capturando los resultados visibles de la búsqueda actual.");

                // Esperamos a que el botón y el overlay reflejen el estado
                // antes de capturar el área central.
                await Task.Delay(120);

                // El overlay de carga vive dentro del área capturable.
                // Se oculta durante el render para que no aparezca en la imagen.
                var previousOverlayVisibility =
                    LoadingOverlay.Visibility;

                LoadingOverlay.Visibility =
                    Visibility.Collapsed;

                try
                {
                    captureFile =
                        await CaptureResultsAreaToPngAsync(
                            queryTitle);
                }
                finally
                {
                    LoadingOverlay.Visibility =
                        previousOverlayVisibility;
                }

                HideLoadingState();

                await UploadFilesToNotionRevisionsAsync(
                    new[] { captureFile },
                    "pantallazo de resultados");
            }
            catch (Exception ex)
            {
                HideLoadingState();

                StatusText.Text =
                    $"Estado: No se pudo crear el pantallazo → {ex.Message}";
            }
            finally
            {
                BtnCaptureResults.IsEnabled = true;
            }
        }

        private string BuildResultsCaptureTitle()
        {
            var query =
                (SearchBox?.Text ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(query))
                return query;

            if (_isBrowsing &&
                !string.IsNullOrWhiteSpace(_currentFolderPath))
            {
                var folderName =
                    Path.GetFileName(
                        _currentFolderPath.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar));

                if (!string.IsNullOrWhiteSpace(folderName))
                    return $"Resultados {folderName}";
            }

            return $"Resultados ANFETA {DateTime.Now:yyyy-MM-dd HH-mm}";
        }

        private async Task<StorageFile> CaptureResultsAreaToPngAsync(
            string suggestedTitle)
        {
            var renderTarget =
                new RenderTargetBitmap();

            await renderTarget.RenderAsync(
                ResultsViewHost);

            var pixelWidth =
                renderTarget.PixelWidth;

            var pixelHeight =
                renderTarget.PixelHeight;

            if (pixelWidth <= 0 ||
                pixelHeight <= 0)
            {
                throw new InvalidOperationException(
                    "Windows no devolvió una imagen válida del área de resultados.");
            }

            var pixels =
                await renderTarget.GetPixelsAsync();

            var safeName =
                SanitizeCaptureFileName(
                    suggestedTitle);

            var fileName =
                $"{safeName}.png";

            var tempFolder =
                ApplicationData.Current.TemporaryFolder;

            var file =
                await tempFolder.CreateFileAsync(
                    fileName,
                    CreationCollisionOption.GenerateUniqueName);

            await using var fileStream =
                await file.OpenStreamForWriteAsync();

            fileStream.SetLength(0);

            var randomAccessStream =
                fileStream.AsRandomAccessStream();

            var encoder =
                await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId,
                    randomAccessStream);

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)pixelWidth,
                (uint)pixelHeight,
                96,
                96,
                pixels.ToArray());

            await encoder.FlushAsync();
            await randomAccessStream.FlushAsync();

            return file;
        }

        private static string SanitizeCaptureFileName(
            string value)
        {
            var clean =
                (value ?? string.Empty).Trim();

            foreach (var invalid in
                     Path.GetInvalidFileNameChars())
            {
                clean =
                    clean.Replace(
                        invalid,
                        ' ');
            }

            clean =
                Regex.Replace(
                    clean,
                    @"\s+",
                    " ")
                .Trim()
                .TrimEnd('.');

            if (string.IsNullOrWhiteSpace(clean))
                clean = "Resultados ANFETA";

            if (clean.Length > 90)
                clean = clean.Substring(0, 90).Trim();

            return clean;
        }

        #endregion

        private const string LS_NotionUploadRecentTags =
            "Notion.Upload.RecentTags";

        private static readonly string[] NotionUploadQuickTags =
        {
            "prtuzREVISION",
            "prtuzCOBRAR",
            "prtuzPAGAR",
            "bbilb"
        };

        private static readonly string[] NotionUploadQuickTags00 =
        {
            "00prtuzREVISION",
            "00prtuzCOBRAR",
            "00prtuzPAGAR",
            "00bbilb",
            "00"
        };

        private static readonly string[] NotionUploadPersonTags =
        {
            "jjohn",
            "kkarl",
            "iisaia",
            "eedua",
            "aacal",
            "aandr",
            "eemma",
            "bbria",
            "ggena",
            "nneft"
        };

        private const string LS_CurrentUserTag =
            "Messaging.CurrentUserTag";

        private static string GetNotionPersonDisplayName(string tag)
        {
            return (tag ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "jjohn" => "John",
                "kkarl" => "Karla",
                "iisaia" => "Isaias",
                "eedua" => "Sotelo",
                "aacal" => "Acalli",
                "aandr" => "Andrade",
                "eemma" => "Emmanuel",
                "bbria" => "Brian",
                "ggena" => "Genaro",
                "nneft" => "Neftali",
                _ => tag
            };
        }


        private static IReadOnlyList<string> LoadNotionUploadRecentTags()
        {
            var raw =
                ApplicationData.Current.LocalSettings.Values[
                    LS_NotionUploadRecentTags] as string;

            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            return raw.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }

        private static void SaveNotionUploadRecentTags(
            IEnumerable<string> tags)
        {
            var current = LoadNotionUploadRecentTags();

            var merged = (tags ?? Array.Empty<string>())
                .Concat(current)
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            ApplicationData.Current.LocalSettings.Values[
                LS_NotionUploadRecentTags] =
                string.Join("|", merged);
        }

        private enum NotionUploadLayout
        {
            SinglePage,
            SeparatePages,
            DropboxOnly
        }

        private sealed record NotionUploadOptions(
            NotionUploadLayout Layout,
            string PageTitle,
            IReadOnlyList<string> SeparatePageTitles,
            IReadOnlyList<StorageFile> Files);

        private async void CtxUploadNotionFile_Click(
            object sender,
            RoutedEventArgs e)
        {
            IReadOnlyList<StorageFile> pickedFiles;

            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads
                };

                picker.FileTypeFilter.Add("*");

                var hwnd =
                    WindowNative.GetWindowHandle(
                        App.MainWindowInstance);

                InitializeWithWindow.Initialize(
                    picker,
                    hwnd);

                pickedFiles =
                    await picker.PickMultipleFilesAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir el selector → {ex.Message}";
                return;
            }

            await UploadFilesToNotionRevisionsAsync(
                pickedFiles,
                "selector");
        }

        private void ResultsDropSurface_DragEnter(
            object sender,
            DragEventArgs e)
        {
            HandleNotionFileDragOver(e);
        }

        private void ResultsDropSurface_DragOver(
            object sender,
            DragEventArgs e)
        {
            HandleNotionFileDragOver(e);
        }

        private void ResultsDropSurface_DragLeave(
            object sender,
            DragEventArgs e)
        {
            HideNotionDropOverlay();
        }

        private async void ResultsDropSurface_Drop(
            object sender,
            DragEventArgs e)
        {
            e.AcceptedOperation =
                Windows.ApplicationModel.DataTransfer
                    .DataPackageOperation.Copy;
            e.Handled = true;

            HideNotionDropOverlay();

            try
            {
                if (!e.DataView.Contains(
                        Windows.ApplicationModel.DataTransfer
                            .StandardDataFormats.StorageItems))
                {
                    StatusText.Text =
                        "Estado: Arrastra archivos desde el Explorador de Windows.";
                    return;
                }

                var storageItems =
                    await e.DataView.GetStorageItemsAsync();

                var files = storageItems
                    .OfType<StorageFile>()
                    .ToList();

                if (files.Count == 0)
                {
                    StatusText.Text =
                        "Estado: No se detectaron archivos para subir.";
                    return;
                }

                await UploadFilesToNotionRevisionsAsync(
                    files,
                    "arrastrar y soltar");
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudieron leer los archivos arrastrados → {ex.Message}";
            }
        }

        private void HandleNotionFileDragOver(
            DragEventArgs e)
        {
            var hasStorageItems =
                e.DataView.Contains(
                    Windows.ApplicationModel.DataTransfer
                        .StandardDataFormats.StorageItems);

            e.AcceptedOperation = hasStorageItems
                ? Windows.ApplicationModel.DataTransfer
                    .DataPackageOperation.Copy
                : Windows.ApplicationModel.DataTransfer
                    .DataPackageOperation.None;

            if (!hasStorageItems)
            {
                HideNotionDropOverlay();
                return;
            }

            _isNotionFileDragActive = true;

            var dropOverlay =
                FindName("NotionDropOverlay") as FrameworkElement;

            if (dropOverlay != null)
                dropOverlay.Visibility =
                    Visibility.Visible;

            e.DragUIOverride.Caption =
                "Subir a Notion → Revisiones";

            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.Handled = true;
        }

        private void HideNotionDropOverlay()
        {
            _isNotionFileDragActive = false;

            var dropOverlay =
                FindName("NotionDropOverlay") as FrameworkElement;

            if (dropOverlay != null)
                dropOverlay.Visibility =
                    Visibility.Collapsed;
        }

        private async Task UploadFilesToNotionRevisionsAsync(
            IReadOnlyList<StorageFile> files,
            string source,
            string? suggestedTitleOverride = null)
        {
            const string notionTokenKey = "Notion.Token";

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    notionTokenKey] as string;

            var validFiles = (files ??
                    Array.Empty<StorageFile>())
                .Where(file =>
                    file != null &&
                    !string.IsNullOrWhiteSpace(file.Path) &&
                    File.Exists(file.Path))
                .ToList();

            if (validFiles.Count == 0)
            {
                StatusText.Text =
                    "Estado: Ningún archivo tiene una ruta local válida.";
                return;
            }

            // Conserva exactamente la búsqueda actual como título sugerido,
            // incluyendo comillas y caracteres especiales. Esto es importante
            // para los pantallazos de búsquedas exactas.
            var currentSearchTitle =
                (SearchBox?.Text ?? string.Empty).Trim();

            var suggestedTitle =
                suggestedTitleOverride != null
                    ? suggestedTitleOverride
                    : !string.IsNullOrWhiteSpace(currentSearchTitle)
                        ? currentSearchTitle
                        : validFiles.Count == 1
                            ? Path.GetFileNameWithoutExtension(
                                validFiles[0].Name)
                            : $"Archivos {DateTime.Now:yyyy-MM-dd HH-mm}";

            var options =
                await PromptNotionRevisionUploadOptionsAsync(
                    validFiles,
                    suggestedTitle);

            if (options == null)
                return;

            if (options.Layout == NotionUploadLayout.DropboxOnly)
            {
                await ChooseDropboxUploadDestinationAsync(options.Files);
                return;
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text = "Estado: Configura el token de Notion para este destino, o elige Solo Dropbox.";
                return;
            }

            validFiles = options.Files
                .Where(file =>
                    file != null &&
                    !string.IsNullOrWhiteSpace(file.Path) &&
                    File.Exists(file.Path))
                .DistinctBy(
                    file => file.Path,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validFiles.Count == 0)
            {
                StatusText.Text =
                    "Estado: No quedaron archivos válidos para subir.";
                return;
            }

            try
            {
                ShowLoadingState(
                    $"Estado: Preparando {validFiles.Count} archivo(s) para Notion...",
                    $"Origen: {source} · Destino: Notion → Revisiones");

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(20));

                var service =
                    new NotionFilePageService();

                if (options.Layout == NotionUploadLayout.SinglePage)
                {
                    var progress =
                        new Progress<NotionFileUploadProgress>(
                            uploadProgress =>
                            {
                                UpdateLoadingState(
                                    $"Estado: Subiendo {uploadProgress.Completed} de {uploadProgress.Total} → {uploadProgress.FileName}",
                                    "Creando una sola página en Revisiones.");
                            });

                    var created =
                        await service.CreateRevisionFromFilesAsync(
                            token,
                            validFiles
                                .Select(file => file.Path)
                                .ToList(),
                            options.PageTitle,
                            progress,
                            cts.Token);

                    await AddCreatedNotionPageToIndexAsync(
                        created.PageId,
                        created.PageUrl,
                        created.Title);

                    StatusText.Text =
                        $"Estado: Página creada en Revisiones ✅ " +
                        $"({created.Title}) · {validFiles.Count} archivo(s)";
                    return;
                }

                var createdCount = 0;
                var failedCount = 0;
                string? lastError = null;

                for (var index = 0; index < validFiles.Count; index++)
                {
                    var file = validFiles[index];
                    var pageTitle =
                        options.SeparatePageTitles.Count > index
                            ? options.SeparatePageTitles[index]
                            : Path.GetFileNameWithoutExtension(
                                file.Name);

                    try
                    {
                        UpdateLoadingState(
                            $"Estado: Creando página {index + 1} de {validFiles.Count} → {file.Name}",
                            "Cada archivo tendrá su propia página en Revisiones.");

                        var created =
                            await service.CreateRevisionFromFileAsync(
                                token,
                                file.Path,
                                pageTitle,
                                cts.Token);

                        await AddCreatedNotionPageToIndexAsync(
                            created.PageId,
                            created.PageUrl,
                            created.Title);

                        createdCount++;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        lastError = $"{file.Name}: {ex.Message}";
                    }
                }

                StatusText.Text = failedCount == 0
                    ? $"Estado: Páginas creadas en Revisiones ✅ ({createdCount})"
                    : $"Estado: Carga parcial ⚠️ Creadas: {createdCount} · Fallaron: {failedCount}" +
                      (string.IsNullOrWhiteSpace(lastError)
                          ? string.Empty
                          : $" · Último: {lastError}");
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: La carga a Notion tardó demasiado o fue cancelada.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: Error creando página en Notion → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }


        private sealed record NaturalReminderCommand(
            DateTime ReminderAt,
            string CleanTitle,
            string CommandText);

        private static bool TryParseNaturalReminderCommand(
            string rawTitle,
            DateTime now,
            out NaturalReminderCommand command)
        {
            command = new NaturalReminderCommand(
                now,
                (rawTitle ?? string.Empty).Trim(),
                string.Empty);

            var title =
                Regex.Replace(
                    (rawTitle ?? string.Empty).Trim(),
                    @"\s+",
                    " ");

            if (string.IsNullOrWhiteSpace(title))
                return false;

            DateTime reminderAt;
            string cleanTitle;
            string commandText;

            var relative =
                Regex.Match(
                    title,
                    @"^(?<value>\d{1,3})\s*(?<unit>m|min|minuto|minutos|h|hr|hora|horas)\b[\s:,-]*(?<title>.*)$",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            if (relative.Success &&
                int.TryParse(
                    relative.Groups["value"].Value,
                    out var amount) &&
                amount > 0)
            {
                var unit =
                    relative.Groups["unit"].Value
                        .Trim()
                        .ToLowerInvariant();

                reminderAt =
                    unit.StartsWith("h", StringComparison.Ordinal)
                        ? now.AddHours(amount)
                        : now.AddMinutes(amount);

                cleanTitle =
                    relative.Groups["title"].Value.Trim();

                commandText =
                    relative.Value
                        .Substring(
                            0,
                            relative.Value.Length -
                            relative.Groups["title"].Value.Length)
                        .Trim(' ', ':', ',', '-');

                command = new NaturalReminderCommand(
                    reminderAt,
                    string.IsNullOrWhiteSpace(cleanTitle)
                        ? title
                        : cleanTitle,
                    commandText);

                return true;
            }

            var tomorrow =
                Regex.Match(
                    title,
                    @"^mañana(?:\s+(?<time>(?:\d{1,2}(?::\d{2})?\s*(?:am|pm)?)))?[\s:,-]*(?<title>.*)$",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            if (tomorrow.Success)
            {
                var targetDate =
                    now.Date.AddDays(1);

                var timeText =
                    tomorrow.Groups["time"].Value.Trim();

                var time =
                    new TimeSpan(9, 0, 0);

                if (!string.IsNullOrWhiteSpace(timeText) &&
                    TryParseReminderClockTime(
                        timeText,
                        out var parsedTime))
                {
                    time = parsedTime;
                }

                reminderAt =
                    targetDate.Add(time);

                cleanTitle =
                    tomorrow.Groups["title"].Value.Trim();

                commandText =
                    string.IsNullOrWhiteSpace(timeText)
                        ? "mañana"
                        : $"mañana {timeText}";

                command = new NaturalReminderCommand(
                    reminderAt,
                    string.IsNullOrWhiteSpace(cleanTitle)
                        ? title
                        : cleanTitle,
                    commandText);

                return true;
            }

            var clock =
                Regex.Match(
                    title,
                    @"^(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm))[\s:,-]*(?<title>.*)$",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            if (!clock.Success)
            {
                clock =
                    Regex.Match(
                        title,
                        @"^(?<time>(?:[01]?\d|2[0-3]):[0-5]\d)[\s:,-]*(?<title>.*)$",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);
            }

            if (clock.Success &&
                TryParseReminderClockTime(
                    clock.Groups["time"].Value,
                    out var clockTime))
            {
                reminderAt =
                    now.Date.Add(clockTime);

                if (reminderAt <= now)
                    reminderAt = reminderAt.AddDays(1);

                cleanTitle =
                    clock.Groups["title"].Value.Trim();

                commandText =
                    clock.Groups["time"].Value.Trim();

                command = new NaturalReminderCommand(
                    reminderAt,
                    string.IsNullOrWhiteSpace(cleanTitle)
                        ? title
                        : cleanTitle,
                    commandText);

                return true;
            }

            return false;
        }

        private static bool TryParseReminderClockTime(
            string value,
            out TimeSpan time)
        {
            time = default;

            var clean =
                Regex.Replace(
                    (value ?? string.Empty).Trim().ToLowerInvariant(),
                    @"\s+",
                    " ");

            var formats = new[]
            {
                "h tt",
                "h:mm tt",
                "hh tt",
                "hh:mm tt",
                "H:mm",
                "HH:mm"
            };

            if (!DateTime.TryParseExact(
                    clean,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsed))
            {
                return false;
            }

            time = parsed.TimeOfDay;
            return true;
        }

        private async Task<NotionUploadOptions?> PromptNotionRevisionUploadOptionsAsync(
            IReadOnlyList<StorageFile> files,
            string suggestedTitle)
        {
            var selectedFiles =
                new ObservableCollection<StorageFile>(
                    (files ?? Array.Empty<StorageFile>())
                    .Where(file => file != null)
                    .DistinctBy(
                        file => file.Path,
                        StringComparer.OrdinalIgnoreCase));

            var originalFilePaths =
                new HashSet<string>(
                    selectedFiles.Select(file => file.Path),
                    StringComparer.OrdinalIgnoreCase);

            var addedAttachmentPaths =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            var titleBox = new TextBox
            {
                HorizontalAlignment =
                    HorizontalAlignment.Stretch,
                Text = suggestedTitle,
                PlaceholderText =
                    "dominio.com → sseo aapli aads wwebs → jjuli → Título Descripción Detalles"
            };

            var titleSuggestionsLabel = new TextBlock
            {
                Text = "Completa la estructura del título",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.82,
                Visibility = Visibility.Collapsed
            };

            var titleSuggestionsPanel = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 3,
                ItemWidth = 205,
                ItemHeight = 42,
                Visibility = Visibility.Collapsed
            };

            string BuildVisualNotionTitleSuggestion(
                string suggestion)
            {
                var clean = Regex.Replace(
                    (suggestion ?? string.Empty).Trim(),
                    @"\s+",
                    " ");

                var tokens = clean.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

                if (tokens.Length < 2)
                    return clean;

                var projectTypes = new[]
                {
                    "sseo", "aapli", "aads", "wwebs"
                };

                var months = new[]
                {
                    "jjane", "ffebr", "mmarz", "aabri",
                    "mmayo", "jjuni", "jjuli", "aagos",
                    "ssept", "ooctu", "nnovi", "ddici"
                };

                var projectIndex = Array.FindIndex(
                    tokens,
                    token => projectTypes.Contains(
                        token,
                        StringComparer.OrdinalIgnoreCase));

                if (projectIndex < 0)
                    return clean;

                var monthIndex = Array.FindIndex(
                    tokens,
                    projectIndex + 1,
                    token => months.Contains(
                        token,
                        StringComparer.OrdinalIgnoreCase));

                var domain = string.Join(
                    " ",
                    tokens.Take(projectIndex));

                var project = tokens[projectIndex];

                if (monthIndex < 0)
                    return $"{domain}  ›  {project}";

                var month = tokens[monthIndex];

                var detail = string.Join(
                    " ",
                    tokens.Skip(monthIndex + 1));

                return string.IsNullOrWhiteSpace(detail)
                    ? $"{domain}  ›  {project}  ›  {month}"
                    : $"{domain}  ›  {project}  ›  {month}  ›  {detail}";
            }

            void RefreshTitleSuggestions()
            {
                var suggestions =
                    BuildStructuredNotionTitleSuggestions(
                        titleBox.Text,
                        max: 12);

                titleSuggestionsPanel.Children.Clear();

                foreach (var suggestion in suggestions)
                {
                    var suggestionButton = new Button
                    {
                        Content = $"◇  {BuildVisualNotionTitleSuggestion(suggestion)}",
                        Tag = suggestion,
                        Width = 198,
                        Height = 36,
                        Margin = new Thickness(0, 0, 7, 7),
                        Padding = new Thickness(10, 5, 10, 5),
                        HorizontalContentAlignment =
                            HorizontalAlignment.Left,
                        Background = new SolidColorBrush(
                            Windows.UI.Color.FromArgb(255, 31, 55, 79)),
                        BorderBrush = new SolidColorBrush(
                            Windows.UI.Color.FromArgb(255, 62, 101, 139)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8)
                    };

                    ToolTipService.SetToolTip(
                        suggestionButton,
                        suggestion);

                    suggestionButton.Click += (_, __) =>
                    {
                        titleBox.Text = suggestion;
                        titleBox.SelectionStart = titleBox.Text.Length;
                        titleBox.Focus(FocusState.Programmatic);
                        RefreshTitleSuggestions();
                    };

                    titleSuggestionsPanel.Children.Add(
                        suggestionButton);
                }

                var visible = suggestions.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                titleSuggestionsLabel.Visibility = visible;
                titleSuggestionsPanel.Visibility = visible;
            }

            titleBox.TextChanged += (_, __) =>
                RefreshTitleSuggestions();

            var onePageOption = new RadioButton
            {
                Content =
                    "Todos los archivos en una sola página",
                GroupName = "NotionUploadLayout",
                IsChecked = true
            };

            var separatePagesOption = new RadioButton
            {
                Content =
                    "Crear una página separada por cada archivo",
                GroupName = "NotionUploadLayout",
                IsEnabled = files.Count > 1
            };

            var titleEditors =
                new List<TextBox>(files.Count);

            var separateTitlesPanel = new StackPanel
            {
                Spacing = 8,
                Visibility = Visibility.Collapsed
            };

            separateTitlesPanel.Children.Add(
                new TextBlock
                {
                    Text =
                        "Título de cada página:",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            separateTitlesPanel.Children.Add(
                new TextBlock
                {
                    Text =
                        "Puedes conservar el nombre del archivo o editarlo antes de subir.",
                    TextWrapping =
                        TextWrapping.Wrap,
                    Opacity = 0.72
                });

            var editorsStack = new StackPanel
            {
                Spacing = 8
            };

            for (var index = 0;
                 index < files.Count;
                 index++)
            {
                var file = files[index];

                var editor = new TextBox
                {
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    Text =
                        Path.GetFileNameWithoutExtension(
                            file.Name),
                    PlaceholderText =
                        $"Título para {file.Name}",
                    Tag = index
                };

                titleEditors.Add(editor);

                var row = new Grid
                {
                    ColumnSpacing = 10
                };

                row.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(180)
                    });

                row.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(
                            1,
                            GridUnitType.Star)
                    });

                var fileNameText =
                    new TextBlock
                    {
                        Text = file.Name,
                        VerticalAlignment =
                            VerticalAlignment.Center,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis
                    };

                ToolTipService.SetToolTip(
                    fileNameText,
                    file.Name);

                Grid.SetColumn(fileNameText, 0);
                row.Children.Add(fileNameText);

                Grid.SetColumn(editor, 1);
                row.Children.Add(editor);

                editorsStack.Children.Add(row);
            }

            var restoreNamesButton = new Button
            {
                Content =
                    "Restaurar nombres de archivo",
                HorizontalAlignment =
                    HorizontalAlignment.Left
            };

            restoreNamesButton.Click += (_, __) =>
            {
                for (var index = 0;
                     index < titleEditors.Count;
                     index++)
                {
                    titleEditors[index].Text =
                        Path.GetFileNameWithoutExtension(
                            files[index].Name);
                }
            };

            separateTitlesPanel.Children.Add(
                restoreNamesButton);

            separateTitlesPanel.Children.Add(
                new ScrollViewer
                {
                    Content = editorsStack,
                    MaxHeight = 260,
                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto
                });

            var filesCountText = new TextBlock
            {
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold
            };

            var fileListPanel = new StackPanel
            {
                Spacing = 5
            };

            var attachmentStatusText = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            };

            var selectAttachmentsButton = new Button
            {
                Content = "＋ Seleccionar imágenes o archivos",
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var attachmentDropContent = new StackPanel
            {
                Spacing = 5,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            attachmentDropContent.Children.Add(
                new TextBlock
                {
                    Text = "📎 Arrastra aquí imágenes o archivos adicionales",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment =
                        HorizontalAlignment.Center
                });

            attachmentDropContent.Children.Add(
                new TextBlock
                {
                    Text =
                        "Se agregarán como adjuntos dentro de la misma página de Notion.",
                    FontSize = 11,
                    Opacity = 0.70,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment =
                        HorizontalAlignment.Center
                });

            attachmentDropContent.Children.Add(
                selectAttachmentsButton);

            var attachmentDropZone = new Border
            {
                AllowDrop = true,
                MinHeight = 92,
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(150, 96, 165, 250)),
                Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(30, 96, 165, 250)),
                Child = attachmentDropContent
            };

            Action refreshDialogState = () => { };

            void RefreshSelectedFilesUi()
            {
                fileListPanel.Children.Clear();

                foreach (var file in selectedFiles)
                {
                    var isAdditional =
                        addedAttachmentPaths.Contains(file.Path);

                    var row = new Grid
                    {
                        ColumnSpacing = 8
                    };

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width = new GridLength(
                                1,
                                GridUnitType.Star)
                        });

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width = GridLength.Auto
                        });

                    var fileText = new TextBlock
                    {
                        Text = isAdditional
                            ? $"📎 {file.Name} · Adjunto"
                            : $"• {file.Name}",
                        TextTrimming =
                            TextTrimming.CharacterEllipsis,
                        VerticalAlignment =
                            VerticalAlignment.Center,
                        Opacity = isAdditional
                            ? 1.0
                            : 0.82
                    };

                    ToolTipService.SetToolTip(
                        fileText,
                        file.Path);

                    Grid.SetColumn(fileText, 0);
                    row.Children.Add(fileText);

                    if (isAdditional)
                    {
                        var removeButton = new Button
                        {
                            Content = "Quitar",
                            Tag = file,
                            Padding = new Thickness(8, 3, 8, 3)
                        };

                        removeButton.Click += (_, __) =>
                        {
                            if (removeButton.Tag is not StorageFile selected)
                                return;

                            selectedFiles.Remove(selected);
                            addedAttachmentPaths.Remove(selected.Path);
                            RefreshSelectedFilesUi();
                        };

                        Grid.SetColumn(removeButton, 1);
                        row.Children.Add(removeButton);
                    }

                    fileListPanel.Children.Add(row);
                }

                filesCountText.Text =
                    $"Archivos seleccionados: {selectedFiles.Count}";

                if (addedAttachmentPaths.Count > 0)
                {
                    onePageOption.IsChecked = true;
                    separatePagesOption.IsEnabled = false;

                    attachmentStatusText.Text =
                        $"{addedAttachmentPaths.Count} adjunto(s) adicional(es). " +
                        "Se subirán junto con el archivo principal en una sola página.";
                }
                else
                {
                    separatePagesOption.IsEnabled =
                        files.Count > 1;

                    attachmentStatusText.Text =
                        "Puedes arrastrar o seleccionar más imágenes y archivos.";
                }

                refreshDialogState();
            }

            void AddAdditionalFiles(
                IEnumerable<StorageFile> additionalFiles)
            {
                var added = 0;

                foreach (var file in
                         additionalFiles ??
                         Array.Empty<StorageFile>())
                {
                    if (file == null ||
                        string.IsNullOrWhiteSpace(file.Path) ||
                        !File.Exists(file.Path) ||
                        selectedFiles.Any(existing =>
                            string.Equals(
                                existing.Path,
                                file.Path,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    selectedFiles.Add(file);
                    addedAttachmentPaths.Add(file.Path);
                    added++;
                }

                attachmentStatusText.Text =
                    added > 0
                        ? $"Se agregaron {added} archivo(s) adicional(es) ✅"
                        : "No se agregaron archivos nuevos.";

                RefreshSelectedFilesUi();
            }

            selectAttachmentsButton.Click +=
                async (_, __) =>
                {
                    try
                    {
                        var picker = new FileOpenPicker
                        {
                            SuggestedStartLocation =
                                PickerLocationId.PicturesLibrary
                        };

                        picker.FileTypeFilter.Add("*");

                        var hwnd =
                            WindowNative.GetWindowHandle(
                                App.MainWindowInstance);

                        InitializeWithWindow.Initialize(
                            picker,
                            hwnd);

                        var picked =
                            await picker.PickMultipleFilesAsync();

                        AddAdditionalFiles(picked);
                    }
                    catch (Exception ex)
                    {
                        attachmentStatusText.Text =
                            $"No se pudieron seleccionar archivos → {ex.Message}";
                    }
                };

            attachmentDropZone.DragOver +=
                (_, args) =>
                {
                    var hasFiles =
                        args.DataView.Contains(
                            Windows.ApplicationModel.DataTransfer
                                .StandardDataFormats.StorageItems);

                    args.AcceptedOperation = hasFiles
                        ? Windows.ApplicationModel.DataTransfer
                            .DataPackageOperation.Copy
                        : Windows.ApplicationModel.DataTransfer
                            .DataPackageOperation.None;

                    args.DragUIOverride.Caption =
                        "Agregar como adjunto a la página";
                    args.DragUIOverride.IsCaptionVisible = true;
                    args.Handled = true;
                };

            attachmentDropZone.Drop +=
                async (_, args) =>
                {
                    try
                    {
                        if (!args.DataView.Contains(
                                Windows.ApplicationModel.DataTransfer
                                    .StandardDataFormats.StorageItems))
                        {
                            return;
                        }

                        var items =
                            await args.DataView.GetStorageItemsAsync();

                        AddAdditionalFiles(
                            items.OfType<StorageFile>());
                    }
                    catch (Exception ex)
                    {
                        attachmentStatusText.Text =
                            $"No se pudieron agregar los archivos → {ex.Message}";
                    }
                };

            var titleSection = new StackPanel
            {
                Spacing = 8
            };

            var titleGuideCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 0, 168, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 0, 168, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9)
            };

            var titleGuideStack = new StackPanel { Spacing = 3 };
            titleGuideStack.Children.Add(new TextBlock
            {
                Text = "💡 Convención recomendada de título:",
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 130, 215, 255))
            });
            titleGuideStack.Children.Add(new TextBlock
            {
                Text = "[dominio.com] → [Tipo: sseo | aapli | aads | wwebs] → [Persona/Mes: jjuli | jjohn] → [Descripción]",
                FontSize = 10.5,
                Opacity = 0.88,
                TextWrapping = TextWrapping.Wrap
            });
            titleGuideCard.Child = titleGuideStack;

            titleSection.Children.Add(new TextBlock
            {
                Text = "Título de la página:",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12.5
            });
            titleSection.Children.Add(titleGuideCard);
            titleSection.Children.Add(titleBox);
            titleSection.Children.Add(titleSuggestionsLabel);
            titleSection.Children.Add(titleSuggestionsPanel);

            var selectedUploadTags =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            var variant00Check = new CheckBox
            {
                Content = "Variante 00 (agrega sufijo '00' al final del tag, ej: prtuzREVISION00)",
                IsChecked = false,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 4)
            };

            void AppendTagToTextBox(
                TextBox editor,
                string tag)
            {
                var cleanTag = (tag ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(cleanTag))
                    return;

                if (variant00Check.IsChecked == true && !cleanTag.EndsWith("00", StringComparison.OrdinalIgnoreCase))
                {
                    cleanTag += "00";
                }

                var current = (editor.Text ?? string.Empty).Trim();
                var tokens = current.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

                if (tokens.Any(x => string.Equals(
                        x,
                        cleanTag,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                editor.Text = string.IsNullOrWhiteSpace(current)
                    ? cleanTag
                    : $"{cleanTag} {current}";

                editor.SelectionStart = editor.Text.Length;
                selectedUploadTags.Add(cleanTag);
            }

            void AppendTagToActiveTitles(string tag)
            {
                if (separatePagesOption.IsChecked == true)
                {
                    foreach (var editor in titleEditors)
                        AppendTagToTextBox(editor, tag);
                }
                else
                {
                    AppendTagToTextBox(titleBox, tag);
                }
            }

            void Toggle00InEditor(TextBox editor, bool is00)
            {
                var text = (editor.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text)) return;

                var allTags = NotionUploadQuickTags.Concat(NotionUploadPersonTags).ToArray();
                var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                bool modified = false;

                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];
                    foreach (var baseTag in allTags)
                    {
                        if (is00 && string.Equals(token, baseTag, StringComparison.OrdinalIgnoreCase))
                        {
                            tokens[i] = baseTag + "00";
                            modified = true;
                            break;
                        }
                        else if (!is00 && string.Equals(token, baseTag + "00", StringComparison.OrdinalIgnoreCase))
                        {
                            tokens[i] = baseTag;
                            modified = true;
                            break;
                        }
                    }
                }

                if (modified)
                {
                    editor.Text = string.Join(" ", tokens);
                    editor.SelectionStart = editor.Text.Length;
                }
            }

            variant00Check.Checked += (_, __) =>
            {
                if (separatePagesOption.IsChecked == true)
                {
                    foreach (var ed in titleEditors) Toggle00InEditor(ed, true);
                }
                else
                {
                    Toggle00InEditor(titleBox, true);
                }
            };

            variant00Check.Unchecked += (_, __) =>
            {
                if (separatePagesOption.IsChecked == true)
                {
                    foreach (var ed in titleEditors) Toggle00InEditor(ed, false);
                }
                else
                {
                    Toggle00InEditor(titleBox, false);
                }
            };

            var quickTagsPanel = new StackPanel
            {
                Spacing = 9
            };

            quickTagsPanel.Children.Add(
                new TextBlock
                {
                    Text = "🏷️ Etiquetas y Estados (Tags):",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 12.5
                });

            quickTagsPanel.Children.Add(
                new TextBlock
                {
                    Text = "Haz clic en un tag para insertarlo al inicio del título:",
                    FontSize = 10.5,
                    Opacity = 0.72
                });

            // Checkbox Variante 00
            quickTagsPanel.Children.Add(variant00Check);

            // Tags principales
            var standardTagsHeader = new TextBlock
            {
                Text = "Tags principales:",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.85
            };
            quickTagsPanel.Children.Add(standardTagsHeader);

            var quickTagButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6
            };

            foreach (var tag in NotionUploadQuickTags)
            {
                var button = new Button
                {
                    Content = tag,
                    Padding = new Thickness(9, 4, 9, 4),
                    Tag = tag,
                    CornerRadius = new CornerRadius(6)
                };

                button.Click += (_, __) =>
                    AppendTagToActiveTitles(tag);

                quickTagButtons.Children.Add(button);
            }

            quickTagsPanel.Children.Add(quickTagButtons);

            // Personas
            var personTagCombo = new ComboBox
            {
                PlaceholderText = "TAGS de persona (ej. jjohn, nneft...)",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var tag in NotionUploadPersonTags)
            {
                personTagCombo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = $"{GetNotionPersonDisplayName(tag)} ({tag})",
                        Tag = tag
                    });
            }

            personTagCombo.SelectionChanged += (_, __) =>
            {
                if (personTagCombo.SelectedItem is not ComboBoxItem item)
                    return;

                var tag = item.Tag?.ToString() ?? string.Empty;
                AppendTagToActiveTitles(tag);
                personTagCombo.SelectedItem = null;
            };

            quickTagsPanel.Children.Add(personTagCombo);

            var reminderCheck = new CheckBox
            {
                Content = "Programar como recordatorio / mensaje",
                IsChecked = false
            };

            var reminderRecipientCombo = new ComboBox
            {
                PlaceholderText = "Selecciona destinatario",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = false
            };

            foreach (var tag in NotionUploadPersonTags)
            {
                reminderRecipientCombo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = GetNotionPersonDisplayName(tag),
                        Tag = tag
                    });
            }

            var savedCurrentUserTag =
                (ApplicationData.Current.LocalSettings.Values[
                    LS_CurrentUserTag] as string ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(savedCurrentUserTag))
            {
                reminderRecipientCombo.SelectedItem =
                    reminderRecipientCombo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item =>
                            string.Equals(
                                item.Tag?.ToString(),
                                savedCurrentUserTag,
                                StringComparison.OrdinalIgnoreCase));
            }

            var reminderDelayCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = false,
                SelectedIndex = 0
            };

            reminderDelayCombo.Items.Add(
                new ComboBoxItem { Content = "En 5 minutos", Tag = "5" });
            reminderDelayCombo.Items.Add(
                new ComboBoxItem { Content = "En 10 minutos", Tag = "10" });
            reminderDelayCombo.Items.Add(
                new ComboBoxItem { Content = "En 15 minutos", Tag = "15" });
            reminderDelayCombo.Items.Add(
                new ComboBoxItem { Content = "En 30 minutos", Tag = "30" });
            reminderDelayCombo.Items.Add(
                new ComboBoxItem { Content = "En 1 hora", Tag = "60" });
            reminderDelayCombo.Items.Add(
                new ComboBoxItem { Content = "Personalizado…", Tag = "custom" });

            var customReminderValueBox = new NumberBox
            {
                Header = "Cantidad",
                Minimum = 1,
                Maximum = 999,
                Value = 1,
                SpinButtonPlacementMode =
                    NumberBoxSpinButtonPlacementMode.Compact,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var customReminderUnitCombo = new ComboBox
            {
                Header = "Unidad",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0
            };

            customReminderUnitCombo.Items.Add(
                new ComboBoxItem { Content = "Minutos", Tag = "minutes" });
            customReminderUnitCombo.Items.Add(
                new ComboBoxItem { Content = "Horas", Tag = "hours" });
            customReminderUnitCombo.Items.Add(
                new ComboBoxItem { Content = "Días", Tag = "days" });

            var customReminderGrid = new Grid
            {
                ColumnSpacing = 10,
                Visibility = Visibility.Collapsed
            };

            customReminderGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            customReminderGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

            Grid.SetColumn(customReminderValueBox, 0);
            customReminderGrid.Children.Add(customReminderValueBox);

            Grid.SetColumn(customReminderUnitCombo, 1);
            customReminderGrid.Children.Add(customReminderUnitCombo);

            var reminderPanel = new StackPanel
            {
                Spacing = 7
            };

            var reminderPreviewText = new TextBlock
            {
                Text =
                    "También puedes iniciar el título con: 30 m, 1 h, mañana, mañana 10 am, 10 am o 15:30.",
                FontSize = 11,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            };

            reminderPanel.Children.Add(new TextBlock
            {
                Text = "⏰ Programación de Recordatorio (Opcional):",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12.5
            });
            reminderPanel.Children.Add(reminderCheck);
            reminderPanel.Children.Add(
                new TextBlock
                {
                    Text = "Destinatario:",
                    FontSize = 11,
                    Opacity = 0.72
                });
            reminderPanel.Children.Add(reminderRecipientCombo);
            reminderPanel.Children.Add(
                new TextBlock
                {
                    Text = "Mostrar recordatorio:",
                    FontSize = 11,
                    Opacity = 0.72
                });
            reminderPanel.Children.Add(reminderDelayCombo);
            reminderPanel.Children.Add(customReminderGrid);
            reminderPanel.Children.Add(reminderPreviewText);

            void RefreshNaturalReminderPreview()
            {
                if (TryParseNaturalReminderCommand(
                        titleBox.Text,
                        DateTime.Now,
                        out var parsed))
                {
                    reminderPreviewText.Text =
                        $"Comando detectado: “{parsed.CommandText}” → " +
                        $"{parsed.ReminderAt:dd/MM/yyyy HH:mm}\n" +
                        $"Título limpio: {parsed.CleanTitle}";

                    reminderPreviewText.Opacity = 1;
                }
                else
                {
                    reminderPreviewText.Text =
                        "También puedes iniciar el título con: 30 m, 1 h, mañana, mañana 10 am, 10 am o 15:30.";

                    reminderPreviewText.Opacity = 0.72;
                }
            }

            void RefreshCustomReminderVisibility()
            {
                var isCustom =
                    reminderDelayCombo.SelectedItem is ComboBoxItem selectedDelay &&
                    string.Equals(
                        selectedDelay.Tag?.ToString(),
                        "custom",
                        StringComparison.OrdinalIgnoreCase);

                customReminderGrid.Visibility =
                    reminderCheck.IsChecked == true && isCustom
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                customReminderValueBox.IsEnabled =
                    reminderCheck.IsChecked == true && isCustom;

                customReminderUnitCombo.IsEnabled =
                    reminderCheck.IsChecked == true && isCustom;
            }

            reminderCheck.Checked += (_, __) =>
            {
                reminderRecipientCombo.IsEnabled = true;
                reminderDelayCombo.IsEnabled = true;
                RefreshCustomReminderVisibility();
            };

            reminderCheck.Unchecked += (_, __) =>
            {
                reminderRecipientCombo.IsEnabled = false;
                reminderDelayCombo.IsEnabled = false;
                RefreshCustomReminderVisibility();
            };

            reminderDelayCombo.SelectionChanged += (_, __) =>
                RefreshCustomReminderVisibility();

            var recentTags = LoadNotionUploadRecentTags();
            if (recentTags.Count > 0)
            {
                quickTagsPanel.Children.Add(
                    new TextBlock
                    {
                        Text = "Usados recientemente:",
                        FontSize = 11,
                        Opacity = 0.70
                    });

                var recentPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6
                };

                foreach (var tag in recentTags.Take(5))
                {
                    var button = new Button
                    {
                        Content = tag,
                        Padding = new Thickness(8, 3, 8, 3),
                        CornerRadius = new CornerRadius(5)
                    };

                    button.Click += (_, __) =>
                        AppendTagToActiveTitles(tag);

                    recentPanel.Children.Add(button);
                }

                quickTagsPanel.Children.Add(recentPanel);
            }

            var content = new StackPanel
            {
                MaxWidth = 660,
                Spacing = 14
            };

            // Sección 1: Archivos
            var filesCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };
            var filesStack = new StackPanel { Spacing = 8 };
            filesStack.Children.Add(filesCountText);
            filesStack.Children.Add(
                new ScrollViewer
                {
                    Content = fileListPanel,
                    MaxHeight = 170,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto
                });
            filesStack.Children.Add(
                new TextBlock
                {
                    Text = "Adjuntos adicionales:",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });
            filesStack.Children.Add(attachmentDropZone);
            filesStack.Children.Add(attachmentStatusText);
            filesCard.Child = filesStack;
            content.Children.Add(filesCard);

            // Sección 2: Destino y Organización
            var orgCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };
            var orgStack = new StackPanel { Spacing = 8 };
            var dropboxOnlyOption = new RadioButton
            {
                Content = "Solo Dropbox · sin crear página en Notion",
                GroupName = "NotionUploadLayout"
            };
            orgStack.Children.Add(
                new TextBlock
                {
                    Text = "📋 Destino: Notion → Revisiones / Solo Dropbox",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 12.5
                });
            orgStack.Children.Add(
                new TextBlock
                {
                    Text = "¿Cómo deseas organizar los archivos?",
                    FontSize = 11,
                    Opacity = 0.75
                });
            orgStack.Children.Add(onePageOption);
            orgStack.Children.Add(separatePagesOption);
            orgStack.Children.Add(dropboxOnlyOption);
            orgCard.Child = orgStack;
            content.Children.Add(orgCard);

            // Sección 3: Título
            var titleCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };
            var titleCardStack = new StackPanel { Spacing = 8 };
            titleCardStack.Children.Add(titleSection);
            titleCardStack.Children.Add(separateTitlesPanel);
            titleCard.Child = titleCardStack;
            content.Children.Add(titleCard);

            // Sección 4: Tags
            var tagsCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };
            tagsCard.Child = quickTagsPanel;
            content.Children.Add(tagsCard);

            // Sección 5: Recordatorio
            var reminderCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };
            reminderCard.Child = reminderPanel;
            content.Children.Add(reminderCard);

            var contentScroll = new ScrollViewer
            {
                Content = content,
                MaxHeight = Math.Clamp(
                    ActualHeight - 190,
                    420,
                    680),
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled
            };

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = files.Count == 1
                    ? "Subir archivo"
                    : "Subir varios archivos",
                Content = contentScroll,
                PrimaryButtonText = "Continuar y subir",
                CloseButtonText = "Cancelar",
                DefaultButton =
                    ContentDialogButton.Primary,
                IsPrimaryButtonEnabled =
                    !string.IsNullOrWhiteSpace(
                        suggestedTitle),
                HorizontalContentAlignment =
                    HorizontalAlignment.Stretch
            };

            dialog.Resources[
                "ContentDialogMaxWidth"] = 760d;

            dialog.Resources[
                "ContentDialogMinWidth"] = Math.Min(420d, Math.Max(240d, (XamlRoot?.Size.Width ?? 760) - 80));

            refreshDialogState = () =>
            {
                var onlyDropbox = dropboxOnlyOption.IsChecked == true;
                titleCard.Visibility = tagsCard.Visibility = reminderCard.Visibility =
                    onlyDropbox ? Visibility.Collapsed : Visibility.Visible;
                var separate =
                    separatePagesOption.IsChecked == true;

                titleSection.Visibility = separate
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                separateTitlesPanel.Visibility = separate
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                var titlesValid = separate
                    ? titleEditors.All(editor =>
                        !string.IsNullOrWhiteSpace(
                            editor.Text))
                    : !string.IsNullOrWhiteSpace(
                        titleBox.Text);

                var customDelaySelected =
                    reminderDelayCombo.SelectedItem is ComboBoxItem selectedDelay &&
                    string.Equals(
                        selectedDelay.Tag?.ToString(),
                        "custom",
                        StringComparison.OrdinalIgnoreCase);

                var customDelayValid =
                    !customDelaySelected ||
                    (!double.IsNaN(customReminderValueBox.Value) &&
                     customReminderValueBox.Value >= 1 &&
                     customReminderUnitCombo.SelectedItem is ComboBoxItem);

                var reminderValid =
                    reminderCheck.IsChecked != true ||
                    (reminderRecipientCombo.SelectedItem is ComboBoxItem &&
                     reminderDelayCombo.SelectedItem is ComboBoxItem &&
                     customDelayValid);

                dialog.IsPrimaryButtonEnabled =
                    selectedFiles.Count > 0 && (onlyDropbox || (titlesValid && reminderValid));
            };

            titleBox.TextChanged +=
                (_, __) =>
                {
                    RefreshNaturalReminderPreview();
                    refreshDialogState();
                };

            foreach (var editor in titleEditors)
            {
                editor.TextChanged +=
                    (_, __) => refreshDialogState();
            }

            onePageOption.Checked +=
                (_, __) => refreshDialogState();
            dropboxOnlyOption.Checked += (_, __) => refreshDialogState();

            separatePagesOption.Checked +=
                (_, __) => refreshDialogState();

            reminderCheck.Checked +=
                (_, __) => refreshDialogState();
            reminderCheck.Unchecked +=
                (_, __) => refreshDialogState();
            reminderRecipientCombo.SelectionChanged +=
                (_, __) => refreshDialogState();
            reminderDelayCombo.SelectionChanged +=
                (_, __) => refreshDialogState();
            customReminderValueBox.ValueChanged +=
                (_, __) => refreshDialogState();
            customReminderUnitCombo.SelectionChanged +=
                (_, __) => refreshDialogState();

            dialog.Opened += (_, __) =>
            {
                RefreshSelectedFilesUi();
                RefreshNaturalReminderPreview();
                refreshDialogState();
                titleBox.Focus(
                    FocusState.Programmatic);

                var innerTitleTextBox =
                    FindVisualChild<TextBox>(titleBox);

                innerTitleTextBox?.SelectAll();
            };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return null;
            }

            var separateTitles = titleEditors
                .Select(editor =>
                    (editor.Text ??
                     string.Empty).Trim())
                .ToList();

            var singleTitle =
                (titleBox.Text ?? string.Empty).Trim();

            if (dropboxOnlyOption.IsChecked == true)
                return new NotionUploadOptions(NotionUploadLayout.DropboxOnly, "", Array.Empty<string>(), selectedFiles.ToList());

            if (reminderCheck.IsChecked == true &&
                reminderRecipientCombo.SelectedItem is ComboBoxItem recipientItem)
            {
                var recipientTag =
                    (recipientItem.Tag?.ToString() ?? string.Empty).Trim();

                double delayMinutes = 5;

                if (reminderDelayCombo.SelectedItem is ComboBoxItem delayItem)
                {
                    var delayTag =
                        delayItem.Tag?.ToString() ?? string.Empty;

                    if (string.Equals(
                            delayTag,
                            "custom",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var amount =
                            double.IsNaN(customReminderValueBox.Value)
                                ? 1
                                : Math.Max(1, customReminderValueBox.Value);

                        var unit =
                            customReminderUnitCombo.SelectedItem is ComboBoxItem unitItem
                                ? unitItem.Tag?.ToString() ?? "minutes"
                                : "minutes";

                        delayMinutes = unit switch
                        {
                            "hours" => amount * 60,
                            "days" => amount * 24 * 60,
                            _ => amount
                        };
                    }
                    else if (double.TryParse(
                                 delayTag,
                                 System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture,
                                 out var parsedDelay))
                    {
                        delayMinutes = parsedDelay;
                    }
                }

                string BuildReminderTitle(string originalTitle)
                {
                    var cleanTitle =
                        (originalTitle ?? string.Empty).Trim();

                    var reminderAt =
                        DateTime.Now.AddMinutes(delayMinutes);

                    if (TryParseNaturalReminderCommand(
                            cleanTitle,
                            DateTime.Now,
                            out var parsedCommand))
                    {
                        reminderAt =
                            parsedCommand.ReminderAt;

                        cleanTitle =
                            parsedCommand.CleanTitle;
                    }

                    var senderTag =
                        (ApplicationData.Current.LocalSettings.Values[
                            LS_CurrentUserTag] as string ?? string.Empty).Trim();

                    var senderToken =
                        string.IsNullOrWhiteSpace(senderTag)
                            ? string.Empty
                            : $" de:{senderTag}";

                    return
                        $"{reminderAt:yyyy-MM-dd HH:mm} {recipientTag}{senderToken} {cleanTitle}".Trim();
                }

                singleTitle =
                    BuildReminderTitle(singleTitle);

                separateTitles = separateTitles
                    .Select(BuildReminderTitle)
                    .ToList();

                selectedUploadTags.Add(recipientTag);
            }

            SaveNotionUploadRecentTags(
                selectedUploadTags);

            return new NotionUploadOptions(
                separatePagesOption.IsChecked == true
                    ? NotionUploadLayout.SeparatePages
                    : NotionUploadLayout.SinglePage,
                singleTitle,
                separateTitles,
                selectedFiles.ToList());
        }

        private async Task AddCreatedNotionPageToIndexAsync(
            string pageId,
            string pageUrl,
            string title)
        {
            var now = DateTime.Now;
            var row = new SearchResultRow
            {
                NodeId = pageId,
                ExternalId = pageId,
                ExternalUrl = pageUrl,
                ExternalSourceName = "Revisiones",
                Name = $"[Revisiones] {title}",
                Target = pageUrl,
                Type = "NOTION_PAGE",
                Size = 0,
                ServerModified = now.ToString("yyyy-MM-dd HH:mm"),
                Source = SearchSource.Notion,
                Description = string.Empty,
                SearchText = $"Revisiones {title}"
            };

            var snapshot = App.LocalIndex.GetAll();
            var existing = snapshot.FirstOrDefault(x =>
                x.Source == SearchSource.Notion &&
                string.Equals(
                    x.ExternalId,
                    pageId,
                    StringComparison.OrdinalIgnoreCase));

            if (existing == null)
                snapshot.Add(row);

            App.LocalIndex.Set(snapshot);

            var root = ApplicationData.Current.LocalSettings.Values[
                LS_DropboxRoot] as string;

            if (!string.IsNullOrWhiteSpace(root) &&
                Directory.Exists(root) &&
                snapshot.Count > 0)
            {
                await LocalIndexPersistence.SaveAsync(
                    root,
                    snapshot,
                    CancellationToken.None);
            }

            var query = (SearchBox.Text ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(query))
            {
                await RunSearchAsync(query);
            }
            else
            {
                Results.Insert(0, row);
                RefreshResultsListView();
            }
        }

        private enum DropboxDuplicateChoice
        {
            Cancel,
            Replace,
            AutoRename
        }

        private async void CtxUploadDropboxFile_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryResolveDropboxDestination(
                    sender,
                    out var destinationLocal,
                    out var destinationRemote,
                    out var error))
            {
                StatusText.Text = $"Estado: {error}";
                return;
            }

            IReadOnlyList<StorageFile> pickedFiles;

            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads
                };

                picker.FileTypeFilter.Add("*");

                var hwnd =
                    WindowNative.GetWindowHandle(App.MainWindowInstance);

                InitializeWithWindow.Initialize(picker, hwnd);

                pickedFiles = await picker.PickMultipleFilesAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir el selector → {ex.Message}";
                return;
            }

            if (pickedFiles == null || pickedFiles.Count == 0)
                return;

            var validFiles = pickedFiles
                .Where(x =>
                    x != null &&
                    !string.IsNullOrWhiteSpace(x.Path) &&
                    File.Exists(x.Path))
                .ToList();

            if (validFiles.Count == 0)
            {
                StatusText.Text =
                    "Estado: Ningún archivo seleccionado tiene una ruta local válida.";
                return;
            }

            await UploadSelectedFilesToDropboxAsync(validFiles, destinationLocal, destinationRemote);
        }

        private async Task ChooseDropboxUploadDestinationAsync(IReadOnlyList<StorageFile> files)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindowInstance));
                var folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                if (!_dropboxPathMapper.TryToDropboxPath(DROPBOX_ROOT, folder.Path, out var remote, out var error))
                {
                    StatusText.Text = $"Estado: {error}";
                    return;
                }
                await UploadSelectedFilesToDropboxAsync(files, folder.Path, remote);
            }
            catch (Exception ex) { StatusText.Text = $"Estado: No se pudo subir a Dropbox: {ex.Message}"; }
        }

        private async Task UploadSelectedFilesToDropboxAsync(IReadOnlyList<StorageFile> validFiles, string destinationLocal, string destinationRemote)
        {
            var uploadedCount = 0;
            var skippedCount = 0;
            var failedCount = 0;
            string? lastError = null;

            ShowLoadingState(
                $"Estado: Preparando {validFiles.Count} archivo(s) para Dropbox...",
                $"Destino: {destinationLocal}");

            try
            {
                for (var index = 0; index < validFiles.Count; index++)
                {
                    var pickedFile = validFiles[index];
                    var position = index + 1;
                    var originalName = pickedFile.Name;

                    var remoteFilePath =
                        _dropboxPathMapper.CombineDropboxPath(
                            destinationRemote,
                            originalName);

                    var overwrite = false;
                    var autorename = false;

                    try
                    {
                        UpdateLoadingState(
                            $"Estado: Revisando {position} de {validFiles.Count} → {originalName}",
                            "Comprobando si ya existe en la carpeta de Dropbox.");

                        using var checkCts =
                            new CancellationTokenSource(
                                TimeSpan.FromSeconds(30));

                        var exists =
                            await _dropboxFileService.ExistsAsync(
                                remoteFilePath,
                                checkCts.Token);

                        if (exists)
                        {
                            // Ocultamos temporalmente el overlay para que el diálogo
                            // de duplicado quede completamente accesible.
                            LoadingOverlay.Visibility = Visibility.Collapsed;

                            var choice =
                                await PromptDropboxDuplicateChoiceAsync(
                                    originalName);

                            LoadingOverlay.Visibility = Visibility.Visible;

                            if (choice == DropboxDuplicateChoice.Cancel)
                            {
                                skippedCount++;
                                continue;
                            }

                            overwrite =
                                choice == DropboxDuplicateChoice.Replace;

                            autorename =
                                choice == DropboxDuplicateChoice.AutoRename;
                        }

                        UpdateLoadingState(
                            $"Estado: Subiendo {position} de {validFiles.Count} → {originalName}",
                            $"Destino: {destinationLocal}");

                        using var uploadCts =
                            new CancellationTokenSource(
                                TimeSpan.FromMinutes(10));

                        var uploaded =
                            await _dropboxFileService.UploadFileAsync(
                                pickedFile.Path,
                                remoteFilePath,
                                overwrite,
                                autorename,
                                uploadCts.Token);

                        var expectedLocalPath = Path.Combine(
                            destinationLocal,
                            uploaded.Name);

                        _ = await WaitForLocalFileAsync(
                            expectedLocalPath,
                            TimeSpan.FromSeconds(25));

                        await AddUploadedFileToIndexAsync(
                            expectedLocalPath,
                            uploaded.Name,
                            uploaded.Size,
                            uploaded.ServerModifiedUtc);

                        uploadedCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        failedCount++;
                        lastError =
                            $"{originalName}: la operación tardó demasiado.";
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        lastError = $"{originalName}: {ex.Message}";
                    }
                }

                UpdateLoadingState(
                    "Estado: Actualizando resultados...",
                    "Refrescando la carpeta y el índice de ANFETA.");

                await RefreshDropboxFolderUiAsync(destinationLocal);

                StatusText.Text =
                    failedCount == 0
                        ? $"Estado: Dropbox actualizado ✅ " +
                          $"Subidos: {uploadedCount} · Omitidos: {skippedCount}"
                        : $"Estado: Dropbox actualizado parcialmente ⚠️ " +
                          $"Subidos: {uploadedCount} · Omitidos: {skippedCount} · " +
                          $"Fallaron: {failedCount}" +
                          (string.IsNullOrWhiteSpace(lastError)
                              ? string.Empty
                              : $" · Último: {lastError}");
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async Task<DropboxDuplicateChoice> PromptDropboxDuplicateChoiceAsync(
            string fileName)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "El archivo ya existe",
                Content =
                    $"Ya existe “{fileName}” en esta carpeta de Dropbox.\n\n" +
                    "Puedes reemplazarlo o subir una copia con un nombre automático.",
                PrimaryButtonText = "Reemplazar",
                SecondaryButtonText = "Renombrar automáticamente",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();

            return result switch
            {
                ContentDialogResult.Primary => DropboxDuplicateChoice.Replace,
                ContentDialogResult.Secondary => DropboxDuplicateChoice.AutoRename,
                _ => DropboxDuplicateChoice.Cancel
            };
        }

        private static async Task<bool> WaitForLocalFileAsync(
            string localPath,
            TimeSpan timeout)
        {
            var started = DateTime.UtcNow;

            while (DateTime.UtcNow - started < timeout)
            {
                if (File.Exists(localPath))
                    return true;

                await Task.Delay(500);
            }

            return File.Exists(localPath);
        }

        private async Task AddUploadedFileToIndexAsync(
            string localPath,
            string fileName,
            long size,
            DateTime serverModifiedUtc)
        {
            var snapshot = App.LocalIndex.GetAll();
            var existing = snapshot.FirstOrDefault(x =>
                x.Source != SearchSource.Notion &&
                string.Equals(x.Target, localPath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Name = fileName;
                existing.Size = size;
                existing.ServerModified = serverModifiedUtc
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm");
            }
            else
            {
                snapshot.Add(new SearchResultRow
                {
                    Name = fileName,
                    Target = localPath,
                    Type = "FILE",
                    Size = size,
                    ServerModified = serverModifiedUtc
                        .ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm"),
                    Source = SearchSource.Local
                });
            }

            App.LocalIndex.Set(snapshot);
            await LocalIndexPersistence.SaveAsync(
                DROPBOX_ROOT,
                snapshot,
                CancellationToken.None);
        }

        private async void CtxBookmark_Click(object sender, RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);

            if (row == null)
            {
                StatusText.Text = "Estado: Selecciona un elemento para agregar a Favoritos.";
                return;
            }

            await ToggleBookmarkAsync(row);
        }

        #endregion

        #region ===== File Ops (Rename / Delete / Move) =====

        private void CancelRefreshWork()
        {
            try { _refreshCts?.Cancel(); } catch { }
            _refreshCts?.Dispose();
            _refreshCts = new CancellationTokenSource();
        }

        private enum FileChangeKind { Rename, Delete }

        private async Task ApplyFileChangeAsync(FileChangeKind kind, SearchResultRow row, string? newFullPath = null)
        {
            if (row == null) return;

            await _mutLock.WaitAsync();
            try
            {
                CancelRefreshWork();

                var oldPath = row.Target;
                var isFolder = row.IsFolder;

                // 1) Disco
                if (kind == FileChangeKind.Delete)
                {
                    if (isFolder) Directory.Delete(oldPath, recursive: true);
                    else File.Delete(oldPath);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(newFullPath))
                        throw new ArgumentException("newFullPath requerido");
                    if (isFolder) Directory.Move(oldPath, newFullPath);
                    else File.Move(oldPath, newFullPath);
                }

                // 2) Índice en memoria
                if (kind == FileChangeKind.Delete)
                {
                    if (isFolder) App.LocalIndex.RemovePrefix(oldPath);
                    else App.LocalIndex.RemoveExact(oldPath);
                }
                else
                {
                    if (isFolder) App.LocalIndex.RenamePrefix(oldPath, newFullPath!);
                    else App.LocalIndex.RenameExact(oldPath, newFullPath!, isFolder: false);
                }

                // 3) Actualizar inmediatamente el objeto visible.
                // El índice puede contener otra instancia de SearchResultRow;
                // por eso la tarjeta/lista actual no siempre cambiaba hasta
                // ejecutar otra búsqueda manualmente.
                if (kind == FileChangeKind.Rename &&
                    !string.IsNullOrWhiteSpace(newFullPath))
                {
                    row.Target = newFullPath;
                    row.Name = Path.GetFileName(newFullPath);
                }

                // 4) Persistir
                var snapshot = App.LocalIndex.GetAll();
                if (snapshot.Count == 0)
                    throw new InvalidOperationException("Índice quedó vacío: no se persistirá.");

                await LocalIndexPersistence.SaveAsync(DROPBOX_ROOT, snapshot, CancellationToken.None);

                // 5) Refresh UI
                await RefreshAfterFileChangeAsync(kind, oldPath, newFullPath);
            }
            finally
            {
                _mutLock.Release();
            }
        }

        private async Task ApplyBatchDeleteAsync(List<SearchResultRow> rows)
        {
            if (rows == null || rows.Count == 0) return;

            await _mutLock.WaitAsync();
            try
            {
                CancelRefreshWork();

                foreach (var row in rows)
                {
                    if (row.IsFolder) Directory.Delete(row.Target, recursive: true);
                    else File.Delete(row.Target);
                }

                foreach (var row in rows)
                {
                    if (row.IsFolder) App.LocalIndex.RemovePrefix(row.Target);
                    else App.LocalIndex.RemoveExact(row.Target);
                }

                var snapshot = App.LocalIndex.GetAll();
                if (snapshot.Count == 0)
                    throw new InvalidOperationException("Índice quedó vacío: no se persistirá.");

                await LocalIndexPersistence.SaveAsync(DROPBOX_ROOT, snapshot, CancellationToken.None);
                await RefreshAfterFileChangeAsync(FileChangeKind.Delete, rows[0].Target, null);
            }
            finally
            {
                _mutLock.Release();
            }
        }

        private async Task RefreshAfterFileChangeAsync(FileChangeKind kind, string oldPath, string? newPath)
        {
            if (ResultsList.SelectedItem is SearchResultRow sel &&
                string.Equals(sel.Target, oldPath, StringComparison.OrdinalIgnoreCase))
                ResultsList.SelectedItem = null;

            if (kind == FileChangeKind.Rename && !string.IsNullOrWhiteSpace(newPath))
            {
                var current = _currentFolderPath;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    var oldN = NormalizePath(oldPath);
                    var newN = NormalizePath(newPath);
                    var curN = NormalizePath(current);

                    if (string.Equals(curN, oldN, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentFolderPath = newN;
                    }
                    else
                    {
                        var oldPrefix = EnsureDirPrefix(oldN);
                        if (curN.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var rest = curN.Substring(oldPrefix.Length);
                            _currentFolderPath = EnsureDirPrefix(newN) + rest;
                        }
                    }
                }
            }

            LoadFoldersRoot();
            BuildTreeRoot();

            var currentQuery =
                (SearchBox?.Text ?? string.Empty).Trim();

            // Conserva exactamente el modo en el que estaba el usuario.
            // Una búsqueda vacía también es una vista global válida de resultados,
            // así que no debe convertirse automáticamente en navegación por carpeta.
            if (!_isBrowsing)
            {
                await RunSearchAsync(currentQuery);
                return;
            }

            var targetFolder =
                !string.IsNullOrWhiteSpace(_currentFolderPath) &&
                Directory.Exists(_currentFolderPath)
                    ? _currentFolderPath
                    : !string.IsNullOrWhiteSpace(_currentFolder) &&
                      Directory.Exists(_currentFolder)
                        ? _currentFolder
                        : DROPBOX_ROOT;

            if (!string.IsNullOrWhiteSpace(targetFolder) &&
                Directory.Exists(targetFolder))
            {
                await BrowseFolderAsync(
                    targetFolder,
                    pushHistory: false);
            }
            else
            {
                _isBrowsing = false;
                await RunSearchAsync(currentQuery);
            }
        }

        private static string NormalizePath(string p)
            => (p ?? "").Trim().Replace('/', '\\');

        private static string EnsureDirPrefix(string folder)
        {
            var p = NormalizePath(folder);
            if (!p.EndsWith("\\", StringComparison.Ordinal)) p += "\\";
            return p;
        }

        private async Task RefreshAfterFileOpsAsync()
            => await RunLocalSearchAsync(SearchBox.Text, CancellationToken.None);

        private async Task RefreshCurrentViewAsync()
        {
            var q = (SearchBox.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(q)) { await RunSearchAsync(q); return; }

            var folderToShow =
                (!string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
                    ? _currentFolder : DROPBOX_ROOT;

            if (!string.IsNullOrWhiteSpace(folderToShow) && Directory.Exists(folderToShow))
                await BrowseFolderAsync(folderToShow, pushHistory: false);
        }

        private void NotifyWorkspaceChanged()
            => WorkspaceChanged?.Invoke(this, EventArgs.Empty);

        #endregion

        #region ===== Rename / Batch Rename =====

        private async void CtxRename_Click(object sender, RoutedEventArgs e)
        {
            var selected = ResultsList.SelectedItems?
                .OfType<SearchResultRow>()
                .ToList() ?? new List<SearchResultRow>();

            if (selected.Count == 0)
            {
                var row =
                    GetCtxRowFromFlyout(sender) ??
                    ResultsList.SelectedItem as SearchResultRow;

                if (row == null)
                    return;

                selected.Add(row);
            }

            var notionRows = selected.Where(IsNotionRow).ToList();
            var localRows = selected.Where(x => !IsNotionRow(x)).ToList();

            if (notionRows.Count > 0 && localRows.Count > 0)
            {
                StatusText.Text =
                    "Estado: No se pueden mezclar páginas de Notion y archivos locales al renombrar.";
                return;
            }

            if (selected.Count > 1)
            {
                try
                {
                    await ShowSmartBatchRenameDialogAsync(selected);
                }
                catch (Exception ex)
                {
                    StatusText.Text =
                        $"Estado: Error en renombrado múltiple → {ex.Message}";
                }

                return;
            }

            if (notionRows.Count == 1)
            {
                await RenameNotionPageAsync(notionRows[0]);
                return;
            }

            var localRow = localRows[0];
            var newName = await PromptRenameAsync(localRow.Name);

            if (string.IsNullOrWhiteSpace(newName) ||
                string.Equals(
                    newName,
                    localRow.Name,
                    StringComparison.Ordinal))
            {
                return;
            }

            var directory =
                Path.GetDirectoryName(localRow.Target) ??
                DROPBOX_ROOT;

            var newFullPath =
                Path.Combine(directory, newName.Trim());

            try
            {
                await ApplyFileChangeAsync(
                    FileChangeKind.Rename,
                    localRow,
                    newFullPath);

                StatusText.Text = "Estado: Renombrado ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Error al renombrar: {ex.Message}";
            }
        }

        private async Task RenameNotionPageAsync(SearchResultRow row)
        {
            if (!TryResolveNotionDataSource(
                    row,
                    out var dataSourceId,
                    out var sourceName))
            {
                StatusText.Text =
                    "Estado: No se pudo identificar la base de esta página.";
                return;
            }

            var currentTitle = row.DisplayName;
            var newTitle = await PromptNotionRenameAsync(
                currentTitle,
                sourceName);

            if (string.IsNullOrWhiteSpace(newTitle) ||
                string.Equals(
                    currentTitle,
                    newTitle.Trim(),
                    StringComparison.Ordinal))
            {
                return;
            }

            var token = GetSavedNotionToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura y guarda primero el token de Notion.";
                return;
            }

            try
            {
                ShowLoadingState(
                    $"Estado: Renombrando página en {sourceName}...",
                    row.DisplayName);

                using var cts =
                    new CancellationTokenSource(TimeSpan.FromSeconds(45));

                var service = new NotionPageActionsService();

                await service.RenamePageAsync(
                    token,
                    row.ExternalId,
                    dataSourceId,
                    newTitle.Trim(),
                    cts.Token);

                await UpdateNotionRowTitleAsync(
                    row.ExternalId,
                    sourceName,
                    newTitle.Trim());

                StatusText.Text =
                    $"Estado: Página renombrada en {sourceName} ✅";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: Notion tardó demasiado en responder.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: Error renombrando página → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async Task<string?> PromptNotionRenameAsync(
            string currentTitle,
            string sourceName)
        {
            var titleBox = new TextBox
            {
                Width = 390,
                Text = currentTitle,
                PlaceholderText = "Nuevo título"
            };

            var content = new StackPanel
            {
                Spacing = 10
            };

            content.Children.Add(new TextBlock
            {
                Text = $"Base: {sourceName}",
                Opacity = 0.75
            });

            content.Children.Add(titleBox);

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "Formato para recordatorio: AAAA-MM-DD HH:mm descripción",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping =
                        TextWrapping.Wrap,
                    Opacity = 0.82
                });

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "Ejemplo: 2026-07-24 13:30 nneft Revisar campaña",
                    TextWrapping =
                        TextWrapping.Wrap,
                    Opacity = 0.68
                });

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Renombrar página de Notion",
                Content = content,
                PrimaryButtonText = "Guardar nombre",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled =
                    !string.IsNullOrWhiteSpace(currentTitle)
            };

            titleBox.TextChanged += (_, __) =>
            {
                dialog.IsPrimaryButtonEnabled =
                    !string.IsNullOrWhiteSpace(titleBox.Text);
            };

            dialog.Opened += (_, __) =>
            {
                titleBox.Focus(FocusState.Programmatic);
                titleBox.SelectAll();
            };

            return await dialog.ShowAsync() ==
                   ContentDialogResult.Primary
                ? titleBox.Text.Trim()
                : null;
        }

        private async Task ShowSmartBatchRenameDialogAsync(
            List<SearchResultRow> rows)
        {
            var renameService = new SmartBatchRenameService();
            var isNotionBatch = rows.All(IsNotionRow);

            var inputs = rows
                .Select(row => new SmartRenameInput(
                    row.DisplayName,
                    row.IsFolder,
                    IsNotionRow(row)))
                .ToList();

            var oldNamesBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = isNotionBatch
                    ? TextWrapping.Wrap
                    : TextWrapping.NoWrap,
                Height = 135,
                Text = string.Join(
                    Environment.NewLine,
                    rows.Select(row =>
                        isNotionBatch
                            ? $"[{row.ExternalSourceName}] {row.DisplayName}"
                            : row.Name))
            };

            var oldFormatBox = new TextBox
            {
                IsReadOnly = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var newFormatBox = new TextBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "Ejemplo: 1 prtuzREVISION %1"
            };

            var previewBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = isNotionBatch
                    ? TextWrapping.Wrap
                    : TextWrapping.NoWrap,
                Height = 150
            };

            var keepExtension = new CheckBox
            {
                Content = "Mantener extensión",
                IsChecked = !isNotionBatch,
                IsEnabled = !isNotionBatch
            };

            var matchCase = new CheckBox
            {
                Content = "Coincidir mayúsculas y minúsculas",
                IsChecked = false
            };

            var matchDiacritics = new CheckBox
            {
                Content = "Coincidir acentos",
                IsChecked = false
            };

            var errorText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.90,
                Visibility = Visibility.Collapsed
            };

            var helpText = new TextBlock
            {
                Text =
                    "Las partes diferentes se representan con %1, %2, etc. " +
                    "Puedes agregar texto antes, después o entre las variables.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            };

            var historyButton = new Button
            {
                Content = "Formatos ▼",
                MinWidth = 100
            };

            var historyFlyout = new MenuFlyout();
            historyButton.Flyout = historyFlyout;

            SmartRenameAnalysis analysis =
                renameService.Analyze(
                    inputs,
                    keepExtension.IsChecked == true,
                    matchCase.IsChecked == true,
                    matchDiacritics.IsChecked == true);

            void RebuildHistory()
            {
                historyFlyout.Items.Clear();

                var presets = new[]
                {
                    ("Agregar 1 al inicio", "1 {old}"),
                    ("Agregar texto al inicio", "NUEVO {old}"),
                    ("Agregar texto al final", "{old} FINAL")
                };

                foreach (var preset in presets)
                {
                    var item = new MenuFlyoutItem
                    {
                        Text = preset.Item1
                    };

                    item.Click += (_, __) =>
                    {
                        newFormatBox.Text =
                            preset.Item2.Replace(
                                "{old}",
                                oldFormatBox.Text ?? string.Empty);
                    };

                    historyFlyout.Items.Add(item);
                }

                var history = LoadBatchRenameHistory();

                if (history.Count > 0)
                {
                    historyFlyout.Items.Add(new MenuFlyoutSeparator());
                    historyFlyout.Items.Add(new MenuFlyoutItem
                    {
                        Text = "Historial",
                        IsEnabled = false
                    });

                    foreach (var format in history)
                    {
                        var item = new MenuFlyoutItem
                        {
                            Text = format.Length > 55
                                ? format.Substring(0, 55) + "…"
                                : format
                        };

                        item.Click += (_, __) =>
                        {
                            newFormatBox.Text = format;
                        };

                        historyFlyout.Items.Add(item);
                    }
                }
            }

            var dialogWidth = Math.Clamp(
                ActualWidth - 120,
                820,
                1120);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = isNotionBatch
                    ? $"Renombrar {rows.Count} páginas de Notion"
                    : $"Renombrar {rows.Count} elementos",
                PrimaryButtonText = "Aplicar cambios",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            // ContentDialog tiene un máximo predeterminado cercano a 548 px.
            // Se sobrescriben los recursos localmente para que esta ventana
            // pueda usar correctamente el diseño comparativo horizontal.
            dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;
            dialog.Resources["ContentDialogMinWidth"] = dialogWidth;

            void Reanalyze(bool preserveNewFormat)
            {
                var previousOld = oldFormatBox.Text ?? string.Empty;
                var previousNew = newFormatBox.Text ?? string.Empty;

                analysis = renameService.Analyze(
                    inputs,
                    keepExtension.IsChecked == true,
                    matchCase.IsChecked == true,
                    matchDiacritics.IsChecked == true);

                oldFormatBox.Text = analysis.OldFormat;

                if (!preserveNewFormat ||
                    string.IsNullOrWhiteSpace(previousNew) ||
                    string.Equals(
                        previousNew,
                        previousOld,
                        StringComparison.Ordinal))
                {
                    newFormatBox.Text = analysis.OldFormat;
                }

                RefreshPreview();
            }

            void RefreshPreview()
            {
                var preview = renameService.Preview(
                    analysis,
                    newFormatBox.Text ?? string.Empty,
                    inputs,
                    keepExtension.IsChecked == true);

                previewBox.Text = string.Join(
                    Environment.NewLine,
                    preview.Names.Select((name, index) =>
                        isNotionBatch
                            ? $"[{rows[index].ExternalSourceName}] {name}"
                            : name));

                errorText.Text = preview.Error ?? string.Empty;
                errorText.Visibility =
                    string.IsNullOrWhiteSpace(preview.Error)
                        ? Visibility.Collapsed
                        : Visibility.Visible;

                dialog.IsPrimaryButtonEnabled =
                    string.IsNullOrWhiteSpace(preview.Error) &&
                    preview.Names.Count == rows.Count &&
                    !preview.Names
                        .Select((name, index) =>
                            string.Equals(
                                name,
                                inputs[index].OriginalName,
                                StringComparison.Ordinal))
                        .All(x => x);
            }

            newFormatBox.TextChanged += (_, __) => RefreshPreview();
            keepExtension.Checked += (_, __) => Reanalyze(true);
            keepExtension.Unchecked += (_, __) => Reanalyze(true);
            matchCase.Checked += (_, __) => Reanalyze(false);
            matchCase.Unchecked += (_, __) => Reanalyze(false);
            matchDiacritics.Checked += (_, __) => Reanalyze(false);
            matchDiacritics.Unchecked += (_, __) => Reanalyze(false);

            RebuildHistory();

            var content = new Grid
            {
                Width = Math.Max(740, dialogWidth - 72),
                RowSpacing = 10,
                ColumnSpacing = 16
            };

            content.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

            content.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

            content.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = new GridLength(
                        isNotionBatch ? 230 : 190)
                });
            content.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });

            var originalLabel = new TextBlock
            {
                Text = "Nombres originales",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };

            Grid.SetRow(originalLabel, 0);
            Grid.SetColumn(originalLabel, 0);
            content.Children.Add(originalLabel);

            var previewLabel = new TextBlock
            {
                Text = "Nombres nuevos (vista previa)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };

            Grid.SetRow(previewLabel, 0);
            Grid.SetColumn(previewLabel, 1);
            content.Children.Add(previewLabel);

            oldNamesBox.Height = double.NaN;
            oldNamesBox.MinHeight =
                isNotionBatch ? 230 : 190;
            oldNamesBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            oldNamesBox.VerticalAlignment = VerticalAlignment.Stretch;

            ScrollViewer.SetHorizontalScrollBarVisibility(
                oldNamesBox,
                ScrollBarVisibility.Auto);

            Grid.SetRow(oldNamesBox, 1);
            Grid.SetColumn(oldNamesBox, 0);
            content.Children.Add(oldNamesBox);

            previewBox.Height = double.NaN;
            previewBox.MinHeight =
                isNotionBatch ? 230 : 190;
            previewBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            previewBox.VerticalAlignment = VerticalAlignment.Stretch;

            ScrollViewer.SetHorizontalScrollBarVisibility(
                previewBox,
                ScrollBarVisibility.Auto);

            Grid.SetRow(previewBox, 1);
            Grid.SetColumn(previewBox, 1);
            content.Children.Add(previewBox);

            var oldFormatPanel = new StackPanel
            {
                Spacing = 5
            };

            oldFormatPanel.Children.Add(
                new TextBlock
                {
                    Text = "Formato anterior detectado",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });

            oldFormatBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            oldFormatPanel.Children.Add(oldFormatBox);

            Grid.SetRow(oldFormatPanel, 2);
            Grid.SetColumn(oldFormatPanel, 0);
            content.Children.Add(oldFormatPanel);

            var newFormatPanel = new StackPanel
            {
                Spacing = 5
            };

            newFormatPanel.Children.Add(
                new TextBlock
                {
                    Text = "Formato nuevo",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });

            var formatGrid = new Grid
            {
                ColumnSpacing = 8
            };

            formatGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

            formatGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            newFormatBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(newFormatBox, 0);
            formatGrid.Children.Add(newFormatBox);

            Grid.SetColumn(historyButton, 1);
            historyButton.VerticalAlignment = VerticalAlignment.Stretch;
            formatGrid.Children.Add(historyButton);

            newFormatPanel.Children.Add(formatGrid);

            Grid.SetRow(newFormatPanel, 2);
            Grid.SetColumn(newFormatPanel, 1);
            content.Children.Add(newFormatPanel);

            helpText.Text = isNotionBatch
                ? "Las diferencias se representan con %1, %2, etc. " +
                  "Recordatorio Notion: AAAA-MM-DD HH:mm descripción."
                : "Las diferencias se representan con %1, %2, etc. " +
                  "Recordatorio local/Dropbox: AAAA-MM-DD HH-mm descripción.ext.";

            Grid.SetRow(helpText, 3);
            Grid.SetColumn(helpText, 0);
            Grid.SetColumnSpan(helpText, 2);
            content.Children.Add(helpText);

            var optionsGrid = new Grid
            {
                ColumnSpacing = 22
            };

            optionsGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            optionsGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            optionsGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            optionsGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

            Grid.SetColumn(keepExtension, 0);
            optionsGrid.Children.Add(keepExtension);

            Grid.SetColumn(matchCase, 1);
            optionsGrid.Children.Add(matchCase);

            Grid.SetColumn(matchDiacritics, 2);
            optionsGrid.Children.Add(matchDiacritics);

            Grid.SetRow(optionsGrid, 4);
            Grid.SetColumn(optionsGrid, 0);
            Grid.SetColumnSpan(optionsGrid, 2);
            content.Children.Add(optionsGrid);

            Grid.SetRow(errorText, 5);
            Grid.SetColumn(errorText, 0);
            Grid.SetColumnSpan(errorText, 2);
            content.Children.Add(errorText);

            dialog.Content = content;

            dialog.Opened += (_, __) =>
            {
                Reanalyze(false);
                RefreshPreview();
                newFormatBox.Focus(FocusState.Programmatic);
                newFormatBox.SelectAll();
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var finalPreview = renameService.Preview(
                analysis,
                newFormatBox.Text ?? string.Empty,
                inputs,
                keepExtension.IsChecked == true);

            if (!string.IsNullOrWhiteSpace(finalPreview.Error) ||
                finalPreview.Names.Count != rows.Count)
            {
                StatusText.Text =
                    $"Estado: Renombrado cancelado → {finalPreview.Error}";
                return;
            }

            AddFormatToHistory(newFormatBox.Text);

            if (isNotionBatch)
            {
                await ApplySmartNotionBatchRenameAsync(
                    rows,
                    finalPreview.Names.ToList());
            }
            else
            {
                await ApplySmartLocalBatchRenameAsync(
                    rows,
                    finalPreview.Names.ToList());
            }
        }

        private async Task ApplySmartLocalBatchRenameAsync(
            List<SearchResultRow> rows,
            List<string> newNames)
        {
            var success = 0;
            var failed = 0;
            string? lastError = null;

            ShowLoadingState(
                $"Estado: Renombrando {rows.Count} elementos...",
                "Aplicando el formato nuevo a los archivos seleccionados.");

            try
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    var row = rows[index];

                    try
                    {
                        UpdateLoadingState(
                            $"Estado: Renombrando {index + 1} de {rows.Count} → {row.Name}",
                            newNames[index]);

                        var directory =
                            Path.GetDirectoryName(row.Target) ??
                            DROPBOX_ROOT;

                        var newFullPath =
                            Path.Combine(directory, newNames[index]);

                        if (string.Equals(
                                row.Target,
                                newFullPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        await ApplyFileChangeAsync(
                            FileChangeKind.Rename,
                            row,
                            newFullPath);

                        success++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        lastError = ex.Message;
                    }
                }

                try
                {
                    await RunLocalSearchAsync(
                        SearchBox.Text,
                        CancellationToken.None);
                }
                catch
                {
                    // El índice ya se actualizó; el refresh no debe invalidar el lote.
                }

                StatusText.Text = failed == 0
                    ? $"Estado: Renombrados ✅ ({success})"
                    : $"Estado: Renombrados ✅ ({success}) · Fallaron ❌ ({failed})" +
                      (string.IsNullOrWhiteSpace(lastError)
                          ? string.Empty
                          : $" · Último: {lastError}");
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async Task ApplySmartNotionBatchRenameAsync(
            List<SearchResultRow> rows,
            List<string> newTitles)
        {
            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura y guarda primero el token de Notion.";
                return;
            }

            var service = new NotionPageActionsService();
            var snapshot = App.LocalIndex.GetAll();
            var success = 0;
            var failed = 0;
            string? lastError = null;

            ShowLoadingState(
                $"Estado: Renombrando {rows.Count} páginas de Notion...",
                "Las páginas pueden pertenecer a bases diferentes.");

            try
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    var row = rows[index];

                    if (!TryResolveNotionDataSource(
                            row,
                            out var dataSourceId,
                            out var sourceName))
                    {
                        failed++;
                        lastError =
                            $"{row.DisplayName}: no se identificó la base.";
                        continue;
                    }

                    try
                    {
                        UpdateLoadingState(
                            $"Estado: Renombrando {index + 1} de {rows.Count} → {row.DisplayName}",
                            $"Base: {sourceName}");

                        using var cts =
                            new CancellationTokenSource(
                                TimeSpan.FromSeconds(45));

                        await service.RenamePageAsync(
                            token,
                            row.ExternalId,
                            dataSourceId,
                            newTitles[index],
                            cts.Token);

                        var indexedRow = snapshot.FirstOrDefault(x =>
                            x.Source == SearchSource.Notion &&
                            string.Equals(
                                x.ExternalId,
                                row.ExternalId,
                                StringComparison.OrdinalIgnoreCase));

                        if (indexedRow != null)
                        {
                            indexedRow.ExternalSourceName = sourceName;
                            indexedRow.Name =
                                $"[{sourceName}] {newTitles[index]}";
                            indexedRow.SearchText = string.Join(
                                " ",
                                new[]
                                {
                                    sourceName,
                                    newTitles[index],
                                    indexedRow.Description
                                }.Where(x =>
                                    !string.IsNullOrWhiteSpace(x)));
                            indexedRow.ServerModified =
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                        }

                        success++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        lastError =
                            $"{row.DisplayName}: {ex.Message}";
                    }
                }

                if (success > 0)
                {
                    App.LocalIndex.Set(snapshot);
                    await PersistCombinedIndexIfPossibleAsync(snapshot);

                    var query =
                        (SearchBox.Text ?? string.Empty).Trim();

                    if (!string.IsNullOrWhiteSpace(query))
                        await RunSearchAsync(query);
                    else
                        await RunLocalSearchAsync(string.Empty);
                }

                StatusText.Text = failed == 0
                    ? $"Estado: Páginas renombradas ✅ ({success})"
                    : $"Estado: Páginas renombradas ✅ ({success}) · Fallaron ❌ ({failed})" +
                      (string.IsNullOrWhiteSpace(lastError)
                          ? string.Empty
                          : $" · Último: {lastError}");
            }
            finally
            {
                HideLoadingState();
            }
        }

        private List<string> LoadBatchRenameHistory()
        {
            try
            {
                var localSettings =
                    ApplicationData.Current.LocalSettings;

                if (localSettings.Values.TryGetValue(
                        LS_BATCH_RENAME_HISTORY,
                        out var raw) &&
                    raw is string json &&
                    !string.IsNullOrWhiteSpace(json))
                {
                    return JsonSerializer
                        .Deserialize<List<string>>(json)?
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()
                        .ToList() ??
                        new List<string>();
                }
            }
            catch
            {
                // Historial opcional.
            }

            return new List<string>();
        }

        private void SaveBatchRenameHistory(List<string> items)
        {
            try
            {
                var clean = items
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct()
                    .Take(BATCH_RENAME_HISTORY_MAX)
                    .ToList();

                ApplicationData.Current.LocalSettings.Values[
                    LS_BATCH_RENAME_HISTORY] =
                    JsonSerializer.Serialize(clean);
            }
            catch
            {
                // Historial opcional.
            }
        }

        private void AddFormatToHistory(string format)
        {
            var clean = (format ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return;

            var history = LoadBatchRenameHistory();

            history.RemoveAll(x =>
                string.Equals(
                    x,
                    clean,
                    StringComparison.Ordinal));

            history.Insert(0, clean);
            SaveBatchRenameHistory(history);
        }

        private async Task<string?> PromptRenameAsync(string currentName)
        {
            var textBox = new TextBox
            {
                Text = currentName,
                Width = 430,
                PlaceholderText =
                    "AAAA-MM-DD HH-mm descripción.ext"
            };

            var content = new StackPanel
            {
                Spacing = 9
            };

            content.Children.Add(textBox);

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "Formato para recordatorio: AAAA-MM-DD HH-mm descripción.ext",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping =
                        TextWrapping.Wrap,
                    Opacity = 0.82
                });

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "Ejemplo: 2026-07-24 13-30 nneft Revisar campaña.png",
                    TextWrapping =
                        TextWrapping.Wrap,
                    Opacity = 0.68
                });

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "En archivos locales y Dropbox la hora usa guion porque Windows no permite ':' en nombres.",
                    TextWrapping =
                        TextWrapping.Wrap,
                    Opacity = 0.60
                });

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Renombrar",
                Content = content,
                PrimaryButtonText = "Aceptar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled =
                    !string.IsNullOrWhiteSpace(currentName)
            };

            textBox.TextChanged += (_, __) =>
            {
                dialog.IsPrimaryButtonEnabled =
                    !string.IsNullOrWhiteSpace(
                        textBox.Text);
            };

            dialog.Opened += (_, __) =>
            {
                textBox.Focus(
                    FocusState.Programmatic);
                textBox.SelectAll();
            };

            return await dialog.ShowAsync() ==
                   ContentDialogResult.Primary
                ? textBox.Text
                : null;
        }

        #endregion

        #region ===== Helpers de selección / ctx =====

        private SearchResultRow? GetCtxRowFromFlyout(object sender)
        {
            var mfi = sender as MenuFlyoutItem;
            var flyout = mfi?.Parent as MenuFlyout;
            var fe = flyout?.Target as FrameworkElement;
            return fe?.DataContext as SearchResultRow;
        }

        private SearchResultRow? GetCtxRowOrSelected(object sender)
            => GetCtxRowFromFlyout(sender) ?? ResultsList.SelectedItem as SearchResultRow;

        private List<SearchResultRow> GetSelectedRowsOrCtx(object sender)
        {
            var selected = ResultsList.SelectedItems?.Cast<SearchResultRow>().ToList();
            if (selected != null && selected.Count > 0) return selected;

            var ctx = GetCtxRowOrSelected(sender);
            return ctx != null ? new List<SearchResultRow> { ctx } : new List<SearchResultRow>();
        }

        private async Task<bool> ConfirmOpenManyAsync(int count, int maxToOpen)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Confirmar",
                Content = $"Vas a abrir {Math.Min(count, maxToOpen)} de {count} elementos.\n¿Deseas continuar?",
                PrimaryButtonText = "Abrir",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async Task<bool> ConfirmDeleteAsync(List<SearchResultRow> rows)
        {
            var count = rows.Count;
            if (count <= 0) return false;

            var preview = string.Join("\n", rows.Take(6).Select(r => $"• {r.Name}"));
            if (count > 6) preview += $"\n• … y {count - 6} más";

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Confirmar eliminación",
                Content = $"Vas a eliminar {count} elemento(s):\n\n{preview}\n\n¿Deseas continuar?",
                PrimaryButtonText = "Eliminar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        #endregion

        #region ===== Vistas rápidas de Notion =====

        private async void OpenSavedNotionView_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item ||
                item.Tag is not string url)
            {
                StatusText.Text =
                    "Estado: La vista de Notion no tiene un enlace válido.";
                return;
            }

            await OpenNotionPageWithFallbackAsync(
                url,
                desktopSuccessStatus:
                    $"Vista abierta en Notion Desktop · {item.Text}",
                browserSuccessStatus:
                    $"Vista abierta en el navegador · {item.Text}",
                failureStatus:
                    "No se pudo abrir la vista de Notion",
                invalidUrlStatus:
                    "La vista de Notion no tiene un enlace válido");
        }


        #endregion

        #region== Notion ==

        private async Task<bool> OpenNotionDesktopAsync(
            SearchResultRow row,
            bool allowBrowserFallback)
        {
            if (row == null || !IsNotionRow(row))
                return false;

            return await OpenNotionPageWithFallbackAsync(
                GetRowTarget(row),
                desktopSuccessStatus:
                    "Página abierta en Notion Desktop",
                browserSuccessStatus:
                    "Página abierta en el navegador",
                failureStatus:
                    "No se pudo abrir la página de Notion",
                invalidUrlStatus:
                    "La página no tiene una URL válida de Notion",
                allowBrowserFallback:
                    allowBrowserFallback);
        }

        private static string GetRowTarget(SearchResultRow row)
        {
            if (IsNotionRow(row) && !string.IsNullOrWhiteSpace(row.ExternalUrl))
                return row.ExternalUrl;

            return row.Target ?? string.Empty;
        }

        private void ResultsContextFlyout_Opening(object sender, object e)
        {
            var flyoutRow =
                (sender as MenuFlyout)?.Target is FrameworkElement target
                    ? target.DataContext as SearchResultRow
                    : null;

            var row = flyoutRow ??
                      ResultsList.SelectedItem as SearchResultRow;

            var isNotion = row != null && IsNotionRow(row);

            CtxMenuOpenItem.Text = isNotion
                ? "Abrir en Notion"
                : "Abrir";

            CtxMenuOpenExplorerItem.Text = isNotion
                ? "No aplica: Explorador local"
                : "Abrir en Explorador Local";

            CtxMenuOpenExplorerItem.IsEnabled = !isNotion;

            CtxMenuCopyPathItem.Text = isNotion
                ? "Copiar URL de Notion"
                : "Copiar ruta";

            CtxMenuCopyLinkItem.Text = isNotion
                ? "Copiar link de Notion"
                : "Copiar link";

            CtxMenuCopyContentItem.Text = isNotion
                ? "Copiar contenido de Notion"
                : "Copiar contenido";
            CtxMenuCopyContentItem.IsEnabled = row != null;

            var detectedDomain = row == null
                ? string.Empty
                : TryExtractFirstDomain(row);

            var hasDomain =
                !string.IsNullOrWhiteSpace(detectedDomain);

            CtxMenuCopyDomainItem.IsEnabled = hasDomain;
            CtxMenuOpenDomainItem.IsEnabled = hasDomain;

            var canUseDropboxActions = CanCreateDropboxFolderHere(row);

            var hasNotionToken =
                !string.IsNullOrWhiteSpace(
                    ApplicationData.Current.LocalSettings.Values["Notion.Token"] as string);

            CtxMenuCreateDropboxFolderItem.IsEnabled = canUseDropboxActions;
            CtxMenuCreateDropboxFolderItem.Visibility =
                isNotion ? Visibility.Collapsed : Visibility.Visible;

            CtxMenuUploadDropboxFileItem.IsEnabled = canUseDropboxActions;
            CtxMenuUploadDropboxFileItem.Visibility =
                isNotion ? Visibility.Collapsed : Visibility.Visible;

            CtxMenuUploadNotionFileItem.IsEnabled = hasNotionToken;

            CtxMenuRenameItem.Text = isNotion
                ? "Renombrar página..."
                : "Renombrar...";

            CtxMenuDeleteItem.Text = isNotion
                ? "Mover a papelera..."
                : "Eliminar";

            CtxMenuRenameItem.IsEnabled = row != null;
            CtxMenuDeleteItem.IsEnabled = row != null;

            if (row != null)
            {
                CtxMenuBookmarkItem.Text = row.IsBookmarked
                    ? "Quitar de Favoritos"
                    : "Agregar a Favoritos";
            }
            else
            {
                CtxMenuBookmarkItem.Text = "Agregar a Favoritos";
            }
        }

        private bool CanCreateDropboxFolderHere(SearchResultRow? row)
        {
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
                return false;

            if (row != null)
            {
                if (IsNotionRow(row))
                    return false;

                var selectedDestination = row.IsFolder
                    ? row.Target
                    : Path.GetDirectoryName(row.Target) ?? string.Empty;

                return !string.IsNullOrWhiteSpace(selectedDestination) &&
                       Directory.Exists(selectedDestination) &&
                       _dropboxPathMapper.IsInsideDropboxRoot(
                           DROPBOX_ROOT,
                           selectedDestination);
            }

            var currentDestination =
                !string.IsNullOrWhiteSpace(_currentFolderPath)
                    ? _currentFolderPath
                    : !string.IsNullOrWhiteSpace(_currentFolder)
                        ? _currentFolder
                        : DROPBOX_ROOT;

            return !string.IsNullOrWhiteSpace(currentDestination) &&
                   Directory.Exists(currentDestination) &&
                   _dropboxPathMapper.IsInsideDropboxRoot(
                       DROPBOX_ROOT,
                       currentDestination);
        }

        private void DetailsMoreFlyout_Opening(object sender, object e)
        {
            var row = ResultsList.SelectedItem as SearchResultRow;
            var isNotion = row != null && IsNotionRow(row);

            var canUseDropboxActions = CanCreateDropboxFolderHere(row);

            var hasNotionToken =
                !string.IsNullOrWhiteSpace(
                    ApplicationData.Current.LocalSettings.Values["Notion.Token"] as string);

            DetailsCreateDropboxFolderItem.IsEnabled = canUseDropboxActions;
            DetailsCreateDropboxFolderItem.Visibility =
                isNotion ? Visibility.Collapsed : Visibility.Visible;

            DetailsUploadDropboxFileItem.IsEnabled = canUseDropboxActions;
            DetailsUploadDropboxFileItem.Visibility =
                isNotion ? Visibility.Collapsed : Visibility.Visible;

            DetailsUploadNotionFileItem.IsEnabled = hasNotionToken;

            DetailsRenameItem.Text = isNotion
                ? "Renombrar página..."
                : "Renombrar...";

            DetailsDeleteItem.Text = isNotion
                ? "Mover a papelera..."
                : "Eliminar";

            DetailsRenameItem.IsEnabled = row != null;
            DetailsDeleteItem.IsEnabled = row != null;
        }

        private static string GetSavedNotionToken()
            => ApplicationData.Current.LocalSettings.Values[
                "Notion.Token"] as string ?? string.Empty;

        private static bool TryResolveNotionDataSource(
            SearchResultRow row,
            out string dataSourceId,
            out string sourceName)
        {
            dataSourceId = string.Empty;

            var requestedSourceName =
                (row.ExternalSourceName ?? string.Empty).Trim();

            sourceName = requestedSourceName;

            if (string.IsNullOrWhiteSpace(requestedSourceName))
                return false;

            var source = NotionDataSources.Default.FirstOrDefault(x =>
                string.Equals(
                    x.Name,
                    requestedSourceName,
                    StringComparison.OrdinalIgnoreCase));

            if (source == null ||
                string.IsNullOrWhiteSpace(source.DataSourceId))
            {
                return false;
            }

            dataSourceId = source.DataSourceId;
            sourceName = source.Name;
            return true;
        }

        private async Task UpdateNotionRowTitleAsync(
            string pageId,
            string sourceName,
            string newTitle)
        {
            var snapshot = App.LocalIndex.GetAll();

            var row = snapshot.FirstOrDefault(x =>
                x.Source == SearchSource.Notion &&
                string.Equals(
                    x.ExternalId,
                    pageId,
                    StringComparison.OrdinalIgnoreCase));

            if (row != null)
            {
                row.ExternalSourceName = sourceName;
                row.Name = $"[{sourceName}] {newTitle}";
                row.SearchText = string.Join(
                    " ",
                    new[] { sourceName, newTitle, row.Description }
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                row.ServerModified =
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            }

            App.LocalIndex.Set(snapshot);
            await PersistCombinedIndexIfPossibleAsync(snapshot);

            var query = (SearchBox.Text ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(query))
                await RunSearchAsync(query);
            else
                RefreshResultsListView();
        }

        private async Task RemoveNotionRowsFromIndexAsync(
            HashSet<string> pageIds)
        {
            var snapshot = App.LocalIndex
                .GetAll()
                .Where(x =>
                    x.Source != SearchSource.Notion ||
                    !pageIds.Contains(x.ExternalId))
                .ToList();

            App.LocalIndex.Set(snapshot);
            await PersistCombinedIndexIfPossibleAsync(snapshot);

            foreach (var row in Results
                .Where(x =>
                    x.Source == SearchSource.Notion &&
                    pageIds.Contains(x.ExternalId))
                .ToList())
            {
                Results.Remove(row);
            }

            ResultsList.SelectedItem = null;

            var query = (SearchBox.Text ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(query))
                await RunSearchAsync(query);
            else
                RefreshResultsListView();
        }

        private static async Task PersistCombinedIndexIfPossibleAsync(
            List<SearchResultRow> snapshot)
        {
            var root = ApplicationData.Current.LocalSettings.Values[
                LS_DropboxRoot] as string;

            if (!string.IsNullOrWhiteSpace(root) &&
                Directory.Exists(root) &&
                snapshot.Count > 0)
            {
                await LocalIndexPersistence.SaveAsync(
                    root,
                    snapshot,
                    CancellationToken.None);
            }
        }

        #endregion


        #region ===== Exclusiones =====

        private sealed class FolderPickItem
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public bool IsChecked { get; set; }
        }

        public sealed class ExcludeNode : INotifyPropertyChanged
        {
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
            public ObservableCollection<ExcludeNode> Children { get; } = new();

            private bool _hasDummyChild;
            public bool HasDummyChild { get => _hasDummyChild; set { if (_hasDummyChild != value) { _hasDummyChild = value; OnPropertyChanged(); } } }

            private bool _isChecked;
            public bool IsChecked { get => _isChecked; set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } } }

            private bool _isEnabled = true;
            public bool IsEnabled { get => _isEnabled; set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } } }

            private bool _isLoaded;
            public bool IsLoaded { get => _isLoaded; set { if (_isLoaded != value) { _isLoaded = value; OnPropertyChanged(); } } }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static Microsoft.UI.Xaml.Controls.TreeViewNode? FindNodeByData(
            IList<Microsoft.UI.Xaml.Controls.TreeViewNode> nodes, ExcludeNode target)
        {
            foreach (var n in nodes)
            {
                if (ReferenceEquals(n.Content, target)) return n;
                var found = FindNodeByData(n.Children, target);
                if (found != null) return found;
            }
            return null;
        }

        private Microsoft.UI.Xaml.Controls.TreeViewNode MakeExcludeNode(string dirPath)
        {
            var data = new ExcludeNode
            {
                Name = System.IO.Path.GetFileName(dirPath),
                Path = dirPath,
                IsChecked = false,
                IsLoaded = true   // ya cargado — no lazy load
            };

            var node = new Microsoft.UI.Xaml.Controls.TreeViewNode
            {
                Content = data,
                IsExpanded = false
            };

            // Carga directa de subcarpetas (solo 1 nivel)
            // Al expandir cada hijo, sus propios hijos se cargarán igual
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dirPath))
                {
                    var childData = new ExcludeNode
                    {
                        Name = System.IO.Path.GetFileName(sub),
                        Path = sub,
                        IsChecked = false,
                        IsLoaded = false   // los nietos se cargan al expandir
                    };
                    var childNode = new Microsoft.UI.Xaml.Controls.TreeViewNode
                    {
                        Content = childData,
                        IsExpanded = false,
                        HasUnrealizedChildren = HasSubfolders(sub)
                    };
                    node.Children.Add(childNode);
                }
            }
            catch { }

            return node;
        }

        private void LoadExcludedFolders()
        {
            _excludedFolders.Clear();
            var raw = ApplicationData.Current.LocalSettings.Values[LS_ExcludedFolders] as string;
            if (string.IsNullOrWhiteSpace(raw)) return;

            foreach (var p in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = p.Trim();
                if (!string.IsNullOrWhiteSpace(t) && !_excludedFolders.Contains(t, StringComparer.OrdinalIgnoreCase))
                    _excludedFolders.Add(t);
            }
        }

        private void SaveExcludedFolders()
        {
            ApplicationData.Current.LocalSettings.Values[LS_ExcludedFolders] =
                string.Join("|", _excludedFolders);
        }

        private void RefreshExcludedFoldersUi()
        {
            _excludedFoldersUi.Clear();
            foreach (var p in _excludedFolders)
                _excludedFoldersUi.Add(p);

            if (ExcludedFoldersList != null)
                ExcludedFoldersList.ItemsSource = _excludedFoldersUi;

            if (ExcludedHint != null)
                ExcludedHint.Visibility = _excludedFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool IsExcludedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var norm = NormalizePath(path);
            foreach (var ex in _excludedFolders)
            {
                var exNorm = NormalizePath(ex);
                if (string.Equals(norm, exNorm, StringComparison.OrdinalIgnoreCase)) return true;
                if (norm.StartsWith(EnsureDirPrefix(exNorm), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private async void BtnAddExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
            {
                StatusText.Text = "Estado: No hay ruta Dropbox configurada.";
                return;
            }

            try
            {
                var tv = new Microsoft.UI.Xaml.Controls.TreeView { SelectionMode = Microsoft.UI.Xaml.Controls.TreeViewSelectionMode.None, Height = 360 };

                tv.ItemContainerStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                       TargetType='TreeViewItem'>
                    <Setter Property='MinHeight' Value='28'/>
                    <Setter Property='Padding' Value='0'/>
                    <Setter Property='Margin' Value='0'/>
                    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                </Style>");

                tv.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
                <DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                  <Grid MinHeight='28' Margin='0'>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width='Auto'/>
                      <ColumnDefinition Width='Auto'/>
                      <ColumnDefinition Width='*'/>
                    </Grid.ColumnDefinitions>
                    <CheckBox Grid.Column='0' IsChecked='{Binding Content.IsChecked, Mode=TwoWay}' IsEnabled='{Binding Content.IsEnabled}' VerticalAlignment='Center' Margin='0,0,8,0'/>
                    <TextBlock Grid.Column='1' VerticalAlignment='Center' FontSize='13' Text='{Binding Content.Name}' TextTrimming='CharacterEllipsis' Opacity='0.9'/>
                  </Grid>
                </DataTemplate>");

                // Expanding carga los nietos cuando el usuario expande un hijo
                tv.Expanding += (s, expandArgs) =>
                {
                    if (expandArgs.Item is not Microsoft.UI.Xaml.Controls.TreeViewNode expandNode) return;
                    if (expandNode.Content is not ExcludeNode expandData) return;
                    if (expandData.IsLoaded) return;

                    expandData.IsLoaded = true;
                    expandNode.HasUnrealizedChildren = false;

                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(expandData.Path))
                        {
                            var childData = new ExcludeNode
                            {
                                Name = System.IO.Path.GetFileName(sub),
                                Path = sub,
                                IsChecked = false,
                                IsLoaded = false
                            };
                            expandNode.Children.Add(new Microsoft.UI.Xaml.Controls.TreeViewNode
                            {
                                Content = childData,
                                IsExpanded = false,
                                HasUnrealizedChildren = HasSubfolders(sub)
                            });
                        }
                    }
                    catch { }
                };

                // Expanding se dispara cuando el usuario abre un nodo con HasUnrealizedChildren=true
                tv.Expanding += (s, expandArgs) =>
                {
                    if (expandArgs.Item is not Microsoft.UI.Xaml.Controls.TreeViewNode expandNode) return;
                    if (expandNode.Content is not ExcludeNode expandData) return;
                    if (expandData.IsLoaded) return;

                    expandData.IsLoaded = true;
                    expandNode.HasUnrealizedChildren = false;

                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(expandData.Path))
                            expandNode.Children.Add(MakeExcludeNode(sub));
                    }
                    catch { }
                };

                // ── FIX 2: Al checkear padre, colapsar y limpiar sus hijos ───
                // Si la carpeta padre está excluida, no tiene sentido navegar sus hijos
                tv.AddHandler(UIElement.TappedEvent, new TappedEventHandler((s, e2) =>
                {
                    var cb = FindAncestor<CheckBox>(e2.OriginalSource as DependencyObject);
                    if (cb == null) return;

                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        Microsoft.UI.Xaml.Controls.TreeViewNode? node = null;
                        ExcludeNode? data = null;

                        if (cb.DataContext is Microsoft.UI.Xaml.Controls.TreeViewNode tvn) { node = tvn; data = tvn.Content as ExcludeNode; }
                        else if (cb.DataContext is ExcludeNode dn) { data = dn; node = FindNodeByData(tv.RootNodes, dn); }

                        if (node == null || data == null || data.Path == "__dummy__") return;

                        var isChecked = cb.IsChecked == true;
                        data.IsChecked = isChecked;
                        data.IsEnabled = true;

                        if (isChecked)
                        {
                            // Padre checked → colapsar nodo y ocultar hijos visualmente
                            // (no tiene sentido excluir subcarpetas si el padre ya está excluido)
                            node.IsExpanded = false;
                            // Marcar hijos como checked+disabled sin mostrarlos
                            ApplyToChildren(node, isChecked: true);
                        }
                        else
                        {
                            // Padre unchecked → rehabilitar hijos
                            ApplyToChildren(node, isChecked: false);
                        }
                    });
                }), true);

                foreach (var dir in Directory.EnumerateDirectories(DROPBOX_ROOT))
                {
                    if (IsExcludedPath(dir)) continue;
                    tv.RootNodes.Add(MakeExcludeNode(dir));
                }

                var dialog = new ContentDialog
                {
                    Title = "Excluir varias carpetas",
                    Content = tv,
                    PrimaryButtonText = "Agregar seleccionadas",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                var selected = new List<string>();
                foreach (var n in tv.RootNodes)
                    CollectChecked(n, selected);

                if (selected.Count == 0) { StatusText.Text = "Estado: No seleccionaste nada."; return; }

                foreach (var p in selected)
                {
                    if (_excludedFolders.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) continue;
                    _excludedFolders.Add(p);
                }

                SaveExcludedFolders();
                RefreshExcludedFoldersUi();
                StatusText.Text = $"Estado: Excluidas {selected.Count} carpetas ✅";
                await RefreshCurrentViewAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error excluyendo → {ex.Message}";
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T wanted) return wanted;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static void CollectChecked(Microsoft.UI.Xaml.Controls.TreeViewNode node, List<string> acc)
        {
            if (node?.Content is ExcludeNode data && data.Path != "__dummy__")
            {
                if (data.IsChecked)
                {
                    // Padre checked → agregar solo el padre y NO recorrer hijos
                    // (excluir el padre ya excluye todo su contenido)
                    if (!string.IsNullOrWhiteSpace(data.Path))
                        acc.Add(data.Path);
                    return;  // ← STOP, no bajar a hijos
                }
            }

            // Padre NO checked → seguir buscando en hijos
            foreach (var child in node.Children)
                CollectChecked(child, acc);
        }

        private static bool HasSubfolders(string path)
        {
            try { return Directory.EnumerateDirectories(path).Any(); }
            catch { return false; }
        }

        private void ApplyToChildren(Microsoft.UI.Xaml.Controls.TreeViewNode parentNode, bool isChecked)
        {
            foreach (var child in parentNode.Children)
            {
                if (child.Content is ExcludeNode cd && cd.Path != "__dummy__")
                {
                    // Si padre checked → deshabilitar hijos (cubiertos por el padre)
                    // Si padre unchecked → rehabilitar hijos y desmarcarlos
                    cd.IsEnabled = !isChecked;
                    cd.IsChecked = false;  // siempre desmarcar — el padre es el que cuenta
                }
                ApplyToChildren(child, isChecked);
            }
        }

        private async void BtnRemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            if (b.Tag is not string path) return;

            _excludedFolders.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            SaveExcludedFolders();
            RefreshExcludedFoldersUi();
            StatusText.Text = "Estado: Exclusión eliminada ✅";
            await RefreshCurrentViewAsync();
        }

        #endregion

        #region ===== Sugerencias de Búsqueda =====

        private static List<string>
            BuildStructuredNotionTitleSuggestions(
                string? input,
                int max = 12)
        {
            var raw =
                Regex.Replace(
                    (input ?? string.Empty).Trim(),
                    @"\s+",
                    " ");

            var projectTypes = new[]
            {
                "sseo",
                "aapli",
                "aads",
                "wwebs"
            };

            var months = new[]
            {
                "jjane",
                "ffebr",
                "mmarz",
                "aabri",
                "mmayo",
                "jjuni",
                "jjuli",
                "aagos",
                "ssept",
                "ooctu",
                "nnovi",
                "ddici"
            };

            string CleanSegment(string value)
                => Regex.Replace(
                    (value ?? string.Empty).Trim(),
                    @"\s+",
                    " ");

            string BuildTitle(
                string domain,
                string project = "",
                string month = "",
                string detail = "")
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(domain))
                    parts.Add(domain.Trim());

                if (!string.IsNullOrWhiteSpace(project))
                    parts.Add(project.Trim());

                if (!string.IsNullOrWhiteSpace(month))
                    parts.Add(month.Trim());

                if (!string.IsNullOrWhiteSpace(detail))
                    parts.Add(detail.Trim());

                return Regex.Replace(
                    string.Join(" ", parts),
                    @"\s+",
                    " ").Trim();
            }

            var suggestions =
                new List<string>();

            var explicitSegments = raw
                .Split(
                    '/',
                    StringSplitOptions.None)
                .Select(CleanSegment)
                .ToList();

            var domain = explicitSegments.Count > 0
                ? explicitSegments[0]
                : string.Empty;

            var project = explicitSegments.Count > 1
                ? explicitSegments[1]
                : string.Empty;

            var month = explicitSegments.Count > 2
                ? explicitSegments[2]
                : string.Empty;

            var detail = explicitSegments.Count > 3
                ? string.Join(
                    " / ",
                    explicitSegments.Skip(3))
                : string.Empty;

            // También entiende escritura sin diagonales:
            // "weblab.mx sseo" -> dominio + tipo.
            if (!raw.Contains('/'))
            {
                var domainMatch = Regex.Match(
                    raw,
                    @"(?<![\w@])(?:https?://)?(?:www\.)?" +
                    @"(?<domain>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+" +
                    @"(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|" +
                    @"com|mx|org|net|io|co|app|dev))",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

                if (domainMatch.Success)
                {
                    domain = domainMatch.Groups["domain"]
                        .Value
                        .Trim()
                        .TrimEnd('.')
                        .ToLowerInvariant();

                    var remainder = raw
                        .Substring(
                            domainMatch.Index +
                            domainMatch.Length)
                        .Trim();

                    var remainderParts = remainder
                        .Split(
                            ' ',
                            StringSplitOptions.RemoveEmptyEntries)
                        .ToList();

                    if (remainderParts.Count > 0)
                    {
                        project =
                            remainderParts[0];

                        if (remainderParts.Count > 1)
                        {
                            month =
                                remainderParts[1];
                        }

                        if (remainderParts.Count > 2)
                        {
                            detail =
                                string.Join(
                                    " ",
                                    remainderParts.Skip(2));
                        }
                    }
                }
            }

            var knownProject = projectTypes
                .FirstOrDefault(value =>
                    string.Equals(
                        value,
                        project,
                        StringComparison.OrdinalIgnoreCase));

            var knownMonth = months
                .FirstOrDefault(value =>
                    string.Equals(
                        value,
                        month,
                        StringComparison.OrdinalIgnoreCase));

            // Etapa 1: completar dominio.
            if (string.IsNullOrWhiteSpace(domain) ||
                !domain.Contains('.'))
            {
                return BuildIndexPredictiveSuggestions(
                        raw,
                        max)
                    .Where(value =>
                        value.Contains('.'))
                    .Take(max)
                    .ToList();
            }

            // Etapa 2: elegir tipo de proyecto.
            if (string.IsNullOrWhiteSpace(project) ||
                knownProject == null)
            {
                foreach (var type in projectTypes
                    .Where(type =>
                        string.IsNullOrWhiteSpace(project) ||
                        type.StartsWith(
                            project,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    suggestions.Add(
                        BuildTitle(
                            domain,
                            type));
                }

                if (suggestions.Count == 0)
                {
                    foreach (var type in projectTypes)
                    {
                        suggestions.Add(
                            BuildTitle(
                                domain,
                                type));
                    }
                }

                return suggestions
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Take(max)
                    .ToList();
            }

            // Etapa 3: elegir mes.
            if (string.IsNullOrWhiteSpace(month) ||
                knownMonth == null)
            {
                foreach (var value in months
                    .Where(value =>
                        string.IsNullOrWhiteSpace(month) ||
                        value.StartsWith(
                            month,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    suggestions.Add(
                        BuildTitle(
                            domain,
                            knownProject,
                            value));
                }

                if (suggestions.Count == 0)
                {
                    foreach (var value in months)
                    {
                        suggestions.Add(
                            BuildTitle(
                                domain,
                                knownProject,
                                value));
                    }
                }

                return suggestions
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Take(max)
                    .ToList();
            }

            // Etapa 4: sugerir detalles frecuentes del índice.
            var titlePrefix =
                BuildTitle(
                    domain,
                    knownProject,
                    knownMonth,
                    detail);

            var frequentDetails =
                BuildIndexPredictiveSuggestions(
                    detail,
                    max: max * 2)
                .Select(value =>
                    value.Trim())
                .Where(value =>
                    !string.IsNullOrWhiteSpace(value))
                .Where(value =>
                    !value.Contains(
                        domain,
                        StringComparison.OrdinalIgnoreCase))
                .Where(value =>
                    !projectTypes.Contains(
                        value,
                        StringComparer.OrdinalIgnoreCase))
                .Where(value =>
                    !months.Contains(
                        value,
                        StringComparer.OrdinalIgnoreCase))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();

            foreach (var value in frequentDetails)
            {
                suggestions.Add(
                    BuildTitle(
                        domain,
                        knownProject,
                        knownMonth,
                        value));
            }

            // Siempre deja una opción limpia para continuar escribiendo libre.
            if (suggestions.Count == 0)
            {
                suggestions.Add(
                    BuildTitle(
                        domain,
                        knownProject,
                        knownMonth));
            }

            return suggestions
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }

        private static List<string> BuildIndexPredictiveSuggestions(
            string? input,
            int max = 10)
        {
            var typed =
                (input ?? string.Empty).Trim();

            if (typed.Length < 2 ||
                !App.LocalIndex.HasData)
            {
                return new List<string>();
            }

            var activeFragment = Regex.Match(
                typed,
                @"(?:^|\s)(?<value>[\p{L}\p{Nd}._\-/]+)$",
                RegexOptions.CultureInvariant)
                .Groups["value"]
                .Value;

            if (string.IsNullOrWhiteSpace(activeFragment))
                activeFragment = typed;

            var prefix = typed.Substring(
                0,
                Math.Max(0, typed.Length - activeFragment.Length));

            var candidates = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string value, int weight)
            {
                var clean =
                    (value ?? string.Empty).Trim()
                    .Trim('(', ')', '[', ']', '{', '}', ',', ';', ':', '!', '?');

                if (clean.Length < 2 ||
                    !clean.Contains(
                        activeFragment,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!candidates.TryGetValue(clean, out var current) ||
                    weight < current)
                {
                    candidates[clean] = weight;
                }
            }

            foreach (var row in App.LocalIndex.GetAll())
            {
                var fields = new[]
                {
                    row.DisplayName,
                    row.Name,
                    row.SearchText,
                    row.Description,
                    row.ProjectUpdateStatus
                };

                foreach (var field in fields)
                {
                    if (string.IsNullOrWhiteSpace(field))
                        continue;

                    foreach (Match match in Regex.Matches(
                        field,
                        @"(?:https?://)?(?:www\.)?(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|com|mx|org|net|io|co|app|dev)|[\p{L}\p{Nd}][\p{L}\p{Nd}._\-/]{1,}",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant))
                    {
                        var token = match.Value;
                        var weight = token.StartsWith(
                            activeFragment,
                            StringComparison.OrdinalIgnoreCase)
                                ? 0
                                : 1;

                        AddCandidate(token, weight);
                    }
                }
            }

            foreach (var fixedValue in new[]
            {
                "sseo", "aapli", "aads", "wwebs",
                "jjane", "ffebr", "mmarz", "aabri",
                "mmayo", "jjuni", "jjuli", "aagos",
                "ssept", "ooctu", "nnovi", "ddici"
            })
            {
                AddCandidate(fixedValue, 0);
            }

            return candidates
                .OrderBy(item => item.Value)
                .ThenBy(item => item.Key.Length)
                .ThenBy(item => item.Key)
                .Take(Math.Max(1, max))
                .Select(item => prefix + item.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GenerateStutterSuggestions(string input, int max = 8)
        {
            input = (input ?? "").Trim();
            if (input.Length < 2) return new List<string>();

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var first = parts.Length > 0 ? parts[0] : input;
            var lower = first.ToLowerInvariant();
            char c0 = lower[0];

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void add(string s)
            {
                s = (s ?? "").Trim();
                if (s.Length == 0) return;
                if (parts.Length > 1) s = $"{s} {string.Join(" ", parts.Skip(1))}";
                if (set.Count < max) set.Add(s);
            }

            add($"{c0}{first}");
            if (first.Length >= 2) add($"{first.Substring(0, 2)}{first}");
            if (first.Length >= 3) add($"{first.Substring(0, 3)}{first}");
            add($"{c0}-{first}");
            add($"{c0}{c0}-{first}");
            if (first.Length >= 4) add($"{c0}{first.Substring(0, 4)}");
            if (first.Length >= 2)
            {
                var second = first[1];
                if ("aeiouáéíóúAEIOUÁÉÍÓÚ".IndexOf(second) >= 0)
                    add($"{first[0]}{second}{first.Substring(1)}");
            }
            add($"{first.Substring(0, 1).ToUpperInvariant()}{first.Substring(0, Math.Min(4, first.Length)).ToUpperInvariant()}");
            add($"{first.Substring(0, 1).ToUpperInvariant()}-{first.ToUpperInvariant()}");

            return set.Take(max).ToList();
        }

        private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string s && !string.IsNullOrWhiteSpace(s))
            {
                _suppressSuggest = true;
                sender.Text = s;
                _suppressSuggest = false;
                sender.IsSuggestionListOpen = false;
                _useExpandedQueryOnSubmit = true;
            }
        }

        private static string CanonicalizeStutterToken(string token)
        {
            token = (token ?? "").Trim();
            if (token.Length == 0) return token;
            token = token.Replace("-", "").Replace("_", "");
            while (token.Length >= 2 && char.ToLowerInvariant(token[0]) == char.ToLowerInvariant(token[1]))
                token = token.Substring(1);
            return token;
        }

        private static List<string> ExpandStutterQuery(string raw)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length == 0) return new List<string>();

            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var expandedParts = new List<List<string>>();

            foreach (var p in parts)
            {
                var baseTok = CanonicalizeStutterToken(p);
                var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { p, baseTok };

                if (baseTok.Length >= 4)
                {
                    if (char.ToLowerInvariant(baseTok[0]) == char.ToLowerInvariant(baseTok[1]))
                        variants.Add(baseTok.Substring(1));
                    else
                        variants.Add(baseTok.Substring(1));
                }

                expandedParts.Add(variants.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }

            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            results.Add(raw);
            results.Add(string.Join(' ', parts.Select(CanonicalizeStutterToken)));

            foreach (var list in expandedParts)
                foreach (var v in list)
                    results.Add(v);

            return results.Where(x => !string.IsNullOrWhiteSpace(x)).Take(6).ToList();
        }

        #endregion

        #region ===== Cambio de Pestaña =====

        private void SetTabTitle(string title)
        {
            title = (title ?? "").Trim();
            if (title.Length > 28) title = title.Substring(0, 28) + "…";
            if (string.IsNullOrWhiteSpace(title)) title = "Buscar";
            TabTitleChanged?.Invoke(this, title);
        }

        public SearchTabState GetTabState()
        {
            if (_stagedSearchState != null) return _stagedSearchState;
            return new SearchTabState
            {
                Header = "",
                Query = (SearchBox?.Text ?? "").Trim(),
                CurrentFolder = _currentFolderPath ?? "",
                Criteria = CaptureSearchCriteria()
            };
        }

        public async Task RestoreTabStateAsync(SearchTabState s)
        {
            if (s == null) return;

            _currentFolderPath = (s.CurrentFolder ?? "").Trim();
            ApplySearchCriteria(s.Criteria);
            _defaultTagAppliedOnce = true;
            _allowProgrammaticSearch = true;
            SearchBox.Text = s.Query ?? "";
            _allowProgrammaticSearch = false;

            if (!string.IsNullOrWhiteSpace(s.Query))
            {
                await RunSearchImmediateAsync(s.Query);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
            {
                await BrowseFolderAsync(_currentFolderPath, pushHistory: false);
                return;
            }

            await RunSearchAsync(s.Query ?? "");
        }

        private async Task RunSearchImmediateAsync(string query)
        {
            if (!App.LocalIndex.HasData) return;
            CancelRefreshWork();
            await RunSearchAsync(query);
        }

        private async Task RunSearchNowAsync(string query)
            => await RunSearchAsync(query);

        #endregion

        #region ===== Help (Popup) =====

        private void MenuHelp_Click(object sender, RoutedEventArgs e)
        {
            if (HelpPopup.IsOpen) { HelpPopup.IsOpen = false; return; }
            HelpContentHost.Content = BuildHelpContentNav();
            HelpPopup.XamlRoot = this.XamlRoot;
            HelpPopup.IsOpen = true;
        }

        private void HelpPopupClose_Click(object sender, RoutedEventArgs e)
            => HelpPopup.IsOpen = false;

        private UIElement BuildHelpContentNav()
        {
            _helpBodyHost = new ContentControl { Content = BuildHelpExamples() };

            var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            nav.Children.Add(MakeNavButton("Ejemplos", () => _helpBodyHost.Content = BuildHelpExamples(), isActive: true));
            nav.Children.Add(MakeNavButton("Operadores", () => _helpBodyHost.Content = BuildHelpOperators()));
            nav.Children.Add(MakeNavButton("Filtros", () => _helpBodyHost.Content = BuildHelpFilters()));
            nav.Children.Add(MakeNavButton("Tips", () => _helpBodyHost.Content = BuildHelpTips()));

            var bodyScroll = new ScrollViewer
            {
                Content = _helpBodyHost,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 8, 12, 0)
            };

            return new StackPanel { Spacing = 12, Children = { nav, bodyScroll } };
        }

        private ToggleButton MakeNavButton(string text, Action onClick, bool isActive = false)
        {
            var t = new ToggleButton { Content = text, IsChecked = isActive, Padding = new Thickness(14, 6, 14, 6), CornerRadius = new CornerRadius(10), MinWidth = 110 };
            t.Click += (_, __) =>
            {
                if (t.Parent is Panel p)
                    foreach (var c in p.Children)
                        if (c is ToggleButton tb) tb.IsChecked = false;
                t.IsChecked = true;
                onClick();
            };
            return t;
        }

        private UIElement CreateSection(string title, string content)
        {
            return new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 }
                }
            };
        }

        private UIElement CreateExampleRow(string example, string? note = null, bool run = true, bool replace = true)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var btn = new Button
            {
                Content = example,
                Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Left
            };
            btn.Click += (_, __) =>
            {
                SearchBox.Text = replace || string.IsNullOrWhiteSpace(SearchBox.Text)
                    ? example
                    : (SearchBox.Text?.Trim() ?? "") + " " + example;
                SearchBox.Focus(FocusState.Programmatic);
                TriggerSearchFromHelp(SearchBox.Text);
                HelpPopup.IsOpen = false;
            };
            row.Children.Add(btn);
            if (!string.IsNullOrWhiteSpace(note))
                row.Children.Add(new TextBlock { Text = note, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            return row;
        }

        private UIElement CreateTokenChip(string token, string? note = null)
        {
            var btn = new Button { Content = token, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(999), HorizontalAlignment = HorizontalAlignment.Left };
            btn.Click += (_, __) =>
            {
                var cur = (SearchBox.Text ?? "").Trim();
                SearchBox.Text = string.IsNullOrWhiteSpace(cur) ? token : cur + " " + token;
                SearchBox.Focus(FocusState.Programmatic);
                TriggerSearchFromHelp(SearchBox.Text);
            };

            if (string.IsNullOrWhiteSpace(note)) return btn;

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { btn, new TextBlock { Text = note, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } }
            };
        }

        private UIElement BuildHelpExamples()
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(CreateSection("Ejemplos rápidos", "Toca un ejemplo para colocarlo automáticamente en el buscador:"));
            stack.Children.Add(CreateExampleRow("reporte -SEO", "Excluye 'SEO' con guión"));
            stack.Children.Add(CreateExampleRow("reporte !SEO", "Excluye 'SEO' con signo de exclamación"));
            stack.Children.Add(CreateExampleRow("factura AND 2026", "Debe contener ambos términos"));
            stack.Children.Add(CreateExampleRow("\"estado de cuenta\"", "Frase exacta entre comillas"));
            stack.Children.Add(CreateExampleRow("aprtzzr|prtzzr|rtzzr bbria", "OR entre variantes + AND con otro término"));
            stack.Children.Add(CreateExampleRow("regex:^00act", "Archivos cuyo nombre empieza con '00act'"));
            stack.Children.Add(CreateExampleRow("regex:reporte.*(pdf|url)", "Regex: reporte seguido de pdf o url"));
            stack.Children.Add(CreateExampleRow("reporte AND febrero !SEO ext:pdf", "Ejemplo completo combinando todo"));
            return stack;
        }

        private UIElement BuildHelpOperators()
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(CreateSection("Operadores lógicos", "Combina términos para refinar la búsqueda:"));
            stack.Children.Add(CreateTokenChip("AND", "Ambos términos deben existir"));
            stack.Children.Add(CreateTokenChip("OR", "Cualquiera de los términos"));
            stack.Children.Add(CreateTokenChip("NOT", "Excluye un término"));
            stack.Children.Add(CreateTokenChip("-SEO", "Forma corta para excluir (NOT SEO)"));
            stack.Children.Add(CreateTokenChip("!SEO", "Igual que -SEO, estilo Everything"));
            stack.Children.Add(CreateTokenChip("a|b|c", "OR entre variantes separadas por |"));
            stack.Children.Add(CreateTokenChip("( A OR B )", "Agrupación con paréntesis"));
            return stack;
        }

        private UIElement BuildHelpFilters()
        {
            var stack = new StackPanel { Spacing = 12 };

            stack.Children.Add(CreateSection("Filtros de tipo", "Limita los resultados por extensión o tipo:"));
            stack.Children.Add(CreateTokenChip("ext:pdf", "Solo archivos PDF"));
            stack.Children.Add(CreateTokenChip("ext:pdf;docx;xlsx", "Varios tipos separados por ;"));
            stack.Children.Add(CreateTokenChip(".url", "Archivos .url (accesos directos web)"));
            stack.Children.Add(CreateTokenChip("type:folder", "Solo carpetas"));
            stack.Children.Add(CreateTokenChip("type:file", "Solo archivos"));

            stack.Children.Add(CreateSection("Filtros de ubicación", "Limita por ruta o carpeta:"));
            stack.Children.Add(CreateTokenChip("folder:finanzas", "Rutas que contengan 'finanzas'"));
            stack.Children.Add(CreateTokenChip("nopath:SEO", "Excluye resultados cuya ruta contenga 'SEO'"));

            stack.Children.Add(CreateSection("Regex", "Expresiones regulares para búsquedas avanzadas:"));
            stack.Children.Add(CreateTokenChip("regex:^00act", "Empieza con '00act'"));
            stack.Children.Add(CreateTokenChip("regex:\\d{4}-\\d{2}", "Patrón de fecha yyyy-mm"));
            stack.Children.Add(CreateTokenChip("regex:reporte.*(pdf|url)", "reporte seguido de pdf o url"));

            stack.Children.Add(CreateSection("Tamaño y fecha", ""));
            stack.Children.Add(CreateTokenChip("size:>10MB", "Archivos mayores a 10 MB"));
            stack.Children.Add(CreateTokenChip("dm:<=7", "Modificado hace 7 días o menos"));
            stack.Children.Add(CreateTokenChip("date:2025-01-01", "Modificado exactamente en esa fecha"));

            return stack;
        }

        private UIElement BuildHelpTips()
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(CreateSection("Tips", "Consejos para búsquedas más efectivas:"));
            stack.Children.Add(new TextBlock { Text = "• Usa comillas para buscar frases exactas: \"estado de cuenta\"", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "• Usa -palabra o !palabra para excluir resultados.", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "• Usa a|b|c para buscar variantes de una misma palabra en un solo token.", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "• nopath:carpeta excluye resultados que estén dentro de esa ruta.", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "• ext:pdf;docx busca varios tipos a la vez separando con ;", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "• regex: acepta cualquier expresión regular .NET. Por defecto ignora mayúsculas.", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "• Combina todo: a|b|c !excluir ext:pdf folder:proyectos", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(CreateExampleRow("aprtzzr|prtzzr bbria !SEO nopath:archivo ext:url", "Ejemplo avanzado estilo Everything"));
            return stack;
        }

        private void TriggerSearchFromHelp(string query)
        {
            EnsureSearchDebounce();

            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }
            if (!App.LocalIndex.HasData)
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                return;
            }

            _searchDebounceTimer!.Stop();
            _searchDebounceTimer.Start();
        }

        #endregion
    }
}
