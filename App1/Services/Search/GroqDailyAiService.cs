using Anfeta.UI.Models.DailyAi;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Search;

public sealed class GroqDailyAiService
{
    public const string Model = "openai/gpt-oss-20b";
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    public async Task<DailyAiAssistantResult> AskAsync(string apiKey, DailyAiSnapshot snapshot, string question,
        CancellationToken cancellationToken, string? conversationMemory = null, string? viewContext = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("No hay una clave activa de Groq.");
        question = string.IsNullOrWhiteSpace(question) ? "Indica las prioridades reales de hoy." : question.Trim();
        var system = "Eres ANFETA AI, copiloto de una aplicación empresarial de escritorio conectada a Notion. " +
            "Responde en español claro usando solamente los datos proporcionados. TotalesOficiales es la fuente definitiva. " +
            "Devuelve OBLIGATORIAMENTE un JSON con los campos: " +
            "\"answer\" (string con tu respuesta directa menor a 180 palabras), " +
            "\"priorities\" (array de strings con las prioridades), " +
            "\"dayPlan\" (array de strings con el plan del día), " +
            "\"whatsappMessage\" (string, vacío si no se pidió), " +
            "\"emailMessage\" (string, vacío si no se pidió). " +
            "No inventes información, no uses Markdown y nunca afirmes que modificaste Notion. Responde estrictamente el JSON.";
        var body = new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = DailyAiSummaryService.BuildAssistantPayload(snapshot, question, conversationMemory, viewContext) }
            },
            temperature = 0.1,
            max_completion_tokens = 2048,
            response_format = new
            {
                type = "json_object"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errJson = await response.Content.ReadAsStringAsync(cancellationToken);
            string? msg = null;
            try
            {
                using var doc = JsonDocument.Parse(errJson);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("message", out var m))
                {
                    msg = m.GetString();
                }
            }
            catch { }

            throw new InvalidOperationException((int)response.StatusCode switch
            {
                401 => "La clave de Groq no es válida o fue revocada.",
                429 => "Groq alcanzó temporalmente su límite gratuito.",
                400 => msg ?? "Groq rechazó el formato de la consulta.",
                404 => msg ?? "El modelo de Groq no está disponible (404).",
                413 => "El contexto enviado a Groq es demasiado grande.",
                _ => msg ?? $"Groq no está disponible en este momento (código {(int)response.StatusCode})."
            });
        }

        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var content = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        var raw = (content ?? string.Empty).Trim();
        if (raw.StartsWith("```"))
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start) raw = raw.Substring(start, end - start + 1);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var answer = "";
            if (root.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String)
                answer = a.GetString() ?? string.Empty;
            else if (root.TryGetProperty("respuesta", out var r) && r.ValueKind == JsonValueKind.String)
                answer = r.GetString() ?? string.Empty;
            else if (root.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String)
                answer = sm.GetString() ?? string.Empty;

            var priorities = ReadStringList(root, "priorities");
            var dayPlan = ReadStringList(root, "dayPlan");
            var wa = root.TryGetProperty("whatsappMessage", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;
            var em = root.TryGetProperty("emailMessage", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(answer))
            {
                if (priorities.Count > 0)
                    answer = $"Prioridades de hoy:\n• {string.Join("\n• ", priorities)}";
                else if (dayPlan.Count > 0)
                    answer = $"Plan del día:\n• {string.Join("\n• ", dayPlan)}";
                else
                    answer = "Consulta procesada correctamente.";
            }

            return new DailyAiAssistantResult(answer, priorities, dayPlan, wa, em);
        }
        catch (Exception)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return new DailyAiAssistantResult(raw, Array.Empty<string>(), Array.Empty<string>(), string.Empty, string.Empty);
            }
            throw new InvalidOperationException("Groq devolvió una respuesta vacía.");
        }
    }

    private static System.Collections.Generic.IReadOnlyList<string> ReadStringList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new System.Collections.Generic.List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                list.Add(value.Trim());
        }
        return list;
    }

    private static object AssistantSchema()
    {
        var strings = new { type = "array", items = new { type = "string" } };
        return new
        {
            type = "object",
            properties = new
            {
                answer = new { type = "string" }, priorities = strings, dayPlan = strings,
                whatsappMessage = new { type = "string" }, emailMessage = new { type = "string" }
            },
            required = new[] { "answer", "priorities", "dayPlan", "whatsappMessage", "emailMessage" },
            additionalProperties = false
        };
    }
}
