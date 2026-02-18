using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;

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

        public async Task<UserSearchResponse> SearchUsersAsync(string query, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/users/search?q={Uri.EscapeDataString(query)}";

                System.Diagnostics.Debug.WriteLine($"[USERS] Buscando: '{query}'");
                System.Diagnostics.Debug.WriteLine($"[USERS] URL: {url}");

                using var res = await _http.GetAsync(url, ct);
                var json = await res.Content.ReadAsStringAsync(ct);

                System.Diagnostics.Debug.WriteLine($"[USERS] Status: {res.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[USERS] Response JSON: {json}");

                if (!res.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[USERS] ERROR HTTP {res.StatusCode}");
                    return UserSearchResponse.FromError($"Error HTTP {res.StatusCode}");
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var items = new List<UserSearchItem>();

                if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                {
                    System.Diagnostics.Debug.WriteLine($"[USERS] Items encontrados: {itemsEl.GetArrayLength()}");

                    foreach (var item in itemsEl.EnumerateArray())
                    {
                        var firstName = item.TryGetProperty("firstName", out var fnEl) ? fnEl.GetString() : "";
                        var lastName = item.TryGetProperty("lastName", out var lnEl) ? lnEl.GetString() : "";
                        var email = item.TryGetProperty("email", out var emEl) ? emEl.GetString() : "";
                        var collaboratorId = item.TryGetProperty("collaboratorId", out var cEl) ? cEl.GetString() : "";

                        System.Diagnostics.Debug.WriteLine($"[USERS] - {firstName} {lastName} ({email}) - ID: {collaboratorId}");

                        if (!string.IsNullOrWhiteSpace(collaboratorId))
                        {
                            items.Add(new UserSearchItem
                            {
                                FirstName = firstName ?? "",
                                LastName = lastName ?? "",
                                Email = email ?? "",
                                CollaboratorId = collaboratorId
                            });
                        }
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[USERS] No se encontró 'items' en respuesta o no es array");
                }

                System.Diagnostics.Debug.WriteLine($"[USERS] Total items parseados: {items.Count}");
                return UserSearchResponse.FromSuccess(items);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[USERS] EXCEPTION: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[USERS] STACK: {ex.StackTrace}");
                return UserSearchResponse.FromError($"Error: {ex.Message}");
            }
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

    public sealed class UserSearchResponse
    {
        public bool Success { get; set; }
        public List<UserSearchItem> Items { get; set; } = new();
        public string? Error { get; set; }

        public static UserSearchResponse FromSuccess(List<UserSearchItem> items)
            => new() { Success = true, Items = items };

        public static UserSearchResponse FromError(string error)
            => new() { Success = false, Error = error };
    }
}