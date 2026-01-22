using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
    public sealed class WeblabAuthClient
    {
        private readonly HttpClient _http;

        public WeblabAuthClient(HttpClient http)
        {
            _http = http;
        }

        public async Task<CheckDeviceResult> CheckDeviceAsync(string deviceId)
        {
            var payload = JsonSerializer.Serialize(new { deviceId });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var res = await _http.PostAsync("/api/auth/check-device", content);
            var json = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return CheckDeviceResult.FromError(json);

            using var doc = JsonDocument.Parse(json);

            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (ok)
            {
                var token = doc.RootElement.TryGetProperty("token", out var tEl) ? tEl.GetString() : null;
                return CheckDeviceResult.FromOk(token ?? "");
            }

            var needs = doc.RootElement.TryGetProperty("needsRegister", out var nrEl) && nrEl.GetBoolean();
            if (needs) return CheckDeviceResult.FromNeedsRegister();

            return CheckDeviceResult.FromError(json);
        }

        // FIRMA CORRECTA SEGÚN TU CAPTURA
        public async Task<AuthTokenResult> RegisterAsync(
            string email,
            string firstName,
            string lastName,
            string collaboratorId,
            string deviceId)
        {
            using var form = new MultipartFormDataContent();

            // Ojo: en tu captura se ve deviceId, pero en algunos backends lo llaman deviceId o deviceid.
            // Usaremos deviceId tal cual pediste.
            form.Add(new StringContent(email), "email");
            form.Add(new StringContent(firstName), "firstName");
            form.Add(new StringContent(lastName), "lastName");
            form.Add(new StringContent(collaboratorId), "collaboratorId");
            form.Add(new StringContent(deviceId), "deviceId");

            using var res = await _http.PostAsync("/api/auth/register", form);
            var json = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return AuthTokenResult.FromError(json);

            using var doc = JsonDocument.Parse(json);

            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok) return AuthTokenResult.FromError(json);

            var token = doc.RootElement.TryGetProperty("token", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
                return AuthTokenResult.FromError("Respuesta sin token.");

            return AuthTokenResult.FromOk(token!);
        }
    }

    public sealed record CheckDeviceResult(bool Ok, bool NeedsRegister, string? Token, string? RawError)
    {
        public static CheckDeviceResult FromOk(string token) => new(true, false, token, null);
        public static CheckDeviceResult FromNeedsRegister() => new(false, true, null, null);
        public static CheckDeviceResult FromError(string raw) => new(false, false, null, raw);
    }

    public sealed record AuthTokenResult(bool Ok, string? Token, string? RawError)
    {
        public static AuthTokenResult FromOk(string token) => new(true, token, null);
        public static AuthTokenResult FromError(string raw) => new(false, null, raw);
    }
}
