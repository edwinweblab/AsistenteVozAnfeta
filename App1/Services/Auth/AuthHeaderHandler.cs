using Anfeta.UI.Data;
using System;
using System.Diagnostics;
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
            // Inyectar token Bearer
            var token = _auth.Token;
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                Debug.WriteLine($"[AuthHeaderHandler] Bearer token inyectado: {token.Substring(0, Math.Min(20, token.Length))}...");
            }
            else
            {
                Debug.WriteLine("[AuthHeaderHandler] WARNING: Token vacío o nulo");
            }

            // Inyectar x-device-id
            var deviceId = DeviceRepository.EnsureActiveDevice();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                request.Headers.TryAddWithoutValidation("x-device-id", deviceId);
                Debug.WriteLine($"[AuthHeaderHandler] x-device-id inyectado: {deviceId}");
            }
            else
            {
                Debug.WriteLine("[AuthHeaderHandler] WARNING: No se pudo obtener deviceId");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}