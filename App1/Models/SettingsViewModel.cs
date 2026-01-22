// ViewModels/SettingsViewModel.cs
using Anfeta.UI.Models;
using Anfeta.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Anfeta.UI.ViewModels
{
    public class SettingsViewModel : IDisposable
    {
        private readonly AudioService _audioService;
        private readonly SettingsService _settingsService;
        private readonly AppStateService _appState;

        public ObservableCollection<AudioDeviceInfo> InputDevices { get; }
        public ObservableCollection<AudioDeviceInfo> OutputDevices { get; }

        public SettingsViewModel(AudioService audioService, SettingsService settingsService, AppStateService appState)
        {
            _audioService = audioService;
            _settingsService = settingsService;
            _appState = appState;

            InputDevices = new ObservableCollection<AudioDeviceInfo>();
            OutputDevices = new ObservableCollection<AudioDeviceInfo>();
        }

        public async Task LoadDevicesAsync()
        {
            await Task.Run(() =>
            {
                var inputs = _audioService.GetInputDevices();
                var outputs = _audioService.GetOutputDevices();

                App.UIQueue?.TryEnqueue(() =>
                {
                    InputDevices.Clear();
                    foreach (var device in inputs) InputDevices.Add(device);

                    OutputDevices.Clear();
                    foreach (var device in outputs) OutputDevices.Add(device);
                });
            });
        }

        public async Task TestMicAsync(int deviceId, Action<float> callback)
        {
            _audioService.StartMicTest(deviceId, callback);
            await Task.Delay(3000);
            _audioService.StopMicTest();
        }

        public async Task TestSpeakerAsync(int deviceId)
        {
            await _audioService.PlayTestSound(deviceId);
        }

        public void Dispose() => _audioService?.Dispose();
    }

}