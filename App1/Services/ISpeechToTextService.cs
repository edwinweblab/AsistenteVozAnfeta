using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public interface ISpeechToTextService
    {
        Task InitializeAsync(string languageTag = "es-MX");
        Task<string?> RecognizeOnceAsync(CancellationToken ct = default);
    }
}
