using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
    public interface ITokenStore
    {
        Task<string?> GetTokenAsync();
        Task SaveTokenAsync(string token);
        Task ClearAsync();
        Task<bool> WasManualLogoutAsync();
        Task ClearManualLogoutFlagAsync();
    }
}