namespace Anfeta.UI.Models
{
    public sealed class SearchCriteriaState
    {
        public string Source { get; set; } = "All";
        public string Base { get; set; } = "";
        public string Payment { get; set; } = "";
        public string? Extension { get; set; }
        public bool Programs { get; set; }
        public bool Bookmarks { get; set; }
        public bool Folders { get; set; }
        public string Grouping { get; set; } = "None";
        public string Sort { get; set; } = "name_asc";
        public Search.QueryMatchOptions? Match { get; set; }
    }
}
