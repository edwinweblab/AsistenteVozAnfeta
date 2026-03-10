using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Anfeta.UI.Models.Search;

namespace Anfeta.UI.Services.Search
{
    public sealed class SavedSearchFiltersRepository
    {
        private const string FileName = "saved_search_filters.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public async Task<List<SavedSearchFilter>> LoadAsync(CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile file = await folder.CreateFileAsync(
                    FileName,
                    CreationCollisionOption.OpenIfExists);

                ct.ThrowIfCancellationRequested();

                string json = await FileIO.ReadTextAsync(file);

                if (string.IsNullOrWhiteSpace(json))
                    return new List<SavedSearchFilter>();

                var items = JsonSerializer.Deserialize<List<SavedSearchFilter>>(json, JsonOptions);
                return items ?? new List<SavedSearchFilter>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new List<SavedSearchFilter>();
            }
        }

        public async Task SaveAsync(IEnumerable<SavedSearchFilter> filters, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            StorageFolder folder = ApplicationData.Current.LocalFolder;
            StorageFile file = await folder.CreateFileAsync(
                FileName,
                CreationCollisionOption.ReplaceExisting);

            string json = JsonSerializer.Serialize(filters, JsonOptions);

            ct.ThrowIfCancellationRequested();
            await FileIO.WriteTextAsync(file, json);
        }

        public async Task DeleteAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            StorageFolder folder = ApplicationData.Current.LocalFolder;

            try
            {
                var item = await folder.TryGetItemAsync(FileName);
                if (item is StorageFile file)
                {
                    ct.ThrowIfCancellationRequested();
                    await file.DeleteAsync();
                }
            }
            catch (FileNotFoundException)
            {
            }
        }
    }
}