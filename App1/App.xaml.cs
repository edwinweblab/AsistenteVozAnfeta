using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Activity;
using Anfeta.UI.Services.Auth;
using Anfeta.UI.Services.Groq;
using Anfeta.UI.Services.Interpretation;
using Anfeta.UI.Services.Speech;
using Anfeta.UI.Services.Weblab;
using Anfeta.UI.ViewModels;
using Anfeta.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Anfeta.UI
{
    public partial class App : Application
    {
        private Window? _window;
        private GlobalHotkeyService? _hotkey;
        private FloatingMicButton? _floatingButton;
        private bool _isShuttingDown = false;
        private readonly object _floatingButtonLock = new object();

        public static Window? MainWindowInstance { get; private set; }
        public static IHost AppHost { get; private set; } = null!;
        public static DispatcherQueue? UIQueue { get; private set; }
        public static HomeViewModel HomeVM => AppHost.Services.GetRequiredService<HomeViewModel>();
        //Nefta
        public static LocalIndexService LocalIndex { get; } = new LocalIndexService();

        // URL base actualizada
        private const string WeblabBaseUrl = "https://wlserver-production.up.railway.app";

        public App()
        {
            InitializeComponent();

            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // =========================
                    // Core app services
                    // =========================
                    services.AddSingleton<AppStateService>();
                    services.AddSingleton<SettingsService>();
                    services.AddSingleton<AudioService>();
                    services.AddSingleton<ISpeechToTextService, SpeechToTextService>();
                    services.AddSingleton<ITextToSpeechService, TextToSpeechService>();
                    services.AddSingleton<GlobalHotkeyService>();

                    // Context system (ORDEN IMPORTA)
                    services.AddSingleton<CapabilityRegistry>();
                    services.AddSingleton<FastCommandClassifier>();
                    services.AddSingleton<InterpretationCache>();
                    services.AddSingleton<ContextManager>();
                    services.AddSingleton<PromptBuilder>();
                    services.AddSingleton<IntentValidator>();

                    // =========================
                    // Activity Creation Flow
                    // =========================
                    services.AddSingleton<ActivityFieldExtractor>();
                    services.AddSingleton<ActivityFieldValidator>();
                    services.AddSingleton<CorrectionCommandDetector>();

                    // =========================
                    // GROQ
                    // =========================
                    services.AddSingleton<ApiKeyRepository>();
                    services.AddSingleton<ApiKeyService>();

                    services.AddTransient<GroqAuthHeaderHandler>();

                    services.AddHttpClient("Groq", client =>
                    {
                        client.BaseAddress = new Uri(GroqConfig.BaseUrl);
                        client.Timeout = TimeSpan.FromSeconds(60);
                    })
                    .AddHttpMessageHandler<GroqAuthHeaderHandler>();

                    services.AddSingleton<ICommandInterpretationService>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        var http = factory.CreateClient("Groq");
                        return new GroqInterpretationService(http, GroqConfig.ModelName);
                    });

                    services.AddHttpClient("GroqValidate", client =>
                    {
                        client.BaseAddress = new Uri(GroqConfig.BaseUrl);
                        client.Timeout = TimeSpan.FromSeconds(30);
                    });

                    services.AddSingleton<GroqKeyValidator>();
                    services.AddTransient<Anfeta.UI.Views.ApiKeysView>();
                    services.AddSingleton<GroqRuntimeService>();

                    // =========================
                    // Auth / Weblab (LOCAL)
                    // =========================
                    services.AddSingleton<ITokenStore, LocalTokenStore>();
                    services.AddSingleton<AuthStateService>();
                    services.AddSingleton<ShellViewModel>();
                    services.AddSingleton<LinkAccountViewModel>();

                    // =========================
                    // Shared Auth (sesión compartida)
                    // =========================
                    services.AddSingleton<SharedTokenStore>();
                    services.AddSingleton<SharedAuthStateService>(sp =>
                        new SharedAuthStateService(
                            sp.GetRequiredService<SharedTokenStore>(),
                            sp.GetRequiredService<WeblabSharedAuthClient>()
                        ));
                    services.AddSingleton<LinkSharedAccountViewModel>();

                    // =========================
                    // HttpClient handlers (SIEMPRE Transient)
                    // =========================
                    services.AddTransient<AuthHeaderHandler>();
                    services.AddTransient<SharedAuthHeaderHandler>();
                    services.AddTransient<SharedAuthRefreshHandler>();

                    // =========================
                    // HttpClients Weblab
                    // =========================

                    // Sin handlers — para login/refresh shared (evita dependencia circular)
                    services.AddHttpClient("WeblabBase", client =>
                    {
                        client.BaseAddress = new Uri(WeblabBaseUrl);
                        client.Timeout = TimeSpan.FromSeconds(100);
                    });

                    // Con token personal — para operaciones de usuario local
                    services.AddHttpClient("WeblabAuthed", client =>
                    {
                        client.BaseAddress = new Uri(WeblabBaseUrl);
                        client.Timeout = TimeSpan.FromSeconds(100);
                    })
                    .AddHttpMessageHandler<AuthHeaderHandler>();

                    // Con token shared + refresh automático — para operaciones del equipo
                    services.AddHttpClient("WeblabSharedAuthedWithRefresh", client =>
                    {
                        client.BaseAddress = new Uri(WeblabBaseUrl);
                        client.Timeout = TimeSpan.FromSeconds(100);
                    })
                    .AddHttpMessageHandler<SharedAuthRefreshHandler>()
                    .AddHttpMessageHandler<AuthHeaderHandler>();

                    // =========================
                    // Weblab API Clients
                    // =========================

                    // WeblabAuthClient — autenticación local (usa WeblabAuthed)
                    services.AddSingleton<Anfeta.UI.Services.Auth.WeblabAuthClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        return new Anfeta.UI.Services.Auth.WeblabAuthClient(factory.CreateClient("WeblabAuthed"));
                    });

                    // WeblabSharedAuthClient — login/refresh shared (usa WeblabBase, sin handlers)
                    services.AddSingleton<WeblabSharedAuthClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        return new WeblabSharedAuthClient(factory.CreateClient("WeblabBase"));
                    });

                    // WeblabReportesClient
                    services.AddSingleton<WeblabReportesClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        var auth = sp.GetRequiredService<Anfeta.UI.Services.Auth.WeblabAuthClient>();
                        return new WeblabReportesClient(factory.CreateClient("WeblabAuthed"), auth);
                    });

                    // WeblabUsersClient
                    services.AddSingleton<WeblabUsersClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        return new WeblabUsersClient(factory.CreateClient("WeblabAuthed"));
                    });

                    // WeblabActividadesClient — usa shared con refresh automático
                    services.AddSingleton<WeblabActividadesClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        var auth = sp.GetRequiredService<Anfeta.UI.Services.Auth.WeblabAuthClient>();
                        return new WeblabActividadesClient(
                            factory.CreateClient("WeblabSharedAuthedWithRefresh"),
                            factory.CreateClient("WeblabAuthed"),
                            auth
                        );
                    });

                    // WeblabRevisionesClient
                    services.AddSingleton<WeblabRevisionesClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        return new WeblabRevisionesClient(factory.CreateClient("WeblabAuthed"));
                    });

                    // WeblabRecordatoriosClient
                    services.AddSingleton<WeblabRecordatoriosClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        var auth = sp.GetRequiredService<Anfeta.UI.Services.Auth.WeblabAuthClient>();
                        return new WeblabRecordatoriosClient(factory.CreateClient("WeblabAuthed"), auth);
                    });

                    // =========================
                    // Google Calendar
                    // =========================
                    services.AddSingleton<Anfeta.UI.Services.Calendar.GoogleCalendarClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        return new Anfeta.UI.Services.Calendar.GoogleCalendarClient(
                            factory.CreateClient("WeblabBase"));
                    });

                    services.AddSingleton<Anfeta.UI.Services.Calendar.GoogleAuthService>(sp =>
                    {
                        var calendarClient = sp.GetRequiredService<Anfeta.UI.Services.Calendar.GoogleCalendarClient>();
                        var authClient = sp.GetRequiredService<Anfeta.UI.Services.Auth.WeblabAuthClient>();
                        return new Anfeta.UI.Services.Calendar.GoogleAuthService(calendarClient, authClient);
                    });

                    services.AddSingleton<GoogleCalendarViewModel>();

                    // =========================
                    // Action Executors
                    // =========================
                    services.AddSingleton<LocalActionExecutor>();
                    services.AddSingleton<Anfeta.UI.Data.LocalAppsRepository>();
                    services.AddSingleton<LocalAppsRepository>();
                    services.AddSingleton<CapabilityRegistry>();
                    services.AddSingleton<AllowedAppsViewModel>();
                    // ApiActionExecutor con todos los clientes necesarios
                    services.AddSingleton<ApiActionExecutor>(sp =>
                    {
                        var actividades = sp.GetRequiredService<WeblabActividadesClient>();
                        var revisiones = sp.GetRequiredService<WeblabRevisionesClient>();
                        var reportes = sp.GetRequiredService<WeblabReportesClient>();
                        var recordatorios = sp.GetRequiredService<WeblabRecordatoriosClient>();
                        var auth = sp.GetRequiredService<Anfeta.UI.Services.Auth.WeblabAuthClient>();
                        var googleCalendar = sp.GetRequiredService<Anfeta.UI.Services.Calendar.GoogleCalendarClient>();
                        var googleAuth = sp.GetRequiredService<Anfeta.UI.Services.Calendar.GoogleAuthService>();
                        return new ApiActionExecutor(actividades, revisiones, reportes, recordatorios, auth, googleCalendar, googleAuth);
                    }); ;

                    // =========================
                    // ViewModels
                    // =========================
                    services.AddSingleton<HomeViewModel>();
                })
                .Build();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Debug.WriteLine("APP INICIADA");

            // 1) Crear/asegurar esquema SQLite
            DatabaseInitializer.InitializeDatabase();

