using Windows.Storage;

namespace Anfeta.UI.Services
{
    /// <summary>Gestiona configuración persistente de la app</summary>
    public class SettingsService
    {
        private readonly ApplicationDataContainer _settings;

        // Configuración en memoria
        public int? InputDeviceId { get; private set; }
        public int? OutputDeviceId { get; private set; }

        public SettingsService()
        {
            _settings = ApplicationData.Current.LocalSettings;
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (_settings.Values.ContainsKey("InputDeviceId"))
                InputDeviceId = (int)_settings.Values["InputDeviceId"];

            if (_settings.Values.ContainsKey("OutputDeviceId"))
                OutputDeviceId = (int)_settings.Values["OutputDeviceId"];
        }

        public void SaveInputDevice(int deviceId)
        {
            InputDeviceId = deviceId;
            _settings.Values["InputDeviceId"] = deviceId;
        }

        public void SaveOutputDevice(int deviceId)
        {
            OutputDeviceId = deviceId;
            _settings.Values["OutputDeviceId"] = deviceId;
        }
    }
}