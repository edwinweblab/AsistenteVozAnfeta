using Anfeta.UI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;


namespace Anfeta.UI.Views
{
    public sealed partial class SearchTabsView : Page
    {
        private int _tabCounter = 1;
        private const string LS_TabsWorkspace = "SearchTabsWorkspace";
        private bool _restoring;
        private bool _workspaceRestored;
        private bool _restoreOnce;

        // Las pestañas no seleccionadas conservan su estado, pero no ejecutan
        // búsquedas ni pintan miles de resultados hasta que el usuario las abre.
        private readonly Dictionary<SearchView, SearchTabState>
            _pendingTabStates = new();

        private sealed class TabHeaderVisual
        {
            public StackPanel Root { get; init; } = null!;
            public FontIcon Icon { get; init; } = null!;
            public TextBlock Title { get; init; } = null!;
        }

        private readonly Dictionary<SearchView, TabHeaderVisual>
            _tabHeaderVisuals = new();


        public SearchTabsView()
        {
            InitializeComponent();

            // El SearchView declarado en XAML no debe bootstrappear antes de
            // saber si existe un workspace que lo va a reemplazar.
            FirstSearchView.DeferInitialIndexPaint = true;

            // Hook del primer tab (Buscar 1)
            if (Tabs.TabItems.Count > 0)
            {
                var firstTab = (TabViewItem)Tabs.TabItems[0];
                HookTabTitle(firstTab, FirstSearchView);
            }

            Loaded += SearchTabsView_Loaded;

            // ✅ NO lambda, método real
            Tabs.SelectionChanged += Tabs_SelectionChanged;
        }
        private void Tabs_AddTabButtonClick(TabView sender, object args)
        {
            AddNewSearchTab();
        }

        public SearchView AddNewSearchTab()
        {
            _tabCounter++;

            // La nueva pestaña aparece primero y se materializa después de que
            // WinUI pinte el cambio de selección. Evita el tirón de construir
            // filtros/resultados en el mismo frame del clic en + / Ctrl+T.
            var view = new SearchView
            {
                DeferInitialIndexPaint = true
            };

            var tab = new TabViewItem
            {
                Header = $"Buscar {_tabCounter}",
                Content = view,
                IsClosable = true
            };

            HookTabTitle(tab, view);
            Tabs.TabItems.Add(tab);
            Tabs.SelectedItem = tab;
            SaveWorkspace();

            return view;
        }

        private void Tabs_TabCloseRequested(
            TabView sender,
            TabViewTabCloseRequestedEventArgs args)
        {
            if (sender.TabItems.Count <= 1)
                return; // no cerrar el último

            if (args.Tab?.Content is SearchView closingView)
            {
                (Application.Current as App)?.ReleaseCalendarSearchOwner(closingView);
                closingView.SuspendAsBackgroundTab();
                _pendingTabStates.Remove(closingView);
                _tabHeaderVisuals.Remove(closingView);
            }

            sender.TabItems.Remove(args.Tab);
            SaveWorkspace();
        }

        private void HookTabTitle(TabViewItem tab, SearchView view)
        {
            var initialTitle =
                tab.Header?.ToString() ?? "Buscar";

            var header = CreateTabHeader(
                initialTitle,
                view.CurrentTabMode);

            _tabHeaderVisuals[view] = header;
            tab.Header = header.Root;

            view.TabTitleChanged += (_, title) =>
            {
                if (_tabHeaderVisuals.TryGetValue(view, out var current))
                    current.Title.Text = title;
            };

            view.TabModeChanged += (_, mode) =>
            {
                if (_tabHeaderVisuals.TryGetValue(view, out var current))
                    ApplyTabModeVisual(current, view.ControlsCalendarSearch ? "linked-calendar" : mode);
            };

            view.CalendarSearchOwnerChanged += (_, __) =>
            {
                if (_tabHeaderVisuals.TryGetValue(view, out var current))
                    ApplyTabModeVisual(current, view.ControlsCalendarSearch ? "linked-calendar" : view.CurrentTabMode);
            };

            view.WorkspaceChanged += (_, __) => SaveWorkspace();
        }

        private static TabHeaderVisual CreateTabHeader(
            string title,
            string mode)
        {
            var icon = new FontIcon
            {
                FontSize = 12
            };

            var titleText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(title)
                    ? "Buscar"
                    : title,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 190,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            root.Children.Add(icon);
            root.Children.Add(titleText);

            var visual = new TabHeaderVisual
            {
                Root = root,
                Icon = icon,
                Title = titleText
            };

            ApplyTabModeVisual(visual, mode);
            return visual;
        }

        private static void ApplyTabModeVisual(
            TabHeaderVisual visual,
            string mode)
        {
            var isCalendar = string.Equals(
                mode,
                "calendar",
                StringComparison.OrdinalIgnoreCase);

            visual.Icon.Glyph = isCalendar
                ? "\uE787"   // Calendar
                : mode == "linked-calendar" ? "\uE787" : "\uE721";

            ToolTipService.SetToolTip(
                visual.Root,
                mode == "linked-calendar" ? "Buscador vinculado: solo esta pestaña filtra el calendario independiente" : isCalendar
                    ? "Esta pestaña está en Calendario"
                    : "Esta pestaña está en Resultados / Buscador");
        }

