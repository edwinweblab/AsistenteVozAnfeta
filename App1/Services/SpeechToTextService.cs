using System;
using System.Collections.Generic;
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

        public SpeechToTextService(AppStateService appState)
        {
            _appState = appState;
        }

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

        public async Task InitializeAsync(string languageTag = "es-MX")
        {
            await _lock.WaitAsync();
            try
            {
                var available = SpeechRecognizer.SupportedTopicLanguages;
                if (available.Count == 0)
                    throw new InvalidOperationException("No hay idiomas instalados.");

                Language? targetLang = available.FirstOrDefault(l =>
                    l.LanguageTag.Equals(languageTag, StringComparison.OrdinalIgnoreCase));

                targetLang ??= available.First();
                _currentLanguage = targetLang.LanguageTag;

                using var probe = new SpeechRecognizer(targetLang);
                probe.Constraints.Add(new SpeechRecognitionTopicConstraint(
                    SpeechRecognitionScenario.Dictation, "dictation"));

                probe.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(3.5);
                probe.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.2);
                probe.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(0);

                try
                {
                    var compile = await probe.CompileConstraintsAsync();
                    if (compile.Status != SpeechRecognitionResultStatus.Success)
                        throw new InvalidOperationException($"CompileConstraintsAsync falló: {compile.Status}");
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    if (ex.HResult == unchecked((int)0x80045509))
                        throw new UnauthorizedAccessException("Activa permisos de voz en Windows.");
                    throw;
                }

                _initialized = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<string?> RecognizeOnceAsync(CancellationToken ct = default)
        {
            if (!_initialized)
                throw new InvalidOperationException("Llama InitializeAsync() primero.");

            await _lock.WaitAsync(ct);
            try
            {
                var available = SpeechRecognizer.SupportedTopicLanguages;
                if (available.Count == 0) return null;

                Language? lang = available.FirstOrDefault(l =>
                    l.LanguageTag.Equals(_currentLanguage, StringComparison.OrdinalIgnoreCase));
                lang ??= available.First();

                var recognizer = new SpeechRecognizer(lang);
                recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(
                    SpeechRecognitionScenario.Dictation, "dictation"));

                recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(3.5);
                recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.2);
                recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(0);

                // NUEVO: Usar device seleccionado si existe
                if (_appState.InputDeviceId.HasValue)
                {
                    var deviceId = _appState.InputDeviceId.Value;
                    System.Diagnostics.Debug.WriteLine($"[STT] Usando micrófono ID: {deviceId}");
                    // Windows.Media.SpeechRecognition NO permite cambiar device directamente
                    // El device seleccionado en SettingsView se usa en NAudio para tests
                    // Para STT, Windows usa el default del sistema
                }

                _activeRecognizer = recognizer;

                try
                {
                    var compile = await recognizer.CompileConstraintsAsync();
                    if (compile.Status != SpeechRecognitionResultStatus.Success)
                        return null;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    if (ex.HResult == unchecked((int)0x80045509))
                        throw new UnauthorizedAccessException("Acepta la política de voz en Windows.");
                    return null;
                }

                using var reg = ct.Register(() => _ = CancelAsync());

                SpeechRecognitionResult result;
                try
                {
                    result = await recognizer.RecognizeAsync();
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    if (ex.HResult == unchecked((int)0x80045509))
                        throw new UnauthorizedAccessException("Acepta la política de voz en Windows.");
                    return null;
                }
                finally
                {
                    try { recognizer.Dispose(); } catch { }
                    if (ReferenceEquals(_activeRecognizer, recognizer))
                        _activeRecognizer = null;
                }

                if (ct.IsCancellationRequested) return null;

                if (result?.Status == SpeechRecognitionResultStatus.Success)
                    return result.Text;

                return null;
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

            try { await r.StopRecognitionAsync(); }
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