using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Auth
{
    public sealed class LocalTokenStore : ITokenStore
    {
        private const string Key = "auth_token";

        public Task<string?> GetTokenAsync()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(Key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                return Task.FromResult<string?>(s);

            return Task.FromResult<string?>(null);
        }

        public Task SaveTokenAsync(string token)
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[Key] = token;
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values.Remove(Key);
            return Task.CompletedTask;
        }
    }
}
