using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Explorer / Tree / Navigation =====

        private void LoadFoldersRoot()
        {
            if (!Directory.Exists(DROPBOX_ROOT))
                throw new Exception($"No existe la ruta: {DROPBOX_ROOT}");
            BuildTreeRoot();
        }

        private void BuildTreeRoot()
        {
            _treeRoots.Clear();

            var root = new FolderNode
            {
                Name = Path.GetFileName(DROPBOX_ROOT),
                FullPath = DROPBOX_ROOT,
                HasDummyChild = true
            };
            root.Children.Add(new FolderNode { Name = "Cargando...", FullPath = "" });
            _treeRoots.Add(root);

            FolderTree.ItemsSource = _treeRoots;
            EmptyTreeHint.Visibility = _treeRoots.Count > 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async Task BrowseFolderAsync(string folder, bool pushHistory = true)
        {
            _currentFolderPath = folder;
            NotifyWorkspaceChanged();

            var q = (SearchBox?.Text ?? "").Trim();
            SetTabTitle(!string.IsNullOrWhiteSpace(q) ? q : Path.GetFileName(folder.TrimEnd('\\')));

            if (!Directory.Exists(folder))
            {
                StatusText.Text = "Estado: Carpeta no existe";
                return;
            }

            _mode = ViewMode.Explorer;
            _isBrowsing = true;
            _onlyBookmarks = false;
            _onlyFolders = false;
            _extFilter = null;

            if (pushHistory &&
                !string.IsNullOrWhiteSpace(_currentFolder) &&
                !string.Equals(_currentFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                _backStack.Push(_currentFolder);
                _forwardStack.Clear();
            }

            _currentFolder = folder;

            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            if (IsExcludedPath(folder))
            {
                Results.Clear();
                ResultsList.ItemsSource = Results;
                BreadcrumbText.Text = "Ruta: (excluida)";
                CountText.Text = "0 resultados";
                EmptyResultsHint.Visibility = Visibility.Visible;
                StatusText.Text = "Estado: Esta carpeta está excluida";
                return;
            }

            try
            {
                var pretty = folder.Equals(DROPBOX_ROOT, StringComparison.OrdinalIgnoreCase)
                    ? "/"
                    : folder.Replace(DROPBOX_ROOT, "").Replace("\\", "/");

                BreadcrumbText.Text = $"Ruta: {pretty}";
                ModeText.Text = "Modo: Explorar (Local)";

                Results.Clear();
                ResultsList.ItemsSource = null;

                // Carpetas primero
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(folder); }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: No pude leer carpetas → {ex.Message}";
                    return;
                }

                foreach (var dir in dirs)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    if (IsExcludedPath(dir)) continue;

                    string name;
                    try { name = new DirectoryInfo(dir).Name; }
                    catch { name = ""; }

                    if (string.IsNullOrWhiteSpace(name) || name == "—") continue;

                    var row = new SearchResultRow
                    {
                        Name = name,
                        Target = dir,
                        Type = "FOLDER",
                        Source = SearchSource.Local
                    };
                    row.Icon = _iconService.GetIcon(row.Type, row.Target);
                    Results.Add(row);
                }

                // Archivos
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(folder); }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: No pude leer archivos → {ex.Message}";
                    return;
                }

                foreach (var file in files)
                {
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    if (IsExcludedPath(file)) continue;

                    FileInfo fi;
                    try { fi = new FileInfo(file); }
                    catch { continue; }

                    var name = fi.Name ?? "";
                    if (string.IsNullOrWhiteSpace(name) || name == "—") continue;

                    var row = new SearchResultRow
                    {
                        Name = name,
                        Target = file,
                        Type = "FILE",
                        Size = fi.Exists ? fi.Length : 0,
                        ServerModified = fi.Exists ? fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") : "",
                        Source = SearchSource.Local
                    };
                    row.Icon = _iconService.GetIcon(row.Type, row.Target);
                    Results.Add(row);
                }

                ResultsList.ItemsSource = Results;
                CountText.Text = $"{Results.Count} resultados";
                EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                StatusText.Text = "Estado: Carpeta cargada ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error → {ex.Message}";
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }

            await Task.CompletedTask;
        }

        private async void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            if (args.Item is not FolderNode node) return;
            if (node.IsLoaded || !node.HasDummyChild) return;

            node.Children.Clear();
            node.HasDummyChild = false;

            await Task.Run(() =>
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(node.FullPath))
                    {
                        var child = new FolderNode
                        {
                            Name = Path.GetFileName(dir),
                            FullPath = dir
                        };

                        try
                        {
                            if (Directory.EnumerateDirectories(dir).Any())
                            {
                                child.HasDummyChild = true;
                                child.Children.Add(new FolderNode { Name = "Cargando...", FullPath = "" });
                            }
                        }
                        catch { }

                        DispatcherQueue.TryEnqueue(() => node.Children.Add(child));
                    }

                    DispatcherQueue.TryEnqueue(() => node.IsLoaded = true);
                }
                catch { }
            });
        }

        private async void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is not FolderNode node) return;
            if (string.IsNullOrWhiteSpace(node.FullPath)) return;

            CancelPendingSearch();
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                SearchBox.Text = "";

            await BrowseFolderAsync(node.FullPath);
        }

        private async void BtnPrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_backStack.Count == 0) return;
            var prev = _backStack.Pop();
            _forwardStack.Push(_currentFolder);
            await BrowseFolderAsync(prev, pushHistory: false);
        }

        private async void BtnNextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_forwardStack.Count == 0) return;
            var next = _forwardStack.Pop();
            _backStack.Push(_currentFolder);
            await BrowseFolderAsync(next, pushHistory: false);
        }

        private async void BtnRefreshTree_Click(object sender, RoutedEventArgs e)
        {
            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }
            if (!App.LocalIndex.HasData)
            {
                StatusText.Text = "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                return;
            }
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: Ruta inválida. Configura de nuevo en Settings.";
                return;
            }

            try
            {
                BuildTreeRoot();
                StatusText.Text = "Estado: Árbol actualizado ✅";

                if (_isBrowsing && !string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
                    await BrowseFolderAsync(_currentFolder, pushHistory: false);
                else
                    await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: Error → {ex.Message}";
            }
        }

        private async void BtnGoRoot_Click(object sender, RoutedEventArgs e)
        {
            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }
            if (!App.LocalIndex.HasData)
            {
                StatusText.Text = "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                return;
            }
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: Ruta inválida. Configura de nuevo en Settings.";
                return;
            }

            await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);
        }

        private void ToggleFoldersPane_Click(object sender, RoutedEventArgs e)
        {
            _foldersPaneVisible = ToggleFoldersPane.IsChecked == true;
            if (_foldersPaneVisible)
            {
                FoldersPane.Visibility = Visibility.Visible;
                FoldersPaneCol.Width = new GridLength(320);
            }
            else
            {
                FoldersPane.Visibility = Visibility.Collapsed;
                FoldersPaneCol.Width = new GridLength(0);
            }
        }

        private void ToggleDetailsPane_Click(object sender, RoutedEventArgs e)
        {
            var show = ToggleDetailsPane.IsChecked == true;
            if (show)
            {
                DetailsPane.Visibility = Visibility.Visible;
                DetailsCol.Width = new GridLength(340);
            }
            else
            {
                DetailsPane.Visibility = Visibility.Collapsed;
                DetailsCol.Width = new GridLength(0);
            }
        }

        #endregion
    }
}
