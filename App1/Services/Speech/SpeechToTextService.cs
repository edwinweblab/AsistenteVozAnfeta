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

        /// Retorna los idiomas disponibles para reconocimiento de voz.
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

        /// Verifica permisos y disponibilidad del idioma.
        /// Crea una instancia de SpeechRecognizer persistente para reutilizarla.
        public async Task InitializeAsync(string languageTag = "es-MX")
        {
            await _lock.WaitAsync();
            try
            {
                if (_initialized && _activeRecognizer != null && _currentLanguage.Equals(languageTag, StringComparison.OrdinalIgnoreCase))
                {
                    return; // Ya está inicializado con el mismo idioma
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

                recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(6.0);
                recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(2.0);
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
                Debug.WriteLine("[STT] Inicialización persistente OK. Lang=" + _currentLanguage);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// Cambia temporalmente el dispositivo de captura predeterminado del sistema.
        private void SetTemporaryDefaultDevice(int naudioId)
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _originalDefaultDevice = currentDefault.ID;

                var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                if (naudioId >= 0 && naudioId < captureDevices.Count)
                {
                    var target = captureDevices[naudioId];
                    Debug.WriteLine($"[STT] Cambiando a device: {target.FriendlyName}");
                    var policyConfig = new PolicyConfigClient();
                    policyConfig.SetDefaultEndpoint(target.ID, 2);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STT] Error al cambiar device: {ex.Message}");
            }
        }



        /// Ejecuta el reconocimiento.
        /// onReady: callback invocado justo antes de RecognizeAsync.
        /// Usar para actualizar la UI a "Escuchando" en el momento exacto en que
        /// el micrófono empieza a capturar — no antes.
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
                    
                    // Pequeña pausa para que Windows asiente el cambio de dispositivo predeterminado
                    await Task.Delay(500, ct); 
                }

                try
                {
                    if (ct.IsCancellationRequested) return null;

                    // El recognizer está a punto de capturar audio.
                    // Notificar al llamador para sincronizar el estado de la UI o emitir sonido.
                    onReady?.Invoke();
                    Debug.WriteLine("[STT] RecognizeAsync iniciando (micrófono activo).");

                    using var reg = ct.Register(() => _ = CancelAsync());

                    SpeechRecognitionResult? result = null;
                    try
                    {
                        var recTask = _activeRecognizer.RecognizeAsync().AsTask();
                        var cancelTcs = new TaskCompletionSource<bool>();
                        using var reg2 = ct.Register(() => cancelTcs.TrySetResult(true));
                        
                        var completed = await Task.WhenAny(recTask, cancelTcs.Task);
                        if (completed == cancelTcs.Task)
                        {
                            Debug.WriteLine("[STT] RecognizeAsync cancelado via CTS");
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
                            throw new UnauthorizedAccessException("El micrófono necesita estar en primer plano o aceptar la política de voz.");
                        
                        Debug.WriteLine($"[STT] COMException en RecognizeAsync: {ex.HResult:X8}");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STT] Exception en RecognizeAsync: {ex.Message}");
                        throw;
                    }

                    if (ct.IsCancellationRequested) return null;

                    var text = result?.Status == SpeechRecognitionResultStatus.Success
                        ? result.Text
                        : null;

                    Debug.WriteLine($"[STT] Resultado: '{text ?? "<null>"}' | Status={result?.Status}");
                    return text;
                }
                catch (Exception ex)
                {
                    if (ex is System.Runtime.InteropServices.COMException comEx && comEx.HResult == unchecked((int)0x80045509))
                    {
                        _initialized = false;
                        try { _activeRecognizer?.Dispose(); } catch { }
                        _activeRecognizer = null;
                        throw new UnauthorizedAccessException("El micrófono necesita estar en primer plano o aceptar la política de voz.");
                    }
                    
                    Debug.WriteLine($"[STT] Excepción general en RecognizeOnceAsync: {ex.Message}");
                    throw;
                }
                finally
                {
                    var toRestore = _originalDefaultDevice;
                    if (toRestore != null)
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                var policyConfig = new PolicyConfigClient();
                                policyConfig.SetDefaultEndpoint(toRestore, 2);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[STT] Error al restaurar device: {ex.Message}");
                            }
                        });
                        _originalDefaultDevice = null;
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task CancelAsync()
        {
            var r = _activeRecognizer;
            if (r == null) return;
            try 
            { 
                var stopTask = r.StopRecognitionAsync().AsTask();
                await Task.WhenAny(stopTask, Task.Delay(1000));
            } 
            catch { }
        }

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