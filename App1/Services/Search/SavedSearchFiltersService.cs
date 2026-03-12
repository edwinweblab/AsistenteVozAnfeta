using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models.Search;

namespace Anfeta.UI.Services.Search
{
    public sealed class SavedSearchFiltersService
    {
        private readonly SavedSearchFiltersRepository _repository;

        public SavedSearchFiltersService(SavedSearchFiltersRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public async Task<List<SavedSearchFilter>> GetAllAsync(CancellationToken ct = default)
        {
            var items = await _repository.LoadAsync(ct);

            return items
                .OrderByDescending(x => x.IsPinned)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<SavedSearchFilter?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var items = await _repository.LoadAsync(ct);
            return items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public async Task AddOrUpdateAsync(SavedSearchFilter filter, CancellationToken ct = default)
        {
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            var items = await _repository.LoadAsync(ct);

            filter.Name = (filter.Name ?? string.Empty).Trim();
            filter.Description = (filter.Description ?? string.Empty).Trim();
            filter.Query = (filter.Query ?? string.Empty).Trim();
            filter.SortBy = string.IsNullOrWhiteSpace(filter.SortBy) ? "name_asc" : filter.SortBy.Trim();

            if (string.IsNullOrWhiteSpace(filter.Id))
                filter.Id = Guid.NewGuid().ToString("N");

            var now = DateTimeOffset.Now;
            var existingIndex = items.FindIndex(x => string.Equals(x.Id, filter.Id, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                filter.CreatedAt = items[existingIndex].CreatedAt;
                filter.UpdatedAt = now;
                items[existingIndex] = filter;
            }
            else
            {
                filter.CreatedAt = now;
                filter.UpdatedAt = now;
                items.Add(filter);
            }

            await _repository.SaveAsync(items, ct);
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            var items = await _repository.LoadAsync(ct);
            int removed = items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

            if (removed <= 0)
                return false;

            await _repository.SaveAsync(items, ct);
            return true;
        }

        public SearchExecutionOptions ToExecutionOptions(SavedSearchFilter filter)
        {
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            return new SearchExecutionOptions
            {
                Query = filter.Query ?? string.Empty,
                SortKey = string.IsNullOrWhiteSpace(filter.SortBy) ? "name_asc" : filter.SortBy,
                Match = new QueryMatchOptions
                {
                    MatchCase = filter.MatchCase,
                    MatchWholeWord = filter.MatchWholeWord,
                    MatchPath = filter.MatchPath,
                    UseRegex = filter.UseRegex,

                    MatchPrefix = filter.MatchPrefix,
                    MatchSuffix = filter.MatchSuffix,
                    IgnorePunctuation = filter.IgnorePunctuation,
                    IgnoreWhitespace = filter.IgnoreWhitespace,
                    MatchDiacritics = filter.MatchDiacritics
                }
            };
        }
        public async Task DeleteAllAsync()
        {
            var all = await GetAllAsync();

            foreach (var filter in all)
            {
                if (!string.IsNullOrWhiteSpace(filter.Id))
                    await DeleteAsync(filter.Id);
            }
        }
    }
}