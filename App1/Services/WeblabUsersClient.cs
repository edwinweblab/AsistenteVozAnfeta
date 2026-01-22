using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public sealed class WeblabUsersClient
    {
        private readonly HttpClient _http;

        public WeblabUsersClient(HttpClient http)
        {
            _http = http;
        }

        public async Task<UserSearchResult> SearchByEmailAsync(string email)
        {
            var url = $"/api/users/search?email={Uri.EscapeDataString(email)}";
            using var res = await _http.GetAsync(url);
            var json = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return UserSearchResult.FromError(json);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Formato real: { "items": [ { ... } ] }
            if (root.TryGetProperty("items", out var itemsEl) &&
                itemsEl.ValueKind == JsonValueKind.Array &&
                itemsEl.GetArrayLength() > 0)
            {
                var u = itemsEl[0];

                var collaboratorId = u.TryGetProperty("collaboratorId", out var cEl) ? cEl.GetString() : null;
                var firstName = u.TryGetProperty("firstName", out var fnEl) ? fnEl.GetString() : null;
                var lastName = u.TryGetProperty("lastName", out var lnEl) ? lnEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(collaboratorId))
                    return UserSearchResult.FromError("items[0] no trae collaboratorId.");

                if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
                    return UserSearchResult.FromError("items[0] no trae firstName/lastName.");

                return UserSearchResult.FromOk(firstName!, lastName!, collaboratorId!);
            }

            return UserSearchResult.FromError("Formato inesperado: no existe items[0].");
        }
    }

    public sealed record UserSearchResult(
        bool Ok,
        string? FirstName,
        string? LastName,
        string? CollaboratorId,
        string? RawError)
    {
        public static UserSearchResult FromOk(string firstName, string lastName, string collaboratorId)
            => new(true, firstName, lastName, collaboratorId, null);

        public static UserSearchResult FromError(string raw)
            => new(false, null, null, null, raw);
    }
}
