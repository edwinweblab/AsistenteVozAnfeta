using Anfeta.UI.FunctionTests;
using Anfeta.UI.Services.Search;
using Anfeta.UI.ViewModels;
using Anfeta.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.UI;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Notion;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Reflection;
using Windows.ApplicationModel;
using Windows.Storage;
using WinRT.Interop;
namespace Anfeta.UI
{
    public sealed partial class MainWindow : Window
    {
        private readonly ShellViewModel _shell;
        private GlobalHotkeyService? _hotkeyService;
        private readonly IndexedFileReminderService _indexedReminderService;
        private readonly SemaphoreSlim _reminderDialogLock = new(1, 1);
        private readonly NotionCalendarService _calendarStartupService = new();
        private const string LS_CalendarLastSyncUtc =
            "Search.Calendar.LastSyncUtc";

        public MainWindow()
        {
            InitializeComponent();
            ApplyVisibleAppVersion();

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
            _ = StartCalendarWarmupOnStartupAsync();
            _hotkeyService = App.AppHost.Services.GetRequiredService<GlobalHotkeyService>();
            _hotkeyService.SearchHotkeyPressed += OnSearchHotkeyPressed;

            _indexedReminderService =
                App.AppHost.Services.GetRequiredService<
                    IndexedFileReminderService>();

            _indexedReminderService.ReminderDue +=
                IndexedReminderService_ReminderDue;

            _indexedReminderService.Start(
                DispatcherQueue);

            this.Closed += MainWindow_Closed;

            Debug.WriteLine("MAINWINDOW: constructor OK");
        }


        private async Task StartCalendarWarmupOnStartupAsync()
        {
            var values =
                ApplicationData.Current.LocalSettings.Values;

            var token =
                values["Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
                return;

            DateTimeOffset? anchorUtc = null;

            var calendarAnchor =
                values[LS_CalendarLastSyncUtc] as string;

            var notionAnchor =
                values["Notion.LastSyncUtc"] as string;

            if (DateTimeOffset.TryParse(
                    calendarAnchor,
                    out var parsedCalendar))
            {
                anchorUtc =
                    parsedCalendar.ToUniversalTime();
            }
            else if (DateTimeOffset.TryParse(
                         notionAnchor,
                         out var parsedNotion))
            {
                anchorUtc =
                    parsedNotion.ToUniversalTime();
            }

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(12));

                await _calendarStartupService
                    .StartStartupWarmupAsync(
                        token,
                        anchorUtc,
                        cts.Token);

                values[LS_CalendarLastSyncUtc] =
                    DateTimeOffset.UtcNow.ToString("O");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[CALENDAR_WARMUP] {ex.Message}");
            }
        }

        private void ApplyVisibleAppVersion()
        {
            var versionText = GetCurrentAppVersion();

            Title = $"ANFETA - Asistente de Voz Empresarial · v{versionText}";

            if (VersionTextBlock != null)
                VersionTextBlock.Text = $"ANFETA v{versionText}";

            if (BrandVersionTextBlock != null)
                BrandVersionTextBlock.Text = $"Asistente de Voz · v{versionText}";
        }

        private static string GetCurrentAppVersion()
        {
            try
            {
                var version = Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return Assembly
                    .GetExecutingAssembly()
                    .GetName()
                    .Version?
                    .ToString() ?? "0.0.0.0";
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

        private async void NewSearchTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;

            var searchItem = FindNavItem("Search");
            if (searchItem != null)
                AppNav.SelectedItem = searchItem;

            if (ContentFrame.CurrentSourcePageType != typeof(SearchTabsView))
            {
                ContentFrame.Navigate(typeof(SearchTabsView));
                await Task.Delay(100);
            }

            if (ContentFrame.Content is SearchTabsView tabsView)
            {
                var view = tabsView.AddNewSearchTab();
                await Task.Delay(50);
                SearchFocusBridge.RequestFocus();
            }
        }

        private void IndexedReminderService_ReminderDue(
            object? sender,
            IndexedFileReminder reminder)
        {
            DispatcherQueue.TryEnqueue(
                async () =>
                {
                    SignalIncomingReminder();

                    await ShowIndexedReminderAsync(
                        reminder);
                });
        }


        private void SignalIncomingReminder()
        {
            try
            {
                MessageBeep(0x00000040); // MB_ICONASTERISK

                var hwnd = WindowNative.GetWindowHandle(this);
                if (hwnd == IntPtr.Zero || IsAppForeground(hwnd))
                    return;

                var flash = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = hwnd,
                    dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                    uCount = 5,
                    dwTimeout = 0
                };

                FlashWindowEx(ref flash);
            }
            catch
            {
                // El aviso visual/sonoro no debe bloquear el recordatorio.
            }
        }

