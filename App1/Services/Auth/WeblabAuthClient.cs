using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
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
            string deviceId,
            string phone)
        {
            using var form = new MultipartFormDataContent();

            // Ojo: en tu captura se ve deviceId, pero en algunos backends lo llaman deviceId o deviceid.
            // Usaremos deviceId tal cual pediste.
            form.Add(new StringContent(email), "email");
            form.Add(new StringContent(firstName), "firstName");
            form.Add(new StringContent(lastName), "lastName");
            form.Add(new StringContent(collaboratorId), "collaboratorId");
            form.Add(new StringContent(deviceId), "deviceId");
            form.Add(new StringContent(phone), "phone");


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

        /// <summary>
        /// Obtiene info del usuario actual desde /api/auth/me
        /// Devuelve: (Ok, Email, Name, CollaboratorId)
        /// </summary>
        public async Task<(bool Ok, string? Email, string? Name, string? CollaboratorId)> GetCurrentUserAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/auth/me", ct);
                if (!resp.IsSuccessStatusCode)
                    return (false, null, null, null);

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("user", out var user))
                    return (false, null, null, null);

                var email = user.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;

                var name = user.TryGetProperty("firstName", out var fn) && fn.ValueKind == JsonValueKind.String
                    ? fn.GetString()
                    : null;

                var collaboratorId = user.TryGetProperty("collaboratorId", out var cid) && cid.ValueKind == JsonValueKind.String
                    ? cid.GetString()
                    : null;

                return (true, email, name, collaboratorId);
            }
            catch
            {
                return (false, null, null, null);
            }
        }

        // Obtiene el perfil completo del usuario autenticado desde /api/auth/me
        // Entrada: ninguna (usa token en headers automáticamente)
        // Salida: (success, userProfile) - userProfile es null si falla
        public async Task<(bool success, UserProfile? profile)> GetUserProfileAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/auth/me", ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return (false, null);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                    return (false, null);

                if (!root.TryGetProperty("user", out var userEl) || userEl.ValueKind != JsonValueKind.Object)
                    return (false, null);

                // Extraer campos requeridos
                var firstName = userEl.TryGetProperty("firstName", out var fnEl) && fnEl.ValueKind == JsonValueKind.String
                    ? fnEl.GetString()
                    : null;

                var lastName = userEl.TryGetProperty("lastName", out var lnEl) && lnEl.ValueKind == JsonValueKind.String
                    ? lnEl.GetString()
                    : null;

                var email = userEl.TryGetProperty("email", out var emEl) && emEl.ValueKind == JsonValueKind.String
                    ? emEl.GetString()
                    : null;

                var createdAtStr = userEl.TryGetProperty("createdAt", out var caEl) && caEl.ValueKind == JsonValueKind.String
                    ? caEl.GetString()
                    : null;

                var updatedAtStr = userEl.TryGetProperty("updatedAt", out var uaEl) && uaEl.ValueKind == JsonValueKind.String
                    ? uaEl.GetString()
                    : null;

                // Validar campos obligatorios
                if (string.IsNullOrWhiteSpace(firstName) ||
                    string.IsNullOrWhiteSpace(lastName) ||
                    string.IsNullOrWhiteSpace(email))
                    return (false, null);

                // Parsear fechas
                if (!DateTime.TryParse(createdAtStr, out var createdAt))
                    createdAt = DateTime.MinValue;

                if (!DateTime.TryParse(updatedAtStr, out var updatedAt))
                    updatedAt = DateTime.MinValue;

                var profile = new UserProfile(
                    firstName!,
                    lastName!,
                    email!,
                    createdAt,
                    updatedAt
                );

                return (true, profile);
            }
            catch (OperationCanceledException)
            {
                return (false, null);
            }
            catch
            {
                return (false, null);
            }
        }
        // Obtiene phone del usuario autenticado
        // Salida: (ok, phone, name)
        public async Task<(bool ok, string? phone, string? name)> GetCurrentUserPhoneAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/auth/me", ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return (false, null, null);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                    return (false, null, null);

                if (!root.TryGetProperty("user", out var userEl) || userEl.ValueKind != JsonValueKind.Object)
                    return (false, null, null);

                var firstName = userEl.TryGetProperty("firstName", out var fnEl) && fnEl.ValueKind == JsonValueKind.String
                    ? fnEl.GetString()
                    : null;

                var lastName = userEl.TryGetProperty("lastName", out var lnEl) && lnEl.ValueKind == JsonValueKind.String
                    ? lnEl.GetString()
                    : null;

                var fullName = !string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(lastName)
                    ? $"{firstName} {lastName}"
                    : firstName ?? lastName ?? "Usuario";

                var phone = userEl.TryGetProperty("phone", out var pEl) && pEl.ValueKind == JsonValueKind.String
                    ? pEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(phone))
                    return (false, null, fullName);

                return (true, phone, fullName);
            }
            catch (OperationCanceledException)
            {
                return (false, null, null);
            }
            catch
            {
                return (false, null, null);
            }
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
