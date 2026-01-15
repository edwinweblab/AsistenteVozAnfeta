using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;

namespace Anfeta.UI.Services
{
    public interface ICommandInterpretationService
    {
        Task<InterpretationResponse> InterpretRawAsync(string recognizedText, CancellationToken ct = default);
    }
}
