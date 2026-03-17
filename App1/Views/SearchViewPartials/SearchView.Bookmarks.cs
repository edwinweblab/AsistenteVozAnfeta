using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Bookmarks =====

        private async Task ShowBookmarksAsync()
        {
            _mode = ViewMode.Bookmarks;
            Results.Clear();

            var list = _bookmarks ?? new System.Collections.Generic.List<BookmarkItem>();
            IEnumerable<BookmarkItem> items = list;

            var rawQuery = (SearchBox?.Text ?? "").Trim();
            var parsed = AdvancedQueryV3.Parse(rawQuery);
            UpdateHighlightTerms(rawQuery, parsed);

            if (!string.IsNullOrWhiteSpace(rawQuery))
            {
                if (!LooksAdvanced(rawQuery))
                {
                    var qq = rawQuery.ToLowerInvariant();
                    items = items.Where(b => (b.Title ?? "").ToLowerInvariant().Contains(qq));
                }
                else
                {
                    items = items.Where(b => AdvancedQueryV3.EvaluateWithPlan(parsed.Expr, new BookmarkView(b), parsed.Plan));
                }
            }

            // Evitar doble filtrado si ya se hizo arriba
            if (string.IsNullOrWhiteSpace(rawQuery))
                items = items.Where(b => AdvancedQueryV3.EvaluateWithPlan(parsed.Expr, new BookmarkView(b), parsed.Plan));

            if (!string.IsNullOrWhiteSpace(parsed.Plan.FolderContains))
            {
                var f = parsed.Plan.FolderContains.ToLowerInvariant();
                items = items.Where(b => (b.LocalPath ?? "").ToLowerInvariant().Contains(f));
            }

            if (_onlyFolders)
                items = items.Where(b => (b.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(_extFilter))
            {
                items = items.Where(b =>
                {
                    var ext = System.IO.Path.GetExtension(b.Title ?? "").TrimStart('.').ToLowerInvariant();
                    if (_extFilter == "img") return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";
                    return ext == _extFilter;
                });
            }

            items = _sortKey switch
            {
                "name_desc" => items.OrderByDescending(b => b.Title),
                _ => items.OrderBy(b => b.Title)
            };

            foreach (var b in items)
            {
                var localPath = (b.LocalPath ?? "").Trim();

                var row = new SearchResultRow
                {
                    Name = b.Title ?? "",
                    Target = localPath,
                    Type = b.Type ?? "",
                    Size = b.Size,
                    ServerModified = b.Modified ?? "",
                    Source = b.Source,
                    IsBookmarked = !string.IsNullOrWhiteSpace(localPath)
                                     && _bookmarksService.Exists(_bookmarks, localPath)
                };

                row.Icon = _iconService.GetIcon(row.Type, row.Target);
                Results.Add(row);
            }

            BreadcrumbText.Text = "Bookmarks";
            ModeText.Text = "Modo: Bookmarks";
            CountText.Text = $"{Results.Count} bookmarks";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            await Task.CompletedTask;
        }

        private async Task LoadBookmarksAsync()
        {
            try
            {
                _bookmarks = await _bookmarksService.LoadAsync(CancellationToken.None);
                RefreshBookmarksPanelUi();
                StatusText.Text = $"Estado: Bookmarks cargados ✅ ({_bookmarks.Count})";
            }
            catch (Exception ex)
            {
                _bookmarks = new();
                _bookmarksOc?.Clear();
                StatusText.Text = $"Estado: Error cargando bookmarks → {ex.Message}";
            }
        }

        /// <summary>
        /// ObservableCollection espejo para el BookmarksList del panel derecho.
        /// Cambia el binding en XAML: ItemsSource="{x:Bind _bookmarksOc, Mode=OneWay}"
        /// </summary>
        private readonly System.Collections.ObjectModel.ObservableCollection<BookmarkItem> _bookmarksOc = new();

        private void RefreshBookmarksPanelUi()
        {
            _bookmarksOc.Clear();
            foreach (var b in _bookmarks.OrderBy(b => b.Title))
                _bookmarksOc.Add(b);
        }

        private async void BtnStar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not SearchResultRow row) return;

            var path = (row.Target ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var ct = CancellationToken.None;
                var exists = _bookmarksService.Exists(_bookmarks, path);

                if (exists)
                {
                    _bookmarksService.RemoveByPath(_bookmarks, path);
                    await _bookmarksService.SaveAsync(_bookmarks, ct);
                    row.IsBookmarked = false;
                    StatusText.Text = "Estado: Bookmark eliminado ⭐❌";
                }
                else
                {
                    _bookmarks.Add(new BookmarkItem
                    {
                        Title = row.Name ?? "",
                        LocalPath = path,
                        Source = row.Source,
                        Type = row.Type ?? "",
                        Size = row.Size,
                        Modified = row.ServerModified ?? "",
                        Folder = "General",
                        CreatedAt = DateTimeOffset.Now
                    });
                    await _bookmarksService.SaveAsync(_bookmarks, ct);
                    row.IsBookmarked = true;
                    StatusText.Text = "Estado: Bookmark guardado ⭐✅";
                }

                RefreshBookmarksPanelUi();

                if (_mode == ViewMode.Bookmarks)
                    await ShowBookmarksAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error bookmark → {ex.Message}";
            }
        }

        private async void BtnBookmarkPanelStar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var path = (btn.Tag as string ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                _bookmarksService.RemoveByPath(_bookmarks, path);
                await _bookmarksService.SaveAsync(_bookmarks, CancellationToken.None);

                RefreshBookmarksPanelUi();
                StatusText.Text = "Estado: Bookmark eliminado ⭐❌";

                if (_mode == ViewMode.Bookmarks)
                    await ShowBookmarksAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error bookmark panel → {ex.Message}";
            }
        }

        // Abrir favorito desde el panel derecho (ItemClick del BookmarksList)
        private async void BookmarksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not BookmarkItem bm) return;

            var path = (bm.LocalPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path)) return;

            if ((bm.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(path))
                {
                    await BrowseFolderAsync(path, pushHistory: true);
                    StatusText.Text = "Estado: Carpeta abierta desde Favoritos 📁";
                }
                else
                {
                    StatusText.Text = "Estado: Carpeta no encontrada en local ❗";
                }
                return;
            }

            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                if (File.Exists(path) && NeedsHydration(path))
                {
                    StatusText.Text = "Estado: Descargando desde Dropbox… ⬇️";
                    var ok = await EnsureHydratedAsync(path, _cts.Token);
                    if (!ok)
                    {
                        StatusText.Text = "Estado: No se pudo descargar (timeout). Revisa tu conexión.";
                        return;
                    }
                }

                if (!File.Exists(path))
                {
                    StatusText.Text = "Estado: Archivo no encontrado en local ❗";
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });

                StatusText.Text = "Estado: Favorito abierto ✅";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error al abrir favorito → {ex.Message}";
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        #endregion
    }
}
