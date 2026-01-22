using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anfeta.UI.ViewModels
{
    public sealed class LinkAccountViewModel : ObservableObject
    {
        private readonly AuthStateService _auth;
        private readonly WeblabUsersClient _users;
        private readonly WeblabAuthClient _authApi;

        private string _email = "";
        public string Email
        {
            get => _email;
            set
            {
                if (SetProperty(ref _email, value))
                {
                    OnPropertyChanged(nameof(CanLink));
                    LinkCommand.NotifyCanExecuteChanged();
                }
            }
        }

        private string _errorMessage = "";
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanLink));
                    LinkCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool CanLink =>
            !IsBusy &&
            !string.IsNullOrWhiteSpace(Email) &&
            Email.Contains("@");

        public IAsyncRelayCommand LinkCommand { get; }

        public event Action? RequestNavigateHome;

        public LinkAccountViewModel(AuthStateService auth, WeblabUsersClient users, WeblabAuthClient authApi)
        {
            _auth = auth;
            _users = users;
            _authApi = authApi;

            LinkCommand = new AsyncRelayCommand(LinkAsync, CanLinkExecute);
        }

        private bool CanLinkExecute() => CanLink;

        private async Task LinkAsync()
        {
            ErrorMessage = "";
            IsBusy = true;

            try
            {
                var email = (Email ?? "").Trim();
                Debug.WriteLine($"[LINK] Intento vincular. Email='{email}'");

                if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
                {
                    ErrorMessage = "Correo inválido.";
                    Debug.WriteLine("[LINK] Correo inválido.");
                    return;
                }

                var deviceId = DeviceRepository.EnsureActiveDevice();
                Debug.WriteLine($"[LINK] deviceId='{deviceId}'");

                // 1) Buscar colaborador por correo
                var search = await _users.SearchByEmailAsync(email);

                Debug.WriteLine(
                    $"[LINK] search.Ok={search.Ok} fn='{search.FirstName}' ln='{search.LastName}' collabId='{search.CollaboratorId}' err='{search.RawError}'"
                );

                if (!search.Ok ||
                    string.IsNullOrWhiteSpace(search.FirstName) ||
                    string.IsNullOrWhiteSpace(search.LastName) ||
                    string.IsNullOrWhiteSpace(search.CollaboratorId))
                {
                    ErrorMessage = "No se encontró el colaborador o faltan datos (firstName/lastName/collaboratorId).";
                    return;
                }

                // 2) Registrar device en backend (CON LOS CAMPOS REALES)
                var reg = await _authApi.RegisterAsync(
                    email: email,
                    firstName: search.FirstName!,
                    lastName: search.LastName!,
                    collaboratorId: search.CollaboratorId!,
                    deviceId: deviceId
                );

                // 🔐 LOG DEL TOKEN (SOLO DEBUG)
                Debug.WriteLine("====================================");
                Debug.WriteLine("[AUTH] TOKEN JWT RECIBIDO:");
                Debug.WriteLine(reg.Token);
                Debug.WriteLine("====================================");

                if (!reg.Ok || string.IsNullOrWhiteSpace(reg.Token))
                {
                    ErrorMessage = "No se pudo registrar el dispositivo.";
                    return;
                }

                // 3) Guardar token local y volver a Home
                await _auth.SetSignedInAsync(reg.Token!);
                Debug.WriteLine("[LINK] Vinculación OK. Token guardado.");

                RequestNavigateHome?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LINK] ERROR: " + ex);
                ErrorMessage = "Error inesperado al vincular.";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
