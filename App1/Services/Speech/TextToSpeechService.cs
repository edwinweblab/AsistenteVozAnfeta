using NAudio.CoreAudioApi;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Anfeta.UI.Services.Speech
{
    public sealed class TextToSpeechService : ITextToSpeechService
    {
        private readonly AppStateService _appState;
        private readonly SpeechSynthesizer _synth = new();
        private MediaPlayer? _player;
        private string? _originalDefaultDevice;

        public TextToSpeechService(AppStateService appState)
        {
            _appState = appState;
        }

        /// <summary>Cambia temporalmente el dispositivo de salida predeterminado</summary>
        private void SetTemporaryDefaultDevice(int naudioId)
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();

                // Guardar dispositivo predeterminado actual
                var currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _originalDefaultDevice = currentDefault.ID;

                // Obtener dispositivo objetivo
                var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

                // NAudio usa -1 como default, ajustar índice
                int adjustedId = naudioId == -1 ? 0 : naudioId;

                if (adjustedId >= 0 && adjustedId < renderDevices.Count)
                {
                    var target = renderDevices[adjustedId];
                    System.Diagnostics.Debug.WriteLine($"[TTS] Cambiando a device: {target.FriendlyName}");

                    var policyConfig = new PolicyConfigClient();
                    policyConfig.SetDefaultEndpoint(target.ID, 1); // 1 = Multimedia
                }
            }
            catch (COMException)
            {
                // Silenciar error COM - usar dispositivo predeterminado del sistema
                _originalDefaultDevice = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] Error al cambiar device: {ex.Message}");
                _originalDefaultDevice = null;
            }
        }

        /// <summary>Restaura el dispositivo predeterminado original</summary>
        private void RestoreDefaultDevice()
        {
            if (_originalDefaultDevice != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[TTS] Restaurando device original");
                    var policyConfig = new PolicyConfigClient();
                    policyConfig.SetDefaultEndpoint(_originalDefaultDevice, 1);
                }
                catch (COMException)
                {
                    // Silenciar error COM
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TTS] Error al restaurar device: {ex.Message}");
                }
                finally
                {
                    _originalDefaultDevice = null;
                }
            }
        }

        public async Task SpeakAsync(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            await StopAsync();
            ct.ThrowIfCancellationRequested();

            if (_appState.OutputDeviceId.HasValue)
            {
                SetTemporaryDefaultDevice(_appState.OutputDeviceId.Value);
            }

            try
            {
                var stream = await _synth.SynthesizeTextToStreamAsync(text);
                ct.ThrowIfCancellationRequested();

                _player = new MediaPlayer();
                _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
                _player.MediaEnded += (s, e) => RestoreDefaultDevice();
                _player.Play();
            }
            catch (OperationCanceledException)
            {
                RestoreDefaultDevice();
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] Error en SpeakAsync: {ex.Message}");
                RestoreDefaultDevice();
            }
        }

        public Task StopAsync()
        {
            Stop();
            return Task.CompletedTask;
        }

        public void Stop()
        {
            try
            {
                if (_player != null)
                {
                    _player.Pause();
                    _player.Source = null;
                    _player.Dispose();
                    _player = null;
                    System.Diagnostics.Debug.WriteLine("[TTS] Detenido y liberado");
                }
                RestoreDefaultDevice();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] Error al detener: {ex.Message}");
            }
        }
    }
}