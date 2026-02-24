using Anfeta.UI.Services.Auth;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    /// Handler HTTP que intercepta 401 y renueva el token shared automáticamente.
    /// Si el refresh falla, la sesión se cierra y el usuario debe re-autenticarse.
    public sealed class SharedAuthRefreshHandler : DelegatingHandler
    {
        private readonly SharedAuthStateService _sharedAuth;

        public SharedAuthRefreshHandler(SharedAuthStateService sharedAuth)
        {
            _sharedAuth = sharedAuth;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            // Inyectar token actual si existe
            if (!string.IsNullOrWhiteSpace(_sharedAuth.Token))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _sharedAuth.Token);
            }

            var response = await base.SendAsync(request, ct);

            // Si el servidor devuelve 401, intentar refresh una vez
            if (response.StatusCode == HttpStatusCode.Unauthorized && _sharedAuth.IsAuthenticated)
            {
                Debug.WriteLine("[SharedAuthHandler] 401 recibido → intentando refresh...");

                var refreshed = await _sharedAuth.TryRefreshAsync(ct);
                if (!refreshed)
                {
                    Debug.WriteLine("[SharedAuthHandler] Refresh fallido → sesión cerrada");
                    return response;
                }

                // Reintentar la request original con el nuevo token
                var retryRequest = await CloneRequestAsync(request);
                retryRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _sharedAuth.Token);

                response.Dispose();
                response = await base.SendAsync(retryRequest, ct);

                Debug.WriteLine($"[SharedAuthHandler] Retry → {response.StatusCode}");
            }

            return response;
        }

        /// Clona la request para poder reenviarla (HttpRequestMessage no es reutilizable).
        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
        {
            var clone = new HttpRequestMessage(original.Method, original.RequestUri);

            foreach (var header in original.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (original.Content != null)
            {
                var bytes = await original.Content.ReadAsByteArrayAsync();
                clone.Content = new ByteArrayContent(bytes);

                foreach (var header in original.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}