// Services/Groq/GroqInterpretationService.cs
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models.Interpretation;
using Anfeta.UI.Services.Interpretation;

namespace Anfeta.UI.Services.Groq
{
    public sealed class GroqInterpretationService : ICommandInterpretationService
    {
        private readonly HttpClient _http;
        private readonly string _modelName;

        // Delays entre reintentos al recibir 429 (ms): 1.5s, 4s, 8s
        private static readonly int[] RetryDelaysMs = { 1500, 4000, 8000 };

        public GroqInterpretationService(HttpClient httpClient, string modelName)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        }

        /// <summary>
        /// Interpreta texto reconocido y devuelve JSON estructurado.
        /// Entrada: recognizedText (texto del usuario), ct (token cancelación).
        /// Salida: InterpretationResponse con PlainText y Json.
        /// Lanza GroqRateLimitException si Groq responde 429 tras todos los reintentos.
        /// </summary>
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

            // Serializar una sola vez — el factory lo reutiliza en cada reintento
            var payloadJson = JsonSerializer.Serialize(payload);

            using var resp = await SendWithRetryAsync(
                () =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
                    r.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                    return r;
                },
                ct).ConfigureAwait(false);

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
                ? pt.GetString() ?? ""
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

        /// <summary>
        /// Envía la petición a Groq con reintentos automáticos en caso de 429.
        /// Entrada: buildRequest (factory del HttpRequestMessage), ct.
        /// Salida: HttpResponseMessage con status != 429.
        /// Lanza GroqRateLimitException si se agotan todos los reintentos.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<HttpRequestMessage> buildRequest,
            CancellationToken ct)
        {
            for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
            {
                using var req = buildRequest();
                var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

                if ((int)resp.StatusCode != 429)
                    return resp;

                resp.Dispose();

                if (attempt == RetryDelaysMs.Length)
                {
                    System.Diagnostics.Debug.WriteLine($"[GROQ] 429 tras {RetryDelaysMs.Length} reintentos. Abortando.");
                    throw new GroqRateLimitException("El servicio de IA está saturado. Intenta en unos segundos.");
                }

                System.Diagnostics.Debug.WriteLine($"[GROQ] 429 recibido. Reintento {attempt + 1}/{RetryDelaysMs.Length} en {RetryDelaysMs[attempt]}ms...");
                await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
            }

            // Inalcanzable — requerido por el compilador
            throw new InvalidOperationException("[GROQ] SendWithRetryAsync: estado inesperado.");
        }

        /// <summary>
        /// Construye prompt optimizado para Groq.
        /// Inyecta fecha/hora actual para que la IA resuelva fechas relativas correctamente.
        /// Entrada: userMessage (comando del usuario).
        /// Salida: Prompt completo para enviar a Groq.
        /// </summary>
        private static string BuildPrompt(string userMessage)
        {
            userMessage ??= "";
            userMessage = userMessage.Replace("\"", "\\\"");

            var now = DateTime.Now;
            var today = now.ToString("yyyy-MM-dd");
            var todayIso = now.ToString("yyyy-MM-ddTHH:mm:ss-06:00");
            var tomorrow = now.AddDays(1).ToString("yyyy-MM-dd");
            var dayName = now.ToString("dddd", new System.Globalization.CultureInfo("es-MX"));

            return
                "Return ONLY valid JSON. No extra text.\n\n"
                + "=== FECHA Y HORA ACTUAL ===\n"
                + $"TODAY: {today} ({dayName})\n"
                + $"NOW: {todayIso}\n"
                + $"TOMORROW: {tomorrow}\n"
                + "TIMEZONE: America/Mexico_City (UTC-6)\n"
                + "USA SIEMPRE este offset (-06:00) en las fechas ISO que generes.\n\n"
                + "Format:\n"
                + "{\n"
                + "  \"plain_text\": \"short message\",\n"
                + "  \"interpretation\": {\n"
                + "    \"intent\": \"OpenApp|CloseApp|MinimizeAll|SwitchWindow|ApiCall|Unknown\",\n"
                + "    \"scope\": \"LOCAL|API|BROWSER\",\n"
                + "    \"provider\": \"weblab|notion|dropbox|google|null\",\n"
                + "    \"app_key\": \"string|null\",\n"
                + "    \"resource\": \"system|actividades|proyectos|recordatorios|reportes|usuarios|opciones|presence|revisiones|agenda|pendientes|null\",\n"
                + "    \"action\": \"open|close|minimize|switch|list|today|tomorrow|pending|get|search|create|update|delete|complete|navigate|new_tab|close_tab|find|unknown\",\n"
                + "    \"confidence\": 0.0,\n"
                + "    \"params\": {},\n"
                + "    \"reason\": \"string breve\"\n"
                + "  }\n"
                + "}\n\n"
                + "CRITICAL RULES:\n"
                + "1) confidence: 1.0 si es claro, 0.7 si es ambiguo, 0.3 si es poco claro.\n\n"
                + "2) LOCAL scope = Windows apps (chrome, calculadora, bloc, explorador).\n"
                + "   Ejemplo: 'abrir chrome' -> intent:OpenApp, scope:LOCAL, app_key:chrome, action:open, confidence:1.0\n\n"
                + "3) API scope = consultas Weblab.\n"
                + "   SI el usuario pide datos (actividades, revisiones, presence, etc) entonces:\n"
                + "   - intent MUST be ApiCall\n"
                + "   - scope MUST be API\n"
                + "   - provider MUST be weblab\n\n"
                + "4) MAPEOS CLAVE (evitar errores):\n"
                + "   - 'dame actividades' / 'ver actividades' / 'obtener actividades' => action:list (NO today)\n"
                + "   - SOLO usa action:today si el texto contiene: 'hoy' o 'del día' o 'de hoy'\n"
                + "   - 'dame actividades de hoy' / 'actividades del día' => action:today\n"
                + "   - 'detalles/detalle de <id>' => action:get y params:{\"id\":\"<id>\"}\n"
                + "   - 'buscar <texto>' / 'encuentra <texto>' => action:search y params:{\"q\":\"<texto>\"}\n\n"
                + "5) REGLA CRÍTICA — 'recuérdame [X]' SIEMPRE es resource:recordatorios.\n"
                + "   Aunque X contenga palabras como 'reporte', 'informe', 'documento', 'nota', 'llamada'.\n"
                + "   resource:reportes SOLO cuando pidan VER historial de revisiones (dame reportes, ver reportes).\n"
                + $"   CORRECTO: 'recuérdame enviar el reporte el viernes a las 3' -> resource:recordatorios, action:create, params:{{\"mensaje\":\"enviar el reporte\",\"fechaHora\":\"<viernes próximo>T15:00:00-06:00\"}}\n"
                + "   INCORRECTO: 'recuérdame enviar el reporte' -> resource:reportes (NUNCA HAGAS ESTO)\n\n"
                + "6) RECURSOS SOPORTADOS (API):\n"
                + "   - actividades: list|today|get|search|create\n"
                + "   - revisiones: today|en-curso\n"
                + "   - reportes: list|today (SOLO historial de revisiones; params permitido: date)\n"
                + "   - recordatorios: list|pending|today|tomorrow|create|update|delete|complete\n"
                + "   - presence: online (mapear como action:list si piden 'usuarios en línea')\n\n"
                + "7) REGLAS PARA FECHAS EN RECORDATORIOS:\n"
                + $"   - Fecha base HOY: {today}\n"
                + $"   - 'mañana' = {tomorrow}\n"
                + "   - 'en una hora' = NOW + 1 hora\n"
                + "   - 'esta tarde' = hoy a las 17:00\n"
                + "   - 'esta noche' = hoy a las 21:00\n"
                + "   - Si el usuario NO especifica hora, usa 09:00:00 como default.\n"
                + "   - Si dice 'a las 3' sin AM/PM, interpreta como PM (15:00) si es una hora de trabajo normal.\n"
                + "   - SIEMPRE usa formato ISO 8601 con offset: yyyy-MM-ddTHH:mm:ss-06:00\n"
                + "   - fechaHora va en params junto con mensaje.\n\n"
                + "8) REGLAS PARA create/update/delete DE RECORDATORIOS:\n"
                + "   CREATE: params DEBE tener 'mensaje' (string) y 'fechaHora' (ISO string). 'duracionMinutos' es opcional (default 30).\n"
                + "   UPDATE: params DEBE tener 'id' (string). Opcionales: 'mensaje', 'fechaHora', 'duracionMinutos'.\n"
                + "   DELETE: params DEBE tener 'id' (string).\n"
                + "   COMPLETE: params DEBE tener 'id' (string).\n\n"
                + "9) EJEMPLOS EXACTOS:\n"
                + "   User: 'dame actividades' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:list, confidence:1.0\n"
                + "   User: 'dame actividades de hoy' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:today, confidence:1.0\n"
                + "   User: 'qué tengo hoy' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:today, confidence:1.0\n"
                + "   User: 'detalles de ABC123' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:get, params:{\"id\":\"ABC123\"}, confidence:1.0\n"
                + "   User: 'buscar actividades de tesis' -> intent:ApiCall, scope:API, provider:weblab, resource:actividades, action:search, params:{\"q\":\"tesis\"}, confidence:1.0\n\n"
                + "   User: 'dame reportes' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:list, confidence:1.0\n"
                + "   User: 'dame mis reportes de hoy' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:today, confidence:1.0\n"
                + "   User: 'dame mis reportes del 4 de febrero' -> intent:ApiCall, scope:API, provider:weblab, resource:reportes, action:list, params:{\"date\":\"2026-02-04\"}, confidence:1.0\n\n"
                + "   User: 'dame mis recordatorios' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:list, confidence:1.0\n"
                + "   User: 'qué recordatorios tengo pendientes' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:pending, confidence:1.0\n"
                + "   User: 'recordatorios de hoy' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:today, confidence:1.0\n"
                + "   User: 'qué tengo para mañana' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:tomorrow, confidence:1.0\n\n"
                + "   -- CREAR recordatorio --\n"
                + $"   User: 'recuérdame llamar al cliente mañana a las 10' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:create, params:{{\"mensaje\":\"llamar al cliente\",\"fechaHora\":\"{tomorrow}T10:00:00-06:00\",\"duracionMinutos\":30}}, confidence:0.9\n"
                + $"   User: 'ponme un recordatorio de la junta hoy a las 3' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:create, params:{{\"mensaje\":\"junta\",\"fechaHora\":\"{today}T15:00:00-06:00\",\"duracionMinutos\":30}}, confidence:0.9\n"
                + $"   User: 'recuérdame enviar el reporte el viernes a las 3' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:create, params:{{\"mensaje\":\"enviar el reporte\",\"fechaHora\":\"<viernes próximo>T15:00:00-06:00\",\"duracionMinutos\":30}}, confidence:0.9\n"
                + $"   User: 'agrega un recordatorio para revisar el informe el viernes a las 9' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:create, params:{{\"mensaje\":\"revisar el informe\",\"fechaHora\":\"<viernes próximo>T09:00:00-06:00\",\"duracionMinutos\":30}}, confidence:0.85\n\n"
                + "   -- ACTUALIZAR recordatorio --\n"
                + "   User: 'cambia el recordatorio ABC123 a las 5 de la tarde' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:update, params:{\"id\":\"ABC123\",\"fechaHora\":\"<fecha original con hora 17:00:00-06:00>\"}, confidence:0.85\n"
                + "   User: 'modifica el recordatorio ABC123 el mensaje a revisar contrato' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:update, params:{\"id\":\"ABC123\",\"mensaje\":\"revisar contrato\"}, confidence:0.9\n\n"
                + "   -- ELIMINAR recordatorio --\n"
                + "   User: 'elimina el recordatorio ABC123' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:delete, params:{\"id\":\"ABC123\"}, confidence:1.0\n"
                + "   User: 'borra el recordatorio ABC123' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:delete, params:{\"id\":\"ABC123\"}, confidence:1.0\n\n"
                + "   -- COMPLETAR recordatorio --\n"
                + "   User: 'marca como completado el recordatorio ABC123' -> intent:ApiCall, scope:API, provider:weblab, resource:recordatorios, action:complete, params:{\"id\":\"ABC123\"}, confidence:1.0\n\n"
                + "User: \""
                + userMessage
                + "\"\n\n"
                + "Return JSON with confidence >= 0.7 for valid commands:\n";
        }

        /// <summary>
        /// Extrae contenido del mensaje del asistente en la respuesta de Groq.
        /// Entrada: json (respuesta completa de Groq API).
        /// Salida: contenido string del mensaje del asistente.
        /// </summary>
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

        /// <summary>
        /// Extrae el primer objeto JSON válido del texto recibido.
        /// Entrada: text (texto que puede contener JSON mezclado con texto).
        /// Salida: JSON extraído o null si no se encuentra ninguno válido.
        /// </summary>
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

    /// <summary>
    /// Excepción lanzada cuando Groq responde 429 tras agotar todos los reintentos.
    /// Capturar en HomeViewModel para mostrar mensaje amigable al usuario.
    /// </summary>
    public sealed class GroqRateLimitException : Exception
    {
        public GroqRateLimitException(string message) : base(message) { }
    }
}