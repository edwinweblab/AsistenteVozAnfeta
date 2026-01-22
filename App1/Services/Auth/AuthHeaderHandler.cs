using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
    public sealed class AuthHeaderHandler : DelegatingHandler
    {
        private readonly AuthStateService _auth;

        public AuthHeaderHandler(AuthStateService auth)
        {
            _auth = auth;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var token = _auth.Token;

            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
