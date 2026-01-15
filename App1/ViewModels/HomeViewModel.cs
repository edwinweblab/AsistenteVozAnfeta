using System;
using System.Linq;
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

        private string _currentLanguageInfo = "Idioma: No inicializado";
        public string CurrentLanguageInfo
        {
            get => _currentLanguageInfo;
            set => SetProperty(ref _currentLanguageInfo, value);
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

                var languages = _speechService.GetAvailableLanguages();
                if (languages.Count == 0)
                {
                    InfoMessage = "No hay idiomas instalados.";
                    StatusText = "Error: No hay idiomas instalados";
                    return;
                }

                await _speechService.InitializeAsync("es-MX");

                var current = _speechService.GetCurrentLanguage();
                var langName = languages.FirstOrDefault(l => l.Tag == current)?.DisplayName ?? current;

                CurrentLanguageInfo = $"Idioma: {langName}";
                InfoMessage = $"Listo en {langName}. Presiona el micrófono y habla.";
                StatusText = $"Listo en {langName}";
            }
            catch (UnauthorizedAccessException ex)
            {
                InfoMessage = ex.Message;
                StatusText = "ERROR: Permiso denegado";
            }
            catch (Exception ex)
            {
                InfoMessage = "Error: " + ex.Message;
                StatusText = "Error al inicializar voz";
            }
        }

        private async Task ListenOnceAsync()
        {
            if (IsListening) return;

            IsListening = true;
            ShowInfo = true;
            InfoMessage = "Escuchando... habla ahora";
            StatusText = "Escuchando... habla ahora";
            RecognizedText = "";

            try
            {
                var text = await _speechService.RecognizeOnceAsync();

                if (string.IsNullOrWhiteSpace(text))
                {
                    InfoMessage = "No se detectó voz. Intenta hablar más fuerte.";
                    StatusText = "No se entendió. Intenta otra vez.";
                    return;
                }

                RecognizedText = text;
                InfoMessage = "Texto detectado correctamente.";
                StatusText = $"Entendí: {text}";
            }
            catch (UnauthorizedAccessException ex)
            {
                InfoMessage = ex.Message;
                StatusText = "ERROR: Permiso denegado";
            }
            catch (Exception ex)
            {
                InfoMessage = "Error: " + ex.Message;
                StatusText = "Error al reconocer voz";
            }
            finally
            {
                IsListening = false;
            }
        }
    }
}