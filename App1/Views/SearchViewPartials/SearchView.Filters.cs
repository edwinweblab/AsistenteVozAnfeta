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
        // zPAGAR y zCOBRAR viven en la misma fuente de Notion, pero se
        // presentan como filtros independientes por título.
        private string _activePaymentBaseTitleFilter = "";
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

        private void GroupResultsCombo_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_loadingModulePreferences ||
                GroupResultsCombo.SelectedItem is not ComboBoxItem item)
            {
                return;
            }

            var tag = (item.Tag?.ToString() ?? "none")
                .Trim()
                .ToLowerInvariant();

            _resultGroupingMode = tag switch
            {
                "domain" => ResultGroupingMode.Domain,
                "name" => ResultGroupingMode.Name,
                _ => ResultGroupingMode.None
            };

            ApplicationData.Current.LocalSettings.Values[
                LS_ResultGroupingMode] = tag;

            ResultsList.SelectedItem = null;
            RefreshResultsListView();

            StatusText.Text =
                _resultGroupingMode == ResultGroupingMode.None
                    ? "Estado: Agrupación desactivada ✅"
                    : $"Estado: Resultados agrupados por " +
                      $"{GetGroupingModeLabel()} ✅";
        }

        private string GetGroupingModeLabel()
        {
            return _resultGroupingMode switch
            {
                ResultGroupingMode.Domain => "dominio",
                ResultGroupingMode.Name => "persona asignada",
                _ => "ninguno"
            };
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
                        StatusText.Text = "Estado: filtro 'Recientes' aún no tiene lÃ³gica implementada";
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
            if (row is null)
                return false;

            string target;

            if (row.Source == Anfeta.UI.Models.Weblab.SearchSource.Notion)
            {
                // En búsquedas normales de Notion se valida únicamente contra
                // el nombre visible del resultado. Así, con AND automático,
                // todas las palabras escritas deben existir en el título.
                target = row.DisplayName
                    ?? row.Name
                    ?? string.Empty;
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
                    Content = "¿Seguro que quieres eliminar todos los filtros guardados?",
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
        private readonly System.Collections.ObjectModel.ObservableCollection<SavedSearch> _visibleSavedSearches = new();
        private readonly System.Collections.ObjectModel.ObservableCollection<PredictiveSuggestion> _predictiveSuggestions = new();
        private bool _quickFlyoutOpen;
        private bool _quickFlyoutFocusRestoreQueued;

        private sealed class PredictiveSuggestion
        {
            public string Title { get; set; } = "";
            public string Subtitle { get; set; } = "";
            public string Query { get; set; } = "";
            public string Kind { get; set; } = "";
            public string IconGlyph { get; set; } = "\uE8A5";
        }

        private sealed class NotionBaseShortcut
        {
            public string PrimaryAlias { get; set; } = "";
            public string SourceName { get; set; } = "";
            public string PathLabel { get; set; } = "";
            public string DisplayLabel { get; set; } = "";
            public string TitleFilter { get; set; } = "";
            public string[] Aliases { get; set; } = Array.Empty<string>();
        }

        private sealed class NotionBaseScope
        {
            public bool HasBase { get; set; }
            public string PrimaryAlias { get; set; } = "";
            public string SourceName { get; set; } = "";
            public string PathLabel { get; set; } = "";
            public string DisplayLabel { get; set; } = "";
            public string TitleFilter { get; set; } = "";
            public string Remainder { get; set; } = "";
        }

        private static readonly string[] PredictiveStopWords =
        {
            "de", "del", "la", "las", "el", "los", "un", "una", "unos", "unas",
            "y", "o", "u", "a", "en", "con", "por", "para", "al", "que", "se",
            "su", "sus", "mi", "mis", "tu", "tus", "es", "son", "sin", "como",
            "web", "www", "com", "mx", "https", "http", "notion", "pagina", "página",
            "revision", "revisiones", "cliente", "clientes", "dominio", "dominios",
            "proyecto", "proyectos", "programa", "programas", "correo", "correos",
            "pagar", "cobrar", "contraseña", "contraseñas", "zrevision", "zrev",
            "zclientes", "zdominios", "zproyectos", "zcorreos", "zpagar", "zcobrar",
            "prtuzrevision"
        };

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

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            RefreshQuickFlyoutContent();
            // El foco automático al abrir una pestaña no debe desplegar el
            // predictivo ni tapar el calendario. Se abre al escribir.
        }

        private void SearchBox_Tapped(object sender, TappedRoutedEventArgs e)
        {
            RefreshQuickFlyoutContent();
            // Un clic solo coloca el cursor. Las sugerencias aparecen cuando
            // existe entrada real del usuario.
        }

        private void RefreshQuickFlyoutContent(string? input = null)
        {
            var rawInput = input ?? SearchBox?.Text ?? string.Empty;
            var q = rawInput.Trim();

            RebuildVisibleSavedSearches(q);
            RebuildPredictiveSuggestions(rawInput);
            UpdateQuickFlyoutVisibility();

            if (_quickFlyoutOpen)
                ResizeQuickCommandsFlyout();
        }

        private void ShowQuickCommandsInputFlyout()
        {
            RefreshQuickFlyoutContent();

            if (!ShouldShowQuickFlyout(SearchBox?.Text ?? string.Empty))
                return;

            ResizeQuickCommandsFlyout();

            if (!_quickFlyoutOpen)
                FlyoutBase.ShowAttachedFlyout(SearchBox);

            QueueSearchBoxFocusRestore();
        }

        private bool ShouldShowQuickFlyout(string query)
        {
            if (_visibleSavedSearches.Count == 0 && _predictiveSuggestions.Count == 0)
                return false;

            var q = (query ?? string.Empty).Trim();

            return string.IsNullOrWhiteSpace(q) ||
                   q.StartsWith("z", StringComparison.OrdinalIgnoreCase) ||
                   _predictiveSuggestions.Count > 0 ||
                   _visibleSavedSearches.Count > 0;
        }


        private void SyncBaseChipsFromQuery(string query)
        {
            if (ChipBaseAll == null)
                return;

            var scope = ResolveNotionBaseScope(query ?? string.Empty);

            if (scope.HasBase)
            {
                SetNotionBaseChipChecks(
                    scope.SourceName,
                    scope.TitleFilter);
                return;
            }

            SetNotionBaseChipChecks(
                _activeNotionBaseFilter ?? string.Empty,
                _activePaymentBaseTitleFilter);
        }

        private void SetNotionBaseChipChecks(
            string sourceName,
            string paymentTitleFilter = "")
        {
            var selected = (sourceName ?? string.Empty).Trim();
            var payment = (paymentTitleFilter ?? string.Empty).Trim();

            if (ChipBaseAll != null)
                ChipBaseAll.IsChecked = string.IsNullOrWhiteSpace(selected);

            if (ChipBaseRevisiones != null)
                ChipBaseRevisiones.IsChecked = string.Equals(selected, "Revisiones", StringComparison.OrdinalIgnoreCase);

            if (ChipBaseClientes != null)
                ChipBaseClientes.IsChecked = string.Equals(selected, "Clientes", StringComparison.OrdinalIgnoreCase);

            if (ChipBaseDominios != null)
                ChipBaseDominios.IsChecked = string.Equals(selected, "Dominios", StringComparison.OrdinalIgnoreCase);

            if (ChipBaseProgramas != null)
                ChipBaseProgramas.IsChecked = string.Equals(selected, "Programas y proyectos", StringComparison.OrdinalIgnoreCase);

            if (ChipBasePagar != null)
                ChipBasePagar.IsChecked =
                    string.Equals(selected, "Cobrar y pagar", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(payment, "PAGAR", StringComparison.OrdinalIgnoreCase);

            if (ChipBaseCobrar != null)
                ChipBaseCobrar.IsChecked =
                    string.Equals(selected, "Cobrar y pagar", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(payment, "COBRAR", StringComparison.OrdinalIgnoreCase);

            if (ChipBaseCorreos != null)
                ChipBaseCorreos.IsChecked = string.Equals(selected, "Correos Contraseñas", StringComparison.OrdinalIgnoreCase);
        }

        private void ResizeQuickCommandsFlyout()
        {
            if (QuickCommandsInputHost == null || SearchBox == null)
                return;

            var desiredWidth = Math.Max(760, SearchBox.ActualWidth);
            var rootWidth = XamlRoot?.Size.Width ?? 0;

            if (rootWidth > 0)
                desiredWidth = Math.Min(desiredWidth, Math.Max(520, rootWidth - 48));

            QuickCommandsInputHost.Width = desiredWidth;
            QuickCommandsInputHost.MinWidth = desiredWidth;
            QuickCommandsInputHost.MaxWidth = desiredWidth;
        }

        private void QuickCommandsInputFlyout_Opened(object sender, object e)
        {
            _quickFlyoutOpen = true;
            ApplyTextScaleToVisualTree();
            QueueSearchBoxFocusRestore();
        }

        private void QuickCommandsInputFlyout_Closed(object sender, object e)
        {
            _quickFlyoutOpen = false;
        }

        private void QueueSearchBoxFocusRestore()
        {
            if (_quickFlyoutFocusRestoreQueued)
                return;

            _quickFlyoutFocusRestoreQueued = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                _quickFlyoutFocusRestoreQueued = false;

                if (SearchBox == null)
                    return;

                var textBox = FindVisualChild<TextBox>(SearchBox);
                if (textBox != null)
                {
                    var caret = Math.Max(0, Math.Min(textBox.SelectionStart, textBox.Text?.Length ?? 0));
                    textBox.Focus(FocusState.Programmatic);
                    textBox.SelectionStart = caret;
                    textBox.SelectionLength = 0;
                    return;
                }

                SearchBox.Focus(FocusState.Programmatic);
            });
        }

        private void RebuildVisibleSavedSearches(string query)
        {
            _visibleSavedSearches.Clear();

            IEnumerable<SavedSearch> items = _savedSearches;
            var q = NormalizeSuggestionText(query);

            if (!string.IsNullOrWhiteSpace(q))
            {
                items = items.Where(x =>
                    NormalizeSuggestionText(x.Title).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    NormalizeSuggestionText(x.Query).Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var item in items.Take(24))
                _visibleSavedSearches.Add(item);

            if (QuickCommandsFlyoutList != null)
            {
                QuickCommandsFlyoutList.ItemsSource = null;
                QuickCommandsFlyoutList.ItemsSource = _visibleSavedSearches;
            }
        }

        private void RebuildPredictiveSuggestions(string query)
        {
            _predictiveSuggestions.Clear();

            if (_activeSourceScope == SearchSourceScope.Dropbox)
            {
                BindPredictiveSuggestions();
                return;
            }

            var q = (query ?? string.Empty).Trim();
            var normalized = NormalizeSuggestionText(q);
            var scope = ResolveNotionBaseScope(q);

            if (string.IsNullOrWhiteSpace(q))
            {
                AddBaseShortcutSuggestions("");
                BindPredictiveSuggestions();
                return;
            }

            if (scope.HasBase)
            {
                AddScopedPredictiveSuggestions(scope);
                BindPredictiveSuggestions();
                return;
            }

            if (IsPartialOrCompleteBaseAlias(normalized))
            {
                AddBaseShortcutSuggestions(q);
                BindPredictiveSuggestions();
                return;
            }

            AddGlobalPredictiveSuggestions(q);
            BindPredictiveSuggestions();
        }


        private static bool IsPartialOrCompleteBaseAlias(string value)
        {
            var normalized = NormalizeSuggestionText(value);

            if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(' '))
                return false;

            return GetNotionBaseShortcuts()
                .SelectMany(x => new[] { x.PrimaryAlias }.Concat(x.Aliases ?? Array.Empty<string>()))
                .Select(NormalizeSuggestionText)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Any(alias =>
                    alias.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private void BindPredictiveSuggestions()
        {
            if (QuickPredictiveFlyoutList != null)
            {
                QuickPredictiveFlyoutList.ItemsSource = null;
                QuickPredictiveFlyoutList.ItemsSource = _predictiveSuggestions;
            }
        }

        private void UpdateQuickFlyoutVisibility()
        {
            var hasPredictive = _predictiveSuggestions.Count > 0;
            var hasSaved = _visibleSavedSearches.Count > 0;

            if (QuickPredictiveHeaderText != null)
            {
                var scope = ResolveNotionBaseScope(SearchBox?.Text ?? "");
                QuickPredictiveHeaderText.Text = scope.HasBase
                    ? $"Sugerencias de {scope.PathLabel}"
                    : "Sugerencias predictivas";
                QuickPredictiveHeaderText.Visibility = hasPredictive ? Visibility.Visible : Visibility.Collapsed;
            }

            if (QuickPredictiveFlyoutList != null)
                QuickPredictiveFlyoutList.Visibility = hasPredictive ? Visibility.Visible : Visibility.Collapsed;

            if (QuickSavedHeaderText != null)
                QuickSavedHeaderText.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;

            if (QuickCommandsFlyoutList != null)
                QuickCommandsFlyoutList.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;

            if (QuickFlyoutHintText != null)
            {
                QuickFlyoutHintText.Text = hasPredictive || hasSaved
                    ? "Clic para buscar · Clic derecho en guardadas para editar o eliminar"
                    : "Escribe zrevision, zclientes, zdominios, zproyectos, zpagar o zcorreos";
            }
        }

        private async void QuickCommandsFlyoutList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not SavedSearch cmd)
                return;

            await ExecuteQuickQueryAsync(cmd.Query);
        }

        private async void QuickPredictiveFlyoutList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not PredictiveSuggestion suggestion)
                return;

            if (string.Equals(suggestion.Kind, "Base", StringComparison.OrdinalIgnoreCase))
            {
                await ExecuteQuickQueryAsync(suggestion.Query);
                return;
            }

            await ExecutePredictiveTermAsync(suggestion);
        }

        private async Task ExecuteQuickQueryAsync(string candidateQuery, bool hideFlyout = true)
        {
            var finalQuery = BuildMergedQuickQueryFromCurrent(candidateQuery);

            if (string.IsNullOrWhiteSpace(finalQuery))
                return;

            if (hideFlyout)
                QuickCommandsInputFlyout?.Hide();

            var scope = ResolveNotionBaseScope(finalQuery);
            var displayQuery = scope.HasBase && string.IsNullOrWhiteSpace(scope.Remainder)
                ? EnsureTagTrailingSpace(scope.PrimaryAlias)
                : finalQuery;

            _suppressSuggest = true;
            SearchBox.Text = displayQuery;
            _suppressSuggest = false;
            MoveSearchBoxCaretToEnd();

            await RunSearchAsync(finalQuery);
        }

        private async Task ExecutePredictiveTermAsync(PredictiveSuggestion suggestion, bool hideFlyout = true)
        {
            var insertText = (suggestion.Query ?? suggestion.Title ?? string.Empty).Trim();
            var finalQuery = BuildQueryByAppendingPredictiveTerm(SearchBox?.Text ?? string.Empty, insertText);

            if (string.IsNullOrWhiteSpace(finalQuery))
                return;

            if (hideFlyout)
                QuickCommandsInputFlyout?.Hide();

            SearchBox.Text = finalQuery;
            MoveSearchBoxCaretToEnd();

            await RunSearchAsync(finalQuery);
        }

        private string BuildQueryByAppendingPredictiveTerm(string currentQuery, string insertText)
        {
            var current = NormalizeSpacesForQuery(currentQuery);
            var insert = NormalizeSpacesForQuery(insertText);

            if (string.IsNullOrWhiteSpace(insert))
                return current;

            if (string.IsNullOrWhiteSpace(current))
                return insert;

            var currentTerms = SplitAutoAndTerms(current)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var insertTerms = SplitAutoAndTerms(insert)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (insertTerms.Count == 0)
                return current;

            var seen = currentTerms
                .Select(NormalizeSuggestionText)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var term in insertTerms)
            {
                var clean = (term ?? string.Empty).Trim();
                var norm = NormalizeSuggestionText(clean);

                if (string.IsNullOrWhiteSpace(norm))
                    continue;

                if (seen.Contains(norm))
                    continue;

                if (currentTerms.Count > 0)
                {
                    var lastIndex = currentTerms.Count - 1;
                    var last = currentTerms[lastIndex] ?? string.Empty;
                    var lastNorm = NormalizeSuggestionText(last);

                    // Autocompleta solo la última palabra escrita.
                    // Ejemplo: "zrevision br" + "bria" => "zrevision bria".
                    if (!string.IsNullOrWhiteSpace(lastNorm) &&
                        lastNorm.Length >= 2 &&
                        !IsKnownBaseAlias(lastNorm) &&
                        norm.StartsWith(lastNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        currentTerms[lastIndex] = clean;
                        seen.Add(norm);
                        continue;
                    }
                }

                currentTerms.Add(clean);
                seen.Add(norm);
            }

            return NormalizeSpacesForQuery(string.Join(" ", currentTerms));
        }

        private string BuildMergedQuickQueryFromCurrent(string candidateQuery)
        {
            var current = NormalizeSpacesForQuery(SearchBox?.Text ?? string.Empty);
            var candidate = NormalizeSpacesForQuery(candidateQuery);

            if (string.IsNullOrWhiteSpace(candidate))
                return current;

            if (string.IsNullOrWhiteSpace(current))
                return candidate;

            var currentNorm = NormalizeSuggestionText(current);
            var candidateNorm = NormalizeSuggestionText(candidate);

            if (string.Equals(currentNorm, candidateNorm, StringComparison.OrdinalIgnoreCase))
                return current;

            // Si el usuario escribió algo parcial como "zrev" y eligió "zrevision",
            // se completa el comando en vez de duplicarlo.
            if (candidateNorm.StartsWith(currentNorm + " ", StringComparison.OrdinalIgnoreCase))
                return candidate;

            // Evita que al seleccionar "Ver todo zrevision" se borre lo que ya escribió:
            // "zrevision neft" + "zrevision" => "zrevision neft".
            if (currentNorm.StartsWith(candidateNorm + " ", StringComparison.OrdinalIgnoreCase))
                return current;

            var candidateScope = ResolveNotionBaseScope(candidate);
            var currentScope = ResolveNotionBaseScope(current);

            if (candidateScope.HasBase)
            {
                var candidateRemainder = ExtractOriginalRemainderForScope(candidate, candidateScope);
                var candidateIsBaseOnly = string.IsNullOrWhiteSpace(candidateRemainder);

                if (currentScope.HasBase &&
                    string.Equals(currentScope.SourceName, candidateScope.SourceName, StringComparison.OrdinalIgnoreCase))
                {
                    var currentRemainder = ExtractOriginalRemainderForScope(current, currentScope);

                    if (candidateIsBaseOnly)
                    {
                        // Completa alias cortos: "zrev" => "zrevision".
                        if (string.IsNullOrWhiteSpace(currentRemainder))
                            return candidateScope.PrimaryAlias;

                        // Mantiene lo escrito: "zrevision neft" + "zrevision" => "zrevision neft".
                        return NormalizeSpacesForQuery($"{candidateScope.PrimaryAlias} {currentRemainder}");
                    }

                    var mergedRemainder = MergeQueryTerms(currentRemainder, candidateRemainder);
                    return NormalizeSpacesForQuery($"{candidateScope.PrimaryAlias} {mergedRemainder}");
                }

                if (IsCurrentPartialAliasForScope(current, candidateScope))
                    return candidateIsBaseOnly
                        ? candidateScope.PrimaryAlias
                        : candidate;

                if (currentScope.HasBase)
                    return MergeQueryTerms(current, candidate);

                if (candidateIsBaseOnly)
                    return NormalizeSpacesForQuery($"{candidateScope.PrimaryAlias} {current}");

                var mergedWithoutBase = MergeQueryTerms(current, candidateRemainder);
                return NormalizeSpacesForQuery($"{candidateScope.PrimaryAlias} {mergedWithoutBase}");
            }

            return MergeQueryTerms(current, candidate);
        }

        private string ExtractOriginalRemainderForScope(string query, NotionBaseScope scope)
        {
            var q = (query ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(q) || scope is null || !scope.HasBase)
                return string.Empty;

            var aliases = GetAliasesForScope(scope)
                .OrderByDescending(x => x.Length)
                .ToList();

            foreach (var alias in aliases)
            {
                if (q.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return string.Empty;

                if (q.StartsWith(alias + " ", StringComparison.OrdinalIgnoreCase))
                    return q.Substring(alias.Length).Trim();
            }

            return scope.Remainder ?? string.Empty;
        }

        private static IEnumerable<string> GetAliasesForScope(NotionBaseScope scope)
        {
            if (scope is null || !scope.HasBase)
                return Enumerable.Empty<string>();

            var shortcut = GetNotionBaseShortcuts()
                .FirstOrDefault(x =>
                    string.Equals(x.SourceName, scope.SourceName, StringComparison.OrdinalIgnoreCase));

            if (shortcut is null)
                return new[] { scope.PrimaryAlias };

            return new[] { shortcut.PrimaryAlias }
                .Concat(shortcut.Aliases ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsCurrentPartialAliasForScope(string current, NotionBaseScope scope)
        {
            var q = NormalizeSuggestionText(current);

            if (string.IsNullOrWhiteSpace(q) || q.Contains(' '))
                return false;

            return GetAliasesForScope(scope)
                .Select(NormalizeSuggestionText)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Any(alias =>
                    alias.StartsWith(q, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(alias, q, StringComparison.OrdinalIgnoreCase));
        }

        private static string MergeQueryTerms(params string[] pieces)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var piece in pieces ?? Array.Empty<string>())
            {
                foreach (var term in SplitAutoAndTerms(piece ?? string.Empty))
                {
                    var clean = (term ?? string.Empty).Trim();
                    var norm = NormalizeSuggestionText(clean);

                    if (string.IsNullOrWhiteSpace(norm))
                        continue;

                    if (seen.Add(norm))
                        result.Add(clean);
                }
            }

            return NormalizeSpacesForQuery(string.Join(" ", result));
        }

        private static string NormalizeSpacesForQuery(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        }

        private void MoveSearchBoxCaretToEnd()
        {
            var textBox = FindVisualChild<TextBox>(SearchBox);

            if (textBox != null)
            {
                textBox.Focus(FocusState.Programmatic);
                textBox.SelectionStart = textBox.Text?.Length ?? 0;
                textBox.SelectionLength = 0;
                return;
            }

            SearchBox.Focus(FocusState.Programmatic);
        }

        private void QuickCommandChip_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe)
                return;

            if (fe.DataContext is not SavedSearch cmd)
                return;

            var flyout = new MenuFlyout();

            var run = new MenuFlyoutItem { Text = "Buscar" };
            run.Click += async (_, __) =>
            {
                await ExecuteQuickQueryAsync(cmd.Query);
            };

            var edit = new MenuFlyoutItem { Text = "Editar" };
            edit.Click += (_, __) =>
            {
                QuickCommandsInputFlyout?.Hide();

                var fakeButton = new Button { Tag = cmd };
                BtnEditSidebarCommand_Click(fakeButton, new RoutedEventArgs());
            };

            var delete = new MenuFlyoutItem { Text = "Eliminar" };
            delete.Click += (_, __) =>
            {
                QuickCommandsInputFlyout?.Hide();

                var fakeButton = new Button { Tag = cmd };
                BtnDeleteSidebarCommand_Click(fakeButton, new RoutedEventArgs());
            };

            flyout.Items.Add(run);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(edit);
            flyout.Items.Add(delete);

            flyout.ShowAt(fe, e.GetPosition(fe));
            e.Handled = true;
        }

        private void AddBaseShortcutSuggestions(string partial)
        {
            var q = NormalizeSuggestionText(partial);

            var ordered = GetNotionBaseShortcuts()
                .Select(item => new
                {
                    Item = item,
                    Aliases = new[] { item.PrimaryAlias }
                        .Concat(item.Aliases ?? Array.Empty<string>())
                        .Select(NormalizeSuggestionText)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList(),
                    Haystack = NormalizeSuggestionText(string.Join(" ", new[]
                    {
                        item.PrimaryAlias,
                        item.DisplayLabel,
                        item.PathLabel,
                        item.SourceName,
                        string.Join(" ", item.Aliases)
                    }))
                })
                .Where(x => string.IsNullOrWhiteSpace(q) ||
                            x.Haystack.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => string.IsNullOrWhiteSpace(q) ? 0 :
                    x.Aliases.Any(alias => alias.StartsWith(q, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
                .ThenBy(x => x.Item.PrimaryAlias);

            foreach (var entry in ordered)
            {
                var item = entry.Item;
                _predictiveSuggestions.Add(new PredictiveSuggestion
                {
                    Title = item.PrimaryAlias,
                    Subtitle = $"Filtrar {item.PathLabel}",
                    Query = item.PrimaryAlias,
                    Kind = "Base",
                    IconGlyph = "\uE8B7"
                });
            }
        }

        private void AddScopedPredictiveSuggestions(NotionBaseScope scope)
        {
            var rows = App.LocalIndex.HasData
                ? App.LocalIndex.GetAll()
                    .Where(x =>
                        x.Source == Anfeta.UI.Models.Weblab.SearchSource.Notion &&
                        string.Equals(x.ExternalSourceName, scope.SourceName, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : new List<Anfeta.UI.Models.Weblab.SearchResultRow>();

            var remainder = (scope.Remainder ?? string.Empty).Trim();
            var currentQuery = SearchBox?.Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(remainder))
            {
                _predictiveSuggestions.Add(new PredictiveSuggestion
                {
                    Title = $"Ver todo {scope.PathLabel}",
                    Subtitle = $"{rows.Count} páginas en esta base",
                    Query = scope.PrimaryAlias,
                    Kind = "Base",
                    IconGlyph = ""
                });
            }

            if (rows.Count == 0)
                return;

            // El predictivo ya no mete títulos completos de páginas.
            // Solo propone palabras/temas que pueden seguir a lo escrito.
            foreach (var topic in BuildFrequentTopicSuggestions(rows, remainder, currentQuery).Take(28))
            {
                _predictiveSuggestions.Add(new PredictiveSuggestion
                {
                    Title = topic.Text,
                    Subtitle = topic.IsDomain
                        ? $"{scope.PathLabel} · dominio completo · aparece {topic.Count}x"
                        : $"{scope.PathLabel} · aparece {topic.Count}x",
                    Query = topic.Text,
                    Kind = topic.IsDomain ? "Domain" : "Topic",
                    IconGlyph = topic.IsDomain ? "\uE71B" : ""
                });
            }
        }

        private void AddGlobalPredictiveSuggestions(string query)
        {
            if (!App.LocalIndex.HasData)
                return;

            var q = NormalizeSuggestionText(query);
            if (string.IsNullOrWhiteSpace(q))
                return;

            var matchingRows = App.LocalIndex.GetAll()
                .Where(x => x.Source == Anfeta.UI.Models.Weblab.SearchSource.Notion)
                .Where(x => RowMatchesPredictiveText(x, query))
                .OrderBy(x => GetPathOrderRank(x))
                .ThenBy(x => x.DisplayName ?? x.Name)
                .ToList();

            if (matchingRows.Count == 0)
                return;

            foreach (var topic in BuildFrequentTopicSuggestions(matchingRows, string.Empty, query).Take(28))
            {
                _predictiveSuggestions.Add(new PredictiveSuggestion
                {
                    Title = topic.Text,
                    Subtitle = topic.IsDomain
                        ? $"Dominio completo · aparece {topic.Count}x"
                        : $"Sugerencia · aparece {topic.Count}x",
                    Query = topic.Text,
                    Kind = topic.IsDomain ? "Domain" : "Topic",
                    IconGlyph = topic.IsDomain ? "\uE71B" : ""
                });
            }
        }

        private NotionBaseScope ResolveNotionBaseScope(string query)
        {
            var q = (query ?? string.Empty).Trim();
            var normalized = NormalizeSuggestionText(q);

            if (string.IsNullOrWhiteSpace(normalized))
                return new NotionBaseScope();

            foreach (var shortcut in GetNotionBaseShortcuts())
            {
                var aliases = new[] { shortcut.PrimaryAlias }
                    .Concat(shortcut.Aliases ?? Array.Empty<string>())
                    .Select(NormalizeSuggestionText)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Length);

                foreach (var alias in aliases)
                {
                    if (normalized.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
                        normalized.StartsWith(alias + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        var remainder = normalized.Length == alias.Length
                            ? ""
                            : normalized.Substring(alias.Length).Trim();

                        return new NotionBaseScope
                        {
                            HasBase = true,
                            PrimaryAlias = shortcut.PrimaryAlias,
                            SourceName = shortcut.SourceName,
                            PathLabel = shortcut.PathLabel,
                            DisplayLabel = shortcut.DisplayLabel,
                            TitleFilter = shortcut.TitleFilter,
                            Remainder = remainder
                        };
                    }
                }
            }

            return new NotionBaseScope();
        }

        private static List<NotionBaseShortcut> GetNotionBaseShortcuts()
        {
            return new List<NotionBaseShortcut>
            {
                new NotionBaseShortcut
                {
                    // Los estados prtuz/rtuz/sprtuz/zREVISION son búsquedas
                    // normales y NO activan automáticamente la base.
                    PrimaryAlias = "revisiones",
                    SourceName = "Revisiones",
                    PathLabel = "Revisiones",
                    DisplayLabel = "Revisiones",
                    Aliases = new[] { "revision", "zrevisiones", "zrevbase" }
                },
                new NotionBaseShortcut
                {
                    PrimaryAlias = "zclientes",
                    SourceName = "Clientes",
                    PathLabel = "zCLIENTES",
                    DisplayLabel = "Clientes",
                    Aliases = new[] { "zcliente", "clientes", "cliente" }
                },
                new NotionBaseShortcut
                {
                    PrimaryAlias = "zdominios",
                    SourceName = "Dominios",
                    PathLabel = "zDOMINIOS",
                    DisplayLabel = "Dominios",
                    Aliases = new[] { "zdominio", "zd", "dominios", "dominio" }
                },
                new NotionBaseShortcut
                {
                    PrimaryAlias = "zproyectos",
                    SourceName = "Programas y proyectos",
                    PathLabel = "zPROYECTOS",
                    DisplayLabel = "Proyectos",
                    Aliases = new[] { "zproyecto", "zprogramas", "zprograma", "zproy", "zprog", "proyectos", "programas" }
                },
                new NotionBaseShortcut
                {
                    PrimaryAlias = "zcorreos",
                    SourceName = "Correos Contraseñas",
                    PathLabel = "zCORREOS",
                    DisplayLabel = "Correos",
                    Aliases = new[] { "zcorreo", "zpass", "zpasswords", "zcontraseñas", "correos", "contraseñas" }
                },
                new NotionBaseShortcut
                {
                    PrimaryAlias = "zpagar",
                    SourceName = "Cobrar y pagar",
                    PathLabel = "zPAGAR",
                    DisplayLabel = "Pagar",
                    TitleFilter = "PAGAR",
                    Aliases = new[] { "zpago" }
                },
                new NotionBaseShortcut
                {
                    PrimaryAlias = "zcobrar",
                    SourceName = "Cobrar y pagar",
                    PathLabel = "zCOBRAR",
                    DisplayLabel = "Cobrar",
                    TitleFilter = "COBRAR",
                    Aliases = new[] { "zcobro" }
                }
            };
        }

        private NotionBaseShortcut? GetShortcutForSource(string sourceName)
        {
            return GetNotionBaseShortcuts()
                .FirstOrDefault(x => string.Equals(x.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));
        }

        private bool RowMatchesPredictiveText(Anfeta.UI.Models.Weblab.SearchResultRow row, string query)
        {
            var terms = SplitAutoAndTerms(query);
            if (terms.Count == 0)
                return true;

            var text = NormalizeSuggestionText(GetPredictiveSearchText(row));

            return terms
                .Select(NormalizeSuggestionText)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetPredictiveTitle(Anfeta.UI.Models.Weblab.SearchResultRow row)
        {
            var title = (row.DisplayName ?? row.Name ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(title) ? "Sin título" : title;
        }

        private static string GetPredictiveSearchText(Anfeta.UI.Models.Weblab.SearchResultRow row)
        {
            return string.Join(" ", new[]
            {
                row.DisplayName,
                row.Name,
                row.PathColumn,
                row.ExternalSourceName,
                row.SearchText,
                row.Description,
                row.Target
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string BuildScopedQuery(string alias, string value)
        {
            var cleanAlias = (alias ?? string.Empty).Trim();
            var cleanValue = (value ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(cleanAlias))
                return cleanValue;

            if (string.IsNullOrWhiteSpace(cleanValue))
                return cleanAlias;

            return $"{cleanAlias} {cleanValue}";
        }

        private sealed class TopicCount
        {
            public string Text { get; set; } = "";
            public int Count { get; set; }
            public bool IsDomain { get; set; }
        }

        private List<TopicCount> BuildFrequentTopicSuggestions(
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> rows,
            string rowFilter,
            string excludedQuery = "")
        {
            var counts =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

            var domainCounts =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

            var filter =
                NormalizeSuggestionText(rowFilter);

            var excludedTerms =
                BuildPredictiveExcludedTerms(excludedQuery);

            var currentToken =
                GetCurrentPredictiveToken(
                    string.IsNullOrWhiteSpace(rowFilter)
                        ? excludedQuery
                        : rowFilter);

            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(filter) &&
                    !RowMatchesPredictiveText(row, rowFilter))
                {
                    continue;
                }

                var originalTitle =
                    GetPredictiveTitle(row);

                var extractedDomains =
                    ExtractPredictiveDomains(originalTitle);

                // Los dominios se extraen ANTES de normalizar el título.
                // Así se conservan los puntos y se sugieren como una sola pieza.
                foreach (var domain in extractedDomains)
                {
                    var normalizedDomain =
                        NormalizeSuggestionText(domain);

                    if (excludedTerms.Contains(normalizedDomain))
                        continue;

                    if (!string.IsNullOrWhiteSpace(currentToken) &&
                        !domain.StartsWith(
                            currentToken,
                            StringComparison.OrdinalIgnoreCase) &&
                        !domain.Contains(
                            currentToken,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    domainCounts[domain] =
                        domainCounts.TryGetValue(
                            domain,
                            out var domainCount)
                            ? domainCount + 1
                            : 1;
                }

                var title =
                    NormalizeSuggestionText(originalTitle);

                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var flattenedDomains =
                    extractedDomains
                        .Select(domain =>
                            Regex.Replace(
                                domain.ToLowerInvariant(),
                                @"[^\p{L}\p{Nd}]+",
                                string.Empty))
                        .Where(value =>
                            !string.IsNullOrWhiteSpace(value))
                        .ToHashSet(
                            StringComparer.OrdinalIgnoreCase);

                var words = title
                    .Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(IsUsefulPredictiveWord)
                    .Where(word =>
                        !flattenedDomains.Contains(
                            Regex.Replace(
                                word.ToLowerInvariant(),
                                @"[^\p{L}\p{Nd}]+",
                                string.Empty)))
                    .Where(word =>
                        !excludedTerms.Contains(
                            NormalizeSuggestionText(word)))
                    .ToList();

                var local =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (var word in words)
                    local.Add(word);

                for (var index = 0;
                     index < words.Count - 1;
                     index++)
                {
                    var phrase =
                        $"{words[index]} {words[index + 1]}";

                    var phraseParts = phrase
                        .Split(
                            ' ',
                            StringSplitOptions.RemoveEmptyEntries)
                        .Select(NormalizeSuggestionText)
                        .ToList();

                    if (phraseParts.Any(part =>
                            excludedTerms.Contains(part)))
                    {
                        continue;
                    }

                    local.Add(phrase);
                }

                foreach (var item in local)
                {
                    counts[item] =
                        counts.TryGetValue(
                            item,
                            out var current)
                            ? current + 1
                            : 1;
                }
            }

            var minCount =
                string.IsNullOrWhiteSpace(filter)
                    ? 2
                    : 1;

            var domainSuggestions =
                domainCounts
                    .Where(pair => pair.Value >= 1)
                    .OrderBy(pair =>
                        GetPredictiveDomainPrefixRank(
                            pair.Key,
                            currentToken))
                    .ThenByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key.Length)
                    .ThenBy(pair => pair.Key)
                    .Select(pair =>
                        new TopicCount
                        {
                            Text = pair.Key,
                            Count = pair.Value,
                            IsDomain = true
                        });

            var topicSuggestions =
                counts
                    .Where(pair =>
                        pair.Value >= minCount)
                    .OrderBy(pair =>
                        GetPredictivePrefixRank(
                            pair.Key,
                            currentToken))
                    .ThenByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key)
                    .Select(pair =>
                        new TopicCount
                        {
                            Text = pair.Key,
                            Count = pair.Value,
                            IsDomain = false
                        });

            return domainSuggestions
                .Concat(topicSuggestions)
                .GroupBy(
                    item => item.Text,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static IReadOnlyList<string>
            ExtractPredictiveDomains(
                string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            const string pattern =
                @"(?<![\w@])(?:https?://)?(?:www\.)?" +
                @"(?<domain>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+" +
                @"(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|" +
                @"com|mx|org|net|io|co|app|dev))" +
                @"(?=$|[/:?#\s)\]}>.,;!])";

            return Regex.Matches(
                    value,
                    pattern,
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match =>
                    match.Groups["domain"]
                        .Value
                        .Trim()
                        .TrimEnd('.')
                        .ToLowerInvariant())
                .Where(domain =>
                    !string.IsNullOrWhiteSpace(domain))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int GetPredictiveDomainPrefixRank(
            string domain,
            string currentToken)
        {
            if (string.IsNullOrWhiteSpace(currentToken))
                return 0;

            var cleanDomain =
                (domain ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            var cleanToken =
                (currentToken ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            if (cleanDomain.StartsWith(
                    cleanToken + ".",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (cleanDomain.StartsWith(
                    cleanToken,
                    StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return cleanDomain.Contains(
                cleanToken,
                StringComparison.OrdinalIgnoreCase)
                    ? 2
                    : 3;
        }


        private static string GetCurrentPredictiveToken(string query)
        {
            var raw = query ?? string.Empty;

            // Al escribir un espacio ya no hay una palabra parcial activa:
            // vuelven a mostrarse las sugerencias frecuentes.
            if (raw.EndsWith(" ", StringComparison.Ordinal))
                return string.Empty;

            var terms = SplitAutoAndTerms(raw);
            return terms.Count == 0
                ? string.Empty
                : NormalizeSuggestionText(terms[^1]);
        }

        private static int GetPredictivePrefixRank(
            string suggestion,
            string currentToken)
        {
            if (string.IsNullOrWhiteSpace(currentToken))
                return 0;

            var normalized = NormalizeSuggestionText(suggestion);
            var words = normalized.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

            if (normalized.StartsWith(
                    currentToken,
                    StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (words.Any(word => word.StartsWith(
                    currentToken,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return 1;
            }

            return normalized.Contains(
                currentToken,
                StringComparison.OrdinalIgnoreCase)
                    ? 2
                    : 3;
        }

        private static HashSet<string> BuildPredictiveExcludedTerms(string query)
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var term in SplitAutoAndTerms(query ?? string.Empty))
            {
                var norm = NormalizeSuggestionText(term);
                if (!string.IsNullOrWhiteSpace(norm))
                    excluded.Add(norm);
            }

            foreach (var shortcut in GetNotionBaseShortcuts())
            {
                foreach (var alias in new[] { shortcut.PrimaryAlias }
                    .Concat(shortcut.Aliases ?? Array.Empty<string>()))
                {
                    var norm = NormalizeSuggestionText(alias);
                    if (!string.IsNullOrWhiteSpace(norm))
                        excluded.Add(norm);
                }

                foreach (var label in new[] { shortcut.SourceName, shortcut.PathLabel, shortcut.DisplayLabel })
                {
                    foreach (var part in SplitAutoAndTerms(label ?? string.Empty))
                    {
                        var norm = NormalizeSuggestionText(part);
                        if (!string.IsNullOrWhiteSpace(norm))
                            excluded.Add(norm);
                    }
                }
            }

            return excluded;
        }

        private static bool IsKnownBaseAlias(string normalizedTerm)
        {
            var norm = NormalizeSuggestionText(normalizedTerm);
            if (string.IsNullOrWhiteSpace(norm))
                return false;

            return GetNotionBaseShortcuts()
                .SelectMany(x => new[] { x.PrimaryAlias }.Concat(x.Aliases ?? Array.Empty<string>()))
                .Select(NormalizeSuggestionText)
                .Any(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUsefulPredictiveWord(string word)
        {
            var w = (word ?? string.Empty).Trim();

            if (w.Length < 3)
                return false;

            return !PredictiveStopWords.Contains(w, StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeSuggestionText(string value)
        {
            var text = (value ?? string.Empty).ToLowerInvariant();
            text = Regex.Replace(text, @"[^\p{L}\p{Nd}]+", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private void LoadSavedSearches()
        {
            _savedSearches.Clear();
            var raw = ApplicationData.Current.LocalSettings.Values[LS_SavedSearches] as string;

            if (string.IsNullOrWhiteSpace(raw))
            {
                RefreshSavedSearchesUi();
                return;
            }

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

            RefreshSavedSearchesUi();
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

            RefreshQuickFlyoutContent(SearchBox?.Text ?? string.Empty);

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

            var titleBox = new TextBox { PlaceholderText = "Título", Text = existing.Title ?? "" };
            var descBox = new TextBox { PlaceholderText = "Descripción (opcional)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = existing.Description ?? "" };
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
                StatusText.Text = "Estado: Título y Query son obligatorios.";
                return;
            }
            if (_savedSearches.Any(x => x.Id != existing.Id &&
                string.Equals(x.Query, newQuery, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = "Estado: Ya existe otro comando con esa búsqueda.";
                return;
            }

            existing.Title = newTitle;
            existing.Description = newDesc;
            existing.Query = newQuery;

            SaveSavedSearches();
            RefreshSavedSearchesUi();
            StatusText.Text = "Estado: Comando actualizado ✅";
        }

        private void BtnSaveSearch_Click(object sender, RoutedEventArgs e)
        {
            var currentQuery = (SearchBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(currentQuery))
            {
                StatusText.Text = "Estado: Escribe una búsqueda antes de guardar.";
                return;
            }

            var exists = _savedSearches.Any(x =>
                string.Equals(x.Query, currentQuery, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                StatusText.Text = "Estado: Esa búsqueda ya está guardada.";
                return;
            }

            _savedSearches.Add(new SavedSearch
            {
                Title = BuildQuickSearchTitle(currentQuery),
                Description = "",
                Query = currentQuery
            });

            SaveSavedSearches();
            RefreshSavedSearchesUi();

            StatusText.Text = $"Estado: Búsqueda guardada ✅ {currentQuery}";
        }
        private static string BuildQuickSearchTitle(string query)
        {
            var title = (query ?? "").Trim();

            if (string.IsNullOrWhiteSpace(title))
                return "Búsqueda rápida";

            return title.Length > 42
                ? title.Substring(0, 42).Trim() + "..."
                : title;
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
                    Content = "¿Seguro que quieres eliminar todos los comandos guardados?",
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

        private async void ChipSourceFilter_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
                return;

            var selected =
                (clicked.Tag?.ToString() ?? "all")
                .Trim()
                .ToLowerInvariant();

            _activeSourceScope = selected switch
            {
                "notion" => SearchSourceScope.Notion,
                "dropbox" => SearchSourceScope.Dropbox,
                _ => SearchSourceScope.All
            };

            if (_activeSourceScope == SearchSourceScope.Dropbox)
            {
                // Al entrar a Dropbox se restablece el orden predeterminado:
                // Fecha modificada, de más reciente a más antiguo.
                _sortKey = "mod_desc";
                UpdateColumnSortIndicators();

                _activeNotionBaseFilter = string.Empty;
                _activePaymentBaseTitleFilter = string.Empty;
                SetNotionBaseChipChecks(string.Empty);

                var currentQuery =
                    (SearchBox.Text ?? string.Empty).Trim();

                var scope = ResolveNotionBaseScope(currentQuery);

                if (scope.HasBase)
                {
                    var remainder =
                        ExtractOriginalRemainderForScope(
                            currentQuery,
                            scope);

                    _suppressSuggest = true;
                    SearchBox.Text = remainder;
                    _suppressSuggest = false;
                }

                QuickCommandsInputFlyout?.Hide();
            }

            SetSourceScopeChipChecks();
            SaveSourceScopePreference();

            var query =
                (SearchBox.Text ?? string.Empty).Trim();

            await RunLocalSearchAsync(query);

            ModeText.Text =
                $"Modo: Buscar ({GetSourceScopeLabel()})";

            StatusText.Text =
                $"Estado: Filtro global → {GetSourceScopeLabel()} ✅";
        }

        private static bool PaymentBaseTitleMatches(
            Anfeta.UI.Models.Weblab.SearchResultRow row,
            string paymentToken)
        {
            if (row == null)
                return false;

            var title =
                row.DisplayName ??
                row.Name ??
                string.Empty;

            var token =
                (paymentToken ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(token))
                return true;

            return Regex.IsMatch(
                title,
                $@"(?<![\p{{L}}\p{{Nd}}_])(?:a?prtuz|sprtuz|rtuz|z)?{Regex.Escape(token)}(?![\p{{L}}\p{{Nd}}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private List<Anfeta.UI.Models.Weblab.SearchResultRow> ApplyNotionBaseFilter(
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> rows)
        {
            var list = rows?.ToList() ?? new List<Anfeta.UI.Models.Weblab.SearchResultRow>();

            if (string.IsNullOrWhiteSpace(_activeNotionBaseFilter))
                return list;

            var filtered = list
                .Where(x =>
                    x.Source == Anfeta.UI.Models.Weblab.SearchSource.Notion &&
                    string.Equals(
                        x.ExternalSourceName,
                        _activeNotionBaseFilter,
                        StringComparison.OrdinalIgnoreCase));

            if (string.Equals(
                    _activeNotionBaseFilter,
                    "Cobrar y pagar",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_activePaymentBaseTitleFilter))
            {
                filtered = filtered.Where(x =>
                    PaymentBaseTitleMatches(
                        x,
                        _activePaymentBaseTitleFilter));
            }

            return filtered.ToList();
        }
        private async void ChipBaseFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
                return;

            var selectedRaw =
                (clicked.Tag as string ?? string.Empty).Trim();

            var selectedParts = selectedRaw.Split('|');
            var selectedBase =
                selectedParts.Length > 0
                    ? selectedParts[0].Trim()
                    : string.Empty;
            var selectedPayment =
                selectedParts.Length > 1
                    ? selectedParts[1].Trim()
                    : string.Empty;

            _activeNotionBaseFilter =
                clicked == ChipBaseAll
                    ? string.Empty
                    : selectedBase;

            _activePaymentBaseTitleFilter =
                clicked == ChipBaseAll
                    ? string.Empty
                    : selectedPayment;

            _activeSourceScope = SearchSourceScope.Notion;
            SetSourceScopeChipChecks();
            SaveSourceScopePreference();

            SetNotionBaseChipChecks(
                _activeNotionBaseFilter,
                _activePaymentBaseTitleFilter);

            await RunLocalSearchAsync(
                (SearchBox.Text ?? string.Empty).Trim());

            ModeText.Text = string.IsNullOrWhiteSpace(
                _activeNotionBaseFilter)
                ? "Modo: Buscar (Notion)"
                : $"Modo: Notion · {_activeNotionBaseFilter}";
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
