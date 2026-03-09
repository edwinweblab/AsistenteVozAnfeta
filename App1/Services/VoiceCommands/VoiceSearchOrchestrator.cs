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
            await _engine.EnsureLoadedAsync();

            var phrase = await _stt.RecognizeOnceAsync(ct);

            if (string.IsNullOrWhiteSpace(phrase))
            {
                return new VoiceListenResult
                {
                    Phrase = phrase,
                    Matched = false
                };
            }

            // 1) primero intenta parse normal (para built-ins como Abrir)
            var parsed = _engine.TryParse(phrase);

            // ✅ Detecta intent "Abrir" por token interno (built-in)
            var isAbrir = parsed is not null &&
                          string.Equals(parsed.Command.Token, "__open__", StringComparison.OrdinalIgnoreCase);

            // ✅ Si es Abrir, conserva tu flujo actual
            if (isAbrir)
            {
                // Si es Abrir sin args: no buscamos nada
                if (string.IsNullOrWhiteSpace(parsed!.ArgsText))
                {
                    return new VoiceListenResult
                    {
                        Phrase = phrase,
                        Matched = true,
                        CommandName = parsed.Command.Name,
                        Token = parsed.Command.Token,
                        ArgsText = parsed.ArgsText,
                        MatchedSynonym = parsed.MatchedSynonym,
                        ExecutedSearchText = null
                    };
                }

                var searchText = (parsed.ArgsText ?? "").Trim();

                if (!ContainsExtUrl(searchText))
                    searchText = (searchText + " ext:url").Trim();

                _post.ArmSpeakTopUrls(6);

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

            // 2) Para comandos del buscador: parse multi-token
            var multi = _engine.TryParseToSearchText(phrase);

            if (multi is null)
            {
                return new VoiceListenResult
                {
                    Phrase = phrase,
                    Matched = false
                };
            }

            var finalSearchText = (multi.SearchText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(finalSearchText))
            {
                return new VoiceListenResult
                {
                    Phrase = phrase,
                    Matched = false
                };
            }

            await sink.ExecuteSearchTextAsync(finalSearchText);

            return new VoiceListenResult
            {
                Phrase = phrase,
                Matched = true,
                CommandName = multi.Tokens.Count > 1
                    ? "Comando múltiple"
                    : (parsed?.Command.Name ?? "Comando"),
                Token = finalSearchText,
                ArgsText = "",
                MatchedSynonym = string.Join(" | ", multi.MatchedSynonyms),
                ExecutedSearchText = finalSearchText
            };
        }
    }
}