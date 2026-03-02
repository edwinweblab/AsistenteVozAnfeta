// ViewModels/GoogleCalendarViewModel.cs
using Anfeta.UI.Services.Calendar;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace Anfeta.UI.ViewModels
{
    /// <summary>
    /// ViewModel para la pantalla de conexión con Google Calendar.
    /// Maneja estado de conexión, inicio de OAuth y desconexión.
    /// Expone propiedades observables para binding en la View.
    /// </summary>
    public sealed class GoogleCalendarViewModel : ObservableObject
    {
        private readonly GoogleAuthService _googleAuth;

        // ─────────────────────────────────────────────
        // PROPIEDADES OBSERVABLES
        // ─────────────────────────────────────────────

        // Color de la barra lateral de la card de estado
        // Propiedad de solo lectura — se recalcula cuando cambia IsConnected
        public string StatusBarColor => IsConnected ? "#34D399" : "#FF6B35";

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (SetProperty(ref _isConnected, value))
                {
                    OnPropertyChanged(nameof(IsDisconnected));
                    OnPropertyChanged(nameof(ConnectionStatusText));
                    OnPropertyChanged(nameof(ConnectionStatusIcon));
                    OnPropertyChanged(nameof(StatusBarColor));
                    ConnectCommand.NotifyCanExecuteChanged();
                    DisconnectCommand.NotifyCanExecuteChanged();

                    // Actualiza el indicador en la barra superior
                    if (App.MainWindowInstance is MainWindow mainWindow)
                        App.UIQueue?.TryEnqueue(() =>
                            mainWindow.UpdateGoogleCalendarIndicator(value));
                }
            }
        }

        // Inverso de IsConnected para binding de visibilidad
        public bool IsDisconnected => !IsConnected;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    ConnectCommand.NotifyCanExecuteChanged();
                    DisconnectCommand.NotifyCanExecuteChanged();
                    RefreshStatusCommand.NotifyCanExecuteChanged();
                }
            }
        }

        private string _statusMessage = "Verificando conexión...";
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        // Texto descriptivo del estado actual
        public string ConnectionStatusText => IsConnected
            ? "Google Calendar conectado"
            : "Google Calendar no conectado";

        // Icono según estado (usa Segoe Fluent Icons)
        public string ConnectionStatusIcon => IsConnected
            ? "\uE73E"   // Checkmark
            : "\uEA3A";  // Cancel

        // ─────────────────────────────────────────────
        // COMANDOS
        // ─────────────────────────────────────────────

        public IAsyncRelayCommand ConnectCommand { get; }
        public IAsyncRelayCommand DisconnectCommand { get; }
        public IAsyncRelayCommand RefreshStatusCommand { get; }

        // ─────────────────────────────────────────────
        // CONSTRUCTOR
        // ─────────────────────────────────────────────

        public GoogleCalendarViewModel(GoogleAuthService googleAuth)
        {
            _googleAuth = googleAuth;

            ConnectCommand = new AsyncRelayCommand(
                ConnectAsync,
                () => !IsLoading && !IsConnected);

            DisconnectCommand = new AsyncRelayCommand(
                DisconnectAsync,
                () => !IsLoading && IsConnected);

            RefreshStatusCommand = new AsyncRelayCommand(
                RefreshStatusAsync,
                () => !IsLoading);

            // Estado inicial desde caché local sin esperar al backend
            IsConnected = _googleAuth.GetCachedConnectionState();
        }

        // ─────────────────────────────────────────────
        // INICIALIZACIÓN
        // ─────────────────────────────────────────────

        /// <summary>
        /// Verifica el estado real de conexión contra el backend.
        /// Debe llamarse al cargar la View (OnNavigatedTo o Loaded).
        /// </summary>
        public async Task InitializeAsync()
        {
            await RefreshStatusAsync();
        }

        // ─────────────────────────────────────────────
        // HANDLERS
        // ─────────────────────────────────────────────

        /// <summary>
        /// Muestra diálogo de elección y ejecuta OAuth según preferencia del usuario.
        /// </summary>
        private async Task ConnectAsync()
        {
            IsLoading = true;
            StatusMessage = "Elige cómo autorizar el acceso...";

            try
            {
                // El comando viene del hilo UI — no necesita dispatcher
                var dialog = new ContentDialog
                {
                    Title = "Conectar Google Calendar",
                    Content = "¿Cómo quieres abrir la URL de autorización?",
                    PrimaryButtonText = "Abrir en navegador",
                    SecondaryButtonText = "Copiar al portapapeles",
                    CloseButtonText = "Cancelar",
                    XamlRoot = App.MainWindowInstance?.Content?.XamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.None)
                {
                    StatusMessage = "Operación cancelada.";
                    return;
                }

                var openBrowser = result == ContentDialogResult.Primary;
                var (ok, message) = await _googleAuth.StartOAuthAsync(openBrowser);

                StatusMessage = message;
                Debug.WriteLine($"[GCAL_VM] ConnectAsync: openBrowser={openBrowser}, ok={ok}");
            }
            catch (Exception ex)
            {
                StatusMessage = "Error al iniciar la autorización.";
                Debug.WriteLine($"[GCAL_VM] ConnectAsync ERROR: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Revoca los tokens de Google en el backend y actualiza el estado.
        /// </summary>
        private async Task DisconnectAsync()
        {
            IsLoading = true;
            StatusMessage = "Desconectando Google Calendar...";

            try
            {
                var (ok, message) = await _googleAuth.DisconnectAsync();

                IsConnected = false;
                StatusMessage = ok
                    ? "Google Calendar desconectado correctamente."
                    : $"Error al desconectar: {message}";

                Debug.WriteLine($"[GCAL_VM] DisconnectAsync: ok={ok}");
            }
            catch (Exception ex)
            {
                StatusMessage = "Error al desconectar.";
                Debug.WriteLine($"[GCAL_VM] DisconnectAsync ERROR: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Consulta el estado real de conexión al backend y actualiza la UI.
        /// </summary>
        private async Task RefreshStatusAsync()
        {
            IsLoading = true;
            StatusMessage = "Verificando conexión con Google...";

            try
            {
                var connected = await _googleAuth.IsConnectedAsync();
                IsConnected = connected;

                StatusMessage = connected
                    ? "Google Calendar conectado y listo."
                    : "Google Calendar no está conectado.";

                Debug.WriteLine($"[GCAL_VM] RefreshStatus: connected={connected}");
            }
            catch (Exception ex)
            {
                StatusMessage = "No se pudo verificar el estado.";
                Debug.WriteLine($"[GCAL_VM] RefreshStatusAsync ERROR: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}