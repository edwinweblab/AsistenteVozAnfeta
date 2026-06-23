using Anfeta.UI.Services;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Storage;
using Windows.UI;
using WinRT.Interop;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
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
        private readonly DispatcherTimer _statusTimer;

        // Dropbox
        private CancellationTokenSource? _indexCts;
        private const string LS_NotionToken = "Notion.Token";
        private const string LS_NotionDataSourceId = "Notion.DataSourceId";
        private const string LS_NotionLastSyncUtc = "Notion.LastSyncUtc";
        public SettingsView()
        {
            InitializeComponent();

            _audioService = new AudioService();
            _appState = App.AppHost.Services.GetRequiredService<AppStateService>();
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
            LoadDropboxRootIntoUI();
            LoadNotionSettingsIntoUI();

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
        // NOTION
        // ─────────────────────────────────────────────────────────

        private void LoadNotionSettingsIntoUI()
        {
            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var dataSourceId = ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] as string;
            var lastSync = ApplicationData.Current.LocalSettings.Values[LS_NotionLastSyncUtc] as string;

            NotionTokenBox.Password = token ?? string.Empty;
            NotionDataSourceIdBox.Text = dataSourceId ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(dataSourceId))
            {
                NotionStatusText.Text = string.IsNullOrWhiteSpace(lastSync)
                    ? $"Configurado: {dataSourceId}"
                    : $"Configurado: {dataSourceId} · Última sincronización: {FormatUtcLocal(lastSync)}";
            }
            else
            {
                NotionStatusText.Text = "Notion no configurado.";
            }
        }

        private bool TryGetNotionInputs(out string token, out string dataSourceId)
        {
            token = (NotionTokenBox.Password ?? string.Empty).Trim();
            dataSourceId = (NotionDataSourceIdBox.Text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(token))
            {
                ShowStatus("Agrega el token de Notion.", InfoBarSeverity.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(dataSourceId))
            {
                ShowStatus("Agrega el Data Source ID de Notion.", InfoBarSeverity.Warning);
                return false;
            }

            return true;
        }

        private void BtnSaveNotion_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetNotionInputs(out var token, out var dataSourceId))
                return;

            ApplicationData.Current.LocalSettings.Values[LS_NotionToken] = token;
            ApplicationData.Current.LocalSettings.Values[LS_NotionDataSourceId] = dataSourceId;

            NotionStatusText.Text = $"Configurado: {dataSourceId}";
            ShowStatus("Configuración de Notion guardada.", InfoBarSeverity.Success);
        }

        private async void BtnTestNotion_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetNotionInputs(out var token, out var dataSourceId))
                return;

            SetNotionButtonsEnabled(false);

            try
            {
                ShowStatus("Probando conexión con Notion...", InfoBarSeverity.Informational);

                var items = await NotionIndexBuilder.BuildAsync(
                    token,
                    dataSourceId,
                    CancellationToken.None,
                    maxItems: 5);

                ShowStatus($"Conexión Notion correcta ✅ Páginas de prueba: {items.Count}", InfoBarSeverity.Success);
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

                var notionItems = await NotionIndexBuilder.BuildAsync(
                    token,
                    dataSourceId,
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

                NotionStatusText.Text = $"Configurado: {dataSourceId} · Última sincronización: {FormatUtcLocal(now)}";

                ShowStatus(
                    $"Notion sincronizado ✅ Páginas: {notionItems.Count} · Índice total: {App.LocalIndex.Count}",
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
                NotionDataSourceIdBox.Text = string.Empty;
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