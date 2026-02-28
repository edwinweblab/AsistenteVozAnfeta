namespace Anfeta.UI.Services.VoiceCommands
{
    public sealed class VoiceListenResult
    {
        public string? Phrase { get; init; }
        public string? CommandName { get; init; }
        public string? Token { get; init; }
        public bool Matched => !string.IsNullOrWhiteSpace(Token);
    }
}