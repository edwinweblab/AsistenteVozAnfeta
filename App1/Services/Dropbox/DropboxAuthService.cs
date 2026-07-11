using Anfeta.UI.Models;
using Dropbox.Api;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;

namespace Anfeta.UI.Services.Dropbox
{
    public sealed class DropboxAuthService
    {
        // La App Key es un identificador público. No se incluye ningún App Secret.
        public const string AppKey = "98zsk2p74wgempw";

        private const string AuthorizeEndpoint = "https://www.dropbox.com/oauth2/authorize";
        private const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly DropboxCredentialStore _credentialStore;

        private string? _pendingCodeVerifier;

        public DropboxAuthService(
            IHttpClientFactory httpClientFactory,
            DropboxCredentialStore credentialStore)
        {
            _httpClientFactory = httpClientFactory;
            _credentialStore = credentialStore;
        }

        public async Task BeginAuthorizationAsync()
        {
            _pendingCodeVerifier = CreateCodeVerifier();
            var challenge = CreateCodeChallenge(_pendingCodeVerifier);

            var authorizeUri = new Uri(
                $"{AuthorizeEndpoint}" +
                $"?client_id={Uri.EscapeDataString(AppKey)}" +
                "&response_type=code" +
                "&token_access_type=offline" +
                $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                "&code_challenge_method=S256");

            var launched = await Launcher.LaunchUriAsync(authorizeUri);
            if (!launched)
            {
                _pendingCodeVerifier = null;
                throw new InvalidOperationException(
                    "No se pudo abrir el navegador para conectar Dropbox.");
            }
        }

        public async Task<DropboxConnectionInfo> CompleteAuthorizationAsync(
            string authorizationCode,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(authorizationCode))
                throw new ArgumentException("Pega el código de autorización de Dropbox.");

            if (string.IsNullOrWhiteSpace(_pendingCodeVerifier))
            {
                throw new InvalidOperationException(
                    "La autorización ya expiró o ANFETA se reinició. Presiona Conectar Dropbox otra vez.");
            }

            var http = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = authorizationCode.Trim(),
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = AppKey,
                    ["code_verifier"] = _pendingCodeVerifier
                })
            };

            using var response = await http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ParseDropboxError(json, response.StatusCode));

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var accessToken = GetRequiredString(root, "access_token");
            var refreshToken = GetRequiredString(root, "refresh_token");

            await _credentialStore.SaveRefreshTokenAsync(refreshToken);
            _pendingCodeVerifier = null;

            return await ReadAccountAsync(accessToken);
        }

        public async Task<DropboxConnectionInfo> TestConnectionAsync(
            CancellationToken cancellationToken = default)
        {
            var accessToken = await GetAccessTokenAsync(cancellationToken);
            return await ReadAccountAsync(accessToken);
        }

        public async Task<bool> HasSavedConnectionAsync()
            => !string.IsNullOrWhiteSpace(await _credentialStore.GetRefreshTokenAsync());

        public async Task DisconnectAsync()
        {
            _pendingCodeVerifier = null;
            await _credentialStore.ClearAsync();
        }

        public async Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default)
        {
            var refreshToken = await _credentialStore.GetRefreshTokenAsync();

            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException("Dropbox no está vinculado.");

            var http = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = AppKey
                })
            };

            using var response = await http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ParseDropboxError(json, response.StatusCode));

            using var document = JsonDocument.Parse(json);
            return GetRequiredString(document.RootElement, "access_token");
        }

        private static async Task<DropboxConnectionInfo> ReadAccountAsync(string accessToken)
        {
            using var client = new DropboxClient(accessToken);
            var account = await client.Users.GetCurrentAccountAsync();

            return new DropboxConnectionInfo(
                IsConnected: true,
                DisplayName: account.Name?.DisplayName ?? "Cuenta Dropbox",
                Email: account.Email ?? string.Empty,
                AccountId: account.AccountId ?? string.Empty,
                Message: "Dropbox vinculado correctamente.");
        }

        private static string CreateCodeVerifier()
        {
            var bytes = RandomNumberGenerator.GetBytes(64);
            return Base64UrlEncode(bytes);
        }

        private static string CreateCodeChallenge(string verifier)
        {
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        private static string GetRequiredString(JsonElement root, string propertyName)
        {
            if (root.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }

            throw new InvalidOperationException(
                $"Dropbox no devolvió el campo requerido '{propertyName}'.");
        }

        private static string ParseDropboxError(string json, System.Net.HttpStatusCode statusCode)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var description =
                    TryReadString(root, "error_description") ??
                    TryReadString(root, "error_summary") ??
                    TryReadString(root, "error");

                if (!string.IsNullOrWhiteSpace(description))
                    return $"Dropbox rechazó la solicitud: {description}";
            }
            catch
            {
                // Se usa el mensaje genérico de abajo.
            }

            return $"Dropbox respondió con HTTP {(int)statusCode}.";
        }

        private static string? TryReadString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object => value.ToString(),
                _ => null
            };
        }
    }
}
