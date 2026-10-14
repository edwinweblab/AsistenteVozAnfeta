using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Presence
{
    public sealed class UserPresenceService
    {
        private static readonly Lazy<UserPresenceService> _instance =
            new(() => new UserPresenceService());

        public static UserPresenceService Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeenUtc =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Timer _heartbeatTimer;
        private string? _dropboxFolder;

        public UserPresenceService()
        {
            TryResolveDropboxFolder();

            // Heartbeat cada 1 minuto
            _heartbeatTimer = new Timer(
                _ => _ = RecordLocalHeartbeatAsync(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMinutes(1));
        }

        private void TryResolveDropboxFolder()
        {
            try
            {
                var customDropbox = ApplicationData.Current.LocalSettings.Values["Search.DropboxPath"] as string;
                if (!string.IsNullOrWhiteSpace(customDropbox) && Directory.Exists(customDropbox))
                {
                    _dropboxFolder = customDropbox;
                    return;
                }

                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var defaultDropbox = Path.Combine(userProfile, "Dropbox");
                if (Directory.Exists(defaultDropbox))
                {
                    _dropboxFolder = defaultDropbox;
                }
            }
            catch
            {
            }
        }

        public string GetCurrentConfiguredUser()
        {
            try
            {
                var tag = ApplicationData.Current.LocalSettings.Values["Notion.DefaultUserTag"] as string
                       ?? ApplicationData.Current.LocalSettings.Values["Notion.PersonTag"] as string;

                if (!string.IsNullOrWhiteSpace(tag))
                {
                    return NormalizeToCanonicalPerson(tag);
                }

                var winUser = Environment.UserName;
                return NormalizeToCanonicalPerson(winUser);
            }
            catch
            {
                return "Neftali";
            }
        }

        public static string NormalizeToCanonicalPerson(string? name)
        {
            var clean = (name ?? string.Empty).Trim().ToLowerInvariant();
            if (clean.Contains("john") || clean.Contains("jjohn")) return "John";
            if (clean.Contains("karl") || clean.Contains("kkarl")) return "Karla";
            if (clean.Contains("isai") || clean.Contains("iisai")) return "Isaias";
            if (clean.Contains("sote") || clean.Contains("ssote") || clean.Contains("edua")) return "Sotelo";
            if (clean.Contains("acal") || clean.Contains("aacal")) return "Acalli";
            if (clean.Contains("andr") || clean.Contains("aandr")) return "Andrade";
            if (clean.Contains("bria") || clean.Contains("bbria")) return "Brian";
            if (clean.Contains("gena") || clean.Contains("ggena")) return "Genaro";
            if (clean.Contains("neft") || clean.Contains("nneft") || clean.Contains("nanoc")) return "Neftali";
            return name?.Trim() ?? "Sin asignar";
        }

        public static string GetPersonInitials(string person)
        {
            var canonical = NormalizeToCanonicalPerson(person);
            return canonical switch
            {
                "John" => "JS",
                "Karla" => "KH",
                "Isaias" => "IG",
                "Sotelo" => "ES",
                "Acalli" => "AC",
                "Andrade" => "AA",
                "Brian" => "BN",
                "Genaro" => "GM",
                "Neftali" => "NC",
                _ => canonical.Length >= 2 ? canonical[..2].ToUpperInvariant() : "NA"
            };
        }

        private static readonly HttpClient PresenceClient = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        public async Task SyncFromWebPresenceAsync()
        {
            try
            {
                var webUrl = ApplicationData.Current.LocalSettings.Values["Search.WebPresenceUrl"] as string;
                if (string.IsNullOrWhiteSpace(webUrl))
                {
                    webUrl = "http://localhost:3000/api/presence";
                }

                using var response = await PresenceClient.GetAsync(webUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("people", out var peopleEl) && peopleEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in peopleEl.EnumerateArray())
                        {
                            if (item.TryGetProperty("name", out var nameEl) &&
                                item.TryGetProperty("lastSeen", out var seenEl) &&
                                seenEl.ValueKind == JsonValueKind.String &&
                                DateTimeOffset.TryParse(seenEl.GetString(), out var dt))
                            {
                                var person = NormalizeToCanonicalPerson(nameEl.GetString());
                                _lastSeenUtc[person] = dt;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silencioso
            }
        }

        public bool IsUserOnline(string person)
        {
            var canonical = NormalizeToCanonicalPerson(person);
            var current = GetCurrentConfiguredUser();

            // El usuario de esta misma máquina siempre está online mientras la app esté abierta
            if (string.Equals(canonical, current, StringComparison.OrdinalIgnoreCase))
                return true;

            // Consultar timestamp en caché o archivo compartido
            if (_lastSeenUtc.TryGetValue(canonical, out var lastSeen))
            {
                return (DateTimeOffset.UtcNow - lastSeen).TotalMinutes <= 3.5;
            }

            // Consultar disco
            var fileLastSeen = ReadPersonLastSeenFromFile(canonical);
            if (fileLastSeen.HasValue)
            {
                _lastSeenUtc[canonical] = fileLastSeen.Value;
                return (DateTimeOffset.UtcNow - fileLastSeen.Value).TotalMinutes <= 3.5;
            }

            return false;
        }

        public string GetPresenceStatusText(string person)
        {
            var canonical = NormalizeToCanonicalPerson(person);
            var current = GetCurrentConfiguredUser();

            if (string.Equals(canonical, current, StringComparison.OrdinalIgnoreCase))
                return $"{canonical} · Conectado (Tú)";

            if (!_lastSeenUtc.TryGetValue(canonical, out var lastSeen))
            {
                var fileLastSeen = ReadPersonLastSeenFromFile(canonical);
                if (fileLastSeen.HasValue)
                {
                    _lastSeenUtc[canonical] = fileLastSeen.Value;
                    lastSeen = fileLastSeen.Value;
                }
            }

            if (lastSeen != default)
            {
                var diff = DateTimeOffset.UtcNow - lastSeen;
                if (diff.TotalMinutes <= 3.5)
                    return $"{canonical} · En línea 🟢";

                var local = lastSeen.ToLocalTime();
                if (diff.TotalMinutes < 60)
                {
                    var mins = Math.Max(1, (int)diff.TotalMinutes);
                    return $"{canonical} · Ausente (Hace {mins} min)";
                }
                if (local.Date == DateTime.Today)
                {
                    return $"{canonical} · Última conexión: Hoy {local:hh:mm tt}";
                }
                if (local.Date == DateTime.Today.AddDays(-1))
                {
                    return $"{canonical} · Última conexión: Ayer {local:hh:mm tt}";
                }
                return $"{canonical} · Última conexión: {local:dd/MM/yyyy hh:mm tt}";
            }

            return $"{canonical} · Desconectado";
        }

        public async Task RecordLocalHeartbeatAsync()
        {
            try
            {
                var user = GetCurrentConfiguredUser();
                _lastSeenUtc[user] = DateTimeOffset.UtcNow;
                _ = SyncFromWebPresenceAsync();

                var data = new Dictionary<string, string>();
                var targetPaths = GetHeartbeatFilePaths();

                // Intentar leer estados existentes
                foreach (var path in targetPaths)
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(path);
                            var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                            if (existing != null)
                            {
                                foreach (var kv in existing)
                                {
                                    if (DateTimeOffset.TryParse(kv.Value, out var dt))
                                    {
                                        _lastSeenUtc[kv.Key] = dt;
                                        data[kv.Key] = kv.Value;
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                // Escribir el propio
                data[user] = DateTimeOffset.UtcNow.ToString("o");

                var outJson = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                foreach (var path in targetPaths)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        await File.WriteAllTextAsync(path, outJson);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private DateTimeOffset? ReadPersonLastSeenFromFile(string person)
        {
            try
            {
                foreach (var path in GetHeartbeatFilePaths())
                {
                    if (File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (existing != null && existing.TryGetValue(person, out var val) && DateTimeOffset.TryParse(val, out var dt))
                        {
                            return dt;
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private IEnumerable<string> GetHeartbeatFilePaths()
        {
            var list = new List<string>();

            // 1. Dropbox compartido si está disponible
            if (!string.IsNullOrWhiteSpace(_dropboxFolder))
            {
                list.Add(Path.Combine(_dropboxFolder, ".anfeta_presence.json"));
            }

            // 2. LocalAppData respaldo local
            var localFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anfeta");
            list.Add(Path.Combine(localFolder, "presence.json"));

            return list;
        }
    }
}
