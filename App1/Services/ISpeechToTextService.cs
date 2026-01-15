using System;
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public interface ISpeechToTextService
    {
        event EventHandler<string>? PartialResult;
        event EventHandler<string>? FinalResult;
        event EventHandler<string>? Error;

        bool IsListening { get; }

        Task StartAsync(string languageTag = "es-MX");
        Task StopAsync();
    }
}
