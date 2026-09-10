using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Search;

public sealed record AssignmentObservation(string PageId, bool Assigned, bool Eligible, DateTimeOffset? EditedAt);

// Sin UI ni red: compara instantáneas, conserva ausencias de índices parciales.
public sealed class AssignmentChangeTracker
{
    private static readonly Regex ActivityPhase = new(
        @"(?:(?<=^|\s|\[|[^\p{L}\p{N}_])(?:001|002|00)?\s*(?<phase>sprtuz|aprtuz|prtuz|rtuz|ztuz|tuz|z|p|r)\s*(?:REVISION|COBRAR|PAGAR)?(?![a-z0-9_]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PriorityVariant = new(
        @"(?<![\p{L}\p{Nd}_])(?<tag>jjohn|nneft|kkarl|bbria|ggena|iisai|iisaia|eemma|aandr|ssote|eedua|aacal)\s*(?<v>001|002|00)(?![\p{L}\p{Nd}_])|" +
        @"(?<![\p{L}\p{Nd}_.:\-/])(?<v>001|002|00)\s*(?<tag>jjohn|nneft|kkarl|bbria|ggena|iisai|iisaia|eemma|aandr|ssote|eedua|aacal)(?![\p{L}\p{Nd}_])|" +
        @"(?<![\p{L}\p{Nd}_.:\-/])(?<v>001|002|00)(?:prt[a-z0-9_-]*)?(?![\p{L}\p{Nd}_.:\-/])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TerminatedKeyword = new(
        @"(?i)(?:\[(?:TERMINAD[OA]|FINALIZAD[OA]|CANCELAD[OA]|COMPLETAD[OA]|HECH[OA]|LIST[OA])\]|\b(?:zREVISION|ztuz\w*|terminad[oa]|finalizad[oa]|completad[oa]|cancelad[oa])\b)",
        RegexOptions.Compiled);

    public static bool IsTerminated(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var clean = title.Trim();
        if (TerminatedKeyword.IsMatch(clean)) return true;
        var phaseMatch = ActivityPhase.Match(clean);
        if (phaseMatch.Success)
        {
            var p = phaseMatch.Groups["phase"].Value.ToLowerInvariant();
            if (p is "ztuz" or "z" or "sprtuz") return true;
        }
        return false;
    }

    public static string GetActivityState(string? title)
    {
        var clean = title ?? string.Empty;
        var varMatch = PriorityVariant.Match(clean);
        var variantStr = varMatch.Success ? varMatch.Groups["v"].Value.ToLowerInvariant() switch
        {
            "00" => "00 · Urgente",
            "001" => "01 · Importante",
            "002" => "02 · Secundaria",
            _ => ""
        } : "";

        var isTerminated = IsTerminated(clean);
        if (isTerminated)
        {
            var termLabel = clean.Contains("sprtuz", StringComparison.OrdinalIgnoreCase) ? "Suspendida" : "Terminada";
            return !string.IsNullOrEmpty(variantStr) ? $"{variantStr} ({termLabel})" : termLabel;
        }

        var phaseMatch = ActivityPhase.Match(clean);
        var phaseStr = phaseMatch.Groups["phase"].Value.ToLowerInvariant() switch
        {
            "sprtuz" => "Suspendida",
            "aprtuz" => "Por hacer",
            "rtuz" or "r" => "En revisión",
            "prtuz" or "tuz" or "p" => "Pendiente",
            "ztuz" or "z" => "Terminada",
            _ => ""
        };

        if (!string.IsNullOrEmpty(variantStr) && !string.IsNullOrEmpty(phaseStr))
            return $"{variantStr} ({phaseStr})";
        if (!string.IsNullOrEmpty(variantStr))
            return variantStr;
        return phaseStr;
    }

    public static bool IsActivityEligible(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (IsTerminated(title)) return false;
        var state = GetActivityState(title);
        if (state.Contains("Terminada", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("Suspendida", StringComparison.OrdinalIgnoreCase))
            return false;
        if (state.Length > 0) return true;
        return title.Contains("[Revisiones]", StringComparison.OrdinalIgnoreCase);
    }

    public DateTimeOffset? StartedAt { get; set; }
    public Dictionary<string, bool> Known { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Separa la asignación observada del aviso entregado. Compatible con cachés v1.
    public HashSet<string> Pending { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Observe(IEnumerable<AssignmentObservation> rows, DateTimeOffset now)
    {
        var changes = new List<string>();
        var baseline = StartedAt == null;
        StartedAt ??= now;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.PageId)) continue;
            var existed = Known.TryGetValue(row.PageId, out var wasAssigned);
            if (!row.Assigned)
            {
                Pending.Remove(row.PageId);
            }
            else if (!baseline && !wasAssigned)
            {
                Pending.Add(row.PageId);
            }
            if (row.Assigned && row.Eligible && Pending.Remove(row.PageId))
            {
                changes.Add(row.PageId);
            }
            Known[row.PageId] = row.Assigned;
        }
        return changes;
    }
}
