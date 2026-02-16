using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
    public sealed class SharedAuthHeaderHandler : DelegatingHandler
    {
        private readonly SharedAuthStateService _shared;

        public SharedAuthHeaderHandler(SharedAuthStateService shared)
        {
            _shared = shared;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            var isSharedAuthEndpoint =
                path.Contains("/api/shared-auth/login", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/api/shared-auth/refresh", StringComparison.OrdinalIgnoreCase);

            // ⬇️ DEBUG DE TODOS LOS HEADERS ANTES ⬇️
            Debug.WriteLine($"[SharedAuthHeaderHandler] ========== ANTES ==========");
            Debug.WriteLine($"[SharedAuthHeaderHandler] Path: {path}");
            Debug.WriteLine($"[SharedAuthHeaderHandler] IsAuthenticated: {_shared.IsAuthenticated}");

            foreach (var header in request.Headers)
            {
                Debug.WriteLine($"[SharedAuthHeaderHandler] Header ANTES: {header.Key} = {string.Join(", ", header.Value)}");
            }

            if (!isSharedAuthEndpoint && _shared.IsAuthenticated && !string.IsNullOrWhiteSpace(_shared.Token))
            {
                request.Headers.Remove("x-shared-token");
                request.Headers.TryAddWithoutValidation("x-shared-token", _shared.Token);
                Debug.WriteLine($"[SharedAuthHeaderHandler] ✅ x-shared-token inyectado");
            }
            else
            {
                Debug.WriteLine("[SharedAuthHeaderHandler] ❌ NO se inyectó token");
            }

            // ⬇️ DEBUG DE TODOS LOS HEADERS DESPUÉS ⬇️
            Debug.WriteLine($"[SharedAuthHeaderHandler] ========== DESPUÉS ==========");
            foreach (var header in request.Headers)
            {
                Debug.WriteLine($"[SharedAuthHeaderHandler] Header DESPUÉS: {header.Key} = {string.Join(", ", header.Value)}");
            }
            Debug.WriteLine($"[SharedAuthHeaderHandler] =====================================");

            return base.SendAsync(request, cancellationToken);
        }
    }
}