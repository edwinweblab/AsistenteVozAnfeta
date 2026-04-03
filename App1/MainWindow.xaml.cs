using Anfeta.UI.FunctionTests;
using Anfeta.UI.Services.Search;
using Anfeta.UI.ViewModels;
using Anfeta.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.Foundation;
using Windows.UI;

namespace Anfeta.UI
{
    public sealed partial class MainWindow : Window
    {
        private readonly ShellViewModel _shell;

        // Evita buscar el overlay más de una vez una vez que ya fue desactivado.
        private bool _lightDismissDisabled = false;

        public MainWindow()
        {
            InitializeComponent();

            _shell = App.AppHost.Services.GetRequiredService<ShellViewModel>();
            Root.DataContext = _shell;

            _shell.RequestOpenLinkAccount += () =>
            {
                if (ContentFrame?.CurrentSourcePageType != typeof(LinkAccountView))
                    ContentFrame?.Navigate(typeof(LinkAccountView));
            };

            _shell.RequestOpenLinkSharedAccount += () =>
            {
                if (ContentFrame?.CurrentSourcePageType != typeof(LinkSharedAccountView))
                    ContentFrame?.Navigate(typeof(LinkSharedAccountView));
            };

            if (ContentFrame != null)
                ContentFrame.Navigate(typeof(HomeView));

            if (AppNav?.MenuItems.Count > 0)
                AppNav.SelectedItem = AppNav.MenuItems[0];

            AppNav.Loaded += AppNav_Loaded;
            AppNav.PaneOpened += AppNav_PaneOpened;
            AppNav.PaneClosed += (s, e) => VersionFooter.Visibility = Visibility.Collapsed;

            SubscribeDropboxState();
            CheckGoogleCalendarStatusOnStartup();

            this.Closed += MainWindow_Closed;

            Debug.WriteLine("MAINWINDOW: constructor OK");
        }

        // ═══════════════════════════════════════════
        // Nav — Clip
        // ═══════════════════════════════════════════

        private void AppNav_Loaded(object sender, RoutedEventArgs e)
        {
            // Recorta el NavigationView a OpenPaneLength para que el ContentGrid
            // interno no tape el Frame que está detrás.
            AppNav.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, AppNav.OpenPaneLength, 10000)
            };

