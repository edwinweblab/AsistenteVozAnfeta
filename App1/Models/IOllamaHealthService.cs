using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;

namespace Anfeta.UI.Services
{
    public interface IOllamaHealthService
    {
        Task<OllamaStatus> CheckAsync(string modelName, CancellationToken ct = default);
    }
}
