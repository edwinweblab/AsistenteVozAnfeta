using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Search
{
    public sealed class PrioritySeenEntry
    {
        public string Key { get; set; } = string.Empty;
        public string SeenBy { get; set; } = string.Empty;
        public DateTimeOffset SeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public string Title { get; set; } = string.Empty;
    }

    public static class PrioritySeenTracker
    {
        private const string FILE_NAME = "anfeta_priority_00_vistos.json";
        private const string LS_DropboxRoot = "LS_DropboxRoot";

        private static readonly ConcurrentDictionary<string, PrioritySeenEntry> _seenEntries =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly SemaphoreSlim _ioLock = new(1, 1);
        private static DateTime _lastLoadedUtc = DateTime.MinValue;
        private static bool _initialized;

        public static event Action? OnSeenChanged;

        private static string NormalizeKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var clean = raw.Trim().Replace("-", "").ToLowerInvariant();
            if (clean.StartsWith("notion:", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(7);
            return clean;
        }

        public static bool IsSeen(string key, out PrioritySeenEntry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(key)) return false;
            var norm = NormalizeKey(key);
            return _seenEntries.TryGetValue(norm, out entry);
        }

        public static async Task MarkSeenAsync(string key, string seenBy, string title)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var norm = NormalizeKey(key);
            var entry = new PrioritySeenEntry
            {
                Key = norm,
                SeenBy = string.IsNullOrWhiteSpace(seenBy) ? "Destinatario" : seenBy.Trim(),
                SeenAtUtc = DateTimeOffset.UtcNow,
                Title = title ?? string.Empty
            };

            _seenEntries[norm] = entry;
            await SaveEntriesAsync();
            OnSeenChanged?.Invoke();
        }

        public static async Task UnmarkSeenAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var norm = NormalizeKey(key);
            if (_seenEntries.TryRemove(norm, out _))
            {
                await SaveEntriesAsync();
                OnSeenChanged?.Invoke();
            }
        }

        public static async Task EnsureLoadedAsync(bool force = false)
        {
            if (_initialized && !force && (DateTime.UtcNow - _lastLoadedUtc).TotalSeconds < 5)
                return;

            await _ioLock.WaitAsync();
            try
            {
                if (_initialized && !force && (DateTime.UtcNow - _lastLoadedUtc).TotalSeconds < 5)
                    return;

                var dropboxPath = GetDropboxFilePath();
                var localPath = GetLocalFilePath();

                var loadedAny = false;

                // Cargar primero de Dropbox si existe
                if (!string.IsNullOrWhiteSpace(dropboxPath) && File.Exists(dropboxPath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(dropboxPath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var list = JsonSerializer.Deserialize<List<PrioritySeenEntry>>(json);
                            if (list != null)
                            {
                                foreach (var item in list)
                                {
                                    var n = NormalizeKey(item.Key);
                                    if (!string.IsNullOrWhiteSpace(n))
                                    {
                                        if (!_seenEntries.TryGetValue(n, out var existing) || item.SeenAtUtc > existing.SeenAtUtc)
                                        {
                                            item.Key = n;
                                            _seenEntries[n] = item;
                                        }
                                    }
                                }
                                loadedAny = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PRIORITY_SEEN] Error reading Dropbox: {ex.Message}");
                    }
                }

                // Cargar también de local
                if (File.Exists(localPath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(localPath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var list = JsonSerializer.Deserialize<List<PrioritySeenEntry>>(json);
                            if (list != null)
                            {
                                foreach (var item in list)
                                {
                                    var n = NormalizeKey(item.Key);
                                    if (!string.IsNullOrWhiteSpace(n))
                                    {
                                        if (!_seenEntries.TryGetValue(n, out var existing) || item.SeenAtUtc > existing.SeenAtUtc)
                                        {
                                            item.Key = n;
                                            _seenEntries[n] = item;
                                        }
                                    }
                                }
                                loadedAny = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PRIORITY_SEEN] Error reading Local: {ex.Message}");
                    }
                }

                _initialized = true;
                _lastLoadedUtc = DateTime.UtcNow;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static async Task SaveEntriesAsync()
        {
            await _ioLock.WaitAsync();
            try
            {
                var list = new List<PrioritySeenEntry>(_seenEntries.Values);
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });

                var localPath = GetLocalFilePath();
                try
                {
                    await File.WriteAllTextAsync(localPath, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PRIORITY_SEEN] Error writing local: {ex.Message}");
                }

                var dropboxPath = GetDropboxFilePath();
                if (!string.IsNullOrWhiteSpace(dropboxPath))
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(dropboxPath);
                        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        await File.WriteAllTextAsync(dropboxPath, json);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PRIORITY_SEEN] Error writing Dropbox: {ex.Message}");
                    }
                }

                _lastLoadedUtc = DateTime.UtcNow;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static string GetDropboxFilePath()
        {
            try
            {
                var root = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    return Path.Combine(root.Trim(), FILE_NAME);
                }
            }
            catch { }
            return string.Empty;
        }

        private static string GetLocalFilePath()
        {
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, FILE_NAME);
        }
    }
}
