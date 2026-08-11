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

        // Un solo lector/escritor pesado del índice a la vez. Antes varias
        // pestañas podían serializar el JSON completo simultáneamente.
        private static readonly SemaphoreSlim PersistenceGate = new(1, 1);

        private sealed class IndexManifest
        {
            public int Version { get; set; } = INDEX_VERSION;
            public string RootPath { get; set; } = "";
            public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        }

        public static async Task SaveAsync(
            string rootPath,
            List<SearchResultRow> items,
            CancellationToken ct)
        {
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("Refusing to persist empty index.");

            ct.ThrowIfCancellationRequested();

            // La lista puede seguir cambiando en memoria mientras se persiste.
            // Copiamos las referencias una vez y hacemos el trabajo de JSON
            // fuera del hilo de UI para evitar tirones al guardar miles de filas.
            var snapshot = new List<SearchResultRow>(items);

            await PersistenceGate.WaitAsync(ct);

            try
            {
                var folder = ApplicationData.Current.LocalFolder;

                var json = await Task.Run(
                    () => JsonSerializer.Serialize(
                        snapshot,
                        new JsonSerializerOptions
                        {
                            WriteIndented = false
                        }),
                    ct);

                ct.ThrowIfCancellationRequested();

                var indexFile = await folder.CreateFileAsync(
                    INDEX_FILE,
                    CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteTextAsync(indexFile, json);

                var manifest = new IndexManifest
                {
                    RootPath = rootPath?.Trim() ?? "",
                    CreatedAt = DateTimeOffset.Now
                };

                var manifestJson = JsonSerializer.Serialize(manifest);

                var manifestFile = await folder.CreateFileAsync(
                    MANIFEST_FILE,
                    CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteTextAsync(manifestFile, manifestJson);
            }
            finally
            {
                PersistenceGate.Release();
            }
        }

        public static async Task<(bool ok, string root, List<SearchResultRow>? items)>
            TryLoadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            await PersistenceGate.WaitAsync(ct);

            try
            {
                var folder = ApplicationData.Current.LocalFolder;

                var mf = await folder.TryGetItemAsync(MANIFEST_FILE) as StorageFile;
                var idx = await folder.TryGetItemAsync(INDEX_FILE) as StorageFile;

                if (mf == null || idx == null)
                    return (false, "", null);

                var mfJson = await FileIO.ReadTextAsync(mf);

                var manifest = await Task.Run(
                    () => JsonSerializer.Deserialize<IndexManifest>(mfJson),
                    ct);

                if (manifest == null || manifest.Version != INDEX_VERSION)
                    return (false, "", null);

                var idxJson = await FileIO.ReadTextAsync(idx);

                var items = await Task.Run(
                    () => JsonSerializer.Deserialize<List<SearchResultRow>>(idxJson)
                          ?? new List<SearchResultRow>(),
                    ct);

                return (true, manifest.RootPath ?? "", items);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (false, "", null);
            }
            finally
            {
                PersistenceGate.Release();
            }
        }

        public static async Task ClearAsync()
        {
            await PersistenceGate.WaitAsync();

            try
            {
                var folder = ApplicationData.Current.LocalFolder;

                var mf = await folder.TryGetItemAsync(MANIFEST_FILE);
                if (mf != null)
                    await mf.DeleteAsync();

                var idx = await folder.TryGetItemAsync(INDEX_FILE);
                if (idx != null)
                    await idx.DeleteAsync();
            }
            finally
            {
                PersistenceGate.Release();
            }
        }

        public static bool RootExists(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return false;

            return Directory.Exists(rootPath);
        }
    }
}
