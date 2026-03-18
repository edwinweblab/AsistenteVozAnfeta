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
        private CancellationTokenSource? _ttsCts;

        // Velocidad actual. Rango: 0.5–6.0. Default: 1.0
        public double SpeakingRate { get; private set; } = 1.0;

        private bool _isPaused;
        public bool IsPaused => _isPaused;

        public TextToSpeechService(AppStateService appState)
        {
            _appState = appState;
        }

        // Cambia velocidad de reproducción en tiempo real si hay audio en curso.
        // Entrada: rate 0.5–6.0. Efecto: inmediato en _player.PlaybackRate.
        public void SetRate(double rate)
        {
            SpeakingRate = Math.Clamp(rate, 0.5, 6.0);

            // PlaybackRate afecta el player activo al instante, sin re-sintetizar.
            if (_player != null)
                _player.PlaybackRate = SpeakingRate;
        }

        private void SetTemporaryDefaultDevice(int naudioId)
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _originalDefaultDevice = currentDefault.ID;

                var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                int adjustedId = naudioId == -1 ? 0 : naudioId;

                if (adjustedId >= 0 && adjustedId < renderDevices.Count)
                {
                    var target = renderDevices[adjustedId];
                    System.Diagnostics.Debug.WriteLine($"[TTS] Cambiando a device: {target.FriendlyName}");
                    var policyConfig = new PolicyConfigClient();
                    policyConfig.SetDefaultEndpoint(target.ID, 1);
                }
            }
            catch (COMException)
            {
                _originalDefaultDevice = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] Error al cambiar device: {ex.Message}");
                _originalDefaultDevice = null;
            }
        }

        private void RestoreDefaultDevice()
        {
            if (_originalDefaultDevice == null) return;
            try
            {
                System.Diagnostics.Debug.WriteLine("[TTS] Restaurando device original");
                var policyConfig = new PolicyConfigClient();
                policyConfig.SetDefaultEndpoint(_originalDefaultDevice, 1);
            }
            catch (COMException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] Error al restaurar device: {ex.Message}");
            }
            finally
            {
                _originalDefaultDevice = null;
            }
        }

        public async Task SpeakAsync(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            await StopAsync();
            ct.ThrowIfCancellationRequested();

            _ttsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedCt = _ttsCts.Token;

            if (_appState.OutputDeviceId.HasValue)
                SetTemporaryDefaultDevice(_appState.OutputDeviceId.Value);

            try
            {
                var stream = await _synth.SynthesizeTextToStreamAsync(text).AsTask(linkedCt);
                linkedCt.ThrowIfCancellationRequested();

                _player = new MediaPlayer();
                _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
                _player.MediaEnded += (s, e) => RestoreDefaultDevice();

                // Aplica la velocidad almacenada antes de iniciar reproducción.
                _player.PlaybackRate = SpeakingRate;

                _isPaused = false;
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

        public void Pause()
        {
            if (_player == null || _isPaused) return;
            try
            {
                _player.Pause();
                _isPaused = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] Error al pausar: {ex.Message}");
            }
        }

        public void Resume()
        {
            if (_player == null || !_isPaused) return;
            try
            {
                _player.Play();
                _isPaused = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] Error al reanudar: {ex.Message}");
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
                _ttsCts?.Cancel();
                _ttsCts?.Dispose();
                _ttsCts = null;

                if (_player != null)
                {
                    _player.Pause();
                    _player.Source = null;
                    _player.Dispose();
                    _player = null;
                    _isPaused = false;
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