        private async Task ShowIndexedReminderAsync(
            IndexedFileReminder reminder)
        {
            await _reminderDialogLock.WaitAsync();

            try
            {
                var sourceLabel =
                    reminder.Source == Models.Weblab.SearchSource.Notion
                        ? "Notion"
                        : reminder.Source == Models.Weblab.SearchSource.Dropbox
                            ? "Dropbox"
                            : "Archivo local";

                var content = new StackPanel
                {
                    Spacing = 8
                };

                content.Children.Add(
                    new TextBlock
                    {
                        Text = reminder.Message,
                        FontSize = 16,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping =
                            TextWrapping.Wrap
                    });

                content.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"Programado: " +
                            $"{reminder.ReminderAt:dd/MM/yyyy HH:mm}",
                        Opacity = 0.72
                    });

                content.Children.Add(
                    new TextBlock
                    {
                        Text = $"Origen: {sourceLabel}",
                        Opacity = 0.72
                    });

                if (!string.IsNullOrWhiteSpace(
                        reminder.RecipientName))
                {
                    content.Children.Add(
                        new TextBlock
                        {
                            Text =
                                $"Para: {reminder.RecipientName} ({reminder.RecipientTag})",
                            Opacity = 0.72
                        });
                }

                if (!string.IsNullOrWhiteSpace(
                        reminder.SenderName))
                {
                    content.Children.Add(
                        new TextBlock
                        {
                            Text =
                                $"De: {reminder.SenderName} ({reminder.SenderTag})",
                            Opacity = 0.72
                        });
                }

                if (!string.IsNullOrWhiteSpace(
                        reminder.Target))
                {
                    content.Children.Add(
                        new TextBlock
                        {
                            Text = reminder.Target,
                            TextWrapping =
                                TextWrapping.Wrap,
                            Opacity = 0.60
                        });
                }

                ContentDialog? reminderDialog = null;

