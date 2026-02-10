using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
    public sealed class SharedAuthStateService : INotifyPropertyChanged
    {
        private readonly SharedTokenStore _store;

        private bool _isAuthenticated;
        private string? _token;
        private string? _refreshToken;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SharedAuthStateService(SharedTokenStore store)
        {
            _store = store;
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

        // Texto que verás en UI si quieres (puedes cambiarlo luego)
        public string DisplayText => IsLinked ? "Anfeta" : "Shared";

        public string? Token => _token;
        public string? RefreshToken => _refreshToken;

        public async Task InitializeAsync()
        {
            var saved = await _store.GetTokenAsync();
            var savedRefresh = await _store.GetRefreshTokenAsync();

            if (!string.IsNullOrWhiteSpace(saved) && !string.IsNullOrWhiteSpace(savedRefresh))
            {
                _token = saved.Trim();
                _refreshToken = savedRefresh.Trim();
                IsAuthenticated = true;
                OnPropertyChanged(nameof(Token));
                OnPropertyChanged(nameof(RefreshToken));
            }
            else
            {
                SetSignedOutInternal();
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
