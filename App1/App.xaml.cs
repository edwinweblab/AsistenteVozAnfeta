using Microsoft.UI.Xaml;
using Microsoft.Data.Sqlite;
using App1.Data;
using System;

namespace App1
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("APP INICIADA");
            DatabaseInitializer.InitializeDatabase();
            TestDatabaseConnection();

            _window = new App1.FunctionTests.TestRunner();
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