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
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class SettingsView : Page
    {
        private readonly AudioService _audioService;
        private readonly SettingsService _settingsService;
        private readonly DispatcherTimer _statusTimer;

        // Dropbox
        private CancellationTokenSource? _indexCts; 


        public SettingsView()
        {
            InitializeComponent();

            _audioService = new AudioService();
            _settingsService = new SettingsService(new AppStateService());

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
                    // Barra de estado superior
                    TxtStatusInput.Text = $"Entrada: {inputName}";
                    TxtStatusOutput.Text = $"Salida: {outputName}";

                    // Labels dentro de cada card
                    TxtMicDevice.Text = inputName;
                    TxtSpeakerDevice.Text = outputName;

                    // Sincroniza AppState → HomeView se actualiza automáticamente
                    var appState = App.AppHost.Services.GetRequiredService<AppStateService>();
                    appState.InputDeviceName = inputName;
                    appState.OutputDeviceName = outputName;
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
        /// Índice 0 = dispositivo predeterminado del sistema en NAudio.
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
        /// Índice 0 = dispositivo predeterminado del sistema en NAudio.
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
            var appState = App.AppHost.Services.GetRequiredService<AppStateService>();
            TxtHotkeyDisplay.Text = appState.GetHotkeyDisplayString();
        }

        /// Abre el diálogo para cambiar el atajo de teclado global.
        private async void BtnChangeHotkey_Click(object sender, RoutedEventArgs e)
        {
            var appState = App.AppHost.Services.GetRequiredService<AppStateService>();

            var dialog = new Anfeta.UI.Dialogs.HotkeyPickerDialog(appState, _settingsService)
            {
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                TxtHotkeyDisplay.Text = appState.GetHotkeyDisplayString();
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
                    var list = await LocalIndexBuilder.BuildAsync(selectedPath, ct);
                    App.LocalIndex.Set(list);
                    await LocalIndexPersistence.SaveAsync(selectedPath, list, ct);
                    ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] =
                    DateTimeOffset.UtcNow.ToString("O");
                    DropboxIndexCoordinator.MarkReady(selectedPath);
                    ShowStatus($"Índice listo ({App.LocalIndex.Count} items)", InfoBarSeverity.Success);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    DropboxIndexCoordinator.MarkError(selectedPath, ex.Message);
                    ShowStatus($"Error indexando → {ex.Message}", InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"Error eligiendo carpeta → {ex.Message}", InfoBarSeverity.Error);
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