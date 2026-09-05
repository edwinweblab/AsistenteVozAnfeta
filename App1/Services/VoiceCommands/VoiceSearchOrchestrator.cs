using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
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
    CancellationToken ct = default, Action? onReady = null, IReadOnlyList<string>? localCommands = null)
        {
            await _stt.InitializeAsync(_stt.GetCurrentLanguage());
            string? phrase;
            if (localCommands != null)
            {
                if (_stt is not ICommandSpeechToTextService local)
                    throw new InvalidOperationException("El servicio actual no admite comandos locales. Selecciona Dictado si deseas usar el servicio en línea.");
                await _engine.EnsureLoadedAsync();
                phrase = await local.RecognizeCommandsAsync(localCommands.Concat(_engine.GetLocalPhrases()).Distinct().ToArray(), ct, onReady);
            }
            else phrase = await _stt.RecognizeOnceAsync(ct, onReady);

            if (string.IsNullOrWhiteSpace(phrase))
            {
                return new VoiceListenResult
                {
                    Phrase = phrase,
                    Matched = false,
                    CommandName = "STT vacío",
                    Token = ""
                };
            }

            ct.ThrowIfCancellationRequested();
            if (await sink.TryExecuteDailyActionAsync(phrase))
                return new VoiceListenResult { Phrase = phrase, Matched = true, CommandName = "Acción diaria", Token = "acción" };

            await _engine.EnsureLoadedAsync();
            var parsed = _engine.TryParse(phrase);

            var isAbrir = parsed is not null &&
                          string.Equals(parsed.Command.Token, "__open__", StringComparison.OrdinalIgnoreCase);

            if (isAbrir)
            {
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

            var multi = _engine.TryParseToSearchText(phrase);

            if (multi is null)
            {
                if (localCommands == null)
                {
                    var query = phrase.Trim();
                    foreach (var prefix in new[] { "buscar ", "busca ", "búscame ", "buscame " })
                        if (query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { query = query[prefix.Length..].Trim(); break; }
                    if (query.Length > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await sink.ExecuteSearchTextAsync(query);
                        return new VoiceListenResult { Phrase = phrase, Matched = true, CommandName = "Búsqueda por dictado", Token = query, ExecutedSearchText = query };
                    }
                }
                return new VoiceListenResult
                {
                    Phrase = phrase,
                    Matched = false,
                    CommandName = "Sin match engine",
                    Token = ""
                };
            }

            var finalSearchText = (multi.SearchText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(finalSearchText))
            {
                return new VoiceListenResult
                {
                    Phrase = phrase,
                    Matched = false,
                    CommandName = "SearchText vacío",
                    Token = ""
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