            Debug.WriteLine($"[NAV] Clip aplicado: {AppNav.OpenPaneLength}px");
        }

        // ═══════════════════════════════════════════
        // Nav — LightDismissLayer
        // Se busca en PaneOpened porque el elemento solo existe en el árbol
        // visual después de que el pane se abre por primera vez.
        // ═══════════════════════════════════════════

        private void AppNav_PaneOpened(NavigationView sender, object args)
        {
            VersionFooter.Visibility = Visibility.Visible;

            if (_lightDismissDisabled) return;

            var overlay = FindDescendantByName(AppNav, "LightDismissLayer");
            if (overlay is FrameworkElement fe)
            {
                fe.Opacity = 0;
                fe.IsHitTestVisible = false;
                _lightDismissDisabled = true;
                Debug.WriteLine("[NAV] LightDismissLayer desactivado.");
            }
            else
            {
                // Si no se encontró, volcamos todos los elementos con nombre
                // para identificar el nombre real en esta versión de WinUI 3.
                Debug.WriteLine("[NAV] LightDismissLayer no encontrado — volcando árbol visual:");
                DumpNamedDescendants(AppNav, 0);
            }
        }

        // Busca un FrameworkElement por nombre en el árbol visual descendente.
        // Entrada: parent, name. Salida: DependencyObject? o null.
        private static DependencyObject? FindDescendantByName(DependencyObject parent, string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && fe.Name == name)
                    return child;

                var result = FindDescendantByName(child, name);
                if (result != null) return result;
            }
            return null;
        }

        // Vuelca en Debug todos los FrameworkElement con nombre no vacío.
        // Entrada: parent, depth — nivel de indentación para el log.
        private static void DumpNamedDescendants(DependencyObject parent, int depth)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                    Debug.WriteLine($"[TREE] {new string(' ', depth * 2)}{child.GetType().Name} -> '{fe.Name}'");

                DumpNamedDescendants(child, depth + 1);
            }
        }

        // ═══════════════════════════════════════════
        // Navegación
        // ═══════════════════════════════════════════

        private void AppNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (ContentFrame == null) return;
            if (args.SelectedItem is not NavigationViewItem item) return;

            var tag = item.Tag as string;
            if (string.IsNullOrWhiteSpace(tag)) return;

            Type pageType = tag switch
            {
                "Home" => typeof(HomeView),
                "Commands" => typeof(CommandsView),
                "AllowedApps" => typeof(AllowedAppsView),
                "Search" => typeof(SearchTabsView),
                "Settings" => typeof(SettingsView),
                "Tests" => typeof(TestRunner),
                "GoogleCalendar" => typeof(GoogleCalendarView),
                "Todoist" => typeof(TodoistView),
                _ => typeof(HomeView)
            };

            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);
        }

        /// Navega a Configuración al hacer click en el indicador Dropbox.
        private void DropboxIndicator_Click(object sender, RoutedEventArgs e)
        {
            var settingsItem = FindNavItem("Settings");
            if (settingsItem != null)
                AppNav.SelectedItem = settingsItem;

            if (ContentFrame?.CurrentSourcePageType != typeof(SettingsView))
                ContentFrame?.Navigate(typeof(SettingsView));
        }

        /// Navega a Google Calendar al hacer click en el indicador.
        private void GoogleCalendarIndicator_Click(object sender, RoutedEventArgs e)
        {
            var calItem = FindNavItem("GoogleCalendar");
            if (calItem != null)
                AppNav.SelectedItem = calItem;

            if (ContentFrame?.CurrentSourcePageType != typeof(GoogleCalendarView))
                ContentFrame?.Navigate(typeof(GoogleCalendarView));
        }

        /// Busca un NavigationViewItem por Tag en MenuItems y FooterMenuItems.
        private NavigationViewItem? FindNavItem(string tag)
        {
            return AllNavItems().FirstOrDefault(i => i.Tag?.ToString() == tag);
        }

        /// Devuelve todos los NavigationViewItem de MenuItems + FooterMenuItems.
        private IEnumerable<NavigationViewItem> AllNavItems()
        {
            return AppNav.MenuItems
                .OfType<NavigationViewItem>()
                .Concat(AppNav.FooterMenuItems.OfType<NavigationViewItem>());
        }

        // ═══════════════════════════════════════════
        // Google Calendar indicator
        // ═══════════════════════════════════════════

        private async void CheckGoogleCalendarStatusOnStartup()
        {
            try
            {
                var googleAuth = App.AppHost.Services
                    .GetRequiredService<Anfeta.UI.Services.Calendar.GoogleAuthService>();

                var connected = await googleAuth.IsConnectedAsync();
                DispatcherQueue.TryEnqueue(() => UpdateGoogleCalendarIndicator(connected));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GCAL_INDICATOR] Error al verificar estado startup: {ex.Message}");
            }
        }

        /// Actualiza el indicador de Google Calendar.
        /// Entrada: connected (bool) — estado de conexión actual.
        public void UpdateGoogleCalendarIndicator(bool connected)
        {
            if (DotGoogleCal == null || IconGoogleCal == null || TxtGoogleCal == null) return;

            if (connected)
            {
                var green = new SolidColorBrush(Color.FromArgb(255, 52, 211, 153));
                DotGoogleCal.Fill = green;
                IconGoogleCal.Foreground = green;
                TxtGoogleCal.Foreground = green;
                ToolTipService.SetToolTip(GoogleCalendarIndicator, "Google Calendar conectado");
            }
            else
            {
                var neutral = new SolidColorBrush(Color.FromArgb(255, 74, 62, 54));
                var neutralText = new SolidColorBrush(Color.FromArgb(255, 107, 95, 86));
                DotGoogleCal.Fill = neutral;
                IconGoogleCal.Foreground = neutral;
                TxtGoogleCal.Foreground = neutralText;
                ToolTipService.SetToolTip(GoogleCalendarIndicator, "Google Calendar no conectado — click para conectar");
            }
        }

        // ═══════════════════════════════════════════
        // Dropbox indicator
        // ═══════════════════════════════════════════

        private void SubscribeDropboxState()
        {
            DropboxIndexCoordinator.StateChanged += OnDropboxStateChanged;
            DispatcherQueue.TryEnqueue(UpdateDropboxIndicator);
        }

        private void OnDropboxStateChanged()
        {
            DispatcherQueue.TryEnqueue(UpdateDropboxIndicator);
        }

        private void UpdateDropboxIndicator()
        {
            if (DotDropbox == null || IconDropbox == null || TxtDropbox == null) return;

            if (DropboxIndexCoordinator.RootPath == null &&
                !DropboxIndexCoordinator.IsIndexing &&
                !DropboxIndexCoordinator.IsReady &&
                DropboxIndexCoordinator.LastError == null)
            {
                SetDropboxColors(239, 68, 68, 100, 116, 139);
                TxtDropbox.Text = "Dropbox";
                ToolTipService.SetToolTip(DropboxIndicator, "Dropbox: sin carpeta configurada — click para configurar");
            }
            else if (DropboxIndexCoordinator.IsIndexing)
            {
                SetDropboxColors(251, 191, 36, 251, 191, 36);
                TxtDropbox.Text = "Indexando...";
                ToolTipService.SetToolTip(DropboxIndicator, "Dropbox: indexando archivos...");
            }
            else if (DropboxIndexCoordinator.IsReady)
            {
                SetDropboxColors(52, 211, 153, 226, 232, 240);
                TxtDropbox.Text = "Dropbox";
                ToolTipService.SetToolTip(DropboxIndicator, $"Dropbox listo — {DropboxIndexCoordinator.RootPath}");
            }
            else if (DropboxIndexCoordinator.LastError != null)
            {
                SetDropboxColors(239, 68, 68, 239, 68, 68);
                TxtDropbox.Text = "Error";
                ToolTipService.SetToolTip(DropboxIndicator, $"Error al indexar: {DropboxIndexCoordinator.LastError}");
            }
        }

        // Aplica colores al indicador Dropbox.
        // Entrada: componentes RGB para el punto y para el texto/ícono.
        private void SetDropboxColors(byte dotR, byte dotG, byte dotB, byte textR, byte textG, byte textB)
        {
            DotDropbox.Fill = new SolidColorBrush(Color.FromArgb(255, dotR, dotG, dotB));
            IconDropbox.Foreground = new SolidColorBrush(Color.FromArgb(255, textR, textG, textB));
            TxtDropbox.Foreground = new SolidColorBrush(Color.FromArgb(255, textR, textG, textB));
        }

        // ═══════════════════════════════════════════
        // Ciclo de vida
        // ═══════════════════════════════════════════

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            DropboxIndexCoordinator.StateChanged -= OnDropboxStateChanged;
            Debug.WriteLine("MAINWINDOW: Closed");
            ((App)Application.Current).CleanupAndExit();
        }
    }
}