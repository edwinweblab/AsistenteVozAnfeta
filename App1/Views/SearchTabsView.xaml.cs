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

            var view = new SearchView
            {
                DeferInitialIndexPaint = false
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

            DispatcherQueue.TryEnqueue(async () =>
            {
                await WaitForLoadedAsync(view);
                await view.ApplyDefaultTagIfEmptyAsync();
            });

            return view;
        }

        private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (sender.TabItems.Count <= 1) return; // no cerrar el último

            if (args.Tab?.Content is SearchView closingView)
                _pendingTabStates.Remove(closingView);

            sender.TabItems.Remove(args.Tab);
            SaveWorkspace();
        }

        private void HookTabTitle(TabViewItem tab, SearchView view)
        {
            view.TabTitleChanged += (_, title) => tab.Header = title;

            // 🔥 nuevo: cuando el SearchView cambie estado, guardamos workspace
            view.WorkspaceChanged += (_, __) => SaveWorkspace();
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
                _tabCounter = 0;

                // 5) Crear tabs
                for (int i = 0; i < ws.Tabs.Count; i++)
                {
                    _tabCounter++;

                    var isSelectedTab =
                        i == ws.SelectedIndex;

                    var view = new SearchView
                    {
                        DeferInitialIndexPaint = !isSelectedTab
                    };

                    var tab = new TabViewItem
                    {
                        Header = $"Buscar {_tabCounter}",
                        Content = view,
                        IsClosable = _tabCounter != 1
                    };

                    HookTabTitle(tab, view);
                    Tabs.TabItems.Add(tab);

                    if (!isSelectedTab)
                    {
                        view.StageDeferredTabState(ws.Tabs[i]);
                        _pendingTabStates[view] = ws.Tabs[i];
                    }
                }

                // 6) Deja que WinUI “monte” los contenidos antes de seleccionar
                Tabs.UpdateLayout();
                await Task.Yield();
                await Task.Yield();

                // 7) Seleccionar tab activo
                if (Tabs.TabItems.Count > 0)
                    Tabs.SelectedIndex = Math.Clamp(ws.SelectedIndex, 0, Tabs.TabItems.Count - 1);

                Tabs.UpdateLayout();
                await Task.Yield();

                // 8) Restaurar estado del tab seleccionado primero
                await WaitForIndexReadyAsync();
                await RestoreSelectedTabStateAsync(ws);

                // 9) Los demás tabs ya quedaron staged en memoria y se
                // materializan solo cuando el usuario los selecciona.

                // 10) Ya puedes permitir guardados
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

            await RestoreWorkspaceAsync();
        }

        private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoring) return;
            if (!_workspaceRestored) return;

            SaveWorkspace();
            await ActivateSelectedDeferredTabAsync();
        }

        private async Task ActivateSelectedDeferredTabAsync()
        {
            if (Tabs.SelectedItem is not TabViewItem tab ||
                tab.Content is not SearchView view)
            {
                return;
            }

            if (_pendingTabStates.TryGetValue(view, out var pendingState))
            {
                _pendingTabStates.Remove(view);
                await view.ActivateDeferredTabAsync(pendingState);
                return;
            }

            if (view.DeferInitialIndexPaint)
            {
                await view.ActivateDeferredTabAsync();
                return;
            }

            // Si otra pestaña sincronizó el índice, esta vista se actualiza desde
            // memoria al seleccionarla. No realiza ninguna petición externa.
            await view.RefreshFromSharedIndexIfChangedAsync();
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