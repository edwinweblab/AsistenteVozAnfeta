// GroqInterpretationService.cs
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
    public sealed class GroqInterpretationService : ICommandInterpretationService
    {
        private readonly HttpClient _http;
        private readonly string _modelName;

        public GroqInterpretationService(HttpClient httpClient, string modelName)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        }

        // Interpreta texto reconocido y devuelve JSON estructurado
        // Entrada: recognizedText (texto del usuario), ct (token cancelación)
        // Salida: InterpretationResponse con PlainText y Json
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

            var payload = new
            {
                model = _modelName,
                temperature = 0.1,
                top_p = 0.9,
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

            return new InterpretationResponse
            {
                PlainText = plain.Trim(),
                Json = interpretationJson
            };
        }

        // Construye prompt optimizado para Groq
        // Entrada: userMessage (comando del usuario)
        // Salida: Prompt completo para enviar a Groq
        private static string BuildPrompt(string userMessage)
        {
            userMessage ??= "";
            userMessage = userMessage.Replace("\"", "\\\"");

            return
                "Return ONLY valid JSON. No extra text.\n\n" +
                "Format:\n" +
                "{\n" +
                "  \"plain_text\": \"short message\",\n" +
                "  \"interpretation\": {\n" +
                "    \"intent\": \"OpenApp|CloseApp|MinimizeAll|SwitchWindow|ApiCall|Unknown\",\n" +
                "    \"scope\": \"LOCAL|API|BROWSER\",\n" +
                "    \"provider\": \"weblab|notion|dropbox|google|null\",\n" +
                "    \"app_key\": \"string|null\",\n" +
                "    \"resource\": \"system|actividades|proyectos|recordatorios|reportes|usuarios|opciones|presence|revisiones|agenda|pendientes|null\",\n" +
                "    \"action\": \"open|close|minimize|switch|list|today|get|search|create|update|delete|navigate|new_tab|close_tab|find|unknown\",\n" +
                "    \"confidence\": 0.0,\n" +
                "    \"params\": {},\n" +
                "    \"reason\": \"string breve\"\n" +
                "  }\n" +
                "}\n\n" +

                "CRITICAL RULES:\n" +

                "1) confidence: 1.0 si es claro, 0.7 si es ambiguo, 0.3 si es poco claro.\n\n" +

                "2) LOCAL scope = Windows apps (chrome, calculadora, bloc, explorador).\n" +
                "   Ejemplo: 'abrir chrome' -> intent:OpenApp, scope:LOCAL, app_key:chrome, action:open, confidence:1.0\n\n" +

                "3) API scope = consultas Weblab.\n" +
                "   SI el usuario pide datos (actividades, revisiones, presence, etc) entonces:\n" +
                "   - intent MUST be ApiCall\n" +
                "   - scope MUST be API\n" +
                "   - provider MUST be weblab\n\n" +

                "4) MAPEOS CLAVE (evitar errores):\n" +
                "   - 'dame actividades' / 'ver actividades' / 'obtener actividades' => action:list (NO today)\n" +
                "   - SOLO usa action:today si el texto contiene: 'hoy' o 'del día' o 'de hoy'\n" +
                "   - 'dame actividades de hoy' / 'actividades del día' => action:today\n" +
                "   - 'detalles/detalle de <id>' => action:get y params:{\"id\":\"<id>\"}\n" +
                "   - 'buscar <texto>' / 'encuentra <texto>' => action:search y params:{\"q\":\"<texto>\"}\n\n" +

                "5) RECURSOS SOPORTADOS (API):\n" +
                "   - actividades: list|today|get|search\n" +
                "   - revisiones: today|en-curso\n" +
                "   - reportes: list|today (SOLO revisiones-por-fecha; params permitido: date)\n" +
                "   - presence: online (mapear como action:list si piden 'usuarios en línea')\n\n" +

                "6) EJEMPLOS EXACTOS:\n" +
                "   User: 'dame actividades' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:list, confidence:1.0\n" +
                "   User: 'obtener actividades' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:list, confidence:1.0\n" +
                "   User: 'dame actividades de hoy' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:today, confidence:1.0\n" +
                "   User: 'qué tengo hoy' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:today, confidence:1.0\n" +
                "   User: 'detalles de ABC123' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:get, params:{\"id\":\"ABC123\"}, confidence:1.0\n" +
                "   User: 'buscar actividades de tesis' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:search, params:{\"q\":\"tesis\"}, confidence:1.0\n\n" +
                "   User: 'dame reportes' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:list, confidence:1.0\n" +
                "   User: 'qué reportes tengo' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:list, confidence:1.0\n" +
                "   User: 'muéstrame mis reportes' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:list, confidence:1.0\n" +
                "   User: 'dame mis reportes de hoy' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:today, confidence:1.0\n" +
                "   User: 'dame mis reportes del 4 de febrero' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:list, params:{\"date\":\"2026-02-04\"}, confidence:1.0\n" +
                "   User: 'qué revisiones tengo' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:list, confidence:0.9\n" +
                "   User: 'qué revisiones tengo para el día 4 de febrero' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:list, params:{\"date\":\"2026-02-04\"}, confidence:1.0\n\n" +

                "User: \"" + userMessage + "\"\n\n" +
                "Return JSON with confidence >= 0.7 for valid commands:\n";
        }

        // Extrae contenido de la respuesta de Groq
        // Entrada: json (respuesta completa de Groq API)
        // Salida: Contenido del mensaje del asistente
        private static string ExtractAssistantContent(string json)
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return "";

            var msg = choices[0].GetProperty("message");
            if (!msg.TryGetProperty("content", out var content))
                return "";

            return content.GetString() ?? "";
        }

        // Extrae primer objeto JSON válido del texto
        // Entrada: text (texto que puede contener JSON)
        // Salida: JSON extraído o null si no se encuentra
        private static string? ExtractFirstJson(string text)
        {
            var match = Regex.Match(text, @"\{[\s\S]*\}", RegexOptions.Multiline);
            if (!match.Success) return null;

            var candidate = match.Value.Trim();
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch
            {
                return null;
            }
        }
    }
}