using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Speech
{
    public interface ITextToSpeechService
    {
        Task SpeakAsync(string text, CancellationToken ct = default);
        Task StopAsync();
        void Stop();
    }
}