using Anfeta.UI.Models;
using Anfeta.UI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Anfeta.UI.Models.Weblab;    
using Anfeta.UI.Services.Speech;  
namespace Anfeta.UI.Services.VoiceCommands;

public sealed class VoicePostActionService : IVoicePostActionService
{
    private readonly ITextToSpeechService _tts;

    private bool _pendingSpeakUrls;
    private int _maxItems = 6;
    private DateTimeOffset _armedAt;
    private readonly object _gate = new();

    // Ajustes UX
    private const int MaxNameChars = 48;   // recorte por nombre
    private const int MaxTotalChars = 220; // evita frases gigantes aun con nombres recortados

    public VoicePostActionService(ITextToSpeechService tts)
    {
        _tts = tts;
        
    }

    public async void ArmSpeakTopUrls(int maxItems = 6)
    {
        lock (_gate)
        {
            _pendingSpeakUrls = true;
            _maxItems = Math.Clamp(maxItems, 1, 10);
            _armedAt = DateTimeOffset.UtcNow;
        }

        // corta cualquier lectura anterior (fire and forget)
        await _tts.StopAsync();
    }

    public async void NotifySearchResults(IReadOnlyList<SearchResultRow> results)
    {
        if (!_pendingSpeakUrls) return;

        if (DateTimeOffset.UtcNow - _armedAt > TimeSpan.FromSeconds(10))
        {
            _pendingSpeakUrls = false;
            return;
        }

        _pendingSpeakUrls = false;

        var names = results
            .Where(IsUrlRow)
            .Select(r => BestName(r))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => TrimName(n!, MaxNameChars))
            .Take(_maxItems)
            .ToList();

        if (names.Count == 0)
        {
            await _tts.SpeakAsync("No encontré URLs.");
            return;
        }

        // ✅ UNA sola frase (no se corta)
        var listText = BuildCompactList(names, MaxTotalChars);

        // Si listText quedó vacío por límite, al menos di el total
        var msg = string.IsNullOrWhiteSpace(listText);
        await _tts.StopAsync();

        var parts = new List<string>
        {
            $"Encontré {names.Count} URLs."
        };

        for (int i = 0; i < names.Count; i++)
        {
            parts.Add($"{NumberToWord(i + 1)}: {names[i]}.");
        }

        var finalText = string.Join(" ", parts);

        await _tts.SpeakAsync(finalText);
    }

    private static bool IsUrlRow(SearchResultRow r)
    {
        var path = r.Target ?? r.Name ?? "";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".url";
    }

    private static string? BestName(SearchResultRow r)
    {
        // Preferimos Name; si viene vacío, caemos a filename del Target
        if (!string.IsNullOrWhiteSpace(r.Name))
            return r.Name;

        if (!string.IsNullOrWhiteSpace(r.Target))
            return Path.GetFileNameWithoutExtension(r.Target);

        return null;
    }

    private static string TrimName(string name, int maxChars)
    {
        name = name.Trim();
        if (name.Length <= maxChars) return name;
        return name.Substring(0, maxChars).TrimEnd() + "…";
    }

    private static string BuildCompactList(List<string> names, int maxTotalChars)
    {
        // Formato: "1) X. 2) Y. 3) Z."
        // y cortamos cuando se pasa del límite.
        var parts = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++)
            parts.Add($"{i + 1}) {names[i]}.");

        var result = "";
        foreach (var p in parts)
        {
            if ((result.Length + 1 + p.Length) > maxTotalChars)
                break;

            result = string.IsNullOrEmpty(result) ? p : (result + " " + p);
        }

        return result;
    }
    private static string NumberToWord(int n) => n switch
    {
        1 => "Uno",
        2 => "Dos",
        3 => "Tres",
        4 => "Cuatro",
        5 => "Cinco",
        6 => "Seis",
        _ => n.ToString()
    };
    public async Task StopAllAsync()
    {
        _pendingSpeakUrls = false;
        await _tts.StopAsync();
    }
}