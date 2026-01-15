using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using System;

namespace Anfeta.UI
{
    public partial class App : Application
    {
        private Window? _window;

        // Ventana global (opcional)
        public static Window? MainWindowInstance { get; private set; }

        // Host DI global
        public static IHost AppHost { get; private set; } = null!;

        public App()
        {
            InitializeComponent();

            // Construimos el contenedor DI al arrancar la app
            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Servicios
                    services.AddSingleton<ISpeechToTextService, SpeechToTextService>();

                    // ViewModels (ejemplo: Home)
                    services.AddSingleton<HomeViewModel>();
                })
                .Build();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("APP INICIADA");

            // BD local (se queda igual)
            DatabaseInitializer.InitializeDatabase();

#if DEBUG
            TestDatabaseConnection();
#endif

            // Ventana principal
            _window = new MainWindow();
            MainWindowInstance = _window;
            _window.Activate();
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
