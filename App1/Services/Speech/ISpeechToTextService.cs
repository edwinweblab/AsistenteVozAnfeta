using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Speech
{
    public interface ISpeechToTextService : IDisposable
    {
        Task InitializeAsync(string languageTag = "es-MX");
        Task<string?> RecognizeOnceAsync(CancellationToken ct = default);

        // NUEVO:
        Task CancelAsync();
        Task ResetAsync(string languageTag = "es-MX");

        List<LanguageInfo> GetAvailableLanguages();
        string GetCurrentLanguage();
    }
}