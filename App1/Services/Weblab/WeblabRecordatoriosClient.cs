// Services/Weblab/WeblabRecordatoriosClient.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Auth;

namespace Anfeta.UI.Services.Weblab
{
    public sealed class WeblabRecordatoriosClient
    {
        private readonly HttpClient _http;
        private readonly WeblabAuthClient _auth;

        public WeblabRecordatoriosClient(HttpClient http, WeblabAuthClient auth)
        {
            _http = http;
            _auth = auth;
        }

        // ─────────────────────────────────────────────
        // HELPERS PRIVADOS
        // ─────────────────────────────────────────────

        /// <summary>
        /// Normaliza el teléfono del usuario a los últimos 10 dígitos sin prefijos.
        /// Entrada: teléfono en cualquier formato (+5217712045261, +521...)
        /// Salida: últimos 10 dígitos (ej: 7712045261)
        /// </summary>
        private static string NormalizePhone(string phone)
        {
            var digits = phone.Replace("+", "").Replace(" ", "").Replace("-", "");
            return digits.Length > 10 ? digits[^10..] : digits;
        }

        /// <summary>
        /// Obtiene y normaliza el teléfono del usuario autenticado.
        /// Salida: (ok, phoneNormalizado)
        /// </summary>
        private async Task<(bool ok, string? phone)> GetNormalizedPhoneAsync(CancellationToken ct)
        {
            var (ok, phone, _) = await _auth.GetCurrentUserPhoneAsync(ct);
            if (!ok || string.IsNullOrWhiteSpace(phone))
                return (false, null);

            return (true, NormalizePhone(phone));
        }

        /// <summary>
        /// Método base compartido: ejecuta GET /api/recordatorios/usuario/:userId y devuelve lista raw.
        /// Centraliza el HTTP call — todos los métodos GET lo usan para evitar duplicación.
        /// Salida: (ok, lista, errorText)
        /// </summary>
        private async Task<(bool ok, List<Recordatorio> list, string errorText)> FetchAllRecordatoriosRawAsync(CancellationToken ct)
        {
            var (ok, phone) = await GetNormalizedPhoneAsync(ct);
            if (!ok)
                return (false, new List<Recordatorio>(), "No pude identificar tu teléfono.");

            var url = $"/api/recordatorios/usuario/{Uri.EscapeDataString(phone!)}";
            Debug.WriteLine($"[REC] FetchAllRecordatoriosRawAsync → {url}");

            using var resp = await _http.GetAsync(url, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            Debug.WriteLine($"[REC] FetchAllRecordatoriosRaw status={resp.StatusCode}");

            if (!resp.IsSuccessStatusCode)
                return (false, new List<Recordatorio>(), "No pude obtener tus recordatorios.");

            return (true, ParseRecordatorios(json), "");
        }

        // ─────────────────────────────────────────────
        // GETs — solo TTS (usados por ApiActionExecutor)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Obtiene todos los recordatorios. Devuelve texto para TTS.
        /// </summary>
        public async Task<ApiPlainResponse> GetMyRecordatoriosAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, list, error) = await FetchAllRecordatoriosRawAsync(ct);
                if (!ok) return new ApiPlainResponse { Ok = false, PlainText = error };

