using App1.Data;
using Microsoft.UI.Xaml;
using System;

namespace App1
{
    public partial class App : Application
    {
        private Window? _window;

        // Referencia global (opcional)
        public static Window? MainWindowInstance { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("APP INICIADA");
            DatabaseInitializer.InitializeDatabase();

#if DEBUG
            TestDatabaseConnection();
#endif

            // ✅ Abrir la ventana principal
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
