using Anfeta.UI.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Anfeta.UI.ViewModels
{
    public sealed partial class LinkSharedAccountViewModel : ObservableObject
    {
        private readonly WeblabSharedAuthClient _sharedApi;
        private readonly SharedAuthStateService _sharedState;

        [ObservableProperty]
        private string _user = "";

        [ObservableProperty]
        private string _pass = "";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "";

        // Implementada manualmente para evitar problemas con source generators
        private bool _isStatusError;
        public bool IsStatusError
        {
            get => _isStatusError;
            set => SetProperty(ref _isStatusError, value);
        }

        public bool IsAuthenticated => _sharedState.IsAuthenticated;

        public LinkSharedAccountViewModel(WeblabSharedAuthClient sharedApi, SharedAuthStateService sharedState)
        {
            _sharedApi = sharedApi;
            _sharedState = sharedState;

            _sharedState.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SharedAuthStateService.IsAuthenticated))
                {
                    OnPropertyChanged(nameof(IsAuthenticated));
                    Debug.WriteLine($"[SharedAuth] Estado cambió → IsAuthenticated={IsAuthenticated}");
                }
            };
        }

        public bool CanLogin => !IsBusy
                                && !string.IsNullOrWhiteSpace(User)
                                && !string.IsNullOrWhiteSpace(Pass);

        partial void OnUserChanged(string value) => LoginSharedCommand.NotifyCanExecuteChanged();
        partial void OnPassChanged(string value) => LoginSharedCommand.NotifyCanExecuteChanged();
        partial void OnIsBusyChanged(bool value) => LoginSharedCommand.NotifyCanExecuteChanged();

        /// <summary>
        /// Inicia sesión con credenciales compartidas.
        /// La vista permanece abierta en éxito y en error.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanLogin))]
        private async Task LoginSharedAsync()
        {
            IsBusy = true;
            StatusMessage = "";
            IsStatusError = false;

            try
            {
                Debug.WriteLine($"[SharedAuth] Intentando login. User='{User.Trim()}'");

                var res = await _sharedApi.LoginAsync(User.Trim(), Pass);

                if (!res.Ok)
                {
                    IsStatusError = true;
                    StatusMessage = "Usuario o contraseña incorrectos.";
                    Debug.WriteLine($"[SharedAuth] Login fallido: {res.RawError}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(res.Token) || string.IsNullOrWhiteSpace(res.RefreshToken))
                {
                    IsStatusError = true;
                    StatusMessage = "Respuesta inválida del servidor.";
                    Debug.WriteLine("[SharedAuth] Token o RefreshToken vacío.");
                    return;
                }

                await _sharedState.SetSignedInAsync(res.Token!, res.RefreshToken!);

                IsStatusError = false;
                StatusMessage = "Sesión iniciada correctamente.";
                Pass = "";

                Debug.WriteLine("[SharedAuth] Login OK.");
            }
            catch (Exception ex)
            {
                IsStatusError = true;
                StatusMessage = "Error al conectar con el servidor.";
                Debug.WriteLine($"[SharedAuth] Excepción en login: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Cierra la sesión shared.
        /// La vista permanece abierta, solo se limpia el estado.
        /// </summary>
        [RelayCommand]
        private async Task LogoutSharedAsync()
        {
            IsBusy = true;
            StatusMessage = "";
            IsStatusError = false;

            try
            {
                await _sharedState.SignOutAsync();

                IsStatusError = false;
                StatusMessage = "Sesión cerrada correctamente.";
                User = "";
                Pass = "";

                Debug.WriteLine("[SharedAuth] Sesión cerrada. Permanece en la página.");
            }
            catch (Exception ex)
            {
                IsStatusError = true;
                StatusMessage = "Error al cerrar sesión.";
                Debug.WriteLine($"[SharedAuth] Error en logout: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}