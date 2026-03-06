// Services/ApiActionExecutor.cs
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Auth;
using Anfeta.UI.Services.Calendar;
using Anfeta.UI.Services.Weblab;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Interpretation
{
    public sealed class ApiActionExecutor
    {
        private readonly WeblabActividadesClient _actividades;
        private readonly WeblabRevisionesClient _revisiones;
        private readonly WeblabAuthClient _auth;
        private readonly WeblabReportesClient _reportes;
        private readonly WeblabRecordatoriosClient _recordatorios;
        private readonly GoogleCalendarClient _googleCalendar;
        private readonly GoogleAuthService _googleAuth;
        private string? _cachedAssignee;

        public ApiActionExecutor(
            WeblabActividadesClient actividades,
            WeblabRevisionesClient revisiones,
            WeblabReportesClient reportes,
            WeblabRecordatoriosClient recordatorios,
            WeblabAuthClient auth,
            GoogleCalendarClient googleCalendar,
            GoogleAuthService googleAuth)
        {
            _actividades = actividades;
            _revisiones = revisiones;
            _reportes = reportes;
            _recordatorios = recordatorios;
            _auth = auth;
            _googleCalendar = googleCalendar;
            _googleAuth = googleAuth;
        }

        /// <summary>
        /// Ejecuta llamada API basada en provider/resource/action.
        /// Entrada: provider (weblab|google), resource, action, paramsJson opcional.
        /// Salida: (ok, mensaje para TTS).
        /// </summary>
        public Task<(bool ok, string message)> ExecuteAsync(
            string? provider,
            string? resource,
            string? action,
            string? paramsJson,
            CancellationToken ct = default)
            => TryExecuteAsync(provider, resource, action, paramsJson, ct);

        public async Task<(bool ok, string message)> TryExecuteAsync(
            string? provider,
            string? resource,
            string? action,
            string? paramsJson,
            CancellationToken ct = default)
        {
            provider = (provider ?? "").Trim().ToLowerInvariant();
            resource = (resource ?? "").Trim().ToLowerInvariant();
            action = (action ?? "").Trim().ToLowerInvariant();

            // ── VALIDAR PROVIDER ─────────────────────────────────────────────
            var validProviders = new[] { "weblab", "google" };
            if (string.IsNullOrWhiteSpace(provider) ||
                !Array.Exists(validProviders, p => p == provider))
            {
                return (false, $"Provider no soportado: '{provider}'. Disponibles: weblab, google.");
            }

            if (provider == "weblab")
            {
                if (string.IsNullOrWhiteSpace(resource))
                    return (false, "Falta especificar el resource (actividades, revisiones, etc).");

                if (string.IsNullOrWhiteSpace(action))
                    return (false, "Falta especificar la action (list, today, search, get, etc).");
            }

            // ════════════════════════════════════════
            // WEBLAB
            // ════════════════════════════════════════
            if (provider == "weblab")
            {
                // ── ACTIVIDADES ──────────────────────────────────────────────
                if (resource == "actividades")
                {
                    if (action == "list")
                    {
                        var limit = TryGetInt(paramsJson, "limit") ?? 10;
                        var r = await _actividades.GetMyActivitiesAsync(limit, ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "today")
                    {
                        var assignee = await GetOrFetchAssigneeAsync(ct);
                        if (string.IsNullOrWhiteSpace(assignee))
                            return (false, "No pude identificar tu usuario.");

                        var r = await _actividades.GetTodayActivitiesAsync(assignee, ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "search")
                    {
                        var q = TryGetString(paramsJson, "q");
                        if (string.IsNullOrWhiteSpace(q))
                            return (false, "Búsqueda inválida: falta params.q.");

                        var limit = TryGetInt(paramsJson, "limit") ?? 10;
                        var r = await _actividades.SearchTitlesAsync(q!, limit, ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "get")
                    {
                        var id = TryGetString(paramsJson, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            return (false, "Falta el ID de la actividad.");

                        var r = await _actividades.GetActivityByIdAsync(id!, ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "create")
                    {
                        CreateActividadRequest? request = null;
                        try
                        {
                            request = JsonSerializer.Deserialize<CreateActividadRequest>(paramsJson);
                        }
                        catch (Exception ex)
                        {
                            return (false, $"Error parseando datos de actividad: {ex.Message}");
                        }

                        if (request == null || string.IsNullOrWhiteSpace(request.Titulo))
                            return (false, "Falta el título de la actividad.");

                        var r = await _actividades.CreateActivityAsync(request, ct);
                        return (r.Ok, r.PlainText);
                    }

                    return (false, $"Acción '{action}' no soportada para actividades. Disponibles: list, today, search, get, create.");
                }

                // ── REVISIONES ───────────────────────────────────────────────
                if (resource == "revisiones")
                {
                    if (action == "today")
                    {
                        var r = await _revisiones.GetTodayRevisionsAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "en-curso" || action == "activa")
                    {
                        var r = await _revisiones.GetActiveRevisionsAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    return (false, $"Acción '{action}' no soportada para revisiones. Disponibles: today, en-curso.");
                }

                // ── REPORTES ─────────────────────────────────────────────────
                if (resource == "reportes")
                {
                    // Últimos eventos de auditoría del equipo.
                    // Sin params — devuelve las últimas N acciones registradas.
                    if (action == "ultimos")
                    {
                        var r = await _reportes.GetUltimosAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    // Comprobatoria del usuario en sesión (FTF + actividades + cuadrated).
                    if (action == "comprobatoria")
                    {
                        var r = await _reportes.GetComprobatoriaAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    // Tareas rezagadas del usuario en sesión a partir de la hora actual.
                    if (action == "rezagadas")
                    {
                        var r = await _reportes.GetRezagadasAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    // Revisiones por fecha. El param "date" viene del FastCommandClassifier
                    // (ya resuelto para hoy/ayer) o de IA para otras fechas.
                    if (action == "revisiones-por-fecha")
                    {
                        var date = TryGetString(paramsJson, "date");
                        var r = await _reportes.GetMyRevisionsReportAsync(date, ct);
                        return (r.Ok, r.PlainText);
                    }

                    // Alias legacy — mantenidos para no romper flujos existentes.
                    if (action == "list")
                    {
                        var date = TryGetString(paramsJson, "date");
                        var r = await _reportes.GetMyRevisionsReportAsync(date, ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "today")
                    {
                        var today = DateTime.Today.ToString("yyyy-MM-dd");
                        var r = await _reportes.GetMyRevisionsReportAsync(today, ct);
                        return (r.Ok, r.PlainText);
                    }

                    return (false, $"Acción '{action}' no soportada para reportes. Disponibles: ultimos, comprobatoria, rezagadas, revisiones-por-fecha.");
                }

                // ── RECORDATORIOS ────────────────────────────────────────────
                if (resource == "recordatorios")
                {
                    if (action == "list")
                    {
                        var r = await _recordatorios.GetMyRecordatoriosAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "pending" || action == "pendientes")
                    {
                        var r = await _recordatorios.GetMyPendingRecordatoriosAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "today")
                    {
                        var r = await _recordatorios.GetMyTodayRecordatoriosAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "tomorrow" || action == "mañana")
                    {
                        var r = await _recordatorios.GetMyTomorrowRecordatoriosAsync(ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "create")
                    {
                        var mensaje = TryGetString(paramsJson, "mensaje");
                        var fechaHora = TryGetString(paramsJson, "fechaHora");

                        if (string.IsNullOrWhiteSpace(mensaje))
                            return (false, "Falta el mensaje del recordatorio.");

                        if (string.IsNullOrWhiteSpace(fechaHora))
                            return (false, "Falta la fecha y hora del recordatorio.");

                        var duracion = TryGetInt(paramsJson, "duracionMinutos") ?? 30;
                        var r = await _recordatorios.CreateRecordatorioAsync(mensaje!, fechaHora!, duracion, ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "update")
                    {
                        var id = TryGetString(paramsJson, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            return (false, "Falta el ID del recordatorio a actualizar.");

                        var mensaje = TryGetString(paramsJson, "mensaje");
                        var fechaHora = TryGetString(paramsJson, "fechaHora");
                        var duracion = TryGetInt(paramsJson, "duracionMinutos");

                        var r = await _recordatorios.UpdateRecordatorioAsync(id!, mensaje, fechaHora, duracion, ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "delete")
                    {
                        var id = TryGetString(paramsJson, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            return (false, "Falta el ID del recordatorio a eliminar.");

                        var r = await _recordatorios.DeleteRecordatorioAsync(id!, ct);
                        return (r.Ok, r.PlainText);
                    }

                    if (action == "complete")
                    {
                        var id = TryGetString(paramsJson, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            return (false, "Falta el ID del recordatorio.");

                        var r = await _recordatorios.CompleteRecordatorioAsync(id!, ct);
                        return (r.Ok, r.PlainText);
                    }

                    return (false, $"Acción '{action}' no soportada para recordatorios. Disponibles: list, pending, today, tomorrow, create, update, delete, complete.");
                }

                return (false, $"Resource '{resource}' no soportado. Disponibles: actividades, revisiones, reportes, recordatorios.");
            }

            // ════════════════════════════════════════
            // GOOGLE
            // ════════════════════════════════════════
            if (provider == "google")
            {
                if (resource != "calendar")
                    return (false, $"Resource '{resource}' no soportado para Google. Usa 'calendar'.");

                if (string.IsNullOrWhiteSpace(action))
                    return (false, "Falta especificar la action para Google Calendar.");

                if (action == "status")
                {
                    var connected = await _googleAuth.IsConnectedAsync(ct);
                    return connected
                        ? (true, "Tu Google Calendar está conectado.")
                        : (false, "Tu Google Calendar no está conectado. Di 'conectar Google Calendar' para vincularlo.");
                }

                if (action == "connect")
                {
                    var (ok, msg) = await _googleAuth.StartOAuthAsync(openBrowser: true, ct: ct);
                    return (ok, msg);
                }

                if (action == "disconnect")
                {
                    var (ok, msg) = await _googleAuth.DisconnectAsync(ct);
                    return (ok, msg);
                }

                if (action == "create")
                {
                    var connected = await _googleAuth.IsConnectedAsync(ct);
                    if (!connected)
                        return (false, "Tu Google Calendar no está conectado. Di 'conectar Google Calendar' primero.");

                    var userId = await _googleAuth.GetUserIdAsync(ct);
                    if (string.IsNullOrWhiteSpace(userId))
                        return (false, "No se pudo identificar tu usuario.");

                    var summary = TryGetString(paramsJson, "summary");
                    var start = TryGetString(paramsJson, "start");
                    var end = TryGetString(paramsJson, "end");

                    if (string.IsNullOrWhiteSpace(summary)) return (false, "Falta el título del evento.");
                    if (string.IsNullOrWhiteSpace(start)) return (false, "Falta la fecha de inicio del evento.");
                    if (string.IsNullOrWhiteSpace(end)) return (false, "Falta la fecha de fin del evento.");

                    var description = TryGetString(paramsJson, "description");
                    var location = TryGetString(paramsJson, "location");

                    var result = await _googleCalendar.CreateEventAsync(
                        userId!, summary!, start!, end!, description, location, ct);

                    if (result.AuthNeeded)
                    {
                        await _googleAuth.StartOAuthAsync(openBrowser: true, ct: ct);
                        return (false, "Tu sesión de Google expiró. Se abrió el navegador para reconectar.");
                    }

                    return (result.Ok, result.Message);
                }

                if (action == "list")
                {
                    var connected = await _googleAuth.IsConnectedAsync(ct);
                    if (!connected)
                        return (false, "Tu Google Calendar no está conectado.");

                    var userId = await _googleAuth.GetUserIdAsync(ct);
                    if (string.IsNullOrWhiteSpace(userId))
                        return (false, "No pude identificar tu usuario.");

                    var timeMin = TryGetString(paramsJson, "timeMin");
                    var timeMax = TryGetString(paramsJson, "timeMax");

                    if (string.IsNullOrWhiteSpace(timeMin))
                    {
                        timeMin = DateTime.Today.ToString("yyyy-MM-dd'T'00:00:00'-06:00'");
                        timeMax = DateTime.Today.ToString("yyyy-MM-dd'T'23:59:59'-06:00'");
                    }

                    var max = TryGetInt(paramsJson, "maxResults") ?? 10;

                    var (ok, _, items) = await _googleCalendar.ListEventsAsync(
                        userId!, timeMin, timeMax, max, ct);

                    if (!ok)
                        return (false, "No pude consultar tu Google Calendar.");

                    return (true, BuildCalendarVoiceResponse(items, timeMin, timeMax));
                }

                if (action == "delete")
                {
                    var connected = await _googleAuth.IsConnectedAsync(ct);
                    if (!connected)
                        return (false, "Tu Google Calendar no está conectado.");

                    var userId = await _googleAuth.GetUserIdAsync(ct);
                    var eventId = TryGetString(paramsJson, "eventId");

                    if (string.IsNullOrWhiteSpace(eventId))
                        return (false, "Falta el ID del evento.");

                    var (ok, msg) = await _googleCalendar.DeleteEventAsync(userId!, eventId!, ct);
                    return (ok, msg);
                }

                return (false, $"Acción '{action}' no soportada para Google Calendar. Disponibles: status, connect, disconnect, create, list, delete.");
            }

            return (false, $"Provider '{provider}' no manejado.");
        }

        // ────────────────────────────────────────────────────────────────────
        // HELPERS PRIVADOS
        // ────────────────────────────────────────────────────────────────────

        /// Obtiene o cachea el assignee del usuario autenticado.
        private async Task<string?> GetOrFetchAssigneeAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_cachedAssignee))
                return _cachedAssignee;

            var (ok, assignee, _, _) = await _auth.GetCurrentUserAsync(ct);
            if (ok && !string.IsNullOrWhiteSpace(assignee))
            {
                _cachedAssignee = assignee;
                return assignee;
            }

            return null;
        }

        /// Extrae string de un JSON por nombre de propiedad.
        private static string? TryGetString(string? json, string prop)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty(prop, out var el)) return null;
                if (el.ValueKind == JsonValueKind.String) return el.GetString();
                return el.GetRawText();
            }
            catch { return null; }
        }

        /// Extrae int de un JSON por nombre de propiedad.
        private static int? TryGetInt(string? json, string prop)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty(prop, out var el)) return null;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return v;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
                return null;
            }
            catch { return null; }
        }

        /// Construye respuesta de voz legible a partir de la lista de eventos de Google Calendar.
        private static string BuildCalendarVoiceResponse(
            List<GoogleCalendarEventItem> items,
            string? timeMin,
            string? timeMax)
        {
            var isOnlyToday =
                timeMin != null &&
                DateTime.TryParse(timeMin, out var rangeMin) &&
                rangeMin.Date == DateTime.Today &&
                timeMax != null &&
                DateTime.TryParse(timeMax, out var rangeMax) &&
                rangeMax.Date == DateTime.Today;

            if (items.Count == 0)
            {
                return isOnlyToday
                    ? "No tienes eventos en tu calendario para hoy."
                    : "No tienes eventos próximos en tu calendario.";
            }

            var header = isOnlyToday
                ? $"Hoy tienes {items.Count} evento{(items.Count > 1 ? "s" : "")}"
                : $"Tienes {items.Count} evento{(items.Count > 1 ? "s" : "")} próximo{(items.Count > 1 ? "s" : "")}";

            var parts = new List<string> { header };
            var culture = new CultureInfo("es-MX");

            foreach (var ev in items)
            {
                if (string.IsNullOrWhiteSpace(ev.Summary)) continue;

                if (DateTime.TryParse(ev.Start, out var startDt))
                {
                    var hora = startDt.ToString("HH:mm");
                    var fecha = startDt.Date == DateTime.Today
                        ? ""
                        : startDt.ToString("dddd dd", culture);

                    parts.Add(string.IsNullOrWhiteSpace(fecha)
                        ? $"a las {hora}, {ev.Summary}"
                        : $"el {fecha} a las {hora}, {ev.Summary}");
                }
                else
                {
                    parts.Add(ev.Summary);
                }
            }

            return string.Join(". ", parts) + ".";
        }
    }
}