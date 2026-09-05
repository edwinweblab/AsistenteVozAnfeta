using Anfeta.UI.Models.Weblab;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Search;

public sealed partial class IndexedFileReminderService
{
    private bool _assignmentScanRunning;
    private string _assignmentUser = "";
    private long _assignmentVersion = -1;
    private readonly Anfeta.UI.Services.Notion.NotionCalendarService _assignmentResolver = new();
    private readonly Dictionary<string, string> _assignmentNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _assignmentRetryAfter = new(StringComparer.OrdinalIgnoreCase);

    private async void ScanAssignmentChanges()
    {
        if (_assignmentScanRunning || ReminderDue == null) return;
        var tag = NormalizeReminderPersonTag(ApplicationData.Current.LocalSettings.Values[LS_CurrentUserTag] as string);
        if (string.IsNullOrWhiteSpace(tag) || !RecipientNames.TryGetValue(tag, out var name) || tag == "__all__") return;
        var state = App.AppHost.Services.GetRequiredService<AppStateService>();
        var user = tag + "|" + state.CollaboratorId;
        var version = App.LocalIndex.Version;
        if (_assignmentVersion == version && _assignmentUser == user) return;
        _assignmentScanRunning = true;
        try
        {
            // Proyección en UI; comparaciones y disco fuera del Dispatcher.
            var keys = new[] { tag, name, state.CollaboratorId ?? "" }.Select(NormalizeAssignmentKey).Where(s => s.Length > 0).ToHashSet();
            var snapshot = App.LocalIndex.GetAll();
            var relationIds = snapshot.Where(r => r.Source == SearchSource.Notion && r.AssignmentDataVersion == 1)
                .Where(r => (r.AssignmentKeys ?? Array.Empty<string>()).All(value => Guid.TryParse(value, out _)))
                .SelectMany(r => r.AssignmentKeys ?? Array.Empty<string>()).Where(value => Guid.TryParse(value, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !_assignmentNames.ContainsKey(id) && !keys.Contains(NormalizeAssignmentKey(id)) &&
                    (!_assignmentRetryAfter.TryGetValue(id, out var retry) || retry <= DateTimeOffset.UtcNow)).Take(32).ToArray();
            var token = ApplicationData.Current.LocalSettings.Values["Notion.Token"] as string ?? "";
            using var lookupTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var id in relationIds)
            {
                if (string.IsNullOrWhiteSpace(token)) break;
                _assignmentRetryAfter[id] = DateTimeOffset.UtcNow.AddMinutes(10);
                try
                {
                    var resolved = await _assignmentResolver.ResolveAssignmentPersonAsync(token, id, lookupTimeout.Token);
                    if (!string.IsNullOrWhiteSpace(resolved)) _assignmentNames[id] = resolved;
                }
                catch (OperationCanceledException) { break; }
                catch (System.Net.Http.HttpRequestException) { break; }
            }
            bool IsResolved(string[]? values) => values == null || values.Length == 0 || values.Any(value => !Guid.TryParse(value, out _) || keys.Contains(NormalizeAssignmentKey(value))) ||
                values.All(value => _assignmentNames.ContainsKey(value));
            bool IsAssigned(string[]? values) => (values ?? Array.Empty<string>()).Any(value => keys.Contains(NormalizeAssignmentKey(value)) ||
                (_assignmentNames.TryGetValue(value, out var resolved) && keys.Contains(NormalizeAssignmentKey(resolved))));
            var hasUnresolved = snapshot.Any(r => r.Source == SearchSource.Notion && r.AssignmentDataVersion == 1 && !IsResolved(r.AssignmentKeys));
            var rows = snapshot.Where(r => r.Source == SearchSource.Notion && r.AssignmentDataVersion == 1 && !string.IsNullOrWhiteSpace(r.ExternalId) && IsResolved(r.AssignmentKeys))
                .GroupBy(r => r.ExternalId.Replace("-", "").ToLowerInvariant()).Select(g => g.OrderByDescending(r => r.NotionEditedUtc).First())
                .Select(r => new { Id = r.ExternalId.Replace("-", "").ToLowerInvariant(), r.Name, r.ExternalUrl, r.Target, r.ScheduledDate,
                    Observation = new AssignmentObservation(r.ExternalId.Replace("-", "").ToLowerInvariant(),
                        IsAssigned(r.AssignmentKeys),
                        AssignmentChangeTracker.GetActivityState(r.Name).Length > 0, r.NotionEditedUtc) }).ToList();
            if (rows.Count == 0) return; // Caché antigua: esperar la primera sincronización real.
            var file = Path.Combine(ApplicationData.Current.LocalFolder.Path, "assignments-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user))) + ".json");
            var changed = await Task.Run(() =>
            {
                // Candado entre procesos: sólo uno genera el aviso de este usuario/equipo.
                using var gate = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                AssignmentChangeTracker tracker;
                try { tracker = File.Exists(file) ? JsonSerializer.Deserialize<AssignmentChangeTracker>(File.ReadAllText(file)) ?? new() : new(); }
                catch (JsonException) { tracker = new(); } // Caché dañada: baseline silencioso, no avalancha.
                var changes = tracker.Observe(rows.Select(r => r.Observation), DateTimeOffset.UtcNow);
                var temp = file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(tracker));
                File.Move(temp, file, true);
                return changes.ToHashSet();
            });
            _assignmentVersion = hasUnresolved ? -1 : version;
            _assignmentUser = user;
            if (!_started || _disposed || NormalizeReminderPersonTag(ApplicationData.Current.LocalSettings.Values[LS_CurrentUserTag] as string) != tag) return;
            foreach (var row in rows.Where(r => changed.Contains(r.Id)))
            {
                var target = string.IsNullOrWhiteSpace(row.ExternalUrl) ? row.Target : row.ExternalUrl;
                if (string.IsNullOrWhiteSpace(target)) target = "https://www.notion.so/" + row.Id;
                var message = $"Nueva actividad asignada a {name}: {row.Name}\nEstado: {AssignmentChangeTracker.GetActivityState(row.Name)}\nFecha de trabajo: {(string.IsNullOrWhiteSpace(row.ScheduledDate) ? "Sin fecha" : row.ScheduledDate)}";
                // PageId vacío intencional: Enterado NO modifica la actividad ni crea mensajes en Notion.
                ReminderDue?.Invoke(this, new IndexedFileReminder("assignment:" + row.Id + ":" + Guid.NewGuid().ToString("N"),
                    "Nueva actividad asignada", message, target, DateTimeOffset.Now, SearchSource.Notion, tag, name, "", "", ""));
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Assignments] " + ex.GetType().Name); }
        finally { _assignmentScanRunning = false; }
    }

    private static string NormalizeAssignmentKey(string value) => AssignmentIdentity.Normalize(value);
}
