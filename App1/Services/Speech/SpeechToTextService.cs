using Anfeta.UI.Services.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Anfeta.UI.Services
{
    public sealed partial class SpeechToTextService : ISpeechToTextService, ICommandSpeechToTextService
    {
        private readonly AppStateService _appState;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private SpeechRecognizer? _activeRecognizer;
        private string _currentLanguage = "es-MX";
        private bool _disposed;
        public SpeechToTextService(AppStateService appState) => _appState = appState;
        public string GetCurrentLanguage() => _currentLanguage;
        public List<LanguageInfo> GetAvailableLanguages() => SpeechRecognizer.SupportedTopicLanguages
            .Concat(SpeechRecognizer.SupportedGrammarLanguages).GroupBy(l => l.LanguageTag)
            .Select(g => new LanguageInfo { Tag = g.Key, DisplayName = g.First().DisplayName, NativeName = g.First().NativeName }).ToList();
        public Task InitializeAsync(string languageTag = "es-MX")
        { _currentLanguage = string.IsNullOrWhiteSpace(languageTag) ? "es-MX" : languageTag; return Task.CompletedTask; }
        private Language ResolveLanguage(bool local)
        {
            var languages = local ? SpeechRecognizer.SupportedGrammarLanguages : SpeechRecognizer.SupportedTopicLanguages;
            return languages.FirstOrDefault(l => l.LanguageTag.Equals(_currentLanguage, StringComparison.OrdinalIgnoreCase))
                ?? languages.FirstOrDefault(l => l.LanguageTag.Split('-')[0].Equals(_currentLanguage.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No está instalado el reconocimiento de {_currentLanguage}. En Windows → Hora e idioma → Idioma, instala Voz para español.");
        }
        public Task<string?> RecognizeOnceAsync(CancellationToken ct = default, Action? onReady = null) => RecognizeCoreAsync(null, ct, onReady);
        public Task<string?> RecognizeCommandsAsync(IReadOnlyList<string> commands, CancellationToken ct = default, Action? onReady = null) => RecognizeCoreAsync(commands, ct, onReady);
        private async Task<string?> RecognizeCoreAsync(IReadOnlyList<string>? commands, CancellationToken ct, Action? onReady)
        {
            await _lock.WaitAsync(ct);
            var restore = new List<(Role Role, string Original, string Temporary)>();
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SpeechToTextService));
                var language = ResolveLanguage(commands != null);
                ct.ThrowIfCancellationRequested();
                SelectConfiguredMicrophone(restore);
                using var recognizer = new SpeechRecognizer(language);
                _activeRecognizer = recognizer;
                if (commands != null)
                {
                    var phrases = commands.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
                    if (phrases.Length == 0) throw new InvalidOperationException("No hay comandos locales configurados.");
                    recognizer.Constraints.Add(new SpeechRecognitionListConstraint(phrases, "anfeta"));
                }
                else recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
                recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(6);
                recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.2);
                recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(12);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(40));
                try
                {
                    var compilation = await recognizer.CompileConstraintsAsync().AsTask(timeout.Token);
                    if (compilation.Status != SpeechRecognitionResultStatus.Success) throw new InvalidOperationException($"No se pudo preparar Voz: {compilation.Status}. Revisa el paquete de idioma de Windows.");
                    ct.ThrowIfCancellationRequested();
                    var recognition = recognizer.RecognizeAsync().AsTask(timeout.Token);
                    onReady?.Invoke();
                    var result = await recognition;
                    ct.ThrowIfCancellationRequested();
                    if (result.Status != SpeechRecognitionResultStatus.Success) throw new InvalidOperationException(DescribeStatus(result.Status.ToString(), commands != null));
                    if (commands != null && result.Confidence != SpeechRecognitionConfidence.High && result.Confidence != SpeechRecognitionConfidence.Medium)
                        throw new InvalidOperationException("El comando no fue claro. No se ejecutó ninguna acción; repítelo cerca del micrófono.");
                    return result.Text;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new TimeoutException("Voz tardó demasiado. Revisa el micrófono o usa Comandos locales en la flecha de Voz."); }
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80045509))
            { throw new UnauthorizedAccessException("Windows bloqueó el dictado en línea. Activa Reconocimiento de voz en línea o usa Comandos locales.", ex); }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070005))
            { throw new UnauthorizedAccessException("Windows no permite usar el micrófono. Activa el acceso para ANFETA en Privacidad.", ex); }
            finally { _activeRecognizer = null; RestoreMicrophone(restore); _lock.Release(); }
        }
        internal static string DescribeStatus(string status, bool local) => status switch
        {
            "InitialSilenceTimeout" => "No llegó voz al micrófono durante 6 segundos. Revisa la entrada y habla al aparecer ‘Habla ahora’.",
            "AudioCaptureUnavailable" or "MicrophoneUnavailable" => "El micrófono no está disponible: revisa permisos, conexión y otras aplicaciones.",
            "NetworkFailure" => "Falló el servicio de dictado en línea. Usa Comandos locales desde la flecha de Voz.",
            "TopicLanguageNotSupported" => "El idioma no admite dictado en línea. Instala Voz para español o usa Comandos locales.",
            _ => $"No se reconoció el audio ({status}). " + (local ? "Usa una de las frases del menú Voz." : "Prueba Comandos locales o revisa el reconocimiento en línea de Windows.")
        };
        // El selector guarda índices WaveIn, no CoreAudio: resolver por nombre.
        private void SelectConfiguredMicrophone(List<(Role Role, string Original, string Temporary)> restore)
        {
            if (!_appState.InputDeviceId.HasValue) return;
            var name = _appState.InputDeviceName;
            if (string.IsNullOrWhiteSpace(name) || name == "No configurado")
            {
                var id = _appState.InputDeviceId.Value;
                if (id < 0 || id >= WaveIn.DeviceCount) throw new InvalidOperationException("El micrófono guardado ya no existe. Selecciónalo nuevamente en Ajustes.");
                name = WaveIn.GetCapabilities(id).ProductName;
            }
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            try
            {
                var matches = devices.Where(d => d.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) matches = devices.Where(d => d.FriendlyName.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) throw new InvalidOperationException("No se identificó un único micrófono guardado. Selecciónalo nuevamente en Ajustes.");
                var target = matches[0];
                foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                {
                    using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                    if (current.ID == target.ID) continue;
                    restore.Add((role, current.ID, target.ID));
                    new PolicyConfigClient().SetDefaultEndpoint(target.ID, (int)role);
                }
            }
            finally { foreach (var device in devices) device.Dispose(); }
        }
        private static void RestoreMicrophone(List<(Role Role, string Original, string Temporary)> restore)
        {
            foreach (var item in restore)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, item.Role);
                    if (current.ID == item.Temporary) new PolicyConfigClient().SetDefaultEndpoint(item.Original, (int)item.Role);
                }
                catch { /* No pisar cambios manuales ni fallar si se desconectó el dispositivo. */ }
            }
        }
        public async Task CancelAsync() { var r = _activeRecognizer; if (r != null) { try { await r.StopRecognitionAsync(); } catch { } } }
        public async Task ResetAsync(string languageTag = "es-MX") { await CancelAsync(); await InitializeAsync(languageTag); }
        public void Dispose() { _disposed = true; _ = CancelAsync(); }
    }
    public class LanguageInfo
    {
        public string Tag { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string NativeName { get; set; } = "";
    }
}
