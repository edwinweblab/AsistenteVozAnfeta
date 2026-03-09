using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.VoiceCommands;

public sealed class VoiceCommandEngine
{
    private readonly VoiceCommandsRepository _repo;

    private List<VoiceCommand> _items = new();
    private readonly Dictionary<string, VoiceCommand> _synIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public VoiceCommandEngine(VoiceCommandsRepository repo)
    {
        _repo = repo;
    }
    
    private readonly List<VoiceCommand> _builtIns = new()
    {
        new VoiceCommand
        {
            Name = "Abrir",
            Token = "__open__",           // token interno (no va al SearchBox)
            IsEnabled = true,
            Synonyms = new List<string> { "abrir", "abre", "ábreme", "abreme", "open" }
        }
    };
    public async Task ReloadAsync()
    {
        _items = await _repo.LoadAsync();
        RebuildIndex();
        _loaded = true;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await ReloadAsync();
    }

    // Compatibilidad: lo viejo sigue funcionando
    public VoiceCommand? TryResolve(string phrase)
    {
        return TryParse(phrase)?.Command;
    }

    // Nuevo resultado del parseo (comando + sinónimo que matcheó + args)
    public sealed record VoiceParseResult(
        VoiceCommand Command,
        string MatchedSynonym,
        string ArgsText
    );

    // Nuevo: soporta sinónimos multi-palabra + extracción de args
    public VoiceParseResult? TryParse(string phrase)
    {
        var p = Normalize(phrase);
        if (string.IsNullOrWhiteSpace(p)) return null;

        // Si no se ha cargado, no intentamos "auto load" aquí para evitar async.
        // Asegúrate de llamar ReloadAsync/EnsureLoadedAsync al iniciar.
        if (_synIndex.Count == 0) return null;

        // 1) match exacto al inicio por sinónimo (multi-palabra)
        // Ordena por longitud descendente para que "abrir reporte" gane a "abrir"
        foreach (var kv in _synIndex.OrderByDescending(x => x.Key.Length))
        {
            var syn = kv.Key;   // ya normalizado
            var cmd = kv.Value;

            if (IsPrefixMatch(p, syn))
            {
                var args = ExtractArgs(p, syn);
                return new VoiceParseResult(cmd, syn, args);
            }
        }

        // 2) fallback opcional: prefijo "suave" por primera palabra (para errores comunes del STT)
        var firstWord = p.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstWord) && firstWord.Length >= 4)
        {
            var hit = _synIndex.Keys
                .OrderByDescending(k => k.Length)
                .FirstOrDefault(k =>
                    k.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase) ||
                    firstWord.StartsWith(k, StringComparison.OrdinalIgnoreCase));

            if (hit != null)
            {
                var cmd = _synIndex[hit];
                var args = ExtractArgs(p, hit);
                return new VoiceParseResult(cmd, hit, args);
            }
        }

        return null;
    }

    private void RebuildIndex()
    {
        _synIndex.Clear();
        // 1) built-ins primero (prioridad)
        IndexCommands(_builtIns);

        // 2) luego user commands
        IndexCommands(_items);
    }
    public sealed record VoiceMultiParseResult(
    string SearchText,
    List<string> Tokens,
    List<string> MatchedSynonyms
    );

    public VoiceMultiParseResult? TryParseToSearchText(string phrase)
    {
        var p = Normalize(phrase);
        if (string.IsNullOrWhiteSpace(p)) return null;
        if (_synIndex.Count == 0) return null;

        var words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;

        var outTokens = new List<string>();
        var matchedSynonyms = new List<string>();

        int i = 0;
        while (i < words.Length)
        {
            VoiceCommand? bestCmd = null;
            string? bestSyn = null;
            int bestLen = 0;

            // 1) intentar match exacto por segmento más largo
            for (int len = words.Length - i; len >= 1; len--)
            {
                var segment = string.Join(" ", words.Skip(i).Take(len));

                if (_synIndex.TryGetValue(segment, out var cmd))
                {
                    // ignorar built-in abrir en modo multi-token
                    if (string.Equals(cmd.Token, "__open__", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bestCmd = cmd;
                    bestSyn = segment;
                    bestLen = len;
                    break;
                }
            }

            // 2) fallback suave solo para una palabra
            if (bestCmd is null)
            {
                var soft = TryResolveSingleWordSoft(words[i]);
                if (soft is not null &&
                    !string.Equals(soft.Token, "__open__", StringComparison.OrdinalIgnoreCase))
                {
                    bestCmd = soft;
                    bestSyn = words[i];
                    bestLen = 1;
                }
            }

            // 3) si no matcheó, deja la palabra tal cual
            if (bestCmd is null)
            {
                outTokens.Add(words[i]);
                i++;
                continue;
            }

            outTokens.Add((bestCmd.Token ?? "").Trim());
            matchedSynonyms.Add(bestSyn!);
            i += bestLen;
        }

        // si no matcheó ningún comando, no lo consideres parse válido
        if (matchedSynonyms.Count == 0)
            return null;

        var finalText = string.Join(" ", outTokens.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(finalText))
            return null;

        return new VoiceMultiParseResult(
            finalText,
            outTokens.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
            matchedSynonyms
        );
    }

    private VoiceCommand? TryResolveSingleWordSoft(string word)
    {
        var w = Normalize(word);
        if (string.IsNullOrWhiteSpace(w) || w.Length < 4) return null;

        var hit = _synIndex.Keys
            .OrderByDescending(k => k.Length)
            .FirstOrDefault(k =>
                !k.Contains(' ') &&
                (k.StartsWith(w, StringComparison.OrdinalIgnoreCase) ||
                 w.StartsWith(k, StringComparison.OrdinalIgnoreCase)));

        if (hit is null) return null;
        return _synIndex[hit];
    }
    private static bool IsPrefixMatch(string phraseNorm, string synNorm)
    {
        if (phraseNorm.Equals(synNorm, StringComparison.OrdinalIgnoreCase))
            return true;

        // Debe ser "syn " para respetar límite de palabra
        return phraseNorm.StartsWith(synNorm + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractArgs(string phraseNorm, string matchedSynNorm)
    {
        if (phraseNorm.Length <= matchedSynNorm.Length) return "";
        return phraseNorm.Substring(matchedSynNorm.Length).Trim();
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim().ToLowerInvariant();

        // quitar acentos (match)
        s = s.Normalize(NormalizationForm.FormD);
        var chars = s.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        s = new string(chars.ToArray()).Normalize(NormalizationForm.FormC);

        // quitar puntuación simple
        s = new string(s.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray());

        // colapsar espacios múltiples (opcional pero ayuda)
        s = string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return s.Trim();
    }
    private void IndexCommands(IEnumerable<VoiceCommand> cmds)
    {
        foreach (var cmd in cmds)
        {
            if (!cmd.IsEnabled) continue;

            foreach (var syn in cmd.Synonyms ?? new List<string>())
            {
                var key = Normalize(syn);
                if (string.IsNullOrWhiteSpace(key)) continue;

                if (!_synIndex.ContainsKey(key))
                    _synIndex[key] = cmd;
            }
        }
    }
}