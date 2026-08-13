using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Storage;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Services
{
    public sealed class WhatsAppBridgeService
    {
        public const string DefaultBridgeUrl =
            "https://anfeta-whatsapp-bridge.onrender.com";

        private const string CredentialResource =
            "ANFETA.WhatsAppBridge";
        private const string CredentialUser =
            "api-key";

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(85)
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private string _baseUrl = string.Empty;
        private string _apiKey = string.Empty;

        public WhatsAppBridgeService()
        {
            ReloadConfiguration();
        }

        public string BaseUrl => _baseUrl;
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_baseUrl) &&
            !string.IsNullOrWhiteSpace(_apiKey);

        public void ReloadConfiguration()
        {
            _baseUrl = NormalizeBaseUrl(
                ApplicationData.Current.LocalSettings.Values[
                    LS_WhatsAppBridgeUrl] as string ??
                DefaultBridgeUrl);

            _apiKey = LoadSavedApiKey();
        }

        public static string GetSavedBridgeUrl()
        {
            return NormalizeBaseUrl(
                ApplicationData.Current.LocalSettings.Values[
                    LS_WhatsAppBridgeUrl] as string ??
                DefaultBridgeUrl);
        }

        public static string GetSavedApiKey()
        {
            return LoadSavedApiKey();
        }

        public static string GetSavedSeedParticipant()
        {
            return (
                ApplicationData.Current.LocalSettings.Values[
                    LS_WhatsAppSeedParticipant] as string ??
                string.Empty)
                .Trim();
        }

        public static void SaveConfiguration(
            string baseUrl,
            string apiKey,
            string seedParticipant)
        {
            var normalizedUrl = NormalizeBaseUrl(baseUrl);

            if (!Uri.TryCreate(
                    normalizedUrl,
                    UriKind.Absolute,
                    out var uri) ||
                !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "La URL del WhatsApp Bridge debe ser HTTPS y válida.");
            }

            apiKey = (apiKey ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "Agrega la API key del WhatsApp Bridge.");
            }

            ApplicationData.Current.LocalSettings.Values[
                LS_WhatsAppBridgeUrl] = normalizedUrl;

            ApplicationData.Current.LocalSettings.Values[
                LS_WhatsAppSeedParticipant] =
                (seedParticipant ?? string.Empty).Trim();

            SaveApiKey(apiKey);
        }

        public async Task<WhatsAppBridgeHealth> GetHealthAsync(
            CancellationToken cancellationToken = default)
        {
            using var request =
                BuildRequest(HttpMethod.Get, "/health", protectedRoute: false);

            return await SendJsonAsync<WhatsAppBridgeHealth>(
                request,
                cancellationToken);
        }

        public async Task<WhatsAppBridgeStatus> GetStatusAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using var request =
                BuildRequest(HttpMethod.Get, "/api/session/status");

            return await SendJsonAsync<WhatsAppBridgeStatus>(
                request,
                cancellationToken);
        }

        public async Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using var request =
                BuildRequest(HttpMethod.Post, "/api/session/start");

            using var response =
                await Client.SendAsync(request, cancellationToken);

            await EnsureSuccessAsync(response, cancellationToken);
        }

        public async Task<byte[]> GetQrImageAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using var request =
                BuildRequest(HttpMethod.Get, "/api/session/qr-image");

            using var response =
                await Client.SendAsync(request, cancellationToken);

            await EnsureSuccessAsync(response, cancellationToken);

            return await response.Content.ReadAsByteArrayAsync(
                cancellationToken);
        }

        public async Task<WhatsAppPersistenceStatus>
            GetPersistenceStatusAsync(
                CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using var request =
                BuildRequest(HttpMethod.Get, "/api/persistence/status");

            return await SendJsonAsync<WhatsAppPersistenceStatus>(
                request,
                cancellationToken);
        }

        public async Task UnlinkAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using var request =
                BuildRequest(HttpMethod.Post, "/api/session/unlink");

            using var response =
                await Client.SendAsync(request, cancellationToken);

            await EnsureSuccessAsync(response, cancellationToken);
        }

        public async Task<WhatsAppGroupResolveResult> ResolveGroupAsync(
            string domain,
            string projectType,
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            var path =
                "/api/groups/resolve?domain=" +
                Uri.EscapeDataString((domain ?? string.Empty).Trim()) +
                "&projectType=" +
                Uri.EscapeDataString((projectType ?? string.Empty).Trim());

            using var request =
                BuildRequest(HttpMethod.Get, path);

            using var response =
                await Client.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new WhatsAppGroupResolveResult
                {
                    Ok = true,
                    Exists = false
                };
            }

            await EnsureSuccessAsync(response, cancellationToken);

            var raw =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            return JsonSerializer.Deserialize<WhatsAppGroupResolveResult>(
                       raw,
                       JsonOptions) ??
                   new WhatsAppGroupResolveResult();
        }

        public async Task<WhatsAppGroupCreateResult> CreateGroupAsync(
            string name,
            string domain,
            string projectType,
            string seedParticipant,
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            var body = JsonSerializer.Serialize(
                new
                {
                    name = (name ?? string.Empty).Trim(),
                    domain = (domain ?? string.Empty).Trim(),
                    projectType = (projectType ?? string.Empty).Trim(),
                    seedParticipant =
                        (seedParticipant ?? string.Empty).Trim()
                });

            using var request =
                BuildRequest(HttpMethod.Post, "/api/groups/create");

            request.Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json");

            return await SendJsonAsync<WhatsAppGroupCreateResult>(
                request,
                cancellationToken);
        }

        public async Task SendMessageAsync(
            string groupId,
            string text,
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            var path =
                "/api/groups/" +
                Uri.EscapeDataString((groupId ?? string.Empty).Trim()) +
                "/send";

            var body = JsonSerializer.Serialize(
                new
                {
                    text = text ?? string.Empty
                });

            using var request =
                BuildRequest(HttpMethod.Post, path);

            request.Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json");

            using var response =
                await Client.SendAsync(request, cancellationToken);

            await EnsureSuccessAsync(response, cancellationToken);
        }

        private HttpRequestMessage BuildRequest(
            HttpMethod method,
            string path,
            bool protectedRoute = true)
        {
            var request = new HttpRequestMessage(
                method,
                BuildUri(path));

            if (protectedRoute &&
                !string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.TryAddWithoutValidation(
                    "x-api-key",
                    _apiKey);
            }

            return request;
        }

        private Uri BuildUri(string path)
        {
            var baseUrl = NormalizeBaseUrl(_baseUrl);

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = DefaultBridgeUrl;
            }

            return new Uri(
                baseUrl + "/" +
                (path ?? string.Empty).TrimStart('/'));
        }

        private void EnsureConfigured()
        {
            if (!IsConfigured)
            {
                throw new InvalidOperationException(
                    "Configura primero la URL y API key del WhatsApp Bridge en Configuración.");
            }
        }

        private static async Task<T> SendJsonAsync<T>(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            where T : new()
        {
            using var response =
                await Client.SendAsync(request, cancellationToken);

            await EnsureSuccessAsync(response, cancellationToken);

            var raw =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(raw))
                return new T();

            return JsonSerializer.Deserialize<T>(
                       raw,
                       JsonOptions) ??
                   new T();
        }

        private static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;

            var raw =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            var message = string.Empty;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);

                    if (doc.RootElement.TryGetProperty(
                            "error",
                            out var error))
                    {
                        message = error.GetString() ?? string.Empty;
                    }
                    else if (doc.RootElement.TryGetProperty(
                                 "message",
                                 out var jsonMessage))
                    {
                        message = jsonMessage.GetString() ?? string.Empty;
                    }
                }
                catch
                {
                    message = raw.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                message =
                    $"WhatsApp Bridge respondió HTTP {(int)response.StatusCode}.";
            }

            throw new HttpRequestException(
                message,
                null,
                response.StatusCode);
        }

        private static string NormalizeBaseUrl(string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .TrimEnd('/');
        }

        private static string LoadSavedApiKey()
        {
            try
            {
                var vault = new PasswordVault();
                var credential =
                    vault.Retrieve(
                        CredentialResource,
                        CredentialUser);

                credential.RetrievePassword();
                return credential.Password ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void SaveApiKey(string apiKey)
        {
            var vault = new PasswordVault();

            try
            {
                foreach (var credential in
                         vault.FindAllByResource(
                             CredentialResource))
                {
                    vault.Remove(credential);
                }
            }
            catch
            {
            }

            vault.Add(
                new PasswordCredential(
                    CredentialResource,
                    CredentialUser,
                    apiKey));
        }
    }

    public sealed class WhatsAppBridgeHealth
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("persistence")]
        public string Persistence { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
    }

    public sealed class WhatsAppBridgeStatus
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        [JsonPropertyName("session")]
        public string Session { get; set; } = string.Empty;

        [JsonPropertyName("persistence")]
        public string Persistence { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("connected")]
        public bool Connected { get; set; }

        [JsonPropertyName("qrAvailable")]
        public bool QrAvailable { get; set; }

        [JsonPropertyName("connectedAt")]
        public DateTimeOffset? ConnectedAt { get; set; }

        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }
    }

    public sealed class WhatsAppPersistenceStatus
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("session")]
        public string Session { get; set; } = string.Empty;

        [JsonPropertyName("configured")]
        public bool Configured { get; set; }

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        [JsonPropertyName("reachable")]
        public bool Reachable { get; set; }

        [JsonPropertyName("sessionStored")]
        public bool SessionStored { get; set; }

        [JsonPropertyName("groupsStored")]
        public int GroupsStored { get; set; }
    }

    public sealed class WhatsAppGroupInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("projectType")]
        public string ProjectType { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("groupId")]
        public string GroupId { get; set; } = string.Empty;

        [JsonPropertyName("inviteLink")]
        public string InviteLink { get; set; } = string.Empty;

        [JsonPropertyName("initialParticipants")]
        public List<string> InitialParticipants { get; set; } = new();
    }

    public sealed class WhatsAppGroupResolveResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("exists")]
        public bool Exists { get; set; }

        [JsonPropertyName("group")]
        public WhatsAppGroupInfo? Group { get; set; }
    }

    public sealed class WhatsAppGroupCreateResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("created")]
        public bool Created { get; set; }

        [JsonPropertyName("group")]
        public WhatsAppGroupInfo? Group { get; set; }
    }
}
