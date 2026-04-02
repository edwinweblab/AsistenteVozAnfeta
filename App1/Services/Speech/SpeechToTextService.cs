using Anfeta.UI.Services.Speech;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Anfeta.UI.Services
{
    public sealed partial class SpeechToTextService : ISpeechToTextService, IDisposable
    {
        private readonly AppStateService _appState;
        private string _currentLanguage = "es-MX";
        private bool _initialized;
        private SpeechRecognizer? _activeRecognizer;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private string? _originalDefaultDevice;

        public SpeechToTextService(AppStateService appState)
        {
            _appState = appState;
        }

        // Retorna idiomas instalados disponibles para reconocimiento de voz.
        public List<LanguageInfo> GetAvailableLanguages()
        {
            var languages = SpeechRecognizer.SupportedTopicLanguages;
            return languages.Select(l => new LanguageInfo
            {
                Tag = l.LanguageTag,
                DisplayName = l.DisplayName,
                NativeName = l.NativeName
            }).ToList();
        }

        public string GetCurrentLanguage() => _currentLanguage;

        // Verifica permisos, compila gramática y deja el recognizer en estado Idle.
        // Sin warm-up: StopRecognitionAsync deja el recognizer en estado transitorio (Stopping, no Idle)
        // y provoca COMException 0x80131509 en el siguiente RecognizeAsync.
        // Entrada: languageTag (default "es-MX"). Lanza excepción si falla.
        public async Task InitializeAsync(string languageTag = "es-MX")
        {
            await _lock.WaitAsync();
            try
            {
                if (_initialized &&
                    _activeRecognizer != null &&
                    _currentLanguage.Equals(languageTag, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_activeRecognizer != null)
                {
                    try { _activeRecognizer.Dispose(); } catch { }
                    _activeRecognizer = null;
                }

                var available = SpeechRecognizer.SupportedTopicLanguages;
                if (available.Count == 0)
                    throw new InvalidOperationException("No hay idiomas instalados.");

                Language? targetLang = available.FirstOrDefault(l =>
                    l.LanguageTag.Equals(languageTag, StringComparison.OrdinalIgnoreCase))
                    ?? available.First();

                _currentLanguage = targetLang.LanguageTag;

                var recognizer = new SpeechRecognizer(targetLang);
                recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(
                    SpeechRecognitionScenario.Dictation, "dictation"));

                // InitialSilenceTimeout: tiempo máximo sin audio antes de retornar null.
                // EndSilenceTimeout: silencio post-speech para dar la frase por terminada.
                // BabbleTimeout 0: nunca cancela por ruido de fondo sin habla reconocible.
                recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(6.0);
                recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.5);
                recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(0);

                try
                {
                    var compile = await recognizer.CompileConstraintsAsync();
                    if (compile.Status != SpeechRecognitionResultStatus.Success)
                        throw new InvalidOperationException($"CompileConstraintsAsync falló: {compile.Status}");
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    if (ex.HResult == unchecked((int)0x80045509))
                        throw new UnauthorizedAccessException("Activa permisos de voz en Windows.");
                    throw;
                }

                _activeRecognizer = recognizer;
                _initialized = true;
                Debug.WriteLine("[STT] Inicialización OK. Lang=" + _currentLanguage);
            }
            finally
            {
                _lock.Release();
            }
        }

        // Cambia el dispositivo de captura predeterminado del sistema temporalmente.
        // Entrada: naudioId (índice WaveIn). Persiste en _originalDefaultDevice para restaurar.
        private void SetTemporaryDefaultDevice(int naudioId)
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _originalDefaultDevice = currentDefault.ID;

                var captureDevices = enumerator
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .ToList();

                if (naudioId >= 0 && naudioId < captureDevices.Count)
                {
                    var target = captureDevices[naudioId];
                    new PolicyConfigClient().SetDefaultEndpoint(target.ID, 2);
                    Debug.WriteLine($"[STT] Dispositivo cambiado a: {target.FriendlyName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STT] Error al cambiar dispositivo: {ex.Message}");
            }
        }

        // Ejecuta un ciclo completo de reconocimiento de voz.
        // Cuando se cancela vía CancellationToken, StopRecognitionAsync se llama y espera
        // DENTRO del lock, garantizando que el recognizer esté en Idle antes de liberar.
        // Así el siguiente RecognizeAsync nunca encuentra el recognizer en estado Stopping.
        // onReady: invocado justo antes de RecognizeAsync — solo para actualizar UI.
        // Entrada: ct (cancelación), onReady (callback de UI).
        // Salida: texto reconocido, o null si no hubo voz / fue cancelado.
        public async Task<string?> RecognizeOnceAsync(CancellationToken ct = default, Action? onReady = null)
        {
            if (!_initialized || _activeRecognizer == null)
                throw new InvalidOperationException("Llama InitializeAsync() primero.");

            await _lock.WaitAsync(ct);
            try
            {
                if (_appState.InputDeviceId.HasValue)
                {
                    var devId = _appState.InputDeviceId.Value;
                    await Task.Run(() => SetTemporaryDefaultDevice(devId));
                    await Task.Delay(250, ct);
                }

                if (ct.IsCancellationRequested) return null;

                onReady?.Invoke();
                Debug.WriteLine("[STT] RecognizeAsync iniciando (micrófono activo).");

                SpeechRecognitionResult? result = null;

                try
                {
                    var recTask = _activeRecognizer.RecognizeAsync().AsTask();
                    var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    using var reg = ct.Register(() => cancelTcs.TrySetResult(true));

                    var completed = await Task.WhenAny(recTask, cancelTcs.Task);

                    if (completed == cancelTcs.Task)
                    {
                        // recTask sigue corriendo: el recognizer está en estado Running.
                        // StopRecognitionAsync lo transiciona Running → Stopping → Idle.
                        // Esperamos ambos DENTRO del lock para que el recognizer esté en Idle
                        // antes de liberar. Sin esto, el siguiente RecognizeAsync lanza 0x80131509.
                        Debug.WriteLine("[STT] CTS cancelado — deteniendo recognizer dentro del lock...");
                        try
                        {
                            var stopTask = _activeRecognizer.StopRecognitionAsync().AsTask();
                            await Task.WhenAll(
                                Task.WhenAny(recTask, Task.Delay(2000)),
                                Task.WhenAny(stopTask, Task.Delay(2000))
                            );
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[STT] Error durante stop en cancel: {ex.Message}");
                        }

                        // Invalidar para forzar re-init limpio — garantía extra ante
                        // cualquier estado interno que el recognizer no haya resuelto.
                        _initialized = false;
                        Debug.WriteLine("[STT] Recognizer detenido y estado invalidado tras cancel");
                        return null;
                    }

                    result = await recTask;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    _initialized = false;
                    try { _activeRecognizer?.Dispose(); } catch { }
                    _activeRecognizer = null;

                    if (ex.HResult == unchecked((int)0x80045509))
                        throw new UnauthorizedAccessException("Permiso de micrófono denegado.");

                    Debug.WriteLine($"[STT] COMException en RecognizeAsync: {ex.HResult:X8}");
                    throw;
                }

                if (ct.IsCancellationRequested) return null;

                var text = result?.Status == SpeechRecognitionResultStatus.Success
                    ? result.Text
                    : null;

                Debug.WriteLine($"[STT] Resultado: '{text ?? "<null>"}' | Status={result?.Status}");
                return text;
            }
            finally
            {
                var toRestore = _originalDefaultDevice;
                if (toRestore != null)
                {
                    _originalDefaultDevice = null;
                    _ = Task.Run(() =>
                    {
                        try { new PolicyConfigClient().SetDefaultEndpoint(toRestore, 2); }
                        catch (Exception ex) { Debug.WriteLine($"[STT] Error al restaurar dispositivo: {ex.Message}"); }
                    });
                }

                _lock.Release();
            }
        }

        // No-op: la cancelación se maneja internamente en RecognizeOnceAsync vía CancellationToken.
        // StopRecognitionAsync se llama y espera dentro del lock en RecognizeOnceAsync.
        // Mantener en la interfaz por compatibilidad.
        public Task CancelAsync() => Task.CompletedTask;

        // Destruye y recrea el recognizer. Usado para recuperarse de errores COM persistentes.
        public async Task ResetAsync(string languageTag = "es-MX")
        {
            await _lock.WaitAsync();
            try
            {
                _initialized = false;
                var r = _activeRecognizer;
                _activeRecognizer = null;

                if (r != null)
                {
                    try { await r.StopRecognitionAsync(); } catch { }
                    try { r.Dispose(); } catch { }
                }

                _currentLanguage = string.IsNullOrWhiteSpace(languageTag) ? "es-MX" : languageTag;
            }
            finally
            {
                _lock.Release();
            }

            await InitializeAsync(_currentLanguage);
        }

        public void Dispose()
        {
            _initialized = false;
            var r = _activeRecognizer;
            _activeRecognizer = null;
            try { r?.Dispose(); } catch { }
        }
    }

    public class LanguageInfo
    {
        public string Tag { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string NativeName { get; set; } = "";
    }
}