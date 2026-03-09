using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Anfeta.UI.Models.Weblab;

namespace Anfeta.UI.Services.Speech
{
    public static class LocalIndexPersistence
    {
        private const string INDEX_FILE = "index_cache.json";
        private const string MANIFEST_FILE = "index_manifest.json";
        private const int INDEX_VERSION = 1;

        private sealed class IndexManifest
        {
            public int Version { get; set; } = INDEX_VERSION;
            public string RootPath { get; set; } = "";
            public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        }

        public static async Task SaveAsync(string rootPath, List<SearchResultRow> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("Refusing to persist empty index.");
            ct.ThrowIfCancellationRequested();

            var folder = ApplicationData.Current.LocalFolder;

            // Índice
            var indexFile = await folder.CreateFileAsync(INDEX_FILE, CreationCollisionOption.ReplaceExisting);
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = false });
            await FileIO.WriteTextAsync(indexFile, json);

            // Manifest
            var manifest = new IndexManifest
            {
                RootPath = rootPath?.Trim() ?? "",
                CreatedAt = DateTimeOffset.Now
            };

            var manifestFile = await folder.CreateFileAsync(MANIFEST_FILE, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(manifestFile, JsonSerializer.Serialize(manifest));
        }

        public static async Task<(bool ok, string root, List<SearchResultRow>? items)> TryLoadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var folder = ApplicationData.Current.LocalFolder;

                var mf = await folder.TryGetItemAsync(MANIFEST_FILE) as StorageFile;
                var idx = await folder.TryGetItemAsync(INDEX_FILE) as StorageFile;

                if (mf == null || idx == null)
                    return (false, "", null);

                var mfJson = await FileIO.ReadTextAsync(mf);
                var manifest = JsonSerializer.Deserialize<IndexManifest>(mfJson);

                if (manifest == null || manifest.Version != INDEX_VERSION)
                    return (false, "", null);

                var idxJson = await FileIO.ReadTextAsync(idx);
                var items = JsonSerializer.Deserialize<List<SearchResultRow>>(idxJson) ?? new List<SearchResultRow>();

                return (true, manifest.RootPath ?? "", items);
            }
            catch
            {
                return (false, "", null);
            }
        }

        public static async Task ClearAsync()
        {
            var folder = ApplicationData.Current.LocalFolder;

            var mf = await folder.TryGetItemAsync(MANIFEST_FILE);
            if (mf != null) await mf.DeleteAsync();

            var idx = await folder.TryGetItemAsync(INDEX_FILE);
            if (idx != null) await idx.DeleteAsync();
        }

        public static bool RootExists(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return false;
            return Directory.Exists(rootPath);
        }
    }
}