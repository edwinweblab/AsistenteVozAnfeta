using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;

namespace Anfeta.UI.Services
{
    public sealed class OllamaHealthService : IOllamaHealthService
    {
        private readonly HttpClient _http;

        public OllamaHealthService(HttpClient httpClient)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<OllamaStatus> CheckAsync(string modelName, CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/tags", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new OllamaStatus
                    {
                        IsRunning = false,
                        ModelAvailable = false,
                        Message = $"Ollama no respondió correctamente (HTTP {(int)resp.StatusCode})."
                    };
                }

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                {
                    return new OllamaStatus
                    {
                        IsRunning = true,
                        ModelAvailable = false,
                        Message = "Ollama está activo, pero no se pudo leer la lista de modelos."
                    };
                }

                bool found = false;
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nameProp))
                    {
                        var name = (nameProp.GetString() ?? "").Trim();
                        if (name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                return new OllamaStatus
                {
                    IsRunning = true,
                    ModelAvailable = found,
                    Message = found
                        ? $"IA lista: '{modelName}'."
                        : $"Ollama activo, pero el modelo '{modelName}' no está instalado."
                };
            }
            catch
            {
                return new OllamaStatus
                {
                    IsRunning = false,
                    ModelAvailable = false,
                    Message = "Ollama no está corriendo en localhost:11434."
                };
            }
        }
    }
}
