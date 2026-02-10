using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Auth
{
    // Implementa ITokenStore para que NO truene tu interfaz.
    // Además, incluye helpers extra para refreshToken.
    public sealed class SharedTokenStore : ITokenStore
    {
        private const string KeyAccess = "shared_access_token";
        private const string KeyRefresh = "shared_refresh_token";
        private const string KeyManualLogout = "shared_manual_logout";

        private readonly ApplicationDataContainer _settings;

        public SharedTokenStore()
        {
            _settings = ApplicationData.Current.LocalSettings;
        }

        public Task<string?> GetTokenAsync()
        {
            _settings.Values.TryGetValue(KeyAccess, out var v);
            return Task.FromResult(v as string);
        }

        public Task SaveTokenAsync(string token)
        {
            _settings.Values[KeyAccess] = token;
            _settings.Values[KeyManualLogout] = false;

            Debug.WriteLine($"[SHARED] ACCESS TOKEN GUARDADO: {token}");
            return Task.CompletedTask;
        }


        public Task ClearAsync()
        {
            _settings.Values.Remove(KeyAccess);
            _settings.Values.Remove(KeyRefresh);
            return Task.CompletedTask;
        }

        public Task<bool> WasManualLogoutAsync()
        {
            if (_settings.Values.TryGetValue(KeyManualLogout, out var v) && v is bool b)
                return Task.FromResult(b);

            return Task.FromResult(false);
        }

        public Task ClearManualLogoutFlagAsync()
        {
            _settings.Values[KeyManualLogout] = false;
            return Task.CompletedTask;
        }

        public Task MarkManualLogoutAsync()
        {
            _settings.Values[KeyManualLogout] = true;
            return Task.CompletedTask;
        }

        // Extras para shared
        public Task<string?> GetRefreshTokenAsync()
        {
            _settings.Values.TryGetValue(KeyRefresh, out var v);
            return Task.FromResult(v as string);
        }

        public Task SaveRefreshTokenAsync(string refreshToken)
        {
            _settings.Values[KeyRefresh] = refreshToken;

            Debug.WriteLine($"[SHARED] REFRESH TOKEN GUARDADO: {refreshToken}");
            return Task.CompletedTask;
        }

    }
}
