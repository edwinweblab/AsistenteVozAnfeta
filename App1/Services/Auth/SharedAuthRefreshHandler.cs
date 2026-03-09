using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
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
            InjectSharedHeaders(request);

            var response = await base.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && _sharedAuth.IsAuthenticated)
            {
                Debug.WriteLine("[SharedAuthHandler] 401 recibido → intentando refresh...");

                var refreshed = await _sharedAuth.TryRefreshAsync(ct);
                if (!refreshed)
                {
                    Debug.WriteLine("[SharedAuthHandler] Refresh fallido → sesión cerrada");
                    return response;
                }

                var retryRequest = await CloneRequestAsync(request);
                InjectSharedHeaders(retryRequest);

                response.Dispose();
                response = await base.SendAsync(retryRequest, ct);

                Debug.WriteLine($"[SharedAuthHandler] Retry → {response.StatusCode}");
            }

            return response;
        }

        private void InjectSharedHeaders(HttpRequestMessage request)
        {
            if (!string.IsNullOrWhiteSpace(_sharedAuth.Token))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _sharedAuth.Token);

                request.Headers.Remove("x-shared-token");
                request.Headers.TryAddWithoutValidation("x-shared-token", _sharedAuth.Token);

                Debug.WriteLine("[SharedAuthHandler] x-shared-token inyectado");
            }
        }

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