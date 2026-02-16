// ViewModels/HomeViewModel.cs
using Anfeta.UI.Models;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Activity;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Services.Activity;

namespace Anfeta.UI.ViewModels
{
    /// <summary>
    /// ViewModel principal para la vista Home.
    /// Maneja reconocimiento de voz, interpretación de comandos y ejecución de acciones LOCAL y API.
    /// </summary>
    public class HomeViewModel : ObservableObject
    {
        private readonly ISpeechToTextService _speechService;
        private readonly ICommandInterpretationService _interpreter;
        private readonly ITextToSpeechService _tts;
        private readonly LocalActionExecutor _localExecutor;
        private readonly ApiActionExecutor _apiExecutor;
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

        private string? _pendingIntent;
        private string? _pendingScope;
        private string? _pendingAppKey;
        private string? _pendingProvider;
        private string? _pendingResource;
        private string? _pendingAction;
        private string? _pendingParamsJson;
        private string _pendingRawJson = "";

        // ===== FLUJO DE CREACIÓN DE ACTIVIDADES =====
        private readonly ActivityFieldExtractor _activityExtractor;
        private readonly ActivityFieldValidator _activityValidator;
        private readonly CorrectionCommandDetector _correctionDetector;
        private ActivityCreationFlow? _activityFlow;
        private bool _isInActivityCreation;

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
            ApiActionExecutor apiExecutor,
            ContextManager contextManager,
            IntentValidator validator,
            FastCommandClassifier fastClassifier,
            InterpretationCache interpretationCache,
            ActivityFieldExtractor activityExtractor,
            ActivityFieldValidator activityValidator,
            CorrectionCommandDetector correctionDetector)
        {
            _speechService = speechService;
            _interpreter = interpreter;
            _tts = tts;
            _localExecutor = localExecutor;
            _apiExecutor = apiExecutor;
            _contextManager = contextManager;
            _validator = validator;
            _fastClassifier = fastClassifier;
            _interpretationCache = interpretationCache;
            _activityExtractor = activityExtractor;
            _activityValidator = activityValidator;
            _correctionDetector = correctionDetector;

            // Inicializar flujo de actividades
            _activityFlow = new ActivityCreationFlow(
                _activityExtractor,
                _activityValidator,
                _correctionDetector);
            _isInActivityCreation = false;

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
            try { await ListenOnceAsync(); }
            finally { _backgroundMode = false; }
        }

        /// <summary>Limpiar acción pendiente</summary>
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

