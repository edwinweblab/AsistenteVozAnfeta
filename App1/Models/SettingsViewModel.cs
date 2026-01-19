// ViewModels/SettingsViewModel.cs
using Anfeta.UI.Models;
using Anfeta.UI.Services;
using NAudio.CoreAudioApi;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.ViewModels
{
    /// <summary>Gestiona configuración de audio</summary>
    public class SettingsViewModel : IDisposable
    {
        private readonly AudioService _audioService;
        private readonly ApplicationDataContainer _settings;

        public ObservableCollection<AudioDeviceInfo> InputDevices { get; }
        public ObservableCollection<AudioDeviceInfo> OutputDevices { get; }

        private AudioDeviceInfo _selectedInputDevice;
        public AudioDeviceInfo SelectedInputDevice
        {
            get => _selectedInputDevice;
            set
            {
                _selectedInputDevice = value;
                if (value != null) UseSystemDefaultInput = false;
            }
        }

        private AudioDeviceInfo _selectedOutputDevice;
        public AudioDeviceInfo SelectedOutputDevice
        {
            get => _selectedOutputDevice;
            set
            {
                _selectedOutputDevice = value;
                if (value != null) UseSystemDefaultOutput = false;
            }
        }

        public bool UseSystemDefaultInput { get; set; }
        public bool UseSystemDefaultOutput { get; set; }

        public SettingsViewModel()
        {
            _audioService = new AudioService();
            _settings = ApplicationData.Current.LocalSettings;
            InputDevices = new ObservableCollection<AudioDeviceInfo>();
            OutputDevices = new ObservableCollection<AudioDeviceInfo>();
        }

        /// <summary>Carga dispositivos async para no bloquear UI</summary>
        public async Task LoadDevicesAsync()
        {
            await Task.Run(() =>
            {
                var inputs = _audioService.GetInputDevices();
                var outputs = _audioService.GetOutputDevices();

                // Volver a UI thread
                Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        InputDevices.Clear();
                        foreach (var device in inputs) InputDevices.Add(device);

                        OutputDevices.Clear();
                        foreach (var device in outputs) OutputDevices.Add(device);

                        LoadSavedSettings();
                    });
            });
        }

        private void LoadSavedSettings()
        {
            UseSystemDefaultInput = _settings.Values.ContainsKey("UseSystemDefaultInput")
                && (bool)_settings.Values["UseSystemDefaultInput"];

            UseSystemDefaultOutput = _settings.Values.ContainsKey("UseSystemDefaultOutput")
                && (bool)_settings.Values["UseSystemDefaultOutput"];

            if (!UseSystemDefaultInput && _settings.Values.ContainsKey("InputCoreAudioId"))
            {
                var id = _settings.Values["InputCoreAudioId"].ToString();
                SelectedInputDevice = InputDevices.FirstOrDefault(d => d.CoreAudioId == id);
            }

            if (!UseSystemDefaultOutput && _settings.Values.ContainsKey("OutputCoreAudioId"))
            {
                var id = _settings.Values["OutputCoreAudioId"].ToString();
                SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.CoreAudioId == id);
            }
        }

        public async Task TestMicrophoneAsync(Action<float> levelCallback)
        {
            if (SelectedInputDevice == null) return;

            _audioService.StartMicTest(SelectedInputDevice.NAudioId, levelCallback);
            await Task.Delay(5000);
            _audioService.StopMicTest();
        }

        public async Task TestSpeakerAsync()
        {
            if (SelectedOutputDevice == null) return;
            await _audioService.PlayTestSound(SelectedOutputDevice.NAudioId);
        }

        public void Dispose()
        {
            _audioService?.Dispose();
        }
    }
}