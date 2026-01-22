// Services/Weblab/WeblabActividadesClient.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models;

namespace Anfeta.UI.Services.Weblab
{
    public sealed class WeblabActividadesClient
    {
        private readonly HttpClient _http;

        public WeblabActividadesClient(HttpClient http)
        {
            _http = http;
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

        private static List<string> ExtractTitles(string json, int limit)
        {
            var list = new List<string>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Esperado: { "success": true, "data": [ { "titulo": "...", ... }, ... ] }
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

        private static string BuildTitlesPlainText(string header, List<string> titles)
        {
            // Texto corto para UI + TTS
            // Ej: "Actividades: 1) ... 2) ... 3) ..."
            var max = Math.Min(titles.Count, 10);

            var parts = new List<string> { $"{header}: {max}." };
            for (var i = 0; i < max; i++)
                parts.Add($"{i + 1}) {titles[i]}");

            return string.Join(" ", parts);
        }
    }
}
