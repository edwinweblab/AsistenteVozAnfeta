using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.VoiceCommands;

public sealed class VoiceCommandsTextImportService
{
    // soporta: "a == b", "a = b", "a -> b", "a : b"
    private static readonly Regex MapRegex = new(
        @"^\s*(?<left>.+?)\s*(==|=|->|:)\s*(?<right>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ImportResult Parse(string raw)
    {
        var result = new ImportResult();

        if (string.IsNullOrWhiteSpace(raw))
            return result;

        // separa líneas de forma robusta
        var lines = raw
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n', StringSplitOptions.None);

        foreach (var line0 in lines)
        {
            var line = (line0 ?? "").Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("#") || line.StartsWith("//")) continue;

            // Soporta: ==, =, ->, :
            var sep = FindSeparator(line);
            if (sep is null)
            {
                result.SkippedLines.Add(line0);
                continue;
            }

            var parts = line.Split(sep, 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                result.SkippedLines.Add(line0);
                continue;
            }

            var left = Normalize(parts[0]);
            var right = Normalize(parts[1]);

            // token debe ser "una palabra/código" (sin espacios)
            right = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                result.SkippedLines.Add(line0);
                continue;
            }

            if (!result.TokenToSynonyms.TryGetValue(right, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result.TokenToSynonyms[right] = set;
            }
            set.Add(left);
        }

        return result;

        static string? FindSeparator(string s)
        {
            if (s.Contains("==")) return "==";
            if (s.Contains("->")) return "->";
            if (s.Contains("=")) return "=";
            if (s.Contains(":")) return ":";
            return null;
        }
    }

    public List<VoiceCommand> BuildCommandsGroupedByToken(ImportResult parsed)
    {
        var cmds = new List<VoiceCommand>();

        foreach (var kv in parsed.TokenToSynonyms.OrderBy(k => k.Key))
        {
            var token = kv.Key;
            var synonyms = kv.Value
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            cmds.Add(new VoiceCommand
            {
                Name = token,          // 👈 simple y consistente
                Token = token,
                IsEnabled = true,
                Synonyms = synonyms
            });
        }

        return cmds;
    }

    private static string Normalize(string s)
    {
        // mínimo: trim y colapsar espacios
        s = s.Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }

    public sealed class ImportResult
    {
        public Dictionary<string, HashSet<string>> TokenToSynonyms { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> SkippedLines { get; } = new();
    }
}