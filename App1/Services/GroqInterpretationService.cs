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
                "   \"intent\": \"OpenApp|CloseApp|MinimizeAll|SwitchWindow|ApiCall|Unknown\",\n" +
                "    \"scope\": \"LOCAL|API|BROWSER\",\n" +
                "    \"provider\": \"weblab|notion|dropbox|google|null\",\n" +
                "    \"app_key\": \"string|null\",\n" +
                "    \"resource\": \"system|actividades|proyectos|recordatorios|reportes|usuarios|opciones|presence|revisiones|agenda|pendientes|null\",\n" +
                "    \"action\": \"open|close|minimize|switch|list|get|search|create|update|delete|navigate|new_tab|close_tab|find|unknown\",\n" +
                "    \"confidence\": 0.0,\n" +
                "    \"params\": {},\n" +
                "    \"reason\": \"string breve\"\n" +
                "  }\n" +
                "}\n\n" +
                "CRITICAL RULES:\n" +
                "1. Set confidence to 1.0 for clear commands, 0.7 for ambiguous, 0.3 for unclear\n\n" +  // ⬅️ NUEVO
                "2. LOCAL scope = Windows apps (chrome, calculadora, bloc, explorador)\n" +
                "   Example: 'abrir chrome' -> intent:OpenApp, scope:LOCAL, app_key:chrome, confidence:1.0\n\n" +
                "3. API scope = Weblab data queries - ALWAYS include resource + action\n" +
                "   Examples:\n" +
                "   'ver actividades' -> resource:actividades, action:list, confidence:1.0\n" +
                "   'qué tengo hoy' -> resource:actividades, action:today, confidence:1.0\n" +
                "   'detalles de ABC123' -> resource:actividades, action:get, params:{id:ABC123}, confidence:1.0\n" +
                "   'qué revisiones hoy' -> resource:revisiones, action:today, confidence:1.0\n" +
                "   'en qué estoy' -> resource:revisiones, action:en-curso, confidence:1.0\n\n" +
                "4. Action mapping:\n" +
                "   'ver/mostrar/listar/dame' (no 'hoy', no 'detalles') -> action:list\n" +
                "   'hoy/del día/de hoy' -> action:today\n" +
                "   'detalles/detalle' -> action:get, extract ID to params.id\n" +
                "   'busca/encuentra' -> action:search\n" +
                "   'en qué estoy/activa/en curso' -> action:en-curso\n\n" +
                "5. Resource: actividades/revisiones/proyectos/usuarios\n\n" +
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