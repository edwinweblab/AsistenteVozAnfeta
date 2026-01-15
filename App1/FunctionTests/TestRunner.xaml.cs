using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace Anfeta.UI.FunctionTests
{
    public sealed partial class TestRunner : Page
    {
        public TestRunner()
        {
            this.InitializeComponent();
        }

        private async void BtnTestNetwork_Click(object sender, RoutedEventArgs e)
        {
            await RunTest("Red", NetworkTest.RunAllTests);
        }

        private async void BtnTestNotifications_Click(object sender, RoutedEventArgs e)
        {
            await RunTest("Notificaciones", NotificationTest.RunAllTests);
        }

        private async void BtnTestSystem_Click(object sender, RoutedEventArgs e)
        {
            await RunTest("Sistema", SystemFunctionsTest.RunAllTests);
        }

        private async void BtnTestMicrophone_Click(object sender, RoutedEventArgs e)
        {
            await RunTest("Micrófono", MicrophoneTest.RunAllTests);
        }

        private async Task RunTest(string testName, Func<Task<string>> testFunc)
        {
            SetTestingState(true);
            txtStatus.Text = $"Ejecutando {testName}...";
            txtResults.Text = $"Iniciando pruebas de {testName}...\n\n";

            try
            {
                txtResults.Text = await testFunc();
                txtStatus.Text = $"{testName} completadas";
            }
            catch (Exception ex)
            {
                txtResults.Text = $"Error: {ex.Message}";
                txtStatus.Text = "Error";
            }
            finally
            {
                SetTestingState(false);
            }
        }

        private void SetTestingState(bool isTesting)
        {
            btnTestNetwork.IsEnabled = !isTesting;
            btnTestNotifications.IsEnabled = !isTesting;
            btnTestSystem.IsEnabled = !isTesting;
            btnTestMicrophone.IsEnabled = !isTesting;
            progressBar.Visibility = isTesting ? Visibility.Visible : Visibility.Collapsed;
            progressBar.IsIndeterminate = isTesting;
        }
    }
}