using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Search;

public sealed record AssignmentObservation(string PageId, bool Assigned, bool Eligible, DateTimeOffset? EditedAt);

// Sin UI ni red: compara instantáneas, conserva ausencias de índices parciales.
public sealed class AssignmentChangeTracker
{
    private static readonly Regex ActivityPhase = new(@"(?<![\p{L}\p{N}])(?<phase>sprtuz|aprtuz|prtuz|rtuz|tuz|p|r)REVISION(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public static string GetActivityState(string? title) => ActivityPhase.Match(title ?? "").Groups["phase"].Value.ToLowerInvariant() switch
    {
        "sprtuz" => "Suspendida",
        "aprtuz" => "Por hacer",
        "rtuz" or "r" => "En revisión",
        "prtuz" or "tuz" or "p" => "Pendiente",
        _ => ""
    };
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
            if (!row.Assigned) Pending.Remove(row.PageId);
            else if (!baseline && !wasAssigned && (existed || row.EditedAt >= StartedAt))
                Pending.Add(row.PageId);
            if (row.Assigned && row.Eligible && Pending.Remove(row.PageId)) changes.Add(row.PageId);
            Known[row.PageId] = row.Assigned;
        }
        return changes;
    }
}
