// Services/Weblab/WeblabRevisionesClient.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models.Weblab;

namespace Anfeta.UI.Services.Weblab
{
    public sealed class WeblabRevisionesClient
    {
        private readonly HttpClient _http;

        public WeblabRevisionesClient(HttpClient http)
        {
            _http = http;
        }

        // GET /api/revisiones/today -> Revisiones del día actual
        // Entrada: ninguna
        // Salida: Texto formateado con revisiones del día
        public async Task<ApiPlainResponse> GetTodayRevisionsAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/revisiones/today", ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener las revisiones de hoy." };

                var revisiones = ExtractRevisiones(json, 10);

                if (revisiones.Count == 0)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes revisiones para hoy." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = BuildRevisionesPlainText($"Hoy tienes {revisiones.Count} revisiones", revisiones)
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando revisiones del día." };
            }
        }

        // GET /api/revisiones/en-curso -> Revisión activa actualmente
        // Entrada: ninguna
        // Salida: Texto con detalles de la revisión en curso
        public async Task<ApiPlainResponse> GetActiveRevisionsAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/revisiones/en-curso", ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ApiPlainResponse { Ok = false, PlainText = "No pude obtener la revisión en curso." };

                var activeRevision = ExtractActiveRevision(json);

                if (activeRevision == null)
                    return new ApiPlainResponse { Ok = true, PlainText = "No tienes ninguna revisión en curso." };

                return new ApiPlainResponse
                {
                    Ok = true,
                    PlainText = activeRevision
                };
            }
            catch (OperationCanceledException)
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Operación cancelada." };
            }
            catch
            {
                return new ApiPlainResponse { Ok = false, PlainText = "Error consultando revisión en curso." };
            }
        }

        private static List<(string titulo, string hora)> ExtractRevisiones(string json, int limit)
        {
            var list = new List<(string, string)>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in itemsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var titulo = item.TryGetProperty("titulo", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? (tEl.GetString() ?? "").Trim()
                    : "";

                var hora = item.TryGetProperty("horaInicio", out var hEl) && hEl.ValueKind == JsonValueKind.String
                    ? (hEl.GetString() ?? "").Trim()
                    : "Sin hora";

                if (!string.IsNullOrWhiteSpace(titulo))
                    list.Add((titulo, hora));

                if (list.Count >= limit) break;
            }

            return list;
        }

        private static string? ExtractActiveRevision(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;

            var titulo = data.TryGetProperty("titulo", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() ?? "Sin título"
                : "Sin título";

            var tiempoTranscurrido = data.TryGetProperty("tiempoTranscurrido", out var ttEl) && ttEl.ValueKind == JsonValueKind.String
                ? ttEl.GetString() ?? "Desconocido"
                : "Desconocido";

            return $"Estás en: {titulo}. Tiempo transcurrido: {tiempoTranscurrido}";
        }

        private static string BuildRevisionesPlainText(string header, List<(string titulo, string hora)> revisiones)
        {
            var max = Math.Min(revisiones.Count, 10);

            var parts = new List<string> { header };
            for (var i = 0; i < max; i++)
            {
                var (titulo, hora) = revisiones[i];
                parts.Add($"{i + 1}) {titulo} - {hora}");
            }

            return string.Join(". ", parts);
        }
    }
}