using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Anfeta.UI.FunctionTests
{
    public class SystemFunctionsTest
    {
        public static string GetCurrentTime()
        {
            return DateTime.Now.ToString("HH:mm:ss");
        }

        public static string GetCurrentDate()
        {
            return DateTime.Now.ToString("dddd, dd MMMM yyyy");
        }

        public static double Calculate(string expression)
        {
            try
            {
                var table = new System.Data.DataTable();
                return Convert.ToDouble(table.Compute(expression, string.Empty));
            }
            catch
            {
                throw new ArgumentException("Expresión inválida");
            }
        }

        public static bool OpenApplication(string appName)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = appName,
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string GetSystemInfo()
        {
            string info = "";
            info += $"SO: {Environment.OSVersion}\n";
            info += $"Equipo: {Environment.MachineName}\n";
            info += $"Usuario: {Environment.UserName}\n";
            info += $"Procesadores: {Environment.ProcessorCount}\n";
            info += $"Memoria: {GC.GetTotalMemory(false) / 1024 / 1024} MB";
            return info;
        }

        public static async Task<string> RunAllTests()
        {
            string result = "=== PRUEBAS DEL SISTEMA ===\n\n";

            result += "1. Hora actual...\n";
            result += $"   {GetCurrentTime()}\n\n";

            result += "2. Fecha actual...\n";
            result += $"   {GetCurrentDate()}\n\n";

            result += "3. Calculadora (5 + 3 * 2)...\n";
            try
            {
                double calcResult = Calculate("5 + 3 * 2");
                result += $"   Resultado: {calcResult}\n\n";
            }
            catch (Exception ex)
            {
                result += $"   Error: {ex.Message}\n\n";
            }

            result += "4. Información del sistema...\n";
            result += $"   {GetSystemInfo()}\n\n";

            result += "5. Abrir Calculadora...\n";
            bool calcOpened = OpenApplication("calc.exe");
            result += $"   {(calcOpened ? "Abierta" : "Error")}\n\n";

            await Task.Delay(1000);
            result += "Pruebas completadas\n";

            return result;
        }
    }
}