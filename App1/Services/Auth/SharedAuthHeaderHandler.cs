using System;
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            // No meter token en login/refresh
            var isSharedAuthEndpoint =
                path.Contains("/api/shared-auth/login", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/api/shared-auth/refresh", StringComparison.OrdinalIgnoreCase);

            if (!isSharedAuthEndpoint && _shared.IsAuthenticated && !string.IsNullOrWhiteSpace(_shared.Token))
            {
                request.Headers.Remove("x-shared-token");
                request.Headers.TryAddWithoutValidation("x-shared-token", _shared.Token);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
