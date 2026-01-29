using System.Collections.Generic;

namespace Anfeta.UI.Models
{
    public sealed class ApiExecutionResult
    {
        public bool Ok { get; set; }
        public string PlainText { get; set; } = "";
        public List<string> Lines { get; set; } = new();
        public string? Error { get; set; }
    }
}
