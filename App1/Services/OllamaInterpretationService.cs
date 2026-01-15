using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;

namespace Anfeta.UI.Services
{
    public sealed class OllamaInterpretationService : ICommandInterpretationService
    {
        private readonly HttpClient _http;
        private readonly string _modelName;

        public OllamaInterpretationService(HttpClient httpClient, string modelName)
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

            var payload = new
            {
                model = _modelName,
                prompt,
                stream = false,
                options = new { temperature = 0.1, top_p = 0.9 }
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/api/generate", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var modelText = ExtractOllamaResponse(respJson);

            var wrapperJson = ExtractFirstJson(modelText);
            if (string.IsNullOrWhiteSpace(wrapperJson))
            {
                return new InterpretationResponse
                {
                    PlainText = "No pude interpretar el comando (salida no válida).",
                    Json = "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"confidence\":0.2,\"needs_confirmation\":false,\"params\":{}}"
                };
            }

            // wrapper { plain_text, interpretation }
            using var doc = JsonDocument.Parse(wrapperJson);
            var root = doc.RootElement;

            string plain = root.TryGetProperty("plain_text", out var pt) ? (pt.GetString() ?? "") : "Interpretación generada.";
            string interpretationJson = root.TryGetProperty("interpretation", out var interp)
                ? interp.GetRawText()
                : "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"confidence\":0.2,\"needs_confirmation\":false,\"params\":{}}";

            // Forzar confirmación (tu política require_confirmation=true)
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

    // Usamos un string normal + concatenación para evitar problemas con { } del JSON
    return
        "Devuelve SOLO un JSON válido.\n\n" +
        "Formato exacto:\n" +
        "{\n" +
        "  \"plain_text\": \"string corto para el usuario\",\n" +
        "  \"interpretation\": {\n" +
        "    \"intent\": \"OpenApp|CloseApp|MinimizeAll|SwitchWindow|Unknown|CreateTask|ListFiles|GetAppointments\",\n" +
        "    \"scope\": \"LOCAL|API\",\n" +
        "    \"app_key\": \"calculadora|bloc|explorador|chrome|null\",\n" +
        "    \"provider\": \"notion|dropbox|weblab|null\",\n" +
        "    \"confidence\": 0.0,\n" +
        "    \"params\": {},\n" +
        "    \"needs_confirmation\": true,\n" +
        "    \"reason\": \"string breve\"\n" +
        "  }\n" +
        "}\n\n" +
        "LOCAL allowed intents:\n" +
        "- OpenApp, CloseApp, MinimizeAll, SwitchWindow\n\n" +
        "LOCAL allowed apps:\n" +
        "- calculadora, bloc, explorador, chrome\n\n" +
        "Blocked apps:\n" +
        "- cmd, powershell, regedit, taskmgr, msiexec\n\n" +
        "Reglas:\n" +
        "- LOCAL: abrir/cerrar/minimizar/cambiar ventana\n" +
        "- API: Notion/Dropbox/Weblab u otros externos (solo estructura; no ejecutar)\n" +
        "- Si es API, app_key=null\n" +
        "- needs_confirmation=true siempre\n\n" +
        "Entrada:\n" +
        "\"" + userMessage + "\"\n\n" +
        "Responde SOLO JSON.\n";
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
