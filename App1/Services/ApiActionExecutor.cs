// Services/ApiActionExecutor.cs
using Anfeta.UI.Models;
using Anfeta.UI.Services.Auth;
using Anfeta.UI.Services.Calendar;
using Anfeta.UI.Services.Weblab;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public sealed class ApiActionExecutor
    {
        private readonly WeblabActividadesClient _actividades;
        private readonly WeblabRevisionesClient _revisiones;
        private readonly WeblabAuthClient _auth;
        private readonly WeblabReportesClient _reportes;
        private readonly WeblabRecordatoriosClient _recordatorios;
        private readonly GoogleCalendarClient _googleCalendar;  // NUEVO
        private readonly GoogleAuthService _googleAuth;          // NUEVO
        private string? _cachedAssignee;

        public ApiActionExecutor(
            WeblabActividadesClient actividades,
            WeblabRevisionesClient revisiones,
            WeblabReportesClient reportes,
            WeblabRecordatoriosClient recordatorios,
            WeblabAuthClient auth,
            GoogleCalendarClient googleCalendar,   // NUEVO
            GoogleAuthService googleAuth)           // NUEVO
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
        /// Ejecuta llamada API basada en provider/resource/action
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

            if (string.IsNullOrWhiteSpace(provider) || provider != "weblab")
                return (false, $"Provider no soportado: '{provider}'. Solo 'weblab' está disponible.");

            if (string.IsNullOrWhiteSpace(resource))
                return (false, "Falta especificar el resource (actividades, revisiones, etc).");

            if (string.IsNullOrWhiteSpace(action))
                return (false, "Falta especificar la action (list, today, search, get, etc).");

            // =========================
            // ACTIVIDADES
            // =========================
            if (resource == "actividades")
            {
                // ✅ TODAS MIS ACTIVIDADES
                if (action == "list")
                {
                    var limit = TryGetInt(paramsJson, "limit") ?? 10;
                    var r = await _actividades.GetMyActivitiesAsync(limit, ct);
                    return (r.Ok, r.PlainText);
                }

                // ✅ MIS ACTIVIDADES DE HOY
                if (action == "today")
                {
                    var assignee = await GetOrFetchAssigneeAsync(ct);
                    if (string.IsNullOrWhiteSpace(assignee))
                        return (false, "No pude identificar tu usuario.");

                    var r = await _actividades.GetTodayActivitiesAsync(assignee, ct);
                    return (r.Ok, r.PlainText);
                }

                // ✅ BUSCAR
                if (action == "search")
                {
                    var q = TryGetString(paramsJson, "q");
                    if (string.IsNullOrWhiteSpace(q))
                        return (false, "Búsqueda inválida: falta params.q.");

                    var limit = TryGetInt(paramsJson, "limit") ?? 10;
                    var r = await _actividades.SearchTitlesAsync(q!, limit, ct);
                    return (r.Ok, r.PlainText);
                }

                // ✅ DETALLES POR ID
                if (action == "get")
                {
                    var id = TryGetString(paramsJson, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        return (false, "Falta el ID de la actividad.");

                    var r = await _actividades.GetActivityByIdAsync(id!, ct);
                    return (r.Ok, r.PlainText);
                }

                // ✅ CREAR ACTIVIDAD
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

            // =========================
            // REVISIONES
            // =========================
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

            // =========================
            // REPORTES
            // =========================
            if (resource == "reportes")
            {
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

                return (false, $"Acción '{action}' no soportada para reportes. Disponibles: list, today.");
            }

            // =========================
            // RECORDATORIOS
            // =========================
            if (resource == "recordatorios")
            {
                // Todos los recordatorios del usuario
                if (action == "list")
                {
                    var r = await _recordatorios.GetMyRecordatoriosAsync(ct);
                    return (r.Ok, r.PlainText);
                }

                // Recordatorios pendientes (activos y no enviados)
                if (action == "pending" || action == "pendientes")
                {
                    var r = await _recordatorios.GetMyPendingRecordatoriosAsync(ct);
                    return (r.Ok, r.PlainText);
                }

                // Recordatorios de hoy
                if (action == "today")
                {
                    var r = await _recordatorios.GetMyTodayRecordatoriosAsync(ct);
                    return (r.Ok, r.PlainText);
                }

                // Recordatorios de mañana
                if (action == "tomorrow" || action == "mañana")
                {
                    var r = await _recordatorios.GetMyTomorrowRecordatoriosAsync(ct);
                    return (r.Ok, r.PlainText);
                }

                // Crear recordatorio
                if (action == "create")
                {
                    var mensaje = TryGetString(paramsJson, "mensaje");
                    var fechaHora = TryGetString(paramsJson, "fechaHora");

                    if (string.IsNullOrWhiteSpace(mensaje))
                        return (false, "Falta el mensaje del recordatorio.");

                    if (string.IsNullOrWhiteSpace(fechaHora))
                        return (false, "Falta la fecha/hora del recordatorio.");

                    var duracion = TryGetInt(paramsJson, "duracionMinutos") ?? 30;

                    var r = await _recordatorios.CreateRecordatorioAsync(mensaje!, fechaHora!, duracion, ct);
                    return (r.Ok, r.PlainText);
                }

                // Completar recordatorio
                if (action == "complete")
                {
                    var id = TryGetString(paramsJson, "id");

                    if (string.IsNullOrWhiteSpace(id))
                        return (false, "Falta el ID del recordatorio.");

                    var r = await _recordatorios.CompleteRecordatorioAsync(id!, ct);
                    return (r.Ok, r.PlainText);
                }

                return (false, $"Acción '{action}' no soportada para recordatorios. Disponibles: list, pending, today, tomorrow, create, complete.");
            }

            // =========================
            // GOOGLE CALENDAR
            // =========================
            if (provider == "google")
            {
                if (resource != "calendar")
                    return (false, $"Resource '{resource}' no soportado para Google. Usa 'calendar'.");

                // ── STATUS ──────────────────────────────────────────
                if (action == "status")
                {
                    var connected = await _googleAuth.IsConnectedAsync(ct);
                    return connected
                        ? (true, "Tu Google Calendar está conectado.")
                        : (false, "Tu Google Calendar no está conectado. Di 'conectar Google Calendar' para vincularlo.");
                }

                // ── CONNECT ─────────────────────────────────────────
                if (action == "connect")
                {
                    var (ok, msg) = await _googleAuth.StartOAuthAsync(ct);
                    return (ok, msg);
                }

                // ── DISCONNECT ──────────────────────────────────────
                if (action == "disconnect")
                {
                    var (ok, msg) = await _googleAuth.DisconnectAsync(ct);
                    return (ok, msg);
                }

                // ── CREATE EVENT ────────────────────────────────────
                if (action == "create")
                {
                    // Verificar conexión antes de intentar crear
                    var connected = await _googleAuth.IsConnectedAsync(ct);
                    if (!connected)
                        return (false, "Tu Google Calendar no está conectado. Di 'conectar Google Calendar' primero.");

                    var userId = await _googleAuth.GetUserIdAsync(ct);
                    if (string.IsNullOrWhiteSpace(userId))
                        return (false, "No se pudo identificar tu usuario.");

                    var summary = TryGetString(paramsJson, "summary");
                    var start = TryGetString(paramsJson, "start");
                    var end = TryGetString(paramsJson, "end");

                    if (string.IsNullOrWhiteSpace(summary))
                        return (false, "Falta el título del evento (params.summary).");

                    if (string.IsNullOrWhiteSpace(start))
                        return (false, "Falta la fecha de inicio del evento (params.start).");

                    if (string.IsNullOrWhiteSpace(end))
                        return (false, "Falta la fecha de fin del evento (params.end).");

                    var description = TryGetString(paramsJson, "description");
                    var location = TryGetString(paramsJson, "location");

                    var result = await _googleCalendar.CreateEventAsync(
                        userId!, summary!, start!, end!,
                        description, location, ct);

                    // El backend indicó que necesita auth (tokens expirados/revocados)
                    if (result.AuthNeeded)
                    {
                        await _googleAuth.StartOAuthAsync(ct);
                        return (false, "Tu sesión de Google expiró. Se abrió el navegador para reconectar.");
                    }

                    return (result.Ok, result.Message);
                }

                // ── LIST EVENTS ─────────────────────────────────────
                if (action == "list")
                {
                    var connected = await _googleAuth.IsConnectedAsync(ct);
                    if (!connected)
                        return (false, "Tu Google Calendar no está conectado.");

                    var userId = await _googleAuth.GetUserIdAsync(ct);
                    var timeMin = TryGetString(paramsJson, "timeMin");
                    var timeMax = TryGetString(paramsJson, "timeMax");
                    var max = TryGetInt(paramsJson, "maxResults") ?? 10;

                    var (ok, msg, _) = await _googleCalendar.ListEventsAsync(
                        userId!, timeMin, timeMax, max, ct);

                    return (ok, msg);
                }

                // ── DELETE EVENT ────────────────────────────────────
                if (action == "delete")
                {
                    var connected = await _googleAuth.IsConnectedAsync(ct);
                    if (!connected)
                        return (false, "Tu Google Calendar no está conectado.");

                    var userId = await _googleAuth.GetUserIdAsync(ct);
                    var eventId = TryGetString(paramsJson, "eventId");

                    if (string.IsNullOrWhiteSpace(eventId))
                        return (false, "Falta el ID del evento (params.eventId).");

                    var (ok, msg) = await _googleCalendar.DeleteEventAsync(userId!, eventId!, ct);
                    return (ok, msg);
                }

                return (false, $"Acción '{action}' no soportada para Google Calendar. Disponibles: status, connect, disconnect, create, list, delete.");
            }

            return (false, $"Resource '{resource}' no soportado. Disponibles: actividades, revisiones, reportes, recordatorios.");
        }

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
            catch
            {
                return null;
            }
        }

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
            catch
            {
                return null;
            }
        }
    }
}