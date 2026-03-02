using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Groq
{
    public class GroqAuthHeaderHandler : DelegatingHandler
    {
        private readonly ApiKeyService _keys;

        public GroqAuthHeaderHandler(ApiKeyService keys)
        {
            _keys = keys;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = await _keys.GetActiveGroqKeyAsync();

            // Si no hay key activa, deja que falle claramente
            if (string.IsNullOrWhiteSpace(key))
                throw new HttpRequestException("No hay API key activa para Groq.");

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