        private void SaveWorkspace()
        {
            if (_restoring) return;

            var ws = new SearchTabsWorkspace
            {
                Version = 1,
                SelectedIndex = Tabs.SelectedIndex
            };

            foreach (var item in Tabs.TabItems)
            {
                if (item is not TabViewItem tab) continue;

                // Content puede ser SearchView directo o un x:Name inicial
                if (tab.Content is SearchView sv)
                    ws.Tabs.Add(sv.GetTabState());
            }

            var json = JsonSerializer.Serialize(ws);
            ApplicationData.Current.LocalSettings.Values[LS_TabsWorkspace] = json;
        }
        private async Task RestoreWorkspaceAsync()
        {
            if (_restoring) return;
            _restoring = true;
            _workspaceRestored = false;

            try
            {
                var ls = ApplicationData.Current.LocalSettings.Values;
                var json = ls[LS_TabsWorkspace] as string;

                if (string.IsNullOrWhiteSpace(json))
                {
                    _workspaceRestored = true;
                    await FirstSearchView.ActivateDeferredTabAsync();
                    return;
                }

                SearchTabsWorkspace? ws = null;

                // 1) Deserialización segura
                try
                {
                    ws = JsonSerializer.Deserialize<SearchTabsWorkspace>(json);
                }
                catch
                {
                    ls.Remove(LS_TabsWorkspace);
                    _workspaceRestored = true;
                    await FirstSearchView.ActivateDeferredTabAsync();
                    return;
                }

                // 2) Validación
                if (ws == null || ws.Tabs == null || ws.Tabs.Count == 0 || ws.Version != 1)
                {
                    ls.Remove(LS_TabsWorkspace);
                    _workspaceRestored = true;
                    await FirstSearchView.ActivateDeferredTabAsync();
                    return;
                }

                // Normaliza SelectedIndex contra ws.Tabs (no contra TabItems todavía)
                if (ws.SelectedIndex < 0) ws.SelectedIndex = 0;
                if (ws.SelectedIndex >= ws.Tabs.Count) ws.SelectedIndex = ws.Tabs.Count - 1;

                // 3) Evitar eventos mientras restauras (IMPORTANTÍSIMO)
                Tabs.SelectionChanged -= Tabs_SelectionChanged;

                // (Opcional pero recomendado si alguna vez guardas en Add/Close)
                Tabs.AddTabButtonClick -= Tabs_AddTabButtonClick;
                Tabs.TabCloseRequested -= Tabs_TabCloseRequested;

                // 4) Reset selección ANTES de tocar TabItems
                Tabs.SelectedItem = null;
                Tabs.SelectedIndex = -1;
                Tabs.UpdateLayout();
                await Task.Yield();

                Tabs.TabItems.Clear();
                _pendingTabStates.Clear();
                _tabHeaderVisuals.Clear();
                _tabCounter = 0;

                // 5) Crear TODOS los tabs en modo diferido. Incluso el que estaba
                // seleccionado al cerrar ANFETA aparece primero y se materializa
                // después; así restaurar varias pestañas no congela el arranque.
                for (int i = 0; i < ws.Tabs.Count; i++)
                {
                    _tabCounter++;

                    var view = new SearchView
                    {
                        DeferInitialIndexPaint = true
                    };

                    var tab = new TabViewItem
                    {
                        Header = $"Buscar {_tabCounter}",
                        Content = view,
                        IsClosable = _tabCounter != 1
                    };

                    HookTabTitle(tab, view);
                    Tabs.TabItems.Add(tab);

                    view.StageDeferredTabState(ws.Tabs[i]);
                    _pendingTabStates[view] = ws.Tabs[i];
                }

                // 6) Deja que WinUI monte primero encabezados/contenidos ligeros.
                Tabs.UpdateLayout();
                await Task.Yield();
                await Task.Yield();

                // 7) Seleccionar tab activo y darle un frame para que el usuario
                // vea la pestaña antes de cargar filtros/resultados.
                if (Tabs.TabItems.Count > 0)
                    Tabs.SelectedIndex = Math.Clamp(ws.SelectedIndex, 0, Tabs.TabItems.Count - 1);

                Tabs.UpdateLayout();
                await Task.Yield();

                // 8) Materializar SOLO el tab activo. Los demás quedan staged.
                if (Tabs.SelectedItem is TabViewItem selectedTab &&
                    selectedTab.Content is SearchView selectedView)
                {
                    await WaitForLoadedAsync(selectedView);
                    await ActivateSelectedDeferredTabAsync();
                }

                // 9) Ya puedes permitir guardados.
                _workspaceRestored = true;
            }
            finally
            {
                // Reenganchar eventos
                Tabs.SelectionChanged -= Tabs_SelectionChanged;
                Tabs.SelectionChanged += Tabs_SelectionChanged;

                Tabs.AddTabButtonClick -= Tabs_AddTabButtonClick;
                Tabs.AddTabButtonClick += Tabs_AddTabButtonClick;

                Tabs.TabCloseRequested -= Tabs_TabCloseRequested;
                Tabs.TabCloseRequested += Tabs_TabCloseRequested;

                _restoring = false;
            }
        }
        private async void SearchTabsView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_restoreOnce) return;
            _restoreOnce = true;

