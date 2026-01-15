using System;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Anfeta.UI.Services
{
    public sealed class WindowsSpeechToTextService : ISpeechToTextService
    {
        private SpeechRecognizer? _recognizer;
        private SpeechContinuousRecognitionSession? _session;

        public event EventHandler<string>? PartialResult;
        public event EventHandler<string>? FinalResult;
        public event EventHandler<string>? Error;

        public bool IsListening { get; private set; }

        public async Task StartAsync(string languageTag = "es-MX")
        {
            if (IsListening) return;

            try
            {
                var lang = new Language(languageTag);
                _recognizer = new SpeechRecognizer(lang);

                _recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(
                    SpeechRecognitionScenario.Dictation, "dictation"));

                var compile = await _recognizer.CompileConstraintsAsync();
                if (compile.Status != SpeechRecognitionResultStatus.Success)
                {
                    Error?.Invoke(this, $"No se pudieron compilar constraints: {compile.Status}");
                    Cleanup();
                    return;
                }

                _recognizer.HypothesisGenerated += Recognizer_HypothesisGenerated;
                _recognizer.ContinuousRecognitionSession.ResultGenerated += Session_ResultGenerated;
                _recognizer.ContinuousRecognitionSession.Completed += Session_Completed;

                _session = _recognizer.ContinuousRecognitionSession;

                IsListening = true;
                await _session.StartAsync();
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex.Message);
                Cleanup();
            }
        }

        public async Task StopAsync()
        {
            if (!IsListening) return;

            try
            {
                if (_session != null)
                    await _session.StopAsync();
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex.Message);
            }
            finally
            {
                Cleanup();
            }
        }

        private void Recognizer_HypothesisGenerated(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
        {
            var text = args?.Hypothesis?.Text;
            if (!string.IsNullOrWhiteSpace(text))
                PartialResult?.Invoke(this, text);
        }

        private void Session_ResultGenerated(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
        {
            var result = args?.Result;
            if (result == null) return;

            var text = result.Text;
            if (!string.IsNullOrWhiteSpace(text))
                FinalResult?.Invoke(this, text);
        }

        private void Session_Completed(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
        {
            if (args.Status != SpeechRecognitionResultStatus.Success)
                Error?.Invoke(this, $"Sesión completada: {args.Status}");

            Cleanup();
        }

        private void Cleanup()
        {
            IsListening = false;

            if (_recognizer != null)
            {
                _recognizer.HypothesisGenerated -= Recognizer_HypothesisGenerated;

                if (_recognizer.ContinuousRecognitionSession != null)
                {
                    _recognizer.ContinuousRecognitionSession.ResultGenerated -= Session_ResultGenerated;
                    _recognizer.ContinuousRecognitionSession.Completed -= Session_Completed;
                }

                _recognizer.Dispose();
                _recognizer = null;
            }

            _session = null;
        }
    }
}
