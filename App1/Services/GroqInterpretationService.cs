using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;

namespace Anfeta.UI.Services
{
    public sealed class GroqInterpretationService : ICommandInterpretationService
    {
        private readonly HttpClient _http;
        private readonly string _modelName;

        public GroqInterpretationService(HttpClient httpClient, string modelName)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
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

            // Groq (OpenAI compatible): chat.completions
            var payload = new
            {
                model = _modelName,
                temperature = 0.1,
                top_p = 0.9,
                // Importante: forzar salida JSON limpia (aun así validamos con ExtractFirstJson)
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = "Eres un parser estricto. Devuelve SOLO JSON válido sin texto extra." },
                    new { role = "user", content = prompt }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var modelText = ExtractGroqAssistantContent(respJson);

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

            string plain = root.TryGetProperty("plain_text", out var pt)
                ? (pt.GetString() ?? "")
                : "Interpretación generada.";

            string interpretationJson = root.TryGetProperty("interpretation", out var interp)
                ? interp.GetRawText()
                : "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"confidence\":0.2,\"needs_confirmation\":false,\"params\":{}}";

            // Forzar confirmación true siempre (tu policy require_confirmation=true)
            interpretationJson = ForceNeedsConfirmationTrue(interpretationJson);

            return new InterpretationResponse
            {
                PlainText = plain.Trim(),
                Json = interpretationJson
            };
        }

        private static string BuildPrompt(string userMessage)
        {
            userMessage ??= "";
            userMessage = userMessage.Replace("\"", "\\\"");

            return
                "Devuelve SOLO un JSON válido. Sin texto extra.\n\n" +
                "Formato exacto:\n" +
                "{\n" +
                "  \"plain_text\": \"string corto para el usuario\",\n" +
                "  \"interpretation\": {\n" +
                "    \"intent\": \"OpenApp|CloseApp|MinimizeAll|SwitchWindow|Unknown\",\n" +
                "    \"scope\": \"LOCAL|API\",\n" +
                "    \"app_key\": \"string|null\",\n" +
                "    \"provider\": \"notion|dropbox|weblab|null\",\n" +
                "    \"confidence\": 0.0,\n" +
                "    \"params\": {},\n" +
                "    \"needs_confirmation\": true,\n" +
                "    \"reason\": \"string breve\"\n" +
                "  }\n" +
                "}\n\n" +
                "APPS LOCALES PERMITIDAS (solo estas 4):\n" +
                "1. chrome - navegador web\n" +
                "2. calculadora - calculadora de Windows\n" +
                "3. bloc - bloc de notas (notepad, blog)\n" +
                "4. explorador - explorador de archivos\n\n" +
                "REGLAS CRÍTICAS:\n" +
                "1) Si el usuario menciona una app NO EN LA LISTA ANTERIOR:\n" +
                "   -> intent=\"Unknown\", app_key=null, confidence=0.1\n" +
                "2) Para LOCAL con OpenApp/CloseApp:\n" +
                "   -> app_key SOLO puede ser: chrome, calculadora, bloc, explorador\n" +
                "   -> Si menciona \"blog\", \"word\", \"excel\", etc: intent=\"Unknown\", app_key=null\n" +
                "3) Si pide abrir un sitio web SIN mencionar chrome:\n" +
                "   -> intent=\"Unknown\" (no asumas navegador)\n" +
                "4) MinimizeAll no necesita app_key\n" +
                "5) needs_confirmation=true siempre\n" +
                "6) reason: explica tu decisión brevemente\n\n" +
                "Entrada del usuario:\n" +
                "\"" + userMessage + "\"\n\n" +
                "Responde SOLO JSON válido.\n";
        }

        private static string ExtractGroqAssistantContent(string json)
        {
            using var doc = JsonDocument.Parse(json);

            // choices[0].message.content
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return "";

            var msg = choices[0].GetProperty("message");
            if (!msg.TryGetProperty("content", out var content))
                return "";

            return content.GetString() ?? "";
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
