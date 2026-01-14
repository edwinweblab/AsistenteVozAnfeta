using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Storage;

namespace App1.Views
{
    public sealed partial class SettingsView : Page
    {
        private ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

        public SettingsView()
        {
            this.InitializeComponent();
            LoadSettings();
        }

        // Cargar configuración guardada
        private void LoadSettings()
        {
            // Reconocimiento de voz
            if (localSettings.Values.ContainsKey("WakeWord"))
                TxtWakeWord.Text = localSettings.Values["WakeWord"].ToString();
            else
                TxtWakeWord.Text = "Anfeta";

            ToggleAlwaysListen.IsOn = GetSetting("AlwaysListen", false);

            // Confirmación
            ToggleAlwaysConfirm.IsOn = GetSetting("AlwaysConfirm", true);
            ToggleVoiceConfirm.IsOn = GetSetting("VoiceConfirm", false);
            SliderConfirmTimeout.Value = GetSetting("ConfirmTimeout", 15.0);

            // APIs
            if (localSettings.Values.ContainsKey("WeblabUrl"))
                TxtWeblabUrl.Text = localSettings.Values["WeblabUrl"].ToString();
            else
                TxtWeblabUrl.Text = "https://wlserver-production.up.railway.app";

            if (localSettings.Values.ContainsKey("DeviceId"))
                TxtDeviceId.Text = localSettings.Values["DeviceId"].ToString();
            else
                GenerateDeviceId();

            // Notificaciones
            ToggleNotifications.IsOn = GetSetting("Notifications", true);
            ToggleSoundNotifications.IsOn = GetSetting("SoundNotifications", true);

            // Preferencias generales
            ToggleStartup.IsOn = GetSetting("Startup", false);
            ToggleMinimizeTray.IsOn = GetSetting("MinimizeTray", false);
        }

        // Guardar configuración
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Reconocimiento de voz
            localSettings.Values["WakeWord"] = TxtWakeWord.Text;
            localSettings.Values["AlwaysListen"] = ToggleAlwaysListen.IsOn;

            // Confirmación
            localSettings.Values["AlwaysConfirm"] = ToggleAlwaysConfirm.IsOn;
            localSettings.Values["VoiceConfirm"] = ToggleVoiceConfirm.IsOn;
            localSettings.Values["ConfirmTimeout"] = SliderConfirmTimeout.Value;

            // APIs
            localSettings.Values["WeblabUrl"] = TxtWeblabUrl.Text;
            localSettings.Values["DeviceId"] = TxtDeviceId.Text;

            // Notificaciones
            localSettings.Values["Notifications"] = ToggleNotifications.IsOn;
            localSettings.Values["SoundNotifications"] = ToggleSoundNotifications.IsOn;

            // Preferencias generales
            localSettings.Values["Startup"] = ToggleStartup.IsOn;
            localSettings.Values["MinimizeTray"] = ToggleMinimizeTray.IsOn;

            ShowInfoBar("Configuración guardada correctamente", InfoBarSeverity.Success);
        }

        // Restaurar valores predeterminados
        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            localSettings.Values.Clear();
            LoadSettings();
            ShowInfoBar("Configuración restaurada a valores predeterminados", InfoBarSeverity.Informational);
        }

        // Generar Device ID
        private void BtnGenerateDeviceId_Click(object sender, RoutedEventArgs e)
        {
            GenerateDeviceId();
        }

        private void GenerateDeviceId()
        {
            TxtDeviceId.Text = $"ANFETA_{Guid.NewGuid().ToString("N").Substring(0, 12).ToUpper()}";
        }

        // Probar conexión Weblab
        private async void BtnTestWeblab_Click(object sender, RoutedEventArgs e)
        {
            WeblabStatus.Visibility = Visibility.Visible;
            TxtWeblabStatus.Text = "Probando conexión...";

            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync($"{TxtWeblabUrl.Text}/api/opciones");

                if (response.IsSuccessStatusCode)
                {
                    TxtWeblabStatus.Text = $"✓ Conexión exitosa ({response.StatusCode})";
                    TxtWeblabStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.Colors.Green);
                }
                else
                {
                    TxtWeblabStatus.Text = $"✗ Error de conexión ({response.StatusCode})";
                    TxtWeblabStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.Colors.Orange);
                }
            }
            catch (Exception ex)
            {
                TxtWeblabStatus.Text = $"✗ Error: {ex.Message}";
                TxtWeblabStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Red);
            }
        }

        // Helpers
        private bool GetSetting(string key, bool defaultValue)
        {
            return localSettings.Values.ContainsKey(key)
                ? (bool)localSettings.Values[key]
                : defaultValue;
        }

        private double GetSetting(string key, double defaultValue)
        {
            return localSettings.Values.ContainsKey(key)
                ? Convert.ToDouble(localSettings.Values[key])
                : defaultValue;
        }

        private void ShowInfoBar(string message, InfoBarSeverity severity)
        {
            var infoBar = new InfoBar
            {
                Message = message,
                Severity = severity,
                IsOpen = true
            };

            // Agregar temporalmente al layout (necesitas un contenedor en el XAML)
            // Por simplicidad, podrías usar un ContentDialog
            var dialog = new ContentDialog
            {
                Title = severity == InfoBarSeverity.Success ? "Éxito" : "Información",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            _ = dialog.ShowAsync();
        }
    }
}