                if (list.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios." };

                return new ApiPlainResponse { Ok = true, PlainText = BuildRecordatoriosText($"Tienes {list.Count} recordatorios", list) };
            }
            catch (OperationCanceledException) { return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] GetMyRecordatoriosAsync ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios." };
            }
        }

        /// <summary>
        /// Obtiene recordatorios pendientes (activo=true, enviado=false). Devuelve texto para TTS.
        /// </summary>
        public async Task<ApiPlainResponse> GetMyPendingRecordatoriosAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, all, error) = await FetchAllRecordatoriosRawAsync(ct);
                if (!ok) return new ApiPlainResponse { Ok = false, PlainText = error };

                var list = all.Where(r => r.Activo && !r.Enviado).ToList();

                if (list.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios pendientes." };

                return new ApiPlainResponse { Ok = true, PlainText = BuildRecordatoriosText($"Tienes {list.Count} recordatorios pendientes", list) };
            }
            catch (OperationCanceledException) { return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] GetMyPendingRecordatoriosAsync ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios pendientes." };
            }
        }

        /// <summary>
        /// Obtiene recordatorios de HOY. Devuelve texto para TTS.
        /// </summary>
        public async Task<ApiPlainResponse> GetMyTodayRecordatoriosAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, all, error) = await FetchAllRecordatoriosRawAsync(ct);
                if (!ok) return new ApiPlainResponse { Ok = false, PlainText = error };

                var list = all.Where(r => r.FechaHora.ToLocalTime().Date == DateTime.Today).ToList();

                if (list.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios para hoy." };

                return new ApiPlainResponse { Ok = true, PlainText = BuildRecordatoriosText($"Hoy tienes {list.Count} recordatorios", list) };
            }
            catch (OperationCanceledException) { return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] GetMyTodayRecordatoriosAsync ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios de hoy." };
            }
        }

        /// <summary>
        /// Obtiene recordatorios de MAÑANA. Devuelve texto para TTS.
        /// </summary>
        public async Task<ApiPlainResponse> GetMyTomorrowRecordatoriosAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, all, error) = await FetchAllRecordatoriosRawAsync(ct);
                if (!ok) return new ApiPlainResponse { Ok = false, PlainText = error };

                var list = all.Where(r => r.FechaHora.ToLocalTime().Date == DateTime.Today.AddDays(1)).ToList();

                if (list.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios para mañana." };

                return new ApiPlainResponse { Ok = true, PlainText = BuildRecordatoriosText($"Mañana tienes {list.Count} recordatorios", list) };
            }
            catch (OperationCanceledException) { return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] GetMyTomorrowRecordatoriosAsync ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios de mañana." };
            }
        }

        // ─────────────────────────────────────────────
        // GET CON LISTA — para caché de selección en HomeViewModel
        // ─────────────────────────────────────────────

        /// <summary>
        /// Obtiene recordatorios con filtro y devuelve AMBOS: texto TTS y lista de objetos.
        /// Usado por HomeViewModel para cachear la lista después de listarla por voz,
        /// permitiendo al usuario decir "elimina el 2" en el turno siguiente.
        /// Entrada: filter = "all" | "today" | "tomorrow" | "pending"
        /// Salida: (ApiPlainResponse para TTS, List<Recordatorio> para caché)
        /// </summary>
        public async Task<(ApiPlainResponse response, List<Recordatorio> list)> GetMyRecordatoriosWithListAsync(
            string filter = "all",
            CancellationToken ct = default)
        {
            try
            {
                var (ok, all, error) = await FetchAllRecordatoriosRawAsync(ct);
                if (!ok)
                    return (new ApiPlainResponse { Ok = false, PlainText = error }, new List<Recordatorio>());

                List<Recordatorio> list;
                string header;

                switch (filter.ToLowerInvariant())
                {
                    case "today":
                        list = all.Where(r => r.FechaHora.ToLocalTime().Date == DateTime.Today).ToList();
                        header = $"Hoy tienes {list.Count} recordatorios";
                        if (list.Count == 0)
                            return (new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios para hoy." }, list);
                        break;

                    case "tomorrow":
                        list = all.Where(r => r.FechaHora.ToLocalTime().Date == DateTime.Today.AddDays(1)).ToList();
                        header = $"Mañana tienes {list.Count} recordatorios";
                        if (list.Count == 0)
                            return (new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios para mañana." }, list);
                        break;

                    case "pending":
                        list = all.Where(r => r.Activo && !r.Enviado).ToList();
                        header = $"Tienes {list.Count} recordatorios pendientes";
                        if (list.Count == 0)
                            return (new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios pendientes." }, list);
                        break;

                    default: // "all"
                        list = all;
                        header = $"Tienes {list.Count} recordatorios";
                        if (list.Count == 0)
                            return (new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios." }, list);
                        break;
                }

                return (new ApiPlainResponse { Ok = true, PlainText = BuildRecordatoriosText(header, list) }, list);
            }
            catch (OperationCanceledException)
            {
                return (new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }, new List<Recordatorio>());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] GetMyRecordatoriosWithListAsync ERROR: {ex.Message}");
                return (new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios." }, new List<Recordatorio>());
            }
        }

        // ─────────────────────────────────────────────
        // CREATE
        // ─────────────────────────────────────────────

        /// <summary>
        /// Crea un recordatorio general. El backend sincroniza con Google Calendar automáticamente.
        /// Llama a POST /api/recordatorios
        /// Entrada: mensaje, fechaHora ISO 8601 con offset, duracionMinutos (default 30).
        /// Salida: ApiPlainResponse con resultado e indicación si se necesita conectar Google.
        /// </summary>
        public async Task<ApiPlainResponse> CreateRecordatorioAsync(
            string mensaje,
            string fechaHoraISO,
            int duracionMinutos = 30,
            CancellationToken ct = default)
        {
            try
            {
                var (ok, phone) = await GetNormalizedPhoneAsync(ct);
                if (!ok)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu teléfono." };

                var payload = new
                {
                    userId = phone,
                    mensaje,
                    fechaHora = fechaHoraISO,
                    duracionMinutos,
                    tipo = "unica_vez",
                    timezone = "America/Mexico_City"
                };

                var body = JsonSerializer.Serialize(payload);
                Debug.WriteLine($"[REC] CreateRecordatorioAsync → body={body}");

                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync("/api/recordatorios", content, ct);
                var respJson = await resp.Content.ReadAsStringAsync(ct);

                Debug.WriteLine($"[REC] CreateRecordatorio status={resp.StatusCode}, body={respJson}");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude crear el recordatorio." };

                return ParseCreateResponse(respJson, mensaje);
            }
            catch (OperationCanceledException) { return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] CreateRecordatorioAsync ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error creando recordatorio." };
            }
        }

        // ─────────────────────────────────────────────
        // UPDATE
        // ─────────────────────────────────────────────

        /// <summary>
        /// Actualiza un recordatorio existente. Si ya tenía Google Event, lo actualiza también.
        /// Llama a PUT /api/recordatorios/:id
        /// Entrada: id del recordatorio, campos a actualizar (todos opcionales).
        /// Salida: ApiPlainResponse con resultado.
        /// </summary>
        public async Task<ApiPlainResponse> UpdateRecordatorioAsync(
            string id,
            string? mensaje = null,
            string? fechaHoraISO = null,
            int? duracionMinutos = null,
            CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                    return new ApiPlainResponse { Ok = false, PlainText = "Falta el ID del recordatorio." };

                var dict = new Dictionary<string, object>();
                if (!string.IsNullOrWhiteSpace(mensaje)) dict["mensaje"] = mensaje;
                if (!string.IsNullOrWhiteSpace(fechaHoraISO)) dict["fechaHora"] = fechaHoraISO;
                if (duracionMinutos.HasValue) dict["duracionMinutos"] = duracionMinutos.Value;

                if (dict.Count == 0)
                    return new ApiPlainResponse { Ok = false, PlainText = "No hay campos para actualizar." };

                var body = JsonSerializer.Serialize(dict);
                var url = $"/api/recordatorios/{Uri.EscapeDataString(id)}";
                Debug.WriteLine($"[REC] UpdateRecordatorioAsync → {url}, body={body}");

                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                using var resp = await _http.SendAsync(req, ct);

                Debug.WriteLine($"[REC] UpdateRecordatorio status={resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude actualizar el recordatorio." };

                return new ApiPlainResponse { Ok = true, PlainText = "Recordatorio actualizado correctamente." };
            }
            catch (OperationCanceledException) { return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] UpdateRecordatorioAsync ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error actualizando recordatorio." };
            }
        }

        // ─────────────────────────────────────────────
        // COMPLETE
        // ─────────────────────────────────────────────

        /// <summary>
        /// Marca un recordatorio como completado.
        /// Llama a PATCH /api/recordatorios/:id/completar
        /// Entrada: id del recordatorio.
        /// Salida: ApiPlainResponse con resultado.
        /// </summary>
        public async Task<ApiPlainResponse> CompleteRecordatorioAsync(string id, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                    return new ApiPlainResponse { Ok = false, PlainText = "Falta el ID del recordatorio." };

                var url = $"/api/recordatorios/{Uri.EscapeDataString(id)}/completar";
                Debug.WriteLine($"[REC] CompleteRecordatorioAsync → {url}");

                using var content = new StringContent("", Encoding.UTF8, "application/json");
                using var resp = await _http.PatchAsync(url, content, ct);

                Debug.WriteLine($"[REC] CompleteRecordatorio status={resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude marcar el recordatorio como completado." };

                return new ApiPlainResponse { Ok = true, PlainText = "Recordatorio marcado como completado." };
            }
            catch (OperationCanceledException) { return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] CompleteRecordatorioAsync ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error marcando recordatorio." };
            }
        }

        // ─────────────────────────────────────────────
        // DELETE
        // ─────────────────────────────────────────────

        /// <summary>
        /// Elimina un recordatorio y su evento de Google Calendar si existe.
        /// Llama a DELETE /api/recordatorios/:id
        /// Entrada: id del recordatorio.
        /// Salida: ApiPlainResponse con resultado.
        /// </summary>
        public async Task<ApiPlainResponse> DeleteRecordatorioAsync(string id, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                    return new ApiPlainResponse { Ok = false, PlainText = "Falta el ID del recordatorio." };

                var url = $"/api/recordatorios/{Uri.EscapeDataString(id)}";
                Debug.WriteLine($"[REC] DeleteRecordatorioAsync → {url}");

                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                using var resp = await _http.SendAsync(req, ct);

                Debug.WriteLine($"[REC] DeleteRecordatorio status={resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude eliminar el recordatorio." };

                return new ApiPlainResponse { Ok = true, PlainText = "Recordatorio eliminado correctamente." };
            }
            catch (OperationCanceledException) { return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." }; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[REC] DeleteRecordatorioAsync ERROR: {ex.Message}");
                return new ApiPlainResponse { Ok = false, PlainText = "Error eliminando recordatorio." };
            }
        }

        // ─────────────────────────────────────────────
        // PARSERS PRIVADOS
        // ─────────────────────────────────────────────

        private static ApiPlainResponse ParseCreateResponse(string respJson, string mensaje)
        {
            try
            {
                using var doc = JsonDocument.Parse(respJson);
                var root = doc.RootElement;

                var authRequired = false;
                if (root.TryGetProperty("google", out var googleEl) && googleEl.ValueKind == JsonValueKind.Object)
                    authRequired = googleEl.TryGetProperty("authRequired", out var arEl) && arEl.ValueKind == JsonValueKind.True;

                if (authRequired)
                    return new ApiPlainResponse { Ok = true, PlainText = $"Recordatorio '{mensaje}' creado en el sistema, pero tu Google Calendar no está conectado. Di 'conectar Google Calendar' para sincronizarlo." };

                var hasGoogleEvent = root.TryGetProperty("google", out var gEl) &&
                                     gEl.TryGetProperty("eventId", out var eidEl) &&
                                     eidEl.ValueKind == JsonValueKind.String &&
                                     !string.IsNullOrWhiteSpace(eidEl.GetString());

                var text = hasGoogleEvent
                    ? $"Recordatorio '{mensaje}' creado y sincronizado con tu Google Calendar."
                    : $"Recordatorio '{mensaje}' creado correctamente.";

                return new ApiPlainResponse { Ok = true, PlainText = text };
            }
            catch
            {
                return new ApiPlainResponse { Ok = true, PlainText = $"Recordatorio '{mensaje}' creado correctamente." };
            }
        }

        private static List<Recordatorio> ParseRecordatorios(string json)
        {
            var list = new List<Recordatorio>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                    return ParseRecordatoriosFromArray(root);

                if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                    return ParseRecordatoriosFromArray(dataEl);
            }
            catch (Exception ex) { Debug.WriteLine($"[REC] ParseRecordatorios ERROR: {ex.Message}"); }

            return list;
        }

        private static List<Recordatorio> ParseRecordatoriosFromArray(JsonElement arrayEl)
        {
            var list = new List<Recordatorio>();

            foreach (var item in arrayEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var id = item.TryGetProperty("_id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                var userId = item.TryGetProperty("userId", out var uEl) && uEl.ValueKind == JsonValueKind.String ? uEl.GetString() ?? "" : "";
                var mensaje = item.TryGetProperty("mensaje", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() ?? "" : "";

                var fechaHoraStr = item.TryGetProperty("fechaHora", out var fhEl) && fhEl.ValueKind == JsonValueKind.String ? fhEl.GetString() : null;
                if (!DateTime.TryParse(fechaHoraStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var fechaHora))
                    continue;

                var duracion = item.TryGetProperty("duracionMinutos", out var dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetInt32() : 0;
                var tipo = item.TryGetProperty("tipo", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() ?? "unica_vez" : "unica_vez";
                var activo = item.TryGetProperty("activo", out var aEl) && aEl.ValueKind == JsonValueKind.True;
                var enviado = item.TryGetProperty("enviado", out var eEl) && eEl.ValueKind == JsonValueKind.True;
                var revisionId = item.TryGetProperty("revisionId", out var rEl) && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;
                var actividadId = item.TryGetProperty("actividadId", out var acEl) && acEl.ValueKind == JsonValueKind.String ? acEl.GetString() : null;
                var googleEventId = item.TryGetProperty("googleEventId", out var geEl) && geEl.ValueKind == JsonValueKind.String ? geEl.GetString() : null;
                var googleHtmlLink = item.TryGetProperty("googleHtmlLink", out var ghEl) && ghEl.ValueKind == JsonValueKind.String ? ghEl.GetString() : null;
                var timezone = item.TryGetProperty("timezone", out var tzEl) && tzEl.ValueKind == JsonValueKind.String ? tzEl.GetString() : null;

                list.Add(new Recordatorio(id, userId, mensaje, fechaHora, duracion, tipo, activo, enviado, revisionId, actividadId, googleEventId, googleHtmlLink, timezone));
            }

            return list;
        }

        /// Construye bloques \n\n estructurados para UI (parseables) y TTS (pausas naturales).
        /// Entrada: encabezado, lista (máximo 10).
        /// Salida: bloques separados por \n\n.
        private static string BuildRecordatoriosText(string header, List<Recordatorio> recordatorios)
        {
            var max = Math.Min(recordatorios.Count, 10);
            var blocks = new List<string> { header };

            for (var i = 0; i < max; i++)
            {
                var r = recordatorios[i];
                var localTime = r.FechaHora.ToLocalTime();
                var hora = localTime.ToString("HH:mm");
                var fecha = localTime.Date == DateTime.Today ? "hoy"
                              : localTime.Date == DateTime.Today.AddDays(1) ? "mañana"
                              : localTime.ToString("dd/MM/yyyy");

                var estado = r.Enviado ? "completado" : "pendiente";
                var calendar = !string.IsNullOrWhiteSpace(r.GoogleEventId) ? "Sí" : "No";

                blocks.Add(
                    $"Recordatorio {i + 1}.\n" +
                    $"{r.Mensaje}.\n" +
                    $"Fecha: {fecha} a las {hora}.\n" +
                    $"Estado: {estado}.\n" +
                    $"Calendar: {calendar}."
                );
            }

            return string.Join("\n\n", blocks);
        }
    }
}