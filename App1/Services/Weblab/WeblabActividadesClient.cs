// Services/Weblab/WeblabActividadesClient.cs
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Weblab
{
    public sealed class WeblabActividadesClient
    {
        private readonly HttpClient _http;           // Para crear actividades (SHARED)
        private readonly HttpClient _httpLocal;      // Para obtener email (LOCAL)
        private readonly WeblabAuthClient _auth;

        public WeblabActividadesClient(HttpClient http, HttpClient httpLocal, WeblabAuthClient auth)
        {
            _http = http;
            _httpLocal = httpLocal;
            _auth = auth;
        }

        /// <summary>
        /// Crea una actividad y luego asigna al usuario actual
        /// Entrada: CreateActividadRequest
        /// Salida: ApiPlainResponse
        /// </summary>
        public async Task<ApiPlainResponse> CreateActivityAsync(CreateActividadRequest req, CancellationToken ct = default)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Titulo))
                    return new ApiPlainResponse { Ok = false, PlainText = "Falta el título para crear la actividad." };

                using var form = new MultipartFormDataContent();

                void AddString(string name, string? value)
                {
                    var v = (value ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(v) || v.Equals("null", StringComparison.OrdinalIgnoreCase))
                        return;
                    form.Add(new StringContent(v, Encoding.UTF8), name);
                }

                AddString("titulo", req.Titulo);
                AddString("status", req.Status);
                AddString("prioridad", req.Prioridad);
                AddString("tipo", req.Tipo);
                AddString("proyectoId", req.ProyectoId);
                AddString("anotaciones", req.Anotaciones);
                AddString("pasosYLinks", req.PasosYLinks);
                AddString("dueStart", req.DueStart);
                AddString("dueEnd", req.DueEnd);

                if (req.Pendientes != null && req.Pendientes.Count > 0)
                {
                    var pendientesJson = JsonSerializer.Serialize(req.Pendientes);
                    form.Add(new StringContent(pendientesJson, Encoding.UTF8, "application/json"), "pendientes");
                }

                if (req.ArchivosPaths != null)
                {
                    foreach (var path in req.ArchivosPaths)
                    {
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                        var bytes = await File.ReadAllBytesAsync(path, ct);
                        var content = new ByteArrayContent(bytes);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        form.Add(content, "archivos", Path.GetFileName(path));
                    }
                }

                if (req.PendienteImagesPaths != null)
                {
                    foreach (var path in req.PendienteImagesPaths)
                    {
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                        var bytes = await File.ReadAllBytesAsync(path, ct);
                        var content = new ByteArrayContent(bytes);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        form.Add(content, "pendienteImages", Path.GetFileName(path));
                    }
                }

#if DEBUG
                System.Diagnostics.Debug.WriteLine("===== CREATE ACTIVITY DEBUG =====");
                System.Diagnostics.Debug.WriteLine($"POST {_http.BaseAddress}api/actividades");
                System.Diagnostics.Debug.WriteLine($"Titulo: {req.Titulo}");
                System.Diagnostics.Debug.WriteLine($"Prioridad: {req.Prioridad}");
                System.Diagnostics.Debug.WriteLine("=================================");
#endif

                using var resp = await _http.PostAsync("/api/actividades", form, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse
                    {
                        Ok = false,
                        PlainText = $"No pude crear la actividad. HTTP {(int)resp.StatusCode}: {json}"
                    };

                string? activityId = null;
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("id", out var idEl))
                    {
                        activityId = idEl.GetString();
                    }
                }

                if (string.IsNullOrWhiteSpace(activityId))
                    return new ApiPlainResponse { Ok = true, PlainText = $"Actividad creada: {req.Titulo}." };

                List<string> assigneeIds;

                if (req.Assignees != null && req.Assignees.Count > 0)
                {
                    assigneeIds = req.Assignees.Select(a => a.CollaboratorId).ToList();
                }
                else
                {
                    var (okUser, email, _, collaboratorId) = await _auth.GetCurrentUserAsync(ct);
                    if (!okUser || string.IsNullOrWhiteSpace(collaboratorId))
                        return new ApiPlainResponse { Ok = true, PlainText = $"Actividad creada: {req.Titulo}. Sin asignar (no se pudo obtener tu usuario)." };

                    assigneeIds = new List<string> { collaboratorId };
                }

                var assignBody = JsonSerializer.Serialize(new { assignees = assigneeIds });
                var assignContent = new StringContent(assignBody, Encoding.UTF8, "application/json");

