using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace App1.FunctionTests
{
    public sealed partial class TestRunner : Window
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

        private async void BtnRunAllTests_Click(object sender, RoutedEventArgs e)
        {
            SetTestingState(true);
            txtResults.Text = "Ejecutando todas las pruebas...\n\n";

            try
            {
                txtStatus.Text = "Pruebas de red...";
                txtResults.Text += await NetworkTest.RunAllTests() + "\n\n";
                await Task.Delay(500);

                txtStatus.Text = "Pruebas de notificaciones...";
                txtResults.Text += await NotificationTest.RunAllTests() + "\n\n";
                await Task.Delay(500);

                txtStatus.Text = "Pruebas del sistema...";
                txtResults.Text += await SystemFunctionsTest.RunAllTests() + "\n\n";

                txtResults.Text += "========================================\n";
                txtResults.Text += "TODAS LAS PRUEBAS COMPLETADAS\n";
                txtResults.Text += "========================================\n";
                txtStatus.Text = "Completado";
            }
            catch (Exception ex)
            {
                txtResults.Text += $"\nError: {ex.Message}\n";
                txtStatus.Text = "Error";
            }
            finally
            {
                SetTestingState(false);
            }
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
            btnRunAllTests.IsEnabled = !isTesting;

            progressBar.Visibility = isTesting ? Visibility.Visible : Visibility.Collapsed;
            progressBar.IsIndeterminate = isTesting;
        }
    }
}