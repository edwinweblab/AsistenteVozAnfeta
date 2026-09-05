using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Search;

public static class MeetLinkParser
{
    public static IReadOnlyList<Uri> Extract(string? text)
    {
        var result = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text ?? "", @"https://[^\s<>""']+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '<', '>', '"', '\'');
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme == "https" && uri.Host.Equals("meet.google.com", StringComparison.OrdinalIgnoreCase) &&
                uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                Regex.IsMatch(uri.AbsolutePath, @"^/[a-z]{3}-[a-z]{4}-[a-z]{3}/?$", RegexOptions.IgnoreCase))
            {
                if (seen.Add(uri.AbsoluteUri)) result.Add(uri);
                if (result.Count == 8) break;
            }
        }
        return result;
    }
}
