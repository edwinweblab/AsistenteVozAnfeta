// ===============================
// App.xaml.cs (COMPLETO)
// - DI ya existente
// - Expone HomeVM global para hotkey (funciona en cualquier pantalla)
// - Fuerza creación del HomeViewModel al iniciar para que warmup arranque
// ===============================

using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        public static Window? MainWindowInstance { get; private set; }
        public static IHost AppHost { get; private set; } = null!;

        // ✅ VM GLOBAL (singleton real del contenedor)
        public static HomeViewModel HomeVM => AppHost.Services.GetRequiredService<HomeViewModel>();

        public App()
        {
            InitializeComponent();

            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<ISpeechToTextService, SpeechToTextService>();

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

                    // ✅ Tu VM se queda singleton
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

            // ✅ Fuerza creación del VM (para que arranque su warmup aunque no entres a Home)
            _ = HomeVM;

            // Warmup adicional (opcional)
            _ = CheckAndWarmupOllamaAsync();

            _window = new MainWindow();
            MainWindowInstance = _window;
            _window.Activate();
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
