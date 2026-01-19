// ===============================
// HomeViewModel.cs (COMPLETO)
// - Gate de “modelo listo” (warmup) antes de permitir escuchar
// - Micrófono LAZY: se inicializa SOLO si se necesita (botón)
// - Cancelación real: invalida sesión + StopRecognitionAsync via CancelAsync()
// - Evita interpretar después de cancelar (aunque llegue texto tarde)
// - Confirmación pendiente por voz
// - Anti-sustitución SOLO para tus 4 apps locales (chrome/calculadora/bloc/explorador)
// - Siempre re-habilita el botón con NotifyCanExecuteChanged()
// - SIN segundo plano: NO hotkey, NO overlay, NO TriggerVoiceFromHotkeyAsync
// ===============================

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anfeta.UI.ViewModels
{
    public class HomeViewModel : ObservableObject
    {
        private readonly ISpeechToTextService _speechService;
        private readonly ICommandInterpretationService _interpreter;
        private readonly LocalActionExecutor _localExecutor = new();

        private CancellationTokenSource? _currentRecognitionCts;

        // ====== control de sesiones para ignorar resultados tardíos ======
        private int _listenSessionId = 0;
        private volatile bool _cancelRequested = false;

        // Acción pendiente (confirmación)
        private string? _pendingIntent;
        private string? _pendingScope;
        private string? _pendingAppKey;
        private string _pendingRawJson = "";

        // Gate “modelo listo”
        private bool _isModelReady;
        public bool IsModelReady
        {
            get => _isModelReady;
            private set => SetProperty(ref _isModelReady, value);
        }

        // ====== Speech init LAZY ======
        private bool _speechInitialized;
        private readonly SemaphoreSlim _speechInitLock = new(1, 1);

        private string _statusText = "Cargando modelo...";
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

        private string _infoMessage = "Iniciando...";
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

            // Estado inicial
            ShowInfo = true;
            InfoMessage = "Cargando modelo... espera un momento.";
            StatusText = "Cargando modelo...";
            IsModelReady = false;

            Debug.WriteLine("[VM] HomeViewModel creado. Iniciando warmup IA en background...");

            // Warmup no bloquea UI
            _ = WarmupModelAsync();
        }

        private bool CanListenOnce() => !IsListening && IsModelReady;

        // ===============================
        // Helpers de estado (CLAVE)
        // ===============================
        private void ClearPending()
        {
            _pendingIntent = null;
            _pendingScope = null;
            _pendingAppKey = null;
            _pendingRawJson = "";
        }

        private bool HasPending() =>
            !string.IsNullOrWhiteSpace(_pendingIntent) &&
            !string.IsNullOrWhiteSpace(_pendingScope);

        private void ResetAfterAction(string infoMessage, string? statusText = null)
        {
            ClearPending();

            IsListening = false;

            ShowInfo = true;
            InfoMessage = infoMessage;
            StatusText = statusText ?? "Listo para escuchar";

            ListenOnceCommand.NotifyCanExecuteChanged();

            Debug.WriteLine($"[VM] ResetAfterAction -> Status='{StatusText}' Info='{InfoMessage}'");
        }

        private static bool IsConfirmationPhrase(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();
            return t == "sí" || t == "si" || t == "confirmar" || t == "confirmo" || t == "ok" || t == "dale";
        }

        private static bool IsCancelPhrase(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();
            return t == "no" || t == "cancelar" || t == "cancela" || t == "negativo";
        }

        // ===============================
        // Warmup modelo
        // ===============================
        private async Task WarmupModelAsync()
        {
            try
            {
                Debug.WriteLine("[IA] Warmup start: InterpretRawAsync('ping')");
                await _interpreter.InterpretRawAsync("ping");

                IsModelReady = true;
                ShowInfo = true;
                InfoMessage = "Modelo listo. Presiona el micrófono y habla.";
                StatusText = "Listo para escuchar";
                ListenOnceCommand.NotifyCanExecuteChanged();

                Debug.WriteLine("[IA] Warmup OK -> modelo listo");
            }
            catch (Exception ex)
            {
                IsModelReady = false;
                ShowInfo = true;
                InfoMessage = "No pude conectar con el modelo. Abre Ollama e intenta reiniciar la app.";
                StatusText = "Modelo no disponible";
                ListenOnceCommand.NotifyCanExecuteChanged();

                Debug.WriteLine("[IA] Warmup ERROR: " + ex);
            }
        }

        // ===============================
        // Speech init LAZY
        // ===============================
        private async Task<bool> EnsureSpeechReadyAsync()
        {
            if (_speechInitialized)
            {
                Debug.WriteLine("[STT] EnsureSpeechReadyAsync: ya inicializado");
                return true;
            }

            await _speechInitLock.WaitAsync();
            try
            {
                if (_speechInitialized)
                {
                    Debug.WriteLine("[STT] EnsureSpeechReadyAsync: ya inicializado (después de lock)");
                    return true;
                }

                Debug.WriteLine("[STT] Inicializando SpeechRecognizer (es-MX)...");
                await _speechService.InitializeAsync("es-MX");
                _speechInitialized = true;

                var current = _speechService.GetCurrentLanguage();
                CurrentLanguageInfo = $"Idioma: {current}";
                Debug.WriteLine("[STT] Inicialización OK. CurrentLanguage=" + current);

                return true;
            }
            catch (Exception ex)
            {
                _speechInitialized = false;
                Debug.WriteLine("[STT] Inicialización ERROR: " + ex);
                return false;
            }
            finally
            {
                _speechInitLock.Release();
            }
        }

        // ===============================
        // Init Speech (botón en pantalla principal)
        // ===============================
        private async Task InitializeSpeechAsync()
        {
            try
            {
                ShowInfo = true;
                InfoMessage = "Inicializando micrófono...";
                StatusText = "Inicializando micrófono...";
                Debug.WriteLine("[UI] InitializeSpeechAsync: start");

                var languages = _speechService.GetAvailableLanguages();
                Debug.WriteLine("[STT] SupportedTopicLanguages count=" + languages.Count);

                if (languages.Count == 0)
                {
                    InfoMessage = "No hay idiomas instalados. Ve a Configuración de Windows → Idioma → Reconocimiento de voz.";
                    StatusText = "Error: No hay idiomas instalados";
                    Debug.WriteLine("[STT] No hay idiomas instalados");
                    return;
                }

                var ok = await EnsureSpeechReadyAsync();
                if (!ok)
                {
                    InfoMessage = "No pude inicializar el micrófono. Revisa permisos y dispositivo.";
                    StatusText = "Error micrófono";
                    return;
                }

                var current = _speechService.GetCurrentLanguage();
                var langName = languages.Find(l => l.Tag == current)?.DisplayName ?? current;
                CurrentLanguageInfo = $"Idioma: {langName}";

                if (!IsModelReady)
                {
                    InfoMessage = "Micrófono listo. Aún cargando modelo...";
                    StatusText = "Cargando modelo...";
                    Debug.WriteLine("[UI] Mic OK pero modelo aún no listo");
                    return;
                }

                InfoMessage = "Listo. Presiona el micrófono y habla.";
                StatusText = $"Listo en {langName}";
                Debug.WriteLine("[UI] InitializeSpeechAsync OK");
            }
            catch (UnauthorizedAccessException ex)
            {
                _speechInitialized = false;
                InfoMessage = ex.Message;
                StatusText = "ERROR: Permiso denegado";
                Debug.WriteLine("[STT] UnauthorizedAccessException: " + ex);
            }
            catch (Exception ex)
            {
                _speechInitialized = false;
                InfoMessage = "Error: " + ex.Message;
                StatusText = "Error al inicializar voz";
                Debug.WriteLine("[STT] InitializeSpeechAsync ERROR: " + ex);
            }
        }

        // ===============================
        // POLICY confirmación (igual a tu versión)
        // ===============================
        private bool RequiresConfirmation(string intent, string scope, string? appKey)
        {
            if (!string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(appKey)) return true;
                return !_localExecutor.IsAllowedApp(appKey);
            }

            return true;
        }

        // ===============================
        // Anti-sustitución SOLO para tus 4 apps locales
        // ===============================
        private static string? ExtractRequestedAppFromSpeech(string speech)
        {
            if (string.IsNullOrWhiteSpace(speech)) return null;
            var t = speech.Trim().ToLowerInvariant();

            if (t.Contains("chrome")) return "chrome";
            if (t.Contains("navegador")) return "chrome";

            if (t.Contains("calculadora")) return "calculadora";

            if (t.Contains("bloc de notas") || t.Contains("bloc") || t.Contains("notepad"))
                return "bloc";

            if (t.Contains("explorador") || t.Contains("archivos") || t.Contains("file explorer"))
                return "explorador";

            return null;
        }

        private string AllowedAppsMessage() => _localExecutor.GetAllowedAppsMessage();

        // ===============================
        // Cancelación centralizada
        // ===============================
        private async Task CancelListeningAsync(string uiMessage, string uiStatus)
        {
            Debug.WriteLine("[STT] CancelListeningAsync");

            _cancelRequested = true;
            Interlocked.Increment(ref _listenSessionId);

            try { await _speechService.CancelAsync(); } catch { }

            try
            {
                _currentRecognitionCts?.Cancel();
                _currentRecognitionCts?.Dispose();
            }
            catch { }
            finally
            {
                _currentRecognitionCts = null;
            }

            ResetAfterAction(uiMessage, uiStatus);
        }

        // ===============================
        // Confirmación pendiente
        // ===============================
        private void ExecutePendingIfAny()
        {
            if (!HasPending())
            {
                ResetAfterAction("No hay ninguna acción pendiente.", "Sin acción pendiente");
                return;
            }

            var intent = _pendingIntent!;
            var scope = _pendingScope!;
            var appKey = _pendingAppKey;

            Debug.WriteLine("[POLICY] EJECUTANDO ACCION PENDIENTE:");
            Debug.WriteLine(_pendingRawJson);

            if (!_localExecutor.TryExecute(intent, scope, appKey, out var msg))
            {
                ResetAfterAction(msg, "Acción no disponible");
                return;
            }

            ResetAfterAction(msg, msg);
        }

        // ===============================
        // LISTEN ONCE
        // ===============================
        private async Task ListenOnceAsync()
        {
            Debug.WriteLine("[STT] ListenOnceAsync start");

            if (!IsModelReady)
            {
                ResetAfterAction("Aún estoy cargando el modelo. Espera un momento.", "Cargando modelo...");
                return;
            }

            if (!await EnsureSpeechReadyAsync())
            {
                ResetAfterAction("No pude inicializar el micrófono. Revisa permisos/dispositivo.", "Error micrófono");
                return;
            }

            // Toggle: si ya está escuchando, cancela
            if (IsListening)
            {
                Debug.WriteLine("[STT] Ya estaba escuchando -> cancelar");
                await CancelListeningAsync("Escucha cancelada. Puedes intentar de nuevo.", "Cancelado");
                return;
            }

            // Inicia sesión
            _cancelRequested = false;
            var mySession = Interlocked.Increment(ref _listenSessionId);

            IsListening = true;
            ShowInfo = true;
            InfoMessage = "Escuchando... habla ahora";
            StatusText = "Escuchando... habla ahora";
            RecognizedText = "";

            _currentRecognitionCts = new CancellationTokenSource();
            var ct = _currentRecognitionCts.Token;

            try
            {
                Debug.WriteLine("[STT] RecognizeOnceAsync...");
                var text = await _speechService.RecognizeOnceAsync(ct);

                Debug.WriteLine("[STT] RecognizeOnceAsync result: " + (text ?? "<null>"));

                // Si cancelaron o cambió la sesión, NO seguimos
                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[STT] Resultado ignorado por cancel/sesion nueva");
                    ResetAfterAction("Reconocimiento cancelado.", "Cancelado");
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    ResetAfterAction("No se detectó voz. Intenta otra vez.", "No se entendió");
                    return;
                }

                RecognizedText = text;
                InfoMessage = "Texto detectado correctamente.";
                StatusText = $"Entendí: {text}";

                // Verificación antes de lógica pesada
                if (_cancelRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[VM] Cancel detectado post-texto -> no interpretar");
                    ResetAfterAction("Reconocimiento cancelado.", "Cancelado");
                    return;
                }

                if (HasPending())
                {
                    Debug.WriteLine("[POLICY] Hay pending. Texto: " + text);

                    if (IsConfirmationPhrase(text))
                    {
                        ExecutePendingIfAny();
                        return;
                    }

                    if (IsCancelPhrase(text))
                    {
                        ResetAfterAction("Acción cancelada.", "Cancelado");
                        return;
                    }

                    ResetAfterAction(
                        "Hay una acción pendiente. Di 'confirmar' para ejecutar o 'cancelar' para abortar.",
                        "Confirmación requerida"
                    );
                    return;
                }

                var requestedFromSpeech = ExtractRequestedAppFromSpeech(RecognizedText);
                Debug.WriteLine("[STT] requestedFromSpeech=" + (requestedFromSpeech ?? "<null>"));

                // Antes de interpretar, valida cancel/sesion inválida
                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[IA] Cancel/sesion inválida antes de interpretar -> abortar");
                    ResetAfterAction("Reconocimiento cancelado.", "Cancelado");
                    return;
                }

                Debug.WriteLine("[IA] InterpretRawAsync(text)...");
                var ia = await _interpreter.InterpretRawAsync(text);

                // Si cancelaron mientras interpretaba, NO uses el resultado
                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[IA] Resultado IA ignorado por cancel/sesion nueva");
                    ResetAfterAction("Reconocimiento cancelado.", "Cancelado");
                    return;
                }

                Debug.WriteLine("===== OLLAMA PLAIN TEXT =====");
                Debug.WriteLine(ia.PlainText);

                Debug.WriteLine("===== OLLAMA JSON =====");
                Debug.WriteLine(ia.Json);

                using var doc = JsonDocument.Parse(ia.Json);
                var root = doc.RootElement;

                var intent = root.TryGetProperty("intent", out var intentEl)
                    ? (intentEl.GetString() ?? "Unknown")
                    : "Unknown";

                var scope = root.TryGetProperty("scope", out var scopeEl)
                    ? (scopeEl.GetString() ?? "LOCAL")
                    : "LOCAL";

                string? appKey = null;
                if (root.TryGetProperty("app_key", out var appEl) && appEl.ValueKind != JsonValueKind.Null)
                    appKey = appEl.GetString();

                Debug.WriteLine($"[IA] Parsed -> intent={intent}, scope={scope}, app_key={appKey}");

                if (!string.IsNullOrWhiteSpace(requestedFromSpeech) &&
                    string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(appKey) &&
                    !requestedFromSpeech.Equals(appKey, StringComparison.OrdinalIgnoreCase))
                {
                    ResetAfterAction(
                        $"Pediste '{requestedFromSpeech}', pero interpreté '{appKey}'. No ejecutaré nada.",
                        "Acción no disponible"
                    );
                    return;
                }

                // Antes de ejecutar/guardar pending, valida cancel/sesion
                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[EXEC] Cancel/sesion inválida antes de ejecutar -> abortar");
                    ResetAfterAction("Reconocimiento cancelado.", "Cancelado");
                    return;
                }

                var requiresConfirmation = RequiresConfirmation(intent, scope, appKey);
                Debug.WriteLine($"[POLICY] requires_confirmation={requiresConfirmation}");

                if (requiresConfirmation)
                {
                    _pendingIntent = intent;
                    _pendingScope = scope;
                    _pendingAppKey = appKey;
                    _pendingRawJson = ia.Json;

                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();

                    ShowInfo = true;
                    InfoMessage = $"Confirmación requerida para: {intent} {(appKey ?? "")}. Di 'confirmar' o 'cancelar'.";
                    StatusText = "Confirmación requerida";

                    Debug.WriteLine("[POLICY] Acción guardada como pending. Esperando confirmación...");
                    return;
                }

                if (!_localExecutor.TryExecute(intent, scope, appKey, out var msg))
                {
                    ResetAfterAction(msg + " " + AllowedAppsMessage(), "Acción no disponible");
                    return;
                }

                ResetAfterAction(msg, msg);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[STT] OperationCanceledException");
                ResetAfterAction("Reconocimiento cancelado.", "Cancelado");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine("[STT] UnauthorizedAccessException: " + ex);
                _speechInitialized = false;
                ResetAfterAction(ex.Message, "ERROR: Permiso denegado");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VM] ERROR: " + ex);
                ResetAfterAction("Error al procesar el comando.", "Error");
            }
            finally
            {
                IsListening = false;
                try { _currentRecognitionCts?.Dispose(); } catch { }
                _currentRecognitionCts = null;

                ListenOnceCommand.NotifyCanExecuteChanged();
                Debug.WriteLine("[STT] ListenOnceAsync end (cleanup OK)");
            }
        }
    }
}
