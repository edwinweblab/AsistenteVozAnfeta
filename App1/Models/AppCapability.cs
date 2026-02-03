using System.Collections.Generic;

namespace Anfeta.UI.Models
{
    /// <summary>Definición de capacidades de una app</summary>
    public sealed class AppCapability
    {
        public string AppKey { get; set; } = "";
        public string ExecutableName { get; set; } = "";
        public string Category { get; set; } = "";
        public List<string> Capabilities { get; set; } = new();
        public string FriendlyName { get; set; } = "";
        public List<string> Synonyms { get; set; } = new();
    }
}