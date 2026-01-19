using Anfeta.UI.Models;
using Anfeta.UI.Services;
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
        private AudioService _audioService;
        private SettingsService _settingsService;
        private List<AudioDeviceInfo> _inputDevices;
        private List<AudioDeviceInfo> _outputDevices;
        private DispatcherTimer _statusTimer;

        public SettingsView()
        {
            InitializeComponent();
            _audioService = new AudioService();
            _settingsService = new SettingsService();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (s, e) => { InfoStatus.IsOpen = false; _statusTimer.Stop(); };

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await RequestMicPermissionAsync();
            await LoadDevicesAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _audioService?.StopMicTest();
            _audioService?.StopTestSound();
            _audioService?.Dispose();
        }

        /// <summary>Solicita permiso de micrófono</summary>
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
                ShowStatus("Permiso de micrófono denegado. Habilítalo en Configuración de Windows.", InfoBarSeverity.Warning);
            }
        }

        private async Task LoadDevicesAsync()
        {
            await Task.Run(() =>
            {
                _inputDevices = _audioService.GetInputDevices();
                _outputDevices = _audioService.GetOutputDevices();

                DispatcherQueue.TryEnqueue(() =>
                {
                    CbInputDevice.Items.Clear();
                    foreach (var device in _inputDevices)
                    {
                        CbInputDevice.Items.Add(device.DisplayName);
                    }

                    CbOutputDevice.Items.Clear();
                    foreach (var device in _outputDevices)
                    {
                        CbOutputDevice.Items.Add(device.DisplayName);
                    }

                    if (_settingsService.InputDeviceId.HasValue &&
                        _settingsService.InputDeviceId.Value < _inputDevices.Count)
                    {
                        CbInputDevice.SelectedIndex = _settingsService.InputDeviceId.Value;
                    }
                    else if (_inputDevices.Count > 0)
                    {
                        CbInputDevice.SelectedIndex = 0;
                    }

                    if (_settingsService.OutputDeviceId.HasValue &&
                        _settingsService.OutputDeviceId.Value < _outputDevices.Count)
                    {
                        CbOutputDevice.SelectedIndex = _settingsService.OutputDeviceId.Value;
                    }
                    else if (_outputDevices.Count > 0)
                    {
                        CbOutputDevice.SelectedIndex = 0;
                    }
                });
            });
        }

        private void CbInputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbInputDevice.SelectedIndex >= 0 && _inputDevices != null)
            {
                var device = _inputDevices[CbInputDevice.SelectedIndex];
                _settingsService.SaveInputDevice(device.NAudioId);
                System.Diagnostics.Debug.WriteLine($"{device.UniqueId} seleccionado");
                ShowStatus("Micrófono configurado", InfoBarSeverity.Success);
            }
        }

        private void CbOutputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbOutputDevice.SelectedIndex >= 0 && _outputDevices != null)
            {
                var device = _outputDevices[CbOutputDevice.SelectedIndex];
                _settingsService.SaveOutputDevice(device.NAudioId);
                System.Diagnostics.Debug.WriteLine($"{device.UniqueId} seleccionado");
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

        /// <summary>Muestra mensaje con auto-cierre en 3 segundos</summary>
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