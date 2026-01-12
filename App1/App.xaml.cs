using Microsoft.UI.Xaml;
using App1.Data;

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

            _window = new MainWindow();
            _window.Activate();
        }
    }
}
