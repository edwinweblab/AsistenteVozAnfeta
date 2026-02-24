using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
    public sealed class SharedAuthStateService : INotifyPropertyChanged
    {
        private readonly SharedTokenStore _store;
        private readonly WeblabSharedAuthClient _client;
        private bool _isAuthenticated;
        private string? _token;
        private string? _refreshToken;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public event PropertyChangedEventHandler? PropertyChanged;

        public SharedAuthStateService(SharedTokenStore store, WeblabSharedAuthClient client)
        {
            _store = store;
            _client = client;
        }

        public bool IsAuthenticated
        {
            get => _isAuthenticated;
            private set
            {
                if (_isAuthenticated == value) return;
                _isAuthenticated = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLinked));
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public bool IsLinked => IsAuthenticated;
        public string DisplayText => IsLinked ? "Anfeta" : "Shared";
        public string? Token => _token;
        public string? RefreshToken => _refreshToken;

        /// Carga los tokens guardados y valida/renueva automáticamente al arrancar.
        /// Si el token está vencido pero el refreshToken es válido, renueva en silencio.
        /// Si ambos son inválidos, cierra sesión y fuerza re-login.
        public async Task InitializeAsync()
        {
            var saved = await _store.GetTokenAsync();
            var savedRefresh = await _store.GetRefreshTokenAsync();

            if (string.IsNullOrWhiteSpace(saved) || string.IsNullOrWhiteSpace(savedRefresh))
            {
                SetSignedOutInternal();
                Debug.WriteLine("[SharedAuth] InitializeAsync: sin tokens guardados");
                return;
            }

            // Cargar en memoria
            _token = saved.Trim();
            _refreshToken = savedRefresh.Trim();
            IsAuthenticated = true;
            OnPropertyChanged(nameof(Token));
            OnPropertyChanged(nameof(RefreshToken));

            Debug.WriteLine("[SharedAuth] Tokens cargados → intentando refresh automático...");

            // Intentar refresh inmediatamente para validar y renovar
            var ok = await TryRefreshAsync();
            if (!ok)
            {
                Debug.WriteLine("[SharedAuth] Refresh fallido en startup → sesión cerrada");
                // SetSignedOutInternal ya fue llamado dentro de TryRefreshAsync
            }
            else
            {
                Debug.WriteLine("[SharedAuth] Refresh startup OK → sesión válida");
            }
        }

        /// Intenta renovar el token usando el refreshToken guardado.
        /// Retorna true si se renovó correctamente, false si falló (sesión cerrada).
        public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                if (string.IsNullOrWhiteSpace(_refreshToken))
                {
                    Debug.WriteLine("[SharedAuth] TryRefreshAsync: sin refreshToken");
                    SetSignedOutInternal();
                    await _store.ClearAsync();
                    return false;
                }

                Debug.WriteLine("[SharedAuth] Llamando RefreshAsync...");
                var result = await _client.RefreshAsync(_refreshToken!, ct);

                if (!result.Ok || string.IsNullOrWhiteSpace(result.Token) || string.IsNullOrWhiteSpace(result.RefreshToken))
                {
                    Debug.WriteLine($"[SharedAuth] Refresh falló: {result.RawError}");
                    SetSignedOutInternal();
                    await _store.ClearAsync();
                    return false;
                }

                // Actualizar tokens en memoria y persistencia
                _token = result.Token!.Trim();
                _refreshToken = result.RefreshToken!.Trim();
                IsAuthenticated = true;

                OnPropertyChanged(nameof(Token));
                OnPropertyChanged(nameof(RefreshToken));

                await _store.SaveTokenAsync(_token);
                await _store.SaveRefreshTokenAsync(_refreshToken);

                Debug.WriteLine("[SharedAuth] Token renovado correctamente");
                return true;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        public async Task SetSignedInAsync(string token, string refreshToken)
        {
            _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            _refreshToken = string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken.Trim();
            IsAuthenticated = !string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_refreshToken);

            OnPropertyChanged(nameof(Token));
            OnPropertyChanged(nameof(RefreshToken));

            if (IsAuthenticated && _token != null && _refreshToken != null)
            {
                await _store.SaveTokenAsync(_token);
                await _store.SaveRefreshTokenAsync(_refreshToken);
            }
        }

        public async Task SignOutAsync()
        {
            SetSignedOutInternal();
            await _store.MarkManualLogoutAsync();
            await _store.ClearAsync();
        }

        private void SetSignedOutInternal()
        {
            _token = null;
            _refreshToken = null;
            IsAuthenticated = false;
            OnPropertyChanged(nameof(Token));
            OnPropertyChanged(nameof(RefreshToken));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}