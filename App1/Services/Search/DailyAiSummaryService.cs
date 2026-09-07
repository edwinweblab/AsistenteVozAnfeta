using Anfeta.UI.Models.DailyAi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using Windows.Storage;

namespace Anfeta.UI.Services.Search;

public sealed class DailyAiSummaryService
{
    public const string Model = "gpt-4.1-mini-2025-04-14";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(35), MaxResponseContentBufferSize = 262144 };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _lastInput;
    private static DailyAiNarrative? _lastSummary;
    private static DateTimeOffset _lastRequest;
    public bool LastResponseFromCache { get; private set; }

    public static string BuildPayload(DailyAiSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        Context = new
        {
            Product = "ANFETA",
            Scope = "Agenda operativa del día; no representa todo el historial de Notion.",
            LocalDate = snapshot.Date.ToString("yyyy-MM-dd"),
            GeneratedLocalTime = snapshot.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz"),
            TimeZone = TimeZoneInfo.Local.DisplayName,
            Meanings = new
            {
                Activity = "Una tarea o actividad de Notion incluida en la agenda del día.",
                Pending = "Actividad aún no terminada; no significa que esté sin responsable.",
                Lagging = "Actividad marcada por las reglas de ANFETA como rezagada.",
                Checks = "Pasos de checklist completados/total. Cero total significa que no existe un checklist verificable, no que se hayan detectado cero tareas.",
                ProgressTodayPct = "Porcentaje de checks del día completados. No equivale a avance histórico total.",
                Person = "Responsable asignado. 'Sin asignar' es el único valor que indica ausencia de responsable.",
                StartEnd = "Horario local programado para la actividad. No es tiempo efectivamente trabajado."
            },
            ReadingRules = new[]
            {
                "Metrics contiene los totales oficiales; no sumar Projects para recalcularlos.",
                "Projects agrupa actividades y puede repetir responsables; People es carga agregada por persona.",
                "Activities es la evidencia de detalle para citar nombres, responsables y horarios.",
                "DataNote describe límites o frescura de los datos."
            }
        },
        snapshot.Date,
        snapshot.Metrics,
        Projects = snapshot.Projects.Select(project => new
        {
            project.ProjectName, project.Domain, project.Area, project.ResponsiblePeople,
            project.ActivitiesToday, project.CompletedToday, project.PendingToday,
            project.ProgressTodayPct, project.ProgressTotalPct, project.IsCritical,
            project.IsLagging, project.IsInReview, project.IsSuspended, project.IsUnassigned,
            project.CriticalReasons
        }),
        snapshot.People,
        snapshot.Areas,
        Activities = snapshot.Activities.Where(activity => !activity.IsHistorical).Select(activity => new
        {
            activity.Title, activity.ProjectName, activity.Domain, activity.Area, activity.Person,
            activity.StateLabel, activity.Start, activity.End, activity.ChecksTodayDone, activity.ChecksTodayTotal,
            activity.ProgressTodayPct, activity.ChecksTotalDone, activity.ChecksTotal, activity.ProgressTotalPct,
            activity.IsLagging, activity.IsInReview, activity.IsSuspended, activity.IsCompleted, activity.IsUnassigned
        }),
        snapshot.DataNote
    });

    public static string BuildAssistantPayload(DailyAiSnapshot snapshot, string question, string? conversationMemory = null, string? viewContext = null)
    {
        var query = (question ?? string.Empty).Trim();
        var normalized = query.ToLowerInvariant();
        var allActivities = snapshot.Activities.Where(x => !x.IsHistorical).ToList();
        IEnumerable<DailyAiActivitySnapshot> activities = allActivities;
        var mentionedPeople = snapshot.People
            .Where(person => normalized.Contains(person.Name.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            .Select(person => person.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (mentionedPeople.Count > 0)
            activities = activities.Where(x => mentionedPeople.Contains(x.Person));
        else if (normalized.Contains("sin responsable") || normalized.Contains("sin asignar"))
            activities = activities.Where(x => x.IsUnassigned);
        else if (normalized.Contains("rezagad") || normalized.Contains("urge") || normalized.Contains("prioridad"))
            activities = activities.Where(x => x.IsLagging || x.IsUnassigned);
        else if (normalized.Contains("próxim") || normalized.Contains("proxim") || normalized.Contains("horario"))
            activities = activities.Where(x => x.End >= DateTime.Now).OrderBy(x => x.Start);

        var asksAgendaByPerson = normalized.Contains("persona") || normalized.Contains("responsable") || normalized.Contains("agenda");
        if (asksAgendaByPerson && mentionedPeople.Count == 0)
            activities = activities.GroupBy(x => x.Person).SelectMany(group => group.OrderBy(x => x.Start).Take(3));

        var evidence = activities
            .OrderByDescending(x => x.IsLagging).ThenByDescending(x => x.IsUnassigned).ThenBy(x => x.Start)
            .Take(24)
            .Select(x => new
            {
                Actividad = x.Title,
                Proyecto = x.ProjectName,
                Area = x.Area,
                Responsable = x.Person,
                Estado = x.StateLabel,
                Inicio = x.Start.ToString("HH:mm"),
                Fin = x.End.ToString("HH:mm"),
                Rezagada = x.IsLagging,
                SinResponsable = x.IsUnassigned,
                ChecklistHoy = x.ChecksTodayTotal > 0 ? $"{x.ChecksTodayDone}/{x.ChecksTodayTotal}" : "No disponible"
            }).ToList();

        var priorityEvidence = snapshot.Activities.Where(x => !x.IsHistorical)
            .OrderByDescending(x => x.IsLagging)
            .ThenByDescending(x => x.IsUnassigned)
            .ThenBy(x => x.Start)
            .Take(15)
            .Select(x => new
            {
                Actividad = x.Title,
                Proyecto = x.ProjectName,
                Responsable = x.Person,
                Estado = x.StateLabel,
                Horario = $"{x.Start:HH:mm}-{x.End:HH:mm}",
                Motivos = new[]
                {
                    x.IsLagging ? "Rezagada" : null,
                    x.IsUnassigned ? "Sin responsable" : null,
                    x.ChecksTodayTotal == 0 ? "Sin checklist verificable" : null
                }.Where(reason => reason != null)
            }).ToList();

        return JsonSerializer.Serialize(new
        {
            Aplicacion = new
            {
                Nombre = "ANFETA",
                Descripcion = "Asistente empresarial de escritorio que consulta Notion y organiza la agenda operativa, actividades, proyectos, responsables, estados, horarios, recordatorios y avance diario.",
                Modulos = new[]
                {
                    "Búsqueda unificada en Notion, Dropbox, carpetas y archivos locales",
                    "Resultados con filtros, agrupación, detalles y cambio de estado",
                    "Calendario diario por persona con actividades y horarios",
                    "Recordatorios, reasignación, reprogramación y avisos de Meet",
                    "Comandos de voz y creación de actividades desde texto",
                    "Resumen operativo diario con métricas, HTML, PDF y Miao Vision",
                    "ANFETA AI con Groq Cloud u Ollama local"
                },
                CapacidadesDeEsteAsistente = new[]
                {
                    "Explicar funciones generales de ANFETA descritas en Modulos",
                    "Consultar totales y carga operativa del día",
                    "Identificar actividades, responsables, proyectos, estados y horarios incluidos en el contexto",
                    "Proponer prioridades y planes sin modificar datos"
                },
                LimitesDelAsistente = new[]
                {
                    "No ve cuerpos completos, comentarios ni archivos de Notion o Dropbox",
                    "No conoce actividades históricas fuera del snapshot diario",
                    "No puede ejecutar cambios desde esta conversación",
                    "Debe reconocer cuando una pregunta requiere abrir búsqueda, calendario, detalles o configuración"
                },
                Alcance = "Esta consulta usa la agenda de hoy ya sincronizada por ANFETA. Es de solo lectura y no modifica Notion.",
                FechaLocal = snapshot.Date.ToString("yyyy-MM-dd"),
                HoraGeneracion = snapshot.GeneratedAt.ToLocalTime().ToString("HH:mm"),
                ZonaHoraria = TimeZoneInfo.Local.DisplayName
            },
            Pregunta = query,
            MemoriaConversacional = string.IsNullOrWhiteSpace(conversationMemory) ? "Sin conversación previa" : conversationMemory,
            ContextoDeLaVistaActual = string.IsNullOrWhiteSpace(viewContext) ? "No disponible" : viewContext,
            TotalesOficiales = new
            {
                Proyectos = snapshot.Metrics.TotalProjects,
                Actividades = snapshot.Metrics.TotalActivities,
                Pendientes = snapshot.Metrics.PendingToday,
                Rezagadas = snapshot.Metrics.LaggingActivities,
                TerminadasHoy = snapshot.Metrics.CompletedToday,
                ProyectosEnRevision = snapshot.Metrics.ProjectsInReview,
                ProyectosSuspendidos = snapshot.Metrics.SuspendedProjects,
                ActividadesSinResponsable = snapshot.Metrics.UnassignedActivities,
                ActividadesSinChecklistVerificable = snapshot.Metrics.MissingChecklistActivities
            },
            CargaPorPersona = snapshot.People.Select(x => new
            {
                Responsable = x.Name,
                Actividades = x.ActivitiesToday,
                Pendientes = x.PendingToday,
                Terminadas = x.CompletedToday,
                Rezagadas = x.LaggingActivities,
                Proyectos = x.ProjectsCount,
                MinutosProgramados = x.ScheduledMinutes
            }),
            PrioridadesConEvidencia = priorityEvidence,
            ProyectosQueRequierenAtencion = snapshot.Projects.Where(x => x.IsCritical)
                .OrderByDescending(x => x.PendingToday).Take(12).Select(x => new
                {
                    Proyecto = x.ProjectName,
                    Dominio = x.Domain,
                    Responsable = x.ResponsiblePeople,
                    Actividades = x.ActivitiesToday,
                    Pendientes = x.PendingToday,
                    AvanceHoy = x.ProgressTodayPct,
                    Motivos = x.CriticalReasons
                }),
            ActividadesRelevantes = evidence,
            Cobertura = new { Incluidas = evidence.Count, Limite = 24, Filtro = mentionedPeople.Count > 0 ? "Responsable mencionado" : asksAgendaByPerson ? "Hasta 3 ejemplos por responsable" : "Según intención de la pregunta" },
            Reglas = new[]
            {
                "Actividad y check no son lo mismo.",
                "Pendiente no significa sin responsable.",
                "Solo 'Sin responsable' indica falta de asignación.",
                "Los horarios son programados, no tiempo trabajado.",
                "No hablar de checks salvo que la pregunta los mencione.",
                "Sin checklist verificable describe falta de evidencia; no ordenar completarlo sin confirmar que aplica.",
                "Una prioridad útil debe identificar actividad, proyecto, responsable u horario y el motivo basado en datos."
            },
            Limites = snapshot.DataNote
        });
    }

    public async Task<DailyAiNarrative> GenerateAsync(string key, DailyAiSnapshot snapshot, CancellationToken cancellationToken)
    {
        var input = BuildPayload(snapshot);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var cacheKey = snapshot.Date.ToString("yyyy-MM-dd") + snapshot.Fingerprint;
            if (_lastInput == cacheKey && _lastSummary != null) return _lastSummary;
            if (DateTimeOffset.UtcNow - _lastRequest < TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("Espera 30 segundos antes de otra consulta.");
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Falta la clave de OpenAI.");
            var stringArray = new { type = "array", items = new { type = "string" } };
            var schema = new
            {
                type = "object",
                properties = new
                {
                    summary = new { type = "string" }, attentionItems = stringArray, positiveSignals = stringArray,
                    priorities = stringArray, workloadObservations = stringArray
                },
                required = new[] { "summary", "attentionItems", "positiveSignals", "priorities", "workloadObservations" },
                additionalProperties = false
            };
            var payload = new
            {
                model = Model, store = false, max_output_tokens = 900,
                instructions = "Analiza únicamente el JSON proporcionado y responde en español. ANFETA ya calculó todas las métricas: no las recalcules ni inventes proyectos, personas, porcentajes, fechas, causas o actividades. Explica qué requiere atención usando exclusivamente CriticalReasons. No clasifiques suspendidos como críticos solo por falta de avance. Si no hay evidencia suficiente, indícalo. Prioriza recomendaciones operativas prudentes, no órdenes automáticas.",
                input,
                text = new { format = new { type = "json_schema", name = "anfeta_daily_narrative", strict = true, schema } }
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
            var result = ParseResponse(await response.Content.ReadAsStringAsync(cancellationToken));
            _lastInput = cacheKey;
            _lastSummary = result;
            return result;
        }
        finally { Gate.Release(); }
    }

    public static DailyAiNarrative ParseResponse(string json)
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
        if (text.Length == 0 || text.Length > 16000) throw new InvalidOperationException("OpenAI no devolvió un resumen válido.");
        var result = JsonSerializer.Deserialize<DailyAiNarrative>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result == null || string.IsNullOrWhiteSpace(result.Summary))
            throw new InvalidOperationException("La respuesta de IA no contiene todos los campos.");
        return result;
    }

    public async Task<DailyAiAssistantResult> AskAsync(string key, DailyAiSnapshot snapshot, string question,
        CancellationToken cancellationToken)
    {
        question = string.IsNullOrWhiteSpace(question) ? "Resume las prioridades y propón un plan prudente para hoy." : question.Trim();
        var cachePath = GetAssistantCachePath(snapshot, question);
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<DailyAiAssistantResult>(await File.ReadAllTextAsync(cachePath, cancellationToken));
                if (cached is not null) { LastResponseFromCache = true; return cached; }
            }
            catch { }
        }
        LastResponseFromCache = false;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - _lastRequest < TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Espera unos segundos antes de otra consulta.");
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Falta la clave de OpenAI.");
            var stringArray = new { type = "array", items = new { type = "string" } };
            var schema = new
            {
                type = "object",
                properties = new
                {
                    answer = new { type = "string" }, priorities = stringArray, dayPlan = stringArray,
                    whatsappMessage = new { type = "string" }, emailMessage = new { type = "string" }
                },
                required = new[] { "answer", "priorities", "dayPlan", "whatsappMessage", "emailMessage" },
                additionalProperties = false
            };
            var payload = new
            {
                model = Model, store = false, max_output_tokens = 1400,
                instructions = "Responde en español usando únicamente el snapshot de ANFETA. No inventes datos, causas, fechas ni responsables. Las prioridades y el plan son sugerencias de solo lectura: nunca afirmes que modificaste Notion. Si falta evidencia, dilo. El mensaje de WhatsApp debe ser breve y el correo profesional, ambos listos para copiar.",
                input = JsonSerializer.Serialize(new { Question = question, Snapshot = JsonSerializer.Deserialize<JsonElement>(BuildPayload(snapshot)) }),
                text = new { format = new { type = "json_schema", name = "anfeta_report_assistant", strict = true, schema } }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            _lastRequest = DateTimeOffset.UtcNow;
            using var response = await Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException((int)response.StatusCode switch
            {
                401 => "La clave de OpenAI no es válida.", 429 => "Se alcanzó el límite de consumo o solicitudes.",
                _ => "OpenAI no respondió correctamente; el reporte local sigue disponible."
            });
            var result = ParseAssistantResponse(await response.Content.ReadAsStringAsync(cancellationToken));
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(result), new UTF8Encoding(false), cancellationToken);
            return result;
        }
        finally { Gate.Release(); }
    }

    private static DailyAiAssistantResult ParseAssistantResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new InvalidOperationException("La respuesta de IA quedó incompleta.");
        var text = string.Concat(root.GetProperty("output").EnumerateArray()
            .Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "message")
            .SelectMany(x => x.GetProperty("content").EnumerateArray())
            .Where(x => x.GetProperty("type").GetString() == "output_text")
            .Select(x => x.GetProperty("text").GetString()));
        return JsonSerializer.Deserialize<DailyAiAssistantResult>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("OpenAI no devolvió una respuesta válida.");
    }

    private static string GetAssistantCachePath(DailyAiSnapshot snapshot, string question)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(question.ToLowerInvariant())))[..16];
        var fingerprint = new string(snapshot.Fingerprint.Where(char.IsLetterOrDigit).ToArray());
        if (fingerprint.Length > 32) fingerprint = fingerprint[..32];
        return Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "DailyAiCache", $"{snapshot.Date:yyyyMMdd}_{fingerprint}_{hash}.json");
    }
}
