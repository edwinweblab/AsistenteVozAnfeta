using System.Collections.Generic;

namespace Anfeta.UI.Models
{
    public sealed class SearchTabState
    {
        public string Header { get; set; } = "Buscar";
        public string Query { get; set; } = "";
        public string CurrentFolder { get; set; } = "";
        public SearchCriteriaState? Criteria { get; set; }
    }

    public sealed class SearchTabsWorkspace
    {
        public int Version { get; set; } = 1;   // ✅ nuevo
        public int SelectedIndex { get; set; } = 0;
        public List<SearchTabState> Tabs { get; set; } = new();
    }
}