            // Permite que primero aparezcan shell + tabs y después materializa
            // el contenido pesado del tab activo.
            await Task.Yield();
            await RestoreWorkspaceAsync();
            if (Tabs.TabItems.Count > 0 && Tabs.TabItems[0] is TabViewItem first && first.Content is SearchView primary)
                (Application.Current as App)?.RegisterCalendarSearchOwner(primary);
        }

        private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoring) return;
            if (!_workspaceRestored) return;

            SuspendBackgroundTabs();
            SaveWorkspace();

            // Da un frame a WinUI para pintar la pestaña/indicador antes de
            // materializar resultados, filtros o watchers de la nueva vista.
            await Task.Yield();
            await ActivateSelectedDeferredTabAsync();
        }

        private void SuspendBackgroundTabs()
        {
            var selected =
                (Tabs.SelectedItem as TabViewItem)?.Content as SearchView;

            foreach (var item in Tabs.TabItems)
            {
                if (item is not TabViewItem tab ||
                    tab.Content is not SearchView view ||
                    ReferenceEquals(view, selected))
                {
                    continue;
                }

                view.SuspendAsBackgroundTab();
            }
        }

        private async Task ActivateSelectedDeferredTabAsync()
        {
            if (Tabs.SelectedItem is not TabViewItem tab ||
                tab.Content is not SearchView view)
            {
                return;
            }

            // La pestaña ya está seleccionada/visible. Esperamos únicamente a que
            // WinUI termine su Loaded antes de tocar filtros, watchers o XamlRoot.
            await WaitForLoadedAsync(view);

            // Si el usuario cambió otra vez de pestaña mientras terminaba Loaded,
            // no iniciamos trabajo para una vista que ya quedó en segundo plano.
            if (!IsSelectedView(view))
            {
                view.SuspendAsBackgroundTab();
                return;
            }

            if (_pendingTabStates.TryGetValue(view, out var pendingState))
            {
                _pendingTabStates.Remove(view);
                await view.ActivateDeferredTabAsync(pendingState);
                SuspendIfNoLongerSelected(view);
                return;
            }

            if (view.DeferInitialIndexPaint)
            {
                await view.ActivateDeferredTabAsync();
                SuspendIfNoLongerSelected(view);
                return;
            }

            // Si otra pestaña sincronizó el índice, esta vista se actualiza desde
            // memoria al seleccionarla. No realiza ninguna petición externa.
            await view.RefreshFromSharedIndexIfChangedAsync();
            SuspendIfNoLongerSelected(view);
        }

        private bool IsSelectedView(SearchView view)
        {
            return Tabs.SelectedItem is TabViewItem selectedTab &&
                   ReferenceEquals(selectedTab.Content, view);
        }

        private void SuspendIfNoLongerSelected(SearchView view)
        {
            if (!IsSelectedView(view))
                view.SuspendAsBackgroundTab();
        }
        private static Task WaitForLoadedAsync(FrameworkElement element)
        {
            var tcs = new TaskCompletionSource();

            if (element.IsLoaded)
            {
                tcs.SetResult();
                return tcs.Task;
            }

            RoutedEventHandler? handler = null;
            handler = (_, __) =>
            {
                element.Loaded -= handler;
                tcs.SetResult();
            };

            element.Loaded += handler;
            return tcs.Task;
        }
        private static async Task WaitForIndexReadyAsync()
        {
            // Espera hasta 2 segundos a que el índice esté listo
            for (int i = 0; i < 40; i++)
            {
                if (App.LocalIndex.HasData) return;
                await Task.Delay(50);
            }
        }
        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (Tabs.TabItems.Count > 0 && Tabs.SelectedIndex < 0)
                Tabs.SelectedIndex = 0;

            Tabs.UpdateLayout();
        }
        private async Task RestoreSelectedTabStateAsync(SearchTabsWorkspace ws)
        {
            var idx = Tabs.SelectedIndex;
            if (idx < 0 || idx >= Tabs.TabItems.Count) return;

            if (Tabs.TabItems[idx] is TabViewItem tab && tab.Content is SearchView view)
            {
                await view.RestoreTabStateAsync(ws.Tabs[idx]);
            }
        }

        private Task RestoreOtherTabsStateAsync(SearchTabsWorkspace ws)
        {
            // Conservado por compatibilidad interna. Desde Performance Cache v1
            // los tabs ocultos ya se preparan con StageDeferredTabState durante
            // RestoreWorkspaceAsync y no ejecutan trabajo aquí.
            return Task.CompletedTask;
        }
    }
}
