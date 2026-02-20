using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public class GroqKeyValidator
    {
        private readonly IHttpClientFactory _factory;

        public GroqKeyValidator(IHttpClientFactory factory)
        {
            _factory = factory;
        }

        public async Task<(bool ok, string? error)> ValidateAsync(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return (false, "La API key está vacía.");

            if (!apiKey.StartsWith("gsk_", StringComparison.OrdinalIgnoreCase))
                return (false, "Formato inválido (se esperaba prefijo gsk_).");

            try
            {
                var http = _factory.CreateClient("GroqValidate");
                using var req = new HttpRequestMessage(HttpMethod.Get, "models");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                    return (true, null);

                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"Groq rechazó la key: {(int)resp.StatusCode} {resp.ReasonPhrase}. {body}");
            }
            catch (Exception ex)
            {
                return (false, $"No se pudo validar: {ex.Message}");
            }
        }
    }
}
