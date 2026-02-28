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
    private Dictionary<string, VoiceCommand> _synIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public VoiceCommandEngine(VoiceCommandsRepository repo)
    {
        _repo = repo;
    }

    public async Task ReloadAsync()
    {
        _items = await _repo.LoadAsync();
        RebuildIndex();
    }

    public VoiceCommand? TryResolve(string phrase)
    {
        var p = Normalize(phrase);
        if (string.IsNullOrWhiteSpace(p)) return null;

        // match por palabra completa (simple y seguro)
        var words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fallback: match por prefijo (para errores comunes)
        foreach (var w in words)
        {
            if (w.Length < 4) continue;

            var hit = _synIndex.Keys.FirstOrDefault(k => k.StartsWith(w, StringComparison.OrdinalIgnoreCase)
                                                      || w.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return _synIndex[hit];
        }
        return null;
    }
    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await ReloadAsync();
        _loaded = true;
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
        return s.Trim();
    }
}