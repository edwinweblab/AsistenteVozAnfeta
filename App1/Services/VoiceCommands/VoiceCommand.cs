using System;
using System.Collections.Generic;

namespace Anfeta.UI.Services.VoiceCommands;

public sealed class VoiceCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public List<string> Synonyms { get; set; } = new();
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}