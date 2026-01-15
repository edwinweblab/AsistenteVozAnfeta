using System;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public sealed class SpeechToTextService : ISpeechToTextService
    {
        private SpeechRecognitionEngine? _engine;

        public Task InitializeAsync(string languageTag = "es-ES")
        {
            if (_engine != null) return Task.CompletedTask;

            // 1) Ver qué recognizers hay instalados
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed == null || installed.Count == 0)
                throw new InvalidOperationException("No hay motores de reconocimiento instalados en Windows (Speech Recognition).");

            // 2) Elegir uno que coincida con el idioma (si existe)
            var chosen = installed
                .FirstOrDefault(r => r.Culture.Name.Equals(languageTag, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == languageTag.Substring(0, 2))
                ?? installed[0];

            _engine = new SpeechRecognitionEngine(chosen);

            // 3) Micrófono default
            _engine.SetInputToDefaultAudioDevice();

            // 4) Gramática (dictation)
            _engine.LoadGrammar(new DictationGrammar());

            return Task.CompletedTask;
        }

        public Task<string?> RecognizeOnceAsync(CancellationToken ct = default)
        {
            if (_engine == null)
                throw new InvalidOperationException("SpeechToTextService no inicializado. Llama InitializeAsync primero.");

            var tcs = new TaskCompletionSource<string?>();

            void completed(object? s, RecognizeCompletedEventArgs e)
            {
                _engine!.RecognizeCompleted -= completed;

                if (e.Cancelled) { tcs.TrySetResult("[CANCELADO]"); return; }
                if (e.Error != null) { tcs.TrySetException(e.Error); return; }

                var text = e.Result?.Text;
                tcs.TrySetResult(text);
            }

            _engine.RecognizeCompleted += completed;

            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    try
                    {
                        _engine.RecognizeAsyncCancel();
                    }
                    catch { }
                });
            }

            _engine.RecognizeAsync(RecognizeMode.Single);

            return tcs.Task;
        }
    }
}
