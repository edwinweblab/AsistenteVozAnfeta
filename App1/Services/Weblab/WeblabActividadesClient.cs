// Services/Weblab/WeblabActividadesClient.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;
using Anfeta.UI.Services.Auth;

namespace Anfeta.UI.Services.Weblab
{
    public sealed class WeblabActividadesClient
    {
        private readonly HttpClient _http;
        private readonly WeblabAuthClient _auth;

        public WeblabActividadesClient(HttpClient http, WeblabAuthClient auth)
        {
            _http = http;
            _auth = auth;
        }

        // =========================
        // ✅ NUEVO: Mis actividades de HOY (sin pasar assignee)
        // Flujo: token -> /auth/me -> collaboratorId -> /actividades/assignee/{id}/del-dia
        // =========================
        public async Task<ApiPlainResponse> GetMyTodayActivitiesAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, assignee, _) = await _auth.GetCurrentUserAsync(ct);
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

        // =========================
        // (Opcional) ✅ NUEVO: Mis actividades (todas) por assignee
        // =========================
        public async Task<ApiPlainResponse> GetMyActivitiesAsync(int limit = 10, CancellationToken ct = default)
        {
            try
            {
                var (ok, assignee, name) = await _auth.GetCurrentUserAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(assignee))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu usuario." };

                var url = $"/api/actividades/assignee/{Uri.EscapeDataString(assignee)}";

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tus actividades." };

                var titles = ExtractTitles(json, limit);

                if (titles.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes actividades asignadas." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildTitlesPlainText($"Actividades de {name ?? "tu usuario"}", titles)
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

        // GET /api/actividades -> convierte JSON a texto (solo títulos)
        public async Task<ApiPlainResponse> ListTitlesAsync(int limit, CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/actividades", ct);
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

        // GET /api/actividades/buscar?q=texto -> convierte JSON a texto (solo títulos)
        public async Task<ApiPlainResponse> SearchTitlesAsync(string q, int limit, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/actividades/buscar?q={Uri.EscapeDataString(q)}";

                using var resp = await _http.GetAsync(url, ct);
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

        // GET /api/actividades/assignee/:assignee/del-dia -> Actividades del día de un usuario
        // Entrada: assignee (collaboratorId del usuario)
        public async Task<ApiPlainResponse> GetTodayActivitiesAsync(string assignee, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/actividades/assignee/{Uri.EscapeDataString(assignee)}/del-dia";

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tus actividades de hoy." };

                var activities = ExtractActivitiesWithStatus(json, 10);

                if (activities.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes actividades para hoy." };

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

        // GET /api/actividades/:id -> Detalles completos de una actividad
        public async Task<ApiPlainResponse> GetActivityByIdAsync(string id, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/actividades/{Uri.EscapeDataString(id)}";

                using var resp = await _http.GetAsync(url, ct);
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

            var parts = new List<string> { header };
            for (var i = 0; i < max; i++)
            {
                var (titulo, status) = activities[i];
                parts.Add($"{i + 1}) {titulo} - {status}");
            }

            return string.Join(". ", parts);
        }
    }
}
