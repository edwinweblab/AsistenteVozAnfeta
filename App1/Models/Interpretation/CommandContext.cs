using System;
using System.Collections.Generic;

namespace Anfeta.UI.Models.Interpretation
{
    /// <summary>Estado actual del sistema para construcción de prompts</summary>
    public sealed class CommandContext
    {
        public ActiveApp? CurrentApp { get; set; }
        public List<CommandHistoryEntry> RecentCommands { get; set; } = new();
        public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>App activa en el sistema</summary>
    public sealed class ActiveApp
    {
        public string AppKey { get; set; } = "";
        public string Category { get; set; } = "";
        public DateTime OpenedAt { get; set; }
        public List<string> Capabilities { get; set; } = new();
    }

    /// <summary>Entrada del historial de comandos</summary>
    public sealed class CommandHistoryEntry
    {
        public string Intent { get; set; } = "";
        public string? AppKey { get; set; }
        public DateTime ExecutedAt { get; set; }
    }
}