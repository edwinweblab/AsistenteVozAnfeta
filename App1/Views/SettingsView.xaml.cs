using Anfeta.UI.Services;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.CoreAudioApi;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Dropbox;
using System.Collections.Generic;
using System.Linq;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class SettingsView : Page
    {
        private readonly AudioService _audioService;
        private readonly SettingsService _settingsService;
        private readonly AppStateService _appState;
        private readonly DropboxAuthService _dropboxAuthService;
        private readonly WhatsAppBridgeService _whatsAppBridgeService;
        private readonly DispatcherTimer _statusTimer;

        private CancellationTokenSource? _whatsAppConnectionCts;
        private bool _whatsAppBusy;

        // Dropbox
        private CancellationTokenSource? _indexCts;
        private const string LS_NotionToken = "Notion.Token";
        private const string LS_NotionDataSourceId = "Notion.DataSourceId";
        private const string LS_NotionLastSyncUtc = "Notion.LastSyncUtc";
        private const string LS_CurrentUserTag = "Messaging.CurrentUserTag";
        public SettingsView()
        {
            InitializeComponent();

            _audioService = new AudioService();
            _appState = App.AppHost.Services.GetRequiredService<AppStateService>();
            _dropboxAuthService = App.AppHost.Services.GetRequiredService<DropboxAuthService>();
            _whatsAppBridgeService = new WhatsAppBridgeService();
            _settingsService = new SettingsService(_appState);

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (s, e) =>
            {
                InfoStatus.IsOpen = false;
                _statusTimer.Stop();
            };

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ─────────────────────────────────────────────────────────
        // CICLO DE VIDA
        // ─────────────────────────────────────────────────────────

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadCurrentHotkey();
            LoadCurrentUserIntoUI();
            LoadDropboxRootIntoUI();
            LoadNotionSettingsIntoUI();
            LoadWhatsAppSettingsIntoUI();
            await LoadDropboxApiStateAsync();

            // WhatsApp se comprueba en paralelo para no retrasar la apertura
            // de Configuración si Render estuviera despertando.
            _ = LoadWhatsAppBridgeStateAsync(silent: true);

            _ = Task.Run(async () =>
            {
                await RequestMicPermissionAsync();
                await LoadDevicesAsync();
            });
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _audioService?.StopMicTest();
            _audioService?.StopTestSound();
            _audioService?.Dispose();

            _indexCts?.Cancel();
            _indexCts = null;

            try
            {
                _whatsAppConnectionCts?.Cancel();
                _whatsAppConnectionCts?.Dispose();
            }
            catch
            {
            }

            _whatsAppConnectionCts = null;
        }

        private void LoadCurrentUserIntoUI()
        {
            var savedTag =
                (ApplicationData.Current.LocalSettings.Values[LS_CurrentUserTag] as string ?? string.Empty)
                .Trim();

            foreach (var item in CurrentUserCombo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(
                        item.Tag?.ToString() ?? string.Empty,
                        savedTag,
                        StringComparison.OrdinalIgnoreCase))
                {
                    CurrentUserCombo.SelectedItem = item;
                    UpdateCurrentUserStatus(item);
                    return;
                }
            }

            CurrentUserCombo.SelectedIndex = 0;
        }

        private void CurrentUserCombo_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (CurrentUserCombo.SelectedItem is not ComboBoxItem item)
                return;

            var tag = (item.Tag?.ToString() ?? string.Empty).Trim();
            ApplicationData.Current.LocalSettings.Values[LS_CurrentUserTag] = tag;
            UpdateCurrentUserStatus(item);

            ShowStatus(
                string.IsNullOrWhiteSpace(tag)
                    ? "Usuario sin seleccionar: se mostrarán todos los recordatorios."
                    : $"Usuario actual guardado: {item.Content}",
                InfoBarSeverity.Success);
        }

        private void UpdateCurrentUserStatus(ComboBoxItem item)
        {
            var tag = (item.Tag?.ToString() ?? string.Empty).Trim();
            CurrentUserStatusText.Text = string.IsNullOrWhiteSpace(tag)
                ? "Se mostrarán todos los recordatorios."
                : $"Solo se mostrarán mensajes dirigidos a {item.Content} ({tag}).";
        }

        // ─────────────────────────────────────────────────────────
        // AUDIO — DISPOSITIVOS DEL SISTEMA
        // ─────────────────────────────────────────────────────────

        /// Solicita permiso de micrófono al sistema operativo.
        private async Task RequestMicPermissionAsync()
        {
            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio
                };
                var capture = new MediaCapture();
                await capture.InitializeAsync(settings);
                capture.Dispose();
            }
            catch
            {
                DispatcherQueue.TryEnqueue(() =>
                    ShowStatus(
                        "Permiso de micrófono denegado. Habilítalo en Configuración de Windows.",
                        InfoBarSeverity.Warning));
            }
        }

        /// Lee los dispositivos predeterminados del sistema y actualiza la UI y AppState.
        private async Task LoadDevicesAsync()
        {
            await Task.Run(() =>
            {
                var inputName = GetSystemDefaultDeviceName(DataFlow.Capture);
                var outputName = GetSystemDefaultDeviceName(DataFlow.Render);

                DispatcherQueue.TryEnqueue(() =>
                {
                    TxtStatusInput.Text = $"Entrada: {inputName}";
                    TxtStatusOutput.Text = $"Salida: {outputName}";

                    TxtMicDevice.Text = inputName;
                    TxtSpeakerDevice.Text = outputName;

                    _appState.InputDeviceName = inputName;
                    _appState.OutputDeviceName = outputName;
                });
            });
        }

        /// Obtiene el nombre amigable del dispositivo de audio predeterminado de Windows.
        /// DataFlow.Capture = micrófono, DataFlow.Render = altavoces.
        private static string GetSystemDefaultDeviceName(DataFlow flow)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                return device?.FriendlyName ?? "No disponible";
            }
            catch
            {
                return "No disponible";
            }
        }

        // ─────────────────────────────────────────────────────────
        // AUDIO — PRUEBAS
        // ─────────────────────────────────────────────────────────

        /// Inicia una prueba de nivel de micrófono durante 3 segundos.
        private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PnlMicLevel.Visibility = Visibility.Visible;
                PgMicLevel.ShowPaused = false;
                PgMicLevel.ShowError = false;

                BtnTestMic.IsEnabled = false;
                IconTestMic.Glyph = "\uE769";
                TxtTestMic.Text = "Escuchando...";

                _audioService.StartMicTest(0, level =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        PgMicLevel.Value = level;
                        TxtMicLevel.Text = $"{(int)level}%";
                    });
                });

                await Task.Delay(3000);
                _audioService.StopMicTest();

                PnlMicLevel.Visibility = Visibility.Collapsed;
                PgMicLevel.Value = 0;
                TxtMicLevel.Text = "0%";

                ShowStatus("Prueba completada", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", InfoBarSeverity.Error);
                _audioService.StopMicTest();
            }
            finally
            {
                BtnTestMic.IsEnabled = true;
                IconTestMic.Glyph = "\uE768";
                TxtTestMic.Text = "Probar micrófono";
            }
        }

        /// Reproduce un tono de prueba por el dispositivo de salida predeterminado del sistema.
        private async void BtnTestSpeaker_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnTestSpeaker.IsEnabled = false;
                IconTestSpeaker.Glyph = "\uE769";
                TxtTestSpeaker.Text = "Reproduciendo...";

                await _audioService.PlayTestSound(0);
                ShowStatus("Sonido reproducido correctamente", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                BtnTestSpeaker.IsEnabled = true;
                IconTestSpeaker.Glyph = "\uE768";
                TxtTestSpeaker.Text = "Probar sonido";
            }
        }

        // ─────────────────────────────────────────────────────────
        // AUDIO — CONFIGURACIÓN DE WINDOWS
        // ─────────────────────────────────────────────────────────

        /// Abre directamente la página de sonido en Configuración de Windows.
        private async void BtnOpenWindowsSettings_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));
        }

        /// Muestra un flyout con pasos para solucionar problemas comunes de audio.
        private void BtnShowTroubleshoot_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };

            var scrollViewer = new ScrollViewer
            {
                MaxHeight = 500,
                Width = 400,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var content = new StackPanel { Spacing = 20, Padding = new Thickness(16) };

            content.Children.Add(CreateProblemSection(
                "\uE767",
                "Windows usa dispositivos predeterminados",
                "El reconocimiento de voz SIEMPRE usa el micrófono predeterminado de Windows.",
                new[]
                {
                    "1. Win + I → Sistema → Sonido",
                    "2. En 'Entrada', selecciona tu micrófono preferido",
                    "3. Hazlo predeterminado",
                    "4. Reinicia esta app"
                }
            ));

            content.Children.Add(CreateProblemSection(
                "\uE7BA",
                "Error: Política de privacidad no aceptada",
                "Si ves \"The speech privacy policy was not accepted\":",
                new[]
                {
                    "1. Win + I → Privacidad y seguridad → Voz",
                    "2. Activa \"Reconocimiento de voz en línea\"",
                    "3. Reinicia esta app"
                }
            ));

            scrollViewer.Content = content;
            flyout.Content = scrollViewer;
            flyout.ShowAt(BtnShowTroubleshoot);
        }

        /// Construye una sección de problema con ícono, título, descripción y pasos.
        private static StackPanel CreateProblemSection(
            string icon, string title, string description, string[] steps)
        {
            var section = new StackPanel { Spacing = 10 };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(new FontIcon
            {
                Glyph = icon,
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 251, 191, 36))
            });
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            });
            section.Children.Add(header);

            section.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 140, 123, 110))
            });

            var stepsList = new StackPanel { Spacing = 6, Margin = new Thickness(20, 4, 0, 0) };
            foreach (var step in steps)
                stepsList.Children.Add(new TextBlock
                {
                    Text = step,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13
                });

            section.Children.Add(stepsList);
            return section;
        }

        // ─────────────────────────────────────────────────────────
        // HOTKEY
        // ─────────────────────────────────────────────────────────

        /// Carga y muestra el atajo de teclado actual guardado en AppState.
        private void LoadCurrentHotkey()
        {
            TxtHotkeyDisplay.Text = _appState.GetHotkeyDisplayString();
            TxtSearchHotkeyDisplay.Text = _appState.GetSearchHotkeyDisplayString();
        }
        private async void BtnChangeSearchHotkey_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Anfeta.UI.Dialogs.HotkeyPickerDialog(
                _appState,
                _settingsService,
                Anfeta.UI.Dialogs.HotkeyTarget.Search)
            {
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                TxtSearchHotkeyDisplay.Text = _appState.GetSearchHotkeyDisplayString();
                ShowStatus("Atajo del buscador actualizado correctamente", InfoBarSeverity.Success);
            }
        }
        /// Abre el diálogo para cambiar el atajo de teclado global.
        private async void BtnChangeHotkey_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Anfeta.UI.Dialogs.HotkeyPickerDialog(_appState, _settingsService)
            {
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                TxtHotkeyDisplay.Text = _appState.GetHotkeyDisplayString();
                ShowStatus("Atajo actualizado correctamente", InfoBarSeverity.Success);
            }
        }

        // ─────────────────────────────────────────────────────────
        // DROPBOX
        // ─────────────────────────────────────────────────────────

        private async Task LoadDropboxApiStateAsync()
        {
            try
            {
                var hasConnection = await _dropboxAuthService.HasSavedConnectionAsync();

                if (!hasConnection)
                {
                    SetDropboxApiDisconnectedUi();
                    return;
                }

                var values = ApplicationData.Current.LocalSettings.Values;
                var savedName = values[LS_DropboxAccountName] as string;
                var savedEmail = values[LS_DropboxAccountEmail] as string;

                DropboxApiStatusText.Text = "Dropbox vinculado";
                DropboxApiAccountText.Text =
                    string.IsNullOrWhiteSpace(savedName) && string.IsNullOrWhiteSpace(savedEmail)
                        ? "Cuenta guardada. Usa Probar conexión para validarla."
                        : $"{savedName} · {savedEmail}".Trim(' ', '·');

                BtnConnectDropbox.IsEnabled = false;
                BtnTestDropboxConnection.IsEnabled = true;
                BtnDisconnectDropbox.IsEnabled = true;
                DropboxAuthorizationPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                SetDropboxApiDisconnectedUi();
                ShowStatus($"No se pudo leer la conexión Dropbox -> {ex.Message}", InfoBarSeverity.Warning);
            }
        }

        private async void BtnConnectDropbox_Click(object sender, RoutedEventArgs e)
        {
            SetDropboxApiButtonsEnabled(false);

            try
            {
                DropboxAuthorizationCodeBox.Text = string.Empty;
                DropboxAuthorizationPanel.Visibility = Visibility.Visible;
                DropboxApiStatusText.Text = "Esperando autorización...";
                DropboxApiAccountText.Text = "Completa el proceso en el navegador.";

                await _dropboxAuthService.BeginAuthorizationAsync();

                BtnCompleteDropboxAuthorization.IsEnabled = true;
                ShowStatus(
                    "Dropbox abierto en el navegador. Autoriza y pega el código en ANFETA.",
                    InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                DropboxAuthorizationPanel.Visibility = Visibility.Collapsed;
                SetDropboxApiDisconnectedUi();
                ShowStatus($"Error iniciando Dropbox -> {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                BtnConnectDropbox.IsEnabled =
                    DropboxAuthorizationPanel.Visibility != Visibility.Visible;
            }
        }

        private async void BtnCompleteDropboxAuthorization_Click(object sender, RoutedEventArgs e)
        {
            var code = (DropboxAuthorizationCodeBox.Text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                ShowStatus("Pega el código de autorización de Dropbox.", InfoBarSeverity.Warning);
                return;
            }

            SetDropboxApiButtonsEnabled(false);
            BtnCompleteDropboxAuthorization.IsEnabled = false;

            try
            {
                DropboxApiStatusText.Text = "Conectando Dropbox...";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var account = await _dropboxAuthService.CompleteAuthorizationAsync(code, cts.Token);

                SaveDropboxAccount(account.DisplayName, account.Email, account.AccountId);
                ApplyDropboxConnectedUi(account.DisplayName, account.Email);

                DropboxAuthorizationCodeBox.Text = string.Empty;
                DropboxAuthorizationPanel.Visibility = Visibility.Collapsed;

                ShowStatus("Dropbox vinculado correctamente ✅", InfoBarSeverity.Success);
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Tiempo agotado conectando Dropbox.", InfoBarSeverity.Warning);
                BtnCompleteDropboxAuthorization.IsEnabled = true;
            }
            catch (Exception ex)
            {
                DropboxApiStatusText.Text = "Error de conexión";
                DropboxApiAccountText.Text = ex.Message;
                BtnCompleteDropboxAuthorization.IsEnabled = true;
                ShowStatus($"Error Dropbox -> {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                var connected = await _dropboxAuthService.HasSavedConnectionAsync();
                BtnConnectDropbox.IsEnabled = !connected;
                BtnTestDropboxConnection.IsEnabled = connected;
                BtnDisconnectDropbox.IsEnabled = connected;
            }
        }

        private async void BtnTestDropboxConnection_Click(object sender, RoutedEventArgs e)
        {
            SetDropboxApiButtonsEnabled(false);

            try
            {
                DropboxApiStatusText.Text = "Probando conexión...";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var account = await _dropboxAuthService.TestConnectionAsync(cts.Token);

                SaveDropboxAccount(account.DisplayName, account.Email, account.AccountId);
                ApplyDropboxConnectedUi(account.DisplayName, account.Email);

                ShowStatus("Conexión con Dropbox correcta ✅", InfoBarSeverity.Success);
            }
            catch (OperationCanceledException)
            {
                DropboxApiStatusText.Text = "Tiempo agotado";
                ShowStatus("Dropbox tardó demasiado en responder.", InfoBarSeverity.Warning);
            }
            catch (Exception ex)
            {
                DropboxApiStatusText.Text = "Error de conexión";
                DropboxApiAccountText.Text = ex.Message;
                ShowStatus($"Error Dropbox -> {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                var connected = await _dropboxAuthService.HasSavedConnectionAsync();
                BtnConnectDropbox.IsEnabled = !connected;
                BtnTestDropboxConnection.IsEnabled = connected;
                BtnDisconnectDropbox.IsEnabled = connected;
            }
        }

        private async void BtnDisconnectDropbox_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Desvincular Dropbox",
                Content = "ANFETA eliminará de este equipo la credencial guardada de Dropbox. No borrará archivos ni carpetas.",
                PrimaryButtonText = "Desvincular",
                CloseButtonText = "Cancelar",
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            try
            {
                await _dropboxAuthService.DisconnectAsync();
                ClearDropboxAccount();
                SetDropboxApiDisconnectedUi();

                ShowStatus("Dropbox desvinculado.", InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error desvinculando Dropbox -> {ex.Message}", InfoBarSeverity.Error);
            }
        }

        private void ApplyDropboxConnectedUi(string displayName, string email)
        {
            DropboxApiStatusText.Text = "Dropbox vinculado ✅";
            DropboxApiAccountText.Text =
                string.IsNullOrWhiteSpace(email)
                    ? displayName
                    : $"{displayName} · {email}";

            BtnConnectDropbox.IsEnabled = false;
            BtnTestDropboxConnection.IsEnabled = true;
            BtnDisconnectDropbox.IsEnabled = true;
            DropboxAuthorizationPanel.Visibility = Visibility.Collapsed;
        }

        private void SetDropboxApiDisconnectedUi()
        {
            DropboxApiStatusText.Text = "Dropbox no vinculado.";
            DropboxApiAccountText.Text = "Sin cuenta conectada.";
            DropboxAuthorizationCodeBox.Text = string.Empty;
            DropboxAuthorizationPanel.Visibility = Visibility.Collapsed;

            BtnConnectDropbox.IsEnabled = true;
            BtnTestDropboxConnection.IsEnabled = false;
            BtnDisconnectDropbox.IsEnabled = false;
        }

        private void SetDropboxApiButtonsEnabled(bool enabled)
        {
            BtnConnectDropbox.IsEnabled = enabled;
            BtnTestDropboxConnection.IsEnabled = enabled;
            BtnDisconnectDropbox.IsEnabled = enabled;
        }

        private static void SaveDropboxAccount(string name, string email, string accountId)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values[LS_DropboxAccountName] = name ?? string.Empty;
            values[LS_DropboxAccountEmail] = email ?? string.Empty;
            values[LS_DropboxAccountId] = accountId ?? string.Empty;
        }

        private static void ClearDropboxAccount()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values.Remove(LS_DropboxAccountName);
            values.Remove(LS_DropboxAccountEmail);
            values.Remove(LS_DropboxAccountId);
        }

        /// Carga en el TextBox la ruta de Dropbox guardada en LocalSettings.
        private void LoadDropboxRootIntoUI()
        {
            var saved = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;
            DropboxPathBox.Text = (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                ? saved
                : string.Empty;
        }

        /// Permite al usuario seleccionar una carpeta raíz e inicia la indexación.
        private async void BtnPickDropboxRoot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");

                var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
                InitializeWithWindow.Initialize(picker, hwnd);

                var folder = await picker.PickSingleFolderAsync();
                if (folder == null)
                {
                    ShowStatus("Selección cancelada.", InfoBarSeverity.Informational);
                    return;
                }

                var selectedPath = folder.Path;
                if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
                {
                    ShowStatus("Ruta inválida. Intenta con otra carpeta.", InfoBarSeverity.Warning);
                    return;
                }

                ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] = selectedPath;

                _indexCts?.Cancel();
                _indexCts = new CancellationTokenSource();
                var ct = _indexCts.Token;

                App.LocalIndex.Clear();
                await LocalIndexPersistence.ClearAsync();
                DropboxIndexCoordinator.StartIndexing(selectedPath);

                DropboxPathBox.Text = selectedPath;
                BtnPickDropboxRoot.IsEnabled = false;
                BtnResetDropboxRoot.IsEnabled = false;

                ShowStatus("Ruta nueva detectada, indexando...", InfoBarSeverity.Informational);

                try
                {
                    // 1. Indexar carpeta local
                    var list = await LocalIndexBuilder.BuildAsync(selectedPath, ct);

                    // 2. Si Notion está configurado, agregar páginas de Notion al mismo índice
                    var notionToken = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
                    var notionDataSourceId = ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] as string;

                    string? notionWarning = null;
                    int notionCount = 0;

                    if (!string.IsNullOrWhiteSpace(notionToken) &&
                        !string.IsNullOrWhiteSpace(notionDataSourceId))
                    {
                        try
                        {
                            ShowStatus("Carpeta indexada. Sincronizando Notion...", InfoBarSeverity.Informational);

                            var notionItems = await NotionIndexBuilder.BuildAsync(
                                notionToken,
                                notionDataSourceId,
                                ct);

                            notionCount = notionItems.Count;
                            list.AddRange(notionItems);

                            ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] =
                                DateTimeOffset.UtcNow.ToString("O");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception notionEx)
                        {
                            notionWarning = notionEx.Message;
                        }
                    }

                    // 3. Guardar índice combinado: Local + Notion
                    App.LocalIndex.Set(list);

                    await LocalIndexPersistence.SaveAsync(selectedPath, list, ct);

                    ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] =
                        DateTimeOffset.UtcNow.ToString("O");

                    DropboxIndexCoordinator.MarkReady(selectedPath);

                    if (!string.IsNullOrWhiteSpace(notionWarning))
                    {
                        ShowStatus(
                            $"Índice local listo ({App.LocalIndex.Count} items), pero Notion falló -> {notionWarning}",
                            InfoBarSeverity.Warning);
                    }
                    else if (notionCount > 0)
                    {
                        ShowStatus(
                            $"Índice listo ({App.LocalIndex.Count} items) · Notion: {notionCount} páginas",
                            InfoBarSeverity.Success);
                    }
                    else
                    {
                        ShowStatus(
                            $"Índice listo ({App.LocalIndex.Count} items)",
                            InfoBarSeverity.Success);
                    }
                }
                catch (OperationCanceledException)
                {
                    ShowStatus("Indexación cancelada.", InfoBarSeverity.Warning);
                }
                catch (Exception ex)
                {
                    DropboxIndexCoordinator.MarkError(selectedPath, ex.Message);
                    ShowStatus($"Error indexando -> {ex.Message}", InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"Error eligiendo carpeta -> {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                BtnPickDropboxRoot.IsEnabled = true;
                BtnResetDropboxRoot.IsEnabled = true;
            }
        }

        /// Limpia la ruta de Dropbox y el índice local tras confirmación del usuario.
        private async void BtnResetDropboxRoot_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ContentDialog
            {
                Title = "Cambiar ruta de Dropbox",
                Content = "Esto limpiará la ruta actual.\n\n¿Deseas continuar?",
                PrimaryButtonText = "Continuar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.XamlRoot
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                return;

            _indexCts?.Cancel();
            _indexCts = null;

            ApplicationData.Current.LocalSettings.Values.Remove(LS_DropboxRoot);
            App.LocalIndex.Clear();
            await LocalIndexPersistence.ClearAsync();
            ApplicationData.Current.LocalSettings.Values.Remove(LS_LastIndexedUtc);
            DropboxIndexCoordinator.Reset();

            DropboxPathBox.Text = string.Empty;
            ShowStatus("Ruta reiniciada. Configura una nueva carpeta.", InfoBarSeverity.Informational);
        }
        // ─────────────────────────────────────────────────────────
        // WHATSAPP WEB — BRIDGE RENDER + BAILEYS
        // ─────────────────────────────────────────────────────────

        private void LoadWhatsAppSettingsIntoUI()
        {
            WhatsAppBridgeUrlBox.Text =
                WhatsAppBridgeService.GetSavedBridgeUrl();

            WhatsAppApiKeyBox.Password =
                WhatsAppBridgeService.GetSavedApiKey();

            WhatsAppSeedParticipantBox.Text =
                WhatsAppBridgeService.GetSavedSeedParticipant();

            _whatsAppBridgeService.ReloadConfiguration();

            if (!_whatsAppBridgeService.IsConfigured)
            {
                SetWhatsAppDisconnectedUi(
                    "Falta configurar URL o API key.");
            }
        }

        private bool SaveWhatsAppConfigurationFromUI(
            bool showSuccess)
        {
            try
            {
                WhatsAppBridgeService.SaveConfiguration(
                    WhatsAppBridgeUrlBox.Text,
                    WhatsAppApiKeyBox.Password,
                    WhatsAppSeedParticipantBox.Text);

                _whatsAppBridgeService.ReloadConfiguration();

                if (showSuccess)
                {
                    ShowStatus(
                        "Configuración del WhatsApp Bridge guardada ✅",
                        InfoBarSeverity.Success);
                }

                return true;
            }
            catch (Exception ex)
            {
                ShowStatus(
                    $"WhatsApp Bridge → {ex.Message}",
                    InfoBarSeverity.Warning);
                return false;
            }
        }

        private void BtnSaveWhatsAppBridge_Click(
            object sender,
            RoutedEventArgs e)
        {
            SaveWhatsAppConfigurationFromUI(
                showSuccess: true);
        }

        private async void BtnTestWhatsAppBridge_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!SaveWhatsAppConfigurationFromUI(
                    showSuccess: false))
            {
                return;
            }

            await LoadWhatsAppBridgeStateAsync(
                silent: false);
        }

        private async void BtnConnectWhatsApp_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!SaveWhatsAppConfigurationFromUI(
                    showSuccess: false))
            {
                return;
            }

            await StartAndMonitorWhatsAppAsync();
        }

        private async void BtnShowWhatsAppQr_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!SaveWhatsAppConfigurationFromUI(
                    showSuccess: false))
            {
                return;
            }

            using var cts =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(45));

            try
            {
                await ShowWhatsAppQrAsync(
                    cts.Token);
            }
            catch (Exception ex)
            {
                ShowStatus(
                    $"No se pudo obtener el QR → {ex.Message}",
                    InfoBarSeverity.Warning);
            }
        }

        private void BtnCloseWhatsAppQr_Click(
            object sender,
            RoutedEventArgs e)
        {
            WhatsAppQrPanel.Visibility =
                Visibility.Collapsed;
        }

        private async void BtnChangeWhatsAppAccount_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!SaveWhatsAppConfigurationFromUI(
                    showSuccess: false))
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Cambiar cuenta de WhatsApp",
                Content =
                    "Se desvinculará la cuenta actual de la sesión anfeta-main " +
                    "y se eliminarán sus credenciales persistidas.\n\n" +
                    "Los grupos guardados por dominio + proyecto NO se borrarán. " +
                    "Después ANFETA mostrará un QR para vincular la nueva cuenta.",
                PrimaryButtonText = "Cambiar cuenta",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                CancelWhatsAppMonitor();
                SetWhatsAppBusy(
                    true,
                    "Desvinculando cuenta…");

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(60));

                await _whatsAppBridgeService.UnlinkAsync(
                    cts.Token);

                SetWhatsAppDisconnectedUi(
                    "Cuenta desvinculada. Preparando QR nuevo…");

                WhatsAppQrPanel.Visibility =
                    Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                SetWhatsAppBusy(false, "Tiempo agotado");
                ShowStatus(
                    "El servidor tardó demasiado en desvincular la cuenta.",
                    InfoBarSeverity.Warning);
                return;
            }
            catch (Exception ex)
            {
                SetWhatsAppBusy(false, "Error");
                ShowStatus(
                    $"No se pudo cambiar la cuenta → {ex.Message}",
                    InfoBarSeverity.Error);
                return;
            }
            finally
            {
                SetWhatsAppBusy(false, "Listo");
            }

            await StartAndMonitorWhatsAppAsync();
        }

        private async Task LoadWhatsAppBridgeStateAsync(
            bool silent)
        {
            _whatsAppBridgeService.ReloadConfiguration();

            if (!_whatsAppBridgeService.IsConfigured)
            {
                SetWhatsAppDisconnectedUi(
                    "Configura URL y API key para comenzar.");
                return;
            }

            try
            {
                if (!silent)
                {
                    SetWhatsAppBusy(
                        true,
                        "Verificando servidor…");
                }

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(80));

                var health =
                    await _whatsAppBridgeService.GetHealthAsync(
                        cts.Token);

                if (!silent)
                {
                    WhatsAppOperationText.Text =
                        $"Bridge v{health.Version} disponible";
                }

                var status =
                    await _whatsAppBridgeService.GetStatusAsync(
                        cts.Token);

                UpdateWhatsAppStatusUi(status);

                try
                {
                    var persistence =
                        await _whatsAppBridgeService
                            .GetPersistenceStatusAsync(
                                cts.Token);

                    WhatsAppPersistenceText.Text =
                        persistence.Reachable
                            ? $"{persistence.Mode} ✓ · " +
                              $"sesión {(persistence.SessionStored ? "guardada" : "pendiente")} · " +
                              $"{persistence.GroupsStored} grupo(s)"
                            : $"{persistence.Mode} · sin respuesta";
                }
                catch
                {
                    // El estado de WhatsApp sigue siendo útil incluso si la
                    // consulta informativa de persistencia falla.
                }

                if (!silent)
                {
                    ShowStatus(
                        status.Connected
                            ? "WhatsApp Bridge conectado ✅"
                            : $"WhatsApp Bridge disponible · {status.Status}",
                        status.Connected
                            ? InfoBarSeverity.Success
                            : InfoBarSeverity.Informational);
                }
            }
            catch (OperationCanceledException)
            {
                if (!silent)
                {
                    ShowStatus(
                        "Tiempo agotado verificando WhatsApp Bridge.",
                        InfoBarSeverity.Warning);
                }
            }
            catch (Exception ex)
            {
                SetWhatsAppErrorUi(ex.Message);

                if (!silent)
                {
                    ShowStatus(
                        $"WhatsApp Bridge → {ex.Message}",
                        InfoBarSeverity.Error);
                }
            }
            finally
            {
                if (!silent)
                    SetWhatsAppBusy(false, "Listo");
            }
        }

        private async Task StartAndMonitorWhatsAppAsync()
        {
            CancelWhatsAppMonitor();

            _whatsAppConnectionCts =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(110));

            var token =
                _whatsAppConnectionCts.Token;

            var startedAt =
                DateTimeOffset.Now;

            try
            {
                SetWhatsAppBusy(
                    true,
                    "Despertando servidor…");

                await _whatsAppBridgeService.StartAsync(token);

                while (!token.IsCancellationRequested)
                {
                    var status =
                        await _whatsAppBridgeService.GetStatusAsync(
                            token);

                    UpdateWhatsAppStatusUi(status);

                    var elapsed =
                        DateTimeOffset.Now - startedAt;

                    WhatsAppOperationText.Text =
                        $"{GetWhatsAppFriendlyStatus(status.Status)} · " +
                        $"{elapsed:mm\\:ss}";

                    if (status.Connected)
                    {
                        WhatsAppQrPanel.Visibility =
                            Visibility.Collapsed;

                        ShowStatus(
                            "WhatsApp conectado correctamente ✅",
                            InfoBarSeverity.Success);

                        return;
                    }

                    if (status.QrAvailable ||
                        string.Equals(
                            status.Status,
                            "QR_REQUIRED",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (WhatsAppQrPanel.Visibility !=
                                Visibility.Visible ||
                            WhatsAppQrImage.Source == null)
                        {
                            await ShowWhatsAppQrAsync(token);
                        }

                        WhatsAppQrWaitText.Text =
                            $"Esperando escaneo… {elapsed:mm\\:ss}";
                    }

                    if (string.Equals(
                            status.Status,
                            "ERROR",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            status.Status,
                            "LOGGED_OUT",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(status.LastError)
                                ? $"WhatsApp quedó en estado {status.Status}."
                                : status.LastError);
                    }

                    await Task.Delay(
                        TimeSpan.FromSeconds(2),
                        token);
                }
            }
            catch (OperationCanceledException)
            {
                // Una cancelación temprana corresponde a navegación, cambio
                // de cuenta u otra operación iniciada por el usuario. El timeout
                // real del monitor ocurre después de ~110 segundos.
                if (DateTimeOffset.Now - startedAt < TimeSpan.FromSeconds(100))
                    return;

                ShowStatus(
                    "La conexión de WhatsApp excedió el tiempo de espera. Puedes volver a intentar.",
                    InfoBarSeverity.Warning);
            }
            catch (Exception ex)
            {
                SetWhatsAppErrorUi(ex.Message);
                ShowStatus(
                    $"No se pudo conectar WhatsApp → {ex.Message}",
                    InfoBarSeverity.Error);
            }
            finally
            {
                SetWhatsAppBusy(false, "Listo");
            }
        }

        private async Task ShowWhatsAppQrAsync(
            CancellationToken cancellationToken)
        {
            WhatsAppQrPanel.Visibility =
                Visibility.Visible;
            WhatsAppQrLoadingRing.Visibility =
                Visibility.Visible;
            WhatsAppQrLoadingRing.IsActive = true;
            WhatsAppQrWaitText.Text =
                "Obteniendo código QR…";

            try
            {
                var bytes =
                    await _whatsAppBridgeService.GetQrImageAsync(
                        cancellationToken);

                using var stream =
                    new InMemoryRandomAccessStream();

                var output = stream.GetOutputStreamAt(0);

                using (var writer = new DataWriter(output))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }

                stream.Seek(0);

                var image = new BitmapImage();
                await image.SetSourceAsync(stream);

                WhatsAppQrImage.Source = image;
                WhatsAppQrWaitText.Text =
                    "Esperando escaneo…";
            }
            finally
            {
                WhatsAppQrLoadingRing.IsActive = false;
                WhatsAppQrLoadingRing.Visibility =
                    Visibility.Collapsed;
            }
        }

        private void UpdateWhatsAppStatusUi(
            WhatsAppBridgeStatus status)
        {
            var state =
                (status?.Status ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

            WhatsAppStatusText.Text =
                GetWhatsAppFriendlyStatus(state);

            WhatsAppSessionText.Text =
                string.IsNullOrWhiteSpace(status?.Session)
                    ? "anfeta-main"
                    : status.Session;

            WhatsAppPersistenceText.Text =
                string.Equals(
                    status?.Persistence,
                    "supabase",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Supabase ✓"
                    : string.IsNullOrWhiteSpace(status?.Persistence)
                        ? "Sin comprobar"
                        : status.Persistence;

            WhatsAppConnectedAtText.Text =
                status?.ConnectedAt == null
                    ? "—"
                    : status.ConnectedAt.Value
                        .ToLocalTime()
                        .ToString("dd/MM/yyyy HH:mm:ss");

            WhatsAppStatusDetailText.Text =
                status?.Connected == true
                    ? "La cuenta está lista para crear y localizar grupos desde ANFETA."
                    : !string.IsNullOrWhiteSpace(status?.LastError)
                        ? status.LastError
                        : state == "QR_REQUIRED"
                            ? "Escanea el QR para vincular la cuenta de WhatsApp."
                            : state == "RECONNECTING"
                                ? "Baileys está recuperando la sesión guardada."
                                : "El Bridge está disponible, pero WhatsApp todavía no está conectado.";

            var color = state switch
            {
                "CONNECTED" => Color.FromArgb(255, 34, 197, 94),
                "QR_REQUIRED" => Color.FromArgb(255, 56, 189, 248),
                "STARTING" or "CONNECTING" or "RECONNECTING" =>
                    Color.FromArgb(255, 250, 204, 21),
                "ERROR" or "LOGGED_OUT" =>
                    Color.FromArgb(255, 248, 113, 113),
                _ => Color.FromArgb(255, 100, 116, 139)
            };

            WhatsAppStatusDot.Background =
                new SolidColorBrush(color);

            BtnShowWhatsAppQr.IsEnabled =
                !_whatsAppBusy && status?.QrAvailable == true;

            BtnChangeWhatsAppAccount.IsEnabled =
                !_whatsAppBusy &&
                _whatsAppBridgeService.IsConfigured;
        }

        private void SetWhatsAppDisconnectedUi(
            string detail)
        {
            WhatsAppStatusDot.Background =
                new SolidColorBrush(
                    Color.FromArgb(255, 100, 116, 139));
            WhatsAppStatusText.Text = "Sin vincular";
            WhatsAppStatusDetailText.Text = detail;
            WhatsAppSessionText.Text = "anfeta-main";
            WhatsAppPersistenceText.Text = "Sin comprobar";
            WhatsAppConnectedAtText.Text = "—";
            BtnShowWhatsAppQr.IsEnabled = false;
        }

        private void SetWhatsAppErrorUi(
            string error)
        {
            WhatsAppStatusDot.Background =
                new SolidColorBrush(
                    Color.FromArgb(255, 248, 113, 113));
            WhatsAppStatusText.Text = "Error";
            WhatsAppStatusDetailText.Text =
                string.IsNullOrWhiteSpace(error)
                    ? "No se pudo comunicar con el WhatsApp Bridge."
                    : error;
            BtnShowWhatsAppQr.IsEnabled = false;
        }

        private void SetWhatsAppBusy(
            bool busy,
            string operation)
        {
            _whatsAppBusy = busy;

            WhatsAppOperationRing.IsActive = busy;
            WhatsAppOperationRing.Visibility =
                busy
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            WhatsAppOperationText.Text = operation;

            BtnSaveWhatsAppBridge.IsEnabled = !busy;
            BtnTestWhatsAppBridge.IsEnabled = !busy;
            BtnConnectWhatsApp.IsEnabled = !busy;
            BtnChangeWhatsAppAccount.IsEnabled =
                !busy && _whatsAppBridgeService.IsConfigured;

            if (busy)
                BtnShowWhatsAppQr.IsEnabled = false;
        }

        private void CancelWhatsAppMonitor()
        {
            try
            {
                _whatsAppConnectionCts?.Cancel();
                _whatsAppConnectionCts?.Dispose();
            }
            catch
            {
            }

            _whatsAppConnectionCts = null;
        }

        private static string GetWhatsAppFriendlyStatus(
            string status)
        {
            return (status ?? string.Empty)
                .Trim()
                .ToUpperInvariant() switch
            {
                "CONNECTED" => "Conectado",
                "QR_REQUIRED" => "Esperando QR",
                "STARTING" => "Iniciando",
                "CONNECTING" => "Conectando",
                "RECONNECTING" => "Reconectando",
                "LOGGED_OUT" => "Sesión cerrada",
                "ERROR" => "Error",
                "STOPPED" => "Detenido",
                _ => "Sin comprobar"
            };
        }

        // ─────────────────────────────────────────────────────────
        // NOTION
        // ─────────────────────────────────────────────────────────

        private void LoadNotionSettingsIntoUI()
        {
            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var lastSync = ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] as string;

            NotionTokenBox.Password = token ?? string.Empty;
            NotionSourcesList.ItemsSource = NotionDataSources.Default;

            var enabledBases = NotionDataSources.Default.Count(x => x.Enabled);

            if (!string.IsNullOrWhiteSpace(token))
            {
                NotionStatusText.Text = string.IsNullOrWhiteSpace(lastSync)
                    ? $"Configurado: {enabledBases} bases"
                    : $"Configurado: {enabledBases} bases · Última sincronización: {FormatUtcLocal(lastSync)}";
            }
            else
            {
                NotionStatusText.Text = "Notion no configurado.";
            }
        }

        private bool TryGetNotionInputs(out string token, out string dataSourceId)
        {
            token = (NotionTokenBox.Password ?? string.Empty).Trim();

            dataSourceId = NotionDataSources.Default
                .FirstOrDefault(x => x.Enabled && !string.IsNullOrWhiteSpace(x.DataSourceId))
                ?.DataSourceId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(token))
            {
                ShowStatus("Agrega el token de Notion.", InfoBarSeverity.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(dataSourceId))
            {
                ShowStatus("No hay bases de Notion configuradas.", InfoBarSeverity.Warning);
                return false;
            }

            return true;
        }

        private void BtnSaveNotion_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetNotionInputs(out var token, out var dataSourceId))
                return;

            ApplicationData.Current.LocalSettings.Values[LS_NotionToken] = token;

            // Se guarda solo por compatibilidad con código viejo que aún revisa esta llave.
            ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] = dataSourceId;

            var enabledBases = NotionDataSources.Default.Count(x => x.Enabled);

            NotionStatusText.Text = $"Configurado: {enabledBases} bases";
            ShowStatus("Configuración de Notion guardada.", InfoBarSeverity.Success);
        }

        private async void BtnTestNotion_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetNotionInputs(out var token, out var dataSourceId))
                return;

            SetNotionButtonsEnabled(false);

            try
            {
                var enabledBases = NotionDataSources.Default.Count(x => x.Enabled);
                ShowStatus($"Probando conexión con Notion... ({enabledBases} bases)", InfoBarSeverity.Informational);

                // Evita que la prueba se quede varios minutos si Notion o una base no responde.
                using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                var items = await NotionIndexBuilder.BuildManyAsync(
                    token,
                    NotionDataSources.Default,
                    testCts.Token,
                    maxItemsPerSource: 1);

                ShowStatus(
                    $"Conexión Notion correcta ✅ Bases: {enabledBases} · Páginas de prueba: {items.Count}",
                    InfoBarSeverity.Success);
            }
            catch (OperationCanceledException)
            {
                ShowStatus(
                    "Tiempo agotado probando Notion. Revisa conexión, token o permisos de las bases.",
                    InfoBarSeverity.Warning);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error Notion -> {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                SetNotionButtonsEnabled(true);
            }
        }

        private async void BtnSyncNotion_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetNotionInputs(out var token, out var dataSourceId))
                return;

            ApplicationData.Current.LocalSettings.Values[LS_NotionToken] = token;
            ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] = dataSourceId;

            _indexCts?.Cancel();
            _indexCts = new CancellationTokenSource();
            var ct = _indexCts.Token;

            SetNotionButtonsEnabled(false);

            try
            {
                ShowStatus("Sincronizando páginas de Notion...", InfoBarSeverity.Informational);

                var notionItems = await NotionIndexBuilder.BuildManyAsync(
                token,
                NotionDataSources.Default,
                ct);

                var currentWithoutNotion = App.LocalIndex
                    .GetAll()
                    .Where(x => x.Source != SearchSource.Notion)
                    .ToList();

                currentWithoutNotion.AddRange(notionItems);

                App.LocalIndex.Set(currentWithoutNotion);

                await SaveCurrentIndexIfPossibleAsync(currentWithoutNotion, ct);

                var now = DateTimeOffset.UtcNow.ToString("O");
                ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] = now;

                NotionStatusText.Text = $"Configurado: {NotionDataSources.Default.Count} bases · Última sincronización: {FormatUtcLocal(now)}";

                ShowStatus(
                    $"Notion sincronizado ✅ Bases: {NotionDataSources.Default.Count} · Páginas: {notionItems.Count} · Índice total: {App.LocalIndex.Count}",
                    InfoBarSeverity.Success);
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Sincronización cancelada.", InfoBarSeverity.Warning);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error sincronizando Notion -> {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                SetNotionButtonsEnabled(true);
            }
        }

        private async void BtnResetNotion_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ContentDialog
            {
                Title = "Limpiar configuración de Notion",
                Content = "Esto quitará el token, el Data Source ID y eliminará las páginas de Notion del índice actual.\n\n¿Deseas continuar?",
                PrimaryButtonText = "Continuar",
                CloseButtonText = "Cancelar",
                XamlRoot = this.XamlRoot
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                return;

            try
            {
                ApplicationData.Current.LocalSettings.Values.Remove(LS_NotionToken);
                ApplicationData.Current.LocalSettings.Values.Remove(LS_NotionDataSourceId);
                ApplicationData.Current.LocalSettings.Values.Remove(LS_NotionLastSyncUtc);

                var withoutNotion = App.LocalIndex
                    .GetAll()
                    .Where(x => x.Source != SearchSource.Notion)
                    .ToList();

                if (withoutNotion.Count > 0)
                {
                    App.LocalIndex.Set(withoutNotion);
                    await SaveCurrentIndexIfPossibleAsync(withoutNotion, CancellationToken.None);
                }
                else
                {
                    App.LocalIndex.Clear();
                    await LocalIndexPersistence.ClearAsync();
                }

                NotionTokenBox.Password = string.Empty;
                NotionStatusText.Text = "Notion no configurado.";

                ShowStatus("Configuración de Notion limpiada.", InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error limpiando Notion -> {ex.Message}", InfoBarSeverity.Error);
            }
        }

        private void SetNotionButtonsEnabled(bool enabled)
        {
            BtnSaveNotion.IsEnabled = enabled;
            BtnTestNotion.IsEnabled = enabled;
            BtnSyncNotion.IsEnabled = enabled;
            BtnResetNotion.IsEnabled = enabled;
        }

        private async Task SaveCurrentIndexIfPossibleAsync(List<SearchResultRow> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0)
            {
                await LocalIndexPersistence.ClearAsync();
                return;
            }

            var root = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;

            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                await LocalIndexPersistence.SaveAsync(root, items, ct);
                ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] =
                    DateTimeOffset.UtcNow.ToString("O");
            }
        }

        private static string FormatUtcLocal(string utcText)
        {
            if (DateTimeOffset.TryParse(utcText, out var dto))
                return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

            return utcText;
        }
        // ─────────────────────────────────────────────────────────
        // API KEYS
        // ─────────────────────────────────────────────────────────

        /// Navega a la vista de administración de API Keys.
        private void BtnApiKeys_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Anfeta.UI.Views.ApiKeysView));
        }

        // ─────────────────────────────────────────────────────────
        // STATUS BAR
        // ─────────────────────────────────────────────────────────

        /// Muestra un mensaje en la InfoBar inferior y lo cierra automáticamente a los 3 segundos.
        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            InfoStatus.Message = message;
            InfoStatus.Severity = severity;
            InfoStatus.IsOpen = true;

            _statusTimer.Stop();
            _statusTimer.Start();
        }
    }
}