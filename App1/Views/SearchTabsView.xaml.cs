using Anfeta.UI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
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


        public SearchTabsView()
        {
            InitializeComponent();

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
            _tabCounter++;

            var view = new SearchView();

            var tab = new TabViewItem
            {
                Header = $"Buscar {_tabCounter}",
                Content = view,
                IsClosable = true
            };

            HookTabTitle(tab, view);

            sender.TabItems.Add(tab);
            sender.SelectedItem = tab;
            SaveWorkspace();
        }

        private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (sender.TabItems.Count <= 1) return; // no cerrar el último
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
                    return;

                SearchTabsWorkspace? ws = null;

                // 1) Deserialización segura
                try
                {
                    ws = JsonSerializer.Deserialize<SearchTabsWorkspace>(json);
                }
                catch
                {
                    ls.Remove(LS_TabsWorkspace);
                    return;
                }

                // 2) Validación
                if (ws == null || ws.Tabs == null || ws.Tabs.Count == 0 || ws.Version != 1)
                {
                    ls.Remove(LS_TabsWorkspace);
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
                _tabCounter = 0;

                // 5) Crear tabs
                for (int i = 0; i < ws.Tabs.Count; i++)
                {
                    _tabCounter++;

                    var view = new SearchView();
                    var tab = new TabViewItem
                    {
                        Header = $"Buscar {_tabCounter}",
                        Content = view,
                        IsClosable = _tabCounter != 1
                    };

                    HookTabTitle(tab, view);
                    Tabs.TabItems.Add(tab);
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

                // 9) Restaurar los demás sin bloquear
                _ = RestoreOtherTabsStateAsync(ws);

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

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoring) return;
            if (!_workspaceRestored) return;   // 🔥 clave
            SaveWorkspace();
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

        private async Task RestoreOtherTabsStateAsync(SearchTabsWorkspace ws)
        {
            // espera un tick para que la UI se pinte
            await Task.Delay(50);

            for (int i = 0; i < Tabs.TabItems.Count && i < ws.Tabs.Count; i++)
            {
                if (i == Tabs.SelectedIndex) continue;

                if (Tabs.TabItems[i] is TabViewItem tab && tab.Content is SearchView view)
                {
                    // NO esperes Loaded: RestoreTabStateAsync debe ser safe sin depender de timer
                    await view.RestoreTabStateAsync(ws.Tabs[i]);
                }
            }
        }
    }
}