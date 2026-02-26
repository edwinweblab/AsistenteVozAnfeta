// Services/GoogleCalendarClient.cs
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Calendar
{
    /// <summary>
    /// HTTP wrapper para todos los endpoints /google/* del backend Weblab.
    /// Entrada: userId (teléfono del usuario), datos del evento según operación.
    /// Salida: GoogleCalendarResult con ok, mensaje y datos opcionales.
    /// No maneja tokens de Google — eso lo hace el backend.
    /// </summary>
    public sealed class GoogleCalendarClient
    {
        private readonly HttpClient _http;

        public GoogleCalendarClient(HttpClient http)
        {
            _http = http;
        }

        // ─────────────────────────────────────────────
        // AUTH
        // ─────────────────────────────────────────────

        /// <summary>
        /// Obtiene la URL de autorización OAuth de Google.
        /// Entrada: userId (teléfono del usuario, e.g. "+5217712045261")
        /// Salida: (ok, authUrl) — la URL debe abrirse en el navegador del sistema.
        /// </summary>
        public async Task<(bool ok, string? authUrl, string? error)> GetAuthUrlAsync(
            string userId,
            CancellationToken ct = default)
        {
            try
            {
                var url = $"/google/auth?userId={Uri.EscapeDataString(userId)}";
                Debug.WriteLine($"[GCAL] GetAuthUrlAsync → {url}");

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[GCAL] GetAuthUrlAsync status={resp.StatusCode}, body={json}");

                if (!resp.IsSuccessStatusCode)
                    return (false, null, $"Error HTTP {(int)resp.StatusCode}");

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // El backend puede devolver { authUrl: "..." } o { ok:true, authUrl:"..." }
                if (root.TryGetProperty("authUrl", out var authEl) &&
                    authEl.ValueKind == JsonValueKind.String)
                {
                    return (true, authEl.GetString(), null);
                }

                return (false, null, "Respuesta sin authUrl.");
            }
            catch (OperationCanceledException)
            {
                return (false, null, "Operación cancelada.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GCAL] GetAuthUrlAsync ERROR: {ex}");
                return (false, null, ex.Message);
            }
        }

        /// <summary>
        /// Verifica si el usuario tiene tokens de Google activos.
        /// Entrada: userId (teléfono)
        /// Salida: (ok, connected) — connected=true si hay tokens válidos.
        /// </summary>
        public async Task<(bool ok, bool connected)> GetStatusAsync(
            string userId,
            CancellationToken ct = default)
        {
            try
            {
                var url = $"/google/status?userId={Uri.EscapeDataString(userId)}";
                Debug.WriteLine($"[GCAL] GetStatusAsync → {url}");

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[GCAL] GetStatusAsync status={resp.StatusCode}, body={json}");

                if (!resp.IsSuccessStatusCode)
                    return (false, false);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // { "ok": true, "connected": true }
                var connected = root.TryGetProperty("connected", out var connEl) &&
                                connEl.ValueKind == JsonValueKind.True;

                return (true, connected);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GCAL] GetStatusAsync ERROR: {ex}");
                return (false, false);
            }
        }

        /// <summary>
        /// Revoca y elimina los tokens de Google del usuario.
        /// Entrada: userId (teléfono)
        /// Salida: (ok, message)
        /// </summary>
        public async Task<(bool ok, string message)> LogoutAsync(
            string userId,
            CancellationToken ct = default)
        {
            try
            {
                var url = $"/google/logout?userId={Uri.EscapeDataString(userId)}";
                Debug.WriteLine($"[GCAL] LogoutAsync → {url}");

                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                using var resp = await _http.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[GCAL] LogoutAsync status={resp.StatusCode}, body={json}");

                if (!resp.IsSuccessStatusCode)
                    return (false, $"Error al cerrar sesión de Google ({(int)resp.StatusCode}).");

                return (true, "Sesión de Google cerrada correctamente.");
            }
            catch (OperationCanceledException)
            {
                return (false, "Operación cancelada.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GCAL] LogoutAsync ERROR: {ex}");
                return (false, ex.Message);
            }
        }

        // ─────────────────────────────────────────────
        // EVENTOS
        // ─────────────────────────────────────────────

        /// <summary>
        /// Crea un evento en Google Calendar.
        /// Entrada: userId, summary (título), start/end en ISO 8601, description y location opcionales.
        /// Salida: (ok, message, eventId, htmlLink)
        /// Si el usuario no tiene tokens devuelve authRequired=true en el resultado.
        /// </summary>
        public async Task<GoogleCalendarEventResult> CreateEventAsync(
            string userId,
            string summary,
            string start,
            string end,
            string? description = null,
            string? location = null,
            CancellationToken ct = default)
        {
            try
            {
                var body = new
                {
                    userId,
                    summary,
                    start,
                    end,
                    description,
                    location
                };

                var payload = JsonSerializer.Serialize(body);
                Debug.WriteLine($"[GCAL] CreateEventAsync → body={payload}");

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync("/google/events", content, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[GCAL] CreateEventAsync status={resp.StatusCode}, body={json}");

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Verificar authRequired
                if (root.TryGetProperty("authRequired", out var arEl) && arEl.GetBoolean())
                {
                    var authUrl = root.TryGetProperty("authUrl", out var auEl)
                        ? auEl.GetString()
                        : null;
                    return GoogleCalendarEventResult.AuthRequired(authUrl);
                }

                if (!resp.IsSuccessStatusCode)
                    return GoogleCalendarEventResult.FromError($"Error HTTP {(int)resp.StatusCode}");

                var ok = root.TryGetProperty("success", out var okEl) && okEl.GetBoolean();
                if (!ok)
                {
                    var err = root.TryGetProperty("error", out var errEl)
                        ? errEl.GetString()
                        : "Error desconocido al crear evento.";
                    return GoogleCalendarEventResult.FromError(err ?? "Error desconocido.");
                }

                string? eventId = null;
                string? htmlLink = null;

                if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                {
                    eventId = dataEl.TryGetProperty("eventId", out var eidEl)
                        ? eidEl.GetString()
                        : null;
                    htmlLink = dataEl.TryGetProperty("htmlLink", out var hlEl)
                        ? hlEl.GetString()
                        : null;
                }

                return GoogleCalendarEventResult.FromOk(
                    $"Evento '{summary}' creado en tu Google Calendar.",
                    eventId,
                    htmlLink);
            }
            catch (OperationCanceledException)
            {
                return GoogleCalendarEventResult.FromError("Operación cancelada.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GCAL] CreateEventAsync ERROR: {ex}");
                return GoogleCalendarEventResult.FromError(ex.Message);
            }
        }

        /// <summary>
        /// Lista eventos del calendario del usuario.
        /// Entrada: userId, timeMin/timeMax opcionales en ISO 8601, maxResults opcional.
        /// Salida: (ok, message, items)
        /// </summary>
        public async Task<(bool ok, string message, System.Collections.Generic.List<GoogleCalendarEventItem> items)>
            ListEventsAsync(
                string userId,
                string? timeMin = null,
                string? timeMax = null,
                int maxResults = 10,
                CancellationToken ct = default)
        {
            var empty = new System.Collections.Generic.List<GoogleCalendarEventItem>();
            try
            {
                var query = $"userId={Uri.EscapeDataString(userId)}&maxResults={maxResults}";
                if (!string.IsNullOrWhiteSpace(timeMin))
                    query += $"&timeMin={Uri.EscapeDataString(timeMin)}";
                if (!string.IsNullOrWhiteSpace(timeMax))
                    query += $"&timeMax={Uri.EscapeDataString(timeMax)}";

                var url = $"/google/events?{query}";
                Debug.WriteLine($"[GCAL] ListEventsAsync → {url}");

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[GCAL] ListEventsAsync status={resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                    return (false, $"Error HTTP {(int)resp.StatusCode}", empty);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                    return (true, "No hay eventos disponibles.", empty);

                var items = new System.Collections.Generic.List<GoogleCalendarEventItem>();
                foreach (var ev in dataEl.EnumerateArray())
                {
                    var eventId = ev.TryGetProperty("eventId", out var eidEl) ? eidEl.GetString() : null;
                    var summary = ev.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() : null;
                    var start = ev.TryGetProperty("start", out var stEl) ? stEl.GetString() : null;
                    var end = ev.TryGetProperty("end", out var enEl) ? enEl.GetString() : null;

                    items.Add(new GoogleCalendarEventItem(eventId, summary, start, end));
                }

                return (true, $"{items.Count} evento(s) encontrado(s).", items);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GCAL] ListEventsAsync ERROR: {ex}");
                return (false, ex.Message, empty);
            }
        }

        /// <summary>
        /// Elimina un evento del calendario.
        /// Entrada: userId, eventId
        /// Salida: (ok, message)
        /// </summary>
        public async Task<(bool ok, string message)> DeleteEventAsync(
            string userId,
            string eventId,
            CancellationToken ct = default)
        {
            try
            {
                var url = $"/google/events/{Uri.EscapeDataString(eventId)}?userId={Uri.EscapeDataString(userId)}";
                Debug.WriteLine($"[GCAL] DeleteEventAsync → {url}");

                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                using var resp = await _http.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[GCAL] DeleteEventAsync status={resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                    return (false, $"Error al eliminar evento ({(int)resp.StatusCode}).");

                return (true, "Evento eliminado correctamente.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GCAL] DeleteEventAsync ERROR: {ex}");
                return (false, ex.Message);
            }
        }
    }

    // ─────────────────────────────────────────────
    // MODELOS DE RESULTADO
    // ─────────────────────────────────────────────

    /// <summary>
    /// Resultado de operaciones de evento en Google Calendar.
    /// AuthNeeded=true indica que el usuario debe conectar su cuenta primero.
    /// </summary>
    public sealed class GoogleCalendarEventResult
    {
        public bool Ok { get; private init; }
        public bool AuthNeeded { get; private init; }
        public string? AuthUrl { get; private init; }
        public string Message { get; private init; } = "";
        public string? EventId { get; private init; }
        public string? HtmlLink { get; private init; }

        public static GoogleCalendarEventResult FromOk(string message, string? eventId, string? htmlLink)
            => new() { Ok = true, Message = message, EventId = eventId, HtmlLink = htmlLink };

        public static GoogleCalendarEventResult FromError(string message)
            => new() { Ok = false, Message = message };

        public static GoogleCalendarEventResult AuthRequired(string? authUrl)
            => new() { Ok = false, AuthNeeded = true, AuthUrl = authUrl, Message = "Necesitas conectar tu Google Calendar primero." };
    }

    /// <summary>Item de evento en lista de Calendar.</summary>
    public sealed record GoogleCalendarEventItem(
        string? EventId,
        string? Summary,
        string? Start,
        string? End);
}