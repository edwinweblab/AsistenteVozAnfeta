using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Devices;

namespace App1.FunctionTests
{
    public static class MicrophoneTest
    {
        public static async Task<string> RunAllTests()
        {
            var results = new StringBuilder();
            results.AppendLine("========================================");
            results.AppendLine("PRUEBAS DE MICRÓFONO");
            results.AppendLine("========================================\n");

            await TestMicrophoneDevices(results);
            await TestMicrophonePermissions(results);
            await TestMicrophoneCapture(results);

            results.AppendLine("\n========================================");
            results.AppendLine("PRUEBAS COMPLETADAS");
            results.AppendLine("========================================");

            return results.ToString();
        }

        private static async Task TestMicrophoneDevices(StringBuilder results)
        {
            results.AppendLine("1. Verificando dispositivos de audio...");
            try
            {
                var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());

                if (devices.Count > 0)
                {
                    results.AppendLine($"   ✓ Encontrados {devices.Count} dispositivo(s) de captura");
                    foreach (var device in devices)
                    {
                        results.AppendLine($"     - {device.Name}");
                    }
                }
                else
                {
                    results.AppendLine("   ✗ No se encontraron dispositivos de audio");
                }
            }
            catch (Exception ex)
            {
                results.AppendLine($"   ✗ Error: {ex.Message}");
            }
            results.AppendLine();
        }

        private static async Task TestMicrophonePermissions(StringBuilder results)
        {
            results.AppendLine("2. Verificando permisos de micrófono...");
            try
            {
                var capture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio
                };

                await capture.InitializeAsync(settings);
                results.AppendLine("   ✓ Permisos de micrófono concedidos");
                capture.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                results.AppendLine("   ✗ Permisos de micrófono denegados");
                results.AppendLine("     Activa los permisos en Configuración > Privacidad > Micrófono");
            }
            catch (Exception ex)
            {
                results.AppendLine($"   ✗ Error: {ex.Message}");
            }
            results.AppendLine();
        }

        private static async Task TestMicrophoneCapture(StringBuilder results)
        {
            results.AppendLine("3. Probando captura de audio...");
            try
            {
                var capture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio
                };

                await capture.InitializeAsync(settings);

                if (capture.AudioDeviceController != null)
                {
                    results.AppendLine("   ✓ Captura de audio inicializada correctamente");
                    results.AppendLine($"     Dispositivo: {capture.MediaCaptureSettings.AudioDeviceId}");
                }
                else
                {
                    results.AppendLine("   ✗ No se pudo inicializar la captura de audio");
                }

                capture.Dispose();
            }
            catch (Exception ex)
            {
                results.AppendLine($"   ✗ Error: {ex.Message}");
            }
            results.AppendLine();
        }
    }
}