using System.Collections.Generic;
using Anfeta.UI.Models.Weblab;

namespace Anfeta.UI.Services.Activity
{
    public enum EditFlowPhase
    {
        None,
        SearchingActivity,
        SelectingActivity,
        AskingField,
        AskingValue,
        Confirming
    }

    public sealed class ActivityEditState
    {
        public string? SearchText { get; set; }
        public List<CachedActivityItem> SearchResults { get; set; } = new();
        public CachedActivityItem? SelectedActivity { get; set; }

        public string? FieldToEdit { get; set; }
        public string? NewValueRaw { get; set; }

        public EditFlowPhase Phase { get; set; } = EditFlowPhase.None;

        public void Reset()
        {
            SearchText = null;
            SearchResults.Clear();
            SelectedActivity = null;
            FieldToEdit = null;
            NewValueRaw = null;
            Phase = EditFlowPhase.None;
        }
    }
}