#if DEBUG
            TestDatabaseConnection();
#endif

            // 2) Crear ventana ANTES del bootstrap auth (evita carreras de UI)
            _window = new MainWindow();
            MainWindowInstance = _window;
            UIQueue = DispatcherQueue.GetForCurrentThread();

            // 3) Mantener tu flujo actual
            _ = HomeVM;
            _ = CheckAndWarmupGroqAsync();

            // 4) Asegurar device activo + Bootstrap auth sin bloquear UI
            try
            {
                var deviceId = DeviceRepository.EnsureActiveDevice();
                Debug.WriteLine($"DEVICE OK: {deviceId}");

#if DEBUG
                DebugDumpDeviceRows();
#endif
                _ = BootstrapAuthAsync(deviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEVICE ERROR: {ex.Message}");
            }

            // 5) Hotkey
            _hotkey = AppHost.Services.GetRequiredService<GlobalHotkeyService>();
            _hotkey.Start();
            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;
            _hotkey.RegistrationFailed += Hotkey_RegistrationFailed;

            HomeVM.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(HomeViewModel.IsListening))
                    _floatingButton?.SetListeningState(HomeVM.IsListening);
            };

            _window.Activate();
        }

        private async Task BootstrapAuthAsync(string deviceId)
        {
            try
            {
                // =========================
                // BOOTSTRAP SHARED (cargar tokens guardados + refresh automático)
                // =========================
                var sharedState = AppHost.Services.GetRequiredService<SharedAuthStateService>();
                await sharedState.InitializeAsync();

#if DEBUG
                var sharedStore = AppHost.Services.GetRequiredService<SharedTokenStore>();
                var sharedAccess = await sharedStore.GetTokenAsync();
                var sharedRefresh = await sharedStore.GetRefreshTokenAsync();

                Debug.WriteLine("======= TOKEN SHARED (LocalSettings) =======");
                Debug.WriteLine($"ACCESS:  {sharedAccess ?? "(NULL)"}");
                Debug.WriteLine($"REFRESH: {sharedRefresh ?? "(NULL)"}");
                Debug.WriteLine("===========================================");
#endif

                var auth = AppHost.Services.GetRequiredService<AuthStateService>();
                var authApi = AppHost.Services.GetRequiredService<Anfeta.UI.Services.Auth.WeblabAuthClient>();
                var tokenStore = AppHost.Services.GetRequiredService<ITokenStore>();

                // 1) Cargar token local
                await auth.InitializeAsync();

#if DEBUG
                var tokenLocal = await tokenStore.GetTokenAsync();
                Debug.WriteLine("======= TOKEN LOCAL =======");
                Debug.WriteLine(tokenLocal ?? "(NULL)");
                Debug.WriteLine("===========================");
#endif

                if (auth.IsAuthenticated)
                {
                    Debug.WriteLine("AUTH: token local válido -> usuario autenticado");
                    return;
                }

                // 2) Verificar logout manual
                var wasManualLogout = await tokenStore.WasManualLogoutAsync();
                if (wasManualLogout)
                {
                    Debug.WriteLine("AUTH: usuario cerró sesión manualmente -> NO auto-login");
                    return;
                }

                // 3) Intentar auto-login por deviceId
                var check = await authApi.CheckDeviceAsync(deviceId);

                if (check.Ok && !string.IsNullOrWhiteSpace(check.Token))
                {
                    await auth.SetSignedInAsync(check.Token!);
                    Debug.WriteLine("AUTH: device vinculado -> token OK (auto-login)");
                    return;
                }

                if (check.NeedsRegister)
                {
                    Debug.WriteLine("AUTH: device NO registrado -> needsRegister");
                    return;
                }

                Debug.WriteLine($"AUTH: check-device error/inesperado -> {check.RawError}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("AUTH BOOTSTRAP ERROR: " + ex.Message);
            }
        }

        private void Hotkey_HotkeyPressed(object? sender, EventArgs e)
        {
            Debug.WriteLine("[HOTKEY] Detectado -> mostrar flotante + UI thread");

            UIQueue?.TryEnqueue(async () =>
            {
                ShowFloatingButton();
                BringMainWindowToFront();
                await HomeVM.TriggerVoiceFromHotkeyAsync();
            });
        }

        private void ShowFloatingButton()
        {
            lock (_floatingButtonLock)
            {
                try
                {
                    if (_floatingButton != null)
                    {
                        Debug.WriteLine("[APP] Flotante ya existe -> reutilizar");
                        _floatingButton.Activate();
                        return;
                    }

                    Debug.WriteLine("[APP] Creando nuevo flotante...");
                    _floatingButton = new FloatingMicButton();
                    _floatingButton.OpenAppRequested += (s, e) => BringMainWindowToFront();
                    _floatingButton.ExitRequested += (s, e) => CleanupAndExit();
                    _floatingButton.VoiceActivationRequested += async (s, e) =>
                    {
                        await HomeVM.ListenOnceCommand.ExecuteAsync(null);
                    };

                    var appState = AppHost.Services.GetRequiredService<AppStateService>();
                    _floatingButton.UpdateHotkeyDisplay(appState.GetHotkeyDisplayString());

                    _floatingButton.Activate();
                    Debug.WriteLine("[APP] Flotante creado y activado OK");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[APP] Error creando flotante: {ex.Message}");
                    _floatingButton = null;
                }
            }
        }

        private void HideFloatingButton()
        {
            lock (_floatingButtonLock)
            {
                try
                {
                    if (_floatingButton != null)
                    {
                        _floatingButton.Close();
                        _floatingButton = null;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[APP] Error ocultando flotante: {ex.Message}");
                    _floatingButton = null;
                }
            }
        }

        private void BringMainWindowToFront()
        {
            if (_window != null)
            {
                var appWindow = AppWindow.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                        WinRT.Interop.WindowNative.GetWindowHandle(_window)
                    )
                );
                appWindow?.Show(true);
            }
        }

        public void CleanupComponents()
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            Debug.WriteLine("[APP] Limpiando componentes...");

            try
            {
                _hotkey?.Stop();
                _hotkey?.Dispose();
                _hotkey = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error hotkey: {ex.Message}");
            }

            try
            {
                if (_floatingButton != null)
                {
                    _floatingButton.Close();
                    _floatingButton = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error flotante: {ex.Message}");
            }

            Application.Current.Exit();
        }

        public void CleanupAndExit()
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            Debug.WriteLine("[APP] Cierre completo...");

            try
            {
                _hotkey?.Stop();
                _hotkey?.Dispose();
                _hotkey = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error hotkey: {ex.Message}");
            }

            try
            {
                if (_floatingButton != null)
                {
                    _floatingButton.Close();
                    _floatingButton = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error flotante: {ex.Message}");
            }

            try
            {
                _window?.Close();
                _window = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error ventana: {ex.Message}");
            }

            Environment.Exit(0);
        }

        private void Hotkey_RegistrationFailed(object? sender, string message)
        {
            UIQueue?.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Error al configurar atajo",
                    Content = message,
                    CloseButtonText = "Entendido",
                    XamlRoot = _window?.Content?.XamlRoot
                };
                await dialog.ShowAsync();
            });
        }

        private async Task CheckAndWarmupGroqAsync()
        {
            try
            {
                var keyService = AppHost.Services.GetRequiredService<ApiKeyService>();
                var key = await keyService.GetActiveGroqKeyAsync();

                if (string.IsNullOrWhiteSpace(key))
                {
                    Debug.WriteLine("GROQ: no hay API key activa (configúrala en Settings)");
                    return;
                }

                var interpreter = AppHost.Services.GetRequiredService<ICommandInterpretationService>();
                await interpreter.InterpretRawAsync("ping");
                Debug.WriteLine("GROQ WARMUP OK");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GROQ WARMUP ERROR: " + ex.Message);
            }
        }

        private void TestDatabaseConnection()
        {
            try
            {
                using var connection = DbConnectionFactory.Create();
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                var result = command.ExecuteScalar();
                Debug.WriteLine("SQLITE OK: " + result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SQLITE ERROR: " + ex.Message);
            }
        }

#if DEBUG
        private void DebugDumpDeviceRows()
        {
            try
            {
                using var connection = DbConnectionFactory.Create();
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT device_id, is_active, last_seen_at
                    FROM device
                    ORDER BY id DESC
                    LIMIT 5;
                ";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var active = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);
                    var seen = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    Debug.WriteLine($"DEVICE ROW -> id={id} active={active} seen={seen}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DEVICE DUMP ERROR: " + ex.Message);
            }
        }
#endif
    }
}