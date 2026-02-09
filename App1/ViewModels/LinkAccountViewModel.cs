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

        private string _email = "";
        private string _phone = "";
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
        private static string NormalizeMexPhone(string? phone)
        {
            var p = (phone ?? "").Trim();

            // quitar espacios o símbolos
            p = p.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");

            // si ya empieza con 52 no tocar
            if (p.StartsWith("52"))
                return p;

            // si empieza con 1 (algunos celulares internacionales)
            if (p.StartsWith("1") && p.Length == 11)
                p = p.Substring(1);

            return "52" + p;
        }

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

        // Propiedades nuevas para modo perfil
        private UserProfile? _userProfile;
        public UserProfile? UserProfile
        {
            get => _userProfile;
            private set
            {
                if (SetProperty(ref _userProfile, value))
                {
                    OnPropertyChanged(nameof(AvatarInitial));
                }
            }
        }

        // Primera letra del nombre para el avatar
        public string AvatarInitial =>
            !string.IsNullOrWhiteSpace(UserProfile?.FirstName)
                ? UserProfile.FirstName.Substring(0, 1).ToUpper()
                : "U";

        // Indica si el usuario está autenticado (para cambiar entre login/perfil)
        public bool IsAuthenticated => _auth.IsAuthenticated;

        // Formato de fecha de registro para mostrar en UI
        public string FormattedCreatedAt => UserProfile?.CreatedAt.ToString("dd MMM yyyy") ?? "";

        // Formato de última actividad para mostrar en UI
        public string FormattedUpdatedAt => UserProfile?.UpdatedAt.ToString("dd MMM yyyy HH:mm") ?? "";

        public bool CanLink =>
            !IsBusy &&
            !string.IsNullOrWhiteSpace(Email) &&
            Email.Contains("@") &&
            !string.IsNullOrWhiteSpace(Phone) &&
            IsPhoneValid(Phone);

        private static bool IsPhoneValid(string phone)
        {
            var p = (phone ?? "").Trim();

            p = p.Replace(" ", "").Replace("-", "");

            if (p.StartsWith("52"))
                p = p.Substring(2);

            return p.Length == 10 && p.All(char.IsDigit);
        }


        public IAsyncRelayCommand LinkCommand { get; }
        public IAsyncRelayCommand SignOutCommand { get; }

        public event Action? RequestNavigateHome;

        public LinkAccountViewModel(AuthStateService auth, WeblabUsersClient users, WeblabAuthClient authApi)
        {
            _auth = auth;
            _users = users;
            _authApi = authApi;

            LinkCommand = new AsyncRelayCommand(LinkAsync, CanLinkExecute);
            SignOutCommand = new AsyncRelayCommand(SignOutAsync);

            // Suscribirse a cambios de autenticación
            _auth.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AuthStateService.IsAuthenticated))
                {
                    OnPropertyChanged(nameof(IsAuthenticated));
                    // Cuando cambia el estado de autenticación, recargar perfil si está autenticado
                    if (IsAuthenticated)
                        _ = LoadProfileAsync();
                }
            };
        }

        // Carga el perfil del usuario desde el API
        // Llamar desde View.Loaded si IsAuthenticated es true
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

        // Cierra sesión del usuario y limpia el perfil
        // UI debe detectar cambio de IsAuthenticated para mostrar login
        private async Task SignOutAsync()
        {
            try
            {
                UserProfile = null;
                await _auth.SignOutAsync();
                Debug.WriteLine("[PROFILE] Sesión cerrada");

                // Notificar cambio de autenticación
                OnPropertyChanged(nameof(IsAuthenticated));

                // Navegar a home para limpiar estado
                RequestNavigateHome?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PROFILE] Error al cerrar sesión: {ex.Message}");
            }
        }

        private bool CanLinkExecute() => CanLink;

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
                    deviceId: deviceId,
                    phone: phone
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

                // 3) Guardar token local y cargar perfil
                await _auth.SetSignedInAsync(reg.Token!);
                Debug.WriteLine("[LINK] Vinculación OK. Token guardado.");

                // Cargar perfil después de login exitoso
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
    }
}