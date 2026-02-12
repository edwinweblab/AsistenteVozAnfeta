using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
    public sealed class WeblabSharedAuthClient
    {
        private readonly HttpClient _http;

        public WeblabSharedAuthClient(HttpClient http)
        {
            _http = http;
        }

        // POST /api/shared-auth/login
        // Body: { user, pass }
        // Resp: { token, refreshToken }
        public async Task<SharedAuthLoginResult> LoginAsync(string user, string pass, CancellationToken ct = default)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { user, pass });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var res = await _http.PostAsync("/api/shared-auth/login", content, ct);
                var json = await res.Content.ReadAsStringAsync(ct);

                if (!res.IsSuccessStatusCode)
                    return SharedAuthLoginResult.FromError(json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var token = root.TryGetProperty("token", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString()
                    : null;

                var refresh = root.TryGetProperty("refreshToken", out var rEl) && rEl.ValueKind == JsonValueKind.String
                    ? rEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(refresh))
                    return SharedAuthLoginResult.FromError("Respuesta sin token o refreshToken.");

                return SharedAuthLoginResult.FromOk(token!, refresh!);
            }
            catch (OperationCanceledException)
            {
                return SharedAuthLoginResult.FromError("Operación cancelada.");
            }
            catch (Exception ex)
            {
                return SharedAuthLoginResult.FromError(ex.Message);
            }
        }

        // POST /api/shared-auth/refresh
        // Header: x-shared-refresh: <refreshToken>
        // Resp: { token, refreshToken }
        public async Task<SharedAuthLoginResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/shared-auth/refresh");
                req.Headers.TryAddWithoutValidation("x-shared-refresh", refreshToken);

                using var res = await _http.SendAsync(req, ct);
                var json = await res.Content.ReadAsStringAsync(ct);

                if (!res.IsSuccessStatusCode)
                    return SharedAuthLoginResult.FromError(json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var token = root.TryGetProperty("token", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString()
                    : null;

                var refresh = root.TryGetProperty("refreshToken", out var rEl) && rEl.ValueKind == JsonValueKind.String
                    ? rEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(refresh))
                    return SharedAuthLoginResult.FromError("Respuesta sin token o refreshToken.");

                return SharedAuthLoginResult.FromOk(token!, refresh!);
            }
            catch (OperationCanceledException)
            {
                return SharedAuthLoginResult.FromError("Operación cancelada.");
            }
            catch (Exception ex)
            {
                return SharedAuthLoginResult.FromError(ex.Message);
            }
        }
    }

    public sealed record SharedAuthLoginResult(bool Ok, string? Token, string? RefreshToken, string? RawError)
    {
        public static SharedAuthLoginResult FromOk(string token, string refreshToken)
            => new(true, token, refreshToken, null);

        public static SharedAuthLoginResult FromError(string raw)
            => new(false, null, null, raw);
    }
}
