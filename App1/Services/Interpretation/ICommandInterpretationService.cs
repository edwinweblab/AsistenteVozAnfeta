using Anfeta.UI.Models.Interpretation;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Interpretation
{
    public interface ICommandInterpretationService
    {
        Task<InterpretationResponse> InterpretRawAsync(string recognizedText, CancellationToken ct = default);
    }
}
