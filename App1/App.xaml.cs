using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
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
                    // Estado global (Single Source of Truth)
                    services.AddSingleton<AppStateService>();

                    // Configuración persistente
                    services.AddSingleton<SettingsService>();

                    // Audio
                    services.AddSingleton<AudioService>();

                    // STT
                    services.AddSingleton<ISpeechToTextService, SpeechToTextService>();

                    // TTS
                    services.AddSingleton<ITextToSpeechService, TextToSpeechService>();

                    // Hotkey global
                    services.AddSingleton<GlobalHotkeyService>();

                    // HTTP base para Ollama
                    services.AddSingleton(new HttpClient
                    {
                        BaseAddress = new Uri(OllamaConfig.BaseUrl),
                        Timeout = TimeSpan.FromMinutes(3)
                    });

                    services.AddSingleton<IOllamaHealthService, OllamaHealthService>();

                    services.AddSingleton<ICommandInterpretationService>(sp =>
                    {
                        var http = sp.GetRequiredService<HttpClient>();
                        return new OllamaInterpretationService(http, OllamaConfig.ModelName);
                    });

                    // VM singleton
                    services.AddSingleton<HomeViewModel>();
                })
                .Build();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Debug.WriteLine("APP INICIADA");
            DatabaseInitializer.InitializeDatabase();

#if DEBUG
            TestDatabaseConnection();
#endif

            _window = new MainWindow();
            MainWindowInstance = _window;
            UIQueue = DispatcherQueue.GetForCurrentThread();

            // Fuerza creación del VM (arranca warmup)
            _ = HomeVM;
            _ = CheckAndWarmupOllamaAsync();

            // Iniciar hotkey global
            _hotkey = AppHost.Services.GetRequiredService<GlobalHotkeyService>();
            _hotkey.Start();
            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;
            _hotkey.RegistrationFailed += Hotkey_RegistrationFailed;

            _window.Activate();
        }

        private void Hotkey_HotkeyPressed(object? sender, EventArgs e)
        {
            Debug.WriteLine("[HOTKEY] Detectado -> UI thread");

            UIQueue?.TryEnqueue(async () =>
            {
                try
                {
                    // Restaurar ventana si está minimizada
                    if (_window != null)
                    {
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                                WinRT.Interop.WindowNative.GetWindowHandle(_window)
                            )
                        );

                        if (appWindow != null)
                        {
                            appWindow.Show(true); // Traer al frente
                        }
                    }

                    await HomeVM.TriggerVoiceFromHotkeyAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HOTKEY] ERROR: " + ex);
                }
            });
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
    }
}