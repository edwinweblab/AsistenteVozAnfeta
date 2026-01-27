// HomeViewModel.cs
using Anfeta.UI.Models;
using Anfeta.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.ViewModels
{
    public class HomeViewModel : ObservableObject
    {
        private readonly ISpeechToTextService _speechService;
        private readonly ICommandInterpretationService _interpreter;
        private readonly ITextToSpeechService _tts;
        private readonly LocalActionExecutor _localExecutor;
        private readonly ContextManager _contextManager;
        private readonly IntentValidator _validator;
        private readonly FastCommandClassifier _fastClassifier;
        private readonly InterpretationCache _interpretationCache;

        private CancellationTokenSource? _currentRecognitionCts;

        // Control de sesiones para ignorar resultados tardíos
        private int _listenSessionId = 0;
        private volatile bool _cancelRequested = false;

        // Modo ejecución (Home UI vs Segundo plano)
        private bool _backgroundMode = false;

        // Acción pendiente (confirmación)
        private string? _pendingIntent;
        private string? _pendingScope;
        private string? _pendingAppKey;
        private string _pendingRawJson = "";

        // Gate "modelo listo"
        private bool _isModelReady;
        public bool IsModelReady
        {
            get => _isModelReady;
            private set => SetProperty(ref _isModelReady, value);
        }

        // Speech init LAZY
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

        public HomeViewModel(
            ISpeechToTextService speechService,
            ICommandInterpretationService interpreter,
            ITextToSpeechService tts,
            LocalActionExecutor localExecutor,
            ContextManager contextManager,
            IntentValidator validator,
            FastCommandClassifier fastClassifier,
            InterpretationCache interpretationCache)
        {
            _speechService = speechService;
            _interpreter = interpreter;
            _tts = tts;
            _localExecutor = localExecutor;
            _contextManager = contextManager;
            _validator = validator;
            _fastClassifier = fastClassifier;
            _interpretationCache = interpretationCache;

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

        /// <summary>Entrypoint para segundo plano (hotkey)</summary>
        public async Task TriggerVoiceFromHotkeyAsync()
        {
            if (!IsModelReady)
            {
                await SpeakSafeAsync("Aún estoy cargando el modelo.");
                return;
            }

            _backgroundMode = true;
            try
            {
                await ListenOnceAsync();
            }
            finally
            {
                _backgroundMode = false;
            }
        }

        /// <summary>Limpiar acción pendiente</summary>
        private void ClearPending()
        {
            _pendingIntent = null;
            _pendingScope = null;
            _pendingAppKey = null;
            _pendingRawJson = "";
        }

        /// <summary>Verificar si hay acción pendiente</summary>
        private bool HasPending() =>
            !string.IsNullOrWhiteSpace(_pendingIntent) &&
            !string.IsNullOrWhiteSpace(_pendingScope);

        /// <summary>TTS con manejo de errores</summary>
        private async Task SpeakSafeAsync(string text)
        {
            try { await _tts.SpeakAsync(text); }
            catch (Exception ex) { Debug.WriteLine("[TTS] ERROR: " + ex); }
        }

        /// <summary>Actualizar UI (solo si NO está en background)</summary>
        private void UpdateUiSafe(string infoMessage, string? statusText = null, string? recognized = null)
        {
            if (_backgroundMode) return;

            ShowInfo = true;
            InfoMessage = infoMessage;
            StatusText = statusText ?? StatusText;

            if (recognized != null)
                RecognizedText = recognized;
        }

        /// <summary>Reset después de acción (sin TTS)</summary>
        private void ResetAfterAction(string infoMessage, string? statusText = null)
        {
            ClearPending();
            IsListening = false;

            UpdateUiSafe(infoMessage, statusText ?? "Listo para escuchar");
            ListenOnceCommand.NotifyCanExecuteChanged();

            Debug.WriteLine($"[VM] ResetAfterAction -> Status='{StatusText}' Info='{InfoMessage}'");
        }

        /// <summary>Reset después de acción (con TTS en background)</summary>
        private async Task ResetAfterActionAsync(string infoMessage, string? statusText = null, string? speak = null)
        {
            ResetAfterAction(infoMessage, statusText);

            if (_backgroundMode)
                await SpeakSafeAsync(speak ?? infoMessage);
        }

        /// <summary>Verificar si es frase de confirmación</summary>
        private static bool IsConfirmationPhrase(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();
            return t == "sí" || t == "si" || t == "confirmar" || t == "confirmo" || t == "ok" || t == "dale";
        }

        /// <summary>Verificar si es frase de cancelación</summary>
        private static bool IsCancelPhrase(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();
            return t == "no" || t == "cancelar" || t == "cancela" || t == "negativo";
        }

        /// <summary>Warmup del modelo IA</summary>
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
                    infoMessage: "No pude conectar con el modelo. Abre Ollama e intenta reiniciar la app.",
                    statusText: "Modelo no disponible"
                );

                ListenOnceCommand.NotifyCanExecuteChanged();
                Debug.WriteLine("[IA] Warmup ERROR: " + ex);
            }
        }

        /// <summary>Inicialización lazy de speech recognition</summary>
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

        /// <summary>Inicialización de speech (botón en Home)</summary>
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

        /// <summary>Determina si una acción requiere confirmación explícita</summary>
        private bool RequiresConfirmation(string intent, string scope, string? appKey)
        {
            // Scope API siempre requiere confirmación
            if (!string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
                return true;

            // OpenApp de apps SEGURAS (whitelist) → SIN confirmación
            if (string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(appKey))
                    return true;

                // WHITELIST: Apps seguras que NO requieren confirmación
                var safeApps = new[] { "chrome", "calculadora", "bloc", "explorador" };
                if (safeApps.Contains(appKey.ToLowerInvariant()))
                    return false;

                return true;
            }

            // CloseApp de app ACTIVA → SIN confirmación
            if (string.Equals(intent, "CloseApp", StringComparison.OrdinalIgnoreCase))
            {
                var ctx = _contextManager.GetContext();

                if (ctx.CurrentApp != null)
                {
                    // Sin app_key especificado → cierra la activa (seguro)
                    if (string.IsNullOrWhiteSpace(appKey))
                        return false;

                    // app_key coincide con la activa → seguro
                    if (appKey.Equals(ctx.CurrentApp.AppKey, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return true;
            }

            // WebSearch con navegador ABIERTO → SIN confirmación
            if (string.Equals(intent, "WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                var ctx = _contextManager.GetContext();
                if (ctx.CurrentApp?.Category == "navegador")
                    return false;

                return true;
            }

            // MinimizeAll es seguro → SIN confirmación
            if (string.Equals(intent, "MinimizeAll", StringComparison.OrdinalIgnoreCase))
                return false;

            // Cualquier otro intent desconocido → requiere confirmación
            return true;
        }

        /// <summary>Anti-sustitución para 4 apps registradas</summary>
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

        /// <summary>Mensaje de apps permitidas</summary>
        private string AllowedAppsMessage() => _localExecutor.GetAllowedAppsMessage();

        /// <summary>Cancelación centralizada</summary>
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

        /// <summary>Ejecutar acción pendiente</summary>
        private async Task ExecutePendingIfAnyAsync()
        {
            if (!HasPending())
            {
                await ResetAfterActionAsync("No hay ninguna acción pendiente.", "Sin acción pendiente", speak: "No hay ninguna acción pendiente.");
                return;
            }

            var intent = _pendingIntent!;
            var scope = _pendingScope!;
            var appKey = _pendingAppKey;

            Debug.WriteLine("[POLICY] EJECUTANDO ACCION PENDIENTE:");
            Debug.WriteLine(_pendingRawJson);

            if (!_localExecutor.TryExecute(intent, scope, appKey, out var msg))
            {
                await ResetAfterActionAsync(msg, "Acción no disponible", speak: msg);
                return;
            }

            // Actualizar contexto después de ejecutar
            _contextManager.AddToHistory(intent, appKey);
            if (intent.Equals("OpenApp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(appKey))
            {
                _contextManager.SetActiveApp(appKey);
            }
            else if (intent.Equals("CloseApp", StringComparison.OrdinalIgnoreCase))
            {
                _contextManager.ClearActiveApp();
            }

            await ResetAfterActionAsync(msg, msg, speak: msg);
        }

        /// <summary>Escuchar comando de voz (Home y segundo plano)</summary>
        private async Task ListenOnceAsync()
        {
            Debug.WriteLine("[STT] ListenOnceAsync start");

            if (!IsModelReady)
            {
                await ResetAfterActionAsync("Aún estoy cargando el modelo. Espera un momento.", "Cargando modelo...", speak: "Aún estoy cargando el modelo.");
                return;
            }

            if (!await EnsureSpeechReadyAsync())
            {
                await ResetAfterActionAsync("No pude inicializar el micrófono. Revisa permisos/dispositivo.", "Error micrófono", speak: "No pude inicializar el micrófono.");
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

                // Si cancelaron o cambió la sesión, NO seguimos
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

                // UI (solo Home)
                UpdateUiSafe("Texto detectado correctamente.", $"Entendí: {text}", recognized: text);

                // Confirmación en segundo plano
                if (_backgroundMode)
                {
                    await SpeakSafeAsync("Mensaje recibido.");
                }

                // Verificación antes de lógica pesada
                if (_cancelRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[VM] Cancel detectado post-texto -> no interpretar");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                // Si hay pending: este texto se usa para confirmar/cancelar
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

                var requestedFromSpeech = ExtractRequestedAppFromSpeech(text);
                Debug.WriteLine("[STT] requestedFromSpeech=" + (requestedFromSpeech ?? "<null>"));

                // CLASIFICACIÓN RÁPIDA (bypass IA para comandos obvios)
                var (fastHandled, fastResult) = _fastClassifier.TryFastClassify(text);
                if (fastHandled && fastResult != null)
                {
                    Debug.WriteLine($"[FAST] Clasificado sin IA: {fastResult.Intent} → {fastResult.AppKey}");

                    var fastValidation = _validator.Validate(fastResult, text);

                    if (!fastValidation.IsValid)
                    {
                        var fastMsg = fastValidation.Message ?? "Comando no válido";
                        await ResetAfterActionAsync(fastMsg, "Validación rechazada", speak: fastMsg);
                        return;
                    }

                    var fastRequiresConfirmation = RequiresConfirmation(fastResult.Intent, fastResult.Scope, fastResult.AppKey);
                    Debug.WriteLine($"[FAST] requires_confirmation={fastRequiresConfirmation}");

                    if (fastRequiresConfirmation)
                    {
                        // Guardar como pending y esperar confirmación
                        _pendingIntent = fastResult.Intent;
                        _pendingScope = fastResult.Scope;
                        _pendingAppKey = fastResult.AppKey;
                        _pendingRawJson = JsonSerializer.Serialize(fastResult);

                        IsListening = false;
                        ListenOnceCommand.NotifyCanExecuteChanged();

                        UpdateUiSafe(
                            $"Confirmación requerida para: {fastResult.Intent} {(fastResult.AppKey ?? "")}. Di 'confirmar' o 'cancelar'.",
                            "Confirmación requerida"
                        );

                        if (_backgroundMode)
                            await SpeakSafeAsync("Confirmación requerida. Di confirmar o cancelar.");

                        Debug.WriteLine("[FAST] Acción guardada como pending. Esperando confirmación...");
                        return;
                    }

                    // Si NO requiere confirmación, ejecutar directamente
                    if (!_localExecutor.TryExecute(fastResult.Intent, fastResult.Scope, fastResult.AppKey, out var fastExecMsg))
                    {
                        await ResetAfterActionAsync(fastExecMsg, "Error", speak: fastExecMsg);
                        return;
                    }

                    _contextManager.AddToHistory(fastResult.Intent, fastResult.AppKey);
                    if (fastResult.Intent.Equals("OpenApp", StringComparison.OrdinalIgnoreCase))
                        _contextManager.SetActiveApp(fastResult.AppKey!);
                    else if (fastResult.Intent.Equals("CloseApp", StringComparison.OrdinalIgnoreCase))
                        _contextManager.ClearActiveApp();

                    await ResetAfterActionAsync(fastExecMsg, fastExecMsg, speak: fastExecMsg);
                    return;
                }

                // Comando complejo -> requiere IA
                Debug.WriteLine("[IA] Comando complejo → InterpretRawAsync...");

                // Validación antes de llamar a IA
                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[IA] Cancel/sesion inválida antes de interpretar -> abortar");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                // VARIABLES DECLARADAS FUERA (para usarlas después del if/else)
                InterpretationResult parsedResult;
                string intent;
                string scope;
                string? appKey;

                // Verificar caché antes de llamar a IA
                if (_interpretationCache.TryGet(text, out var cachedResult) && cachedResult != null)
                {
                    Debug.WriteLine("[CACHE] Usando resultado cacheado");
                    parsedResult = cachedResult;
                    intent = cachedResult.Intent;
                    scope = cachedResult.Scope;
                    appKey = cachedResult.AppKey;
                }
                else
                {
                    Debug.WriteLine("[IA] Llamando a Ollama (no en caché)");
                    var ia = await _interpreter.InterpretRawAsync(text);

                    // Validación después de llamar a IA
                    if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                    {
                        Debug.WriteLine("[IA] Resultado IA ignorado por cancel/sesion nueva");
                        await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                        return;
                    }

                    Debug.WriteLine("===== OLLAMA PLAIN TEXT =====");
                    Debug.WriteLine(ia.PlainText);

                    Debug.WriteLine("===== OLLAMA JSON =====");
                    Debug.WriteLine(ia.Json);

                    using var doc = JsonDocument.Parse(ia.Json);
                    var root = doc.RootElement;

                    intent = root.TryGetProperty("intent", out var intentEl)
                        ? (intentEl.GetString() ?? "Unknown")
                        : "Unknown";

                    scope = root.TryGetProperty("scope", out var scopeEl)
                        ? (scopeEl.GetString() ?? "LOCAL")
                        : "LOCAL";

                    appKey = null;
                    if (root.TryGetProperty("app_key", out var appEl) && appEl.ValueKind != JsonValueKind.Null)
                        appKey = appEl.GetString();

                    Debug.WriteLine($"[IA] Parsed -> intent={intent}, scope={scope}, app_key={appKey}");

                    parsedResult = new InterpretationResult
                    {
                        Intent = intent,
                        Scope = scope,
                        AppKey = appKey,
                        Confidence = root.TryGetProperty("confidence", out var confEl) ? confEl.GetDouble() : 0.5
                    };

                    // Guardar en caché
                    _interpretationCache.Set(text, parsedResult);
                }

                // VALIDACIÓN CON IntentValidator (continúa igual)
                var validation = _validator.Validate(parsedResult, text);

                if (!validation.IsValid)
                {
                    var validationMsg = validation.Message ?? "Comando no válido";
                    if (!string.IsNullOrWhiteSpace(validation.SuggestAlternative))
                        validationMsg += " " + validation.SuggestAlternative;

                    await ResetAfterActionAsync(validationMsg, "Validación rechazada", speak: validationMsg);
                    return;
                }

                // Si fue inferido, usar resultado enriquecido
                if (validation.WasInferred && validation.EnrichedResult != null)
                {
                    appKey = validation.EnrichedResult.AppKey;
                    intent = validation.EnrichedResult.Intent;
                    Debug.WriteLine($"[Validator] Inferido: intent={intent}, app_key={appKey}");
                }

                // Anti-sustitución
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

                // Validación antes de ejecutar
                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[EXEC] Cancel/sesion inválida antes de ejecutar -> abortar");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                var requiresConfirmation = RequiresConfirmation(intent, scope, appKey);
                Debug.WriteLine($"[POLICY] requires_confirmation={requiresConfirmation}");

                if (requiresConfirmation)
                {
                    _pendingIntent = intent;
                    _pendingScope = scope;
                    _pendingAppKey = appKey;
                    _pendingRawJson = JsonSerializer.Serialize(parsedResult);

                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();

                    UpdateUiSafe(
                        $"Confirmación requerida para: {intent} {(appKey ?? "")}. Di 'confirmar' o 'cancelar'.",
                        "Confirmación requerida"
                    );

                    if (_backgroundMode)
                        await SpeakSafeAsync("Confirmación requerida. Di confirmar o cancelar.");

                    Debug.WriteLine("[POLICY] Acción guardada como pending. Esperando confirmación...");
                    return;
                }

                if (!_localExecutor.TryExecute(intent, scope, appKey, out var msg))
                {
                    await ResetAfterActionAsync(
                        msg + " " + AllowedAppsMessage(),
                        "Acción no disponible",
                        speak: msg
                    );
                    return;
                }

                // Actualizar contexto después de ejecutar
                _contextManager.AddToHistory(intent, appKey);
                if (intent.Equals("OpenApp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(appKey))
                {
                    _contextManager.SetActiveApp(appKey);
                }
                else if (intent.Equals("CloseApp", StringComparison.OrdinalIgnoreCase))
                {
                    _contextManager.ClearActiveApp();
                }

                await ResetAfterActionAsync(msg, msg, speak: msg);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[STT] OperationCanceledException");
                await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine("[STT] UnauthorizedAccessException: " + ex);
                _speechInitialized = false;
                await ResetAfterActionAsync(ex.Message, "ERROR: Permiso denegado", speak: "Permiso denegado para el micrófono.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VM] ERROR: " + ex);
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