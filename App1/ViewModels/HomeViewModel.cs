using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Anfeta.UI.Services;
using System.Diagnostics;
using System.Text.Json;


namespace Anfeta.UI.ViewModels
{
    public class HomeViewModel : ObservableObject
    {
        private readonly ISpeechToTextService _speechService;
        private readonly ICommandInterpretationService _interpreter;
        private CancellationTokenSource? _currentRecognitionCts;

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

        public HomeViewModel(ISpeechToTextService speechService, ICommandInterpretationService interpreter)
        {
            _speechService = speechService;
            _interpreter = interpreter;

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
                    InfoMessage = "No hay idiomas instalados. Ve a Configuración de Windows → Idioma → Reconocimiento de voz.";
                    StatusText = "Error: No hay idiomas instalados";
                    return;
                }

                await _speechService.InitializeAsync("es-MX");

                var current = _speechService.GetCurrentLanguage();
                var langName = languages.FirstOrDefault(l => l.Tag == current)?.DisplayName ?? current;

                CurrentLanguageInfo = $"Idioma: {langName}";
                InfoMessage = "Listo. Presiona el micrófono y habla.";
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
            if (IsListening)
            {
                _currentRecognitionCts?.Cancel();
                _currentRecognitionCts?.Dispose();
                _currentRecognitionCts = null;
                IsListening = false;
                await Task.Delay(100);
            }

            IsListening = true;
            ShowInfo = true;
            InfoMessage = "Escuchando... habla ahora";
            StatusText = "Escuchando... habla ahora";
            RecognizedText = "";

            _currentRecognitionCts = new CancellationTokenSource();
            var ct = _currentRecognitionCts.Token;

            try
            {
                var text = await _speechService.RecognizeOnceAsync(ct);

                if (ct.IsCancellationRequested)
                {
                    InfoMessage = "Reconocimiento cancelado.";
                    StatusText = "Cancelado";
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    InfoMessage = "No se detectó voz. Intenta hablar más fuerte.";
                    StatusText = "No se entendió. Intenta otra vez.";
                    return;
                }

                RecognizedText = text;
                InfoMessage = "Texto detectado correctamente.";
                StatusText = $"Entendí: {text}";

                // Llamada a Ollama para interpretar
                try
                {
                    var ia = await _interpreter.InterpretRawAsync(text);

                    System.Diagnostics.Debug.WriteLine("===== OLLAMA PLAIN TEXT =====");
                    System.Diagnostics.Debug.WriteLine(ia.PlainText);

                    System.Diagnostics.Debug.WriteLine("===== OLLAMA JSON =====");
                    System.Diagnostics.Debug.WriteLine(ia.Json);

                    // ============================
                    // ACCION LOCAL (SOLO CHROME)
                    // ============================
                    try
                    {
                        using var doc = JsonDocument.Parse(ia.Json);
                        var root = doc.RootElement;

                        var intent = root.TryGetProperty("intent", out var intentEl) ? intentEl.GetString() : null;
                        var scope = root.TryGetProperty("scope", out var scopeEl) ? scopeEl.GetString() : null;
                        var appKey = root.TryGetProperty("app_key", out var appEl) ? appEl.GetString() : null;

                        if (string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(appKey, "chrome", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.WriteLine("ACCION: Abrir Chrome");

                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "chrome.exe",
                                UseShellExecute = true
                            });

                            Debug.WriteLine("ACCION OK: Chrome abierto");
                        }
                        else
                        {
                            Debug.WriteLine($"ACCION NO SOPORTADA AUN -> intent={intent}, scope={scope}, app_key={appKey}");
                        }
                    }
                    catch (Exception exAction)
                    {
                        Debug.WriteLine("ERROR PARSE/EJECUCION JSON: " + exAction.Message);
                    }
                }
                catch (Exception exIa)
                {
                    System.Diagnostics.Debug.WriteLine("===== ERROR OLLAMA =====");
                    System.Diagnostics.Debug.WriteLine(exIa.Message);
                }
            }
            catch (OperationCanceledException)
            {
                InfoMessage = "Reconocimiento cancelado.";
                StatusText = "Cancelado";
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
                _currentRecognitionCts?.Dispose();
                _currentRecognitionCts = null;
            }
        }
        }
    }
