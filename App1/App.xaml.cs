// ===============================
// App.xaml.cs (COMPLETO) - SEGUNDO PLANO LISTO
// - DI ya existente
// - Expone HomeVM global para hotkey
// - Fuerza creación del HomeViewModel al iniciar para que warmup arranque
// - Agrega TTS (ITextToSpeechService) para segundo plano con voz
// - Expone UIQueue para ejecutar hotkey/overlay en UI thread
// - Registra + inicia GlobalHotkeyService (Ctrl+Alt+V)
// - Hotkey llama a HomeVM.TriggerVoiceFromHotkeyAsync() en UI thread
// ===============================

using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;

namespace Anfeta.UI
{
    public partial class App : Application
    {
        private Window? _window;

        // Hotkey singleton
        private GlobalHotkeyService? _hotkey;

        public static Window? MainWindowInstance { get; private set; }
        public static IHost AppHost { get; private set; } = null!;

        // UI thread dispatcher queue (para hotkey -> VM sin errores de hilo)
        public static DispatcherQueue? UIQueue { get; private set; }

        // VM GLOBAL (singleton real del contenedor)
        public static HomeViewModel HomeVM => AppHost.Services.GetRequiredService<HomeViewModel>();

        public App()
        {
            InitializeComponent();

            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // STT
                    services.AddSingleton<ISpeechToTextService, SpeechToTextService>();

                    // TTS (nuevo)
                    services.AddSingleton<ITextToSpeechService, TextToSpeechService>();

                    // Hotkey global (nuevo)
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

            // Crear ventana principal
            _window = new MainWindow();
            MainWindowInstance = _window;

            // Guardar DispatcherQueue del hilo UI (ya existe aquí)
            UIQueue = DispatcherQueue.GetForCurrentThread();

            // Fuerza creación del VM (arranca warmup aunque no entres a Home)
            _ = HomeVM;

            // Warmup adicional (opcional)
            _ = CheckAndWarmupOllamaAsync();

            // Iniciar hotkey global
            _hotkey = AppHost.Services.GetRequiredService<GlobalHotkeyService>();
            _hotkey.Start();

            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;

            _window.Activate();
        }

        private void Hotkey_HotkeyPressed(object? sender, EventArgs e)
        {
            Debug.WriteLine("[HOTKEY] Evento recibido -> ejecutar flujo en UI thread");

            // Hotkey llega desde Win32: siempre encolar a UI
            UIQueue?.TryEnqueue(async () =>
            {
                try
                {
                    // Opcional: aquí abres overlay micrófono
                    // ShowOverlay();

                    await HomeVM.TriggerVoiceFromHotkeyAsync();

                    // Opcional: aquí cierras overlay
                    // HideOverlay();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HOTKEY] ERROR ejecutando TriggerVoiceFromHotkeyAsync: " + ex);
                }
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
                    Debug.WriteLine("OLLAMA NO RESPONDE OK. Revisa que Ollama esté abierto.");
                    return;
                }

                var interpreter = AppHost.Services.GetRequiredService<ICommandInterpretationService>();
                await interpreter.InterpretRawAsync("ping");

                Debug.WriteLine("OLLAMA WARMUP OK (modelo listo)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OLLAMA CHECK/WARMUP ERROR: " + ex.Message);
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

                Debug.WriteLine("CONEXION A SQLITE EXITOSA. RESULTADO: " + result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ERROR AL CONECTAR CON SQLITE: " + ex.Message);
            }
        }
    }
}
