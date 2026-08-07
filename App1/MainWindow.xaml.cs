using Anfeta.UI.FunctionTests;
using Anfeta.UI.Services.Search;
using Anfeta.UI.ViewModels;
using Anfeta.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

        private sealed class ReminderToastEntry
        {
            public Popup Popup { get; init; } = null!;
            public IndexedFileReminder Reminder { get; init; } = null!;
        }

        private readonly List<ReminderToastEntry>
            _activeReminderToasts = new();

        private const int MaxVisibleReminderToasts = 6;
        private const double ReminderToastWidth = 360d;
        private const double ReminderToastGap = 10d;
        private const double ReminderToastMargin = 18d;

        private readonly NotionCalendarService _calendarStartupService = new();
        private const string LS_CalendarLastSyncUtc =
            "Search.Calendar.LastSyncUtc";

        private const string LS_MessagingCurrentUserTag =
            "Messaging.CurrentUserTag";

        private const string MessagesAllRecipientsTag =
            "__all__";

        private const string AppInstallerUrl =
            "https://github.com/neftaliweblab/anfeta-updates/releases/latest/download/ANFETA.appinstaller";

        private bool _isCheckingForUpdates;

        public MainWindow()
        {
            InitializeComponent();
            ApplyVisibleAppVersion();

            _shell = App.AppHost.Services.GetRequiredService<ShellViewModel>();
            Root.DataContext = _shell;

            Root.SizeChanged +=
                (_, __) =>
                    RepositionReminderToasts();

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

            // ANFETA inicia directamente en el Buscador. El antiguo panel
            // principal de voz permanece en el proyecto, pero ya no se crea
            // ni se selecciona durante el arranque normal.
            if (ContentFrame != null)
                ContentFrame.Navigate(typeof(SearchTabsView));

            var initialSearchItem = FindNavItem("Search");
            if (initialSearchItem != null)
                AppNav.SelectedItem = initialSearchItem;

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

        private async void CheckUpdatesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_isCheckingForUpdates)
                return;

            _isCheckingForUpdates = true;
            CheckUpdatesButton.IsEnabled = false;

            SetUpdateStatus(
                "Buscando actualización…",
                Color.FromArgb(255, 251, 191, 36));

            try
            {
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "ANFETA-UpdateChecker/1.0");

                var xml = await http.GetStringAsync(
                    AppInstallerUrl);

                var document = XDocument.Parse(xml);
                XNamespace ns =
                    "http://schemas.microsoft.com/appx/appinstaller/2017/2";

                var remoteRaw =
                    document.Root?.Attribute("Version")?.Value ??
                    document.Root?
                        .Element(ns + "MainPackage")?
                        .Attribute("Version")?
                        .Value ??
                    string.Empty;

                if (!Version.TryParse(remoteRaw, out var remoteVersion))
                {
                    throw new InvalidOperationException(
                        "El archivo de actualización no contiene una versión válida.");
                }

                var localRaw = GetCurrentAppVersion();

                if (!Version.TryParse(localRaw, out var localVersion))
                    localVersion = new Version(0, 0, 0, 0);

                if (remoteVersion <= localVersion)
                {
                    SetUpdateStatus(
                        $"ANFETA está actualizado · v{localRaw}",
                        Color.FromArgb(255, 52, 211, 153));

                    await ShowUpdateDialogAsync(
                        "ANFETA está actualizado",
                        $"Tienes instalada la versión {localRaw}. No hay una versión más reciente disponible.",
                        showInstallButton: false);

                    return;
                }

                SetUpdateStatus(
                    $"Actualización disponible · v{remoteVersion}",
                    Color.FromArgb(255, 96, 165, 250));

                var install = await ShowUpdateDialogAsync(
                    "Actualización disponible",
                    $"Versión instalada: {localRaw}\n" +
                    $"Nueva versión: {remoteVersion}\n\n" +
                    "ANFETA abrirá App Installer para completar la actualización.",
                    showInstallButton: true);

                if (install)
                {
                    SetUpdateStatus(
                        "Descargando instalador de actualización…",
                        Color.FromArgb(255, 251, 191, 36));

                    var installerBytes =
                        await http.GetByteArrayAsync(
                            AppInstallerUrl);

                    var installerFile =
                        await ApplicationData.Current.TemporaryFolder
                            .CreateFileAsync(
                                "ANFETA.appinstaller",
                                CreationCollisionOption.ReplaceExisting);

                    await FileIO.WriteBytesAsync(
                        installerFile,
                        installerBytes);

                    var opened =
                        await Windows.System.Launcher
                            .LaunchFileAsync(installerFile);

                    if (!opened)
                    {
                        opened =
                            await Windows.System.Launcher
                                .LaunchUriAsync(
                                    new Uri(AppInstallerUrl));
                    }

                    SetUpdateStatus(
                        opened
                            ? "Instalador de actualización abierto"
                            : "No se pudo abrir App Installer",
                        opened
                            ? Color.FromArgb(255, 96, 165, 250)
                            : Color.FromArgb(255, 248, 113, 113));
                }
            }
            catch (Exception ex)
            {
                SetUpdateStatus(
                    "Error al buscar actualizaciones",
                    Color.FromArgb(255, 248, 113, 113));

                await ShowUpdateDialogAsync(
                    "No se pudo comprobar la actualización",
                    "Revisa tu conexión a Internet y vuelve a intentarlo.\n\n" +
                    ex.Message,
                    showInstallButton: false);
            }
            finally
            {
                _isCheckingForUpdates = false;
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private void SetUpdateStatus(
            string text,
            Color color)
        {
            if (UpdateStatusText != null)
                UpdateStatusText.Text = text;

            if (UpdateStatusDot != null)
                UpdateStatusDot.Fill = new SolidColorBrush(color);
        }

        private async Task<bool> ShowUpdateDialogAsync(
            string title,
            string message,
            bool showInstallButton)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460
                },
                CloseButtonText = showInstallButton
                    ? "Después"
                    : "Cerrar",
                DefaultButton = showInstallButton
                    ? ContentDialogButton.Primary
                    : ContentDialogButton.Close
            };

            if (showInstallButton)
                dialog.PrimaryButtonText = "Actualizar ahora";

            var result = await dialog.ShowAsync();

            return showInstallButton &&
                   result == ContentDialogResult.Primary;
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
                // Home queda redirigido al Buscador por compatibilidad con
                // accesos antiguos, aunque el elemento ya está oculto.
                "Home" => typeof(SearchTabsView),
                "Commands" => typeof(CommandsView),
                "AllowedApps" => typeof(AllowedAppsView),
                "Search" => typeof(SearchTabsView),
                "Settings" => typeof(SettingsView),
                "Tests" => typeof(TestRunner),
                "GoogleCalendar" => typeof(GoogleCalendarView),
                "Todoist" => typeof(TodoistView),
                _ => typeof(SearchTabsView)
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
            if (!ReminderBelongsToConfiguredUser(reminder))
                return;

            DispatcherQueue.TryEnqueue(
                () =>
                {
                    SignalIncomingReminder();

                    // El aviso inmediato ya no bloquea la aplicación con un
                    // ContentDialog. Se muestra como tarjeta flotante y el
                    // usuario decide cuándo abrir el detalle completo.
                    ShowReminderToast(reminder);
                });
        }


        private static string NormalizeReminderPersonTag(
            string? value)
        {
            var clean =
                (value ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

            return clean switch
            {
                "iisai" or "iisiaia" or "isaias" => "iisaia",
                _ => clean
            };
        }

        private static bool ReminderBelongsToConfiguredUser(
            IndexedFileReminder reminder)
        {
            if (reminder == null)
                return false;

            var recipientTag =
                NormalizeReminderPersonTag(
                    reminder.RecipientTag);

            if (string.IsNullOrWhiteSpace(recipientTag) &&
                reminder.Source ==
                    Models.Weblab.SearchSource.Notion)
            {
                var legacyRecipient =
                    Regex.Match(
                        reminder.Title ?? string.Empty,
                        @"(?<!\d)\d{4}-\d{2}-\d{2}[ T]\d{2}[:\-]\d{2}\s+(?<tag>[a-z0-9_\-]+)",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);

                if (legacyRecipient.Success)
                {
                    recipientTag =
                        NormalizeReminderPersonTag(
                            legacyRecipient.Groups["tag"].Value);
                }
            }

            if (string.Equals(
                    recipientTag,
                    MessagesAllRecipientsTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var currentUserTag =
                NormalizeReminderPersonTag(
                    ApplicationData.Current.LocalSettings.Values[
                        LS_MessagingCurrentUserTag] as string);

            // Los recordatorios locales sin destinatario se conservan. Las
            // páginas de Mensajes/Revisiones de Notion deben tener receptor y
            // nunca se muestran a otro usuario por un título incompleto.
            if (string.IsNullOrWhiteSpace(recipientTag))
            {
                return reminder.Source !=
                    Models.Weblab.SearchSource.Notion;
            }

            if (string.IsNullOrWhiteSpace(currentUserTag))
                return false;

            return string.Equals(
                recipientTag,
                currentUserTag,
                StringComparison.OrdinalIgnoreCase);
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


        private void ShowReminderToast(
            IndexedFileReminder reminder)
        {
            if (reminder == null ||
                !ReminderBelongsToConfiguredUser(reminder))
            {
                return;
            }

            if (Root?.XamlRoot == null ||
                Root.ActualWidth <= 0 ||
                Root.ActualHeight <= 0)
            {
                DispatcherQueue.TryEnqueue(
                    async () =>
                    {
                        await Task.Delay(250);
                        ShowReminderToast(reminder);
                    });
                return;
            }

            // Evita duplicar visualmente el mismo recordatorio si el escaneo
            // y un pospuesto coinciden durante el mismo ciclo.
            if (_activeReminderToasts.Any(item =>
                    string.Equals(
                        item.Reminder.Identity,
                        reminder.Identity,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var title =
                new TextBlock
                {
                    Text = "🔔 Nuevo recordatorio",
                    FontSize = 13.5,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground =
                        new SolidColorBrush(
                            Color.FromArgb(
                                255,
                                233,
                                213,
                                255))
                };

            var message =
                new TextBlock
                {
                    Text = reminder.Message,
                    FontSize = 12.5,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 3,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                };

            var schedule =
                new TextBlock
                {
                    Text =
                        $"{reminder.ReminderAt:dd/MM/yyyy · h:mm tt}" +
                        (string.IsNullOrWhiteSpace(
                             reminder.SenderName)
                            ? string.Empty
                            : $" · De {reminder.SenderName}"),
                    FontSize = 10.5,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                };

            var actions =
                new Grid
                {
                    ColumnSpacing = 8
                };

            actions.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            actions.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            actions.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            var viewButton =
                new Button
                {
                    Content = "Ver",
                    MinHeight = 32,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    HorizontalContentAlignment =
                        HorizontalAlignment.Center
                };

            var remindAgainButton =
                new Button
                {
                    Content = "↻ 15 min",
                    MinHeight = 32,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    HorizontalContentAlignment =
                        HorizontalAlignment.Center
                };

            ToolTipService.SetToolTip(
                remindAgainButton,
                "Cerrar este aviso y volver a mostrarlo en 15 minutos.");

            var understoodButton =
                new Button
                {
                    Content = "✓ Entendido",
                    MinHeight = 32,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    HorizontalContentAlignment =
                        HorizontalAlignment.Center,
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(
                                100,
                                168,
                                85,
                                247)),
                    BorderBrush =
                        new SolidColorBrush(
                            Color.FromArgb(
                                220,
                                192,
                                132,
                                252))
                };

            Grid.SetColumn(viewButton, 0);
            actions.Children.Add(viewButton);

            Grid.SetColumn(remindAgainButton, 1);
            actions.Children.Add(remindAgainButton);

            Grid.SetColumn(understoodButton, 2);
            actions.Children.Add(understoodButton);

            var body =
                new StackPanel
                {
                    Spacing = 8
                };

            body.Children.Add(title);
            body.Children.Add(message);
            body.Children.Add(schedule);
            body.Children.Add(actions);

            var card =
                new Border
                {
                    Width = ReminderToastWidth,
                    Padding =
                        new Thickness(13, 11, 13, 11),
                    CornerRadius =
                        new CornerRadius(10),
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(
                                250,
                                28,
                                24,
                                36)),
                    BorderBrush =
                        new SolidColorBrush(
                            Color.FromArgb(
                                255,
                                217,
                                70,
                                239)),
                    BorderThickness =
                        new Thickness(2, 1, 1, 1),
                    Child = body
                };

            var popup =
                new Popup
                {
                    XamlRoot = Root.XamlRoot,
                    IsLightDismissEnabled = false,
                    Child = card
                };

            var entry =
                new ReminderToastEntry
                {
                    Popup = popup,
                    Reminder = reminder
                };

            viewButton.Click +=
                async (_, __) =>
                {
                    DismissReminderToast(
                        entry,
                        acknowledged: false);

                    await ShowIndexedReminderAsync(
                        reminder);
                };

            remindAgainButton.Click +=
                (_, __) =>
                {
                    // No marca el recordatorio como entendido. Solo cierra
                    // esta tarjeta y la agenda de nuevo para dentro de 15 min.
                    _indexedReminderService.Snooze(
                        reminder,
                        TimeSpan.FromMinutes(15));

                    DismissReminderToast(
                        entry,
                        acknowledged: false);
                };

            understoodButton.Click +=
                (_, __) =>
                {
                    DismissReminderToast(
                        entry,
                        acknowledged: true);
                };

            card.SizeChanged +=
                (_, __) =>
                    RepositionReminderToasts();

            _activeReminderToasts.Add(entry);

            while (_activeReminderToasts.Count >
                   MaxVisibleReminderToasts)
            {
                var oldest =
                    _activeReminderToasts[0];

                oldest.Popup.IsOpen = false;
                _activeReminderToasts.RemoveAt(0);
            }

            popup.IsOpen = true;
            RepositionReminderToasts();
        }

        private void DismissReminderToast(
            ReminderToastEntry entry,
            bool acknowledged)
        {
            if (entry == null)
                return;

            if (acknowledged)
            {
                _indexedReminderService.Acknowledge(
                    entry.Reminder);

                if (!string.IsNullOrWhiteSpace(
                        entry.Reminder.PageId))
                {
                    // Si el Buscador ya está cargado, actualiza de inmediato
                    // sus badges y calendarios. Si no lo está, la solicitud
                    // queda pendiente para cuando se abra.
                    SearchView.RequestReminderQuickAction(
                        entry.Reminder.PageId,
                        "mark-read");
                }
            }

            entry.Popup.IsOpen = false;
            entry.Popup.Child = null;

            _activeReminderToasts.Remove(entry);

            RepositionReminderToasts();
        }

        private void RepositionReminderToasts()
        {
            if (_activeReminderToasts.Count == 0 ||
                Root == null)
            {
                return;
            }

            var rootWidth =
                Math.Max(
                    ReminderToastWidth +
                    ReminderToastMargin * 2,
                    Root.ActualWidth);

            var rootHeight =
                Math.Max(
                    200,
                    Root.ActualHeight);

            var x =
                Math.Max(
                    ReminderToastMargin,
                    rootWidth -
                    ReminderToastWidth -
                    ReminderToastMargin);

            var cursorBottom =
                rootHeight -
                ReminderToastMargin;

            // La notificación más reciente queda abajo y las anteriores se
            // apilan hacia arriba.
            for (var index =
                     _activeReminderToasts.Count - 1;
                 index >= 0;
                 index--)
            {
                var entry =
                    _activeReminderToasts[index];

                if (!entry.Popup.IsOpen ||
                    entry.Popup.Child is not
                        FrameworkElement child)
                {
                    continue;
                }

                var height =
                    child.ActualHeight > 10
                        ? child.ActualHeight
                        : 116;

                cursorBottom -= height;

                entry.Popup.HorizontalOffset =
                    x;

                entry.Popup.VerticalOffset =
                    Math.Max(
                        ReminderToastMargin,
                        cursorBottom);

                cursorBottom -=
                    ReminderToastGap;
            }
        }

        private void ClearReminderToasts()
        {
            foreach (var entry in
                     _activeReminderToasts.ToList())
            {
                entry.Popup.IsOpen = false;
                entry.Popup.Child = null;
            }

            _activeReminderToasts.Clear();
        }

        private async Task ShowIndexedReminderAsync(
            IndexedFileReminder reminder)
        {
            // Segunda validación por seguridad. También cubre recordatorios
            // pospuestos que fueron creados antes de cambiar de usuario.
            if (!ReminderBelongsToConfiguredUser(reminder))
                return;

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

                var isProjectReminder =
                    IsReminderProjectMessage(
                        reminder.Message);

                Uri? notionUri = null;

                var hasNotionTarget =
                    reminder.Source ==
                        Models.Weblab.SearchSource.Notion &&
                    Uri.TryCreate(
                        reminder.Target,
                        UriKind.Absolute,
                        out notionUri);

                if (isProjectReminder &&
                    reminder.Source ==
                        Models.Weblab.SearchSource.Notion &&
                    !string.IsNullOrWhiteSpace(
                        reminder.PageId))
                {
                    var openActivityButton =
                        new Button
                        {
                            Content = "Abrir actividad",
                            HorizontalAlignment =
                                HorizontalAlignment.Left
                        };

                    ToolTipService.SetToolTip(
                        openActivityButton,
                        "Abrir primero la actividad real vinculada al proyecto o revisión.");

                    openActivityButton.Click +=
                        async (_, __) =>
                        {
                            actionStatus.Text =
                                "Buscando la actividad real...";

                            reminderDialog?.Hide();

                            await Task.Delay(150);

                            await OpenReminderQuickActionAsync(
                                reminder,
                                "open-original");
                        };

                    actions.Children.Add(
                        openActivityButton);
                }

                if (!isProjectReminder &&
                    hasNotionTarget &&
                    notionUri != null)
                {
                    actions.Children.Add(
                        CreateReminderNotionButton(
                            "Abrir recordatorio",
                            notionUri,
                            actionStatus,
                            "Recordatorio"));
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

                    actions.Children.Add(
                        conversationButton);
                }

                if (isProjectReminder &&
                    hasNotionTarget &&
                    notionUri != null)
                {
                    actions.Children.Add(
                        CreateReminderNotionButton(
                            "Abrir notificación",
                            notionUri,
                            actionStatus,
                            "Notificación"));
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

                if (reminder.Source ==
                        Models.Weblab.SearchSource.Notion &&
                    !string.IsNullOrWhiteSpace(
                        reminder.PageId))
                {
                    var isReviewAlert =
                        IsReminderReviewAlert(
                            reminder.Message);

                    content.Children.Add(
                        new TextBlock
                        {
                            Text = "Acciones rápidas",
                            Margin = new Thickness(0, 4, 0, 0),
                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold,
                            Opacity = 0.88
                        });

                    var quickActions = new Grid
                    {
                        ColumnSpacing = 8,
                        RowSpacing = 8
                    };

                    for (var index = 0;
                         index < 3;
                         index++)
                    {
                        quickActions.ColumnDefinitions.Add(
                            new ColumnDefinition
                            {
                                Width = new GridLength(
                                    1,
                                    GridUnitType.Star)
                            });
                    }

                    quickActions.RowDefinitions.Add(
                        new RowDefinition
                        {
                            Height = GridLength.Auto
                        });

                    quickActions.RowDefinitions.Add(
                        new RowDefinition
                        {
                            Height = GridLength.Auto
                        });

                    Button AddQuickAction(
                        string text,
                        string action,
                        int column,
                        int row,
                        bool enabled = true,
                        string? toolTip = null)
                    {
                        var button = new Button
                        {
                            Content = text,
                            Tag = action,
                            IsEnabled = enabled,
                            HorizontalAlignment =
                                HorizontalAlignment.Stretch,
                            HorizontalContentAlignment =
                                HorizontalAlignment.Center,
                            MinHeight = 36,
                            Padding =
                                new Thickness(10, 6, 10, 6)
                        };

                        if (!string.IsNullOrWhiteSpace(toolTip))
                        {
                            ToolTipService.SetToolTip(
                                button,
                                toolTip);
                        }

                        button.Click += async (_, __) =>
                        {
                            actionStatus.Text =
                                $"Abriendo {text.ToLowerInvariant()}...";

                            reminderDialog?.Hide();

                            await Task.Delay(150);

                            await OpenReminderQuickActionAsync(
                                reminder,
                                action);
                        };

                        Grid.SetColumn(button, column);
                        Grid.SetRow(button, row);
                        quickActions.Children.Add(button);

                        return button;
                    }

                    AddQuickAction(
                        "Historial",
                        "history",
                        0,
                        0);

                    AddQuickAction(
                        "Reasignar",
                        "reassign",
                        1,
                        0,
                        enabled: !isReviewAlert,
                        toolTip: isReviewAlert
                            ? "Las alertas de revisión no se pueden reasignar."
                            : null);

                    AddQuickAction(
                        "Reprogramar",
                        "reschedule",
                        2,
                        0,
                        enabled: !isReviewAlert,
                        toolTip: isReviewAlert
                            ? "Las alertas de revisión no se pueden reprogramar."
                            : null);

                    AddQuickAction(
                        isReviewAlert
                            ? "Atender alerta"
                            : "Terminar",
                        "complete",
                        0,
                        1);

                    AddQuickAction(
                        isReviewAlert
                            ? "Eliminar notificación"
                            : "Eliminar",
                        "delete",
                        1,
                        1);

                    AddQuickAction(
                        "Marcar como visto",
                        "mark-read",
                        2,
                        1);

                    content.Children.Add(quickActions);
                }

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
                    new ComboBoxItem { Content = "15 minutos", Tag = "15" });
                snoozeCombo.Items.Add(
                    new ComboBoxItem { Content = "30 minutos", Tag = "30" });
                snoozeCombo.Items.Add(
                    new ComboBoxItem { Content = "1 hora", Tag = "60" });
                snoozeCombo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = "📅 Elegir fecha y hora…",
                        Tag = "custom"
                    });

                var defaultCustomTarget =
                    DateTimeOffset.Now.AddHours(1);

                var customSnoozeDatePicker =
                    new DatePicker
                    {
                        Header = "Fecha",
                        MinYear =
                            new DateTimeOffset(
                                DateTime.Today),
                        SelectedDate =
                            new DateTimeOffset(
                                defaultCustomTarget.Date),
                        HorizontalAlignment =
                            HorizontalAlignment.Stretch
                    };

                var customSnoozeTimePicker =
                    new TimePicker
                    {
                        Header = "Hora",
                        ClockIdentifier = "12HourClock",
                        MinuteIncrement = 1,
                        SelectedTime =
                            defaultCustomTarget.TimeOfDay,
                        HorizontalAlignment =
                            HorizontalAlignment.Stretch
                    };

                var customSnoozePanel =
                    new Grid
                    {
                        ColumnSpacing = 8,
                        Margin = new Thickness(0, 8, 0, 0),
                        Visibility = Visibility.Collapsed
                    };

                customSnoozePanel.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(
                            1,
                            GridUnitType.Star)
                    });

                customSnoozePanel.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(
                            1,
                            GridUnitType.Star)
                    });

                Grid.SetColumn(
                    customSnoozeDatePicker,
                    0);

                customSnoozePanel.Children.Add(
                    customSnoozeDatePicker);

                Grid.SetColumn(
                    customSnoozeTimePicker,
                    1);

                customSnoozePanel.Children.Add(
                    customSnoozeTimePicker);

                snoozeCombo.SelectionChanged +=
                    (_, __) =>
                    {
                        var selectedTag =
                            (snoozeCombo.SelectedItem as
                                ComboBoxItem)?
                                .Tag?
                                .ToString();

                        customSnoozePanel.Visibility =
                            string.Equals(
                                selectedTag,
                                "custom",
                                StringComparison.OrdinalIgnoreCase)
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                    };

                var snoozeButton = new Button
                {
                    Content = "Posponer",
                    VerticalAlignment = VerticalAlignment.Bottom
                };

                var snoozeOptionsPanel =
                    new StackPanel
                    {
                        Spacing = 0
                    };

                snoozeOptionsPanel.Children.Add(
                    snoozeCombo);

                snoozeOptionsPanel.Children.Add(
                    customSnoozePanel);

                Grid.SetColumn(
                    snoozeOptionsPanel,
                    0);

                snoozePanel.Children.Add(
                    snoozeOptionsPanel);

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
                        ContentDialogButton.Primary,
                    MinWidth = 620,
                    MaxWidth = 620
                };

                reminderDialog.Resources[
                    "ContentDialogMinWidth"] = 620d;

                reminderDialog.Resources[
                    "ContentDialogMaxWidth"] = 620d;

                var dialog = reminderDialog;

                snoozeButton.Click += (_, __) =>
                {
                    if (snoozeCombo.SelectedItem is not
                            ComboBoxItem selected)
                    {
                        actionStatus.Text =
                            "Selecciona un tiempo para posponer.";
                        return;
                    }

                    var selectedTag =
                        selected.Tag?.ToString() ??
                        string.Empty;

                    if (string.Equals(
                            selectedTag,
                            "custom",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!customSnoozeDatePicker
                                .SelectedDate.HasValue ||
                            !customSnoozeTimePicker
                                .SelectedTime.HasValue)
                        {
                            actionStatus.Text =
                                "Selecciona una fecha y una hora.";
                            return;
                        }

                        var selectedLocalDateTime =
                            customSnoozeDatePicker
                                .SelectedDate
                                .Value
                                .Date
                                .Add(
                                    customSnoozeTimePicker
                                        .SelectedTime
                                        .Value);

                        var selectedTarget =
                            new DateTimeOffset(
                                DateTime.SpecifyKind(
                                    selectedLocalDateTime,
                                    DateTimeKind.Local));

                        var delay =
                            selectedTarget -
                            DateTimeOffset.Now;

                        if (delay <=
                            TimeSpan.FromSeconds(30))
                        {
                            actionStatus.Text =
                                "La fecha y hora deben ser futuras.";
                            return;
                        }

                        _indexedReminderService.Snooze(
                            reminder,
                            delay);

                        actionStatus.Text =
                            $"Recordatorio pospuesto hasta " +
                            $"{selectedTarget:dd/MM/yyyy h:mm tt} ✅";

                        dialog.Hide();
                        return;
                    }

                    if (!double.TryParse(
                            selectedTag,
                            out var minutes))
                    {
                        actionStatus.Text =
                            "Selecciona un tiempo válido.";
                        return;
                    }

                    _indexedReminderService.Snooze(
                        reminder,
                        TimeSpan.FromMinutes(minutes));

                    actionStatus.Text =
                        $"Recordatorio pospuesto {selected.Content} ✅";

                    dialog.Hide();
                };

                var result =
                    await dialog.ShowAsync();

                if (result ==
                        ContentDialogResult.Primary)
                {
                    _indexedReminderService.Acknowledge(
                        reminder);

                    if (!string.IsNullOrWhiteSpace(
                            reminder.PageId))
                    {
                        SearchView.RequestReminderQuickAction(
                            reminder.PageId,
                            "mark-read");
                    }
                }
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

        private static Button CreateReminderNotionButton(
            string text,
            Uri notionUri,
            TextBlock actionStatus,
            string successLabel)
        {
            var button = new Button
            {
                Content = text,
                HorizontalAlignment =
                    HorizontalAlignment.Left
            };

            button.Click += async (_, __) =>
            {
                try
                {
                    var desktopUri = new Uri(
                        notionUri.AbsoluteUri.Replace(
                            "https://",
                            "notion://",
                            StringComparison.OrdinalIgnoreCase));

                    var support =
                        await Windows.System.Launcher
                            .QueryUriSupportAsync(
                                desktopUri,
                                Windows.System
                                    .LaunchQuerySupportType.Uri);

                    var opened =
                        support == Windows.System
                            .LaunchQuerySupportStatus.Available &&
                        await Windows.System.Launcher
                            .LaunchUriAsync(desktopUri);

                    if (!opened)
                    {
                        opened =
                            await Windows.System.Launcher
                                .LaunchUriAsync(notionUri);
                    }

                    actionStatus.Text = opened
                        ? $"{successLabel} abierto ✅"
                        : $"No se pudo abrir {successLabel.ToLowerInvariant()}.";
                }
                catch (Exception ex)
                {
                    actionStatus.Text =
                        $"No se pudo abrir → {ex.Message}";
                }
            };

            return button;
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

        private async Task OpenReminderQuickActionAsync(
            IndexedFileReminder reminder,
            string action)
        {
            if (string.IsNullOrWhiteSpace(
                    reminder.PageId) ||
                string.IsNullOrWhiteSpace(action))
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
            else
            {
                await Task.Delay(100);
            }

            SearchView.RequestReminderQuickAction(
                reminder.PageId,
                action);
        }

        private static bool IsReminderReviewAlert(
            string? message)
        {
            var value =
                (message ?? string.Empty).Trim();

            return value.StartsWith(
                       "Actividad lista para revisión",
                       StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith(
                       "Correcciones solicitadas",
                       StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith(
                       "Revisión aprobada",
                       StringComparison.OrdinalIgnoreCase);
        }


        private static bool IsReminderProjectMessage(
            string? message)
        {
            if (IsReminderReviewAlert(message))
                return true;

            var value =
                (message ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(value))
                return false;

            var hasProjectToken =
                Regex.IsMatch(
                    value,
                    @"(?<![a-z0-9_])(?:sseo|aapli|aads|wwebs|pprog|sprtuzrevision|prtuzrevision|rtuzrevision|zrevision)(?![a-z0-9_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            return hasProjectToken &&
                   (value.Contains('/') ||
                    value.Contains(
                        "revisión",
                        StringComparison.OrdinalIgnoreCase) ||
                    value.Contains(
                        "revision",
                        StringComparison.OrdinalIgnoreCase));
        }

        // ═══════════════════════════════════════════
        // Ciclo de vida
        // ═══════════════════════════════════════════

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            DropboxIndexCoordinator.StateChanged -= OnDropboxStateChanged;

            _indexedReminderService.ReminderDue -=
                IndexedReminderService_ReminderDue;

            ClearReminderToasts();
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