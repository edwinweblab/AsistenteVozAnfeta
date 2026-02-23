using Anfeta.UI.Models;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Storage;
using Windows.UI;
using WinRT.Interop;

namespace Anfeta.UI.Views
{
    public sealed partial class SettingsView : Page
    {
        private readonly AudioService _audioService;
        private readonly SettingsService _settingsService;

        private List<AudioDeviceInfo> _inputDevices = new();
        private List<AudioDeviceInfo> _outputDevices = new();

        private readonly DispatcherTimer _statusTimer;

        // Dropbox
        private const string LS_DropboxRoot = "DropboxRoot";
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

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadCurrentHotkey();

            // Dropbox: para que no se vea vacío al volver
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

        // =========================
        // Hotkey
        // =========================
        private void LoadCurrentHotkey()
        {
            var appState = App.AppHost.Services.GetRequiredService<AppStateService>();
            TxtHotkeyDisplay.Text = appState.GetHotkeyDisplayString();
        }

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

        // =========================
        // Audio
        // =========================
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
                {
                    ShowStatus("Permiso de micrófono denegado. Habilítalo en Configuración de Windows.", InfoBarSeverity.Warning);
                });
            }
        }

        private async Task LoadDevicesAsync()
        {
            await Task.Run(() =>
            {
                _inputDevices = _audioService.GetInputDevices() ?? new List<AudioDeviceInfo>();
                _outputDevices = _audioService.GetOutputDevices() ?? new List<AudioDeviceInfo>();

                DispatcherQueue.TryEnqueue(() =>
                {
                    CbInputDevice.Items.Clear();
                    foreach (var device in _inputDevices)
                        CbInputDevice.Items.Add(device.DisplayName);

                    CbOutputDevice.Items.Clear();
                    foreach (var device in _outputDevices)
                        CbOutputDevice.Items.Add(device.DisplayName);

                    if (_inputDevices.Count > 0) CbInputDevice.SelectedIndex = 0;
                    if (_outputDevices.Count > 0) CbOutputDevice.SelectedIndex = 0;

                    UpdateCurrentDeviceLabels();
                });
            });
        }

        private void UpdateCurrentDeviceLabels()
        {
            if (CbInputDevice.SelectedIndex >= 0 && CbInputDevice.SelectedIndex < _inputDevices.Count)
            {
                var device = _inputDevices[CbInputDevice.SelectedIndex];
                TxtCurrentInput.Text = $"Entrada: {device.DeviceName}";
            }

            if (CbOutputDevice.SelectedIndex >= 0 && CbOutputDevice.SelectedIndex < _outputDevices.Count)
            {
                var device = _outputDevices[CbOutputDevice.SelectedIndex];
                TxtCurrentOutput.Text = $"Salida: {device.DeviceName}";
            }
        }

        private void CbInputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbInputDevice.SelectedIndex >= 0 && CbInputDevice.SelectedIndex < _inputDevices.Count)
            {
                var device = _inputDevices[CbInputDevice.SelectedIndex];
                _settingsService.SaveInputDevice(device.NAudioId, device.DeviceName);
                UpdateCurrentDeviceLabels();
                ShowStatus("Micrófono configurado", InfoBarSeverity.Success);
            }
        }

        private void CbOutputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbOutputDevice.SelectedIndex >= 0 && CbOutputDevice.SelectedIndex < _outputDevices.Count)
            {
                var device = _outputDevices[CbOutputDevice.SelectedIndex];
                _settingsService.SaveOutputDevice(device.NAudioId, device.DeviceName);
                UpdateCurrentDeviceLabels();
                ShowStatus("Altavoces configurados", InfoBarSeverity.Success);
            }
        }

        private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
        {
            if (CbInputDevice.SelectedIndex < 0 || CbInputDevice.SelectedIndex >= _inputDevices.Count)
            {
                ShowStatus("Selecciona un micrófono", InfoBarSeverity.Warning);
                return;
            }

            var device = _inputDevices[CbInputDevice.SelectedIndex];

            try
            {
                PnlMicLevel.Visibility = Visibility.Visible;
                PgMicLevel.ShowPaused = false;
                PgMicLevel.ShowError = false;

                BtnTestMic.IsEnabled = false;
                IconTestMic.Glyph = "\uE769";
                TxtTestMic.Text = "Escuchando...";

                _audioService.StartMicTest(device.NAudioId, (level) =>
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
                TxtTestMic.Text = "Probar Micrófono";
            }
        }

        private async void BtnTestSpeaker_Click(object sender, RoutedEventArgs e)
        {
            if (CbOutputDevice.SelectedIndex < 0 || CbOutputDevice.SelectedIndex >= _outputDevices.Count)
            {
                ShowStatus("Selecciona altavoces", InfoBarSeverity.Warning);
                return;
            }

            var device = _outputDevices[CbOutputDevice.SelectedIndex];

            try
            {
                BtnTestSpeaker.IsEnabled = false;
                IconTestSpeaker.Glyph = "\uE769";
                TxtTestSpeaker.Text = "Reproduciendo...";

                await _audioService.PlayTestSound(device.NAudioId);
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
                TxtTestSpeaker.Text = "Probar Sonido";
            }
        }

        private async void BtnOpenWindowsSettings_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));
        }

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
                "El reconocimiento de voz SIEMPRE usa el micrófono predeterminado de Windows, no el que seleccionas aquí.",
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

        private StackPanel CreateProblemSection(string icon, string title, string description, string[] steps)
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
                Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184))
            });

            var stepsList = new StackPanel { Spacing = 6, Margin = new Thickness(20, 4, 0, 0) };
            foreach (var step in steps)
            {
                stepsList.Children.Add(new TextBlock
                {
                    Text = step,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13
                });
            }

            section.Children.Add(stepsList);
            return section;
        }

        // =========================
        // Dropbox
        // =========================
        private void LoadDropboxRootIntoUI()
        {
            var saved = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                DropboxPathBox.Text = saved;
            else
                DropboxPathBox.Text = "";
        }

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

                // 4) Limpiar índice viejo ANTES (evita resultados viejos)
                App.LocalIndex.Clear();

                // ✅ limpia cache persistido anterior (evita mismatch si se cancela)
                await LocalIndexPersistence.ClearAsync();
                DropboxIndexCoordinator.StartIndexing(selectedPath);

                DropboxPathBox.Text = selectedPath;
                BtnPickDropboxRoot.IsEnabled = false;
                BtnResetDropboxRoot.IsEnabled = false;

                ShowStatus("Ruta nueva detectada, indexando...", InfoBarSeverity.Informational);

                try
                {
                    var list = await LocalIndexBuilder.BuildAsync(selectedPath, ct);
                    // 7) Guardar índice global
                    App.LocalIndex.Set(list);

                    // 7.1) ✅ Guardar índice persistido (para que sobreviva al cierre)
                    await LocalIndexPersistence.SaveAsync(selectedPath, list, ct);

                    // 8) Avisar "listo"
                    DropboxIndexCoordinator.MarkReady(selectedPath);
                    ShowStatus($"Índice listo ({App.LocalIndex.Count} items)", InfoBarSeverity.Success);
                }
                catch (OperationCanceledException)
                {
                    // No mostrar error si cambió de ruta rápido
                }
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

            // Limpiar índice global para NO mostrar resultados viejos
            App.LocalIndex.Clear();

            // ✅ borrar cache persistido
            await LocalIndexPersistence.ClearAsync();

            // Avisar a SearchView que el estado cambió (sin índice)
            DropboxIndexCoordinator.Reset();

            DropboxPathBox.Text = "";
            ShowStatus("Ruta reiniciada. Configura una nueva carpeta.", InfoBarSeverity.Informational);
        }

        // =========================
        // API Keys
        // =========================
        private void BtnApiKeys_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Anfeta.UI.Views.ApiKeysView));
        }

        // =========================
        // Status
        // =========================
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