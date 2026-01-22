using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Auth;
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
using System.Threading.Tasks;

namespace Anfeta.UI
{
    public partial class App : Application
    {
        private Window? _window;
        private GlobalHotkeyService? _hotkey;
        private FloatingMicButton? _floatingButton;

        public static Window? MainWindowInstance { get; private set; }
        public static IHost AppHost { get; private set; } = null!;
        public static DispatcherQueue? UIQueue { get; private set; }
        public static HomeViewModel HomeVM => AppHost.Services.GetRequiredService<HomeViewModel>();

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

                    // =========================
                    // Ollama 
                    // =========================
                    services.AddSingleton(new HttpClient
                    {
                        BaseAddress = new Uri(OllamaConfig.BaseUrl),
                        Timeout = TimeSpan.FromMinutes(3)
                    });

                    services.AddSingleton<IOllamaHealthService, OllamaHealthService>();
                    services.AddSingleton<ICommandInterpretationService>(sp =>
                    {
                        var http = sp.GetRequiredService<HttpClient>(); // <- ESTE ES EL DE OLLAMA
                        return new OllamaInterpretationService(http, OllamaConfig.ModelName);
                    });

                    // =========================
                    // Auth / Weblab (AGREGADO)
                    // =========================
                    services.AddSingleton<ITokenStore, LocalTokenStore>();
                    services.AddSingleton<AuthStateService>();
                    services.AddSingleton<ShellViewModel>();
                    services.AddSingleton<LinkAccountViewModel>();

                    // =========================
                    // HttpClientFactory + Auth header 
                    // =========================
                    services.AddSingleton<AuthHeaderHandler>();

                    services.AddHttpClient("WeblabAuthed", client =>
                    {
                        client.BaseAddress = new Uri("https://wlserver-production.up.railway.app");
                        client.Timeout = TimeSpan.FromSeconds(30);
                    })
                    .AddHttpMessageHandler<AuthHeaderHandler>();

                    // AuthClient 
                    services.AddHttpClient<WeblabAuthClient>(client =>
                    {
                        client.BaseAddress = new Uri("https://wlserver-production.up.railway.app");
                        client.Timeout = TimeSpan.FromSeconds(30);
                    });

                    // UsersClient 
                    services.AddSingleton<WeblabUsersClient>(sp =>
                    {
                        var factory = sp.GetRequiredService<IHttpClientFactory>();
                        return new WeblabUsersClient(factory.CreateClient("WeblabAuthed"));
                    });

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
            _ = CheckAndWarmupOllamaAsync();

            // 4) Asegurar device activo (genera/lee de SQLite)
            //    + Bootstrap auth (check-device) sin bloquear UI
            try
            {
                var deviceId = DeviceRepository.EnsureActiveDevice();
                Debug.WriteLine($"DEVICE OK: {deviceId}");

#if DEBUG
                DebugDumpDeviceRows();
#endif
                // Auto-login / check-device (no bloquea)
                _ = BootstrapAuthAsync(deviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEVICE ERROR: {ex.Message}");
                Debug.WriteLine("Si el error menciona 'is_active', tu asistente.db es viejo. " +
                                "Borra la BD (carpeta LocalFolder) para regenerarla con el esquema nuevo.");
            }

            // 5) Hotkey
            _hotkey = AppHost.Services.GetRequiredService<GlobalHotkeyService>();
            _hotkey.Start();
            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;
            _hotkey.RegistrationFailed += Hotkey_RegistrationFailed;

            ((MainWindow)_window).SizeChanged += Window_SizeChanged;

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
                var auth = AppHost.Services.GetRequiredService<AuthStateService>();
                var authApi = AppHost.Services.GetRequiredService<WeblabAuthClient>();

                // 1) Cargar token local si existe (LocalSettings)
                await auth.InitializeAsync();

                // 2) Fuente real: backend por deviceId
                var check = await authApi.CheckDeviceAsync(deviceId);

                if (check.Ok && !string.IsNullOrWhiteSpace(check.Token))
                {
                    await auth.SetSignedInAsync(check.Token!);
                    Debug.WriteLine("AUTH: device vinculado -> token OK");
                    return;
                }

                if (check.NeedsRegister)
                {
                    // Backend confirma que este device NO está registrado
                    // -limpiar token local para evitar estado inconsistente
                    await auth.SignOutAsync();
                    Debug.WriteLine("AUTH: device NO registrado -> needsRegister");
                    return;
                }

                // Si llegamos aquí, el backend devolvió algo inesperado o error:
                // NO borres el token local (puede ser offline / fallo temporal)
                Debug.WriteLine($"AUTH: check-device error/inesperado -> {check.RawError}");
            }
            catch (Exception ex)
            {
                // NO borres el token local aquí tampoco (offline / timeout)
                Debug.WriteLine("AUTH BOOTSTRAP ERROR: " + ex.Message);
            }
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            var appWindow = AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(_window)
                )
            );

            if (appWindow?.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                    ShowFloatingButton();
                else
                    HideFloatingButton();
            }
        }

        private void Hotkey_HotkeyPressed(object? sender, EventArgs e)
        {
            Debug.WriteLine("[HOTKEY] Detectado -> mostrar flotante + UI thread");
            ShowFloatingButton();

            UIQueue?.TryEnqueue(async () =>
            {
                BringMainWindowToFront();
                await HomeVM.TriggerVoiceFromHotkeyAsync();
            });
        }

        private void ShowFloatingButton()
        {
            UIQueue?.TryEnqueue(() =>
            {
                if (_floatingButton == null)
                {
                    _floatingButton = new FloatingMicButton();
                    _floatingButton.OpenAppRequested += (s, e) => BringMainWindowToFront();
                    _floatingButton.ExitRequested += (s, e) => CleanupAndExit();
                    _floatingButton.VoiceActivationRequested += async (s, e) =>
                    {
                        await HomeVM.ListenOnceCommand.ExecuteAsync(null);
                    };

                    var appState = AppHost.Services.GetRequiredService<AppStateService>();
                    _floatingButton.UpdateHotkeyDisplay(appState.GetHotkeyDisplayString());
                }
                _floatingButton.Activate();
            });
        }

        private void HideFloatingButton()
        {
            UIQueue?.TryEnqueue(() =>
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
                }
            });
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

        private void CleanupAndExit()
        {
            Debug.WriteLine("[APP] Iniciando cierre limpio...");

            try
            {
                _hotkey?.Stop();
                _hotkey?.Dispose();
                _hotkey = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error deteniendo hotkey: {ex.Message}");
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
                Debug.WriteLine($"[APP] Error cerrando flotante: {ex.Message}");
            }

            try
            {
                _window?.Close();
                _window = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error cerrando ventana: {ex.Message}");
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

        private async Task CheckAndWarmupOllamaAsync()
        {
            try
            {
                using var quick = new HttpClient
                {
                    BaseAddress = new Uri(OllamaConfig.BaseUrl),
                    Timeout = TimeSpan.FromSeconds(5)
                };

                var res = await quick.GetAsync("/api/tags");
                Debug.WriteLine($"OLLAMA STATUS: {(int)res.StatusCode}");

                if (!res.IsSuccessStatusCode)
                {
                    Debug.WriteLine("OLLAMA NO RESPONDE OK.");
                    return;
                }

                var interpreter = AppHost.Services.GetRequiredService<ICommandInterpretationService>();
                await interpreter.InterpretRawAsync("ping");

                Debug.WriteLine("OLLAMA WARMUP OK");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OLLAMA CHECK ERROR: " + ex.Message);
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
