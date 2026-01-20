using Anfeta.UI.Dialogs;
using Anfeta.UI.Models;
using Anfeta.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Capture;

namespace Anfeta.UI.Views
{
    public sealed partial class SettingsView : Page
    {
        private readonly AudioService _audioService;
        private readonly SettingsService _settingsService;
        private readonly AppStateService _appState;
        private List<AudioDeviceInfo>? _inputDevices;
        private List<AudioDeviceInfo>? _outputDevices;
        private readonly DispatcherTimer _statusTimer;
        private bool _devicesLoaded;

        public SettingsView()
        {
            InitializeComponent();

            _audioService = App.AppHost.Services.GetRequiredService<AudioService>();
            _settingsService = App.AppHost.Services.GetRequiredService<SettingsService>();
            _appState = App.AppHost.Services.GetRequiredService<AppStateService>();

            // Bindear AppStateService al DataContext para mostrar InputDeviceName/OutputDeviceName
            DataContext = _appState;

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (s, e) => { InfoStatus.IsOpen = false; _statusTimer.Stop(); };

            // Actualizar display de hotkey
            TxtHotkeyDisplay.Text = _appState.GetHotkeyDisplayString();
            _appState.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppStateService.HotkeyModifiers) ||
                    e.PropertyName == nameof(AppStateService.HotkeyKey))
                {
                    DispatcherQueue.TryEnqueue(() => TxtHotkeyDisplay.Text = _appState.GetHotkeyDisplayString());
                }
            };

            // Lazy load: cargar devices cuando ComboBox reciba foco
            CbInputDevice.GotFocus += (s, e) => { if (!_devicesLoaded) _ = LoadDevicesAsync(); };
            CbOutputDevice.GotFocus += (s, e) => { if (!_devicesLoaded) _ = LoadDevicesAsync(); };

            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _audioService?.StopMicTest();
            _audioService?.StopTestSound();
        }

        // Lazy load devices
        private async Task LoadDevicesAsync()
        {
            if (_devicesLoaded) return;
            _devicesLoaded = true;

            await Task.Run(() =>
            {
                _inputDevices = _audioService.GetInputDevices();
                _outputDevices = _audioService.GetOutputDevices();

                DispatcherQueue.TryEnqueue(() =>
                {
                    CbInputDevice.Items.Clear();
                    foreach (var device in _inputDevices)
                        CbInputDevice.Items.Add(device.DeviceName);

                    CbOutputDevice.Items.Clear();
                    foreach (var device in _outputDevices)
                        CbOutputDevice.Items.Add(device.DeviceName);

                    // Restaurar selección desde AppStateService
                    if (_appState.InputDeviceId.HasValue)
                    {
                        int idx = _inputDevices.FindIndex(d => d.NAudioId == _appState.InputDeviceId.Value);
                        if (idx >= 0) CbInputDevice.SelectedIndex = idx;
                    }
                    else if (_inputDevices.Count > 0)
                    {
                        CbInputDevice.SelectedIndex = 0;
                    }

                    if (_appState.OutputDeviceId.HasValue)
                    {
                        int idx = _outputDevices.FindIndex(d => d.NAudioId == _appState.OutputDeviceId.Value);
                        if (idx >= 0) CbOutputDevice.SelectedIndex = idx;
                    }
                    else if (_outputDevices.Count > 0)
                    {
                        CbOutputDevice.SelectedIndex = 0;
                    }
                    TxtCurrentInput.Text = $"Entrada: {_appState.InputDeviceName}";
                    TxtCurrentOutput.Text = $"Salida: {_appState.OutputDeviceName}";
                });
            });
        }

        private async void CbInputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbInputDevice.SelectedIndex < 0 || _inputDevices == null) return;

            var device = _inputDevices[CbInputDevice.SelectedIndex];

            try
            {
                var settings = new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio };
                var capture = new MediaCapture();
                await capture.InitializeAsync(settings);
                capture.Dispose();

                _settingsService.SaveInputDevice(device.NAudioId);
                ShowStatus("Micrófono configurado", InfoBarSeverity.Success);
            }
            catch
            {
                ShowStatus("Permiso de micrófono denegado", InfoBarSeverity.Warning);
            }
        }

        private void CbOutputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbOutputDevice.SelectedIndex < 0 || _outputDevices == null) return;

            var device = _outputDevices[CbOutputDevice.SelectedIndex];
            _settingsService.SaveOutputDevice(device.NAudioId);
            ShowStatus("Altavoces configurados", InfoBarSeverity.Success);
        }

        private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
        {
            if (CbInputDevice.SelectedIndex < 0) return;
            var device = _inputDevices[CbInputDevice.SelectedIndex];

            try
            {
                PnlMicLevel.Visibility = Visibility.Visible;
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
                ShowStatus("Prueba completada", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", InfoBarSeverity.Error);
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
            if (CbOutputDevice.SelectedIndex < 0) return;
            var device = _outputDevices[CbOutputDevice.SelectedIndex];

            try
            {
                BtnTestSpeaker.IsEnabled = false;
                IconTestSpeaker.Glyph = "\uE769";
                TxtTestSpeaker.Text = "Reproduciendo...";

                await _audioService.PlayTestSound(device.NAudioId);
                ShowStatus("Sonido reproducido", InfoBarSeverity.Success);
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

        private async void BtnChangeHotkey_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new HotkeyPickerDialog(_appState) { XamlRoot = this.XamlRoot };
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                ShowStatus("Atajo actualizado", InfoBarSeverity.Success);
            }
        }

        private async void BtnOpenWindowsSettings_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));
        }

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