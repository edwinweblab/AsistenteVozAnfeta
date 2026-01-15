using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public interface ISpeechToTextService : IDisposable
    {
        Task InitializeAsync(string languageTag = "es-MX");
        Task<string?> RecognizeOnceAsync(CancellationToken ct = default);
        List<LanguageInfo> GetAvailableLanguages();
        string GetCurrentLanguage();
    }
}