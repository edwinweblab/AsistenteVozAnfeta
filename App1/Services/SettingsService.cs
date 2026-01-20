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

        private void LoadSettings()
        {
            if (_settings.Values.ContainsKey("InputDeviceId"))
                _appState.InputDeviceId = (int)_settings.Values["InputDeviceId"];

            if (_settings.Values.ContainsKey("OutputDeviceId"))
                _appState.OutputDeviceId = (int)_settings.Values["OutputDeviceId"];

            if (_settings.Values.ContainsKey("HotkeyModifiers"))
                _appState.HotkeyModifiers = (uint)(int)_settings.Values["HotkeyModifiers"];

            if (_settings.Values.ContainsKey("HotkeyKey"))
                _appState.HotkeyKey = (uint)(int)_settings.Values["HotkeyKey"];
        }

        public void SaveInputDevice(int deviceId)
        {
            _appState.InputDeviceId = deviceId;
            _settings.Values["InputDeviceId"] = deviceId;
        }

        public void SaveOutputDevice(int deviceId)
        {
            _appState.OutputDeviceId = deviceId;
            _settings.Values["OutputDeviceId"] = deviceId;
        }

        public void SaveHotkey(uint modifiers, uint key)
        {
            _appState.HotkeyModifiers = modifiers;
            _appState.HotkeyKey = key;
            _settings.Values["HotkeyModifiers"] = (int)modifiers;
            _settings.Values["HotkeyKey"] = (int)key;
        }
    }
}