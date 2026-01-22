// ViewModels/HomeViewModel.cs
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
        private readonly ITextToSpeechService _tts;

        private readonly LocalActionExecutor _localExecutor;
        private readonly ApiActionExecutor _apiExecutor;

        private CancellationTokenSource? _currentRecognitionCts;

        private int _listenSessionId = 0;
        private volatile bool _cancelRequested = false;

        private bool _backgroundMode = false;

        private string? _pendingIntent;
        private string? _pendingScope;
        private string? _pendingAppKey;
        private string? _pendingProvider;
        private string? _pendingResource;
        private string? _pendingAction;
        private string? _pendingParamsJson;
        private string _pendingRawJson = "";

        private bool _isModelReady;
        public bool IsModelReady
        {
            get => _isModelReady;
            private set => SetProperty(ref _isModelReady, value);
        }

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
                    ListenOnceCommand.NotifyCanExecuteChanged();
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

        public HomeViewModel(
            ISpeechToTextService speechService,
            ICommandInterpretationService interpreter,
            ITextToSpeechService tts,
            LocalActionExecutor localExecutor,
            ApiActionExecutor apiExecutor)
        {
            _speechService = speechService;
            _interpreter = interpreter;
            _tts = tts;

            _localExecutor = localExecutor;
            _apiExecutor = apiExecutor;

            InitializeSpeechCommand = new AsyncRelayCommand(InitializeSpeechAsync);
            ListenOnceCommand = new AsyncRelayCommand(ListenOnceAsync, CanListenOnce);

            ShowInfo = true;
            InfoMessage = "Cargando modelo... espera un momento.";
            StatusText = "Cargando modelo...";
            IsModelReady = false;

            Debug.WriteLine("[VM] HomeViewModel creado. Iniciando warmup IA en background...");
            _ = WarmupModelAsync();
        }

        private bool CanListenOnce() => !IsListening && IsModelReady;

        public async Task TriggerVoiceFromHotkeyAsync()
        {
            if (!IsModelReady)
            {
                await SpeakSafeAsync("Aún estoy cargando el modelo.");
                return;
            }

            _backgroundMode = true;
            try { await ListenOnceAsync(); }
            finally { _backgroundMode = false; }
        }

        private void ClearPending()
        {
            _pendingIntent = null;
            _pendingScope = null;
            _pendingAppKey = null;
            _pendingProvider = null;
            _pendingResource = null;
            _pendingAction = null;
            _pendingParamsJson = null;
            _pendingRawJson = "";
        }

        private bool HasPending() =>
            !string.IsNullOrWhiteSpace(_pendingIntent) &&
            !string.IsNullOrWhiteSpace(_pendingScope);

        private async Task SpeakSafeAsync(string text)
        {
            try { await _tts.SpeakAsync(text); }
            catch (Exception ex) { Debug.WriteLine("[TTS] ERROR: " + ex); }
        }

        private void UpdateUiSafe(string infoMessage, string? statusText = null, string? recognized = null)
        {
            if (_backgroundMode) return;

            ShowInfo = true;
            InfoMessage = infoMessage;
            StatusText = statusText ?? StatusText;

            if (recognized != null)
                RecognizedText = recognized;
        }

        private void ResetAfterAction(string infoMessage, string? statusText = null)
        {
            ClearPending();
            IsListening = false;

            UpdateUiSafe(infoMessage, statusText ?? "Listo para escuchar");
            ListenOnceCommand.NotifyCanExecuteChanged();

            Debug.WriteLine($"[VM] ResetAfterAction -> Status='{StatusText}' Info='{InfoMessage}'");
        }

        private async Task ResetAfterActionAsync(string infoMessage, string? statusText = null, string? speak = null)
        {
            ResetAfterAction(infoMessage, statusText);
            if (_backgroundMode)
                await SpeakSafeAsync(speak ?? infoMessage);
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

        private async Task WarmupModelAsync()
        {
            try
            {
                Debug.WriteLine("[IA] Warmup start: InterpretRawAsync('ping')");
                await _interpreter.InterpretRawAsync("ping");

                IsModelReady = true;

                UpdateUiSafe(
                    infoMessage: "Modelo listo. Presiona el micrófono y habla.",
                    statusText: "Listo para escuchar"
                );

                ListenOnceCommand.NotifyCanExecuteChanged();
                Debug.WriteLine("[IA] Warmup OK -> modelo listo");
            }
            catch (Exception ex)
            {
                IsModelReady = false;

                UpdateUiSafe(
                    infoMessage: "No pude conectar con el modelo. Revisa el servicio e intenta reiniciar la app.",
                    statusText: "Modelo no disponible"
                );

                ListenOnceCommand.NotifyCanExecuteChanged();
                Debug.WriteLine("[IA] Warmup ERROR: " + ex);
            }
        }

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
                if (!_backgroundMode)
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

        private async Task InitializeSpeechAsync()
        {
            try
            {
                UpdateUiSafe("Inicializando micrófono...", "Inicializando micrófono...");
                Debug.WriteLine("[UI] InitializeSpeechAsync: start");

                var languages = _speechService.GetAvailableLanguages();
                Debug.WriteLine("[STT] SupportedTopicLanguages count=" + languages.Count);

                if (languages.Count == 0)
                {
                    UpdateUiSafe(
                        "No hay idiomas instalados. Ve a Configuración de Windows → Idioma → Reconocimiento de voz.",
                        "Error: No hay idiomas instalados"
                    );
                    Debug.WriteLine("[STT] No hay idiomas instalados");
                    return;
                }

                var ok = await EnsureSpeechReadyAsync();
                if (!ok)
                {
                    UpdateUiSafe("No pude inicializar el micrófono. Revisa permisos y dispositivo.", "Error micrófono");
                    return;
                }

                var current = _speechService.GetCurrentLanguage();
                var langName = languages.Find(l => l.Tag == current)?.DisplayName ?? current;

                if (!_backgroundMode)
                    CurrentLanguageInfo = $"Idioma: {langName}";

                if (!IsModelReady)
                {
                    UpdateUiSafe("Micrófono listo. Aún cargando modelo...", "Cargando modelo...");
                    Debug.WriteLine("[UI] Mic OK pero modelo aún no listo");
                    return;
                }

                UpdateUiSafe("Listo. Presiona el micrófono y habla.", $"Listo en {langName}");
                Debug.WriteLine("[UI] InitializeSpeechAsync OK");
            }
            catch (UnauthorizedAccessException ex)
            {
                _speechInitialized = false;
                UpdateUiSafe(ex.Message, "ERROR: Permiso denegado");
                Debug.WriteLine("[STT] UnauthorizedAccessException: " + ex);
            }
            catch (Exception ex)
            {
                _speechInitialized = false;
                UpdateUiSafe("Error: " + ex.Message, "Error al inicializar voz");
                Debug.WriteLine("[STT] InitializeSpeechAsync ERROR: " + ex);
            }
        }

        // - LOCAL: sin confirmación
        // - API: solo create/update/delete requiere confirmación
        // - BROWSER: sin confirmación
        private static bool RequiresConfirmation(string scope, string? action)
        {
            if (string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(scope, "API", StringComparison.OrdinalIgnoreCase))
            {
                var a = (action ?? "").Trim().ToLowerInvariant();
                return a == "create" || a == "update" || a == "delete";
            }

            if (string.Equals(scope, "BROWSER", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private string AllowedAppsMessage() => _localExecutor.GetAllowedAppsMessage();

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

            await ResetAfterActionAsync(uiMessage, uiStatus, speak: uiMessage);
        }

        private async Task ExecutePendingIfAnyAsync()
        {
            if (!HasPending())
            {
                await ResetAfterActionAsync(
                    "No hay ninguna acción pendiente.",
                    "Sin acción pendiente",
                    speak: "No hay ninguna acción pendiente."
                );
                return;
            }

            var intent = _pendingIntent!;
            var scope = _pendingScope!;
            var appKey = _pendingAppKey;

            Debug.WriteLine("[POLICY] EJECUTANDO ACCION PENDIENTE:");
            Debug.WriteLine(_pendingRawJson);

            if (string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
            {
                if (!_localExecutor.TryExecute(intent, scope, appKey, out var msg))
                {
                    await ResetAfterActionAsync(msg, "Acción no disponible", speak: msg);
                    return;
                }

                await ResetAfterActionAsync(msg, msg, speak: msg);
                return;
            }

            if (string.Equals(scope, "API", StringComparison.OrdinalIgnoreCase))
            {
                var provider = _pendingProvider;
                var resource = _pendingResource;
                var action = _pendingAction;
                var paramsJson = _pendingParamsJson ?? "{}";

                var (ok, msg) = await _apiExecutor.ExecuteAsync(
                    provider,
                    resource,
                    action,
                    paramsJson,
                    CancellationToken.None
                );

                if (!ok)
                {
                    await ResetAfterActionAsync(msg, "API no disponible", speak: msg);
                    return;
                }

                // Mostrar y hablar el resultado real
                await ResetAfterActionAsync(msg, "Listo.", speak: msg);
                return;
            }

            await ResetAfterActionAsync(
                "Acción pendiente no soportada.",
                "No soportado",
                speak: "Acción pendiente no soportada."
            );
        }

        private async Task ListenOnceAsync()
        {
            Debug.WriteLine("[STT] ListenOnceAsync start");

            if (!IsModelReady)
            {
                await ResetAfterActionAsync(
                    "Aún estoy cargando el modelo. Espera un momento.",
                    "Cargando modelo...",
                    speak: "Aún estoy cargando el modelo."
                );
                return;
            }

            if (!await EnsureSpeechReadyAsync())
            {
                await ResetAfterActionAsync(
                    "No pude inicializar el micrófono. Revisa permisos/dispositivo.",
                    "Error micrófono",
                    speak: "No pude inicializar el micrófono."
                );
                return;
            }

            if (IsListening)
            {
                Debug.WriteLine("[STT] Ya estaba escuchando -> cancelar");
                await CancelListeningAsync("Escucha cancelada. Puedes intentar de nuevo.", "Cancelado");
                return;
            }

            _cancelRequested = false;
            var mySession = Interlocked.Increment(ref _listenSessionId);

            IsListening = true;

            UpdateUiSafe("Escuchando... habla ahora", "Escuchando... habla ahora", recognized: "");

            if (_backgroundMode)
                await SpeakSafeAsync("Te escucho.");

            _currentRecognitionCts = new CancellationTokenSource();
            var ct = _currentRecognitionCts.Token;

            try
            {
                Debug.WriteLine("[STT] RecognizeOnceAsync...");
                var text = await _speechService.RecognizeOnceAsync(ct);

                Debug.WriteLine("[STT] RecognizeOnceAsync result: " + (text ?? "<null>"));

                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[STT] Resultado ignorado por cancel/sesion nueva");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    await ResetAfterActionAsync("No se detectó voz. Intenta otra vez.", "No se entendió", speak: "No detecté voz. Intenta de nuevo.");
                    return;
                }

                UpdateUiSafe("Texto detectado correctamente.", $"Entendí: {text}", recognized: text);

                if (_backgroundMode)
                    await SpeakSafeAsync("Mensaje recibido.");

                if (_cancelRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[VM] Cancel detectado post-texto -> no interpretar");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                // pending confirmación
                if (HasPending())
                {
                    Debug.WriteLine("[POLICY] Hay pending. Texto: " + text);

                    if (IsConfirmationPhrase(text))
                    {
                        await ExecutePendingIfAnyAsync();
                        return;
                    }

                    if (IsCancelPhrase(text))
                    {
                        await ResetAfterActionAsync("Acción cancelada.", "Cancelado", speak: "Acción cancelada.");
                        return;
                    }

                    await ResetAfterActionAsync(
                        "Hay una acción pendiente. Di 'confirmar' para ejecutar o 'cancelar' para abortar.",
                        "Confirmación requerida",
                        speak: "Hay una acción pendiente. Di confirmar o cancelar."
                    );
                    return;
                }

                // Reparación/anti-sustitución local
                var requestedFromSpeech = _localExecutor.ResolveAppKeyFromSpeech(text);
                Debug.WriteLine("[STT] requestedFromSpeech=" + (requestedFromSpeech ?? "<null>"));

                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[IA] Cancel/sesion inválida antes de interpretar -> abortar");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                Debug.WriteLine("[IA] InterpretRawAsync(text)...");
                var ia = await _interpreter.InterpretRawAsync(text);

                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[IA] Resultado IA ignorado por cancel/sesion nueva");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

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

                string? provider = null;
                if (root.TryGetProperty("provider", out var provEl) && provEl.ValueKind != JsonValueKind.Null)
                    provider = provEl.GetString();

                string? resource = null;
                if (root.TryGetProperty("resource", out var resEl) && resEl.ValueKind != JsonValueKind.Null)
                    resource = resEl.GetString();

                string? action = null;
                if (root.TryGetProperty("action", out var actEl) && actEl.ValueKind != JsonValueKind.Null)
                    action = actEl.GetString();

                string? paramsJson = null;
                if (root.TryGetProperty("params", out var pr) && pr.ValueKind == JsonValueKind.Object)
                    paramsJson = pr.GetRawText();

                if (string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(appKey) &&
                    !string.IsNullOrWhiteSpace(requestedFromSpeech))
                {
                    appKey = requestedFromSpeech;
                }

                if (!string.IsNullOrWhiteSpace(requestedFromSpeech) &&
                    string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(appKey) &&
                    !requestedFromSpeech.Equals(appKey, StringComparison.OrdinalIgnoreCase))
                {
                    await ResetAfterActionAsync(
                        $"Pediste '{requestedFromSpeech}', pero interpreté '{appKey}'. No ejecutaré nada.",
                        "Acción no disponible",
                        speak: "No ejecutaré nada porque no coincide lo que pediste."
                    );
                    return;
                }

                var requiresConfirmation = RequiresConfirmation(scope, action);

                if (requiresConfirmation)
                {
                    _pendingIntent = intent;
                    _pendingScope = scope;
                    _pendingAppKey = appKey;
                    _pendingProvider = provider;
                    _pendingResource = resource;
                    _pendingAction = action;
                    _pendingParamsJson = paramsJson;
                    _pendingRawJson = ia.Json;

                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();

                    var what = scope.ToUpperInvariant() switch
                    {
                        "LOCAL" => $"{intent} {(appKey ?? "")}".Trim(),
                        "API" => $"{provider ?? "api"} {resource ?? ""} {action ?? ""}".Trim(),
                        "BROWSER" => $"{action ?? "browser"}".Trim(),
                        _ => $"{intent}".Trim()
                    };

                    UpdateUiSafe(
                        $"Confirmación requerida para: {what}. Di 'confirmar' o 'cancelar'.",
                        "Confirmación requerida"
                    );

                    if (_backgroundMode)
                        await SpeakSafeAsync("Confirmación requerida. Di confirmar o cancelar.");

                    return;
                }

                if (string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_localExecutor.TryExecute(intent, scope, appKey, out var msg))
                    {
                        await ResetAfterActionAsync(
                            msg + " " + AllowedAppsMessage(),
                            "Acción no disponible",
                            speak: msg
                        );
                        return;
                    }

                    await ResetAfterActionAsync(msg, msg, speak: msg);
                    return;
                }

                if (string.Equals(scope, "API", StringComparison.OrdinalIgnoreCase))
                {
                    var (ok, msg) = await _apiExecutor.ExecuteAsync(
                        provider,
                        resource,
                        action,
                        paramsJson ?? "{}",
                        ct
                    );

                    if (!ok)
                    {
                        await ResetAfterActionAsync(msg, "API no disponible", speak: msg);
                        return;
                    }

                    // Mostrar y hablar el resultado real
                    await ResetAfterActionAsync(msg, "Listo.", speak: msg);
                    return;
                }

                await ResetAfterActionAsync("Acción no soportada.", "No soportado", speak: "Acción no soportada.");
            }
            catch (OperationCanceledException)
            {
                await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _speechInitialized = false;
                await ResetAfterActionAsync(ex.Message, "ERROR: Permiso denegado", speak: "Permiso denegado para el micrófono.");
            }
            catch
            {
                await ResetAfterActionAsync("Error al procesar el comando.", "Error", speak: "Ocurrió un error.");
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
