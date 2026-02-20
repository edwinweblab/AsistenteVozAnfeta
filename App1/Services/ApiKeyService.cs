using System;
using System.Threading.Tasks;
using Anfeta.UI.Data;

namespace Anfeta.UI.Services
{
    public class ApiKeyService
    {
        private readonly ApiKeyRepository _repo;

        public ApiKeyService(ApiKeyRepository repo)
        {
            _repo = repo;
        }

        public event EventHandler? KeysChanged;

        public void NotifyKeysChanged()
            => KeysChanged?.Invoke(this, EventArgs.Empty);

        public async Task<string?> GetActiveGroqKeyAsync()
        {
            var active = await _repo.GetActiveAsync("groq");
            return active.apiKey;
        }
    }
}
