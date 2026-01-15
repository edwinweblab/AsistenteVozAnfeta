using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Capture;
using Windows.Devices.Enumeration;
using Windows.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class SettingsView : Page
    {
        private ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        private MediaCapture? _mediaCapture;

        public SettingsView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await LoadAudioDevices();
            LoadSettings();
        }

        // Cargar dispositivos de audio
        private async Task LoadAudioDevices()
        {
            try
            {
                // Cargar micrófonos
                var inputDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
                var inputNames = inputDevices.Select(d => d.Name).ToList();
                CbInputDevice.ItemsSource = inputNames;

                var savedInput = _settings.Values["InputDevice"] as string;
                if (!string.IsNullOrEmpty(savedInput) && inputNames.Contains(savedInput))
                    CbInputDevice.SelectedItem = savedInput;
                else if (inputNames.Count > 0)
                    CbInputDevice.SelectedIndex = 0;

                // Cargar altavoces
                var outputDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
                var outputNames = outputDevices.Select(d => d.Name).ToList();
                CbOutputDevice.ItemsSource = outputNames;

                var savedOutput = _settings.Values["OutputDevice"] as string;
                if (!string.IsNullOrEmpty(savedOutput) && outputNames.Contains(savedOutput))
                    CbOutputDevice.SelectedItem = savedOutput;
                else if (outputNames.Count > 0)
                    CbOutputDevice.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                ShowMessage("Error", $"No se pudieron cargar los dispositivos: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            var language = _settings.Values["Language"] as string ?? "Español (México)";
            var langItem = CbLanguage.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Content?.ToString() == language);
            if (langItem != null) langItem.IsSelected = true;

            ToggleNotifications.IsOn = GetBoolSetting("Notifications", true);
            ToggleSoundNotifications.IsOn = GetBoolSetting("SoundNotifications", true);
            ToggleStartup.IsOn = GetBoolSetting("Startup", false);
            ToggleMinimizeTray.IsOn = GetBoolSetting("MinimizeTray", false);

            var theme = _settings.Values["Theme"] as string ?? "Sistema";
            var themeItem = CbTheme.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Content?.ToString() == theme);
            if (themeItem != null) themeItem.IsSelected = true;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _settings.Values["InputDevice"] = CbInputDevice.SelectedItem?.ToString() ?? "";
            _settings.Values["OutputDevice"] = CbOutputDevice.SelectedItem?.ToString() ?? "";
            _settings.Values["Language"] = (CbLanguage.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            _settings.Values["Notifications"] = ToggleNotifications.IsOn;
            _settings.Values["SoundNotifications"] = ToggleSoundNotifications.IsOn;
            _settings.Values["Startup"] = ToggleStartup.IsOn;
            _settings.Values["MinimizeTray"] = ToggleMinimizeTray.IsOn;
            _settings.Values["Theme"] = (CbTheme.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

            ConfigureStartup(ToggleStartup.IsOn);
            ShowMessage("Guardado", "Configuración guardada correctamente");
        }

        private async void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _settings.Values.Clear();
            await LoadAudioDevices();
            LoadSettings();
            ShowMessage("Restaurado", "Configuración restablecida");
        }

        private async void BtnTestMicrophone_Click(object sender, RoutedEventArgs e)
        {
            MicTestStatus.Visibility = Visibility.Visible;
            TxtMicTestStatus.Text = "Probando micrófono...";

            try
            {
                _mediaCapture?.Dispose();
                _mediaCapture = new MediaCapture();

                await _mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio
                });

                TxtMicTestStatus.Text = "✓ Micrófono funcionando correctamente";
                TxtMicTestStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Green);

                await Task.Delay(3000);
                MicTestStatus.Visibility = Visibility.Collapsed;
            }
            catch (UnauthorizedAccessException)
            {
                TxtMicTestStatus.Text = "✗ Permiso denegado. Ve a Configuración de Windows > Privacidad > Micrófono";
                TxtMicTestStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Red);
            }
            catch (Exception ex)
            {
                TxtMicTestStatus.Text = $"✗ Error: {ex.Message}";
                TxtMicTestStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Red);
            }
        }

        private void CbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var theme = (CbTheme.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = theme switch
                {
                    "Claro" => ElementTheme.Light,
                    "Oscuro" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }
        }

        private async void ConfigureStartup(bool enable)
        {
            try
            {
                var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync("AnfetaStartup");
                if (enable)
                    await startupTask.RequestEnableAsync();
                else
                    startupTask.Disable();
            }
            catch { }
        }

        private bool GetBoolSetting(string key, bool defaultValue)
        {
            return _settings.Values.ContainsKey(key)
                ? (bool)_settings.Values[key]
                : defaultValue;
        }

        private void ShowMessage(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            _ = dialog.ShowAsync();
        }
    }
}