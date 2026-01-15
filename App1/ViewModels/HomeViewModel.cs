using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Anfeta.UI.Services;

namespace Anfeta.UI.ViewModels
{
    public partial class HomeViewModel : ObservableObject
    {
        private readonly ISpeechToTextService _stt;
        private readonly DispatcherQueue _ui;

        [ObservableProperty] private bool isListening;
        [ObservableProperty] private string statusText = "Listo";
        [ObservableProperty] private string lastHeard = "Di: \"Abrir navegador\"";

        public IAsyncRelayCommand ToggleMicCommand { get; }

        public HomeViewModel(ISpeechToTextService stt, DispatcherQueue uiQueue)
        {
            _stt = stt;
            _ui = uiQueue;

            _stt.PartialResult += (_, text) =>
            {
                _ui.TryEnqueue(() =>
                {
                    LastHeard = text;
                    StatusText = "Escuchando...";
                });
            };

            _stt.FinalResult += (_, text) =>
            {
                _ui.TryEnqueue(() =>
                {
                    LastHeard = text;
                    StatusText = "Entendido";
                });
            };

            _stt.Error += (_, msg) =>
            {
                _ui.TryEnqueue(() =>
                {
                    StatusText = "Error";
                    LastHeard = msg;
                    IsListening = false;
                });
            };

            ToggleMicCommand = new AsyncRelayCommand(ToggleMicAsync);
        }

        private async Task ToggleMicAsync()
        {
            if (!_stt.IsListening)
            {
                StatusText = "Iniciando micrófono...";
                await _stt.StartAsync("es-MX");

                // Asegura que la UI se actualice desde UI thread
                _ui.TryEnqueue(() =>
                {
                    IsListening = _stt.IsListening;
                    StatusText = IsListening ? "Escuchando..." : "Listo";
                });
            }
            else
            {
                StatusText = "Deteniendo...";
                await _stt.StopAsync();

                _ui.TryEnqueue(() =>
                {
                    IsListening = false;
                    StatusText = "Listo";
                });
            }
        }
    }
}
