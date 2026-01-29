// Services/ApiActionExecutor.cs
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Services.Weblab;

namespace Anfeta.UI.Services
{
    public sealed class ApiActionExecutor
    {
        private readonly WeblabActividadesClient _actividades;

        public ApiActionExecutor(WeblabActividadesClient actividades)
        {
            _actividades = actividades;
        }

        // Nombre consistente con tu HomeViewModel actual (ExecuteAsync)
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

            if (provider != "weblab")
                return (false, "Acción API inválida: provider no soportado.");

            if (resource == "actividades")
            {
                if (action == "list")
                {
                    var limit = TryGetInt(paramsJson, "limit") ?? 10;
                    var r = await _actividades.ListTitlesAsync(limit, ct);
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

                return (false, $"Acción actividades no soportada: {action}.");
            }

            return (false, $"Resource no soportado: {resource}.");
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
            catch { return null; }
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
            catch { return null; }
        }
    }
}
