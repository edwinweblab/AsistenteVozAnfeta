using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;
using Windows.Globalization;

namespace Anfeta.UI.Services
{
    public sealed class SpeechToTextService : ISpeechToTextService, IDisposable
    {
        private SpeechRecognizer? _recognizer;
        private string _currentLanguage = "";

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
            _recognizer?.Dispose();
            _recognizer = null;

            var available = SpeechRecognizer.SupportedTopicLanguages;
            if (available.Count == 0)
                throw new InvalidOperationException("No hay idiomas instalados.");

            Language? targetLang = available.FirstOrDefault(l =>
                l.LanguageTag.Equals(languageTag, StringComparison.OrdinalIgnoreCase));

            if (targetLang == null)
                targetLang = available.First();

            _currentLanguage = targetLang.LanguageTag;
            _recognizer = new SpeechRecognizer(targetLang);

            _recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(10);
            _recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(2);

            try
            {
                await _recognizer.CompileConstraintsAsync();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                if (ex.HResult == unchecked((int)0x80045509))
                {
                    throw new UnauthorizedAccessException(
                        "Debes activar 'Reconocimiento de voz' en Configuración de Windows → Privacidad → Voz"
                    );
                }
                throw;
            }
        }

        public async Task<string?> RecognizeOnceAsync(CancellationToken ct = default)
        {
            if (_recognizer == null)
                throw new InvalidOperationException("Servicio no inicializado.");

            try
            {
                var result = await _recognizer.RecognizeAsync();
                return result.Status == SpeechRecognitionResultStatus.Success ? result.Text : null;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                if (ex.HResult == unchecked((int)0x80045509))
                {
                    throw new UnauthorizedAccessException("Debes aceptar la política de privacidad de voz en Windows.");
                }
                return null;
            }
        }

        public void Dispose()
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    public class LanguageInfo
    {
        public string Tag { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string NativeName { get; set; } = "";
    }
}