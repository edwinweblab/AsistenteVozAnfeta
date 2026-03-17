using Anfeta.UI.Models;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Speech;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Acciones del Menú Contextual =====

        private async void CtxOpen_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            try
            {
                const int MAX_OPEN = 5;
                if (rows.Count > 1)
                {
                    var ok = await ConfirmOpenManyAsync(rows.Count, MAX_OPEN);
                    if (!ok) return;
                }

                var max = Math.Min(rows.Count, MAX_OPEN);
                for (int i = 0; i < max; i++)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = rows[i].Target,
                        UseShellExecute = true
                    });
                }

                StatusText.Text = rows.Count == 1
                    ? "Abierto ✅"
                    : $"Abiertos {Math.Min(rows.Count, MAX_OPEN)} de {rows.Count} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al abrir: {ex.Message}";
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

            var text = string.Join(Environment.NewLine, rows.Select(r => r.Name));
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            StatusText.Text = rows.Count == 1 ? "Copiado: nombre ✅" : $"Copiados {rows.Count} nombres ✅";
        }

        private void CtxCopyFullPath_Click(object sender, RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);
            if (row == null) return;

            try
            {
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(row.Target);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                StatusText.Text = "Copiado: ruta ✅";
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

        private void CtxOpenWeb_Click(object sender, RoutedEventArgs e) { }

        private void CtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var text = string.Join(Environment.NewLine, rows.Select(r => r.Target));
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            StatusText.Text = rows.Count == 1 ? "Copiado: ruta ✅" : $"Copiadas {rows.Count} rutas ✅";
        }

        private void CtxCopyLink_Click(object sender, RoutedEventArgs e)
        {
            var row = GetCtxRowOrSelected(sender);
            if (row == null) { StatusText.Text = "DEBUG: row null (copiar link)"; return; }

            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(row.Target);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            StatusText.Text = "Copiado ✅";
        }

        private async void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            var rows = GetSelectedRowsOrCtx(sender);
            if (rows.Count == 0) return;

            var ok = await ConfirmDeleteAsync(rows);
            if (!ok) return;

            try
            {
                if (rows.Count == 1)
                    await ApplyFileChangeAsync(FileChangeKind.Delete, rows[0]);
                else
                    await ApplyBatchDeleteAsync(rows);

                StatusText.Text = rows.Count == 1
                    ? "Estado: Eliminado ✅"
                    : $"Estado: Eliminados {rows.Count} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al eliminar: {ex.Message}";
            }
        }

        private void CtxBookmark_Click(object sender, RoutedEventArgs e) { }

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

                // 3) Persistir
                var snapshot = App.LocalIndex.GetAll();
                if (snapshot.Count == 0)
                    throw new InvalidOperationException("Índice quedó vacío: no se persistirá.");

                await LocalIndexPersistence.SaveAsync(DROPBOX_ROOT, snapshot, CancellationToken.None);

                // 4) Refresh UI
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

            var targetFolder = _currentFolderPath;
            if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
                targetFolder = DROPBOX_ROOT;

            await BrowseFolderAsync(targetFolder, pushHistory: false);
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
                .OfType<SearchResultRow>().ToList() ?? new List<SearchResultRow>();

            if (selected.Count == 0)
            {
                var row = GetCtxRowFromFlyout(sender) ?? ResultsList.SelectedItem as SearchResultRow;
                if (row == null) return;
                selected.Add(row);
            }

            if (selected.Count == 1)
            {
                var row = selected[0];
                var newName = await PromptRenameAsync(row.Name);
                if (string.IsNullOrWhiteSpace(newName) ||
                    string.Equals(newName, row.Name, StringComparison.Ordinal)) return;

                var dir = Path.GetDirectoryName(row.Target) ?? DROPBOX_ROOT;
                var newFullPath = Path.Combine(dir, newName.Trim());

                try
                {
                    await ApplyFileChangeAsync(FileChangeKind.Rename, row, newFullPath);
                    StatusText.Text = "Estado: Renombrado ✅";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Error al renombrar: {ex.Message}";
                }
                return;
            }

            try { await ShowBatchRenameDialogAsync(selected); }
            catch (Exception ex) { StatusText.Text = $"Error en renombrado múltiple: {ex.Message}"; }
        }

        private async Task ShowBatchRenameDialogAsync(List<SearchResultRow> rows)
        {
            var oldBox = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, Height = 140, Text = string.Join(Environment.NewLine, rows.Select(r => r.Name)) };
            var fmtBox = new TextBox { PlaceholderText = "Ej: {name} ({n}){ext}", Text = "{name} ({n}){ext}", MinWidth = 260 };

            var history = LoadBatchRenameHistory();
            if (history.Count > 0) fmtBox.Text = history[0];

            var presetsBtn = new Button { Content = "▼", Width = 38, Height = 32 };
            var fly = new MenuFlyout();

            void RebuildFlyout()
            {
                fly.Items.Clear();
                fly.Items.Add(new MenuFlyoutItem { Text = "Presets", IsEnabled = false });
                foreach (var p in BatchRenamePresets)
                {
                    var item = new MenuFlyoutItem { Text = p.Title };
                    item.Click += (_, __) => fmtBox.Text = p.Format;
                    fly.Items.Add(item);
                }

                fly.Items.Add(new MenuFlyoutSeparator());

                var freshHistory = LoadBatchRenameHistory();
                if (freshHistory.Count == 0)
                {
                    fly.Items.Add(new MenuFlyoutItem { Text = "Historial vacío", IsEnabled = false });
                }
                else
                {
                    fly.Items.Add(new MenuFlyoutItem { Text = "Historial", IsEnabled = false });
                    foreach (var h in freshHistory)
                    {
                        var label = h.Length > 42 ? h.Substring(0, 42) + "…" : h;
                        var item = new MenuFlyoutItem { Text = label };
                        item.Click += (_, __) => fmtBox.Text = h;
                        fly.Items.Add(item);
                    }

                    fly.Items.Add(new MenuFlyoutSeparator());
                    var clear = new MenuFlyoutItem { Text = "Limpiar historial" };
                    clear.Click += (_, __) => { SaveBatchRenameHistory(new List<string>()); RebuildFlyout(); };
                    fly.Items.Add(clear);
                }
            }

            RebuildFlyout();
            presetsBtn.Flyout = fly;
            presetsBtn.Click += (_, __) => RebuildFlyout();

            var keepExt = new CheckBox { Content = "Mantener extensión original", IsChecked = true };
            var startNumber = new NumberBox { Minimum = 1, Value = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 120 };
            var previewBox = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, Height = 140 };
            var errorText = new TextBlock { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };

            void RefreshPreview()
            {
                var (preview, error) = BatchRename_Preview(rows, fmtBox.Text, (int)startNumber.Value, keepExt.IsChecked == true);
                previewBox.Text = string.Join(Environment.NewLine, preview);
                errorText.Text = error ?? "";
                errorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
            }

            fmtBox.TextChanged += (_, __) => RefreshPreview();
            keepExt.Checked += (_, __) => RefreshPreview();
            keepExt.Unchecked += (_, __) => RefreshPreview();
            startNumber.ValueChanged += (_, __) => RefreshPreview();

            RefreshPreview();

            var root = new StackPanel { Spacing = 10 };
            root.Children.Add(new TextBlock { Text = "Old filenames:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            root.Children.Add(oldBox);

            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row1.Children.Add(new TextBlock { Text = "New format:", VerticalAlignment = VerticalAlignment.Center });
            row1.Children.Add(fmtBox);
            row1.Children.Add(presetsBtn);
            root.Children.Add(row1);

            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            row2.Children.Add(keepExt);
            row2.Children.Add(new TextBlock { Text = "Start:", VerticalAlignment = VerticalAlignment.Center });
            row2.Children.Add(startNumber);
            root.Children.Add(row2);

            root.Children.Add(new TextBlock { Text = "New filenames (preview):", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            root.Children.Add(previewBox);
            root.Children.Add(errorText);

            var dlg = new ContentDialog
            {
                Title = $"Renombrar ({rows.Count})",
                Content = root,
                PrimaryButtonText = "Aplicar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            void SyncApplyEnabled() => dlg.IsPrimaryButtonEnabled = string.IsNullOrWhiteSpace(errorText.Text);

            dlg.Opened += (_, __) => { RefreshPreview(); SyncApplyEnabled(); };
            fmtBox.TextChanged += (_, __) => SyncApplyEnabled();
            keepExt.Checked += (_, __) => SyncApplyEnabled();
            keepExt.Unchecked += (_, __) => SyncApplyEnabled();
            startNumber.ValueChanged += (_, __) => SyncApplyEnabled();

            var result = await dlg.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            AddFormatToHistory(fmtBox.Text);
            await ApplyBatchRenameAsync(rows, fmtBox.Text, (int)startNumber.Value, keepExt.IsChecked == true);
        }

        private (List<string> Preview, string? Error) BatchRename_Preview(List<SearchResultRow> rows, string format, int start, bool keepOriginalExtension)
        {
            format = format ?? "";
            if (string.IsNullOrWhiteSpace(format))
                return (new List<string>(), "El formato está vacío.");

            var preview = new List<string>(rows.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < rows.Count; i++)
            {
                var oldName = rows[i].Name ?? "";
                var (name, ext) = SplitNameExt(oldName);
                var n = start + i;
                var newName = ExpandFormat(format, oldName, name, ext, n, rows.Count);

                if (keepOriginalExtension)
                {
                    var (nn, _) = SplitNameExt(newName);
                    newName = nn + ext;
                }

                if (string.IsNullOrWhiteSpace(newName))
                    return (preview, "El formato produce nombres vacíos.");
                if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return (preview, $"Nombre inválido generado: {newName}");
                if (!seen.Add(newName))
                    return (preview, $"Duplicado en el lote: {newName}");

                preview.Add(newName);
            }

            return (preview, null);
        }

        private static (string Name, string Ext) SplitNameExt(string filename)
            => (Path.GetFileNameWithoutExtension(filename) ?? filename, Path.GetExtension(filename) ?? "");

        private static string ExpandFormat(string format, string full, string name, string ext, int n, int N)
        {
            var s = (format ?? "")
                .Replace("{full}", full)
                .Replace("{name}", name)
                .Replace("{ext}", ext)
                .Replace("{N}", N.ToString());

            s = Regex.Replace(s, @"\{n(?::(?<fmt>0+))?\}", m =>
            {
                var fmt = m.Groups["fmt"].Value;
                return !string.IsNullOrEmpty(fmt)
                    ? n.ToString(new string('0', fmt.Length))
                    : n.ToString();
            });

            return s;
        }

        private async Task ApplyBatchRenameAsync(List<SearchResultRow> rows, string format, int start, bool keepOriginalExtension)
        {
            var (preview, error) = BatchRename_Preview(rows, format, start, keepOriginalExtension);
            if (!string.IsNullOrWhiteSpace(error)) { StatusText.Text = $"Estado: Rename cancelado ❌ ({error})"; return; }

            int ok = 0, fail = 0;
            string? lastError = null;

            for (int i = 0; i < rows.Count; i++)
            {
                try
                {
                    var oldPath = rows[i].Target;
                    if (string.IsNullOrWhiteSpace(oldPath)) throw new Exception("Target vacío");

                    var newFullPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? DROPBOX_ROOT, preview[i]);
                    if (string.Equals(oldPath, newFullPath, StringComparison.OrdinalIgnoreCase)) continue;

                    await ApplyFileChangeAsync(FileChangeKind.Rename, rows[i], newFullPath);
                    ok++;
                }
                catch (Exception ex) { fail++; lastError = ex.Message; }
            }

            try { await RunLocalSearchAsync(SearchBox.Text, CancellationToken.None); } catch { }

            StatusText.Text = fail == 0
                ? $"Estado: Renombrados ✅ ({ok})"
                : $"Estado: Renombrados ✅ ({ok}) | Fallaron ❌ ({fail})" + (lastError != null ? $" | Último: {lastError}" : "");
        }

        private List<string> LoadBatchRenameHistory()
        {
            try
            {
                var ls = ApplicationData.Current.LocalSettings;
                if (ls.Values.TryGetValue(LS_BATCH_RENAME_HISTORY, out var obj) && obj is string json && !string.IsNullOrWhiteSpace(json))
                    return JsonSerializer.Deserialize<List<string>>(json)?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList() ?? new List<string>();
            }
            catch { }
            return new List<string>();
        }

        private void SaveBatchRenameHistory(List<string> items)
        {
            try
            {
                items = items.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
                    .Distinct().Take(BATCH_RENAME_HISTORY_MAX).ToList();
                ApplicationData.Current.LocalSettings.Values[LS_BATCH_RENAME_HISTORY] = JsonSerializer.Serialize(items);
            }
            catch { }
        }

        private void AddFormatToHistory(string format)
        {
            format = (format ?? "").Trim();
            if (string.IsNullOrWhiteSpace(format)) return;
            var list = LoadBatchRenameHistory();
            list.RemoveAll(x => string.Equals(x, format, StringComparison.Ordinal));
            list.Insert(0, format);
            SaveBatchRenameHistory(list);
        }

        private static readonly (string Title, string Format)[] BatchRenamePresets =
        {
            ("Numerar al final",  "{name} ({n}){ext}"),
            ("Numerar al inicio", "{n:00} - {name}{ext}"),
            ("Reporte fijo",      "Reporte_{n:000}{ext}"),
            ("Solo número",       "{n:000}{ext}"),
            ("Nombre + total",    "{name} {n} de {N}{ext}")
        };

        private async Task<string?> PromptRenameAsync(string currentName)
        {
            var tb = new TextBox { Text = currentName, Width = 320 };
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Renombrar",
                Content = tb,
                PrimaryButtonText = "Aceptar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? tb.Text : null;
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