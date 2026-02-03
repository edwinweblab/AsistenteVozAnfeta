using Anfeta.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public interface IOllamaHealthService
    {
        Task<OllamaStatus> CheckAsync(string modelName, CancellationToken ct = default);
    }
}
