// Models/VoiceHistoryEntry.cs
namespace Anfeta.UI.Models
{
    public sealed class VoiceHistoryEntry
    {
        public string InputText { get; init; } = "";
        public string Category { get; init; } = "";
        public string Time { get; init; } = "";
    }
}