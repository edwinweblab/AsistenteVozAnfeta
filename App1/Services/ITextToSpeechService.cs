using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public interface ITextToSpeechService
    {
        Task SpeakAsync(string text, CancellationToken ct = default);
        Task StopAsync();
        void Stop();
    }
}