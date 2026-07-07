using Anfeta.UI.Models.Search;
using Anfeta.UI.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using System.Text.RegularExpressions;
namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private string _activeNotionBaseFilter = "";
        #region ===== Filters / Sort =====

        private async void ChipFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton chip) return;

            switch (chip.Name)
            {
                case nameof(ChipBookmarks): _onlyBookmarks = chip.IsChecked == true; break;
                case nameof(ChipFolders): _onlyFolders = chip.IsChecked == true; break;
            }

            string? newExt = chip.Name switch
            {
                nameof(ChipPdf) => chip.IsChecked == true ? "pdf" : null,
                nameof(ChipDocx) => chip.IsChecked == true ? "docx" : null,
                nameof(ChipXlsx) => chip.IsChecked == true ? "xlsx" : null,
                nameof(ChipImg) => chip.IsChecked == true ? "img" : null,
                nameof(ChipUrl) => chip.IsChecked == true ? "url" : null,
                _ => _extFilter
            };
            _extFilter = newExt;

            if (_extFilter != null)
            {
                if (chip.Name != nameof(ChipPdf)) ChipPdf.IsChecked = false;
                if (chip.Name != nameof(ChipDocx)) ChipDocx.IsChecked = false;
                if (chip.Name != nameof(ChipXlsx)) ChipXlsx.IsChecked = false;
                if (chip.Name != nameof(ChipImg)) ChipImg.IsChecked = false;
                if (chip.Name != nameof(ChipUrl)) ChipUrl.IsChecked = false;
            }

            if (_onlyBookmarks)
            {
                await ShowBookmarksAsync();
                FinishUi();
                return;
            }

            await RunLocalSearchAsync(SearchBox.Text ?? "");
            FinishUi();
        }

        private async void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SortCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
                _sortKey = tag;
            UpdateColumnSortIndicators();

            if (_onlyBookmarks)
                await ShowBookmarksAsync();
            else
                await RunLocalSearchAsync(SearchBox.Text ?? "");

            FinishUi();
        }

        private async void FilterTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingFilterCombo) return;
            if (FilterTypeCombo.SelectedItem is not ComboBoxItem item) return;

            var tag = (item.Tag as string ?? "").Trim().ToLowerInvariant();
            _isUpdatingFilterCombo = true;

            try
            {
                _onlyBookmarks = false;
                _onlyFolders = false;
                _extFilter = null;
                _mode = ViewMode.Explorer;

                ChipPdf.IsChecked = false;
                ChipDocx.IsChecked = false;
                ChipXlsx.IsChecked = false;
                ChipImg.IsChecked = false;
                ChipUrl.IsChecked = false;
                ChipRecent.IsChecked = false;
                ChipBookmarks.IsChecked = false;
                ChipFolders.IsChecked = false;

                switch (tag)
                {
                    case "all":
                        ModeText.Text = "Modo: Explorar";
                        var qAll = (SearchBox.Text ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(qAll))
                            await RunSearchAsync(qAll);
                        else
                        {
                            var folderToShow =
                                (!string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
                                    ? _currentFolder : DROPBOX_ROOT;
                            if (!string.IsNullOrWhiteSpace(folderToShow) && Directory.Exists(folderToShow))
                                await BrowseFolderAsync(folderToShow, pushHistory: false);
                        }
                        return;

                    case "pdf": ChipPdf.IsChecked = true; _extFilter = "pdf"; break;
                    case "docx": ChipDocx.IsChecked = true; _extFilter = "docx"; break;
                    case "xlsx": ChipXlsx.IsChecked = true; _extFilter = "xlsx"; break;
                    case "img": ChipImg.IsChecked = true; _extFilter = "img"; break;
                    case "url": ChipUrl.IsChecked = true; _extFilter = "url"; break;

                    case "bookmarks":
                        ChipBookmarks.IsChecked = true;
                        _onlyBookmarks = true;
                        await ShowBookmarksAsync();
                        FinishUi();
                        return;

                    case "folders":
                        ChipFolders.IsChecked = true;
                        _onlyFolders = true;
                        break;

                    case "recent":
                        ChipRecent.IsChecked = true;
                        StatusText.Text = "Estado: filtro 'Recientes' aÃºn no tiene lÃ³gica implementada";
                        break;

                    default: return;
                }

                var q = (SearchBox.Text ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(q))
                    await RunSearchAsync(q);
                else
                {
                    await RunLocalSearchAsync("");
                    FinishUi();
                }
            }
            finally
            {
                _isUpdatingFilterCombo = false;
            }
        }

        #endregion

        #region ===== Filtros Avanzados =====

        private async Task LoadSavedFiltersAsync(CancellationToken ct = default)
        {
            var items = await _savedFiltersService.GetAllAsync(ct);
            _savedFilters.Clear();
            foreach (var item in items)
                _savedFilters.Add(item);
            RefreshSavedFiltersUi();
        }

        private void RefreshSavedFiltersUi()
        {
            if (SavedFiltersEmptyHint != null)
                SavedFiltersEmptyHint.Visibility =
                    _savedFilters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task ApplySavedFilterAsync(SavedSearchFilter filter)
        {
            if (filter is null) return;

            var options = _savedFiltersService.ToExecutionOptions(filter);
            _currentMatchOptions = options.Match ?? new QueryMatchOptions();
            _sortKey = string.IsNullOrWhiteSpace(options.SortKey) ? "name_asc" : options.SortKey;
            SearchBox.Text = options.Query ?? string.Empty;

            await RunSearchAsync(options.Query ?? string.Empty);
        }

        private async Task ReloadSavedFiltersAsync() => await LoadSavedFiltersAsync();

        private async Task ShowCreateSavedFilterDialogAsync()
        {
            var dialog = new SavedSearchFilterDialog { XamlRoot = this.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            await _savedFiltersService.AddOrUpdateAsync(dialog.Filter);
            await LoadSavedFiltersAsync();
            RefreshSavedFiltersUi();
        }

        private async Task ShowEditSavedFilterDialogAsync(SavedSearchFilter filter)
        {
            if (filter is null) return;
            var dialog = new SavedSearchFilterDialog(filter) { XamlRoot = this.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            await _savedFiltersService.AddOrUpdateAsync(dialog.Filter);
            await LoadSavedFiltersAsync();
            RefreshSavedFiltersUi();
        }

        private async Task DeleteSavedFilterAsync(SavedSearchFilter filter)
        {
            if (filter is null) return;
            await _savedFiltersService.DeleteAsync(filter.Id);
            await LoadSavedFiltersAsync();
            RefreshSavedFiltersUi();
        }

        private async Task EditSavedFilterByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var filter = _savedFilters.FirstOrDefault(f => f.Id == id);
            if (filter is null) return;
            await ShowEditSavedFilterDialogAsync(filter);
        }

        private async Task DeleteSavedFilterByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var filter = _savedFilters.FirstOrDefault(f => f.Id == id);
            if (filter is null) return;
            await _savedFiltersService.DeleteAsync(id);
            await LoadSavedFiltersAsync();
            RefreshSavedFiltersUi();
        }

        private void ResetCurrentMatchOptions() => _currentMatchOptions = new QueryMatchOptions();

        private bool MatchesSavedFilterText(string source, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            if (string.IsNullOrWhiteSpace(source)) return false;
            var comparison = _currentMatchOptions.MatchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            return source.Contains(query, comparison);
        }

        private bool MatchesSavedFilterOnRow(Anfeta.UI.Models.Weblab.SearchResultRow row, string query)
        {
            if (row is null) return false;

            string target;

            if (row.Source == Anfeta.UI.Models.Weblab.SearchSource.Notion)
            {
                target = string.Join(" ", new[]
                {
            row.Name,
            row.Target,
            row.SearchText,
            row.Description
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            else
            {
                target = _currentMatchOptions.MatchPath
                    ? (row.Target ?? string.Empty)
                    : (row.Name ?? string.Empty);
            }

            return MatchesSavedFilterText(target, query);
        }

        private bool MatchesAutoAndQueryOnRow(
    Anfeta.UI.Models.Weblab.SearchResultRow row,
    string query)
        {
            if (row is null)
                return false;

            var terms = SplitAutoAndTerms(query);

            if (terms.Count == 0)
                return true;

            // AND automático:
            // Todas las palabras deben existir en el resultado.
            return terms.All(term => MatchesSavedFilterOnRow(row, term));
        }

        private static List<string> SplitAutoAndTerms(string query)
        {
            var q = (query ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(q))
                return new List<string>();

            // Soporta:
            // cliente monterrey diseño
            // "cliente monterrey" diseño
            var matches = Regex.Matches(q, "\"([^\"]+)\"|'([^']+)'|(\\S+)");

            return matches
                .Cast<Match>()
                .Select(m =>
                    m.Groups[1].Success ? m.Groups[1].Value :
                    m.Groups[2].Success ? m.Groups[2].Value :
                    m.Groups[3].Value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        // Handlers XAML
        private async void NewSavedFilter_Click(object sender, RoutedEventArgs e)
            => await ShowCreateSavedFilterDialogAsync();

        private async void SavedFiltersList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SavedSearchFilter filter)
                await ApplySavedFilterAsync(filter);
        }

        private async void SavedFilter_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not SavedSearchFilter filter) return;

            var flyout = new MenuFlyout();

            var apply = new MenuFlyoutItem { Text = "Aplicar" };
            apply.Click += async (_, __) => await ApplySavedFilterAsync(filter);

            var edit = new MenuFlyoutItem { Text = "Editar" };
            edit.Click += async (_, __) => await ShowEditSavedFilterDialogAsync(filter);

            var delete = new MenuFlyoutItem { Text = "Eliminar" };
            delete.Click += async (_, __) =>
            {
                await _savedFiltersService.DeleteAsync(filter.Id);
                await LoadSavedFiltersAsync();
                RefreshSavedFiltersUi();
            };

            flyout.Items.Add(apply);
            flyout.Items.Add(edit);
            flyout.Items.Add(delete);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteAll = new MenuFlyoutItem { Text = "Borrar todos los filtros" };
            deleteAll.Click += async (_, __) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Borrar todos los filtros",
                    Content = "Â¿Seguro que quieres eliminar todos los filtros guardados?",
                    PrimaryButtonText = "Borrar",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                await _savedFiltersService.DeleteAllAsync();
                await LoadSavedFiltersAsync();
                RefreshSavedFiltersUi();
                StatusText.Text = "Estado: filtros eliminados";
            };

            flyout.Items.Add(deleteAll);
            flyout.ShowAt(fe, e.GetPosition(fe));
        }

        private async void SavedFilterRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not SavedSearchFilter filter) return;

            var flyout = new MenuFlyout();
            var edit = new MenuFlyoutItem { Text = "Editar" };
            edit.Click += async (_, __) => await EditSavedFilterByIdAsync(filter.Id);
            var delete = new MenuFlyoutItem { Text = "Eliminar" };
            delete.Click += async (_, __) => await DeleteSavedFilterByIdAsync(filter.Id);

            flyout.Items.Add(edit);
            flyout.Items.Add(delete);
            flyout.ShowAt(fe, e.GetPosition(fe));
        }

        private async void SavedFilterMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var id = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(id)) return;

            var flyout = new MenuFlyout();
            var edit = new MenuFlyoutItem { Text = "Editar" };
            edit.Click += async (_, __) =>
            {
                var filter = _savedFilters.FirstOrDefault(f => f.Id == id);
                if (filter is null) return;
                await ShowEditSavedFilterDialogAsync(filter);
            };

            var delete = new MenuFlyoutItem { Text = "Eliminar" };
            delete.Click += async (_, __) =>
            {
                await _savedFiltersService.DeleteAsync(id);
                await LoadSavedFiltersAsync();
                RefreshSavedFiltersUi();
            };

            flyout.Items.Add(edit);
            flyout.Items.Add(delete);
            flyout.ShowAt(btn);
        }

        #endregion

        #region ===== Importar Filtros CSV =====

        private async Task ImportSavedFiltersFromCsvAsync(string filePath)
        {
            var imported = await _csvFilterImporter.ImportFromFileAsync(filePath);
            foreach (var filter in imported)
                await _savedFiltersService.AddOrUpdateAsync(filter);

            await LoadSavedFiltersAsync();
            RefreshSavedFiltersUi();
            StatusText.Text = $"Estado: {imported.Count} filtros importados";
        }

        private async void ImportFiltersCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = await _filePickerService.PickCsvFileAsync();
                if (path is null) return;
                await ImportSavedFiltersFromCsvAsync(path);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error al importar CSV: {ex.Message}";
            }
        }

        #endregion

        #region ===== Comandos Predefinidos (Sidebar) =====

        private sealed class SavedSearch
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Title { get; set; } = "";
            public string Description { get; set; } = "";
            public string Query { get; set; } = "";
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<SavedSearch> _savedSearches = new();

        private void RefreshCommandsSidebarUi()
        {
            if (CommandsSidebarEmptyHint != null)
                CommandsSidebarEmptyHint.Visibility =
                    _savedSearches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CommandsSidebarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CommandsSidebarList.SelectedItem is SavedSearch cmd)
            {
                SearchBox.Text = cmd.Query;
                await RunSearchAsync(cmd.Query);
                CommandsSidebarList.SelectedItem = null;
            }
        }

        private void BtnQuickSaveCommand_Click(object sender, RoutedEventArgs e)
            => BtnSaveSearch_Click(sender, e);

        private async void CommandsSidebarList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SavedSearch cmd)
            {
                SearchBox.Text = cmd.Query;
                await RunSearchAsync(cmd.Query);
                CommandsSidebarList.SelectedItem = null;
            }
        }

        private void LoadSavedSearches()
        {
            _savedSearches.Clear();
            var raw = ApplicationData.Current.LocalSettings.Values[LS_SavedSearches] as string;
            if (string.IsNullOrWhiteSpace(raw)) return;

            try
            {
                var list = JsonSerializer.Deserialize<System.Collections.Generic.List<SavedSearch>>(raw)
                           ?? new System.Collections.Generic.List<SavedSearch>();
                foreach (var it in list)
                {
                    if (string.IsNullOrWhiteSpace(it?.Query)) continue;
                    if (string.IsNullOrWhiteSpace(it.Title)) it.Title = it.Query;
                    _savedSearches.Add(it);
                }
            }
            catch
            {
                ApplicationData.Current.LocalSettings.Values[LS_SavedSearches] = "";
            }
        }

        private void SaveSavedSearches()
        {
            var list = _savedSearches.ToList();
            var raw = JsonSerializer.Serialize(list);
            ApplicationData.Current.LocalSettings.Values[LS_SavedSearches] = raw;
        }

        private void RefreshSavedSearchesUi()
        {
            if (CommandsSidebarList != null)
            {
                CommandsSidebarList.ItemsSource = null;
                CommandsSidebarList.ItemsSource = _savedSearches;
            }
            RefreshCommandsSidebarUi();
        }

        private void BtnDeleteSidebarCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not SavedSearch cmd) return;

            _savedSearches.Remove(cmd);
            SaveSavedSearches();
            if (CommandsSidebarList != null)
                CommandsSidebarList.SelectedItem = null;
            RefreshSavedSearchesUi();
        }

        private async void BtnEditSidebarCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not SavedSearch cmd) return;

            var existing = _savedSearches.FirstOrDefault(x => x.Id == cmd.Id);
            if (existing == null) return;

            var titleBox = new TextBox { PlaceholderText = "TÃ­tulo", Text = existing.Title ?? "" };
            var descBox = new TextBox { PlaceholderText = "DescripciÃ³n (opcional)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = existing.Description ?? "" };
            var queryBox = new TextBox { Text = existing.Query ?? "" };

            var panel = new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Text = "Editar comando", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, titleBox, descBox, new TextBlock { Text = "Query:" }, queryBox }
            };

            var dialog = new ContentDialog
            {
                Title = "Editar comando",
                Content = panel,
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var newTitle = (titleBox.Text ?? "").Trim();
            var newQuery = (queryBox.Text ?? "").Trim();
            var newDesc = (descBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(newTitle) || string.IsNullOrWhiteSpace(newQuery))
            {
                StatusText.Text = "Estado: TÃ­tulo y Query son obligatorios.";
                return;
            }
            if (_savedSearches.Any(x => x.Id != existing.Id &&
                string.Equals(x.Query, newQuery, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = "Estado: Ya existe otro comando con esa bÃºsqueda.";
                return;
            }

            existing.Title = newTitle;
            existing.Description = newDesc;
            existing.Query = newQuery;

            SaveSavedSearches();
            RefreshSavedSearchesUi();
            StatusText.Text = "Estado: Comando actualizado âœ…";
        }

        private async void BtnSaveSearch_Click(object sender, RoutedEventArgs e)
        {
            var currentQuery = (SearchBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(currentQuery))
            {
                StatusText.Text = "Estado: Escribe una bÃºsqueda antes de guardar.";
                return;
            }

            var titleBox = new TextBox { PlaceholderText = "TÃ­tulo (ej: Reportes PDF)", Text = currentQuery.Length > 24 ? currentQuery.Substring(0, 24) : currentQuery };
            var descBox = new TextBox { PlaceholderText = "DescripciÃ³n (opcional)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            var queryBox = new TextBox { Text = currentQuery };

            var panel = new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Text = "Guardar bÃºsqueda como comando", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, titleBox, descBox, new TextBlock { Text = "Query:" }, queryBox }
            };

            var dialog = new ContentDialog
            {
                Title = "Nuevo comando",
                Content = panel,
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var finalTitle = (titleBox.Text ?? "").Trim();
            var finalQuery = (queryBox.Text ?? "").Trim();
            var finalDesc = (descBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(finalTitle) || string.IsNullOrWhiteSpace(finalQuery))
            {
                StatusText.Text = "Estado: TÃ­tulo y Query son obligatorios.";
                return;
            }
            if (_savedSearches.Any(x => string.Equals(x.Query, finalQuery, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = "Estado: Ya existe un comando con esa bÃºsqueda.";
                return;
            }

            _savedSearches.Add(new SavedSearch { Title = finalTitle, Description = finalDesc, Query = finalQuery });
            SaveSavedSearches();
            RefreshSavedSearchesUi();
            StatusText.Text = "Estado: Comando guardado ðŸ’¾";
        }

        private void LoadSidebarExpandedStates()
        {
            var ls = ApplicationData.Current.LocalSettings.Values;
            if (ls.TryGetValue(LS_CommandsExpanded, out var c) && c is bool cb) CommandsExpander.IsExpanded = cb;
            if (ls.TryGetValue(LS_ExcludedExpanded, out var ex) && ex is bool eb) ExcludedExpander.IsExpanded = eb;

            CommandsExpander.Expanding += (_, __) => SaveSidebarExpandedStates();
            CommandsExpander.Collapsed += (_, __) => SaveSidebarExpandedStates();
            ExcludedExpander.Collapsed += (_, __) => SaveSidebarExpandedStates();
        }

        private void SaveSidebarExpandedStates()
        {
            var ls = ApplicationData.Current.LocalSettings.Values;
            ls[LS_CommandsExpanded] = CommandsExpander.IsExpanded;
            ls[LS_ExcludedExpanded] = ExcludedExpander.IsExpanded;
        }

        private void CommandRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not SavedSearch cmd) return;

            var flyout = new MenuFlyout();
            var edit = new MenuFlyoutItem { Text = "Editar" };
            edit.Click += (_, __) =>
            {
                var fakeButton = new Button { Tag = cmd };
                BtnEditSidebarCommand_Click(fakeButton, new RoutedEventArgs());
            };

            var del = new MenuFlyoutItem { Text = "Eliminar" };
            del.Click += (_, __) =>
            {
                var fakeButton = new Button { Tag = cmd };
                BtnDeleteSidebarCommand_Click(fakeButton, new RoutedEventArgs());
            };

            flyout.Items.Add(edit);
            flyout.Items.Add(del);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteAll = new MenuFlyoutItem { Text = "Borrar todos" };
            deleteAll.Click += async (_, __) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Borrar todos los comandos",
                    Content = "Â¿Seguro que quieres eliminar todos los comandos guardados?",
                    PrimaryButtonText = "Borrar",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                _savedSearches.Clear();
                SaveSavedSearches();
                if (CommandsSidebarList != null) CommandsSidebarList.SelectedItem = null;
                RefreshSavedSearchesUi();
                StatusText.Text = "Estado: comandos eliminados";
            };

            flyout.Items.Add(deleteAll);
            flyout.ShowAt(fe, e.GetPosition(fe));
        }

        private void CommandsSidebarItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is null) return;
            CommandsSidebarList.SelectedItem = btn.Tag;
        }

        private void SavedCommandRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var command = fe.DataContext;
            if (command is null) return;

            var flyout = new MenuFlyout();
            var edit = new MenuFlyoutItem { Text = "Editar" };
            edit.Click += (_, __) => BtnEditSidebarCommand_Click(command, new RoutedEventArgs());
            var del = new MenuFlyoutItem { Text = "Eliminar" };
            del.Click += (_, __) => BtnDeleteSidebarCommand_Click(command, new RoutedEventArgs());

            flyout.Items.Add(edit);
            flyout.Items.Add(del);
            flyout.ShowAt(fe, e.GetPosition(fe));
        }

        #endregion

        private List<Anfeta.UI.Models.Weblab.SearchResultRow> ApplyNotionBaseFilter(
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> rows)
        {
            var list = rows?.ToList() ?? new List<Anfeta.UI.Models.Weblab.SearchResultRow>();

            if (string.IsNullOrWhiteSpace(_activeNotionBaseFilter))
                return list;

            return list
                .Where(x =>
                    x.Source == Anfeta.UI.Models.Weblab.SearchSource.Notion &&
                    string.Equals(
                        x.ExternalSourceName,
                        _activeNotionBaseFilter,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        private async void ChipBaseFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
                return;

            var selectedBase = (clicked.Tag as string ?? string.Empty).Trim();

            _activeNotionBaseFilter = selectedBase;

            ChipBaseAll.IsChecked = clicked == ChipBaseAll;
            ChipBaseRevisiones.IsChecked = clicked == ChipBaseRevisiones;
            ChipBaseClientes.IsChecked = clicked == ChipBaseClientes;
            ChipBaseDominios.IsChecked = clicked == ChipBaseDominios;
            ChipBaseProgramas.IsChecked = clicked == ChipBaseProgramas;
            ChipBaseCobrar.IsChecked = clicked == ChipBaseCobrar;
            ChipBaseCorreos.IsChecked = clicked == ChipBaseCorreos;

            if (clicked == ChipBaseAll)
                _activeNotionBaseFilter = "";

            await PaintLoadedIndexAsync();
        }
        private async void HeaderNameSort_Click(object sender, RoutedEventArgs e)
        {
            _sortKey = _sortKey == "name_asc" ? "name_desc" : "name_asc";

            UpdateColumnSortIndicators();

            if (_onlyBookmarks)
                await ShowBookmarksAsync();
            else
                await RunLocalSearchAsync(SearchBox.Text ?? "");

            FinishUi();
        }

        private async void HeaderModifiedSort_Click(object sender, RoutedEventArgs e)
        {
            _sortKey = _sortKey == "mod_desc" ? "mod_asc" : "mod_desc";

            UpdateColumnSortIndicators();

            if (_onlyBookmarks)
                await ShowBookmarksAsync();
            else
                await RunLocalSearchAsync(SearchBox.Text ?? "");

            FinishUi();
        }

        private void UpdateColumnSortIndicators()
        {
            if (NameSortArrow == null || ModifiedSortArrow == null)
                return;

            NameSortArrow.Text = "";
            ModifiedSortArrow.Text = "";

            switch (_sortKey)
            {
                case "name_asc":
                    NameSortArrow.Text = "▲";
                    break;

                case "name_desc":
                    NameSortArrow.Text = "▼";
                    break;

                case "mod_desc":
                    ModifiedSortArrow.Text = "▼";
                    break;

                case "mod_asc":
                    ModifiedSortArrow.Text = "▲";
                    break;
            }
        }
    }
}
