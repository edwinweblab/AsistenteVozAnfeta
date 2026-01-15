using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace Anfeta.UI.FunctionTests
{
    public class NetworkTest
    {
        public static async Task<bool> CheckInternetConnection()
        {
            try
            {
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync("8.8.8.8", 3000);
                    return reply.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string GetNetworkStatus()
        {
            return NetworkInterface.GetIsNetworkAvailable() ? "Red disponible" : "Sin red";
        }

        public static async Task<string> RunAllTests()
        {
            string result = "=== PRUEBAS DE RED ===\n\n";

            result += "1. Verificando disponibilidad de red...\n";
            result += $"   Resultado: {GetNetworkStatus()}\n\n";

            result += "2. Verificando conexión a internet...\n";
            bool hasInternet = await CheckInternetConnection();
            result += $"   Resultado: {(hasInternet ? "Conectado" : "Sin internet")}\n\n";

            result += "3. Interfaces de red activas:\n";
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    result += $"   - {ni.Name} ({ni.NetworkInterfaceType})\n";
                }
            }

            return result;
        }
    }
}