                var actionStatus =
                    new TextBlock
                    {
                        Text = string.Empty,
                        Opacity = 0.72,
                        TextWrapping = TextWrapping.Wrap
                    };

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };

                if (reminder.Source == Models.Weblab.SearchSource.Notion &&
                    Uri.TryCreate(
                        reminder.Target,
                        UriKind.Absolute,
                        out var notionUri))
                {
                    var openButton = new Button
                    {
                        Content = "Abrir en Notion",
                        HorizontalAlignment = HorizontalAlignment.Left
                    };

                    openButton.Click += async (_, __) =>
                    {
                        try
                        {
                            var desktopUri = new Uri(
                                notionUri.AbsoluteUri.Replace(
                                    "https://",
                                    "notion://",
                                    StringComparison.OrdinalIgnoreCase));

                            var support =
                                await Windows.System.Launcher.QueryUriSupportAsync(
                                    desktopUri,
                                    Windows.System.LaunchQuerySupportType.Uri);

                            var opened =
                                support == Windows.System.LaunchQuerySupportStatus.Available &&
                                await Windows.System.Launcher.LaunchUriAsync(desktopUri);

                            if (!opened)
                                opened = await Windows.System.Launcher.LaunchUriAsync(notionUri);

                            actionStatus.Text = opened
                                ? "Mensaje abierto ✅"
                                : "No se pudo abrir el mensaje.";
                        }
                        catch (Exception ex)
                        {
                            actionStatus.Text =
                                $"No se pudo abrir → {ex.Message}";
                        }
                    };

                    actions.Children.Add(openButton);
                }

                if (reminder.Source ==
                        Models.Weblab.SearchSource.Notion &&
                    !string.IsNullOrWhiteSpace(
                        reminder.PageId))
                {
                    var conversationButton =
                        new Button
                        {
                            Content = "Abrir conversación",
                            HorizontalAlignment =
                                HorizontalAlignment.Left
                        };

                    conversationButton.Click +=
                        async (_, __) =>
                        {
                            actionStatus.Text =
                                "Abriendo conversación...";

                            reminderDialog?.Hide();

                            await Task.Delay(150);

                            await OpenReminderConversationAsync(
                                reminder);
                        };

                    actions.Children.Insert(
                        0,
                        conversationButton);
                }

                var copyStatus =
                    new TextBlock
                    {
                        Text = string.Empty,
                        Opacity = 0.72
                    };

                var copyButton =
                    new Button
                    {
                        Content = "Copiar texto",
                        HorizontalAlignment =
                            HorizontalAlignment.Left
                    };

                copyButton.Click += (_, __) =>
                {
                    var package =
                        new Windows.ApplicationModel.DataTransfer
                            .DataPackage();

                    package.SetText(
                        reminder.Message);

                    Windows.ApplicationModel.DataTransfer
                        .Clipboard.SetContent(package);

                    copyStatus.Text =
                        "Texto copiado ✅";
                };

                actions.Children.Add(copyButton);
                content.Children.Add(actions);

                var snoozePanel = new Grid
                {
                    ColumnSpacing = 8
                };

                snoozePanel.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star)
                    });
                snoozePanel.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });

                var snoozeCombo = new ComboBox
                {
                    Header = "Posponer recordatorio",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    SelectedIndex = 0
                };

                snoozeCombo.Items.Add(
                    new ComboBoxItem { Content = "5 minutos", Tag = "5" });
                snoozeCombo.Items.Add(
                    new ComboBoxItem { Content = "10 minutos", Tag = "10" });
                snoozeCombo.Items.Add(
                    new ComboBoxItem { Content = "30 minutos", Tag = "30" });
                snoozeCombo.Items.Add(
                    new ComboBoxItem { Content = "1 hora", Tag = "60" });

                var snoozeButton = new Button
                {
                    Content = "Posponer",
                    VerticalAlignment = VerticalAlignment.Bottom
                };

                Grid.SetColumn(snoozeCombo, 0);
                snoozePanel.Children.Add(snoozeCombo);

                Grid.SetColumn(snoozeButton, 1);
                snoozePanel.Children.Add(snoozeButton);

                content.Children.Add(snoozePanel);
                content.Children.Add(actionStatus);
                content.Children.Add(copyStatus);

                reminderDialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = "Recordatorio ANFETA",
                    Content = content,
                    PrimaryButtonText = "Entendido",
                    DefaultButton =
                        ContentDialogButton.Primary
                };

                var dialog = reminderDialog;

                snoozeButton.Click += (_, __) =>
                {
                    if (snoozeCombo.SelectedItem is not ComboBoxItem selected ||
                        !double.TryParse(
                            selected.Tag?.ToString(),
                            out var minutes))
                    {
                        actionStatus.Text =
                            "Selecciona un tiempo para posponer.";
                        return;
                    }

                    _indexedReminderService.Snooze(
                        reminder,
                        TimeSpan.FromMinutes(minutes));

                    actionStatus.Text =
                        $"Recordatorio pospuesto {selected.Content} ✅";

                    dialog.Hide();
                };

                await dialog.ShowAsync();
            }
            catch
            {
                // Un diálogo no debe bloquear futuros avisos.
            }
            finally
            {
                _reminderDialogLock.Release();
            }
        }

        private async Task OpenReminderConversationAsync(
            IndexedFileReminder reminder)
        {
            if (string.IsNullOrWhiteSpace(
                    reminder.PageId))
            {
                return;
            }

            var hwnd =
                WindowNative.GetWindowHandle(this);

            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                Activate();
                SetForegroundWindow(hwnd);
            }

            var searchItem =
                FindNavItem("Search");

            if (searchItem != null)
                AppNav.SelectedItem = searchItem;

            if (ContentFrame.CurrentSourcePageType !=
                typeof(SearchTabsView))
            {
                ContentFrame.Navigate(
                    typeof(SearchTabsView));

                await Task.Delay(300);
            }

            SearchView.RequestOpenConversation(
                reminder.PageId);
        }

        // ═══════════════════════════════════════════
        // Ciclo de vida
        // ═══════════════════════════════════════════

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            DropboxIndexCoordinator.StateChanged -= OnDropboxStateChanged;

            _indexedReminderService.ReminderDue -=
                IndexedReminderService_ReminderDue;

            _indexedReminderService.Stop();

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

        private const uint FLASHW_TRAY = 0x00000002;
        private const uint FLASHW_TIMERNOFG = 0x0000000C;

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}