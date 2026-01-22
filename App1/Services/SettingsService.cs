using Windows.Storage;
namespace Anfeta.UI.Services
{
    public class SettingsService
    {
        private readonly ApplicationDataContainer _settings;
        private readonly AppStateService _appState;

        public SettingsService(AppStateService appState)
        {
            _settings = ApplicationData.Current.LocalSettings;
            _appState = appState;
            LoadSettings();
        }

        /// <summary>Carga configuración guardada al iniciar</summary>
        private void LoadSettings()
        {
            if (_settings.Values.ContainsKey("InputDeviceId"))
                _appState.InputDeviceId = (int)_settings.Values["InputDeviceId"];

            if (_settings.Values.ContainsKey("InputDeviceName"))
                _appState.InputDeviceName = (string)_settings.Values["InputDeviceName"];

            if (_settings.Values.ContainsKey("OutputDeviceId"))
                _appState.OutputDeviceId = (int)_settings.Values["OutputDeviceId"];

            if (_settings.Values.ContainsKey("OutputDeviceName"))
                _appState.OutputDeviceName = (string)_settings.Values["OutputDeviceName"];

            if (_settings.Values.ContainsKey("HotkeyModifiers"))
                _appState.HotkeyModifiers = (uint)(int)_settings.Values["HotkeyModifiers"];

            if (_settings.Values.ContainsKey("HotkeyKey"))
                _appState.HotkeyKey = (uint)(int)_settings.Values["HotkeyKey"];
        }

        /// <summary>Guarda dispositivo de entrada y su nombre</summary>
        public void SaveInputDevice(int deviceId, string deviceName)
        {
            _appState.InputDeviceId = deviceId;
            _appState.InputDeviceName = deviceName;
            _settings.Values["InputDeviceId"] = deviceId;
            _settings.Values["InputDeviceName"] = deviceName;
        }

        /// <summary>Guarda dispositivo de salida y su nombre</summary>
        public void SaveOutputDevice(int deviceId, string deviceName)
        {
            _appState.OutputDeviceId = deviceId;
            _appState.OutputDeviceName = deviceName;
            _settings.Values["OutputDeviceId"] = deviceId;
            _settings.Values["OutputDeviceName"] = deviceName;
        }

        /// <summary>Guarda combinación de teclas</summary>
        public void SaveHotkey(uint modifiers, uint key)
        {
            _appState.HotkeyModifiers = modifiers;
            _appState.HotkeyKey = key;
            _settings.Values["HotkeyModifiers"] = (int)modifiers;
            _settings.Values["HotkeyKey"] = (int)key;
        }
    }
}