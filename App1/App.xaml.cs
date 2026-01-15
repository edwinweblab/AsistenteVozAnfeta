using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Anfeta.UI
{
    public partial class App : Application
    {
        private Window? _window;

        public static Window? MainWindowInstance { get; private set; }
        public static IHost AppHost { get; private set; } = null!;

        public App()
        {
            InitializeComponent();

            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Speech to text (igual que antes)
                    services.AddSingleton<ISpeechToTextService, SpeechToTextService>();

                    // HttpClient para Ollama (timeout mayor)
                    services.AddSingleton(new HttpClient
                    {
                        BaseAddress = new Uri(OllamaConfig.BaseUrl),
                        Timeout = TimeSpan.FromMinutes(3)
                    });

                    // Servicios Ollama
                    services.AddSingleton<IOllamaHealthService, OllamaHealthService>();
                    services.AddSingleton<ICommandInterpretationService>(sp =>
                    {
                        var http = sp.GetRequiredService<HttpClient>();
                        return new OllamaInterpretationService(http, OllamaConfig.ModelName);
                    });

                    // ViewModel
                    services.AddSingleton<HomeViewModel>();
                })
                .Build();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("APP INICIADA");

            DatabaseInitializer.InitializeDatabase();

#if DEBUG
            TestDatabaseConnection();
#endif

            // Verificar Ollama + warmup (no bloquea UI)
            _ = CheckAndWarmupOllamaAsync();

            _window = new MainWindow();
            MainWindowInstance = _window;
            _window.Activate();
        }

        private async Task CheckAndWarmupOllamaAsync()
        {
            try
            {
                // 1) Verificar si Ollama responde (rápido)
                using var quick = new HttpClient
                {
                    BaseAddress = new Uri(OllamaConfig.BaseUrl),
                    Timeout = TimeSpan.FromSeconds(5)
                };

                var res = await quick.GetAsync("/api/tags");
                System.Diagnostics.Debug.WriteLine($"OLLAMA STATUS: {(int)res.StatusCode}");

                if (!res.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("OLLAMA NO RESPONDE OK. Revisa que Ollama esté abierto.");
                    return;
                }

                // 2) Warmup del modelo (puede tardar, usa el timeout grande del DI)
                var interpreter = AppHost.Services.GetRequiredService<ICommandInterpretationService>();
                await interpreter.InterpretRawAsync("ping");

                System.Diagnostics.Debug.WriteLine("OLLAMA WARMUP OK (modelo listo)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OLLAMA CHECK/WARMUP ERROR: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("CONEXION A SQLITE EXITOSA. RESULTADO: " + result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ERROR AL CONECTAR CON SQLITE: " + ex.Message);
            }
        }
    }
}
