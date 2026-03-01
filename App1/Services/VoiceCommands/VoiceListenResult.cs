namespace Anfeta.UI.Services.VoiceCommands
{
    public sealed class VoiceListenResult
    {
        public string? Phrase { get; init; }

        public bool Matched { get; init; }

        public string? CommandName { get; init; }

        public string? Token { get; init; }

        // Nuevas para multi-palabra
        public string? ArgsText { get; init; }

        public string? MatchedSynonym { get; init; }

        public string? ExecutedSearchText { get; init; }
    }
}