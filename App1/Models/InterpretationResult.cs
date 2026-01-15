using System.Collections.Generic;

namespace Anfeta.UI.Models
{
    public sealed class InterpretationResult
    {
        public string Intent { get; set; } = "Unknown";
        public string Scope { get; set; } = "LOCAL"; // LOCAL | API
        public string? AppKey { get; set; }
        public string? Provider { get; set; }
        public double Confidence { get; set; } = 0.0;
        public Dictionary<string, object> Params { get; set; } = new();
        public bool NeedsConfirmation { get; set; } = true;
        public string? Reason { get; set; }
    }
}
