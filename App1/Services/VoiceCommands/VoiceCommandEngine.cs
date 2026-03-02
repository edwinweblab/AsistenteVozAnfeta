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

        foreach (var cmd in _items)
        {
            if (!cmd.IsEnabled) continue;

            foreach (var syn in cmd.Synonyms ?? new List<string>())
            {
                var key = Normalize(syn);
                if (string.IsNullOrWhiteSpace(key)) continue;

                // si hay conflicto, nos quedamos con el primero (luego metemos prioridad)
                if (!_synIndex.ContainsKey(key))
                    _synIndex[key] = cmd;
            }
        }
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
}