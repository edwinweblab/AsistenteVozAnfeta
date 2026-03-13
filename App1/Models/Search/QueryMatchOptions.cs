namespace Anfeta.UI.Models.Search
{
    public sealed class QueryMatchOptions
    {
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
    }
}