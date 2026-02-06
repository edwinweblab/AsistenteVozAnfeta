using Anfeta.UI.Models;
using Anfeta.UI.Services.Auth;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Weblab
{
    public sealed class WeblabReportesClient
    {
        private readonly HttpClient _http;
        private readonly WeblabAuthClient _auth;

        public WeblabReportesClient(HttpClient http, WeblabAuthClient auth)
        {
            _http = http;
            _auth = auth;
        }

        // ============================
        // BuildPlainText (TOTALES + HORAS)
        // - total (count)
        // - terminadas
        // - confirmadas
        // - pendientes
        // - minutos -> horas
        // ============================
        private static string BuildPlainText(string json, string name, string? assigneeEmail = null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data))
                    return "No pude leer el reporte.";

                var date = data.TryGetProperty("date", out var d) ? (d.GetString() ?? "").Trim() : "";

                if (!data.TryGetProperty("colaboradores", out var colabs) || colabs.ValueKind != JsonValueKind.Array)
                    return "No hay colaboradores en el reporte.";

                // Buscar MIS datos:
                // En tu API el query param "assignee" es el correo (ej: eedwi@practicante.com),
                // pero dentro del JSON el correo aparece en assignees[].name.
                if (!string.IsNullOrWhiteSpace(assigneeEmail))
                {
                    foreach (var c in colabs.EnumerateArray())
                    {
                        if (!BelongsToAssigneeEmail(c, assigneeEmail))
                            continue;

                        var terminadas = c.TryGetProperty("terminadas", out var t) ? t.GetInt32() : 0;
                        var confirmadas = c.TryGetProperty("confirmadas", out var cf) ? cf.GetInt32() : 0;
                        var pendientes = c.TryGetProperty("pendientes", out var p) ? p.GetInt32() : 0;

                        var total = c.TryGetProperty("count", out var ct)
                            ? ct.GetInt32()
                            : (terminadas + confirmadas + pendientes);

                        var minutos = c.TryGetProperty("minutos", out var m) ? m.GetInt32() : 0;
                        var horas = minutos / 60.0;

                        var fechaTxt = string.IsNullOrWhiteSpace(date) ? "hoy" : $"el {date}";

                        // Mensaje simple como pediste
                        return $"{name}, {fechaTxt} tienes {total} revisiones: {terminadas} terminadas, {confirmadas} confirmadas y {pendientes} pendientes. Tiempo: {horas:0.##} horas ({minutos} min).";
                    }
                }

                // Fallback: si no encontramos al usuario, damos el total del día (general)
                var totalDia = data.TryGetProperty("totalRevisiones", out var td) ? td.GetInt32() : 0;
                var fechaDia = string.IsNullOrWhiteSpace(date) ? "" : $" del {date}";
                return $"{name}, hay {totalDia} revisiones registradas{fechaDia}.";
            }
            catch
            {
                return "No pude interpretar el reporte.";
            }
        }

        // Detecta si este bloque de colaborador incluye el correo del usuario
        // buscando en items.actividades[].(terminadas|confirmadas|pendientes)[][].assignees[].name
        private static bool BelongsToAssigneeEmail(JsonElement colaborador, string assigneeEmail)
        {
            if (!colaborador.TryGetProperty("items", out var items))
                return false;

            if (!items.TryGetProperty("actividades", out var actividades) || actividades.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var act in actividades.EnumerateArray())
            {
                foreach (var bucketName in new[] { "terminadas", "confirmadas", "pendientes" })
                {
                    if (!act.TryGetProperty(bucketName, out var bucket) || bucket.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var rev in bucket.EnumerateArray())
                    {
                        if (!rev.TryGetProperty("assignees", out var assignees) || assignees.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var a in assignees.EnumerateArray())
                        {
                            if (!a.TryGetProperty("name", out var n))
                                continue;

                            var email = (n.GetString() ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(email))
                                continue;

                            if (email.Equals(assigneeEmail, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        public async Task<ApiPlainResponse> GetMyRevisionsReportAsync(string? date = null, CancellationToken ct = default)
        {
            try
            {
                // Nota: en tu app esto devuelve (ok, assignee, name)
                // y por tu uso actual "assignee" es el correo del token (ej: eedwi@practicante.com)
                var (ok, assignee, name) = await _auth.GetCurrentUserAsync(ct);

                if (!ok || string.IsNullOrWhiteSpace(assignee))
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude identificar tu usuario." };

                if (string.IsNullOrWhiteSpace(date))
                    date = DateTime.Today.ToString("yyyy-MM-dd");

                var url =
                    $"/api/reportes/revisiones-por-fecha?assignee={Uri.EscapeDataString(assignee)}&date={Uri.EscapeDataString(date)}";

                // =========================
                // LOGS IMPORTANTES
                // =========================
                Debug.WriteLine("====== REPORTES API ======");
                Debug.WriteLine($"Usuario (assignee email): {assignee}");
                Debug.WriteLine($"Fecha enviada: {date}");
                Debug.WriteLine($"URL FINAL: {url}");
                Debug.WriteLine("==========================");

                using var resp = await _http.GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                // LOG RESPUESTA
                Debug.WriteLine("====== RESPUESTA API ======");
                Debug.WriteLine($"Status: {(int)resp.StatusCode}");
                Debug.WriteLine($"Body: {json}");
                Debug.WriteLine("===========================");

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener tus reportes." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildPlainText(json, name ?? "usuario", assignee)
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ERROR REPORTES API: " + ex.Message);
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando reportes." };
            }
        }
    }
}
