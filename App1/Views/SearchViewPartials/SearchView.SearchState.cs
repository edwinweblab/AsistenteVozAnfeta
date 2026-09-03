using Anfeta.UI.Models;
using Anfeta.UI.Models.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private string? _lastCompletedSearchQuery;
        private SearchTabState? _stagedSearchState;
        private static readonly List<WeakReference<SearchView>> PresetViews = new();
        private bool _refreshingPresets;

        private SearchCriteriaState CaptureSearchCriteria() => new()
        {
            Source = _activeSourceScope.ToString(),
            Base = _activeNotionBaseFilter,
            Payment = _activePaymentBaseTitleFilter,
            Extension = _extFilter,
            Programs = _programasQuickFilter,
            Bookmarks = _onlyBookmarks,
            Folders = _onlyFolders,
            Grouping = _resultGroupingMode.ToString(),
            Sort = _sortKey,
            Match = JsonSerializer.Deserialize<QueryMatchOptions>(JsonSerializer.Serialize(_currentMatchOptions))
        };

        private void ApplySearchCriteria(SearchCriteriaState? state)
        {
            if (state == null) return; // Older saved searches remain compatible.
            _loadingModulePreferences = true;
            try
            {
                _activeSourceScope = Enum.TryParse<SearchSourceScope>(state.Source, out var source) ? source : SearchSourceScope.All;
                _activeNotionBaseFilter = state.Base ?? "";
                _activePaymentBaseTitleFilter = state.Payment ?? "";
                _extFilter = state.Extension;
                _programasQuickFilter = state.Programs;
                _onlyBookmarks = state.Bookmarks;
                _onlyFolders = state.Folders;
                _resultGroupingMode = Enum.TryParse<ResultGroupingMode>(state.Grouping, out var grouping) ? grouping : ResultGroupingMode.None;
                _sortKey = state.Sort ?? "name_asc";
                _currentMatchOptions = state.Match ?? new QueryMatchOptions();
                SetSourceScopeChipChecks();
                SetNotionBaseChipChecks(_activeNotionBaseFilter, _activePaymentBaseTitleFilter);
                ChipBookmarks.IsChecked = _onlyBookmarks;
                ChipFolders.IsChecked = _onlyFolders;
                ChipPdf.IsChecked = _extFilter == "pdf";
                ChipDocx.IsChecked = _extFilter == "docx";
                ChipXlsx.IsChecked = _extFilter == "xlsx";
                ChipImg.IsChecked = _extFilter == "img";
                ChipUrl.IsChecked = _extFilter == "url";
                SelectComboItemByTag(GroupResultsCombo, _resultGroupingMode.ToString().ToLowerInvariant());
                UpdateColumnSortIndicators();
            }
            finally { _loadingModulePreferences = false; }
        }

        private async Task RefreshSharedSearchPresetsAsync()
        {
            if (_refreshingPresets) return;
            _refreshingPresets = true;
            try { LoadSavedSearches(); await LoadSavedFiltersAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            finally { _refreshingPresets = false; }
        }

        private void NotifySearchPresetsChanged()
        {
            PresetViews.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in PresetViews.ToArray())
                if (reference.TryGetTarget(out var view) && !ReferenceEquals(view, this) && view.IsLoaded && !view.DeferInitialIndexPaint)
                    view.DispatcherQueue.TryEnqueue(async () => await view.RefreshSharedSearchPresetsAsync());
        }

        private string CurrentSearchSaveName()
        {
            var query = (SearchBox.Text ?? "").Trim();
            if (query.Length > 0) return query;
            var state = CaptureSearchCriteria();
            return string.Join(" · ", new[] { state.Source != "All" ? state.Source : "", state.Base,
                state.Payment, state.Programs ? "Programas" : "", state.Extension ?? "",
                state.Bookmarks ? "Favoritos" : "", state.Folders ? "Carpetas" : "" }.Where(x => x.Length > 0));
        }

        private async Task ApplySavedQuickSearchAsync(SavedSearch saved)
        {
            CancelPendingSearch();
            ApplySearchCriteria(saved.Criteria ?? new SearchCriteriaState());
            QuickCommandsInputFlyout?.Hide();
            SearchBox.Text = saved.Query;
            SetTabTitle(saved.Query);
            NotifyWorkspaceChanged();
            QueueCalendarWindowFilterSync(saved.Query);
            await RunSearchAsync(saved.Query);
        }

        // State is taken from the title's leading token, never from body text
        // or arbitrary substrings such as "prtuz" containing "rtuz".
        private static readonly Regex WorkflowPrefix = new(
            @"^\s*(?:\[[^\]]+\]\s*)?(?<tag>aprtuz|prtuz|rtuz|ztuz|z)REVISION\b|^\s*(?<tag>aprtuz|prtuz|rtuz|ztuz)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Orden solicitado en reunión por John:
        // A -> P -> R -> Z -> Otros -> Cobros/Pagos/Referencias.
        // sprtuz no se reclasifica sin una regla explícita: permanece en OTROS.
        internal const int WorkflowAprtuz = 0;
        internal const int WorkflowPrtuz = 1;
        internal const int WorkflowRtuz = 2;
        internal const int WorkflowFinished = 3;
        internal const int WorkflowOther = 4;
        internal const int WorkflowBillingReferences = 5;

        internal static int GetWorkflowState(string? title)
        {
            var match = WorkflowPrefix.Match(title ?? "");

            return match.Groups["tag"].Value.ToLowerInvariant() switch
            {
                "aprtuz" => WorkflowAprtuz,
                "prtuz" => WorkflowPrtuz,
                "rtuz" => WorkflowRtuz,
                "ztuz" or "z" => WorkflowFinished,
                _ => WorkflowOther
            };
        }

        private static string GetWorkflowStateLabel(int state) => state switch
        {
            WorkflowAprtuz => "A · PRIORIDAD / APRTU",
            WorkflowPrtuz => "P · PENDIENTES / PRTU",
            WorkflowRtuz => "R · SOLICITUDES DE REVISIÓN",
            WorkflowFinished => "Z · TERMINADOS",
            WorkflowOther => "OTROS",
            WorkflowBillingReferences => "COBROS / PAGOS / REFERENCIAS",
            _ => "OTROS"
        };

        private void AddQuickCriteriaSuggestions(string query)
        {
            var current = NormalizeSpacesForQuery(query ?? string.Empty);

            // BÚSQUEDAS RÁPIDAS DEL INPUT
            // Estas opciones NO cambian los chips superiores ni sus estados
            // (_extFilter, _activeNotionBaseFilter, etc.). Solamente agregan
            // texto visible/editable al SearchBox y usan el motor normal.
            var quickItems = new[]
            {
                (Title: "PDF", Query: "ext:pdf", Subtitle: "Agregar ext:pdf"),
                (Title: "Documentos", Query: "ext:doc;docx", Subtitle: "Agregar DOC / DOCX"),
                (Title: "Hojas de cálculo", Query: "ext:xls;xlsx", Subtitle: "Agregar XLS / XLSX"),
                (Title: "Imágenes", Query: "ext:png;jpg;jpeg;webp;gif;bmp", Subtitle: "Filtrar imágenes"),

                (Title: "Pendientes", Query: "prtuzREVISION", Subtitle: "Agregar prtuzREVISION"),
                (Title: "Solicitudes de revisión", Query: "rtuzREVISION", Subtitle: "Agregar rtuzREVISION"),
                (Title: "Terminados", Query: "zREVISION", Subtitle: "Agregar zREVISION"),
                (Title: "Programas", Query: "pprog", Subtitle: "Agregar tag pprog"),
                (Title: "Biblioteca", Query: "bbibl", Subtitle: "Agregar tag bbibl"),
                (Title: "Respuesta", Query: "[RESPUESTA]", Subtitle: "Agregar [RESPUESTA]"),

                (Title: "Brian", Query: "bbria", Subtitle: "Buscar actividades de Brian"),
                (Title: "Genaro", Query: "ggena", Subtitle: "Buscar actividades de Genaro"),
                (Title: "Isaias", Query: "iisai", Subtitle: "Buscar actividades de Isaias"),
                (Title: "Karla", Query: "kKarl", Subtitle: "Buscar actividades de Karla"),
                (Title: "Neftali", Query: "nNeft", Subtitle: "Buscar actividades de Neftali")
            };

            var currentTerms = SplitAutoAndTerms(current)
                .Select(NormalizeSpacesForQuery)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in quickItems)
            {
                if (currentTerms.Contains(item.Query) ||
                    _predictiveSuggestions.Any(s =>
                        string.Equals(
                            NormalizeSpacesForQuery(s.Query ?? string.Empty),
                            item.Query,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _predictiveSuggestions.Add(new PredictiveSuggestion
                {
                    Title = item.Title,
                    Query = item.Query,
                    Subtitle = item.Subtitle,
                    Kind = "Criterio"
                });
            }

            // Bases de Notion también se ofrecen como búsquedas rápidas del input.
            // Al seleccionarlas se escribe el alias en SearchBox y el motor ya
            // existente resuelve la base correspondiente.
            if (_activeSourceScope != SearchSourceScope.Dropbox)
            {
                foreach (var item in GetNotionBaseShortcuts())
                {
                    if (_predictiveSuggestions.Any(s =>
                        string.Equals(
                            s.Query,
                            item.PrimaryAlias,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    _predictiveSuggestions.Add(new PredictiveSuggestion
                    {
                        Title = item.DisplayLabel,
                        Query = item.PrimaryAlias,
                        Subtitle = $"Agregar {item.PrimaryAlias} al buscador",
                        Kind = "Base"
                    });
                }
            }
        }
    }
}
