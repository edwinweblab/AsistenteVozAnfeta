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

    // Lista de modelos detectados en el servidor Ollama local
    public static List<string> InstalledModels { get; } = new();

    // Caché de contexto para que las consultas consecutivas de la misma sesión no reevalúen todo el prompt
    private static int[]? _lastContext;

    // Timeout amplio para el cliente; el control de cancelación y streaming se realiza mediante CancellationToken y ResponseHeadersRead
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434"),
        Timeout = TimeSpan.FromMinutes(3)
    };

    public static void ResetContext()
    {
        _lastContext = null;
    }

    /// <summary>
    /// Precalienta silenciosamente el modelo en memoria RAM en segundo plano para evitar el retraso
    /// inicial de arranque en frío (Cold Start) de 5 a 10 segundos.
    /// </summary>
    public static async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = Model,
                keep_alive = "60m"
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync("/api/generate", content, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Silencioso: si Ollama no está en ejecución, no debe afectar el flujo principal
        }
    }

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
            InstalledModels.Clear();
            foreach (var item in models.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n))
                {
                    var name = n.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name)) InstalledModels.Add(name);
                }
            }

            // Prioridad a qwen3 u otros modelos con mayor vocabulario y razonamiento para respuestas completas
            detected = InstalledModels.FirstOrDefault(n => n.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase))
                    ?? InstalledModels.FirstOrDefault(n => n.StartsWith("qwen2.5:1.5b", StringComparison.OrdinalIgnoreCase))
                    ?? InstalledModels.FirstOrDefault(n => n.StartsWith("llama3.2", StringComparison.OrdinalIgnoreCase))
                    ?? InstalledModels.FirstOrDefault(n => n.StartsWith("qwen2.5:0.5b", StringComparison.OrdinalIgnoreCase))
                    ?? InstalledModels.FirstOrDefault(n => n.StartsWith("qwen2.5", StringComparison.OrdinalIgnoreCase))
                    ?? InstalledModels.FirstOrDefault(n => n.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
                    ?? InstalledModels.FirstOrDefault();

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
            "Eres ANFETA AI, copiloto operativo de Notion. " +
            "Responde SIEMPRE en texto natural limpio, claro, profesional y completo en español. " +
            "REGLAS OBLIGATORIAS: " +
            "1. Responde ÚNICAMENTE en viñetas limpias numeradas (1., 2., 3., etc.). " +
            "2. PROHIBIDO usar tablas markdown con barras |, guiones | --- | o esquemas técnicos. " +
            "3. Formato para cada punto: '1. Actividad - Responsable (Horario): Motivo'. " +
            "4. Cierra con un breve resumen del estado general sin dejar frases a medias.";

        var requestedCount = ExtractRequestedCount(effectiveQuestion);
        var numPredict = Math.Max(750, requestedCount * 60);
        var fullBuilder = new StringBuilder();

        await foreach (var chunk in StreamGenerateAsync(prompt, systemPrompt, cancellationToken, numPredict).ConfigureAwait(false))
        {
            fullBuilder.Append(chunk);
            onChunkReceived?.Invoke(chunk);
        }

        var text = fullBuilder.ToString().Trim();

        // Limpiar delimitadores markdown o etiquetas think residuales
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

    public static int ExtractRequestedCount(string question)
    {
        var match = System.Text.RegularExpressions.Regex.Match(question, @"\b([1-9]|[12][0-9]|30)\b");
        if (match.Success && int.TryParse(match.Value, out var n))
        {
            return Math.Max(5, Math.Min(30, n));
        }
        return 10;
    }

    private static List<DailyAiActivitySnapshot> DeduplicateActivities(IEnumerable<DailyAiActivitySnapshot> source)
    {
        var result = new List<DailyAiActivitySnapshot>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in source)
        {
            // Quitar tokens de fecha de recurrencia ej. "26-[07JUL]" o "26-[08AGO]" para agrupar tareas repetidas de meses pasados
            var cleanTitle = System.Text.RegularExpressions.Regex.Replace(a.Title, @"\b\d{1,2}-\[\d{2}[A-Z]{3}\]\b", "").Trim();
            cleanTitle = System.Text.RegularExpressions.Regex.Replace(cleanTitle, @"\s{2,}", " ");
            if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = a.Title;

            var key = $"{cleanTitle}::{a.Person}";
            if (seenKeys.Add(key))
            {
                result.Add(a);
            }
        }
        return result;
    }

    private static string BuildConcisePrompt(DailyAiSnapshot snapshot, string question, string? memory = null, string? viewContext = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CONSULTA: {question}");
        if (!string.IsNullOrWhiteSpace(memory) && memory != "Sin conversación previa")
        {
            sb.AppendLine($"MEMORIA: {memory}");
        }
        sb.AppendLine($"TOTALES REALES: {snapshot.Metrics.TotalActivities} actividades totales, {snapshot.Metrics.PendingToday} pendientes hoy, {snapshot.Metrics.LaggingActivities} rezagadas, {snapshot.Metrics.UnassignedActivities} sin responsable.");

        var lowerQ = question.ToLowerInvariant();
        bool isUnassignedQuery = lowerQ.Contains("sin responsable") || lowerQ.Contains("sin asignar") || lowerQ.Contains("unassigned");

        // Buscar si el usuario preguntó por una persona en específico (ej. Karla, Genaro, Isaias, Brian...)
        var personMatch = snapshot.People.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) && (
            lowerQ.Contains(p.Name.ToLowerInvariant()) ||
            p.Name.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries).Any(part => part.Length >= 3 && lowerQ.Contains(part.ToLowerInvariant()))
        ));

        var requestedCount = ExtractRequestedCount(question);

        List<DailyAiActivitySnapshot> activities;

        if (isUnassignedQuery)
        {
            var rawList = snapshot.Activities
                .Where(x => !x.IsHistorical && x.IsUnassigned)
                .OrderByDescending(x => x.IsLagging)
                .ThenBy(x => x.Start)
                .ToList();

            activities = DeduplicateActivities(rawList);

            sb.AppendLine($"ACTIVIDADES SIN RESPONSABLE DETECTADAS ({activities.Count} en total):");
            foreach (var a in activities)
            {
                var tag = a.IsLagging ? " [REZAGADA]" : "";
                sb.AppendLine($"- {a.Title} (Proyecto: {a.ProjectName}, Horario: {a.Start:HH:mm}){tag}");
            }

            sb.AppendLine($"Instrucción: Lista ÚNICAMENTE las {activities.Count} actividades sin responsable listadas arriba (hay exactamente {activities.Count}). Para cada una pon: 'Responsable: Sin responsable'. No inventes responsables ni agregues actividades con responsable.");
            return sb.ToString();
        }
        else if (personMatch != null)
        {
            var rawList = snapshot.Activities
                .Where(x => !x.IsHistorical && (
                    x.Person.Contains(personMatch.Name, StringComparison.OrdinalIgnoreCase) ||
                    personMatch.Name.Contains(x.Person, StringComparison.OrdinalIgnoreCase) ||
                    x.Title.Contains(personMatch.Name, StringComparison.OrdinalIgnoreCase)
                ))
                .OrderByDescending(x => x.IsLagging)
                .ThenBy(x => x.Start)
                .ToList();

            activities = DeduplicateActivities(rawList).Take(requestedCount + 4).ToList();

            sb.AppendLine($"ACTIVIDADES DE {personMatch.Name.ToUpperInvariant()} ({activities.Count} encontradas en total):");
            foreach (var a in activities)
            {
                var tag = a.IsLagging ? " [REZAGADA]" : "";
                sb.AppendLine($"- {a.Title} (Responsable: {personMatch.Name}, Horario: {a.Start:HH:mm}){tag}");
            }

            sb.AppendLine($"Instrucción: Primero menciona claramente cuántas actividades tiene {personMatch.Name} (tiene exactamente {activities.Count}). Luego lista cada una en viñetas numeradas (1., 2., ...). Formato obligatorio: '1. Actividad - Responsable: {personMatch.Name}, Horario: HH:mm, Motivo: Detalle'. NO uses 'Sin responsable', todas pertenecen a {personMatch.Name}.");
            return sb.ToString();
        }
        else if (lowerQ.Contains("próxima") || lowerQ.Contains("proxima") || lowerQ.Contains("horario") || lowerQ.Contains("cronológ"))
        {
            var rawList = snapshot.Activities
                .Where(x => !x.IsHistorical)
                .OrderBy(x => x.Start)
                .ToList();

            activities = DeduplicateActivities(rawList).Take(requestedCount + 4).ToList();
        }
        else
        {
            var rawList = snapshot.Activities
                .Where(x => !x.IsHistorical)
                .OrderByDescending(x => x.IsLagging)
                .ThenByDescending(x => x.Start.Date == snapshot.Date.Date)
                .ToList();

            activities = DeduplicateActivities(rawList).Take(requestedCount + 4).ToList();
        }

        if (activities.Count > 0)
        {
            sb.AppendLine($"ACTIVIDADES DISPONIBLES (Muestra las {Math.Min(requestedCount, activities.Count)} más relevantes sin duplicar):");
            foreach (var a in activities)
            {
                var tag = a.IsLagging ? " [REZAGADA]" : "";
                var resp = a.IsUnassigned ? "Sin responsable" : a.Person;
                sb.AppendLine($"- {a.Title} ({resp}, {a.Start:HH:mm}){tag}");
            }
        }

        if (snapshot.People.Count > 0)
        {
            var topPeople = snapshot.People.OrderByDescending(p => p.PendingToday).Take(4).ToList();
            sb.AppendLine("CARGA: " + string.Join(", ", topPeople.Select(p => $"{p.Name} ({p.PendingToday} pend)")));
        }

        var targetCount = Math.Min(requestedCount, activities.Count);
        sb.AppendLine($"Instrucción: Lista exactamente {targetCount} actividades diferentes sin duplicarlas en viñetas numeradas limpias (1., 2., ... {targetCount}.). Formato: '1. Actividad - Responsable: Nombre, Horario: HH:mm, Motivo: Detalle'. NO USES TABLAS. Cierra la respuesta por completo.");
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

    /// <summary>
    /// Calcula el número óptimo de hilos de CPU evitando saturar los hilos lógicos/hiperhilos,
    /// lo cual degrada la memoria caché L3 y la velocidad de inferencia de Ollama en procesadores multinúcleo.
    /// </summary>
    private static int GetOptimalThreadCount()
    {
        var logicalCores = Environment.ProcessorCount;
        if (logicalCores >= 12) return 6; // Para 6 o más núcleos físicos (como Ryzen 5 5500U)
        if (logicalCores >= 8) return 5;
        return Math.Max(2, logicalCores - 1);
    }

    public static async IAsyncEnumerable<string> StreamGenerateAsync(
        string prompt,
        string system,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int numPredict = 750)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["system"] = system,
            ["prompt"] = prompt,
            ["stream"] = true,
            ["think"] = false,
            ["keep_alive"] = "60m",
            ["options"] = new
            {
                temperature = 0.2,
                num_ctx = 4096,
                num_predict = numPredict,
                num_thread = GetOptimalThreadCount()
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

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("response", out var respProp))
                    chunk = respProp.GetString();
            }
            catch
            {
                // Ignorar fragmentos parciales
            }

            if (!string.IsNullOrEmpty(chunk))
                yield return chunk;
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
