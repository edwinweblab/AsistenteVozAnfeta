using Anfeta.UI.FunctionTests;
using Anfeta.UI.Services.Search;
using Anfeta.UI.ViewModels;
using Anfeta.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.Linq;
using Windows.UI;

namespace Anfeta.UI
{
    public sealed partial class MainWindow : Window
    {
        private readonly ShellViewModel _shell;

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

            SubscribeDropboxState();
            CheckGoogleCalendarStatusOnStartup();

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
                _ => typeof(HomeView)
            };

            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);
        }

        /// Navega a Configuración al hacer click en el indicador Dropbox.
        private void DropboxIndicator_Click(object sender, RoutedEventArgs e)
        {
            var settingsItem = AppNav.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == "Settings");

            if (settingsItem != null)
                AppNav.SelectedItem = settingsItem;

            if (ContentFrame?.CurrentSourcePageType != typeof(SettingsView))
                ContentFrame?.Navigate(typeof(SettingsView));
        }

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


        /// Navega a Google Calendar al hacer click en el indicador.
        private void GoogleCalendarIndicator_Click(object sender, RoutedEventArgs e)
        {
            var calItem = AppNav.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == "GoogleCalendar");

            if (calItem != null)
                AppNav.SelectedItem = calItem;

            if (ContentFrame?.CurrentSourcePageType != typeof(GoogleCalendarView))
                ContentFrame?.Navigate(typeof(GoogleCalendarView));
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


        /// Actualiza el botón Dropbox según el estado actual:
        /// sin configurar → rojo | indexando → amarillo | listo → verde | error → rojo
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
        /// dotR/G/B → color del punto | textR/G/B → color del ícono y texto
        private void SetDropboxColors(byte dotR, byte dotG, byte dotB, byte textR, byte textG, byte textB)
        {
            var dotColor = new SolidColorBrush(Color.FromArgb(255, dotR, dotG, dotB));
            var textColor = new SolidColorBrush(Color.FromArgb(255, textR, textG, textB));

            DotDropbox.Fill = dotColor;
            IconDropbox.Foreground = textColor;
            TxtDropbox.Foreground = textColor;
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