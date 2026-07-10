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
        #region ===== Search (Everything-like) =====

        private void EnsureSearchDebounce()
        {
            if (_searchDebounceTimer != null) return;

            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            _searchDebounceTimer.Tick += async (_, __) =>
            {
                _searchDebounceTimer.Stop();

                var q = (SearchBox?.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(q)) return;
                if (!App.LocalIndex.HasData) return;

                _searchCts?.Cancel();
                _searchCts = new CancellationTokenSource();
                var token = _searchCts.Token;

                try { await RunLocalSearchAsync(q, token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { StatusText.Text = $"Estado: Error buscando → {ex.Message}"; }
            };
        }

        private void CancelPendingSearch()
        {
            _searchDebounceTimer?.Stop();
            _searchCts?.Cancel();
        }

        private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput && !_allowProgrammaticSearch)
                return;

            if (_suppressSuggest)
                return;

            // Sugerencias desactivadas temporalmente para evitar que tapen los resultados.
            sender.ItemsSource = null;
            sender.IsSuggestionListOpen = false;

            EnsureSearchDebounce();

            var q = (sender.Text ?? string.Empty).Trim();
            SyncBaseChipsFromQuery(q);
            RefreshQuickFlyoutContent(q);

            if (ShouldShowQuickFlyout(q))
                ShowQuickCommandsInputFlyout();

            // ─────────────────────────────────────────────
            // Si el usuario limpió el buscador
            // ─────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(q))
            {
                CancelPendingSearch();

                // Guardar que el query quedó vacío.
                SetTabTitle("");
                NotifyWorkspaceChanged();

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

                // Si hay índice cargado, mostrar todos los resultados.
                // Aquí entran Notion, local o local + Notion.
                BreadcrumbText.Text = "Todos los resultados";

                await PaintLoadedIndexAsync();

                return;
            }

            // ─────────────────────────────────────────────
            // Si el usuario escribió una búsqueda
            // ─────────────────────────────────────────────
            if (DropboxIndexCoordinator.IsIndexing)
            {
                StatusText.Text = "Estado: Ruta nueva detectada, indexando…";
                return;
            }

            SetTabTitle(SearchBox.Text);
            NotifyWorkspaceChanged();

            if (!App.LocalIndex.HasData)
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                return;
            }

            _searchDebounceTimer!.Stop();
            _searchDebounceTimer.Start();
        }

        private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            CancelPendingSearch();
            sender.ItemsSource = null;
            sender.IsSuggestionListOpen = false;
            QuickCommandsInputFlyout?.Hide();
            _predictiveSuggestions.Clear();
            _visibleSavedSearches.Clear();
            BindPredictiveSuggestions();
            UpdateQuickFlyoutVisibility();
            ResetCurrentMatchOptions();
            var ui = (sender.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(ui))
            {
                _useExpandedQueryOnSubmit = false;
                await RunSearchAsync("");
                return;
            }

            string effective = ui;

            if (_useExpandedQueryOnSubmit)
            {
                var variants = ExpandStutterQuery(ui);
                variants = variants?
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new System.Collections.Generic.List<string>();

                if (variants.Count > 0)
                    effective = string.Join(" | ", variants);
            }

            _useExpandedQueryOnSubmit = false;
            await RunSearchAsync(ui, effective);
        }

        private async Task RunSearchAsync(string uiQuery, string? effectiveQuery = null)
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

            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            try
            {
                BreadcrumbText.Text = string.IsNullOrWhiteSpace(uiQuery)
                    ? DROPBOX_ROOT
                    : $"Buscar: {uiQuery}";
                ModeText.Text = "Modo: Buscar (Local + Notion)";

                await RunLocalSearchAsync(effectiveQuery ?? uiQuery);
                StatusText.Text = "Estado: Búsqueda local ✅";
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        // Sin CancellationToken — para llamadas directas
        private async Task RunLocalSearchAsync(string query)
        {
            _mode = ViewMode.Explorer;
            Results.Clear();

            var rawQuery = (query ?? "").Trim();
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> items = App.LocalIndex.GetAll();
            items = items.Where(x => !IsExcludedPath(x.Target));

            var scope = ResolveNotionBaseScope(rawQuery);
            var queryForSearch = scope.HasBase ? scope.Remainder : rawQuery;

            SyncBaseChipsFromQuery(rawQuery);
            UpdateSearchBreadcrumb(rawQuery, scope, queryForSearch);

            if (scope.HasBase)
            {
                items = items.Where(x =>
                    x.Source == Anfeta.UI.Models.Weblab.SearchSource.Notion &&
                    string.Equals(x.ExternalSourceName, scope.SourceName, StringComparison.OrdinalIgnoreCase));
            }

            var parsed = AdvancedQueryV3.Parse(queryForSearch);

            if (!LooksAdvanced(queryForSearch))
                UpdateHighlightTermsForAutoAnd(queryForSearch);
            else
                UpdateHighlightTerms(queryForSearch, parsed);

            if (!string.IsNullOrWhiteSpace(queryForSearch))
            {
                if (queryForSearch == "-") return;

                if (!LooksAdvanced(queryForSearch))
                {
                    items = items.Where(x => MatchesAutoAndQueryOnRow(x, queryForSearch));
                }
                else
                {
                    // EvaluateWithPlan ya maneja internamente:
                    //   - expr booleana (AND/OR/NOT/pipe)
                    //   - nopath:
                    //   - ext: con lista (pdf;docx;xlsx)
                    //   - FolderContains y OnlyFolders se aplican abajo como siempre
                    items = items.Where(x => AdvancedQueryV3.EvaluateWithPlan(parsed.Expr, new RowView(x), parsed.Plan));

                    // FolderContains: filtro de ruta/carpeta (de la query o ruta absoluta)
                    if (!string.IsNullOrWhiteSpace(parsed.Plan.FolderContains))
                    {
                        var f = parsed.Plan.FolderContains.ToLowerInvariant();
                        items = items.Where(x => (x.Target ?? "").ToLowerInvariant().Contains(f));
                    }

                    // OnlyFolders: type:folder / type:file
                    if (parsed.Plan.OnlyFolders.HasValue)
                    {
                        var wantFolder = parsed.Plan.OnlyFolders.Value;
                        items = items.Where(x =>
                            wantFolder
                                ? (x.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase)
                                : (x.Type ?? "").Equals("FILE", StringComparison.OrdinalIgnoreCase));
                    }

                    // Nota: parsed.Plan.Ext / ExtList ya fue aplicado dentro de EvaluateWithPlan,
                    // NO hace falta volver a filtrar aquí.
                }
            }

            // ApplyChipFilters: solo actúa cuando el usuario clickea un chip
            // sin escribir query (p. ej. _extFilter viene del chip PDF, DOCX, etc.)
            items = ApplyChipFilters(items);

            if (!scope.HasBase)
                items = ApplyNotionBaseFilter(items);

            items = ApplySortKey(items);

            foreach (var it in items.Take(500))
            {
                it.IsBookmarked = _bookmarksService.Exists(_bookmarks, it.Target);
                it.Icon ??= _iconService.GetIcon(it.Type, it.Target);
                Results.Add(it);
            }
            RefreshResultsListView();

            CountText.Text = $"{Results.Count} resultados";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _voicePost.NotifySearchResults(Results);
            Dictation_SetResults(Results);
            await Task.CompletedTask;
        }

        // Con CancellationToken — para el debounce
        private async Task RunLocalSearchAsync(string query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            _mode = ViewMode.Explorer;
            Results.Clear();

            var rawQuery = (query ?? "").Trim();
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> items = App.LocalIndex.GetAll();
            items = items.Where(x => !IsExcludedPath(x.Target));
            token.ThrowIfCancellationRequested();

            var scope = ResolveNotionBaseScope(rawQuery);
            var queryForSearch = scope.HasBase ? scope.Remainder : rawQuery;

            SyncBaseChipsFromQuery(rawQuery);
            UpdateSearchBreadcrumb(rawQuery, scope, queryForSearch);

            if (scope.HasBase)
            {
                items = items.Where(x =>
                    x.Source == Anfeta.UI.Models.Weblab.SearchSource.Notion &&
                    string.Equals(x.ExternalSourceName, scope.SourceName, StringComparison.OrdinalIgnoreCase));
            }

            var parsed = AdvancedQueryV3.Parse(queryForSearch);

            if (!LooksAdvanced(queryForSearch))
                UpdateHighlightTermsForAutoAnd(queryForSearch);
            else
                UpdateHighlightTerms(queryForSearch, parsed);

            if (!string.IsNullOrWhiteSpace(queryForSearch))
            {
                if (queryForSearch == "-") return;

                if (!LooksAdvanced(queryForSearch))
                {
                    items = items.Where(x => MatchesAutoAndQueryOnRow(x, queryForSearch));
                }
                else
                {
                    items = items.Where(x => AdvancedQueryV3.EvaluateWithPlan(parsed.Expr, new RowView(x), parsed.Plan));

                    if (!string.IsNullOrWhiteSpace(parsed.Plan.FolderContains))
                    {
                        var f = parsed.Plan.FolderContains.ToLowerInvariant();
                        items = items.Where(x => (x.Target ?? "").ToLowerInvariant().Contains(f));
                    }

                    if (parsed.Plan.OnlyFolders.HasValue)
                    {
                        var wantFolder = parsed.Plan.OnlyFolders.Value;
                        items = items.Where(x =>
                            wantFolder
                                ? (x.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase)
                                : (x.Type ?? "").Equals("FILE", StringComparison.OrdinalIgnoreCase));
                    }
                    // Ext / ExtList ya aplicados dentro de EvaluateWithPlan
                }
            }

            items = ApplyChipFilters(items);

            if (!scope.HasBase)
                items = ApplyNotionBaseFilter(items);

            items = ApplySortKey(items);

            foreach (var it in items.Take(500))
            {
                token.ThrowIfCancellationRequested();
                it.IsBookmarked = _bookmarksService.Exists(_bookmarks, it.Target);
                it.Icon ??= _iconService.GetIcon(it.Type, it.Target);
                Results.Add(it);
            }
            RefreshResultsListView();

            CountText.Text = $"{Results.Count} resultados";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _voicePost.NotifySearchResults(Results);
            Dictation_SetResults(Results);
            await Task.CompletedTask;
        }

        private void UpdateSearchBreadcrumb(string rawQuery, NotionBaseScope scope, string queryForSearch)
        {
            if (BreadcrumbText == null)
                return;

            var original = (rawQuery ?? string.Empty).Trim();
            var terms = (queryForSearch ?? string.Empty).Trim();

            if (scope != null && scope.HasBase)
            {
                BreadcrumbText.Text = string.IsNullOrWhiteSpace(terms)
                    ? $"Base: {scope.PathLabel}"
                    : $"Base: {scope.PathLabel} · Buscar: {terms}";
                return;
            }

            BreadcrumbText.Text = string.IsNullOrWhiteSpace(original)
                ? "Todos los resultados"
                : $"Buscar: {original}";
        }

        private void UpdateHighlightTermsForAutoAnd(string rawQuery)
        {
            _highlightTerms = SplitAutoAndTerms(rawQuery)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Length)
                .ToList();
        }
        // Helpers compartidos con Filters
        private IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> ApplyChipFilters(
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> items)
        {
            if (_onlyFolders)
                items = items.Where(x => (x.Type ?? "").Equals("FOLDER", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(_extFilter))
            {
                items = items.Where(x =>
                {
                    var ext = Path.GetExtension(x.Target ?? x.Name ?? "").TrimStart('.').ToLowerInvariant();
                    if (_extFilter == "img") return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";
                    return ext == _extFilter;
                });
            }

            return items;
        }

        private IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> ApplySortKey(
          IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> items)
        {
            return _sortKey switch
            {
                "name_desc" => items
                    .OrderBy(x => GetPathOrderRank(x))
                    .ThenByDescending(x => x.DisplayName ?? x.Name),

                "mod_desc" => items
                    .OrderBy(x => GetPathOrderRank(x))
                    .ThenByDescending(x => ParseModifiedForSort(x.ServerModified))
                    .ThenBy(x => x.DisplayName ?? x.Name),

                "mod_asc" => items
                    .OrderBy(x => GetPathOrderRank(x))
                    .ThenBy(x => ParseModifiedForSort(x.ServerModified))
                    .ThenBy(x => x.DisplayName ?? x.Name),

                _ => items
                    .OrderBy(x => GetPathOrderRank(x))
                    .ThenBy(x => x.DisplayName ?? x.Name)
            };
        }

        private static string GetSortableName(Anfeta.UI.Models.Weblab.SearchResultRow row)
        {
            return (row.DisplayName ?? row.Name ?? string.Empty).Trim();
        }

        private int GetSearchRelevanceRank(
            Anfeta.UI.Models.Weblab.SearchResultRow row,
            string query)
        {
            var name = GetSortableName(row);
            var normalizedName = NormalizeForRank(name);
            var normalizedQuery = NormalizeForRank(query);

            var fullText = string.Join(" ", new[]
            {
                row.DisplayName,
                row.Name,
                row.PathColumn,
                row.SearchText,
                row.Description,
                row.Target
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            var terms = SplitAutoAndTerms(query)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (string.IsNullOrWhiteSpace(normalizedQuery))
                return 9;

            // 0. Coincidencia exacta del nombre.
            if (string.Equals(normalizedName, normalizedQuery, StringComparison.OrdinalIgnoreCase))
                return 0;

            // 1. El nombre empieza con toda la búsqueda.
            if (normalizedName.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                return 1;

            // 2. Todas las palabras empiezan dentro del nombre.
            // Ejemplo: "ccqv persa" prioriza "ccqvpersa..." sobre resultados donde aparece más lejos.
            if (terms.Count > 0 && terms.All(term =>
                name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(word => word.StartsWith(term, StringComparison.OrdinalIgnoreCase))))
                return 2;

            // 3. Todas las palabras están dentro del nombre.
            if (terms.Count > 0 && terms.All(term =>
                name.Contains(term, StringComparison.OrdinalIgnoreCase)))
                return 3;

            // 4. La búsqueda completa aparece en el nombre.
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                return 4;

            // 5. Todas las palabras aparecen en cualquier campo indexado.
            if (terms.Count > 0 && terms.All(term =>
                fullText.Contains(term, StringComparison.OrdinalIgnoreCase)))
                return 5;

            return 9;
        }

        private static string NormalizeForRank(string value)
        {
            return System.Text.RegularExpressions.Regex
                .Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9áéíóúñ]+", "");
        }

        private static DateTime ParseModifiedForSort(string? value)
        {
            if (DateTime.TryParse(value, out var dt))
                return dt;

            return DateTime.MinValue;
        }

        private static int GetPathOrderRank(Anfeta.UI.Models.Weblab.SearchResultRow row)
        {
            var path = row.PathColumn ?? "";

            return path switch
            {
                "Revisiones" => 0,
                "zCLIENTES" => 1,
                "zDOMINIOS" => 2,
                "zPROYECTOS" => 3,
                "zCORREOS" => 4,
                "zPAGAR - zCOBRAR" => 5,
                "Local" => 6,
                _ => 99
            };
        }

        #endregion
    }
}