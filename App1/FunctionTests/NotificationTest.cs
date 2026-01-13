using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace App1.FunctionTests
{
    public class NotificationTest
    {
        // Diálogo simple
        public static async Task<bool> ShowSimpleNotification(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };
                await dialog.ShowAsync();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }

        // Diálogo con botones
        public static async Task<bool> ShowNotificationWithButtons(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = "Aceptar",
                    CloseButtonText = "Cancelar",
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };

                var result = await dialog.ShowAsync();
                System.Diagnostics.Debug.WriteLine($"Usuario presionó: {result}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }

        public static async Task<string> RunAllTests()
        {
            string result = "=== PRUEBAS DE NOTIFICACIONES ===\n\n";

            result += "1. Diálogo simple...\n";
            bool simple = await ShowSimpleNotification("Prueba ANFETA", "Notificación de prueba");
            result += simple ? "   ✓ Mostrado\n\n" : "   ✗ Error\n\n";

            result += "2. Diálogo con botones...\n";
            bool buttons = await ShowNotificationWithButtons("Confirmación", "¿Continuar con la operación?");
            result += buttons ? "   ✓ Mostrado\n\n" : "   ✗ Error\n\n";

            result += "Pruebas completadas\n";
            return result;
        }
    }
}