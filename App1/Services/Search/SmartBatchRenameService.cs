using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Anfeta.UI.Services.Search
{
    public sealed record SmartRenameInput(
        string OriginalName,
        bool IsFolder,
        bool IsNotion = false);

    public sealed record SmartRenameAnalysis(
        string OldFormat,
        IReadOnlyList<IReadOnlyList<string>> Variables,
        IReadOnlyList<string> Extensions,
        bool HasCommonStructure);

    public sealed record SmartRenamePreview(
        IReadOnlyList<string> Names,
        string? Error);

    /// <summary>
    /// Analiza varios nombres al estilo Everything:
    /// conserva las palabras comunes como texto y convierte las partes
    /// diferentes en variables %1, %2, etc.
    /// </summary>
    public sealed class SmartBatchRenameService
    {
        public SmartRenameAnalysis Analyze(
            IReadOnlyList<SmartRenameInput> inputs,
            bool keepExtensions,
            bool matchCase,
            bool matchDiacritics)
        {
            if (inputs == null || inputs.Count == 0)
                return new SmartRenameAnalysis(
                    "%1",
                    Array.Empty<IReadOnlyList<string>>(),
                    Array.Empty<string>(),
                    false);

            var sourceNames = inputs
                .Select(x => x.OriginalName ?? string.Empty)
                .ToList();

            var extensions = sourceNames
                .Select((name, index) =>
                    keepExtensions && !inputs[index].IsFolder
                        ? Path.GetExtension(name) ?? string.Empty
                        : string.Empty)
                .ToList();

            var stems = sourceNames
                .Select((name, index) =>
                    keepExtensions && !inputs[index].IsFolder
                        ? Path.GetFileNameWithoutExtension(name) ?? name
                        : name)
                .ToList();

            var tokensByName = stems
                .Select(TokenizeWords)
                .ToList();

            var commonTokens = tokensByName.Count == 0
                ? new List<string>()
                : tokensByName[0].ToList();

            for (var i = 1; i < tokensByName.Count && commonTokens.Count > 0; i++)
            {
                commonTokens = LongestCommonSubsequence(
                    commonTokens,
                    tokensByName[i],
                    matchCase,
                    matchDiacritics);
            }

            // Evita patrones excesivamente fragmentados.
            commonTokens = commonTokens
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var allVariables = new List<IReadOnlyList<string>>();
            var successful = true;

            foreach (var stem in stems)
            {
                if (!TryExtractVariables(
                        stem,
                        commonTokens,
                        matchCase,
                        matchDiacritics,
                        out var variables))
                {
                    successful = false;
                    break;
                }

                allVariables.Add(variables);
            }

            if (!successful || commonTokens.Count == 0)
            {
                var fallbackVariables = stems
                    .Select(x => (IReadOnlyList<string>)new[] { x })
                    .ToList();

                var extSuffix = GetCommonExtensionSuffix(extensions);
                return new SmartRenameAnalysis(
                    "%1" + extSuffix,
                    fallbackVariables,
                    extensions,
                    false);
            }

            var oldFormat = BuildFormat(commonTokens, allVariables, extensions);
            return new SmartRenameAnalysis(
                oldFormat,
                allVariables,
                extensions,
                true);
        }

        public SmartRenamePreview Preview(
            SmartRenameAnalysis analysis,
            string newFormat,
            IReadOnlyList<SmartRenameInput> inputs,
            bool keepExtensions)
        {
            if (analysis == null)
                return new SmartRenamePreview(
                    Array.Empty<string>(),
                    "No existe un análisis de nombres.");

            if (string.IsNullOrWhiteSpace(newFormat))
                return new SmartRenamePreview(
                    Array.Empty<string>(),
                    "El formato nuevo está vacío.");

            var output = new List<string>(inputs.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < inputs.Count; i++)
            {
                var variables = i < analysis.Variables.Count
                    ? analysis.Variables[i]
                    : Array.Empty<string>();

                var candidate = ExpandVariables(newFormat, variables).Trim();

                if (keepExtensions && !inputs[i].IsFolder)
                {
                    var extension = i < analysis.Extensions.Count
                        ? analysis.Extensions[i]
                        : Path.GetExtension(inputs[i].OriginalName) ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(extension) &&
                        !candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    {
                        candidate += extension;
                    }
                }

                if (string.IsNullOrWhiteSpace(candidate))
                    return new SmartRenamePreview(output, "El formato produce un nombre vacío.");

                if (!inputs[i].IsNotion)
                {
                    if (candidate is "." or "..")
                        return new SmartRenamePreview(
                            output,
                            $"Nombre inválido: {candidate}");

                    if (candidate.IndexOfAny(
                            Path.GetInvalidFileNameChars()) >= 0 ||
                        candidate.Contains('/') ||
                        candidate.Contains('\\'))
                    {
                        return new SmartRenamePreview(
                            output,
                            $"Nombre inválido generado: {candidate}");
                    }

                    if (candidate.EndsWith(
                            ".",
                            StringComparison.Ordinal) ||
                        candidate.EndsWith(
                            " ",
                            StringComparison.Ordinal))
                    {
                        return new SmartRenamePreview(
                            output,
                            $"El nombre no puede terminar con punto o espacio: {candidate}");
                    }
                }

                if (!seen.Add(candidate))
                {
                    return new SmartRenamePreview(
                        output,
                        $"El formato produce un nombre duplicado: {candidate}");
                }

                output.Add(candidate);
            }

            return new SmartRenamePreview(output, null);
        }

        private static string BuildFormat(
            IReadOnlyList<string> commonTokens,
            IReadOnlyList<IReadOnlyList<string>> variables,
            IReadOnlyList<string> extensions)
        {
            var variableCount = variables.Count == 0
                ? 1
                : variables.Max(x => x.Count);

            var pieces = new List<string>();
            var variableIndex = 1;

            for (var anchorIndex = 0; anchorIndex <= commonTokens.Count; anchorIndex++)
            {
                var hasMeaningfulVariable =
                    variableIndex <= variableCount &&
                    variables.Any(x =>
                        x.Count >= variableIndex &&
                        !string.IsNullOrWhiteSpace(x[variableIndex - 1]));

                if (hasMeaningfulVariable)
                    pieces.Add($"%{variableIndex}");

                if (anchorIndex < commonTokens.Count)
                    pieces.Add(commonTokens[anchorIndex]);

                variableIndex++;
            }

            var format = string.Join(" ", pieces.Where(x => !string.IsNullOrWhiteSpace(x)));
            var extSuffix = GetCommonExtensionSuffix(extensions);

            return string.IsNullOrWhiteSpace(format)
                ? "%1" + extSuffix
                : format + extSuffix;
        }

        private static string GetCommonExtensionSuffix(IReadOnlyList<string> extensions)
        {
            if (extensions == null || extensions.Count == 0)
                return string.Empty;

            var first = extensions[0] ?? string.Empty;

            return extensions.All(x =>
                    string.Equals(x ?? string.Empty, first, StringComparison.OrdinalIgnoreCase))
                ? first
                : string.Empty;
        }

        private static string ExpandVariables(
            string format,
            IReadOnlyList<string> variables)
        {
            var result = format ?? string.Empty;

            for (var i = variables.Count; i >= 1; i--)
            {
                result = result.Replace(
                    $"%{i}",
                    variables[i - 1] ?? string.Empty,
                    StringComparison.Ordinal);
            }

            return NormalizeSpaces(result);
        }

        private static bool TryExtractVariables(
            string source,
            IReadOnlyList<string> anchors,
            bool matchCase,
            bool matchDiacritics,
            out IReadOnlyList<string> variables)
        {
            var parts = new List<string>();
            var cursor = 0;

            foreach (var anchor in anchors)
            {
                var index = IndexOfNormalized(
                    source,
                    anchor,
                    cursor,
                    matchCase,
                    matchDiacritics);

                if (index < 0)
                {
                    variables = Array.Empty<string>();
                    return false;
                }

                parts.Add(source.Substring(cursor, index - cursor).Trim());
                cursor = index + anchor.Length;
            }

            parts.Add(source.Substring(cursor).Trim());
            variables = parts;
            return true;
        }

        private static int IndexOfNormalized(
            string source,
            string value,
            int startIndex,
            bool matchCase,
            bool matchDiacritics)
        {
            if (startIndex < 0 || startIndex > source.Length)
                return -1;

            if (matchDiacritics)
            {
                return source.IndexOf(
                    value,
                    startIndex,
                    matchCase
                        ? StringComparison.Ordinal
                        : StringComparison.OrdinalIgnoreCase);
            }

            // Cuando se ignoran diacríticos buscamos manteniendo la misma longitud
            // de cada carácter base; suficiente para títulos y nombres habituales.
            var normalizedSource = NormalizeForComparison(
                source.Substring(startIndex),
                matchCase,
                matchDiacritics);

            var normalizedValue = NormalizeForComparison(
                value,
                matchCase,
                matchDiacritics);

            var relative = normalizedSource.IndexOf(
                normalizedValue,
                StringComparison.Ordinal);

            return relative < 0 ? -1 : startIndex + relative;
        }

        private static List<string> LongestCommonSubsequence(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right,
            bool matchCase,
            bool matchDiacritics)
        {
            var dp = new int[left.Count + 1, right.Count + 1];

            for (var i = left.Count - 1; i >= 0; i--)
            {
                for (var j = right.Count - 1; j >= 0; j--)
                {
                    dp[i, j] = TokenEquals(
                        left[i],
                        right[j],
                        matchCase,
                        matchDiacritics)
                            ? dp[i + 1, j + 1] + 1
                            : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            var output = new List<string>();
            var x = 0;
            var y = 0;

            while (x < left.Count && y < right.Count)
            {
                if (TokenEquals(left[x], right[y], matchCase, matchDiacritics))
                {
                    output.Add(left[x]);
                    x++;
                    y++;
                }
                else if (dp[x + 1, y] >= dp[x, y + 1])
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }

            return output;
        }

        private static bool TokenEquals(
            string left,
            string right,
            bool matchCase,
            bool matchDiacritics)
        {
            return string.Equals(
                NormalizeForComparison(left, matchCase, matchDiacritics),
                NormalizeForComparison(right, matchCase, matchDiacritics),
                StringComparison.Ordinal);
        }

        private static string NormalizeForComparison(
            string value,
            bool matchCase,
            bool matchDiacritics)
        {
            var text = value ?? string.Empty;

            if (!matchDiacritics)
            {
                var decomposed = text.Normalize(NormalizationForm.FormD);
                var builder = new StringBuilder(decomposed.Length);

                foreach (var character in decomposed)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(character) !=
                        UnicodeCategory.NonSpacingMark)
                    {
                        builder.Append(character);
                    }
                }

                text = builder.ToString().Normalize(NormalizationForm.FormC);
            }

            return matchCase ? text : text.ToUpperInvariant();
        }

        private static List<string> TokenizeWords(string value)
        {
            return (value ?? string.Empty)
                .Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }

        private static string NormalizeSpaces(string value)
        {
            return string.Join(
                " ",
                (value ?? string.Empty)
                    .Split(
                        new[] { ' ', '\t', '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
