using System.Collections.Generic;
using System.Threading.Tasks;
using Anfeta.UI.Models;

namespace Anfeta.UI.Services.VoiceCommands;

public interface IVoicePostActionService
{
    void ArmSpeakTopUrls(int maxItems = 6);
    void NotifySearchResults(IReadOnlyList<SearchResultRow> results);

    Task StopAllAsync();
}