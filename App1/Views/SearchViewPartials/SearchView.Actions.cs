using Anfeta.UI.Models;
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
            SeparatePages
        }

        private sealed record NotionUploadOptions(
            NotionUploadLayout Layout,
            string PageTitle,
            IReadOnlyList<string> SeparatePageTitles);

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
            string source)
        {
            const string notionTokenKey = "Notion.Token";

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    notionTokenKey] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura y guarda primero el token de Notion en Configuración.";
                return;
            }

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

            var suggestedTitle =
                validFiles.Count == 1
                    ? Path.GetFileNameWithoutExtension(
                        validFiles[0].Name)
                    : $"Archivos {DateTime.Now:yyyy-MM-dd HH-mm}";

            var options =
                await PromptNotionRevisionUploadOptionsAsync(
                    validFiles,
                    suggestedTitle);

            if (options == null)
                return;

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

        private async Task<NotionUploadOptions?> PromptNotionRevisionUploadOptionsAsync(
            IReadOnlyList<StorageFile> files,
            string suggestedTitle)
        {
            var titleBox = new TextBox
            {
                HorizontalAlignment =
                    HorizontalAlignment.Stretch,
                Text = suggestedTitle,
                PlaceholderText = "Título de la página"
            };

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

            var fileList = new TextBlock
            {
                Text = string.Join(
                    Environment.NewLine,
                    files.Take(12)
                        .Select(x => $"• {x.Name}")) +
                    (files.Count > 12
                        ? $"{Environment.NewLine}• … y {files.Count - 12} más"
                        : string.Empty),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82
            };

            var titleSection = new StackPanel
            {
                Spacing = 6
            };

            titleSection.Children.Add(
                new TextBlock
                {
                    Text = "Título de la página:",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            titleSection.Children.Add(titleBox);

            var selectedUploadTags =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            void AppendTagToEditor(
                TextBox editor,
                string tag)
            {
                var cleanTag = (tag ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(cleanTag))
                    return;

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
                        AppendTagToEditor(editor, tag);
                }
                else
                {
                    AppendTagToEditor(titleBox, tag);
                }
            }

            var quickTagsPanel = new StackPanel
            {
                Spacing = 7
            };

            quickTagsPanel.Children.Add(
                new TextBlock
                {
                    Text = "Tags más utilizados:",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

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
                    Tag = tag
                };

                button.Click += (_, __) =>
                    AppendTagToActiveTitles(tag);

                quickTagButtons.Children.Add(button);
            }

            quickTagsPanel.Children.Add(quickTagButtons);

            var personTagCombo = new ComboBox
            {
                PlaceholderText = "TAGS (personas)",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var tag in NotionUploadPersonTags)
            {
                personTagCombo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = tag,
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
                        Padding = new Thickness(8, 3, 8, 3)
                    };

                    button.Click += (_, __) =>
                        AppendTagToActiveTitles(tag);

                    recentPanel.Children.Add(button);
                }

                quickTagsPanel.Children.Add(recentPanel);
            }

            var content = new StackPanel
            {
                Width = 660,
                Spacing = 10
            };

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Archivos seleccionados: {files.Count}",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            content.Children.Add(
                new ScrollViewer
                {
                    Content = fileList,
                    MaxHeight = 150,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto
                });

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "Destino: Notion → Revisiones",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "¿Cómo deseas organizar los archivos?",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            content.Children.Add(onePageOption);
            content.Children.Add(separatePagesOption);
            content.Children.Add(titleSection);
            content.Children.Add(separateTitlesPanel);
            content.Children.Add(quickTagsPanel);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = files.Count == 1
                    ? "Subir archivo a Notion"
                    : "Subir varios archivos a Notion",
                Content = content,
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
                "ContentDialogMinWidth"] = 760d;

            void RefreshDialogState()
            {
                var separate =
                    separatePagesOption.IsChecked == true;

                titleSection.Visibility = separate
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                separateTitlesPanel.Visibility = separate
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                dialog.IsPrimaryButtonEnabled = separate
                    ? titleEditors.All(editor =>
                        !string.IsNullOrWhiteSpace(
                            editor.Text))
                    : !string.IsNullOrWhiteSpace(
                        titleBox.Text);
            }

            titleBox.TextChanged +=
                (_, __) => RefreshDialogState();

            foreach (var editor in titleEditors)
            {
                editor.TextChanged +=
                    (_, __) => RefreshDialogState();
            }

            onePageOption.Checked +=
                (_, __) => RefreshDialogState();

            separatePagesOption.Checked +=
                (_, __) => RefreshDialogState();

            dialog.Opened += (_, __) =>
            {
                RefreshDialogState();
                titleBox.Focus(
                    FocusState.Programmatic);
                titleBox.SelectAll();
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

            SaveNotionUploadRecentTags(
                selectedUploadTags);

            return new NotionUploadOptions(
                separatePagesOption.IsChecked == true
                    ? NotionUploadLayout.SeparatePages
                    : NotionUploadLayout.SinglePage,
                (titleBox.Text ??
                 string.Empty).Trim(),
                separateTitles);
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
                item.Tag is not string url ||
                string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var webUri))
            {
                StatusText.Text =
                    "Estado: La vista de Notion no tiene un enlace válido.";
                return;
            }

            var desktopUri =
                BuildNotionDesktopUri(webUri);

            try
            {
                var support =
                    await Launcher.QueryUriSupportAsync(
                        desktopUri,
                        LaunchQuerySupportType.Uri);

                if (support ==
                    LaunchQuerySupportStatus.Available)
                {
                    StatusText.Text =
                        $"Estado: Abriendo {item.Text} en Notion...";

                    if (await Launcher.LaunchUriAsync(
                            desktopUri))
                    {
                        StatusText.Text =
                            $"Estado: Vista abierta en Notion ✅ {item.Text}";
                        return;
                    }
                }

                var browserOpened =
                    await Launcher.LaunchUriAsync(
                        webUri);

                StatusText.Text = browserOpened
                    ? $"Estado: Vista abierta en navegador ✅ {item.Text}"
                    : "Estado: No fue posible abrir la vista.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: Error abriendo la vista → {ex.Message}";
            }
        }

        #endregion

        #region== Notion ==

        private async Task<bool> OpenNotionDesktopAsync(
            SearchResultRow row,
            bool allowBrowserFallback)
        {
            if (row == null || !IsNotionRow(row))
                return false;

            var webUrl = GetRowTarget(row);

            if (string.IsNullOrWhiteSpace(webUrl) ||
                !Uri.TryCreate(webUrl, UriKind.Absolute, out var webUri))
            {
                StatusText.Text =
                    "Estado: La página no tiene una URL válida de Notion.";
                return false;
            }

            var desktopUri =
                BuildNotionDesktopUri(webUri);

            LaunchQuerySupportStatus supportStatus;

            try
            {
                supportStatus =
                    await Launcher.QueryUriSupportAsync(
                        desktopUri,
                        LaunchQuerySupportType.Uri);
            }
            catch
            {
                supportStatus =
                    LaunchQuerySupportStatus.Unknown;
            }

            if (supportStatus ==
                LaunchQuerySupportStatus.Available)
            {
                try
                {
                    StatusText.Text =
                        "Estado: Abriendo en Notion Desktop...";

                    var desktopOpened =
                        await Launcher.LaunchUriAsync(
                            desktopUri);

                    if (desktopOpened)
                    {
                        StatusText.Text =
                            "Estado: Página abierta en Notion Desktop ✅";
                        return true;
                    }
                }
                catch
                {
                    // Notion estaba registrado, pero Windows no pudo iniciarlo.
                }
            }

            if (!allowBrowserFallback)
            {
                StatusText.Text =
                    "Estado: No fue posible abrir Notion Desktop.";
                return false;
            }

            return await PromptOpenNotionInBrowserAsync(
                webUri,
                supportStatus);
        }

        private static Uri BuildNotionDesktopUri(Uri webUri)
        {
            var absolute = webUri.AbsoluteUri;

            if (absolute.StartsWith(
                    "notion://",
                    StringComparison.OrdinalIgnoreCase))
            {
                return webUri;
            }

            var hostAndPath = absolute
                .Replace(
                    "https://",
                    string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    "http://",
                    string.Empty,
                    StringComparison.OrdinalIgnoreCase);

            return new Uri(
                $"notion://{hostAndPath}",
                UriKind.Absolute);
        }

        private async Task<bool> PromptOpenNotionInBrowserAsync(
            Uri webUri,
            LaunchQuerySupportStatus supportStatus)
        {
            var content = new StackPanel
            {
                Spacing = 8
            };

            var reason = supportStatus switch
            {
                LaunchQuerySupportStatus.AppNotInstalled =>
                    "No se encontró una aplicación instalada para abrir enlaces de Notion.",
                LaunchQuerySupportStatus.AppUnavailable =>
                    "Notion Desktop está instalado, pero no se encuentra disponible en este momento.",
                LaunchQuerySupportStatus.NotSupported =>
                    "Windows no tiene una aplicación asociada al protocolo de Notion.",
                _ =>
                    "No fue posible abrir la aplicación de escritorio de Notion."
            };

            content.Children.Add(
                new TextBlock
                {
                    Text = reason,
                    TextWrapping = TextWrapping.Wrap
                });

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "Verifica que Notion Desktop esté instalado y que la opción " +
                        "“Abrir enlaces en la aplicación de escritorio” esté activa. " +
                        "También puedes abrir esta página en el navegador.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                });

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Notion Desktop no disponible",
                Content = content,
                PrimaryButtonText = "Abrir en navegador",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                StatusText.Text =
                    "Estado: Apertura cancelada.";
                return false;
            }

            try
            {
                var browserOpened =
                    await Launcher.LaunchUriAsync(webUri);

                StatusText.Text = browserOpened
                    ? "Estado: Página abierta en el navegador ✅"
                    : "Estado: No fue posible abrir la página.";

                return browserOpened;
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: Error abriendo la página → {ex.Message}";
                return false;
            }
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
            return new SearchTabState
            {
                Header = "",
                Query = (SearchBox?.Text ?? "").Trim(),
                CurrentFolder = _currentFolderPath ?? ""
            };
        }

        public async Task RestoreTabStateAsync(SearchTabState s)
        {
            if (s == null) return;

            _currentFolderPath = (s.CurrentFolder ?? "").Trim();
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

            if (!string.IsNullOrWhiteSpace(DROPBOX_ROOT) && Directory.Exists(DROPBOX_ROOT))
                await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);
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