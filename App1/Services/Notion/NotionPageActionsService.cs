using System;
using System.Collections.Concurrent;
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
    public sealed class NotionDuplicatePageResult
    {
        public string PageId { get; init; } = string.Empty;
        public string PageUrl { get; init; } = string.Empty;
        public IReadOnlyList<string> SkippedProperties { get; init; } =
            Array.Empty<string>();
    }

    public sealed class NotionPageActionsService
    {
        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";
        private static readonly ConcurrentDictionary<string, string> TitlePropertyCache = new(StringComparer.OrdinalIgnoreCase);

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

            var payloadJson =
                JsonSerializer.Serialize(payload);

            using var response =
                await NotionRequestCoordinator.SendAsync(
                    http,
                    () => new HttpRequestMessage(
                        HttpMethod.Patch,
                        $"pages/{NormalizeId(pageId)}")
                    {
                        Content = new StringContent(
                            payloadJson,
                            Encoding.UTF8,
                            "application/json")
                    },
                    cancellationToken);
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

            var payloadJson =
                JsonSerializer.Serialize(payload);

            using var response =
                await NotionRequestCoordinator.SendAsync(
                    http,
                    () => new HttpRequestMessage(
                        HttpMethod.Patch,
                        $"pages/{NormalizeId(pageId)}")
                    {
                        Content = new StringContent(
                            payloadJson,
                            Encoding.UTF8,
                            "application/json")
                    },
                    cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Borrado idempotente: si ANFETA conserva una fila local pero
                // la página ya fue eliminada/archivada en Notion, para el usuario
                // el objetivo ya está cumplido. No se debe bloquear la limpieza
                // local con "object_not_found".
                if (IsMissingPageResponse(response, json))
                    return;

                throw CreateNotionException(
                    "mover la página a la papelera",
                    response,
                    json);
            }
        }

        public static bool IsMissingPageError(Exception? exception)
        {
            if (exception == null)
                return false;

            var text = exception.ToString();

            return text.Contains(
                       "HTTP 404",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Contains(
                       "object_not_found",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Contains(
                       "could not find",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Contains(
                       "no se encuentra",
                       StringComparison.OrdinalIgnoreCase) ||
                   IsAlreadyArchivedText(text);
        }

        private static bool IsMissingPageResponse(
            HttpResponseMessage response,
            string? body)
        {
            if ((int)response.StatusCode == 404)
                return true;

            var text = body ?? string.Empty;

            return text.Contains(
                       "object_not_found",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Contains(
                       "could not find",
                       StringComparison.OrdinalIgnoreCase) ||
                   IsAlreadyArchivedText(text);
        }

        private static bool IsAlreadyArchivedText(
            string? value)
        {
            var text =
                value ??
                string.Empty;

            // Notion responde 400 validation_error cuando ANFETA intenta
            // mandar a papelera una página que YA está archivada:
            // "Can't edit block that is archived. You must unarchive..."
            // Para eliminar/limpiar en ANFETA ese estado ya equivale a éxito.
            return text.Contains(
                       "archived",
                       StringComparison.OrdinalIgnoreCase) &&
                   (text.Contains(
                        "must unarchive",
                        StringComparison.OrdinalIgnoreCase) ||
                    text.Contains(
                        "can't edit block",
                        StringComparison.OrdinalIgnoreCase) ||
                    text.Contains(
                        "cannot edit block",
                        StringComparison.OrdinalIgnoreCase));
        }

        public Task<NotionDuplicatePageResult> DuplicatePageWithoutBodyAsync(
            string token,
            string pageId,
            string dataSourceId,
            CancellationToken cancellationToken = default)
        {
            return DuplicatePageWithoutBodyAsync(
                token,
                pageId,
                dataSourceId,
                duplicateTitle: null,
                cancellationToken);
        }

        public async Task<NotionDuplicatePageResult> DuplicatePageWithoutBodyAsync(
            string token,
            string pageId,
            string dataSourceId,
            string? duplicateTitle,
            CancellationToken cancellationToken = default)
        {
            ValidateTokenAndPage(token, pageId);

            if (string.IsNullOrWhiteSpace(dataSourceId))
            {
                throw new ArgumentException(
                    "No se pudo identificar la base de Notion.");
            }

            using var http = CreateClient(token);

            using var sourceResponse =
                await NotionRequestCoordinator.SendAsync(
                    http,
                    () => new HttpRequestMessage(
                        HttpMethod.Get,
                        $"pages/{NormalizeId(pageId)}"),
                    cancellationToken);

            var sourceJson =
                await sourceResponse.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!sourceResponse.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "leer la actividad que se va a duplicar",
                    sourceResponse,
                    sourceJson);
            }

            using var sourceDocument =
                JsonDocument.Parse(sourceJson);

            if (!sourceDocument.RootElement.TryGetProperty(
                    "properties",
                    out var sourceProperties) ||
                sourceProperties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Notion no devolvió las propiedades de la actividad.");
            }

            var duplicateProperties =
                new Dictionary<string, object?>(
                    StringComparer.OrdinalIgnoreCase);

            var skippedProperties =
                new List<string>();

            var titlePropertyName = string.Empty;

            foreach (var property in
                     sourceProperties.EnumerateObject())
            {
                if (string.Equals(
                        ReadString(property.Value, "type"),
                        "title",
                        StringComparison.OrdinalIgnoreCase))
                {
                    titlePropertyName = property.Name;
                }

                if (TryBuildWritablePropertyValue(
                        property.Value,
                        out var writableValue))
                {
                    duplicateProperties[property.Name] =
                        writableValue;
                }
                else
                {
                    skippedProperties.Add(property.Name);
                }
            }

            duplicateTitle =
                (duplicateTitle ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(duplicateTitle))
            {
                if (string.IsNullOrWhiteSpace(titlePropertyName))
                {
                    throw new InvalidOperationException(
                        "No se pudo identificar la propiedad de título de la actividad.");
                }

                duplicateProperties[titlePropertyName] =
                    new Dictionary<string, object?>
                    {
                        ["title"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] =
                                    new Dictionary<string, object?>
                                    {
                                        ["content"] = duplicateTitle
                                    }
                            }
                        }
                    };
            }

            if (duplicateProperties.Count == 0)
            {
                throw new InvalidOperationException(
                    "No se encontraron propiedades editables para duplicar.");
            }

            // No se envía `children`: la página nueva conserva las propiedades
            // de la actividad, pero el body/instrucciones queda vacío.
            var payload =
                new Dictionary<string, object?>
                {
                    ["parent"] =
                        new Dictionary<string, object?>
                        {
                            ["type"] = "data_source_id",
                            ["data_source_id"] =
                                NormalizeId(dataSourceId)
                        },
                    ["properties"] = duplicateProperties
                };

            var payloadJson =
                JsonSerializer.Serialize(payload);

            using var createResponse =
                await NotionRequestCoordinator.SendAsync(
                    http,
                    () => new HttpRequestMessage(
                        HttpMethod.Post,
                        "pages")
                    {
                        Content = new StringContent(
                            payloadJson,
                            Encoding.UTF8,
                            "application/json")
                    },
                    cancellationToken);

            var createJson =
                await createResponse.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!createResponse.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "duplicar la actividad",
                    createResponse,
                    createJson);
            }

            using var createdDocument =
                JsonDocument.Parse(createJson);

            return new NotionDuplicatePageResult
            {
                PageId = ReadString(
                    createdDocument.RootElement,
                    "id"),
                PageUrl = ReadString(
                    createdDocument.RootElement,
                    "url"),
                SkippedProperties = skippedProperties
            };
        }

        private static bool TryBuildWritablePropertyValue(
            JsonElement property,
            out object? writableValue)
        {
            writableValue = null;

            var type = ReadString(property, "type");

            if (string.IsNullOrWhiteSpace(type))
                return false;

            switch (type)
            {
                case "title":
                case "rich_text":
                    {
                        if (!property.TryGetProperty(
                                type,
                                out var richText) ||
                            richText.ValueKind != JsonValueKind.Array)
                        {
                            return false;
                        }

                        writableValue =
                            new Dictionary<string, object?>
                            {
                                [type] =
                                    BuildWritableRichTextArray(
                                        richText)
                            };

                        return true;
                    }

                case "number":
                    {
                        if (!property.TryGetProperty(
                                "number",
                                out var number))
                        {
                            return false;
                        }

                        writableValue =
                            new Dictionary<string, object?>
                            {
                                ["number"] =
                                    number.ValueKind ==
                                        JsonValueKind.Null
                                        ? null
                                        : number.TryGetInt64(
                                            out var integer)
                                            ? integer
                                            : number.GetDouble()
                            };

                        return true;
                    }

                case "select":
                case "status":
                    {
                        if (!property.TryGetProperty(
                                type,
                                out var selected))
                        {
                            return false;
                        }

                        object? selectedPayload = null;

                        if (selected.ValueKind ==
                            JsonValueKind.Object)
                        {
                            var name =
                                ReadString(selected, "name");

                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                selectedPayload =
                                    new Dictionary<string, object?>
                                    {
                                        ["name"] = name
                                    };
                            }
                        }

                        writableValue =
                            new Dictionary<string, object?>
                            {
                                [type] = selectedPayload
                            };

                        return true;
                    }

                case "multi_select":
                    {
                        if (!property.TryGetProperty(
                                "multi_select",
                                out var values) ||
                            values.ValueKind != JsonValueKind.Array)
                        {
                            return false;
                        }

                        writableValue =
                            new Dictionary<string, object?>
                            {
                                ["multi_select"] = values
                                    .EnumerateArray()
                                    .Select(item =>
                                        ReadString(item, "name"))
                                    .Where(name =>
                                        !string.IsNullOrWhiteSpace(name))
                                    .Select(name =>
                                        (object)new Dictionary<
                                            string, object?>
                                        {
                                            ["name"] = name
                                        })
                                    .ToArray()
                            };

                        return true;
                    }

                case "date":
                    {
                        if (!property.TryGetProperty(
                                "date",
                                out var date))
                        {
                            return false;
                        }

                        object? datePayload = null;

                        if (date.ValueKind == JsonValueKind.Object)
                        {
                            var start =
                                ReadString(date, "start");

                            if (!string.IsNullOrWhiteSpace(start))
                            {
                                var data =
                                    new Dictionary<string, object?>
                                    {
                                        ["start"] = start
                                    };

                                var end =
                                    ReadString(date, "end");

                                if (!string.IsNullOrWhiteSpace(end))
                                    data["end"] = end;

                                var timeZone =
                                    ReadString(date, "time_zone");

                                if (!string.IsNullOrWhiteSpace(timeZone))
                                    data["time_zone"] = timeZone;

                                datePayload = data;
                            }
                        }

                        writableValue =
                            new Dictionary<string, object?>
                            {
                                ["date"] = datePayload
                            };

                        return true;
                    }

                case "checkbox":
                    {
                        if (!property.TryGetProperty(
                                "checkbox",
                                out var checkbox) ||
                            (checkbox.ValueKind != JsonValueKind.True &&
                             checkbox.ValueKind != JsonValueKind.False))
                        {
                            return false;
                        }

                        writableValue =
                            new Dictionary<string, object?>
                            {
                                ["checkbox"] = checkbox.GetBoolean()
                            };

                        return true;
                    }

                case "url":
                case "email":
                case "phone_number":
                    {
                        if (!property.TryGetProperty(
                                type,
                                out var scalar))
                        {
                            return false;
                        }

                        writableValue =
                            new Dictionary<string, object?>
                            {
                                [type] =
                                    scalar.ValueKind ==
                                        JsonValueKind.String
                                        ? scalar.GetString()
                                        : null
                            };

                        return true;
                    }

                case "people":
                case "relation":
                    {
                        if (!property.TryGetProperty(
                                type,
                                out var values) ||
                            values.ValueKind != JsonValueKind.Array)
                        {
                            return false;
                        }

                        writableValue =
                            new Dictionary<string, object?>
                            {
                                [type] = values
                                    .EnumerateArray()
                                    .Select(item =>
                                        ReadString(item, "id"))
                                    .Where(id =>
                                        !string.IsNullOrWhiteSpace(id))
                                    .Select(id =>
                                        (object)new Dictionary<
                                            string, object?>
                                        {
                                            ["id"] = id
                                        })
                                    .ToArray()
                            };

                        return true;
                    }

                // Estos tipos los administra Notion o requieren un flujo de
                // carga/archivo distinto. Se omiten deliberadamente para no
                // impedir la creación de la copia.
                case "formula":
                case "rollup":
                case "created_time":
                case "created_by":
                case "last_edited_time":
                case "last_edited_by":
                case "unique_id":
                case "verification":
                case "button":
                case "files":
                default:
                    return false;
            }
        }

        private static object[] BuildWritableRichTextArray(
            JsonElement richText)
        {
            var result = new List<object>();

            foreach (var item in richText.EnumerateArray())
            {
                var content = string.Empty;
                string? linkUrl = null;

                if (item.TryGetProperty(
                        "text",
                        out var text) &&
                    text.ValueKind == JsonValueKind.Object)
                {
                    content = ReadString(text, "content");

                    if (text.TryGetProperty(
                            "link",
                            out var link) &&
                        link.ValueKind == JsonValueKind.Object)
                    {
                        linkUrl = ReadString(link, "url");
                    }
                }

                if (string.IsNullOrWhiteSpace(content))
                    content = ReadString(item, "plain_text");

                if (string.IsNullOrEmpty(content))
                    continue;

                var textPayload =
                    new Dictionary<string, object?>
                    {
                        ["content"] = content
                    };

                if (!string.IsNullOrWhiteSpace(linkUrl))
                {
                    textPayload["link"] =
                        new Dictionary<string, object?>
                        {
                            ["url"] = linkUrl
                        };
                }

                result.Add(
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = textPayload
                    });
            }

            return result.ToArray();
        }

        private static async Task<string> GetTitlePropertyNameAsync(
            HttpClient http,
            string dataSourceId,
            CancellationToken cancellationToken)
        {
            var normalizedDataSourceId = NormalizeId(dataSourceId);

            if (TitlePropertyCache.TryGetValue(
                    normalizedDataSourceId,
                    out var cachedTitleProperty))
            {
                return cachedTitleProperty;
            }

            using var response =
                await NotionRequestCoordinator.SendAsync(
                    http,
                    () => new HttpRequestMessage(
                        HttpMethod.Get,
                        $"data_sources/{NormalizeId(dataSourceId)}"),
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
                    TitlePropertyCache[normalizedDataSourceId] =
                        property.Name;

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