#if DEBUG
                System.Diagnostics.Debug.WriteLine("===== ASSIGN ACTIVITY DEBUG =====");
                System.Diagnostics.Debug.WriteLine($"PUT {_http.BaseAddress}api/actividades/{activityId}");
                System.Diagnostics.Debug.WriteLine($"Body: {assignBody}");
                System.Diagnostics.Debug.WriteLine("=================================");
#endif

                using var assignResp = await _http.PutAsync($"/api/actividades/{activityId}", assignContent, ct);
                var assignJson = await assignResp.Content.ReadAsStringAsync(ct);

#if DEBUG
                System.Diagnostics.Debug.WriteLine("===== ASSIGN RESPONSE =====");
                System.Diagnostics.Debug.WriteLine($"Status: {assignResp.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"Body: {assignJson}");
                System.Diagnostics.Debug.WriteLine("===========================");
#endif

                if (!assignResp.IsSuccessStatusCode)
                {
                    if (req.Assignees != null && req.Assignees.Count > 0)
                    {
                        var names = string.Join(", ", req.Assignees.Select(a => a.Name));
                        return new ApiPlainResponse
                        {
                            Ok = true,
                            PlainText = $"Actividad creada pero no pude asignarla a: {names}."
                        };
                    }
                    return new ApiPlainResponse
                    {
                        Ok = true,
                        PlainText = $"Actividad creada pero no pude asignártela: {req.Titulo}."
                    };
                }

                if (req.Assignees == null || req.Assignees.Count == 0)
                {
                    return new ApiPlainResponse { Ok = true, PlainText = $"Actividad creada y asignada a ti" };
                }
                else if (req.Assignees.Count == 1)
                {
                    return new ApiPlainResponse { Ok = true, PlainText = $"Actividad creada y asignada a {req.Assignees[0].Name}: {req.Titulo}." };
                }
                else
                {
                    var names = string.Join(", ", req.Assignees.Select(a => a.Name));
                    return new ApiPlainResponse { Ok = true, PlainText = $"Actividad creada y asignada a: {names}." };
                }
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch (Exception ex)
            {
                return new ApiPlainResponse { Ok = false, PlainText = $"Error: {ex.Message}" };
            }
        }
        public async Task<List<CachedActivityItem>> GetMyActivitiesForCacheAsync(CancellationToken ct = default)
        {
            try
            {
                var result = new List<CachedActivityItem>();

                var (ok, assignee, _, _) = await _auth.GetCurrentUserAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(assignee))
                    return result;

                var url = $"/api/actividades/assignee/{Uri.EscapeDataString(assignee)}";

                using var resp = await _httpLocal.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return result;

                var items = ExtractActivitiesDetailed(json);

                foreach (var a in items)
                {
                    result.Add(new CachedActivityItem
                    {
                        Id = a.Id,
                        Title = a.Title,
                        Status = a.Status,
                        Priority = a.Priority,
                        DueStart = a.DueStart,
                        DueEnd = a.DueEnd
                    });
                }

                return result;
            }
            catch
            {
                return new List<CachedActivityItem>();
            }
        }
        /// <summary>
        /// Mis actividades de HOY (sin pasar assignee)
        /// </summary>
        public async Task<ApiPlainResponse> GetMyTodayActivitiesAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, assignee, _, _) = await _auth.GetCurrentUserAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(assignee))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu usuario." };

                return await GetTodayActivitiesAsync(assignee, ct);
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando tus actividades de hoy." };
            }
        }

        /// <summary>
        /// Mis actividades (todas) con orden por dueStart
        /// GET /api/actividades/assignee/{assignee}
        /// </summary>
        public async Task<ApiPlainResponse> GetMyActivitiesAsync(int limit = 10, CancellationToken ct = default)
        {
            try
            {
                var (ok, assignee, name, _) = await _auth.GetCurrentUserAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(assignee))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu usuario." };

                // ✅ CORREGIDO: Usar la URL correcta
                var url = $"/api/actividades/assignee/{Uri.EscapeDataString(assignee)}";

                using var resp = await _httpLocal.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tus actividades." };

                var items = ExtractActivitiesDetailed(json);

                if (items.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes actividades asignadas." };

                // Orden: por dueStart (asc). Si no tiene, al final.
                items.Sort((a, b) =>
                {
                    var da = a.DueStart ?? DateTimeOffset.MaxValue;
                    var db = b.DueStart ?? DateTimeOffset.MaxValue;
                    return da.CompareTo(db);
                });

                if (limit <= 0) limit = 10;
                if (items.Count > limit) items = items.GetRange(0, limit);

                var header = $"Actividades de {name ?? "tu usuario"}: {items.Count}.";
                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildActivitiesDetailedPlainText(header, items)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando tus actividades." };
            }
        }

        /// <summary>
        /// GET /api/actividades - Listar títulos
        /// </summary>
        public async Task<ApiPlainResponse> ListTitlesAsync(int limit, CancellationToken ct = default)
        {
            try
            {
                using var resp = await _httpLocal.GetAsync("/api/actividades", ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener actividades." };

                var titles = ExtractTitles(json, limit);

                if (titles.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No hay actividades." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildTitlesPlainText("Actividades", titles)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando actividades." };
            }
        }

        /// <summary>
        /// GET /api/actividades/buscar?q=texto
        /// </summary>
        public async Task<ApiPlainResponse> SearchTitlesAsync(string q, int limit, CancellationToken ct = default)
        {
            try
            {
                // ✅ CORREGIDO: Usar la URL correcta
                var url = $"/api/actividades/buscar?q={Uri.EscapeDataString(q)}";

                using var resp = await _httpLocal.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude buscar actividades." };

                var titles = ExtractTitles(json, limit);

                if (titles.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = $"No encontré actividades para: {q}." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildTitlesPlainText($"Resultados para {q}", titles)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error buscando actividades." };
            }
        }

        /// <summary>
        /// GET /api/actividades/assignee/:assignee/del-dia
        /// </summary>
        public async Task<ApiPlainResponse> GetTodayActivitiesAsync(string assignee, CancellationToken ct = default)
        {
            try
            {
                // ✅ CORREGIDO: Usar la URL correcta
                var url = $"/api/actividades/assignee/{Uri.EscapeDataString(assignee)}/del-dia";

                using var resp = await _httpLocal.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tus actividades de hoy." };

                var activities = ExtractActivitiesWithStatus(json, 10);

                if (activities.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes actividades para hoy." };

                // Mantiene orden backend, pero mejora pausas al hablar
                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildActivitiesPlainText($"Hoy tienes {activities.Count} actividades", activities)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando actividades del día." };
            }
        }

        /// <summary>
        /// GET /api/actividades/:id - Detalles completos
        /// </summary>
        public async Task<ApiPlainResponse> GetActivityByIdAsync(string id, CancellationToken ct = default)
        {
            try
            {
                // ✅ CORREGIDO: Usar la URL correcta
                var url = $"/api/actividades/{Uri.EscapeDataString(id)}";

                using var resp = await _httpLocal.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener los detalles de la actividad." };

                var details = ExtractActivityDetails(json);

                if (details == null)
                    return new ApiPlainResponse { Ok = false, PlainText = "No encontré esa actividad." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = details
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando detalles de actividad." };
            }
        }

        // =========================
        // Extractores
        // =========================

        private static List<string> ExtractTitles(string json, int limit)
        {
            var list = new List<string>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (item.TryGetProperty("titulo", out var tituloEl) && tituloEl.ValueKind == JsonValueKind.String)
                {
                    var t = (tituloEl.GetString() ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(t))
                        list.Add(t);
                }

                if (list.Count >= limit) break;
            }

            return list;
        }
        
        private static List<(string titulo, string status)> ExtractActivitiesWithStatus(string json, int limit)
        {
            var list = new List<(string, string)>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var titulo = item.TryGetProperty("titulo", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? (tEl.GetString() ?? "").Trim()
                    : "";

                var status = item.TryGetProperty("status", out var sEl) && sEl.ValueKind == JsonValueKind.String
                    ? (sEl.GetString() ?? "").Trim()
                    : "Sin estado";

                if (!string.IsNullOrWhiteSpace(titulo))
                    list.Add((titulo, status));

                if (list.Count >= limit) break;
            }

            return list;
        }

        private static string? ExtractActivityDetails(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;

            var titulo = data.TryGetProperty("titulo", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() ?? "Sin título"
                : "Sin título";

            var status = data.TryGetProperty("status", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString() ?? "Sin estado"
                : "Sin estado";

            var prioridad = data.TryGetProperty("prioridad", out var pEl) && pEl.ValueKind == JsonValueKind.String
                ? pEl.GetString() ?? "Normal"
                : "Normal";

            var parts = new List<string>
            {
                titulo,
                $"Estado: {status}",
                $"Prioridad: {prioridad}"
            };

            if (data.TryGetProperty("pendientes", out var pendEl) && pendEl.ValueKind == JsonValueKind.Array)
            {
                var count = pendEl.GetArrayLength();
                if (count > 0)
                {
                    parts.Add($"Tiene {count} pendientes");

                    var max = Math.Min(count, 3);
                    for (var i = 0; i < max; i++)
                    {
                        var pend = pendEl[i];
                        if (pend.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            var text = textEl.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(text))
                                parts.Add($"{i + 1}) {text}");
                        }
                    }
                }
            }

            return string.Join(". ", parts);
        }

        private static string BuildTitlesPlainText(string header, List<string> titles)
        {
            var max = Math.Min(titles.Count, 10);

            var parts = new List<string> { $"{header}: {max}." };
            for (var i = 0; i < max; i++)
                parts.Add($"{i + 1}) {titles[i]}");

            return string.Join(" ", parts);
        }

        private static string BuildActivitiesPlainText(string header, List<(string titulo, string status)> activities)
        {
            var max = Math.Min(activities.Count, 10);

            var parts = new List<string>
            {
                header + "."
            };

            for (var i = 0; i < max; i++)
            {
                var (titulo, status) = activities[i];
                parts.Add($"Actividad {i + 1}: {titulo}. Estado: {status}.");
            }

            return string.Join("\n\n", parts);
        }

        private sealed class ActivityInfo
        {
            public string Id { get; set; } = "";
            public string Title { get; set; } = "";
            public string Status { get; set; } = "Sin estado";
            public string Priority { get; set; } = "Sin prioridad";
            public string ProjectName { get; set; } = "Sin proyecto";
            public DateTimeOffset? DueStart { get; set; }
            public DateTimeOffset? DueEnd { get; set; }
            public int PendingCount { get; set; }
            public bool HasDoc { get; set; }
            public bool HasUrl { get; set; }
        }

        private static List<ActivityInfo> ExtractActivitiesDetailed(string json)
        {
            var list = new List<ActivityInfo>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                string GetString(string prop, string fallback = "")
                    => item.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                        ? (el.GetString() ?? fallback).Trim()
                        : fallback;

                DateTimeOffset? GetDate(string prop)
                {
                    if (!item.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String)
                        return null;

                    var s = (el.GetString() ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(s)) return null;

                    return DateTimeOffset.TryParse(s, out var dt) ? dt : null;
                }

                var title = GetString("titulo", "Sin título");
                var status = GetString("status", "Sin estado");
                var priority = GetString("prioridad", "Sin prioridad");
                var id = GetString("id", "");

                // project.name
                var projectName = "Sin proyecto";
                if (item.TryGetProperty("project", out var projEl) && projEl.ValueKind == JsonValueKind.Object)
                {
                    if (projEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        projectName = (nameEl.GetString() ?? "Sin proyecto").Trim();
                }

                // pendientes count
                var pendingCount = 0;
                if (item.TryGetProperty("pendientes", out var pendEl) && pendEl.ValueKind == JsonValueKind.Array)
                    pendingCount = pendEl.GetArrayLength();

                // links existence
                var docShared = GetString("documentoCompartido", "");
                var url = GetString("url", "");
                var hasDoc = !string.IsNullOrWhiteSpace(docShared);
                var hasUrl = !string.IsNullOrWhiteSpace(url);

                list.Add(new ActivityInfo
                {
                    Id = id,
                    Title = title,
                    Status = status,
                    Priority = priority,
                    ProjectName = string.IsNullOrWhiteSpace(projectName) ? "Sin proyecto" : projectName,
                    DueStart = GetDate("dueStart"),
                    DueEnd = GetDate("dueEnd"),
                    PendingCount = pendingCount,
                    HasDoc = hasDoc,
                    HasUrl = hasUrl
                });
            }

            return list;
        }

        private static string BuildActivitiesDetailedPlainText(string header, List<ActivityInfo> items)
        {
            static string FmtRange(DateTimeOffset? start, DateTimeOffset? end)
            {
                if (start == null && end == null) return "Sin horario";

                var s = start?.ToLocalTime().ToString("dd/MM HH:mm");
                var e = end?.ToLocalTime().ToString("HH:mm");

                if (start != null && end != null) return $"{s} a {e}";
                if (start != null) return $"{s}";
                return $"Hasta {end?.ToLocalTime().ToString("dd/MM HH:mm")}";
            }

            var parts = new List<string> { header };

            for (var i = 0; i < items.Count; i++)
            {
                var a = items[i];

                var horario = FmtRange(a.DueStart, a.DueEnd);
                var links = new List<string>();
                if (a.HasDoc) links.Add("Documento");
                if (a.HasUrl) links.Add("Link");
                var linksText = links.Count > 0 ? string.Join(" y ", links) : "Sin links";

                var pendientesText = a.PendingCount > 0 ? $"{a.PendingCount} pendientes" : "Sin pendientes";

                parts.Add(
                    $"Actividad {i + 1}.\n" +
                    $"{a.Title}.\n" +
                    $"Proyecto: {a.ProjectName}.\n" +
                    $"Horario: {horario}.\n" +
                    $"Estado: {a.Status}. Prioridad: {a.Priority}.\n" +
                    $"{pendientesText}. {linksText}."
                );
            }

            return string.Join("\n\n", parts);
        }
        public async Task<ApiPlainResponse> UpdateActivityAsync(string id, UpdateActividadRequest req, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                    return new ApiPlainResponse { Ok = false, PlainText = "Falta el ID de la actividad." };

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };

                var json = JsonSerializer.Serialize(req, options);

#if DEBUG
                System.Diagnostics.Debug.WriteLine("===== UPDATE ACTIVITY DEBUG =====");
                System.Diagnostics.Debug.WriteLine($"PUT {_http.BaseAddress}api/actividades/{id}");
                System.Diagnostics.Debug.WriteLine(json);
                System.Diagnostics.Debug.WriteLine("=================================");
#endif

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _http.PutAsync($"/api/actividades/{Uri.EscapeDataString(id)}", content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    return new ApiPlainResponse
                    {
                        Ok = false,
                        PlainText = $"No pude actualizar la actividad. HTTP {(int)resp.StatusCode}: {body}"
                    };
                }

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = "Actividad actualizada correctamente."
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch (Exception ex)
            {
                return new ApiPlainResponse { Ok = false, PlainText = $"Error: {ex.Message}" };
            }
        }
        public async Task<ApiPlainResponse> DeleteActivityAsync(string id, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                    return new ApiPlainResponse { Ok = false, PlainText = "Falta el ID de la actividad." };

#if DEBUG
                System.Diagnostics.Debug.WriteLine("===== DELETE ACTIVITY DEBUG =====");
                System.Diagnostics.Debug.WriteLine($"DELETE {_http.BaseAddress}api/actividades/{id}");
                System.Diagnostics.Debug.WriteLine("=================================");
#endif

                using var resp = await _http.DeleteAsync($"/api/actividades/{Uri.EscapeDataString(id)}", ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    return new ApiPlainResponse
                    {
                        Ok = false,
                        PlainText = $"No pude eliminar la actividad. HTTP {(int)resp.StatusCode}: {body}"
                    };
                }

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = "Actividad eliminada correctamente."
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch (Exception ex)
            {
                return new ApiPlainResponse { Ok = false, PlainText = $"Error: {ex.Message}" };
            }
        }
    }
}