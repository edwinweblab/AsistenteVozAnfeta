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
using Windows.UI;
using Anfeta.UI.Services;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using WinRT.Interop;
namespace Anfeta.UI
{
    public sealed partial class MainWindow : Window
    {
        private readonly ShellViewModel _shell;
        private GlobalHotkeyService? _hotkeyService;

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

            // Mostrar/ocultar footer de versión + overlay del panel sin desplazar contenido.
            // Cuando el panel se expande, se aplica un desplazamiento negativo al Frame
            // igual a la diferencia entre OpenPaneLength y CompactPaneLength (220-52=168),
            // haciendo que el panel se superponga al contenido en lugar de empujarlo.
            AppNav.PaneOpened += (s, e) =>
            {
                VersionFooter.Visibility = Visibility.Visible;
                ContentFrame.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform
                {
                    X = -(AppNav.OpenPaneLength - AppNav.CompactPaneLength)
                };
            };
            AppNav.PaneClosed += (s, e) =>
            {
                VersionFooter.Visibility = Visibility.Collapsed;
                ContentFrame.RenderTransform = null;
            };

            SubscribeDropboxState();
            CheckGoogleCalendarStatusOnStartup();
            _hotkeyService = App.AppHost.Services.GetRequiredService<GlobalHotkeyService>();
            _hotkeyService.SearchHotkeyPressed += OnSearchHotkeyPressed;
            this.Closed += MainWindow_Closed;

            Debug.WriteLine("MAINWINDOW: constructor OK");
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
        /// Entrada: tag (string) — valor del Tag a buscar.
        /// Salida: NavigationViewItem encontrado, o null.
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

        /// Verifica el estado de Google Calendar al iniciar y actualiza el indicador.
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

        /// Actualiza el indicador de Google Calendar en la barra superior.
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

        /// Suscribe al evento de cambio de estado del coordinador Dropbox.
        private void SubscribeDropboxState()
        {
            DropboxIndexCoordinator.StateChanged += OnDropboxStateChanged;
            DispatcherQueue.TryEnqueue(UpdateDropboxIndicator);
        }

        /// Se dispara desde cualquier hilo cuando el coordinador cambia de estado.
        private void OnDropboxStateChanged()
        {
            DispatcherQueue.TryEnqueue(UpdateDropboxIndicator);
        }

        /// Actualiza el botón Dropbox según el estado actual.
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

        /// Helper: aplica colores al punto, ícono y texto del indicador Dropbox.
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
            if (_hotkeyService != null)
                _hotkeyService.SearchHotkeyPressed -= OnSearchHotkeyPressed;
            ((App)Application.Current).CleanupAndExit();
        }
        public void ToggleSearchFromHotkey()
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var hwnd = WindowNative.GetWindowHandle(this);

                if (hwnd == IntPtr.Zero)
                    return;

                // Si ANFETA está abierta y al frente, el mismo comando la minimiza.
                if (IsAppForeground(hwnd) && !IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_MINIMIZE);
                    return;
                }

                // Si está minimizada o detrás de otra app, la restaura y la manda al buscador.
                ShowWindow(hwnd, SW_RESTORE);
                Activate();
                SetForegroundWindow(hwnd);

                var searchItem = FindNavItem("Search");

                if (searchItem != null)
                    AppNav.SelectedItem = searchItem;

                if (ContentFrame.CurrentSourcePageType != typeof(SearchTabsView))
                    ContentFrame.Navigate(typeof(SearchTabsView));

                await Task.Delay(250);

                SearchFocusBridge.RequestFocus();
            });
        }
        private void OnSearchHotkeyPressed(object? sender, EventArgs e)
        {
            ToggleSearchFromHotkey();
        }
        private static bool IsAppForeground(IntPtr hwnd)
        {
            return GetForegroundWindow() == hwnd;
        }

        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}