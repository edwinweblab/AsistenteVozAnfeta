using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;
using Anfeta.UI.Services.Auth;

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
        private static string BuildPlainText(string json, string name, string? assignee = null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data))
                    return "No pude leer el reporte.";

                var total = data.GetProperty("totalRevisiones").GetInt32();
                var date = data.GetProperty("date").GetString();

                // Buscar MIS datos
                if (!string.IsNullOrWhiteSpace(assignee) &&
                    data.TryGetProperty("colaboradores", out var colabs))
                {
                    foreach (var c in colabs.EnumerateArray())
                    {
                        var id = c.GetProperty("idAsignee").GetString();

                        if (id == assignee)
                        {
                            var pendientes = c.GetProperty("pendientes").GetInt32();
                            var terminadas = c.GetProperty("terminadas").GetInt32();
                            var confirmadas = c.GetProperty("confirmadas").GetInt32();

                            // =====================
                            // FRASES INTELIGENTES
                            // =====================

                            if (pendientes == 0 && terminadas > 0)
                                return $"{name}, ya terminaste todas tus revisiones del {date}.";

                            if (terminadas == 0 && confirmadas == 0)
                                return $"{name}, tienes {pendientes} revisiones pendientes para el {date}.";

                            if (terminadas > 0 && pendientes > 0)
                                return $"{name}, tienes {terminadas} revisiones terminadas y {pendientes} pendientes para el {date}.";

                            return $"{name}, tienes {total} revisiones para el {date}.";
                        }
                    }
                }

                return $"{name}, hay {total} revisiones registradas.";
            }
            catch
            {
                return "No pude interpretar el reporte.";
            }
        }

        public async Task<ApiPlainResponse> GetMyRevisionsReportAsync(string? date = null, CancellationToken ct = default)
        {
            try
            {
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
                System.Diagnostics.Debug.WriteLine("====== REPORTES API ======");
                System.Diagnostics.Debug.WriteLine($"Usuario (assignee): {assignee}");
                System.Diagnostics.Debug.WriteLine($"Fecha enviada: {date}");
                System.Diagnostics.Debug.WriteLine($"URL FINAL: {url}");
                System.Diagnostics.Debug.WriteLine("==========================");

                using var resp = await _http.GetAsync(url, ct);

                var json = await resp.Content.ReadAsStringAsync(ct);

                // LOG RESPUESTA
                System.Diagnostics.Debug.WriteLine("====== RESPUESTA API ======");
                System.Diagnostics.Debug.WriteLine($"Status: {(int)resp.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"Body: {json}");
                System.Diagnostics.Debug.WriteLine("===========================");

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
                System.Diagnostics.Debug.WriteLine("ERROR REPORTES API: " + ex.Message);
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando reportes." };
            }
        }
    }
}
