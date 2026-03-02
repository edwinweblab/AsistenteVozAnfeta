using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using System.Linq;

namespace Anfeta.UI.Services.VoiceCommands;

public sealed class VoiceCommandsRepository
{
    private const string FileName = "voice_commands.json";

    private sealed class Store
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<VoiceCommand> Items { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public async Task<List<VoiceCommand>> LoadAsync()
    {
        try
        {
            var folder = ApplicationData.Current.LocalFolder;
            var item = await folder.TryGetItemAsync(FileName).AsTask().ConfigureAwait(false);
            if (item is not StorageFile file) return new List<VoiceCommand>();

            var json = await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return new List<VoiceCommand>();

            var store = JsonSerializer.Deserialize<Store>(json, JsonOpts);
            return store?.Items ?? new List<VoiceCommand>();
        }
        catch
        {
            return new List<VoiceCommand>();
        }
    }

    public async Task SaveAsync(IEnumerable<VoiceCommand> items)
    {
        var store = new Store
        {
            SchemaVersion = 1,
            UpdatedAtUtc = DateTime.UtcNow,
            Items = items.ToList()
        };

        var json = JsonSerializer.Serialize(store, JsonOpts);

        var folder = ApplicationData.Current.LocalFolder;
        var file = await folder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(file, json);
    }
}