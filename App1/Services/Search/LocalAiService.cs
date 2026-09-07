using Anfeta.UI.Models.DailyAi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Search;

public sealed record LocalAiStatus(bool ServerAvailable, bool ModelInstalled, string Message);

public sealed class LocalAiService
{
    public static string Model { get; set; } = "qwen3:1.7b";
    public const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";

    // Timeout amplio para el cliente; el control de cancelación y streaming se realiza mediante CancellationToken y ResponseHeadersRead
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434"),
        Timeout = TimeSpan.FromMinutes(3)
    };

    public async Task<LocalAiStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync("/api/tags", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, false, "Ollama no está respondiendo.");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return new(true, false, "Ollama está activo, pero no tiene modelos instalados.");

            string? detected = null;
            foreach (var item in models.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n))
                {
                    var name = n.GetString() ?? string.Empty;
                    if (name.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("qwen2.5", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
                    {
                        detected = name;
                        break;
                    }
                    detected ??= name;
                }
            }

            if (!string.IsNullOrEmpty(detected))
            {
                Model = detected;
                return new(true, true, $"IA local lista · {Model}");
            }

            return new(true, false, "Ollama está listo; falta descargar un modelo.");
        }
        catch { return new(false, false, "Ollama no está instalado o no está ejecutándose."); }
    }

    public async Task PullModelAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { model = Model, stream = false });
        using var response = await Client.PostAsync("/api/pull", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"No se pudo descargar el modelo {Model}.");
    }

    public async Task<DailyAiAssistantResult> AskAsync(
        DailyAiSnapshot snapshot,
        string question,
        CancellationToken cancellationToken,
        string? conversationMemory = null,
        string? viewContext = null,
        Action<string>? onChunkReceived = null)
    {
        var effectiveQuestion = string.IsNullOrWhiteSpace(question)
            ? "Identifica las actividades prioritarias de hoy y resume el estado operativo."
            : question.Trim();

        var prompt = BuildConcisePrompt(snapshot, effectiveQuestion, conversationMemory, viewContext);

        const string systemPrompt =
            "Eres ANFETA AI, copiloto operativo de una aplicación de escritorio conectada a Notion. " +
            "Responde SIEMPRE en texto natural limpio, claro, profesional y directo en español. " +
            "REGLAS OBLIGATORIAS: " +
            "1. NO devuelvas JSON, NO devuelvas llaves {}, corchetes [] ni esquemas técnicos. " +
            "2. Responde directamente la pregunta del usuario en 2 a 4 oraciones o viñetas concisas. " +
            "3. Cita nombres de actividades, responsables, proyectos y horarios según el contexto.";

        var fullBuilder = new StringBuilder();

        await foreach (var chunk in StreamGenerateAsync(prompt, systemPrompt, cancellationToken).ConfigureAwait(false))
        {
            fullBuilder.Append(chunk);
            onChunkReceived?.Invoke(chunk);
        }

        var text = fullBuilder.ToString().Trim();

        // Limpiar delimitadores markdown si el modelo los añade
        if (text.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            var lines = text.Split('\n');
            text = string.Join('\n', lines.Where(l => !l.Trim().StartsWith("```"))).Trim();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "No se detectaron actividades críticas o rezagadas en la agenda de hoy. Todo se encuentra al corriente.";
        }

        return new DailyAiAssistantResult(text, Array.Empty<string>(), Array.Empty<string>(), string.Empty, string.Empty);
    }

    private static string BuildConcisePrompt(DailyAiSnapshot snapshot, string question, string? memory = null, string? viewContext = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CONSULTA DEL USUARIO: {question}");
        if (!string.IsNullOrWhiteSpace(memory) && memory != "Sin conversación previa")
        {
            sb.AppendLine($"MEMORIA PREVIA: {memory}");
        }
        sb.AppendLine();
        sb.AppendLine($"FECHA: {snapshot.Date:dddd, dd 'de' MMMM yyyy}");
        sb.AppendLine($"TOTALES: {snapshot.Metrics.TotalActivities} actividades, {snapshot.Metrics.PendingToday} pendientes, {snapshot.Metrics.LaggingActivities} rezagadas, {snapshot.Metrics.UnassignedActivities} sin responsable.");

        var activities = snapshot.Activities.Where(x => !x.IsHistorical).Take(10).ToList();
        if (activities.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("ACTIVIDADES DE LA AGENDA:");
            foreach (var a in activities)
            {
                var tag = a.IsLagging ? " [REZAGADA]" : a.IsUnassigned ? " [SIN ASIGNAR]" : "";
                sb.AppendLine($"- {a.Title} | Responsable: {a.Person} | Proyecto: {a.ProjectName} | Horario: {a.Start:HH:mm}-{a.End:HH:mm} | Estado: {a.StateLabel}{tag}");
            }
        }

        if (snapshot.People.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RESPONSABLES:");
            foreach (var p in snapshot.People.Take(6))
            {
                sb.AppendLine($"- {p.Name}: {p.ActivitiesToday} actividades ({p.PendingToday} pendientes)");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Instrucción: Responde en texto natural claro en español. Ve directo al grano sin código ni JSON.");
        return sb.ToString();
    }

    public Task<DailyAiNarrative> SummarizeAsync(
        DailyAiSnapshot snapshot,
        CancellationToken cancellationToken,
        Action<string>? onChunkReceived = null)
    {
        return Task.FromResult(new DailyAiNarrative(
            $"Resumen del {snapshot.Date:dd/MM/yyyy}: {snapshot.Metrics.TotalActivities} actividades, {snapshot.Metrics.PendingToday} pendientes y {snapshot.Metrics.LaggingActivities} rezagadas.",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
    }

    public static async IAsyncEnumerable<string> StreamGenerateAsync(
        string prompt,
        string system,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new
        {
            model = Model,
            system,
            prompt,
            stream = true,
            think = false,
            keep_alive = "30m",
            options = new
            {
                temperature = 0.2,
                num_ctx = 1024,
                num_predict = 180,
                num_thread = Math.Max(2, Environment.ProcessorCount - 1)
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate") { Content = content };

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"La IA local devolvió un error HTTP {(int)response.StatusCode}.");

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? chunk = null;
            bool isDone = false;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("response", out var respProp))
                    chunk = respProp.GetString();
                if (root.TryGetProperty("done", out var doneProp))
                    isDone = doneProp.GetBoolean();
            }
            catch
            {
                // Ignorar fragmentos parciales
            }

            if (!string.IsNullOrEmpty(chunk))
                yield return chunk;

            if (isDone) break;
        }
    }

    private static T ParseResult<T>(string text)
    {
        try
        {
            if (typeof(T) == typeof(DailyAiAssistantResult))
            {
                using var doc = JsonDocument.Parse(text);
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
                        answer = $"Prioridades del día:\n• {string.Join("\n• ", priorities)}";
                    else if (dayPlan.Count > 0)
                        answer = $"Plan y seguimiento:\n• {string.Join("\n• ", dayPlan)}";
                    else
                        answer = "No se detectaron actividades críticas pendientes en este momento. La agenda se encuentra al día.";
                }

                return (T)(object)new DailyAiAssistantResult(answer, priorities, dayPlan, wa, em);
            }

            if (typeof(T) == typeof(DailyAiNarrative))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
                var attention = ReadStringList(root, "attentionItems");
                var positive = ReadStringList(root, "positiveSignals");
                var priorities = ReadStringList(root, "priorities");
                var workload = ReadStringList(root, "workloadObservations");

                if (string.IsNullOrWhiteSpace(summary))
                    summary = "Resumen de la jornada completado.";

                return (T)(object)new DailyAiNarrative(summary, attention, positive, priorities, workload);
            }

            return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Respuesta nula.");
        }
        catch
        {
            // Fallback en caso de que el modelo haya devuelto texto plano en lugar de JSON
            var fallbackText = string.IsNullOrWhiteSpace(text)
                ? "No se reportan actividades críticas en este momento."
                : text;

            if (typeof(T) == typeof(DailyAiAssistantResult))
            {
                return (T)(object)new DailyAiAssistantResult(fallbackText, Array.Empty<string>(), Array.Empty<string>(), string.Empty, string.Empty);
            }

            if (typeof(T) == typeof(DailyAiNarrative))
            {
                return (T)(object)new DailyAiNarrative(fallbackText, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            }

            throw new InvalidOperationException("La IA local devolvió una respuesta incompleta.");
        }
    }

    private static List<string> ReadStringList(JsonElement root, string propertyName)
    {
        var list = new List<string>();
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var val = item.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) list.Add(val);
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var parts = new List<string>();
                    foreach (var p in item.EnumerateObject())
                    {
                        var val = p.Value.ToString();
                        if (!string.IsNullOrWhiteSpace(val)) parts.Add(val);
                    }
                    if (parts.Count > 0) list.Add(string.Join(" · ", parts));
                }
                else
                {
                    var val = item.ToString();
                    if (!string.IsNullOrWhiteSpace(val)) list.Add(val);
                }
            }
        }
        return list;
    }
}
