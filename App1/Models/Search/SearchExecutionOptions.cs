namespace Anfeta.UI.Models.Search
{
    public sealed class SearchExecutionOptions
    {
        public string Query { get; set; } = string.Empty;
        public string SortKey { get; set; } = "name_asc";

        public bool OnlyFolders { get; set; }
        public string? ExtFilter { get; set; }

        public QueryMatchOptions Match { get; set; } = new();
    }
}