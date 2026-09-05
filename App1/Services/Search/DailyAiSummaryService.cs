using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Search;

public sealed record DailySummaryCounts(int Activities, int Pending, int Review, int Suspended,
    int Finished, int Other, int ChecklistKnown, int ChecksDone, int ChecksTotal);

public sealed class DailyAiSummaryService
{
    public const string Model = "gpt-4.1-mini-2025-04-14";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(35), MaxResponseContentBufferSize = 131072 };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _lastInput;
    private static string? _lastSummary;
    private static DateTimeOffset _lastRequest;

    public static string BuildPayload(DailySummaryCounts counts) => JsonSerializer.Serialize(counts);

    public async Task<string> GenerateAsync(string key, DailySummaryCounts counts, CancellationToken cancellationToken)
    {
        var input = BuildPayload(counts);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            // Caché de una instantánea por día, compartida entre ventanas del proceso.
            var cacheKey = DateTime.Today.ToString("yyyy-MM-dd") + input;
            if (_lastInput == cacheKey && _lastSummary != null) return _lastSummary;
            if (DateTimeOffset.UtcNow - _lastRequest < TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("Espera 30 segundos antes de otra consulta.");
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Falta la clave de OpenAI.");
            var schema = new
            {
                type = "object",
                properties = new { resumen = new { type = "string" }, sugerencia = new { type = "string" } },
                required = new[] { "resumen", "sugerencia" }, additionalProperties = false
            };
            var payload = new
            {
                model = Model, store = false, max_output_tokens = 500,
                instructions = "Resume en español, máximo 120 palabras, únicamente estos conteos de actividades cargadas de hoy. " +
                    "Pending incluye pendientes y por hacer. Other es estado desconocido. ChecklistKnown es número de actividades con checklist consultado; " +
                    "ChecksDone/ChecksTotal son subtareas SOLO de esas actividades. No son avance horario ni avance total del proyecto. " +
                    "No inventes fechas, nombres, urgencias, horas, causas ni compromisos. No asumas que suspendidas están atrasadas. " +
                    "Da una sugerencia prudente de revisión, no una instrucción automática. Reconoce cobertura parcial de datos.",
                input,
                text = new { format = new { type = "json_schema", name = "resumen_diario", strict = true, schema } }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            _lastRequest = DateTimeOffset.UtcNow;
            using var response = await Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException((int)response.StatusCode switch
                {
                    401 => "La clave de OpenAI no es válida. Revisa la configuración.",
                    403 or 404 => "La cuenta no tiene acceso al modelo configurado.",
                    429 => "OpenAI indica límite de consumo o solicitudes. Revisa el saldo y los límites de tu cuenta.",
                    _ => "OpenAI no respondió correctamente. El resumen local sigue disponible."
                });
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = ParseResponse(json);
            _lastInput = cacheKey;
            _lastSummary = result;
            return result;
        }
        finally { Gate.Release(); }
    }

    public static string ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new InvalidOperationException("El resumen quedó incompleto. No se mostrará como resultado válido.");
        var text = string.Concat(root.GetProperty("output").EnumerateArray()
            .Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "message")
            .SelectMany(x => x.GetProperty("content").EnumerateArray())
            .Where(x => x.GetProperty("type").GetString() == "output_text")
            .Select(x => x.GetProperty("text").GetString()));
        if (text.Length == 0 || text.Length > 8000)
            throw new InvalidOperationException("OpenAI no devolvió un resumen válido.");
        using var summary = JsonDocument.Parse(text);
        var result = summary.RootElement.GetProperty("resumen").GetString();
        var suggestion = summary.RootElement.GetProperty("sugerencia").GetString();
        if (string.IsNullOrWhiteSpace(result) || string.IsNullOrWhiteSpace(suggestion))
            throw new InvalidOperationException("La respuesta de IA no contiene todos los campos.");
        return result + "\n\nSugerencia: " + suggestion;
    }
}
