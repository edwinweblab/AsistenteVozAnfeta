using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Search;

public static class MeetLinkHelper
{
    /// <summary>
    /// Attempts to extract Google Meet links from the primary text (typically the message)
    /// and, if none are found, from a secondary text (e.g., the reminder title).
    /// If neither contains a full URL, it also tries to detect a plain Meet code like "Meet 2 · omp-srcb-uix"
    /// and builds the URL.
    /// </summary>
    /// <param name="primary">Primary source text (may be null).</param>
    /// <param name="secondary">Secondary source text (may be null).</param>
    /// <returns>A read‑only list of distinct <see cref="Uri"/> objects.</returns>
    public static IReadOnlyList<Uri> ExtractMeetLinks(string? primary, string? secondary)
    {
        // Try full URL extraction first.
        var links = MeetLinkParser.Extract(primary);
        if (links.Count > 0)
            return links;

        // Fallback to secondary source (often the title).
        links = MeetLinkParser.Extract(secondary);
        if (links.Count > 0)
            return links;

        // If no URL was found, attempt to locate a plain Meet code.
        var code = ExtractMeetCode(primary) ?? ExtractMeetCode(secondary);
        if (code == null)
        {
            // Some messages use a middle dot separator before the Meet code, e.g., "Meet 2 · omp-srcb-uix"
            string[] parts = (primary ?? secondary ?? string.Empty).Split('·');
            if (parts.Length > 1)
            {
                var candidate = parts[^1].Trim();
                // Validate candidate with same regex
                var match = Regex.Match(candidate, @"[a-z0-9]{2,4}[-\u2010\u2011][a-z0-9]{4,5}[-\u2010\u2011][a-z0-9]{3}", RegexOptions.IgnoreCase);
                if (match.Success) code = match.Value;
            }
        }
        if (code != null)
        {
            var uriString = $"https://meet.google.com/{code}";
            if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
                return new List<Uri> { uri };
        }
        return links;
    }

    // Internal helper to detect a Meet code like "abc-defg-hij".
    private static string? ExtractMeetCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        // Pattern: three groups separated by hyphens, typical Meet code.
        var match = Regex.Match(text, @"[a-z0-9]{2,4}[-\u2010\u2011][a-z0-9]{4,5}[-\u2010\u2011][a-z0-9]{3}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }
}
