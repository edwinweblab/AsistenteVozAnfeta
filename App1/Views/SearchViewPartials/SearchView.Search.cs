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

            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var suggestions = GenerateStutterSuggestions(sender.Text, max: 8);
                sender.ItemsSource = suggestions;
                sender.IsSuggestionListOpen = suggestions.Count > 0;
            }
            else
            {
                sender.ItemsSource = null;
                sender.IsSuggestionListOpen = false;
            }

            EnsureSearchDebounce();

            var q = (sender.Text ?? string.Empty).Trim();

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

            var parsed = AdvancedQueryV3.Parse(rawQuery);
            UpdateHighlightTerms(rawQuery, parsed);

            if (!string.IsNullOrWhiteSpace(rawQuery))
            {
                if (rawQuery == "-") return;

                if (!LooksAdvanced(rawQuery))
                {
                    items = items.Where(x => MatchesSavedFilterOnRow(x, rawQuery));
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
            items = ApplySortKey(items);

            foreach (var it in items.Take(500))
            {
                it.IsBookmarked = _bookmarksService.Exists(_bookmarks, it.Target);
                it.Icon ??= _iconService.GetIcon(it.Type, it.Target);
                Results.Add(it);
            }

            ResultsList.ItemsSource = null;
            ResultsList.ItemsSource = Results;

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

            var parsed = AdvancedQueryV3.Parse(rawQuery);
            UpdateHighlightTerms(rawQuery, parsed);

            if (!string.IsNullOrWhiteSpace(rawQuery))
            {
                if (rawQuery == "-") return;

                if (!LooksAdvanced(rawQuery))
                {
                    items = items.Where(x => MatchesSavedFilterOnRow(x, rawQuery));
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
            items = ApplySortKey(items);

            foreach (var it in items.Take(500))
            {
                token.ThrowIfCancellationRequested();
                it.IsBookmarked = _bookmarksService.Exists(_bookmarks, it.Target);
                it.Icon ??= _iconService.GetIcon(it.Type, it.Target);
                Results.Add(it);
            }

            ResultsList.ItemsSource = null;
            ResultsList.ItemsSource = Results;

            CountText.Text = $"{Results.Count} resultados";
            EmptyResultsHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _voicePost.NotifySearchResults(Results);
            Dictation_SetResults(Results);
            await Task.CompletedTask;
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
                "name_desc" => items.OrderByDescending(x => x.Name),
                _ => items.OrderBy(x => x.Name)
            };
        }

        #endregion
    }
}