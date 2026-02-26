// Services/Calendar/GoogleAuthService.cs
using Anfeta.UI.Services.Auth;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Calendar
{
    /// <summary>
    /// Gestiona el estado de conexión con Google Calendar.
    /// Cachea el teléfono del usuario, verifica conexión y abre el OAuth en el navegador.
    /// No maneja tokens de Google directamente — eso lo hace el backend.
    /// </summary>
    public sealed class GoogleAuthService
    {
        private readonly GoogleCalendarClient _calendarClient;
        private readonly WeblabAuthClient _authClient;

        private const string SettingsKeyConnected = "google_calendar_connected";
        private const string SettingsKeyUserId = "google_calendar_user_id";

        // Caché en memoria para evitar llamadas repetidas a /api/auth/me
        private string? _cachedUserId;
        private readonly SemaphoreSlim _phoneLock = new(1, 1);

        public GoogleAuthService(
            GoogleCalendarClient calendarClient,
            WeblabAuthClient authClient)
        {
            _calendarClient = calendarClient;
            _authClient = authClient;
        }

        // ─────────────────────────────────────────────
        // USER ID (TELÉFONO)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Obtiene el teléfono del usuario autenticado (userId para Google).
        /// Prioridad: caché en memoria → LocalSettings → /api/auth/me.
        /// Entrada: ninguna
        /// Salida: teléfono del usuario o null si no disponible.
        /// </summary>
        public async Task<string?> GetUserIdAsync(CancellationToken ct = default)
        {
            // 1. Caché en memoria
            if (!string.IsNullOrWhiteSpace(_cachedUserId))
                return _cachedUserId;

            await _phoneLock.WaitAsync(ct);
            try
            {
                // 2. Double-check después del lock
                if (!string.IsNullOrWhiteSpace(_cachedUserId))
                    return _cachedUserId;

                // 3. LocalSettings
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue(SettingsKeyUserId, out var saved) &&
                    saved is string savedPhone &&
                    !string.IsNullOrWhiteSpace(savedPhone))
                {
                    _cachedUserId = savedPhone;
                    Debug.WriteLine($"[GAUTH] UserId cargado de LocalSettings: {_cachedUserId}");
                    return _cachedUserId;
                }

                // 4. Llamada al backend
                Debug.WriteLine("[GAUTH] Obteniendo phone desde /api/auth/me...");
                var (ok, phone, name) = await _authClient.GetCurrentUserPhoneAsync(ct);

                if (!ok || string.IsNullOrWhiteSpace(phone))
                {
                    Debug.WriteLine("[GAUTH] No se pudo obtener el phone del usuario.");
                    return null;
                }

                _cachedUserId = phone;
                settings.Values[SettingsKeyUserId] = phone;

                Debug.WriteLine($"[GAUTH] UserId obtenido y cacheado: {_cachedUserId} ({name})");
                return _cachedUserId;
            }
            finally
            {
                _phoneLock.Release();
            }
        }

        /// <summary>
        /// Limpia el caché del userId (útil al cerrar sesión en la app).
        /// </summary>
        public void ClearUserIdCache()
        {
            _cachedUserId = null;

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values.Remove(SettingsKeyUserId);

            Debug.WriteLine("[GAUTH] UserId cache limpiado.");
        }

        // ─────────────────────────────────────────────
        // ESTADO DE CONEXIÓN
        // ─────────────────────────────────────────────

        /// <summary>
        /// Verifica si el usuario tiene Google Calendar conectado.
        /// Consulta directamente al backend (GET /google/status).
        /// Entrada: ninguna
        /// Salida: true si hay tokens válidos en el backend.
        /// </summary>
        public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
        {
            try
            {
                var userId = await GetUserIdAsync(ct);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    Debug.WriteLine("[GAUTH] IsConnectedAsync: no hay userId disponible.");
                    return false;
                }

                var (ok, connected) = await _calendarClient.GetStatusAsync(userId, ct);

                Debug.WriteLine($"[GAUTH] IsConnectedAsync: ok={ok}, connected={connected}");

                // Persistir estado localmente para referencia rápida en UI
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values[SettingsKeyConnected] = connected;

                return ok && connected;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GAUTH] IsConnectedAsync ERROR: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Retorna el último estado de conexión guardado en LocalSettings sin llamar al backend.
        /// Útil para UI inicial rápida mientras se verifica en background.
        /// </summary>
        public bool GetCachedConnectionState()
        {
            var settings = ApplicationData.Current.LocalSettings;
            return settings.Values.TryGetValue(SettingsKeyConnected, out var v) &&
                   v is bool b && b;
        }

        // ─────────────────────────────────────────────
        // OAUTH
        // ─────────────────────────────────────────────

        /// <summary>
        /// Inicia el flujo OAuth de Google Calendar abriendo el navegador del sistema.
        /// El backend maneja el callback y guarda los tokens automáticamente.
        /// Entrada: ninguna
        /// Salida: (ok, message) — ok=true si se abrió el navegador correctamente.
        /// </summary>
        public async Task<(bool ok, string message)> StartOAuthAsync(CancellationToken ct = default)
        {
            try
            {
                var userId = await GetUserIdAsync(ct);
                if (string.IsNullOrWhiteSpace(userId))
                    return (false, "No se pudo identificar tu usuario. Asegúrate de estar autenticado.");

                Debug.WriteLine($"[GAUTH] StartOAuthAsync para userId={userId}");

                var (ok, authUrl, error) = await _calendarClient.GetAuthUrlAsync(userId, ct);

                if (!ok || string.IsNullOrWhiteSpace(authUrl))
                {
                    var msg = error ?? "No se pudo obtener la URL de autorización.";
                    Debug.WriteLine($"[GAUTH] GetAuthUrlAsync falló: {msg}");
                    return (false, msg);
                }

                Debug.WriteLine($"[GAUTH] Abriendo navegador: {authUrl}");

                // Abre la URL en el navegador predeterminado del sistema
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);

                return (true, "Se abrió el navegador para conectar tu cuenta de Google. " +
                              "Autoriza el acceso y vuelve a la aplicación.");
            }
            catch (OperationCanceledException)
            {
                return (false, "Operación cancelada.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GAUTH] StartOAuthAsync ERROR: {ex}");
                return (false, $"Error al iniciar autorización: {ex.Message}");
            }
        }

        /// <summary>
        /// Cierra sesión de Google Calendar revocando tokens en el backend.
        /// Entrada: ninguna
        /// Salida: (ok, message)
        /// </summary>
        public async Task<(bool ok, string message)> DisconnectAsync(CancellationToken ct = default)
        {
            try
            {
                var userId = await GetUserIdAsync(ct);
                if (string.IsNullOrWhiteSpace(userId))
                    return (false, "No se pudo identificar tu usuario.");

                var (ok, message) = await _calendarClient.LogoutAsync(userId, ct);

                // Limpiar estado local independientemente del resultado
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values[SettingsKeyConnected] = false;

                Debug.WriteLine($"[GAUTH] DisconnectAsync: ok={ok}, msg={message}");
                return (ok, message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GAUTH] DisconnectAsync ERROR: {ex}");
                return (false, ex.Message);
            }
        }
        /// <summary>
        /// Inicia el flujo OAuth. Si openBrowser=true abre el navegador del sistema,
        /// si false copia la URL al portapapeles.
        /// Entrada: openBrowser (bool)
        /// Salida: (ok, message)
        /// </summary>
        public async Task<(bool ok, string message)> StartOAuthAsync(
            bool openBrowser = true,
            CancellationToken ct = default)
        {
            try
            {
                var userId = await GetUserIdAsync(ct);
                if (string.IsNullOrWhiteSpace(userId))
                    return (false, "No se pudo identificar tu usuario. Asegúrate de estar autenticado.");

                var (ok, authUrl, error) = await _calendarClient.GetAuthUrlAsync(userId, ct);

                if (!ok || string.IsNullOrWhiteSpace(authUrl))
                    return (false, error ?? "No se pudo obtener la URL de autorización.");

                if (openBrowser)
                {
                    Debug.WriteLine($"[GAUTH] Abriendo navegador: {authUrl}");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = authUrl,
                        UseShellExecute = true
                    });
                    return (true, "Se abrió el navegador predeterminado. Autoriza el acceso y presiona 'Verificar conexión'.");
                }
                else
                {
                    Debug.WriteLine($"[GAUTH] Copiando URL al portapapeles: {authUrl}");
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(authUrl);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                    return (true, "URL copiada al portapapeles. Pégala en tu navegador, autoriza y presiona 'Verificar conexión'.");
                }
            }
            catch (OperationCanceledException)
            {
                return (false, "Operación cancelada.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GAUTH] StartOAuthAsync ERROR: {ex}");
                return (false, $"Error al obtener la URL: {ex.Message}");
            }
        }

    }
}