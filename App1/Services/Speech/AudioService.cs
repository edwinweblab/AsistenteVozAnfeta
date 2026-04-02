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

        /// Nombre del dispositivo predeterminado del sistema.
        /// flow: Capture (mic) o Render (speakers).
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

        // Entrada: ninguna. Salida: lista de dispositivos de captura activos con su índice NAudio.
        public List<Models.AudioDeviceInfo> GetInputDevices()
        {
            var devices = new List<Models.AudioDeviceInfo>();
            MMDevice? defaultDevice = null;

            try
            {
                defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }
            catch { }

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var cap = WaveIn.GetCapabilities(i);
                var coreDevice = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Contains(cap.ProductName));

                string deviceId = coreDevice?.ID ?? Guid.NewGuid().ToString();
                string uniqueId = DeviceIdManager.GetOrCreateId(deviceId, "INPUT");
                bool isDefault = defaultDevice != null && coreDevice != null && coreDevice.ID == defaultDevice.ID;

                devices.Add(new Models.AudioDeviceInfo(i, deviceId, uniqueId, cap.ProductName, isDefault));
            }

            return devices;
        }

        // Entrada: ninguna. Salida: lista de dispositivos de salida activos con su índice NAudio.
        public List<Models.AudioDeviceInfo> GetOutputDevices()
        {
            var devices = new List<Models.AudioDeviceInfo>();
            MMDevice? defaultDevice = null;

            try
            {
                defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { }

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
                bool isDefault = defaultDevice != null && coreDevice != null && coreDevice.ID == defaultDevice.ID;

                devices.Add(new Models.AudioDeviceInfo(i, deviceId, uniqueId, cap.ProductName, isDefault));
            }

            return devices;
        }

        // Inicia prueba de micrófono. Envía nivel 0-100 al callback cada ~50ms.
        // Entrada: deviceId (índice WaveIn), levelCallback.
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

        // Detiene la prueba de micrófono y libera recursos.
        public void StopMicTest()
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
        }

        // Reproduce un tono suave de confirmación en el dispositivo indicado.
        // Entrada: deviceId (-1 = predeterminado del sistema).
        // El tono usa frecuencias graves con fade-in/out para no ser agresivo al oído.
        public async Task PlayTestSound(int deviceId)
        {
            StopTestSound();

            // 440 Hz (La4) y 520 Hz: zona de confort auditivo, no agresivas.
            // Amplitud 0.12f: suficiente para escuchar sin sobresaltar.
            // 90ms por tono con envelope de 20% fade-in + 60% sustain + 20% fade-out.
            var chime = new MultiToneProvider(
                frequencies: new[] { 440f, 520f },
                amplitudes: new[] { 0.12f, 0.10f },
                durationMs: new[] { 90, 90 }
            );

            _waveOut = new WaveOutEvent { DeviceNumber = deviceId };
            _waveOut.Init(chime);
            _waveOut.Play();

            // 280ms = tiempo total de audio + margen para flush del buffer.
            await Task.Delay(280);
            StopTestSound();
        }

        // Detiene el sonido de prueba y libera el WaveOutEvent.
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

    // Generador de tono puro con envelope trapezoidal (fade-in / sustain / fade-out).
    // Elimina el clic abrupto en ataque y release que hace percibir el sonido como agresivo.
    internal class MultiToneProvider : WaveProvider32
    {
        private readonly float[] _frequencies;
        private readonly float[] _amplitudes;
        private readonly int[] _durations;
        private int _sample;
        private int _toneIndex;
        private int _toneSamples;

        // fadeRatio: fracción del tono usada para fade-in y fade-out (0.0–0.5).
        private const float FadeRatio = 0.20f;

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

                int durationSamples = _durations[_toneIndex] * WaveFormat.SampleRate / 1000;
                float freq = _frequencies[_toneIndex];
                float amp = _amplitudes[_toneIndex];

                // Envelope trapezoidal: evita clicks en ataque y release.
                float progress = durationSamples > 0 ? (float)_toneSamples / durationSamples : 1f;
                float envelope;
                if (progress < FadeRatio)
                    envelope = progress / FadeRatio;               // fade-in
                else if (progress > 1f - FadeRatio)
                    envelope = (1f - progress) / FadeRatio;        // fade-out
                else
                    envelope = 1f;                                  // sustain

                envelope = Math.Clamp(envelope, 0f, 1f);

                buffer[offset + i] = envelope * amp *
                    (float)Math.Sin(2 * Math.PI * _sample * freq / WaveFormat.SampleRate);

                _sample++;
                _toneSamples++;

                if (_toneSamples >= durationSamples)
                {
                    _toneIndex++;
                    _sample = 0;
                    _toneSamples = 0;
                }
            }

            return count;
        }
    }

    /// Gestiona IDs únicos persistentes por dispositivo entre sesiones.
    public static class DeviceIdManager
    {
        private static readonly Windows.Storage.ApplicationDataContainer _settings =
            Windows.Storage.ApplicationData.Current.LocalSettings;

        public static string GetOrCreateId(string coreAudioId, string type)
        {
            string key = $"DeviceId_{type}_{coreAudioId}";

            if (_settings.Values.ContainsKey(key))
                return (string)_settings.Values[key];

            int nextId = GetNextId(type);
            string uid = $"{type}_ID{nextId}";
            _settings.Values[key] = uid;
            return uid;
        }

        private static int GetNextId(string type)
        {
            string counterKey = $"DeviceCounter_{type}";
            int current = _settings.Values.ContainsKey(counterKey)
                ? (int)_settings.Values[counterKey] : 0;
            int next = current + 1;
            _settings.Values[counterKey] = next;
            return next;
        }
    }
}