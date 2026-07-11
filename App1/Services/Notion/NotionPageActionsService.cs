using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Notion
{
    public sealed class NotionPageActionsService
    {
        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";

        public async Task<string> RenamePageAsync(
            string token,
            string pageId,
            string dataSourceId,
            string newTitle,
            CancellationToken cancellationToken = default)
        {
            ValidateTokenAndPage(token, pageId);

            if (string.IsNullOrWhiteSpace(dataSourceId))
                throw new ArgumentException("No se pudo identificar la base de Notion.");

            newTitle = (newTitle ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newTitle))
                throw new ArgumentException("El nuevo nombre está vacío.");

            using var http = CreateClient(token);

            var titleProperty = await GetTitlePropertyNameAsync(
                http,
                dataSourceId,
                cancellationToken);

            var payload = new Dictionary<string, object?>
            {
                ["properties"] = new Dictionary<string, object?>
                {
                    [titleProperty] = new Dictionary<string, object?>
                    {
                        ["type"] = "title",
                        ["title"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = new Dictionary<string, object?>
                                {
                                    ["content"] = newTitle
                                }
                            }
                        }
                    }
                }
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"pages/{NormalizeId(pageId)}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };

            using var response = await http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw CreateNotionException("renombrar la página", response, json);

            return titleProperty;
        }

        public async Task MovePageToTrashAsync(
            string token,
            string pageId,
            CancellationToken cancellationToken = default)
        {
            ValidateTokenAndPage(token, pageId);

            using var http = CreateClient(token);

            var payload = new Dictionary<string, object?>
            {
                ["in_trash"] = true
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"pages/{NormalizeId(pageId)}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };

            using var response = await http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw CreateNotionException(
                    "mover la página a la papelera",
                    response,
                    json);
        }

        private static async Task<string> GetTitlePropertyNameAsync(
            HttpClient http,
            string dataSourceId,
            CancellationToken cancellationToken)
        {
            using var response = await http.GetAsync(
                $"data_sources/{NormalizeId(dataSourceId)}",
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw CreateNotionException(
                    "consultar el esquema de la base",
                    response,
                    json);

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty(
                    "properties",
                    out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Notion no devolvió las propiedades de la base.");
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    string.Equals(
                        type.GetString(),
                        "title",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return property.Name;
                }
            }

            throw new InvalidOperationException(
                "No se encontró una propiedad de tipo título en esta base.");
        }

        private static HttpClient CreateClient(string token)
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(NotionBaseUrl),
                Timeout = TimeSpan.FromSeconds(45)
            };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Trim());

            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "Notion-Version",
                NotionVersion);

            return http;
        }

        private static void ValidateTokenAndPage(string token, string pageId)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "No hay un token de Notion configurado.");

            if (string.IsNullOrWhiteSpace(pageId))
                throw new ArgumentException(
                    "La página de Notion no tiene identificador.");
        }

        private static string NormalizeId(string value)
            => (value ?? string.Empty).Trim();

        private static InvalidOperationException CreateNotionException(
            string operation,
            HttpResponseMessage response,
            string body)
        {
            var detail = body;

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                var code = ReadString(root, "code");
                var message = ReadString(root, "message");

                detail = string.IsNullOrWhiteSpace(code)
                    ? message
                    : $"{code}: {message}";
            }
            catch
            {
                // Conserva el cuerpo original si no es JSON.
            }

            return new InvalidOperationException(
                $"Notion no pudo {operation} " +
                $"(HTTP {(int)response.StatusCode}): {detail}");
        }

        private static string ReadString(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString() ?? string.Empty;
        }
    }
}
