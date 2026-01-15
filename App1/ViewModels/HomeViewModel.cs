using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Anfeta.UI.Services;

namespace Anfeta.UI.ViewModels
{
    public class HomeViewModel : ObservableObject
    {
        private readonly ISpeechToTextService _speechService;

        private string _statusText = "Listo para escuchar";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private string _recognizedText = "";
        public string RecognizedText
        {
            get => _recognizedText;
            set => SetProperty(ref _recognizedText, value);
        }

        private bool _isListening;
        public bool IsListening
        {
            get => _isListening;
            set
            {
                if (SetProperty(ref _isListening, value))
                {
                    ListenOnceCommand.NotifyCanExecuteChanged();
                }
            }
        }

        // Aviso visual (InfoBar)
        private bool _showInfo;
        public bool ShowInfo
        {
            get => _showInfo;
            set => SetProperty(ref _showInfo, value);
        }

        private string _infoMessage = "";
        public string InfoMessage
        {
            get => _infoMessage;
            set => SetProperty(ref _infoMessage, value);
        }

        public IAsyncRelayCommand InitializeSpeechCommand { get; }
        public IAsyncRelayCommand ListenOnceCommand { get; }

        public HomeViewModel(ISpeechToTextService speechService)
        {
            _speechService = speechService;

            InitializeSpeechCommand = new AsyncRelayCommand(InitializeSpeechAsync);
            ListenOnceCommand = new AsyncRelayCommand(ListenOnceAsync, CanListenOnce);
        }

        private bool CanListenOnce() => !IsListening;

        private async Task InitializeSpeechAsync()
        {
            try
            {
                ShowInfo = true;
                InfoMessage = "Inicializando micrófono...";
                StatusText = "Inicializando micrófono...";

                await _speechService.InitializeAsync("es-US");

                InfoMessage = "Listo. Presiona el micrófono y habla.";
                StatusText = "Listo. Presiona el micrófono y habla.";
            }
            catch (Exception ex)
            {
                InfoMessage = "Error al inicializar voz: " + ex.Message;
                StatusText = "Error al inicializar voz: " + ex.Message;
            }
        }

        private async Task ListenOnceAsync()
        {
            if (IsListening) return;

            IsListening = true;
            ShowInfo = true;
            InfoMessage = "Escuchando... habla ahora";
            StatusText = "Escuchando... habla ahora";

            // Limpia para que se note que inició una nueva escucha
            RecognizedText = "";

            try
            {
                var text = await _speechService.RecognizeOnceAsync();

                if (string.IsNullOrWhiteSpace(text))
                {
                    InfoMessage = "No se detectó voz o no se entendió. Intenta otra vez.";
                    StatusText = "No se entendió. Intenta otra vez.";
                    return;
                }

                RecognizedText = text;
                InfoMessage = "Texto detectado.";
                StatusText = "Entendí: " + text;
            }
            catch (Exception ex)
            {
                InfoMessage = "Error: " + ex.Message;
                StatusText = "Error: " + ex.Message;
            }
            finally
            {
                IsListening = false;
            }
        }
    }
}
