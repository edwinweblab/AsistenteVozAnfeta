using Anfeta.UI.Models.Weblab;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Weblab
{
    // Caché del último resultado de revisiones por fecha.
    // Permite drill-down sin re-consultar el API.
    public sealed class ReportRevisionesCache
    {
        public string Date { get; init; } = "";
        public DateTime CachedAt { get; init; } = DateTime.Now;
        public string DisplayName { get; init; } = "";
        public List<string> Pendientes { get; init; } = new();
        public List<string> Terminadas { get; init; } = new();
        public List<string> Confirmadas { get; init; } = new();
        public int Total => Pendientes.Count + Terminadas.Count + Confirmadas.Count;

        // Expira en 10 minutos para no responder datos obsoletos.
        public bool IsExpired => (DateTime.Now - CachedAt).TotalMinutes > 10;
    }

    public sealed class WeblabReportesClient
    {
        private readonly HttpClient _http;
        private readonly AppStateService _appState;

        // Caché pública: HomeViewModel la lee para drill-down sin llamar al API.
        public ReportRevisionesCache? LastRevisionesCache { get; private set; }

        public WeblabReportesClient(HttpClient http, AppStateService appState)
        {
            _http = http;
            _appState = appState;
        }

        // ────────────────────────────────────────────────────────────────────────
        // GET /api/reportes/ultimos
        // Devuelve los últimos eventos de auditoría del equipo.
        // ────────────────────────────────────────────────────────────────────────

        /// Obtiene los últimos eventos de auditoría registrados.
        /// Salida: PlainText legible en voz con los 5 eventos más recientes.
        public async Task<ApiPlainResponse> GetUltimosAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/reportes/ultimos", ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[REPORTES-ULTIMOS] Status={resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener los últimos eventos." };

                return new ApiPlainResponse { Ok = true, PlainText = BuildUltimosPlainText(json) };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REPORTES-ULTIMOS] ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando los últimos eventos." };
            }
        }

        private static string BuildUltimosPlainText(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return "No hay eventos recientes registrados.";

                var count = 0;
                var lineas = new System.Text.StringBuilder();

                foreach (var item in items.EnumerateArray())
                {
                    if (count >= 5) break;

                    var title = item.TryGetProperty("entity", out var ent) && ent.TryGetProperty("title", out var t)
                        ? (t.GetString() ?? "sin título") : "sin título";

                    var email = item.TryGetProperty("actor", out var actor) && actor.TryGetProperty("email", out var em)
                        ? (em.GetString() ?? "") : "";

                    var evType = item.TryGetProperty("event", out var ev)
                        ? TranslateEvent(ev.GetString() ?? "") : "cambio";

                    var who = !string.IsNullOrWhiteSpace(email) ? email.Split('@')[0] : "alguien";
                    lineas.AppendLine($"{count + 1}. {who} {evType}: {TruncateTitle(title, 50)}");
                    count++;
                }

                if (count == 0) return "No hay eventos recientes registrados.";
                return $"Últimas {count} acciones del equipo:\n{lineas.ToString().TrimEnd()}";
            }
            catch { return "No pude interpretar los últimos eventos."; }
        }

        private static string TranslateEvent(string eventName) => eventName switch
        {
            "revisionMarcada" => "marcó revisión",
            "revisionCreada" => "creó revisión",
            "revisionActualizada" => "actualizó revisión",
            "actividadCreada" => "creó actividad",
            "actividadActualizada" => "actualizó actividad",
            _ => eventName
        };

        // ────────────────────────────────────────────────────────────────────────
        // GET /api/reportes/comprobatoria?assignee={email}
        // ────────────────────────────────────────────────────────────────────────

        /// Obtiene la comprobatoria del usuario autenticado.
        /// Usa AppStateService directamente — sin llamar a /api/auth/me.
        public async Task<ApiPlainResponse> GetComprobatoriaAsync(CancellationToken ct = default)
        {
            var assignee = _appState.CurrentUserEmail;
            var name = _appState.CurrentUserName ?? assignee;

            if (string.IsNullOrWhiteSpace(assignee))
                return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu usuario." };

            try
            {
                var url = $"/api/reportes/comprobatoria?assignee={Uri.EscapeDataString(assignee)}";
                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[REPORTES-COMPROBATORIA] Status={resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tu comprobatoria." };

                return new ApiPlainResponse { Ok = true, PlainText = BuildComprobatoriaPlainText(json, name!) };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REPORTES-COMPROBATORIA] ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando tu comprobatoria." };
            }
        }

        private static string BuildComprobatoriaPlainText(string json, string name)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data))
                    return "No pude leer tu comprobatoria.";

                var partes = new List<string>();

                if (data.TryGetProperty("ftf", out var ftf))
                    partes.Add(ftf.TryGetProperty("ok", out var fo) && fo.GetBoolean() ? "FTF completado." : "FTF pendiente.");

                if (data.TryGetProperty("activities", out var acts))
                {
                    var ok = acts.TryGetProperty("ok", out var ao) && ao.GetBoolean();
                    if (!ok && acts.TryGetProperty("anotaciones", out var notas) && notas.ValueKind == JsonValueKind.Array)
                        foreach (var nota in notas.EnumerateArray())
                        {
                            var txt = nota.GetString();
                            if (!string.IsNullOrWhiteSpace(txt)) partes.Add(txt);
                        }
                    else if (ok) partes.Add("Actividades en orden.");
                }

                if (data.TryGetProperty("cuadrated", out var cuad))
                {
                    var ok = cuad.TryGetProperty("ok", out var co) && co.GetBoolean();
                    if (!ok && cuad.TryGetProperty("anotaciones", out var notas2) && notas2.ValueKind == JsonValueKind.Array)
                        foreach (var nota in notas2.EnumerateArray())
                        {
                            var txt = nota.GetString();
                            if (!string.IsNullOrWhiteSpace(txt)) partes.Add(txt);
                        }
                    else if (ok) partes.Add("Cuadrated en orden.");
                }

                return partes.Count == 0
                    ? $"{name}, no encontré datos en tu comprobatoria de hoy."
                    : $"{name}: {string.Join(" ", partes)}";
            }
            catch { return "No pude interpretar tu comprobatoria."; }
        }

        // ────────────────────────────────────────────────────────────────────────
        // GET /api/reportes/rezagadas?assignee={email}&time={HH:mm}
        // ────────────────────────────────────────────────────────────────────────

        /// Obtiene las tareas rezagadas del usuario autenticado.
        /// Usa AppStateService directamente — sin llamar a /api/auth/me.
        public async Task<ApiPlainResponse> GetRezagadasAsync(CancellationToken ct = default)
        {
            var assignee = _appState.CurrentUserEmail;
            var name = _appState.CurrentUserName ?? assignee;

            if (string.IsNullOrWhiteSpace(assignee))
                return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu usuario." };

            try
            {
                var time = DateTime.Now.ToString("HH:mm");
                var url = $"/api/reportes/rezagadas?assignee={Uri.EscapeDataString(assignee)}&time={Uri.EscapeDataString(time)}";

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[REPORTES-REZAGADAS] Status={resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tus tareas rezagadas." };

                return new ApiPlainResponse { Ok = true, PlainText = BuildRezagadasPlainText(json, name!) };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REPORTES-REZAGADAS] ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando tareas rezagadas." };
            }
        }

        private static string BuildRezagadasPlainText(string json, string name)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data))
                    return "No pude leer las tareas rezagadas.";

                if (!data.TryGetProperty("rezagadas", out var rez))
                    return $"{name}, no encontré tareas rezagadas.";

                var total = rez.TryGetProperty("total", out var t) ? t.GetInt32() : 0;

                if (total == 0)
                    return $"{name}, no tienes tareas rezagadas en este momento.";

                var sb = new System.Text.StringBuilder();
                sb.Append($"{name}, tienes {total} {(total == 1 ? "tarea rezagada" : "tareas rezagadas")}: ");

                if (rez.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    var idx = 1;
                    foreach (var item in items.EnumerateArray())
                    {
                        if (idx > 5) break;
                        var titulo = item.TryGetProperty("titulo", out var tit) ? (tit.GetString() ?? "sin título") : "sin título";
                        var hora = item.TryGetProperty("startHHmm", out var h) ? (h.GetString() ?? "") : "";
                        var horaTxt = !string.IsNullOrWhiteSpace(hora) ? $" (desde las {hora})" : "";
                        sb.Append($"{idx}. {TruncateTitle(titulo, 50)}{horaTxt}. ");
                        idx++;
                    }
                }

                return sb.ToString().TrimEnd();
            }
            catch { return "No pude interpretar las tareas rezagadas."; }
        }

        // ────────────────────────────────────────────────────────────────────────
        // GET /api/reportes/revisiones-por-fecha?date=YYYY-MM-DD
        // ────────────────────────────────────────────────────────────────────────

        /// Obtiene las revisiones del usuario autenticado para la fecha dada.
        /// Usa AppStateService directamente — sin llamar a /api/auth/me.
        /// Popula LastRevisionesCache para drill-down posterior sin re-consultar el API.
        public async Task<ApiPlainResponse> GetMyRevisionsReportAsync(string? date = null, CancellationToken ct = default)
        {
            var name = _appState.CurrentUserName ?? _appState.CurrentUserEmail ?? "Usuario";
            var collaboratorId = _appState.CollaboratorId;

            if (string.IsNullOrWhiteSpace(collaboratorId))
                return new ApiPlainResponse
                {
                    Ok = false,
                    PlainText = "No pude identificar tu perfil. Intenta cerrar y abrir sesión."
                };

            if (string.IsNullOrWhiteSpace(date))
                date = DateTime.Today.ToString("yyyy-MM-dd");

            try
            {
                var url = $"/api/reportes/revisiones-por-fecha?date={Uri.EscapeDataString(date)}";

                Debug.WriteLine($"[REPORTES-REVISIONES] collaboratorId={collaboratorId} date={date}");

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[REPORTES-REVISIONES] Status={resp.StatusCode} BodyLen={json.Length}");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tus reportes." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildRevisionesPorFechaPlainText(json, name, collaboratorId, date)
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REPORTES-REVISIONES] ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando reportes." };
            }
        }

        /// Convierte la respuesta en texto para voz y popula LastRevisionesCache.
        /// Match directo: colaboradores[].idAsignee == collaboratorId.
        private string BuildRevisionesPorFechaPlainText(string json, string name, string collaboratorId, string date)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data))
                    return "No pude leer el reporte de revisiones.";

                var totalGlobal = data.TryGetProperty("totalRevisiones", out var tg) ? tg.GetInt32() : 0;

                if (!data.TryGetProperty("colaboradores", out var colabs) || colabs.ValueKind != JsonValueKind.Array)
                    return $"Hay {totalGlobal} revisiones registradas el {date}.";

                foreach (var colab in colabs.EnumerateArray())
                {
                    if (!colab.TryGetProperty("idAsignee", out var idProp)) continue;
                    if (!string.Equals(idProp.GetString(), collaboratorId, StringComparison.OrdinalIgnoreCase)) continue;

                    var pendientes = new List<string>();
                    var terminadas = new List<string>();
                    var confirmadas = new List<string>();

                    if (colab.TryGetProperty("items", out var items) &&
                        items.TryGetProperty("actividades", out var acts) &&
                        acts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var act in acts.EnumerateArray())
                        {
                            CollectNames(act, "pendientes", pendientes);
                            CollectNames(act, "terminadas", terminadas);
                            CollectNames(act, "confirmadas", confirmadas);
                        }
                    }

                    // Poblar caché para drill-down
                    LastRevisionesCache = new ReportRevisionesCache
                    {
                        Date = date,
                        CachedAt = DateTime.Now,
                        DisplayName = name,
                        Pendientes = pendientes,
                        Terminadas = terminadas,
                        Confirmadas = confirmadas
                    };

                    var total = pendientes.Count + terminadas.Count + confirmadas.Count;
                    var fechaTxt = date == DateTime.Today.ToString("yyyy-MM-dd") ? "hoy" :
                                   date == DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd") ? "ayer" :
                                   $"el {date}";

                    if (total == 0)
                        return $"{name}, no tienes revisiones asignadas {fechaTxt}.";

                    return $"{name}, {fechaTxt} tienes {total} revisiones: " +
                           $"{terminadas.Count} terminadas, {confirmadas.Count} confirmadas y {pendientes.Count} pendientes.";
                }

                var fechaFallback = date == DateTime.Today.ToString("yyyy-MM-dd") ? "hoy" : date;
                return $"{name}, no tienes revisiones asignadas para {fechaFallback}.";
            }
            catch { return "No pude interpretar el reporte de revisiones."; }
        }

        // ────────────────────────────────────────────────────────────────────────
        // DRILL-DOWN — responde desde caché sin llamar al API
        // ────────────────────────────────────────────────────────────────────────

        /// Devuelve la lista de revisiones de un bucket (pendientes/terminadas/confirmadas).
        /// Lee de LastRevisionesCache — sin llamada HTTP.
        /// Entrada: bucket = "pendientes" | "terminadas" | "confirmadas" | "todas"
        public ApiPlainResponse GetRevisionesDetail(string bucket)
        {
            if (LastRevisionesCache == null || LastRevisionesCache.IsExpired)
            {
                LastRevisionesCache = null;
                return new ApiPlainResponse
                {
                    Ok = false,
                    PlainText = "No tengo revisiones cargadas. Di 'revisiones de hoy' primero."
                };
            }

            var cache = LastRevisionesCache;
            var name = cache.DisplayName;

            if (bucket == "todas")
            {
                if (cache.Total == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = $"{name}, no tienes revisiones." };

                var sb = new System.Text.StringBuilder();
                if (cache.Terminadas.Count > 0) AppendBucket(sb, "Terminadas", cache.Terminadas);
                if (cache.Confirmadas.Count > 0) AppendBucket(sb, "Confirmadas", cache.Confirmadas);
                if (cache.Pendientes.Count > 0) AppendBucket(sb, "Pendientes", cache.Pendientes);
                return new ApiPlainResponse { Ok = true, PlainText = sb.ToString().TrimEnd() };
            }

            var list = bucket switch
            {
                "pendientes" => cache.Pendientes,
                "terminadas" => cache.Terminadas,
                "confirmadas" => cache.Confirmadas,
                _ => cache.Pendientes
            };

            if (list.Count == 0)
                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = $"{name}, no tienes revisiones {bucket}."
                };

            var result = new System.Text.StringBuilder();
            result.AppendLine($"{name}, tienes {list.Count} {(list.Count == 1 ? "revisión" : "revisiones")} {bucket}:");
            for (var i = 0; i < Math.Min(list.Count, 10); i++)
                result.AppendLine($"{i + 1}. {TruncateTitle(list[i], 70)}");

            if (list.Count > 10)
                result.AppendLine($"... y {list.Count - 10} más.");

            return new ApiPlainResponse { Ok = true, PlainText = result.ToString().TrimEnd() };
        }

        // ────────────────────────────────────────────────────────────────────────
        // HELPERS PRIVADOS
        // ────────────────────────────────────────────────────────────────────────

        /// Extrae el nombre de cada revisión en un bucket y lo agrega a la lista.
        private static void CollectNames(JsonElement act, string bucket, List<string> target)
        {
            if (!act.TryGetProperty(bucket, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var rev in arr.EnumerateArray())
            {
                var nombre = rev.TryGetProperty("nombre", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(nombre))
                    target.Add(nombre!);
            }
        }

        private static void AppendBucket(System.Text.StringBuilder sb, string label, List<string> items)
        {
            sb.AppendLine($"{label} ({items.Count}):");
            for (var i = 0; i < Math.Min(items.Count, 5); i++)
                sb.AppendLine($"  {i + 1}. {TruncateTitle(items[i], 60)}");
            if (items.Count > 5)
                sb.AppendLine($"  ... y {items.Count - 5} más.");
        }

        private static string TruncateTitle(string title, int maxLen) =>
            title.Length <= maxLen ? title : title[..maxLen].TrimEnd() + "...";
    }
}