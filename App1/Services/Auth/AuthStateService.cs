using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Auth
{
    public sealed class AuthStateService : INotifyPropertyChanged
    {
        private readonly ITokenStore _tokenStore;

        private bool _isAuthenticated;
        private string? _token;

        public event PropertyChangedEventHandler? PropertyChanged;

        public AuthStateService(ITokenStore tokenStore)
        {
            _tokenStore = tokenStore;
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

        public string DisplayText => IsLinked ? "Conectado" : "Notion";

        public string? Token => _token;

        public async Task InitializeAsync()
        {
            var saved = await _tokenStore.GetTokenAsync();
            if (!string.IsNullOrWhiteSpace(saved))
            {
                _token = saved.Trim();
                IsAuthenticated = true;
                OnPropertyChanged(nameof(Token));
            }
            else
            {
                SetSignedOutInternal();
            }
        }

        public async Task SetSignedInAsync(string token)
        {
            _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            IsAuthenticated = !string.IsNullOrWhiteSpace(_token);

            OnPropertyChanged(nameof(Token));

            if (IsAuthenticated && _token != null)
                await _tokenStore.SaveTokenAsync(_token);
        }

        public async Task SignOutAsync()
        {
            SetSignedOutInternal();
            await _tokenStore.ClearAsync();
        }

        private void SetSignedOutInternal()
        {
            _token = null;
            IsAuthenticated = false;
            OnPropertyChanged(nameof(Token));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
