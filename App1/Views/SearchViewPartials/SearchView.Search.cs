using Anfeta.UI.Services.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

                try
                {
                    ShowLoadingState(
                        "Estado: Buscando resultados...",
                        $"Buscando: {q}");

                    await RunLocalSearchAsync(q, token);
                    StatusText.Text = $"Estado: Búsqueda lista ✅ ({Results.Count} resultados)";
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    StatusText.Text = $"Estado: Error buscando → {ex.Message}";
                }
                finally
                {
                    HideLoadingState();
                }
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

            // El buscador usa únicamente el panel predictivo personalizado
            // (tarjetas azules). Se desactiva el desplegable nativo del
            // AutoSuggestBox para evitar mostrar dos predictivos a la vez.
            sender.ItemsSource = null;
            sender.IsSuggestionListOpen = false;

            EnsureSearchDebounce();

            var q = (sender.Text ?? string.Empty).Trim();

            if (_calendarViewActive)
            {
                CancelPendingSearch();
                QuickCommandsInputFlyout?.Hide();
                ApplyCalendarSearchFilter(q);
                return;
            }

            SyncBaseChipsFromQuery(q);
            RefreshQuickFlyoutContent(q);

            // Prioriza dominios completos dentro del predictivo personalizado.
            // Ejemplo: al escribir "weblab" o "weblab." se muestran primero
            // weblab.mx, weblab.com y weblab.com.mx, conservando los puntos.
            PromoteDomainPredictiveSuggestions(q);

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
                // Importante: esta es una vista global de búsqueda, no navegación
                // por carpeta. Si no se limpia este estado, al renombrar un archivo
                // ANFETA intenta refrescar la carpeta raíz y cambia de vista.
                _isBrowsing = false;
                _currentFolder = string.Empty;
                _currentFolderPath = string.Empty;

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


        private void PromoteDomainPredictiveSuggestions(
            string query)
        {
            if (!App.LocalIndex.HasData)
                return;

            var raw =
                (query ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return;

            // El predictivo trabaja sobre la última parte escrita para no
            // interferir con búsquedas que ya contienen varios términos.
            var currentPart = raw
                .Split(
                    new[] { ' ', '\t', '\r', '\n', '"', '\'' },
                    StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?
                .Trim()
                .ToLowerInvariant() ?? string.Empty;

            if (currentPart.Length < 2)
                return;

            const string domainPattern =
                @"(?<![\w@])(?:https?://)?(?:www\.)?" +
                @"(?<domain>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+" +
                @"(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|" +
                @"com|mx|org|net|io|co|app|dev))" +
                @"(?=$|[/:?#\s)\]}>.,;!])";

            var frequency =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var row in App.LocalIndex.GetAll())
            {
                var searchable = string.Join(
                    " ",
                    new[]
                    {
                        row.DisplayName,
                        row.Name,
                        row.SearchText,
                        row.Description,
                        row.Target
                    }.Where(value =>
                        !string.IsNullOrWhiteSpace(value)));

                foreach (Match match in Regex.Matches(
                             searchable,
                             domainPattern,
                             RegexOptions.IgnoreCase |
                             RegexOptions.CultureInvariant))
                {
                    var domain = match.Groups["domain"]
                        .Value
                        .Trim()
                        .TrimEnd('.')
                        .ToLowerInvariant();

                    if (string.IsNullOrWhiteSpace(domain))
                        continue;

                    if (!domain.StartsWith(
                            currentPart,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    frequency[domain] =
                        frequency.TryGetValue(domain, out var count)
                            ? count + 1
                            : 1;
                }
            }

            var domains = frequency
                .OrderByDescending(pair =>
                    string.Equals(
                        pair.Key,
                        currentPart,
                        StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(pair =>
                    pair.Key.StartsWith(
                        currentPart + ".",
                        StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key.Length)
                .ThenBy(pair => pair.Key)
                .Take(8)
                .ToList();

            if (domains.Count == 0)
                return;

            // Evita duplicados y coloca los dominios al inicio del panel azul.
            for (var index =
                     _predictiveSuggestions.Count - 1;
                 index >= 0;
                 index--)
            {
                var existing =
                    _predictiveSuggestions[index];

                if (domains.Any(pair =>
                        string.Equals(
                            existing.Query,
                            pair.Key,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            existing.Title,
                            pair.Key,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    _predictiveSuggestions.RemoveAt(index);
                }
            }

            for (var index = domains.Count - 1;
                 index >= 0;
                 index--)
            {
                var pair = domains[index];

                _predictiveSuggestions.Insert(
                    0,
                    new PredictiveSuggestion
                    {
                        Title = pair.Key,
                        Subtitle =
                            $"Dominio completo · aparece {pair.Value}x",
                        Query = pair.Key,
                        Kind = "Domain",
                        IconGlyph = "\uE71B"
                    });
            }

            BindPredictiveSuggestions();
            UpdateQuickFlyoutVisibility();
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

            if (_calendarViewActive)
            {
                ApplyCalendarSearchFilter(ui);
                return;
            }

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
            // Toda búsqueda, incluso una búsqueda vacía, representa la vista
            // global del índice. Esto evita conservar por error el estado de
            // navegación de una carpeta anterior.
            _isBrowsing = false;
            _currentFolder = string.Empty;
            _currentFolderPath = string.Empty;

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

            ShowLoadingState(
                "Estado: Buscando resultados...",
                $"Origen: {GetSourceScopeLabel()}");

            try
            {
                BreadcrumbText.Text = string.IsNullOrWhiteSpace(uiQuery)
                    ? DROPBOX_ROOT
                    : $"Buscar: {uiQuery}";
                ModeText.Text =
                    $"Modo: Buscar ({GetSourceScopeLabel()})";

                await RunLocalSearchAsync(effectiveQuery ?? uiQuery);
                StatusText.Text = "Estado: Búsqueda local ✅";
            }
            finally
            {
                HideLoadingState();
            }
        }

        // Sin CancellationToken — para llamadas directas
        private async Task RunLocalSearchAsync(string query)
        {
            _isBrowsing = false;
            _mode = ViewMode.Explorer;
            Results.Clear();

            var rawQuery = (query ?? "").Trim();
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> items = App.LocalIndex.GetAll();
            items = items.Where(x => !IsExcludedPath(x.Target));
            items = ApplyGlobalSourceFilter(items);

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

            if (HasQuotedSearchParts(queryForSearch) ||
                !LooksAdvanced(queryForSearch))
                UpdateHighlightTermsForAutoAnd(queryForSearch);
            else
                UpdateHighlightTerms(queryForSearch, parsed);

            if (!string.IsNullOrWhiteSpace(queryForSearch))
            {
                if (queryForSearch == "-") return;

                if (HasQuotedSearchParts(queryForSearch) ||
                    !LooksAdvanced(queryForSearch))
                {
                    items = items.Where(x => MatchesFlexibleOrQuotedQuery(x, queryForSearch));
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
            Dictation_SetResults(BuildSpeechResults(Results));
            await Task.CompletedTask;
        }

        // Con CancellationToken — para el debounce
        private async Task RunLocalSearchAsync(string query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            _isBrowsing = false;
            _mode = ViewMode.Explorer;
            Results.Clear();

            var rawQuery = (query ?? "").Trim();
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> items = App.LocalIndex.GetAll();
            items = items.Where(x => !IsExcludedPath(x.Target));
            items = ApplyGlobalSourceFilter(items);
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

            if (HasQuotedSearchParts(queryForSearch) ||
                !LooksAdvanced(queryForSearch))
                UpdateHighlightTermsForAutoAnd(queryForSearch);
            else
                UpdateHighlightTerms(queryForSearch, parsed);

            if (!string.IsNullOrWhiteSpace(queryForSearch))
            {
                if (queryForSearch == "-") return;

                if (HasQuotedSearchParts(queryForSearch) ||
                    !LooksAdvanced(queryForSearch))
                {
                    items = items.Where(x => MatchesFlexibleOrQuotedQuery(x, queryForSearch));
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
            Dictation_SetResults(BuildSpeechResults(Results));
            await Task.CompletedTask;
        }

        private static bool HasQuotedSearchParts(
            string query)
        {
            return Regex.IsMatch(
                query ?? string.Empty,
                "\"[^\"]+\"",
                RegexOptions.CultureInvariant);
        }

        private static bool MatchesFlexibleOrQuotedQuery(
            Anfeta.UI.Models.Weblab.SearchResultRow row,
            string query)
        {
            var searchable = string.Join(
                " ",
                new[]
                {
                    row.DisplayName,
                    row.Name,
                    row.Target,
                    row.PathColumn,
                    row.SearchText,
                    row.Description,
                    row.ProjectUpdateStatus,
                    row.ScheduledDate,
                    row.ExternalSourceName
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));

            var parts = ParseFlexibleSearchParts(query);

            if (parts.Count == 0)
                return true;

            return parts.All(part =>
                part.IsExact
                    ? ContainsExactSearchPart(
                        searchable,
                        part.Value)
                    : searchable.Contains(
                        part.Value,
                        StringComparison.OrdinalIgnoreCase));
        }

        private sealed record FlexibleSearchPart(
            string Value,
            bool IsExact);

        private static IReadOnlyList<FlexibleSearchPart>
            ParseFlexibleSearchParts(string query)
        {
            var result =
                new List<FlexibleSearchPart>();

            var raw =
                (query ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            var matches = Regex.Matches(
                raw,
                "\\\"(?<exact>[^\\\"]+)\\\"|(?<normal>\\S+)",
                RegexOptions.CultureInvariant);

            foreach (Match match in matches)
            {
                var exact =
                    match.Groups["exact"].Success;

                var value = exact
                    ? match.Groups["exact"].Value
                    : match.Groups["normal"].Value;

                value = value.Trim();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(
                        new FlexibleSearchPart(
                            value,
                            exact));
                }
            }

            return result;
        }

        private static bool ContainsExactSearchPart(
            string searchable,
            string exactValue)
        {
            var value =
                (exactValue ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(value))
                return true;

            // Una frase con espacios o un dominio se busca completa y en orden.
            // Para tags simples se exigen límites para impedir que zREVISION
            // coincida dentro de rtuzREVISION o prtuzREVISION.
            if (value.Any(char.IsWhiteSpace) ||
                value.Contains('.') ||
                value.Contains('/') ||
                value.Contains('\\'))
            {
                return searchable.Contains(
                    value,
                    StringComparison.OrdinalIgnoreCase);
            }

            var pattern =
                $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(value)}(?![\p{{L}}\p{{Nd}}_])";

            return Regex.IsMatch(
                searchable,
                pattern,
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
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

            var sourceLabel = GetSourceScopeLabel();

            BreadcrumbText.Text = string.IsNullOrWhiteSpace(original)
                ? sourceLabel == "Todo"
                    ? "Todos los resultados"
                    : $"Origen: {sourceLabel}"
                : sourceLabel == "Todo"
                    ? $"Buscar: {original}"
                    : $"Origen: {sourceLabel} · Buscar: {original}";
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

                "scheduled_asc" => items
                    .OrderBy(x => HasScheduledDateForSort(x) ? 0 : 1)
                    .ThenBy(x => ParseScheduledDateForSort(x.ScheduledDate))
                    .ThenBy(x => GetPathOrderRank(x))
                    .ThenBy(x => x.DisplayName ?? x.Name),

                "scheduled_desc" => items
                    .OrderBy(x => HasScheduledDateForSort(x) ? 0 : 1)
                    .ThenByDescending(x => ParseScheduledDateForSort(x.ScheduledDate))
                    .ThenBy(x => GetPathOrderRank(x))
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

        private async void HeaderScheduledDateSort_Click(
            object sender,
            RoutedEventArgs e)
        {
            _sortKey =
                _sortKey == "scheduled_asc"
                    ? "scheduled_desc"
                    : "scheduled_asc";

            if (ScheduledDateSortArrow != null)
            {
                ScheduledDateSortArrow.Text =
                    _sortKey == "scheduled_asc"
                        ? "▲"
                        : "▼";
            }

            if (NameSortArrow != null)
                NameSortArrow.Text = string.Empty;

            if (ModifiedSortArrow != null)
                ModifiedSortArrow.Text = string.Empty;

            await RunSearchAsync(
                (SearchBox?.Text ?? string.Empty).Trim());

            StatusText.Text =
                _sortKey == "scheduled_asc"
                    ? "Estado: Fecha por hacer ordenada de antigua a reciente ✅"
                    : "Estado: Fecha por hacer ordenada de reciente a antigua ✅";
        }

        private static bool HasScheduledDateForSort(
            Anfeta.UI.Models.Weblab.SearchResultRow row)
        {
            return ParseScheduledDateForSort(
                       row.ScheduledDate) !=
                   DateTime.MaxValue;
        }

        private static DateTime ParseScheduledDateForSort(
            string? value)
        {
            var raw =
                (value ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return DateTime.MaxValue;

            var separatorIndex =
                raw.IndexOf(
                    " - ",
                    StringComparison.Ordinal);

            if (separatorIndex > 0)
            {
                raw =
                    raw.Substring(
                        0,
                        separatorIndex).Trim();
            }

            if (DateTimeOffset.TryParse(
                    raw,
                    out var offset))
            {
                return offset.LocalDateTime;
            }

            if (DateTime.TryParse(
                    raw,
                    out var date))
            {
                return date;
            }

            return DateTime.MaxValue;
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


        private IReadOnlyList<Anfeta.UI.Models.Weblab.SearchResultRow> BuildSpeechResults(
            IEnumerable<Anfeta.UI.Models.Weblab.SearchResultRow> rows)
        {
            var query = (SearchBox?.Text ?? string.Empty).Trim();
            var queryParts = ParseFlexibleSearchParts(query)
                .Select(x => x.Value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            return rows.Select(row =>
            {
                var clean = CleanResultSpeechText(row.DisplayName ?? row.Name, queryParts);
                return new Anfeta.UI.Models.Weblab.SearchResultRow
                {
                    Name = string.IsNullOrWhiteSpace(clean) ? row.DisplayName : clean,
                    Target = row.Target,
                    Type = row.Type,
                    Source = row.Source,
                    ExternalId = row.ExternalId,
                    ExternalUrl = row.ExternalUrl,
                    ExternalSourceName = row.ExternalSourceName
                };
            }).ToList();
        }

        private static string CleanResultSpeechText(
            string? value,
            IReadOnlyList<string> queryParts)
        {
            var text = value ?? string.Empty;
            text = Regex.Replace(text,
                @"(?<![\p{L}\p{Nd}_])(?:prtuzREVISION|rtuzREVISION|zREVISION|sprtuzREVISION)(?![\p{L}\p{Nd}_])",
                " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"\bRevisiones\b", " ", RegexOptions.IgnoreCase);

            foreach (var part in queryParts)
            {
                var cleanPart = part.Trim('"', '\'', ' ');
                if (cleanPart.Length < 3) continue;
                text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(cleanPart)}(?![\p{{L}}\p{{Nd}}_])", " ", RegexOptions.IgnoreCase);
            }

            return Regex.Replace(text, @"\s+", " ")
                .Trim(' ', '-', '–', '—', ':', '|', '/');
        }

        #endregion
    }
}