        /// <summary>Reset después de acción (con TTS)</summary>
        private async Task ResetAfterActionAsync(string infoMessage, string? statusText = null, string? speak = null)
        {
            ResetAfterAction(infoMessage, statusText);

            // TTS SIEMPRE para respuestas (no solo background)
            if (!string.IsNullOrWhiteSpace(speak))
                await SpeakSafeAsync(speak);
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

        /// <summary>Verificar si es inicio de creación de actividad</summary>
        private static bool IsCreateActivityCommand(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();
            return t.Contains("crear actividad") ||
                   t.Contains("crea actividad") ||
                   t.Contains("nueva actividad") ||
                   t == "crear tarea" ||
                   t == "nueva tarea";
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
                    infoMessage: "No pude conectar con el modelo. Revisa el servicio e intenta reiniciar la app.",
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

        /// <summary>
        /// Determina si una acción requiere confirmación explícita.
        /// LOCAL: Sin confirmación para apps seguras.
        /// API: Solo create/update/delete requieren confirmación.
        /// BROWSER: Sin confirmación.
        /// </summary>
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

        /// <summary>Extrae app solicitada del texto hablado (anti-sustitución)</summary>
        private string? ExtractRequestedAppFromSpeech(string speech)
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

        /// <summary>Ejecutar acción pendiente (LOCAL o API)</summary>
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
            var provider = _pendingProvider;
            var resource = _pendingResource;
            var action = _pendingAction;
            var paramsJson = _pendingParamsJson;

            Debug.WriteLine("[POLICY] EJECUTANDO ACCION PENDIENTE:");
            Debug.WriteLine(_pendingRawJson);

            if (string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
            {
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
                return;
            }

            if (string.Equals(scope, "API", StringComparison.OrdinalIgnoreCase))
            {
                var (ok, msg) = await _apiExecutor.ExecuteAsync(
                    provider,
                    resource,
                    action,
                    paramsJson ?? "{}",
                    CancellationToken.None
                );

                if (!ok)
                {
                    await ResetAfterActionAsync(msg, "API no disponible", speak: msg);
                    return;
                }

                // Actualizar contexto
                _contextManager.AddToHistory($"API:{resource}:{action}", null);

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

        /// <summary>Escuchar comando de voz (Home y segundo plano)</summary>
        private async Task ListenOnceAsync()
        {
            Debug.WriteLine("[STT] ListenOnceAsync start");

            // DETENER TTS SI ESTÁ HABLANDO
            try
            {
                _tts.Stop();
                Debug.WriteLine("[TTS] Detenido antes de escuchar");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TTS] Error al detener: {ex.Message}");
            }

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

                // ===== MANEJO DE FLUJO DE CREACIÓN DE ACTIVIDADES =====
                if (_isInActivityCreation && _activityFlow != null)
                {
                    Debug.WriteLine("[ACTIVITY_FLOW] Procesando respuesta en flujo de creación");

                    var (shouldContinue, message, readyData) = _activityFlow.ProcessResponse(text);

                    if (!shouldContinue)
                    {
                        // Flujo terminado (cancelado o listo para crear)
                        _isInActivityCreation = false;

                        if (readyData != null)
                        {
                            // Construir request para backend
                            var request = new CreateActividadRequest
                            {
                                Titulo = readyData.Titulo ?? "Sin título",
                                Prioridad = readyData.Prioridad,
                                // NO enviamos Status ni Tipo - el backend usa sus defaults
                                DueStart = readyData.DueStart?.ToString("o"),
                                DueEnd = readyData.DueEnd?.ToString("o")
                            };

                            // Ejecutar creación
                            var (ok, apiMsg) = await _apiExecutor.ExecuteAsync(
                                "weblab",
                                "actividades",
                                "create",
                                JsonSerializer.Serialize(request),
                                ct);

                            await ResetAfterActionAsync(apiMsg, ok ? "Actividad creada" : "Error", speak: apiMsg);
                            return;
                        }

                        // Flujo cancelado
                        await ResetAfterActionAsync(message, "Flujo cancelado", speak: message);
                        return;
                    }

                    // Flujo continúa - mostrar siguiente pregunta
                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();
                    UpdateUiSafe(message, "Creando actividad...");
                    await SpeakSafeAsync(message);
                    return;
                }

                // Detectar inicio de creación de actividad
                if (IsCreateActivityCommand(text))
                {
                    Debug.WriteLine("[ACTIVITY_FLOW] Iniciando flujo de creación");
                    _isInActivityCreation = true;

                    var startMessage = _activityFlow!.Start(text);

                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();
                    UpdateUiSafe(startMessage, "Creando actividad...");
                    await SpeakSafeAsync(startMessage);
                    return;
                }

                // ===== FLUJO NORMAL (código existente continúa igual) =====

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
                var requestedFromSpeech = ExtractRequestedAppFromSpeech(text);
                Debug.WriteLine("[STT] requestedFromSpeech=" + (requestedFromSpeech ?? "<null>"));

                // CLASIFICACIÓN RÁPIDA (bypass IA para comandos obvios)
                var (fastHandled, fastResult) = _fastClassifier.TryFastClassify(text);
                if (fastHandled && fastResult != null)
                {
                    Debug.WriteLine($"[FAST] Clasificado sin IA: {fastResult.Intent} → {fastResult.AppKey}");

                    // ✅ NUEVO: Manejo especial para CreateActivity
                    if (fastResult.Intent == "CreateActivity" && fastResult.Scope == "API")
                    {
                        Debug.WriteLine("[ACTIVITY_FLOW] Iniciando flujo de creación (desde FastClassifier)");
                        _isInActivityCreation = true;

                        var startMessage = _activityFlow!.Start(text);

                        IsListening = false;
                        ListenOnceCommand.NotifyCanExecuteChanged();
                        UpdateUiSafe(startMessage, "Creando actividad...");
                        await SpeakSafeAsync(startMessage);
                        return;
                    }

                    var fastRequiresConfirmation = RequiresConfirmation(fastResult.Scope, null);
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
                string? provider = null;
                string? resource = null;
                string? action = null;
                string? paramsJson = null;

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
                    Debug.WriteLine("[IA] Llamando a Groq/Ollama (no en caché)");
                    var ia = await _interpreter.InterpretRawAsync(text);

                    // Validación después de llamar a IA
                    if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                    {
                        Debug.WriteLine("[IA] Resultado IA ignorado por cancel/sesion nueva");
                        await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                        return;
                    }

                    Debug.WriteLine("===== IA PLAIN TEXT =====");
                    Debug.WriteLine(ia.PlainText);

                    Debug.WriteLine("===== IA JSON =====");
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

                    // Extraer campos API si existen
                    if (root.TryGetProperty("provider", out var providerEl) && providerEl.ValueKind != JsonValueKind.Null)
                        provider = providerEl.GetString();

                    if (root.TryGetProperty("resource", out var resourceEl) && resourceEl.ValueKind != JsonValueKind.Null)
                        resource = resourceEl.GetString();

                    if (root.TryGetProperty("action", out var actionEl) && actionEl.ValueKind != JsonValueKind.Null)
                        action = actionEl.GetString();

                    // Extraer params como JSON string
                    if (root.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
                    {
                        paramsJson = paramsEl.GetRawText();
                    }

                    Debug.WriteLine($"[IA] Parsed -> intent={intent}, scope={scope}, app_key={appKey}, provider={provider}, resource={resource}, action={action}");

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

                // VALIDACIÓN CON IntentValidator
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

                // Verificar si requiere confirmación
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
                    _pendingRawJson = JsonSerializer.Serialize(parsedResult);

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

                    Debug.WriteLine($"[POLICY] Acción pendiente guardada: {what}");
                    return;
                }

                // EJECUTAR ACCIÓN DIRECTAMENTE (sin confirmación)
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

                    // Actualizar contexto
                    _contextManager.AddToHistory($"API:{resource}:{action}", null);

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
            catch (Exception ex)
            {
                Debug.WriteLine("[VM] Error inesperado: " + ex);
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