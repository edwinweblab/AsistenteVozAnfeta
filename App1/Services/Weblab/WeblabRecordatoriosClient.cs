// Services/Weblab/WeblabRecordatoriosClient.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;
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

        // Obtiene todos los recordatorios del usuario autenticado
        // Salida: lista de recordatorios o mensaje de error
        public async Task<ApiPlainResponse> GetMyRecordatoriosAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, phone, name) = await _auth.GetCurrentUserPhoneAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(phone))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu teléfono." };

                var normalizedPhone = phone.Replace("+", "").Replace(" ", "");
                if (normalizedPhone.Length > 10)
                    normalizedPhone = normalizedPhone[^10..];
                var url = $"/api/recordatorios/usuario/{Uri.EscapeDataString(normalizedPhone)}";

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tus recordatorios." };

                var recordatorios = ParseRecordatorios(json);

                if (recordatorios.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildRecordatoriosText($"Tienes {recordatorios.Count} recordatorios", recordatorios)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios." };
            }
        }

        // Obtiene recordatorios pendientes (activos y no enviados) del usuario autenticado
        public async Task<ApiPlainResponse> GetMyPendingRecordatoriosAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, phone, _) = await _auth.GetCurrentUserPhoneAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(phone))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu teléfono." };

                using var resp = await _http.GetAsync("/api/recordatorios/pendientes", ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener recordatorios pendientes." };

                var normalizedPhone = phone.Replace("+", "").Replace(" ", "");
                if (normalizedPhone.Length > 10)
                    normalizedPhone = normalizedPhone[^10..];

                var recordatorios = ParseRecordatorios(json)
                    .Where(r => r.UserId == normalizedPhone)
                    .ToList();

                if (recordatorios.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios pendientes." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildRecordatoriosText($"Tienes {recordatorios.Count} recordatorios pendientes", recordatorios)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios pendientes." };
            }
        }

        // Obtiene recordatorios de HOY del usuario autenticado
        public async Task<ApiPlainResponse> GetMyTodayRecordatoriosAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, phone, _) = await _auth.GetCurrentUserPhoneAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(phone))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu teléfono." };

                var normalizedPhone = phone.Replace("+", "").Replace(" ", "");
                if (normalizedPhone.Length > 10)
                    normalizedPhone = normalizedPhone[^10..];
                var url = $"/api/recordatorios/usuario/{Uri.EscapeDataString(normalizedPhone)}";

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener recordatorios de hoy." };

                var today = DateTime.Today;
                var recordatorios = ParseRecordatorios(json)
                    .Where(r => r.FechaHora.Date == today)
                    .ToList();

                if (recordatorios.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios para hoy." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildRecordatoriosText($"Hoy tienes {recordatorios.Count} recordatorios", recordatorios)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios de hoy." };
            }
        }

        // Obtiene recordatorios de MAÑANA del usuario autenticado
        public async Task<ApiPlainResponse> GetMyTomorrowRecordatoriosAsync(CancellationToken ct = default)
        {
            try
            {
                var (ok, phone, _) = await _auth.GetCurrentUserPhoneAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(phone))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu teléfono." };

                var normalizedPhone = phone.Replace("+", "").Replace(" ", "");
                if (normalizedPhone.Length > 10)
                    normalizedPhone = normalizedPhone[^10..];
                var url = $"/api/recordatorios/usuario/{Uri.EscapeDataString(normalizedPhone)}";

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener recordatorios de mañana." };

                var tomorrow = DateTime.Today.AddDays(1);
                var recordatorios = ParseRecordatorios(json)
                    .Where(r => r.FechaHora.Date == tomorrow)
                    .ToList();

                if (recordatorios.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes recordatorios para mañana." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildRecordatoriosText($"Mañana tienes {recordatorios.Count} recordatorios", recordatorios)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando recordatorios de mañana." };
            }
        }


        // Crea recordatorio nuevo
        // Entrada: mensaje, fechaHora ISO, duracionMinutos opcional
        public async Task<ApiPlainResponse> CreateRecordatorioAsync(
            string mensaje,
            string fechaHoraISO,
            int duracionMinutos = 30,
            CancellationToken ct = default)
        {
            try
            {
                var (ok, phone, _) = await _auth.GetCurrentUserPhoneAsync(ct);
                if (!ok || string.IsNullOrWhiteSpace(phone))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu teléfono." };

                var payload = new
                {
                    userId = phone,
                    mensaje,
                    fechaHora = fechaHoraISO,
                    duracionMinutos,
                    tipo = "unica_vez"
                };

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                using var resp = await _http.PostAsync("/api/recordatorios", content, ct);
                var respJson = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude crear el recordatorio." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = $"Recordatorio creado: {mensaje}"
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error creando recordatorio." };
            }
        }

        // Marca recordatorio como enviado/completado
        // Entrada: id del recordatorio
        public async Task<ApiPlainResponse> CompleteRecordatorioAsync(string id, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/recordatorios/{Uri.EscapeDataString(id)}/completar";

                using var content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
                using var resp = await _http.PatchAsync(url, content, ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude marcar el recordatorio." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = "Recordatorio marcado como completado."
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error marcando recordatorio." };
            }
        }

        // Parse JSON de API a lista de recordatorios
        private static List<Recordatorio> ParseRecordatorios(string json)
        {
            var list = new List<Recordatorio>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Caso 1: Array directo []
            if (root.ValueKind == JsonValueKind.Array)
            {
                return ParseRecordatoriosFromArray(root);
            }

            // Caso 2: {"data": []}
            if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
            {
                return ParseRecordatoriosFromArray(dataEl);
            }

            return list;
        }

        // Extrae recordatorios de un JsonElement array
        private static List<Recordatorio> ParseRecordatoriosFromArray(JsonElement arrayEl)
        {
            var list = new List<Recordatorio>();

            foreach (var item in arrayEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var id = item.TryGetProperty("_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? ""
                    : "";

                var userId = item.TryGetProperty("userId", out var uEl) && uEl.ValueKind == JsonValueKind.String
                    ? uEl.GetString() ?? ""
                    : "";

                var mensaje = item.TryGetProperty("mensaje", out var mEl) && mEl.ValueKind == JsonValueKind.String
                    ? mEl.GetString() ?? ""
                    : "";

                var fechaHoraStr = item.TryGetProperty("fechaHora", out var fhEl) && fhEl.ValueKind == JsonValueKind.String
                    ? fhEl.GetString()
                    : null;

                if (!DateTime.TryParse(fechaHoraStr, out var fechaHora))
                    continue;

                var duracion = item.TryGetProperty("duracionMinutos", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                    ? dEl.GetInt32()
                    : 0;

                var tipo = item.TryGetProperty("tipo", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? "unica_vez"
                    : "unica_vez";

                var activo = item.TryGetProperty("activo", out var aEl) && aEl.ValueKind == JsonValueKind.True;

                var enviado = item.TryGetProperty("enviado", out var eEl) && eEl.ValueKind == JsonValueKind.True;

                var revisionId = item.TryGetProperty("revisionId", out var rEl) && rEl.ValueKind == JsonValueKind.String
                    ? rEl.GetString()
                    : null;

                var actividadId = item.TryGetProperty("actividadId", out var acEl) && acEl.ValueKind == JsonValueKind.String
                    ? acEl.GetString()
                    : null;

                var googleEventId = item.TryGetProperty("googleEventId", out var geEl) && geEl.ValueKind == JsonValueKind.String
                    ? geEl.GetString()
                    : null;

                var googleHtmlLink = item.TryGetProperty("googleHtmlLink", out var ghEl) && ghEl.ValueKind == JsonValueKind.String
                    ? ghEl.GetString()
                    : null;

                var timezone = item.TryGetProperty("timezone", out var tzEl) && tzEl.ValueKind == JsonValueKind.String
                    ? tzEl.GetString()
                    : null;

                list.Add(new Recordatorio(
                    id,
                    userId,
                    mensaje,
                    fechaHora,
                    duracion,
                    tipo,
                    activo,
                    enviado,
                    revisionId,
                    actividadId,
                    googleEventId,
                    googleHtmlLink,
                    timezone
                ));
            }

            return list;
        }

        // Construye texto legible de lista de recordatorios
        private static string BuildRecordatoriosText(string header, List<Recordatorio> recordatorios)
        {
            var max = Math.Min(recordatorios.Count, 10);

            var parts = new List<string> { header };
            for (var i = 0; i < max; i++)
            {
                var r = recordatorios[i];
                var hora = r.FechaHora.ToString("HH:mm");
                var fecha = r.FechaHora.ToString("dd/MM/yyyy");
                var estado = r.Enviado ? "enviado" : "pendiente";

                parts.Add($"{i + 1}) {r.Mensaje} - {fecha} a las {hora} ({estado})");
            }

            return string.Join(". ", parts);
        }
    }
}