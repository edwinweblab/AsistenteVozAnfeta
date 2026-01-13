using System;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace App1.FunctionTests
{
    public class NotificationTest
    {
        private static AppNotificationManager? _notificationManager;

        public static void Initialize()
        {
            _notificationManager = AppNotificationManager.Default;
            _notificationManager.NotificationInvoked += OnNotificationInvoked;
            _notificationManager.Register();
        }

        public static void ShowSimpleNotification(string title, string message)
        {
            try
            {
                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message);

                var notification = builder.BuildNotification();
                _notificationManager?.Show(notification);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
            }
        }

        public static void ShowNotificationWithButtons(string title, string message)
        {
            try
            {
                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message)
                    .AddButton(new AppNotificationButton("Aceptar")
                        .AddArgument("action", "accept"))
                    .AddButton(new AppNotificationButton("Cancelar")
                        .AddArgument("action", "cancel"));

                var notification = builder.BuildNotification();
                _notificationManager?.Show(notification);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
            }
        }

        private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine($"Notificación activada: {args.Argument}");
        }

        public static async Task<string> RunAllTests()
        {
            string result = "=== PRUEBAS DE NOTIFICACIONES ===\n\n";

            try
            {
                result += "1. Inicializando sistema...\n";
                Initialize();
                result += "   Sistema inicializado\n\n";

                result += "2. Notificación simple...\n";
                ShowSimpleNotification("Prueba ANFETA", "Notificación de prueba");
                result += "   Notificación enviada\n\n";

                await Task.Delay(2000);

                result += "3. Notificación con botones...\n";
                ShowNotificationWithButtons("Confirmación", "¿Continuar?");
                result += "   Notificación enviada\n\n";

                result += "Pruebas completadas\n";
            }
            catch (Exception ex)
            {
                result += $"Error: {ex.Message}\n";
            }

            return result;
        }
    }
}