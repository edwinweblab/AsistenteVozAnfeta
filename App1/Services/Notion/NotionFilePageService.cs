using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Notion
{
    public sealed record NotionCreatedFilePage(
        string PageId,
        string PageUrl,
        string Title,
        string FileUploadId);

    public sealed record NotionFileUploadProgress(
        int Completed,
        int Total,
        string FileName);

    public sealed class NotionFilePageService
    {
        public const string RevisionesDataSourceId =
            "2eeabd7d-91b7-8193-a131-000b08cd54e2";

        public const string RevisionesTitleProperty =
            "TITULO PRiNCIPAL";

        private const string NotionBaseUrl = "https://api.notion.com/v1/";
        private const string NotionVersion = "2026-03-11";
        private const long MaxSinglePartBytes = 20L * 1024L * 1024L;
        private const int MultiPartChunkBytes = 10 * 1024 * 1024;
        private const long MaxMultiPartBytes = 5L * 1024L * 1024L * 1024L;
        private const int MaxFilesPerPage = 50;

        public async Task<NotionCreatedFilePage>
            CreateRevisionMessageAsync(
                string token,
                string pageTitle,
                IReadOnlyList<string>? localFilePaths = null,
                IProgress<NotionFileUploadProgress>? progress = null,
                CancellationToken cancellationToken = default)
        {
            var paths =
                (localFilePaths ??
                 Array.Empty<string>())
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (paths.Count > 0)
            {
                return await CreateRevisionFromFilesAsync(
                    token,
                    paths,
                    pageTitle,
                    progress,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "No hay un token de Notion configurado.");

            pageTitle =
                (pageTitle ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(pageTitle))
                throw new ArgumentException(
                    "El título de la página está vacío.");

            using var http =
                CreateHttpClient(token);

            return await CreateRevisionPageAsync(
                http,
                Array.Empty<UploadedNotionFile>(),
                pageTitle,
                cancellationToken);
        }

        public Task<NotionCreatedFilePage> CreateRevisionFromFileAsync(
            string token,
            string localFilePath,
            string pageTitle,
            CancellationToken cancellationToken = default)
        {
            return CreateRevisionFromFilesAsync(
                token,
                new[] { localFilePath },
                pageTitle,
                progress: null,
                cancellationToken);
        }

        public async Task<NotionCreatedFilePage> CreateRevisionFromFilesAsync(
            string token,
            IReadOnlyList<string> localFilePaths,
            string pageTitle,
            IProgress<NotionFileUploadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "No hay un token de Notion configurado.");

            pageTitle = (pageTitle ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pageTitle))
                throw new ArgumentException("El título de la página está vacío.");

            var validPaths = (localFilePaths ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validPaths.Count == 0)
                throw new ArgumentException("No se seleccionaron archivos.");

            if (validPaths.Count > MaxFilesPerPage)
            {
                throw new InvalidOperationException(
                    $"Puedes agregar como máximo {MaxFilesPerPage} archivos en una sola página.");
            }

            var files = new List<FileInfo>(validPaths.Count);

            foreach (var path in validPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        "No se encontró uno de los archivos seleccionados.",
                        path);

                var info = new FileInfo(path);

                if (info.Length > MaxMultiPartBytes)
                {
                    throw new InvalidOperationException(
                        $"“{info.Name}” supera el máximo de 5 GB admitido por la carga multiparte de Notion.");
                }

                files.Add(info);
            }

            using var http = CreateHttpClient(token);
            var uploadedFiles = new List<UploadedNotionFile>(files.Count);

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = files[index];
                var contentType = GetContentType(file.Extension);

                progress?.Report(new NotionFileUploadProgress(
                    Completed: index,
                    Total: files.Count,
                    FileName: file.Name));

                var fileUploadId =
                    await UploadFileAsync(
                        http,
                        file,
                        contentType,
                        cancellationToken);

                uploadedFiles.Add(new UploadedNotionFile(
                    fileUploadId,
                    file.Name,
                    file.Extension));

                progress?.Report(new NotionFileUploadProgress(
                    Completed: index + 1,
                    Total: files.Count,
                    FileName: file.Name));
            }

            return await CreateRevisionPageAsync(
                http,
                uploadedFiles,
                pageTitle,
                cancellationToken);
        }

        public sealed record NotionAppendedAttachment(
            string FileUploadId,
            string FileName,
            string BlockType);

        public async Task<IReadOnlyList<NotionAppendedAttachment>>
            AppendFilesToPageAsync(
                string token,
                string pageId,
                IReadOnlyList<string> localFilePaths,
                IProgress<NotionFileUploadProgress>? progress = null,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "No hay un token de Notion configurado.");

            if (string.IsNullOrWhiteSpace(pageId))
                throw new ArgumentException(
                    "La conversación no tiene identificador de Notion.");

            var validPaths = (localFilePaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validPaths.Count == 0)
                return Array.Empty<NotionAppendedAttachment>();

            if (validPaths.Count > MaxFilesPerPage)
            {
                throw new InvalidOperationException(
                    $"Puedes adjuntar como máximo {MaxFilesPerPage} archivos.");
            }

            using var http = CreateHttpClient(token);
            var uploaded = new List<UploadedNotionFile>();

            for (var index = 0; index < validPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = validPaths[index];

                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        "No se encontró uno de los archivos seleccionados.",
                        path);

                var info = new FileInfo(path);

                if (info.Length > MaxMultiPartBytes)
                {
                    throw new InvalidOperationException(
                        $"“{info.Name}” supera el máximo de 5 GB admitido por la carga multiparte de Notion.");
                }

                var contentType = GetContentType(info.Extension);

                progress?.Report(
                    new NotionFileUploadProgress(
                        index,
                        validPaths.Count,
                        info.Name));

                var fileUploadId =
                    await UploadFileAsync(
                        http,
                        info,
                        contentType,
                        cancellationToken);

                uploaded.Add(
                    new UploadedNotionFile(
                        fileUploadId,
                        info.Name,
                        info.Extension));

                progress?.Report(
                    new NotionFileUploadProgress(
                        index + 1,
                        validPaths.Count,
                        info.Name));
            }

            var children = new List<object>();

            foreach (var item in uploaded)
            {
                var blockType = GetBlockType(item.Extension);

                var fileReference =
                    new Dictionary<string, object?>
                    {
                        ["type"] = "file_upload",
                        ["file_upload"] =
                            new Dictionary<string, object?>
                            {
                                ["id"] = item.FileUploadId
                            }
                    };

                children.Add(
                    new Dictionary<string, object?>
                    {
                        ["object"] = "block",
                        ["type"] = blockType,
                        [blockType] = fileReference
                    });
            }

            var payload =
                new Dictionary<string, object?>
                {
                    ["children"] = children
                };

            using var content =
                new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

            using var response =
                await http.PatchAsync(
                    $"blocks/{pageId.Trim()}/children",
                    content,
                    cancellationToken);

            var json =
                await response.Content
                    .ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "adjuntar archivos a la conversación",
                    response,
                    json);
            }

            return uploaded
                .Select(item =>
                    new NotionAppendedAttachment(
                        item.FileUploadId,
                        item.FileName,
                        GetBlockType(item.Extension)))
                .ToList();
        }

        private sealed record UploadedNotionFile(
            string FileUploadId,
            string FileName,
            string Extension);

        private static HttpClient CreateHttpClient(string token)
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(NotionBaseUrl),
                Timeout = TimeSpan.FromMinutes(10)
            };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Trim());

            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "Notion-Version",
                NotionVersion);

            return http;
        }

        private static async Task<string> CreateFileUploadAsync(
            HttpClient http,
            string fileName,
            string contentType,
            CancellationToken cancellationToken,
            bool multiPart = false,
            int numberOfParts = 1)
        {
            var payload = new Dictionary<string, object?>
            {
                ["mode"] =
                    multiPart
                        ? "multi_part"
                        : "single_part",
                ["filename"] = fileName,
                ["content_type"] = contentType
            };

            if (multiPart)
                payload["number_of_parts"] = numberOfParts;

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await http.PostAsync(
                "file_uploads",
                content,
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw CreateNotionException("iniciar la carga", response, json);

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                throw new InvalidOperationException(
                    "Notion no devolvió el identificador de carga.");
            }

            return idElement.GetString()!;
        }

        private static async Task<string>
            UploadFileAsync(
                HttpClient http,
                FileInfo file,
                string contentType,
                CancellationToken cancellationToken)
        {
            if (file.Length <= MaxSinglePartBytes)
            {
                var id =
                    await CreateFileUploadAsync(
                        http,
                        file.Name,
                        contentType,
                        cancellationToken);

                await SendFileBytesAsync(
                    http,
                    id,
                    file.FullName,
                    file.Name,
                    contentType,
                    cancellationToken);

                return id;
            }

            var numberOfParts =
                (int)Math.Ceiling(
                    file.Length /
                    (double)MultiPartChunkBytes);

            var uploadId =
                await CreateFileUploadAsync(
                    http,
                    file.Name,
                    contentType,
                    cancellationToken,
                    multiPart: true,
                    numberOfParts:
                        numberOfParts);

            await using var stream =
                new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    useAsync: true);

            var buffer =
                new byte[MultiPartChunkBytes];

            for (var part = 1;
                 part <= numberOfParts;
                 part++)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                var totalRead = 0;

                while (totalRead < buffer.Length)
                {
                    var read =
                        await stream.ReadAsync(
                            buffer.AsMemory(
                                totalRead,
                                buffer.Length -
                                totalRead),
                            cancellationToken);

                    if (read == 0)
                        break;

                    totalRead += read;
                }

                using var form =
                    new MultipartFormDataContent();

                using var partContent =
                    new ByteArrayContent(
                        buffer,
                        0,
                        totalRead);

                partContent.Headers.ContentType =
                    new MediaTypeHeaderValue(
                        contentType);

                form.Add(
                    partContent,
                    "file",
                    file.Name);

                form.Add(
                    new StringContent(
                        part.ToString(
                            System.Globalization
                                .CultureInfo
                                .InvariantCulture)),
                    "part_number");

                using var response =
                    await http.PostAsync(
                        $"file_uploads/{uploadId}/send",
                        form,
                        cancellationToken);

                var json =
                    await response.Content
                        .ReadAsStringAsync(
                            cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateNotionException(
                        $"enviar la parte {part} de {numberOfParts}",
                        response,
                        json);
                }
            }

            using var completeContent =
                new StringContent(
                    "{}",
                    Encoding.UTF8,
                    "application/json");

            using var completeResponse =
                await http.PostAsync(
                    $"file_uploads/{uploadId}/complete",
                    completeContent,
                    cancellationToken);

            var completeJson =
                await completeResponse.Content
                    .ReadAsStringAsync(
                        cancellationToken);

            if (!completeResponse.IsSuccessStatusCode)
            {
                throw CreateNotionException(
                    "completar la carga multiparte",
                    completeResponse,
                    completeJson);
            }

            return uploadId;
        }

        private static async Task SendFileBytesAsync(
            HttpClient http,
            string fileUploadId,
            string localFilePath,
            string fileName,
            string contentType,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                localFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            using var form = new MultipartFormDataContent();
            using var fileContent = new StreamContent(stream);

            fileContent.Headers.ContentType =
                new MediaTypeHeaderValue(contentType);

            form.Add(fileContent, "file", fileName);

            using var response = await http.PostAsync(
                $"file_uploads/{fileUploadId}/send",
                form,
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw CreateNotionException("enviar el archivo", response, json);
        }

        private static async Task<NotionCreatedFilePage> CreateRevisionPageAsync(
            HttpClient http,
            IReadOnlyList<UploadedNotionFile> uploadedFiles,
            string pageTitle,
            CancellationToken cancellationToken)
        {
            var children = new List<object>();

            foreach (var uploaded in uploadedFiles)
            {
                var blockType = GetBlockType(uploaded.Extension);

                var fileReference = new Dictionary<string, object?>
                {
                    ["type"] = "file_upload",
                    ["file_upload"] = new Dictionary<string, object?>
                    {
                        ["id"] = uploaded.FileUploadId
                    }
                };

                children.Add(new Dictionary<string, object?>
                {
                    ["object"] = "block",
                    ["type"] = blockType,
                    [blockType] = fileReference
                });

                children.Add(new Dictionary<string, object?>
                {
                    ["object"] = "block",
                    ["type"] = "paragraph",
                    ["paragraph"] = new Dictionary<string, object?>
                    {
                        ["rich_text"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = new Dictionary<string, object?>
                                {
                                    ["content"] = $"Archivo original: {uploaded.FileName}"
                                }
                            }
                        }
                    }
                });
            }

            var payload = new Dictionary<string, object?>
            {
                ["parent"] = new Dictionary<string, object?>
                {
                    ["type"] = "data_source_id",
                    ["data_source_id"] = RevisionesDataSourceId
                },
                ["properties"] = new Dictionary<string, object?>
                {
                    [RevisionesTitleProperty] = new Dictionary<string, object?>
                    {
                        ["type"] = "title",
                        ["title"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = new Dictionary<string, object?>
                                {
                                    ["content"] = pageTitle
                                }
                            }
                        }
                    }
                },
                ["children"] = children
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await http.PostAsync(
                "pages",
                content,
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw CreateNotionException(
                    "crear la página en Revisiones",
                    response,
                    json);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var pageId = ReadString(root, "id");
            var pageUrl = ReadString(root, "url");

            if (string.IsNullOrWhiteSpace(pageId))
                throw new InvalidOperationException(
                    "Notion creó la página, pero no devolvió su identificador.");

            return new NotionCreatedFilePage(
                PageId: pageId,
                PageUrl: pageUrl,
                Title: pageTitle,
                FileUploadId:
                    uploadedFiles.FirstOrDefault()?
                        .FileUploadId ??
                    string.Empty);
        }

        private static InvalidOperationException CreateNotionException(
            string operation,
            HttpResponseMessage response,
            string responseBody)
        {
            var detail = responseBody;

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;

                var message = ReadString(root, "message");
                var code = ReadString(root, "code");

                detail = string.IsNullOrWhiteSpace(code)
                    ? message
                    : $"{code}: {message}";
            }
            catch
            {
                // Conserva el cuerpo original si no es JSON.
            }

            return new InvalidOperationException(
                $"Notion no pudo {operation} (HTTP {(int)response.StatusCode}): {detail}");
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString() ?? string.Empty;
        }

        private static string GetBlockType(string extension)
        {
            var ext = (extension ?? string.Empty).ToLowerInvariant();

            return ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg"
                    => "image",

                ".pdf"
                    => "pdf",

                ".mp3" or ".wav" or ".m4a" or ".ogg"
                    => "audio",

                ".mp4" or ".mov" or ".avi" or ".webm" or ".mkv"
                    => "video",

                _ => "file"
            };
        }

        private static string GetContentType(string extension)
        {
            return (extension ?? string.Empty).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".zip" => "application/zip",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".m4a" => "audio/mp4",
                ".ogg" => "audio/ogg",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                ".webm" => "video/webm",
                _ => "application/octet-stream"
            };
        }
    }
}
