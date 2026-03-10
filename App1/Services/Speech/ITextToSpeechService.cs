using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Speech
{
    public interface ITextToSpeechService
    {
        Task SpeakAsync(string text, CancellationToken ct = default);
        Task StopAsync();
        void Stop();

        // Pausa la reproducción actual. No libera el player.
        void Pause();

        // Reanuda desde donde se pausó.
        void Resume();

        // True si el player está actualmente pausado (no detenido).
        bool IsPaused { get; }

        // Rango válido: 0.5 (lento) – 6.0 (rápido). Default: 1.0
        void SetRate(double rate);
        double SpeakingRate { get; }
    }
}