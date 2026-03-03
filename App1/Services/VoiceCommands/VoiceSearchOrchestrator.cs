using System;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Speech;

namespace Anfeta.UI.Services.VoiceCommands
{
    public sealed class VoiceSearchOrchestrator
    {
        private readonly ISpeechToTextService _stt;
        private readonly VoiceCommandEngine _engine;
        private readonly IVoicePostActionService _post;
        private static bool ContainsExtUrl(string text)
        {
            var t = (text ?? "").ToLowerInvariant();
            return t.Contains("ext:url");
        }
        public VoiceSearchOrchestrator(ISpeechToTextService stt, VoiceCommandEngine engine, IVoicePostActionService post)
        {
            _stt = stt;
            _engine = engine;
            _post = post;
        }
        public async Task<VoiceListenResult> ListenAndExecuteAsync(
            ISearchCommandSink sink,
            CancellationToken ct = default)
        {
            await _stt.InitializeAsync(_stt.GetCurrentLanguage());

            var phrase = await _stt.RecognizeOnceAsync(ct);

            if (string.IsNullOrWhiteSpace(phrase))
            {
                return new VoiceListenResult
                {
                    Phrase = phrase,
                    Matched = false
                };
            }
            var parsed = _engine.TryParse(phrase);
            if (parsed is null)
                return new VoiceListenResult { Phrase = phrase, Matched = false };

            var isAbrir = string.Equals(parsed.Command.Name, "Abrir", StringComparison.OrdinalIgnoreCase);

            var baseText = string.IsNullOrWhiteSpace(parsed.ArgsText)
                ? parsed.Command.Token
                : parsed.ArgsText;

            var searchText = baseText;

            if (isAbrir)
            {
                if (!ContainsExtUrl(baseText))
                    searchText = (baseText + " ext:url").Trim();

                _post.ArmSpeakTopUrls(6);
            }

            await sink.ExecuteSearchTextAsync(searchText);

            return new VoiceListenResult
            {
                Phrase = phrase,
                Matched = true,
                CommandName = parsed.Command.Name,
                Token = parsed.Command.Token,
                ArgsText = parsed.ArgsText,
                MatchedSynonym = parsed.MatchedSynonym,
                ExecutedSearchText = searchText
            };
        }
    }
}