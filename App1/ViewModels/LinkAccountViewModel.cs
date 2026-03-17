using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Anfeta.UI.ViewModels
{
    public sealed class LinkAccountViewModel : ObservableObject
    {
        private readonly AuthStateService _auth;
        private readonly WeblabUsersClient _users;
        private readonly WeblabAuthClient _authApi;
        private readonly AppStateService _appState;

        private string _email = "";
        private string _phone = "";
        private string _errorMessage = "";
        private bool _isBusy;
        private UserProfile? _userProfile;

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

        public string Phone
        {
            get => _phone;
            set
            {
                if (SetProperty(ref _phone, value))
                {
                    OnPropertyChanged(nameof(CanLink));
                    LinkCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

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

        public UserProfile? UserProfile
        {
            get => _userProfile;
            private set
            {
                if (SetProperty(ref _userProfile, value))
                    OnPropertyChanged(nameof(AvatarInitial));
            }
        }

        public string AvatarInitial =>
            !string.IsNullOrWhiteSpace(UserProfile?.FirstName)
                ? UserProfile.FirstName.Substring(0, 1).ToUpper()
                : "U";

        public bool IsAuthenticated => _auth.IsAuthenticated;
        public string FormattedCreatedAt => UserProfile?.CreatedAt.ToString("dd MMM yyyy") ?? "";
        public string FormattedUpdatedAt => UserProfile?.UpdatedAt.ToString("dd MMM yyyy HH:mm") ?? "";

        public bool CanLink =>
            !IsBusy &&
            !string.IsNullOrWhiteSpace(Email) &&
            Email.Contains("@") &&
            !string.IsNullOrWhiteSpace(Phone) &&
            IsPhoneValid(Phone);

        public IAsyncRelayCommand LinkCommand { get; }
        public IAsyncRelayCommand SignOutCommand { get; }

        // Solo se invoca al vincular exitosamente, NO al cerrar sesión.
        public event Action? RequestNavigateHome;

        public LinkAccountViewModel(
            AuthStateService auth,
            WeblabUsersClient users,
            WeblabAuthClient authApi,
            AppStateService appState)
        {
            _auth = auth;
            _users = users;
            _authApi = authApi;
            _appState = appState;

            LinkCommand = new AsyncRelayCommand(LinkAsync, CanLinkExecute);
            SignOutCommand = new AsyncRelayCommand(SignOutAsync);

            _auth.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AuthStateService.IsAuthenticated))
                {
                    OnPropertyChanged(nameof(IsAuthenticated));
                    if (IsAuthenticated)
                        _ = LoadProfileAsync();
                }
            };
        }

        /// Carga el perfil del usuario desde el API.
        /// Llamar desde View.Loaded si IsAuthenticated es true.
        public async Task LoadProfileAsync()
        {
            if (!IsAuthenticated)
            {
                UserProfile = null;
                return;
            }

            try
            {
                var (success, profile) = await _authApi.GetUserProfileAsync();

                if (success && profile != null)
                {
                    UserProfile = profile;
                    OnPropertyChanged(nameof(FormattedCreatedAt));
                    OnPropertyChanged(nameof(FormattedUpdatedAt));
                    Debug.WriteLine("[PROFILE] Perfil cargado OK");
                }
                else
                {
                    Debug.WriteLine("[PROFILE] Error al cargar perfil");
                    UserProfile = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PROFILE] Excepción: {ex.Message}");
                UserProfile = null;
            }
        }

        /// Cierra sesión, limpia perfil y limpia AppStateService.
        /// La vista permanece abierta, no navega al home.
        private async Task SignOutAsync()
        {
            try
            {
                UserProfile = null;

                _appState.CurrentUserEmail = null;
                _appState.CurrentUserName = null;
                _appState.CollaboratorId = null;

                await _auth.SignOutAsync();

                OnPropertyChanged(nameof(IsAuthenticated));
                Debug.WriteLine("[PROFILE] Sesión cerrada. AppState limpiado.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PROFILE] Error al cerrar sesión: {ex.Message}");
            }
        }

        private bool CanLinkExecute() => CanLink;

        /// Vincula el dispositivo con la cuenta Weblab.
        /// Puebla AppStateService inmediatamente tras vinculación exitosa.
        /// Navega al home solo si la vinculación fue exitosa.
        private async Task LinkAsync()
        {
            ErrorMessage = "";
            IsBusy = true;

            try
            {
                var phone = NormalizeMexPhone(Phone);

                if (!IsPhoneValid(phone))
                {
                    ErrorMessage = "Teléfono inválido. Usa solo dígitos (10 a 15).";
                    return;
                }

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

                var search = await _users.SearchByEmailAsync(email);
                Debug.WriteLine($"[LINK] search.Ok={search.Ok} fn='{search.FirstName}' ln='{search.LastName}' collabId='{search.CollaboratorId}' err='{search.RawError}'");

                if (!search.Ok ||
                    string.IsNullOrWhiteSpace(search.FirstName) ||
                    string.IsNullOrWhiteSpace(search.LastName) ||
                    string.IsNullOrWhiteSpace(search.CollaboratorId))
                {
                    ErrorMessage = "No se encontró el colaborador o faltan datos.";
                    return;
                }

                var reg = await _authApi.RegisterAsync(
                    email: email,
                    firstName: search.FirstName!,
                    lastName: search.LastName!,
                    collaboratorId: search.CollaboratorId!,
                    deviceId: deviceId,
                    phone: phone
                );

                Debug.WriteLine("====================================");
                Debug.WriteLine("[AUTH] TOKEN JWT RECIBIDO:");
                Debug.WriteLine(reg.Token);
                Debug.WriteLine("====================================");

                if (!reg.Ok || string.IsNullOrWhiteSpace(reg.Token))
                {
                    ErrorMessage = "No se pudo registrar el dispositivo.";
                    return;
                }

                await _auth.SetSignedInAsync(reg.Token!);
                Debug.WriteLine("[LINK] Vinculación OK. Token guardado.");

                // Poblar AppStateService inmediatamente con los datos ya disponibles.
                // Evita que los reportes fallen en la misma sesión de vinculación.
                _appState.CurrentUserEmail = email;
                _appState.CurrentUserName = $"{search.FirstName} {search.LastName}".Trim();
                _appState.CollaboratorId = search.CollaboratorId;

                Debug.WriteLine($"[LINK] AppState poblado → Email={_appState.CurrentUserEmail} Name={_appState.CurrentUserName} CollabId={_appState.CollaboratorId}");

                await LoadProfileAsync();

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

        private static string NormalizeMexPhone(string? phone)
        {
            var p = (phone ?? "").Trim()
                .Replace(" ", "").Replace("-", "")
                .Replace("(", "").Replace(")", "");

            if (p.StartsWith("52")) return p;
            if (p.StartsWith("1") && p.Length == 11) p = p.Substring(1);

            return "52" + p;
        }

        private static bool IsPhoneValid(string phone)
        {
            var p = (phone ?? "").Trim()
                .Replace(" ", "").Replace("-", "");

            if (p.StartsWith("52")) p = p.Substring(2);

            return p.Length == 10 && p.All(char.IsDigit);
        }
    }
}