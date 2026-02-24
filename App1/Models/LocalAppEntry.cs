namespace Anfeta.UI.Models
{
    public sealed class LocalAppEntry
    {
        public string AppKey { get; set; } = "";
        public string FriendlyName { get; set; } = "";
        public string Category { get; set; } = "";
        public string ExecutableName { get; set; } = "";
        public string? ExecutablePath { get; set; }
        public bool Enabled { get; set; }
        public string? Source { get; set; }  // seed | detected | manual
    }
}