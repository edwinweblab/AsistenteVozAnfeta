// Services/ApiActionExecutor.cs
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Services.Weblab;
using Anfeta.UI.Services.Auth;

namespace Anfeta.UI.Services
{
    public sealed class ApiActionExecutor
    {
        private readonly WeblabActividadesClient _actividades;
        private readonly WeblabRevisionesClient _revisiones;
        private readonly WeblabAuthClient _auth;
        private readonly WeblabReportesClient _reportes;

        private string? _cachedAssignee;

        public ApiActionExecutor(
            WeblabActividadesClient actividades,
            WeblabRevisionesClient revisiones,
            WeblabReportesClient reportes,
            WeblabAuthClient auth)

        {
            _actividades = actividades;
            _revisiones = revisiones;
            _reportes = reportes;
            _auth = auth;
        }

        // Ejecuta llamada API basada en provider/resource/action
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
                // ✅ TODAS MIS ACTIVIDADES (sin fecha)
                // Usa: GET /api/actividades/assignee/:assignee
                if (action == "list")
                {
                    var limit = TryGetInt(paramsJson, "limit") ?? 10;

                    var assignee = await GetOrFetchAssigneeAsync(ct);
                    if (string.IsNullOrWhiteSpace(assignee))
                        return (false, "No pude identificar tu usuario.");

                    // Este método ya lo tienes en WeblabActividadesClient
                    var r = await _actividades.GetMyActivitiesAsync(limit, ct);
                    return (r.Ok, r.PlainText);
                }

                // ✅ MIS ACTIVIDADES DE HOY
                // Usa: GET /api/actividades/assignee/:assignee/del-dia
                if (action == "today")
                {
                    var assignee = await GetOrFetchAssigneeAsync(ct);
                    if (string.IsNullOrWhiteSpace(assignee))
                        return (false, "No pude identificar tu usuario.");

                    var r = await _actividades.GetTodayActivitiesAsync(assignee, ct);
                    return (r.Ok, r.PlainText);
                }

                // ✅ BUSCAR (general, no filtra por usuario si tu API no lo hace)
                // Usa: GET /api/actividades/buscar?q=...
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
                // Usa: GET /api/actividades/:id
                if (action == "get")
                {
                    var id = TryGetString(paramsJson, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        return (false, "Falta el ID de la actividad.");

                    var r = await _actividades.GetActivityByIdAsync(id!, ct);
                    return (r.Ok, r.PlainText);
                }

                return (false, $"Acción '{action}' no soportada para actividades. Disponibles: list, today, search, get.");
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
                    var date = TryGetString(paramsJson, "date"); // SOLO date
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


            return (false, $"Resource '{resource}' no soportado. Disponibles: actividades, revisiones, reportes.");
        }


        private async Task<string?> GetOrFetchAssigneeAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_cachedAssignee))
                return _cachedAssignee;

            var (ok, assignee, _) = await _auth.GetCurrentUserAsync(ct);
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
