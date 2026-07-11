using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Security.Credentials;

namespace Anfeta.UI.Services.Dropbox
{
    public sealed class DropboxCredentialStore
    {
        private const string VaultResource = "ANFETA.Dropbox";
        private const string RefreshTokenUser = "refresh_token";

        public Task SaveRefreshTokenAsync(string refreshToken)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new ArgumentException("El refresh token de Dropbox está vacío.", nameof(refreshToken));

            var vault = new PasswordVault();

            // PasswordVault lanza "Cannot find credential in Vault"
            // cuando no existen credenciales para ese recurso.
            // Por eso primero se obtiene la lista de forma segura.
            foreach (var existing in FindAllSafe(vault))
            {
                if (string.Equals(existing.UserName, RefreshTokenUser, StringComparison.Ordinal))
                    vault.Remove(existing);
            }

            vault.Add(new PasswordCredential(
                VaultResource,
                RefreshTokenUser,
                refreshToken.Trim()));

            return Task.CompletedTask;
        }

        public Task<string?> GetRefreshTokenAsync()
        {
            try
            {
                var vault = new PasswordVault();

                var credential = FindAllSafe(vault)
                    .FirstOrDefault(x => string.Equals(
                        x.UserName,
                        RefreshTokenUser,
                        StringComparison.Ordinal));

                if (credential == null)
                    return Task.FromResult<string?>(null);

                credential.RetrievePassword();
                return Task.FromResult<string?>(credential.Password);
            }
            catch
            {
                return Task.FromResult<string?>(null);
            }
        }

        public Task ClearAsync()
        {
            var vault = new PasswordVault();

            foreach (var credential in FindAllSafe(vault))
            {
                try
                {
                    vault.Remove(credential);
                }
                catch
                {
                    // Si otra operación la quitó antes, se ignora.
                }
            }

            return Task.CompletedTask;
        }

        private static IReadOnlyList<PasswordCredential> FindAllSafe(PasswordVault vault)
        {
            try
            {
                return vault.FindAllByResource(VaultResource).ToList();
            }
            catch
            {
                return Array.Empty<PasswordCredential>();
            }
        }
    }
}