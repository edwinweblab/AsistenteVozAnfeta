using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Auth
{
    public sealed class LocalTokenStore : ITokenStore
    {
        private const string TokenKey = "auth_token";
        private const string ManualLogoutKey = "manual_logout_flag";

        public Task<string?> GetTokenAsync()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(TokenKey, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                return Task.FromResult<string?>(s);
            return Task.FromResult<string?>(null);
        }

        public Task SaveTokenAsync(string token)
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[TokenKey] = token;
            // Al guardar un token, limpiar el flag de logout manual
            settings.Values.Remove(ManualLogoutKey);
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values.Remove(TokenKey);
            // Marcar que el usuario cerró sesión manualmente
            settings.Values[ManualLogoutKey] = true;
            return Task.CompletedTask;
        }

        // Verifica si el usuario cerró sesión manualmente
        // Retorna true si el usuario hizo logout manual
        public Task<bool> WasManualLogoutAsync()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(ManualLogoutKey, out var v) && v is bool b)
                return Task.FromResult(b);
            return Task.FromResult(false);
        }

        // Limpia el flag de logout manual (para permitir auto-login de nuevo)
        public Task ClearManualLogoutFlagAsync()
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values.Remove(ManualLogoutKey);
            return Task.CompletedTask;
        }
    }
}