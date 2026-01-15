namespace Anfeta.UI.Models
{
    public sealed class OllamaStatus
    {
        public bool IsRunning { get; set; }
        public bool ModelAvailable { get; set; }
        public string Message { get; set; } = "";
    }
}
