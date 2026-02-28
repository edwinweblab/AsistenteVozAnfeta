using System;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Services.Search;

namespace Anfeta.UI.Services.VoiceCommands
{
    public sealed class VoiceSearchOrchestrator
    {
        private readonly ISpeechToTextService _stt;
        private readonly VoiceCommandEngine _engine;

        public VoiceSearchOrchestrator(ISpeechToTextService stt, VoiceCommandEngine engine)
        {
            _stt = stt;
            _engine = engine;
        }

        /// <summary>
        /// Escucha una sola vez, detecta comando por sinónimo y ejecuta token search en el buscador.
        /// </summary>
        public async Task<VoiceListenResult> ListenAndExecuteAsync(ISearchCommandSink sink, CancellationToken ct = default)
        {
            await _stt.InitializeAsync(_stt.GetCurrentLanguage());

            var phrase = await _stt.RecognizeOnceAsync(ct);

            if (string.IsNullOrWhiteSpace(phrase))
                return new VoiceListenResult { Phrase = phrase };

            var cmd = _engine.TryResolve(phrase);

            if (cmd is null)
                return new VoiceListenResult { Phrase = phrase };

            await sink.ExecuteSearchTextAsync(cmd.Token);

            return new VoiceListenResult
            {
                Phrase = phrase,
                CommandName = cmd.Name,
                Token = cmd.Token
            };
        }
    }
} 