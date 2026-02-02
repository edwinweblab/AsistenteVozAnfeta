using Anfeta.UI.Models;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public sealed class OllamaInterpretationService : ICommandInterpretationService
    {
        private readonly HttpClient _http;
        private readonly string _modelName;
        private readonly PromptBuilder _promptBuilder;

        public OllamaInterpretationService(
            HttpClient httpClient,
            string modelName,
            PromptBuilder promptBuilder)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
            _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        }

        public async Task<InterpretationResponse> InterpretRawAsync(string recognizedText, CancellationToken ct = default)
        {
            recognizedText ??= "";
            recognizedText = recognizedText.Trim();

            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                return new InterpretationResponse
                {
                    PlainText = "No se detectó texto para interpretar.",
                    Json = "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"confidence\":0.0,\"needs_confirmation\":false,\"params\":{}}"
                };
            }

            var prompt = BuildPrompt(recognizedText);

            var payload = new
            {
                model = _modelName,
                prompt,
                stream = false,
                options = new
                {
                    temperature = 0,
                    top_p = 0.95,           // CAMBIADO: era 0.9, ahora más determinista
                    num_predict = 80,       // CAMBIADO: era 150, ahora 80 (suficiente para JSON)
                    num_ctx = 1024,         // CAMBIADO: era 2048, ahora 1024 (reduce latencia)
                    num_thread = 4          // NUEVO: usa 4 threads para acelerar
                }
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/api/generate", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var modelText = ExtractOllamaResponse(respJson);

            System.Diagnostics.Debug.WriteLine($"===== MODELO RAW =====");
            System.Diagnostics.Debug.WriteLine(modelText);

            var wrapperJson = ExtractFirstJson(modelText);
            if (string.IsNullOrWhiteSpace(wrapperJson))
            {
                return new InterpretationResponse
                {
                    PlainText = "No pude interpretar el comando (salida no válida).",
                    Json = "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"confidence\":0.2,\"needs_confirmation\":false,\"params\":{}}"
                };
            }

            using var doc = JsonDocument.Parse(wrapperJson);
            var root = doc.RootElement;

            string plain = root.TryGetProperty("plain_text", out var pt) ? (pt.GetString() ?? "") : "Interpretación generada.";
            string interpretationJson = root.TryGetProperty("interpretation", out var interp)
                ? interp.GetRawText()
                : "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"confidence\":0.2,\"needs_confirmation\":false,\"params\":{}}";

            interpretationJson = ForceNeedsConfirmationTrue(interpretationJson);

            return new InterpretationResponse
            {
                PlainText = plain.Trim(),
                Json = interpretationJson
            };
        }

        private string BuildPrompt(string recognizedText)
        {
            return _promptBuilder.BuildPrompt(recognizedText);
        }

        private static string ExtractOllamaResponse(string ollamaJson)
        {
            using var doc = JsonDocument.Parse(ollamaJson);
            return doc.RootElement.TryGetProperty("response", out var r) ? (r.GetString() ?? "") : "";
        }

        private static string? ExtractFirstJson(string text)
        {
            var match = Regex.Match(text, @"\{[\s\S]*\}", RegexOptions.Multiline);
            if (!match.Success) return null;

            var candidate = match.Value.Trim();
            try { using var _ = JsonDocument.Parse(candidate); return candidate; }
            catch { return null; }
        }

        private static string ForceNeedsConfirmationTrue(string interpretationJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(interpretationJson);
                var root = doc.RootElement;

                var obj = new
                {
                    intent = root.TryGetProperty("intent", out var i) ? (i.GetString() ?? "Unknown") : "Unknown",
                    scope = root.TryGetProperty("scope", out var s) ? (s.GetString() ?? "LOCAL") : "LOCAL",
                    app_key = root.TryGetProperty("app_key", out var a) && a.ValueKind != JsonValueKind.Null ? a.GetString() : null,
                    provider = root.TryGetProperty("provider", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null,
                    confidence = root.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.2,
                    @params = root.TryGetProperty("params", out var pr) && pr.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<object>(pr.GetRawText())
                        : new { },
                    needs_confirmation = true,
                    reason = root.TryGetProperty("reason", out var rs) ? (rs.GetString() ?? "") : ""
                };

                return JsonSerializer.Serialize(obj);
            }
            catch
            {
                return interpretationJson;
            }
        }
    }
}