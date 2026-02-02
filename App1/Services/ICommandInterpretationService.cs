using Anfeta.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public interface ICommandInterpretationService
    {
        Task<InterpretationResponse> InterpretRawAsync(string recognizedText, CancellationToken ct = default);
    }
}
