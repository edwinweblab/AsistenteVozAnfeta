// GroqInterpretationService.cs
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
                    Json = "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"app_key\":null,\"provider\":null,\"resource\":null,\"action\":\"unknown\",\"confidence\":0.0,\"params\":{},\"reason\":\"\"}"
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
            var modelText = ExtractAssistantContent(respJson);

            var wrapperJson = ExtractFirstJson(modelText);
            if (string.IsNullOrWhiteSpace(wrapperJson))
            {
                return new InterpretationResponse
                {
                    PlainText = "No pude interpretar el comando (salida no válida).",
                    Json = "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"app_key\":null,\"provider\":null,\"resource\":null,\"action\":\"unknown\",\"confidence\":0.2,\"params\":{},\"reason\":\"\"}"
                };
            }

            using var doc = JsonDocument.Parse(wrapperJson);
            var root = doc.RootElement;

            string plain = root.TryGetProperty("plain_text", out var pt)
                ? (pt.GetString() ?? "")
                : "Interpretación generada.";

            string interpretationJson = root.TryGetProperty("interpretation", out var interp)
                ? interp.GetRawText()
                : "{\"intent\":\"Unknown\",\"scope\":\"LOCAL\",\"app_key\":null,\"provider\":null,\"resource\":null,\"action\":\"unknown\",\"confidence\":0.2,\"params\":{},\"reason\":\"\"}";

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
                "    \"intent\": \"OpenApp|CloseApp|MinimizeAll|SwitchWindow|ApiCall|Unknown\",\n" +
                "    \"scope\": \"LOCAL|API|BROWSER\",\n" +
                "    \"provider\": \"weblab|notion|dropbox|google|null\",\n" +
                "    \"app_key\": \"string|null\",\n" +
                "    \"resource\": \"system|actividades|proyectos|recordatorios|reportes|usuarios|opciones|presence|revisiones|agenda|pendientes|null\",\n" +
                "    \"action\": \"open|close|minimize|switch|list|get|search|create|update|delete|navigate|new_tab|close_tab|find|unknown\",\n" +
                "    \"confidence\": 0.0,\n" +
                "    \"params\": {},\n" +
                "    \"needs_confirmation\": true,\n" +
                "    \"reason\": \"string breve\"\n" +
                "  }\n" +
                "}\n\n" +
                "REGLAS CLAVE (NO inventes nada):\n" +
                "1) LOCAL (Windows) = abrir/cerrar apps o acciones del sistema.\n" +
                "   - scope=\"LOCAL\", provider=null, resource=\"system\".\n" +
                "   - intent: OpenApp|CloseApp|MinimizeAll|SwitchWindow.\n" +
                "   - Para OpenApp/CloseApp: app_key obligatorio.\n" +
                "   - 'abrir navegador' o 'abrir chrome' SIEMPRE es LOCAL (app_key=\"chrome\").\n" +
                "   - 'calculadora' => app_key=\"calculadora\".\n" +
                "   - 'bloc de notas/notepad' => app_key=\"bloc\".\n" +
                "   - 'explorador/archivos/file explorer' => app_key=\"explorador\".\n" +
                "2) API (Weblab módulos) cuando el usuario pide ver/listar cosas del sistema:\n" +
                "   - scope=\"API\", provider=\"weblab\", app_key=null.\n" +
                "   - resource debe ser uno de: actividades, proyectos, recordatorios, reportes, usuarios, opciones, presence, revisiones, agenda, pendientes.\n" +
                "   - Heurísticas:\n" +
                "       'ver/mostrar/listar/dame' => action=\"list\"\n" +
                "       'actividad 12' => action=\"get\" y params.id=12\n" +
                "       'busca X' => action=\"search\" y params.q=\"X\"\n" +
                "       'crear/agregar' => action=\"create\"\n" +
                "       'actualizar/editar' => action=\"update\"\n" +
                "       'eliminar/borrar' => action=\"delete\"\n" +
                "3) BROWSER solo si pide acciones dentro del navegador (internet):\n" +
                "   - ejemplos: 'busca en internet...', 've a google.com', 'abre nueva pestaña', 'cierra pestaña'.\n" +
                "4) Si el texto dice 'actividades/proyectos/...' NO es app local: es API.\n" +
                "5) Si hay duda: intent=\"Unknown\", scope=\"LOCAL\", action=\"unknown\", app_key=null.\n\n" +
                "Entrada:\n" +
                "\"" + userMessage + "\"\n\n" +
                "Responde SOLO JSON.\n";
        }

        private static string ExtractAssistantContent(string json)
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
