using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Speech
{
    public class AudioService : IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator;
        private WaveInEvent? _waveIn;
        private WaveOutEvent? _waveOut;

        public AudioService()
        {
            _enumerator = new MMDeviceEnumerator();
        }

        /// <summary>Obtiene el nombre amigable del dispositivo predeterminado de Windows.</summary>
        public static string GetSystemDefaultDeviceName(DataFlow flow)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                return device?.FriendlyName ?? "No disponible";
            }
            catch
            {
                return "No disponible";
            }
        }

        // Obtiene la lista de dispositivos de entrada de audio disponibles
        // Retorna lista de AudioDeviceInfo con index, id, nombre y si es default
        public List<Models.AudioDeviceInfo> GetInputDevices()
        {
            var devices = new List<Models.AudioDeviceInfo>();
            MMDevice? defaultDevice = null;

            try
            {
                defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }
            catch
            {
                // No hay dispositivo de entrada por defecto
            }

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var cap = WaveIn.GetCapabilities(i);
                var coreDevice = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Contains(cap.ProductName));

                string deviceId = coreDevice?.ID ?? Guid.NewGuid().ToString();
                string uniqueId = DeviceIdManager.GetOrCreateId(deviceId, "INPUT");

                bool isDefault = defaultDevice != null &&
                                 coreDevice != null &&
                                 coreDevice.ID == defaultDevice.ID;

                devices.Add(new Models.AudioDeviceInfo(
                    i,
                    deviceId,
                    uniqueId,
                    cap.ProductName,
                    isDefault
                ));
            }

            return devices;
        }

        // Obtiene la lista de dispositivos de salida de audio disponibles
        // Retorna lista de AudioDeviceInfo con index, id, nombre y si es default
        public List<Models.AudioDeviceInfo> GetOutputDevices()
        {
            var devices = new List<Models.AudioDeviceInfo>();
            MMDevice? defaultDevice = null;

            try
            {
                defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch
            {
                // No hay dispositivo de salida por defecto
            }

            for (int i = -1; i < WaveOut.DeviceCount; i++)
            {
                var cap = WaveOut.GetCapabilities(i);

                if (cap.ProductName.Contains("Asignador") ||
                    cap.ProductName.Contains("Microsoft Sound Mapper"))
                    continue;

                var coreDevice = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Contains(cap.ProductName));

                string deviceId = coreDevice?.ID ?? Guid.NewGuid().ToString();
                string uniqueId = DeviceIdManager.GetOrCreateId(deviceId, "OUTPUT");

                bool isDefault = defaultDevice != null &&
                                 coreDevice != null &&
                                 coreDevice.ID == defaultDevice.ID;

                devices.Add(new Models.AudioDeviceInfo(
                    i,
                    deviceId,
                    uniqueId,
                    cap.ProductName,
                    isDefault
                ));
            }

            return devices;
        }

        // Inicia la prueba del micrófono y envía niveles de audio al callback
        // deviceId: índice del dispositivo WaveIn
        // levelCallback: función que recibe el nivel de audio (0-100)
        public void StartMicTest(int deviceId, Action<float> levelCallback)
        {
            StopMicTest();

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceId,
                WaveFormat = new WaveFormat(16000, 1),
                BufferMilliseconds = 50
            };

            _waveIn.DataAvailable += (s, args) =>
            {
                float max = 0;
                for (int i = 0; i < args.BytesRecorded; i += 2)
                {
                    short sample = (short)(args.Buffer[i + 1] << 8 | args.Buffer[i]);
                    float sample32 = Math.Abs(sample / 32768f);
                    if (sample32 > max) max = sample32;
                }
                levelCallback?.Invoke(Math.Min(100, max * 100 * 10));
            };

            _waveIn.StartRecording();
        }

        // Detiene la prueba del micrófono y libera recursos
        public void StopMicTest()
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
        }

        public async Task PlayTestSound(int deviceId)
        {
            StopTestSound();

            var chime = new MultiToneProvider(
                new[] { 800f, 1000f },
                new[] { 0.4f, 0.4f },
                new[] { 150, 150 }
            );

            _waveOut = new WaveOutEvent { DeviceNumber = deviceId };
            _waveOut.Init(chime);
            _waveOut.Play();

            await Task.Delay(400);
            StopTestSound();
        }

        // Detiene el sonido de prueba y libera recursos
        public void StopTestSound()
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
        }

        public void Dispose()
        {
            StopMicTest();
            StopTestSound();
            _enumerator?.Dispose();
        }
    }

    internal class MultiToneProvider : WaveProvider32
    {
        private readonly float[] _frequencies;
        private readonly float[] _amplitudes;
        private readonly int[] _durations;
        private int _sample;
        private int _toneIndex;
        private int _toneSamples;

        public MultiToneProvider(float[] frequencies, float[] amplitudes, int[] durationMs)
        {
            _frequencies = frequencies;
            _amplitudes = amplitudes;
            _durations = durationMs;
            SetWaveFormat(16000, 1);
        }

        public override int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_toneIndex >= _frequencies.Length)
                {
                    buffer[offset + i] = 0;
                    continue;
                }

                var freq = _frequencies[_toneIndex];
                var amp = _amplitudes[_toneIndex];
                var duration = _durations[_toneIndex] * WaveFormat.SampleRate / 1000;

                buffer[offset + i] = (float)(amp * Math.Sin(2 * Math.PI * _sample * freq / WaveFormat.SampleRate));

                _sample++;
                _toneSamples++;

                if (_toneSamples >= duration)
                {
                    _toneIndex++;
                    _sample = 0;
                    _toneSamples = 0;
                }
            }

            return count;
        }
    }

    /// <summary>Gestiona IDs únicos persistentes para dispositivos</summary>
    public static class DeviceIdManager
    {
        private static readonly Windows.Storage.ApplicationDataContainer _settings =
            Windows.Storage.ApplicationData.Current.LocalSettings;

        public static string GetOrCreateId(string coreAudioId, string type)
        {
            string key = $"DeviceId_{type}_{coreAudioId}";

            if (_settings.Values.ContainsKey(key))
            {
                return (string)_settings.Values[key];
            }

            int nextId = GetNextId(type);
            string uniqueId = $"{type}_ID{nextId}";
            _settings.Values[key] = uniqueId;

            return uniqueId;
        }

        private static int GetNextId(string type)
        {
            string counterKey = $"DeviceCounter_{type}";
            int current = _settings.Values.ContainsKey(counterKey)
                ? (int)_settings.Values[counterKey]
                : 0;

            int next = current + 1;
            _settings.Values[counterKey] = next;
            return next;
        }
    }
}