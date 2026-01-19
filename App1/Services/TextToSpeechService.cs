using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Anfeta.UI.Services
{
    public sealed class TextToSpeechService : ITextToSpeechService
    {
        private readonly SpeechSynthesizer _synth = new();
        private MediaPlayer? _player;

        public async Task SpeakAsync(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            await StopAsync();

            ct.ThrowIfCancellationRequested();

            var stream = await _synth.SynthesizeTextToStreamAsync(text);

            ct.ThrowIfCancellationRequested();

            _player = new MediaPlayer();
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
        }

        public Task StopAsync()
        {
            try
            {
                _player?.Pause();
                _player?.Dispose();
                _player = null;
            }
            catch { }
            return Task.CompletedTask;
        }
    }
}
