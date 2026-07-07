using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Results / Details / Open =====

        private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;
            BtnDetailsLocation.Content = IsNotionRow(row)
    ? "Abrir Notion"
    : "Ubicación";
            if (ResultsList.SelectedIndex >= 0)
                _dictIndex = ResultsList.SelectedIndex;

            DetailsTitle.Text = row.Name;
            DetailsPath.Text = row.Target;

            if (IsNotionRow(row))
            {
                var baseName = string.IsNullOrWhiteSpace(row.ExternalSourceName)
                     ? "Notion"
                     : row.ExternalSourceName;

                DetailsMeta.Text =
                    $"Tipo: {row.Type}\n" +
                    $"Origen: Notion\n" +
                    $"Base: {baseName}\n" +
                    $"Estado: Página de Notion\n" +
                    $"Modificado: {(!string.IsNullOrWhiteSpace(row.ServerModified) ? row.ServerModified : "—")}";
                StatusText.Text = "Estado: Página de Notion seleccionada ✅";
                return;
            }

            var online = File.Exists(row.Target) && NeedsHydration(row.Target);

            DetailsMeta.Text =
                $"Tipo: {row.Type}\n" +
                $"Estado: {(online ? "Online-only (se descarga al abrir)" : "Disponible local")}\n" +
                $"Tamaño: {(row.Size > 0 ? $"{row.Size / 1024:N0} KB" : "—")}\n" +
                $"Modificado: {(!string.IsNullOrWhiteSpace(row.ServerModified) ? row.ServerModified : "—")}";

            StatusText.Text = (row.Type ?? "").Equals("folder", StringComparison.OrdinalIgnoreCase)
                ? "Estado: Es carpeta (usa acciones de navegación) 📁"
                : "Estado: Seleccionado ✅";
        }

        private async void ResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;
            if (string.IsNullOrWhiteSpace(row.Target)) return;

            if (IsNotionRow(row))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = row.Target,
                        UseShellExecute = true
                    });

                    StatusText.Text = "Estado: Página de Notion abierta ✅";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: Error abriendo Notion → {ex.Message}";
                }

                return;
            }

            if ((row.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase))
            {
                await BrowseFolderAsync(row.Target);
                StatusText.Text = "Estado: Carpeta abierta 📁";
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                if (NeedsHydration(row.Target))
                {
                    StatusText.Text = "Estado: Descargando desde Dropbox… ⬇️";
                    var ok = await EnsureHydratedAsync(row.Target, _cts.Token);
                    if (!ok)
                    {
                        StatusText.Text = "Estado: No se pudo descargar (timeout). Revisa tu conexión/Dropbox.";
                        return;
                    }
                }

                StatusText.Text = "Estado: Abriendo…";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = row.Target,
                    UseShellExecute = true
                });
                StatusText.Text = "Estado: Archivo abierto ✅";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StatusText.Text = $"Estado: Error → {ex.Message}"; }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private async Task OpenSelectedAsync()
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;
            if (string.IsNullOrWhiteSpace(row.Target)) return;

            if (IsNotionRow(row))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = row.Target,
                        UseShellExecute = true
                    });

                    StatusText.Text = "Estado: Página de Notion abierta ✅";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: Error abriendo Notion → {ex.Message}";
                }

                return;
            }

            if ((row.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = row.Target,
                    UseShellExecute = true
                });
                StatusText.Text = "Estado: Carpeta abierta 📁";
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                if (NeedsHydration(row.Target))
                {
                    StatusText.Text = "Estado: Descargando desde Dropbox… ⬇️";
                    var ok = await EnsureHydratedAsync(row.Target, _cts.Token);
                    if (!ok)
                    {
                        StatusText.Text = "Estado: No se pudo descargar (timeout).";
                        return;
                    }
                }

                StatusText.Text = "Estado: Abriendo…";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = row.Target,
                    UseShellExecute = true
                });
                StatusText.Text = "Estado: Archivo abierto ✅";
            }
            catch (Exception ex) { StatusText.Text = $"Estado: Error → {ex.Message}"; }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnDetailsLink_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultRow row) return;
            if (string.IsNullOrWhiteSpace(row.Target)) return;

            if (IsNotionRow(row))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = row.Target,
                        UseShellExecute = true
                    });

                    StatusText.Text = "Estado: Página de Notion abierta ✅";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: Error abriendo Notion → {ex.Message}";
                }

                return;
            }

            var path = row.Target;

            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                StatusText.Text = "Estado: Mostrando archivo en carpeta 📁";
                return;
            }

            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                StatusText.Text = "Estado: Carpeta abierta 📁";
                return;
            }

            StatusText.Text = "Estado: No existe en local (pulsa doble tap para descargar) ❗";
        }

        private void ResultsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // El ContextFlyout ya existe en XAML
        }

        private void BtnDetailsInfo_Click(object sender, RoutedEventArgs e) { }

        private void PageSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        #endregion

        #region ==== Notion ==== 
        private static bool IsNotionRow(SearchResultRow row)
        {
            return row.Source == SearchSource.Notion ||
                   string.Equals(row.Type, "NOTION_PAGE", StringComparison.OrdinalIgnoreCase);
        }

        #endregion



        #region ===== Utils (Highlight / Hydration / Helpers) =====

        private static bool NeedsHydration(string path)
        {
            try
            {
                var attrs = File.GetAttributes(path);
                var flags = (int)attrs;
                return (flags & FILE_ATTRIBUTE_OFFLINE) != 0
                    || (flags & FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0
                    || (flags & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0;
            }
            catch { return true; }
        }

        private async Task<bool> EnsureHydratedAsync(string path, CancellationToken ct)
        {
            if (!NeedsHydration(path)) return true;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[1];
                _ = await fs.ReadAsync(buffer, 0, 1, ct);
            }
            catch { }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int timeout = 120_000;
            const int poll = 600;

            while (sw.ElapsedMilliseconds < timeout)
            {
                ct.ThrowIfCancellationRequested();
                if (!NeedsHydration(path))
                {
                    try { if (new FileInfo(path).Exists) return true; }
                    catch { }
                }
                await Task.Delay(poll, ct);
            }

            return false;
        }

        private static bool LooksAdvanced(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return false;
            if (q.Contains('"') || q.Contains('|') || q.Contains('(') || q.Contains(')')) return true;

            var up = q.ToUpperInvariant();
            if (up.Contains(" AND ") || up.Contains(" OR ") || up.Contains(" NOT ")) return true;
            if (q.StartsWith("-", StringComparison.Ordinal) || q.Contains(" -", StringComparison.Ordinal)) return true;

            // ! como NOT inline (Everything: reporte !SEO)
            if (q.Contains('!')) return true;

            // | como OR inline (Everything: aprtzzr|prtzzr|rtzzr)
            // ya está arriba en q.Contains('|') — solo lo dejamos documentado aquí

            string[] cmds = { "ext:", "type:", "folder:", "sort:", "limit:", "page:", "size:", "date:", "dm:", "year:", "month:", "name:", "path:", "nopath:", "regex:", "content:", "id:", "status:", "meta:", "author:", "creator:", "access:", "shared:" };
            foreach (var c in cmds)
                if (up.Contains(c.ToUpperInvariant(), StringComparison.Ordinal)) return true;

            return false;
        }

        private void UpdateHighlightTerms(string rawQuery, Anfeta.UI.Services.Search.ParsedQuery parsed)
        {
            rawQuery = (rawQuery ?? "").Trim();
            if (string.IsNullOrWhiteSpace(rawQuery)) { _highlightTerms = new(); return; }

            if (!LooksAdvanced(rawQuery)) { _highlightTerms = new() { rawQuery }; return; }

            var list = new System.Collections.Generic.List<string>();
            CollectHighlightTerms(parsed.Expr, list, insideNot: false);

            _highlightTerms = list
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(s => s.Length)
                .ToList();
        }

        private static void CollectHighlightTerms(Anfeta.UI.Services.Search.QNode? node, System.Collections.Generic.List<string> outList, bool insideNot)
        {
            if (node is null) return;
            switch (node)
            {
                case Anfeta.UI.Services.Search.TextTerm t:
                    if (!insideNot) outList.Add(t.Pattern);
                    break;
                case Anfeta.UI.Services.Search.Not n:
                    CollectHighlightTerms(n.X, outList, insideNot: true);
                    break;
                case Anfeta.UI.Services.Search.And a:
                    CollectHighlightTerms(a.L, outList, insideNot);
                    CollectHighlightTerms(a.R, outList, insideNot);
                    break;
                case Anfeta.UI.Services.Search.Or o:
                    CollectHighlightTerms(o.L, outList, insideNot);
                    CollectHighlightTerms(o.R, outList, insideNot);
                    break;
            }
        }

        private void ApplyHighlightToTextBlock(TextBlock tb, string text)
        {
            tb.Inlines.Clear();
            text ??= "";

            if (_highlightTerms == null || _highlightTerms.Count == 0 || text.Length == 0)
            {
                tb.Text = text;
                return;
            }

            int i = 0;
            while (i < text.Length)
            {
                int bestIndex = -1;
                string? bestNeedle = null;

                foreach (var n in _highlightTerms)
                {
                    var idx = text.IndexOf(n, i, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    if (bestIndex < 0 || idx < bestIndex) { bestIndex = idx; bestNeedle = n; if (bestIndex == i) break; }
                }

                if (bestIndex < 0 || bestNeedle is null)
                {
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text.Substring(i) });
                    break;
                }

                if (bestIndex > i)
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text.Substring(i, bestIndex - i) });

                tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = text.Substring(bestIndex, bestNeedle.Length),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemControlHighlightAccentBrush"]
                });

                i = bestIndex + bestNeedle.Length;
            }
        }

        private void NameText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is not TextBlock tb) return;

            if (args.NewValue is SearchResultRow row)
                ApplyHighlightToTextBlock(tb, row.DisplayName ?? "");
            else
                tb.Text = "";
        }

        private static string SafeFileName(string fullPath)
        {
            fullPath = (fullPath ?? "").Trim().TrimEnd('\\', '/');
            var name = System.IO.Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(name)) name = fullPath;
            return name;
        }

        #endregion
    }
}