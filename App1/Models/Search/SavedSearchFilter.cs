using System;

namespace Anfeta.UI.Models.Search
{
    public sealed class SavedSearchFilter
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
        public Anfeta.UI.Models.SearchCriteriaState? Criteria { get; set; }

        public bool MatchCase { get; set; }
        public bool MatchWholeWord { get; set; }
        public bool MatchPath { get; set; }
        public bool UseRegex { get; set; }

        // Futuro / opcionales
        public bool MatchPrefix { get; set; }
        public bool MatchSuffix { get; set; }
        public bool IgnorePunctuation { get; set; }
        public bool IgnoreWhitespace { get; set; }
        public bool MatchDiacritics { get; set; }

        public string SortBy { get; set; } = "name_asc";
        public bool IsPinned { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    }
}
