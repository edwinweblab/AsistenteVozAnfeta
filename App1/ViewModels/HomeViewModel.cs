// ViewModels/HomeViewModel.cs
using Anfeta.UI.Models;
using Anfeta.UI.Models.Interpretation;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Activity;
using Anfeta.UI.Services.Groq;
using Anfeta.UI.Services.Interpretation;
using Anfeta.UI.Services.Speech;
using Anfeta.UI.Services.Weblab;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.ViewModels
{
    /// <summary>
    /// ViewModel principal para la vista Home.
    /// Maneja reconocimiento de voz, interpretación de comandos y ejecución de acciones LOCAL y API.
    /// </summary>
    public class HomeViewModel : ObservableObject, IDisposable
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
        private readonly ApiKeyService _apiKeyService;
        private readonly WeblabActividadesClient _actividadesClient;
        private readonly ActivitiesCacheService _activitiesCache;
        private readonly WeblabRecordatoriosClient _recordatoriosClient;
        private readonly WeblabReportesClient _reportesClient;
        private readonly SemaphoreSlim _warmupLock = new(1, 1);

        // ===== NUEVO: FLUJO DE EDICIÓN DE ACTIVIDADES =====
        private readonly ActivityEditFlow _activityEditFlow;

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
        private readonly WeblabUsersClient _usersClient;
        private ActivityCreationFlow? _activityFlow;
        private bool _isInActivityCreation;

        // ===== CACHÉ DE SELECCIÓN DE RECORDATORIOS =====
        // Se llena cuando el usuario lista sus recordatorios.
        // Se invalida tras cualquier mutación (create/update/delete/complete) y expira a los 5 min.
        private List<Recordatorio> _lastRecordatoriosList = new();
        private DateTime _lastRecordatoriosCacheTime = DateTime.MinValue;
        private static readonly TimeSpan RecordatoriosCacheTtl = TimeSpan.FromMinutes(5);

        // Estado para el flujo de edición: guarda el recordatorio seleccionado
        // mientras se espera el nuevo valor del usuario.
        private Recordatorio? _editingRecordatorio;

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
            CorrectionCommandDetector correctionDetector,
            WeblabUsersClient usersClient,
            ApiKeyService apiKeyService,
            ActivitiesCacheService activitiesCache,
            WeblabActividadesClient actividadesClient,
            WeblabRecordatoriosClient recordatoriosClient,
            WeblabReportesClient reportesClient,
            ActivityEditFlow activityEditFlow)
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
            _usersClient = usersClient;
            _recordatoriosClient = recordatoriosClient;
            _reportesClient = reportesClient;

            _apiKeyService = apiKeyService;
            _activitiesCache = activitiesCache;
            _actividadesClient = actividadesClient;

            _activityEditFlow = activityEditFlow;

            // Suscripción segura (para poder desuscribir en Dispose)
            _apiKeyService.KeysChanged += OnKeysChanged;

            _activityFlow = new ActivityCreationFlow(
                _activityExtractor,
                _activityValidator,
                _correctionDetector,
                _usersClient);
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

        public void Dispose()
        {
            _apiKeyService.KeysChanged -= OnKeysChanged;
            try { _currentRecognitionCts?.Cancel(); } catch { }
            try { _currentRecognitionCts?.Dispose(); } catch { }
            _currentRecognitionCts = null;
        }

        private bool CanListenOnce() => !IsListening && IsModelReady;

        /// <summary>Handler del evento KeysChanged</summary>
        private async void OnKeysChanged(object? sender, EventArgs e)
        {
            await RecheckModelAsync();
        }

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

        /// <summary>Refresca el cache de actividades del usuario actual</summary>
        private async Task RefreshActivitiesCacheAsync(CancellationToken ct)
        {
            try
            {
                var items = await _actividadesClient.GetMyActivitiesForCacheAsync(ct);

                if (items.Count > 0)
                {
                    _activitiesCache.SetActivities(items);

                    Debug.WriteLine($"[CACHE_ACTIVIDADES] Guardadas {items.Count} actividades.");
                    foreach (var a in items)
                    {
                        Debug.WriteLine($" - {a.Title} ({a.Id})");
                    }
                }
                else
                {
                    Debug.WriteLine("[CACHE_ACTIVIDADES] No se guardó nada.");
                    _activitiesCache.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CACHE_ACTIVIDADES] Error: " + ex.Message);
            }
        }

        /// <summary>
        /// Limpiar acción pendiente y estado de edición de recordatorio.
        /// Se llama en todo reset después de ejecutar o cancelar una acción.
        /// </summary>
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
            _editingRecordatorio = null;
        }

        /// <summary>
        /// Invalida el cache de recordatorios.
        /// Llamar después de cualquier mutación (create, update, delete, complete).
        /// </summary>
        private void InvalidateRecordatoriosCache()
        {
            _lastRecordatoriosList = new();
            _lastRecordatoriosCacheTime = DateTime.MinValue;
            Debug.WriteLine("[REC-SEL] Cache invalidado");
        }

        /// <summary>
        /// Actualiza el cache de recordatorios con una lista fresca.
        /// Centraliza la asignación para garantizar consistencia entre lista y timestamp.
        /// Entrada: lista de recordatorios obtenida del servidor.
        /// </summary>
        private void SetRecordatoriosCache(List<Recordatorio> list)
        {
            _lastRecordatoriosList = list;
            _lastRecordatoriosCacheTime = DateTime.Now;
            Debug.WriteLine($"[REC-SEL] Cache actualizado: {list.Count} recordatorios");
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

        /// <summary>Reset después de acción (sin TTS). Siempre limpia pending.</summary>
        private void ResetAfterAction(string infoMessage, string? statusText = null)
        {
            ClearPending();
            IsListening = false;

            UpdateUiSafe(infoMessage, statusText ?? "Listo para escuchar");
            ListenOnceCommand.NotifyCanExecuteChanged();

            Debug.WriteLine($"[VM] ResetAfterAction -> Status='{StatusText}' Info='{InfoMessage}'");
        }

        /// <summary>Reset después de acción (con TTS). Siempre limpia pending.</summary>
        private async Task ResetAfterActionAsync(string infoMessage, string? statusText = null, string? speak = null)
        {
            ResetAfterAction(infoMessage, statusText);

            if (!string.IsNullOrWhiteSpace(speak))
                await SpeakSafeAsync(speak);
        }

        /// <summary>
        /// Actualiza UI y habla SIN limpiar la acción pendiente.
        /// Usar cuando hay pending esperando confirmación del usuario.
        /// </summary>
        private async Task SpeakWithoutResetAsync(string uiMessage, string? statusText = null)
        {
            IsListening = false;
            ListenOnceCommand.NotifyCanExecuteChanged();
            UpdateUiSafe(uiMessage, statusText ?? StatusText);
            await SpeakSafeAsync(uiMessage);
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

        /// <summary>Verificar si es inicio de edición de actividad</summary>
        private static bool IsEditActivityCommand(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();

            return t.Contains("editar actividad") ||
                   t.Contains("edita actividad") ||
                   t.StartsWith("editar ") ||
                   t.StartsWith("edita ") ||
                   t.Contains("cambiar actividad") ||
                   t.Contains("cambia actividad") ||
                   t.Contains("modificar actividad") ||
                   t.Contains("modifica actividad");
        }

        /// <summary>
        /// Detecta si el texto es un comando de selección sobre la lista cacheada de recordatorios.
        /// Reconoce: "elimina el 2", "borra el primero", "completa el 3", "edita el segundo", etc.
        /// Entrada: texto hablado.
        /// Salida: (oneBasedIndex, action = "delete" | "complete" | "update") — false si no coincide.
        /// </summary>
        private static bool TryParseRecordatorioSelection(string text, out int oneBasedIndex, out string selAction)
        {
            oneBasedIndex = 0;
            selAction = "";

            var t = (text ?? "").Trim().ToLowerInvariant();

            if (!t.Contains("recordatorio"))
                return false;

            string action;
            if (t.Contains("elimina") || t.Contains("borra") || t.Contains("eliminar") || t.Contains("borrar"))
                action = "delete";
            else if (t.Contains("edita") || t.Contains("modifica") || t.Contains("editar") || t.Contains("modificar") || t.Contains("actualiza") || t.Contains("actualizar"))
                action = "update";
            else if (t.Contains("completa") || t.Contains("completar") || t.Contains("marca") || t.Contains("marcar"))
                action = "complete";
            else
                return false;

            var ordinals = new Dictionary<string, int>
            {
                ["primero"] = 1,
                ["primer"] = 1,
                ["primera"] = 1,
                ["segundo"] = 2,
                ["segunda"] = 2,
                ["tercero"] = 3,
                ["tercera"] = 3,
                ["tercer"] = 3,
                ["cuarto"] = 4,
                ["cuarta"] = 4,
                ["quinto"] = 5,
                ["quinta"] = 5,
                ["sexto"] = 6,
                ["sexta"] = 6,
                ["séptimo"] = 7,
                ["septimo"] = 7,
                ["octavo"] = 8,
                ["octava"] = 8,
                ["noveno"] = 9,
                ["novena"] = 9,
                ["décimo"] = 10,
                ["decimo"] = 10,
                ["uno"] = 1,
                ["dos"] = 2,
                ["tres"] = 3,
                ["cuatro"] = 4,
                ["cinco"] = 5,
                ["seis"] = 6,
                ["siete"] = 7,
                ["ocho"] = 8,
                ["nueve"] = 9,
                ["diez"] = 10
            };

            foreach (var kv in ordinals)
            {
                if (t.Contains(kv.Key))
                {
                    oneBasedIndex = kv.Value;
                    selAction = action;
                    return true;
                }
            }

            var match = System.Text.RegularExpressions.Regex.Match(t, @"\b(\d+)\b");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num) && num >= 1 && num <= 10)
            {
                oneBasedIndex = num;
                selAction = action;
                return true;
            }

            return false;
        }

        /// <summary>Warmup del modelo IA</summary>
        private async Task WarmupModelAsync()
        {
            UpdateUiSafe("Revisando conexión con el modelo...", "Revisando...");
            IsModelReady = false;
            ListenOnceCommand.NotifyCanExecuteChanged();

            try
            {
                Debug.WriteLine("[IA] Warmup start: InterpretRawAsync('ping')");
                await _interpreter.InterpretRawAsync("ping");

                IsModelReady = true;
                UpdateUiSafe("Modelo listo. Presiona el micrófono y habla.", "Listo para escuchar");
                ListenOnceCommand.NotifyCanExecuteChanged();
                Debug.WriteLine("[IA] Warmup OK -> modelo listo");
            }
            catch (Exception ex)
            {
                IsModelReady = false;
                UpdateUiSafe("No pude conectar con el modelo. Revisa tu API key y vuelve a intentar.", "Modelo no disponible");
                ListenOnceCommand.NotifyCanExecuteChanged();
                Debug.WriteLine("[IA] Warmup ERROR: " + ex);
            }
        }

        public async Task RecheckModelAsync()
        {
            await _warmupLock.WaitAsync();
            try { await WarmupModelAsync(); }
            finally { _warmupLock.Release(); }
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
                    UpdateUiSafe("No hay idiomas instalados. Ve a Configuración de Windows → Idioma → Reconocimiento de voz.", "Error: No hay idiomas instalados");
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
        /// LOCAL y BROWSER: sin confirmación.
        /// API: solo create/update/delete requieren confirmación.
        /// </summary>
        private static bool RequiresConfirmation(string scope, string? action)
        {
            if (string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(scope, "BROWSER", StringComparison.OrdinalIgnoreCase)) return false;

            if (string.Equals(scope, "API", StringComparison.OrdinalIgnoreCase))
            {
                var a = (action ?? "").Trim().ToLowerInvariant();
                return a == "create" || a == "update" || a == "delete";
            }

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
            if (t.Contains("bloc de notas") || t.Contains("bloc") || t.Contains("notepad")) return "bloc";
            if (t.Contains("explorador") || t.Contains("archivos") || t.Contains("file explorer")) return "explorador";

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

        /// <summary>
        /// Ejecutar acción pendiente (LOCAL o API).
        /// Invalida el cache de recordatorios si la acción fue una mutación.
        /// </summary>
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

                _contextManager.AddToHistory(intent, appKey);
                if (intent.Equals("OpenApp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(appKey))
                    _contextManager.SetActiveApp(appKey);
                else if (intent.Equals("CloseApp", StringComparison.OrdinalIgnoreCase))
                    _contextManager.ClearActiveApp();

                await ResetAfterActionAsync(msg, msg, speak: msg);
                return;
            }

            if (string.Equals(scope, "API", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(resource, "recordatorios", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
                {
                    (ApiPlainResponse listResp, List<Recordatorio> listData) = await Task.Run(() =>
                        _recordatoriosClient.GetMyRecordatoriosWithListAsync("all", CancellationToken.None));

                    SetRecordatoriosCache(listData);

                    _contextManager.AddToHistory("API:recordatorios:list", null);
                    await ResetAfterActionAsync(listResp.PlainText, listResp.Ok ? "Listo." : "Error", speak: listResp.PlainText);
                    return;
                }

                (bool ok, string msg) = await _apiExecutor.ExecuteAsync(provider, resource, action, paramsJson ?? "{}", CancellationToken.None);

                if (!ok)
                {
                    await ResetAfterActionAsync(msg, "API no disponible", speak: msg);
                    return;
                }

                if (string.Equals(resource, "recordatorios", StringComparison.OrdinalIgnoreCase) &&
                    action is "create" or "update" or "delete" or "complete")
                    InvalidateRecordatoriosCache();

                if (string.Equals(provider, "weblab", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(resource, "actividades", StringComparison.OrdinalIgnoreCase) &&
                    action is "list" or "update" or "create")
                {
                    await RefreshActivitiesCacheAsync(CancellationToken.None);
                }

                _contextManager.AddToHistory($"API:{resource}:{action}", null);
                await ResetAfterActionAsync(msg, "Listo.", speak: msg);
                return;
            }

            await ResetAfterActionAsync("Acción pendiente no soportada.", "No soportado", speak: "Acción pendiente no soportada.");
        }

        /// <summary>Escuchar comando de voz (Home y segundo plano)</summary>
        private async Task ListenOnceAsync()
        {
            Debug.WriteLine("[STT] ListenOnceAsync start");

            try
            {
                _tts.Stop();
                Debug.WriteLine("[TTS] Detenido antes de escuchar");
            }
            catch (GroqRateLimitException ex)
            {
                Debug.WriteLine("[GROQ] Rate limit: " + ex.Message);
                await ResetAfterActionAsync(
                    "El servicio de IA está saturado. Espera unos segundos e intenta de nuevo.",
                    "Rate limit Groq",
                    speak: "El servicio de inteligencia está saturado. Intenta en unos segundos.");
            }
            catch (Exception ex) { Debug.WriteLine($"[TTS] Error al detener: {ex.Message}"); }

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

            if (IsListening)
            {
                Debug.WriteLine("[STT] Ya estaba escuchando -> cancelar");
                await CancelListeningAsync("Escucha cancelada. Puedes intentar de nuevo.", "Cancelado");
                return;
            }

            _cancelRequested = false;
            var mySession = Interlocked.Increment(ref _listenSessionId);

            IsListening = true;
            UpdateUiSafe("Preparando micrófono...", "Preparando...", recognized: "");

            _currentRecognitionCts = new CancellationTokenSource();
            var ct = _currentRecognitionCts.Token;

            try
            {
                Debug.WriteLine("[STT] RecognizeOnceAsync...");
                var text = await _speechService.RecognizeOnceAsync(ct, onReady: () =>
                {
                    UpdateUiSafe("Escuchando... habla ahora", "Escuchando... habla ahora", recognized: "");
                    if (_backgroundMode)
                        _ = SpeakSafeAsync("Te escucho.");
                });
                Debug.WriteLine("------------------------------------");
                Debug.WriteLine("[STT] TEXTO: " + (text ?? "<null>"));
                Debug.WriteLine("------------------------------------");

                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[STT] Resultado ignorado por cancel/sesion nueva");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    if (HasPending())
                    {
                        await SpeakWithoutResetAsync("No te escuché. Hay una acción pendiente, di confirmar o cancelar.");
                        return;
                    }

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

                // ===== FLUJO DE CREACIÓN DE ACTIVIDADES =====
                if (_isInActivityCreation && _activityFlow != null)
                {
                    Debug.WriteLine("[ACTIVITY_FLOW] Procesando respuesta en flujo de creación");
                    var (shouldContinue, message, readyData) = _activityFlow.ProcessResponse(text);

                    if (!shouldContinue)
                    {
                        _isInActivityCreation = false;

                        if (readyData != null)
                        {
                            var request = new CreateActividadRequest
                            {
                                Titulo = readyData.Titulo ?? "Sin título",
                                Prioridad = readyData.Prioridad,
                                DueStart = readyData.DueStart?.ToString("o"),
                                DueEnd = readyData.DueEnd?.ToString("o"),
                                Assignees = readyData.Assignees
                            };

                            var (ok, apiMsg) = await _apiExecutor.ExecuteAsync("weblab", "actividades", "create", JsonSerializer.Serialize(request), ct);

                            if (ok)
                                await RefreshActivitiesCacheAsync(ct);

                            await ResetAfterActionAsync(apiMsg, ok ? "Actividad creada" : "Error", speak: apiMsg);
                            return;
                        }

                        await ResetAfterActionAsync(message, "Flujo cancelado", speak: message);
                        return;
                    }

                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();
                    UpdateUiSafe(message, "Creando actividad...");
                    await SpeakSafeAsync(message);
                    return;
                }

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

                // ===== FLUJO DE EDICIÓN DE ACTIVIDADES =====
                // Va ANTES del confirm/cancel global para que "confirmar" y "cancelar"
                // se consuman dentro del flujo de edición cuando está activo.
                if (_activityEditFlow.IsActive)
                {
                    Debug.WriteLine("[ACTIVITY_EDIT_FLOW] Procesando respuesta");
                    var editResult = _activityEditFlow.ProcessResponse(text);
                    Debug.WriteLine("===== PATCH DEBUG =====");
                    Debug.WriteLine($"Titulo: {editResult.Patch?.Titulo}");
                    Debug.WriteLine($"Status: {editResult.Patch?.Status}");
                    Debug.WriteLine($"Prioridad: {editResult.Patch?.Prioridad}");
                    Debug.WriteLine($"DueStart: {editResult.Patch?.DueStart}");
                    Debug.WriteLine($"DueEnd: {editResult.Patch?.DueEnd}");
                    Debug.WriteLine($"Anotaciones: {editResult.Patch?.Anotaciones}");
                    Debug.WriteLine($"PasosYLinks: {editResult.Patch?.PasosYLinks}");
                    Debug.WriteLine("=======================");
                    if (editResult.Continue)
                    {
                        IsListening = false;
                        ListenOnceCommand.NotifyCanExecuteChanged();
                        UpdateUiSafe(editResult.Message, "Editando actividad...");
                        await SpeakSafeAsync(editResult.Message);
                        return;
                    }

                    if (editResult.Activity != null && editResult.Patch != null)
                    {

                        var payload = new
                        {
                            id = editResult.Activity.Id,
                            titulo = editResult.Patch.Titulo,
                            status = editResult.Patch.Status,
                            prioridad = editResult.Patch.Prioridad,
                            dueStart = editResult.Patch.DueStart,
                            dueEnd = editResult.Patch.DueEnd,
                            anotaciones = editResult.Patch.Anotaciones,
                            pasosYLinks = editResult.Patch.PasosYLinks
                        };

                        var updateParamsJson = JsonSerializer.Serialize(payload);
                        Debug.WriteLine("===== UPDATE JSON FINAL =====");
                        Debug.WriteLine(updateParamsJson);
                        Debug.WriteLine("=============================");
                        if (updateParamsJson == "{}")
                        {
                            await ResetAfterActionAsync(
                                "No detecté ningún cambio para actualizar.",
                                "Sin cambios",
                                speak: "No detecté ningún cambio para actualizar.");
                            return;
                        }

                        var (ok, msg) = await _apiExecutor.ExecuteAsync("weblab", "actividades", "update", updateParamsJson, ct);

                        if (ok)
                            await RefreshActivitiesCacheAsync(ct);

                        await ResetAfterActionAsync(msg, ok ? "Actividad actualizada" : "Error", speak: msg);
                        return;
                    }

                    await ResetAfterActionAsync(editResult.Message, "Edición cancelada", speak: editResult.Message);
                    return;
                }

                if (IsEditActivityCommand(text))
                {
                    Debug.WriteLine("[ACTIVITY_EDIT_FLOW] Iniciando flujo de edición");

                    if (!_activitiesCache.HasData())
                        await RefreshActivitiesCacheAsync(ct);

                    var startEditMessage = _activityEditFlow.Start(text);

                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();
                    UpdateUiSafe(startEditMessage, "Editando actividad...");
                    await SpeakSafeAsync(startEditMessage);
                    return;
                }

                // ===== CONFIRMAR / CANCELAR GLOBAL =====
                if (IsConfirmationPhrase(text))
                {
                    if (HasPending())
                    {
                        await ExecutePendingIfAnyAsync();
                        return;
                    }
                    await ResetAfterActionAsync("No hay ninguna acción pendiente.", "Listo para escuchar", speak: "No hay ninguna acción pendiente.");
                    return;
                }

                if (IsCancelPhrase(text))
                {
                    if (HasPending() || _editingRecordatorio != null)
                    {
                        _editingRecordatorio = null;
                        await ResetAfterActionAsync("Acción cancelada.", "Cancelado", speak: "Acción cancelada.");
                        return;
                    }
                    await ResetAfterActionAsync("No hay nada que cancelar.", "Listo para escuchar", speak: "No hay nada que cancelar.");
                    return;
                }

                // ===== PENDING NO RESUELTO — RECORDATORIO AL USUARIO =====
                if (HasPending())
                {
                    Debug.WriteLine("[POLICY] Hay pending no resuelto. Texto: " + text);
                    await SpeakWithoutResetAsync("Hay una acción pendiente. Di confirmar para ejecutar o cancelar para abortar.", "Confirmación requerida");
                    return;
                }

                // ===== FLUJO DE EDICIÓN DE RECORDATORIO =====
                if (_editingRecordatorio != null)
                {
                    Debug.WriteLine($"[REC-SEL] Capturando nuevo valor para edición. Recordatorio: '{_editingRecordatorio.Mensaje}'");

                    var editId = _editingRecordatorio.Id;
                    var editMensaje = _editingRecordatorio.Mensaje;
                    _editingRecordatorio = null;

                    var (parsedDate, cleanMensaje) = SpanishDateParser.TryParse(text);

                    object updateParams;
                    if (parsedDate.HasValue && !string.IsNullOrWhiteSpace(cleanMensaje))
                        updateParams = new { id = editId, mensaje = cleanMensaje, fechaHora = parsedDate.Value.ToString("yyyy-MM-ddTHH:mm:ss-06:00") };
                    else if (parsedDate.HasValue)
                        updateParams = new { id = editId, fechaHora = parsedDate.Value.ToString("yyyy-MM-ddTHH:mm:ss-06:00") };
                    else
                        updateParams = new { id = editId, mensaje = text };

                    _pendingIntent = "ApiCall";
                    _pendingScope = "API";
                    _pendingProvider = "weblab";
                    _pendingResource = "recordatorios";
                    _pendingAction = "update";
                    _pendingParamsJson = JsonSerializer.Serialize(updateParams);
                    _pendingRawJson = $"{{\"resource\":\"recordatorios\",\"action\":\"update\",\"id\":\"{editId}\"}}";

                    var editConfirm = $"¿Confirmas actualizar '{editMensaje}' con: {text}?";
                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();
                    UpdateUiSafe(editConfirm, "Confirmación requerida");
                    await SpeakSafeAsync(editConfirm);

                    Debug.WriteLine($"[REC-SEL] Pending update guardado para id={editId}");
                    return;
                }

                // ===== DRILL-DOWN DE REVISIONES (desde caché) =====
                if (TryParseRevisionesDetail(text, out var revBucket))
                {
                    Debug.WriteLine($"[REV-DETAIL] Drill-down detectado: bucket={revBucket}");

                    var detail = _reportesClient.GetRevisionesDetail(revBucket);

                    if (!detail.Ok)
                    {
                        await ResetAfterActionAsync(detail.PlainText, "Sin datos", speak: detail.PlainText);
                        return;
                    }

                    _contextManager.AddToHistory($"API:reportes:detalle:{revBucket}", null);
                    await ResetAfterActionAsync(detail.PlainText, "Listo.", speak: detail.PlainText);
                    return;
                }

                // ===== SELECCIÓN DE RECORDATORIO — AUTO-FETCH SI LA LISTA ESTÁ VACÍA =====
                if (_lastRecordatoriosList.Count == 0 && TryParseRecordatorioSelection(text, out _, out _))
                {
                    Debug.WriteLine("[REC-SEL] Lista vacía pero hay selección → auto-fetch");

                    var (autoResponse, autoList) = await Task.Run(() =>
                        _recordatoriosClient.GetMyRecordatoriosWithListAsync("all", ct));

                    if (!autoResponse.Ok || autoList.Count == 0)
                    {
                        await ResetAfterActionAsync(autoResponse.PlainText, "Sin recordatorios", speak: autoResponse.PlainText);
                        return;
                    }

                    SetRecordatoriosCache(autoList);
                }

                // ===== EXPIRAR CACHE POR TTL =====
                if (_lastRecordatoriosList.Count > 0 && DateTime.Now - _lastRecordatoriosCacheTime > RecordatoriosCacheTtl)
                {
                    Debug.WriteLine("[REC-SEL] Cache expirado por TTL, invalidando");
                    InvalidateRecordatoriosCache();
                }

                // ===== SELECCIÓN DE RECORDATORIO POR ÍNDICE =====
                if (_lastRecordatoriosList.Count > 0 && TryParseRecordatorioSelection(text, out var selIndex, out var selAction))
                {
                    Debug.WriteLine($"[REC-SEL] Selección detectada: {selAction} índice {selIndex} de {_lastRecordatoriosList.Count}");

                    var idx = selIndex - 1;
                    if (idx < 0 || idx >= _lastRecordatoriosList.Count)
                    {
                        await SpeakWithoutResetAsync(
                            $"No existe el número {selIndex}. Tienes {_lastRecordatoriosList.Count} recordatorios.",
                            "Índice inválido");
                        return;
                    }

                    var selected = _lastRecordatoriosList[idx];
                    var localTime = selected.FechaHora.ToLocalTime();
                    var fecha = localTime.Date == DateTime.Today
                        ? "hoy"
                        : localTime.Date == DateTime.Today.AddDays(1)
                            ? "mañana"
                            : localTime.ToString("dd 'de' MMMM");

                    if (selAction == "update")
                    {
                        _editingRecordatorio = selected;

                        IsListening = false;
                        ListenOnceCommand.NotifyCanExecuteChanged();
                        var editMsg = $"¿Qué deseas cambiar en '{selected.Mensaje}'? Di el nuevo mensaje o la nueva fecha y hora.";
                        UpdateUiSafe(editMsg, "Editando recordatorio...");
                        await SpeakSafeAsync(editMsg);

                        Debug.WriteLine($"[REC-SEL] Edición iniciada para id={selected.Id}, mensaje='{selected.Mensaje}'");
                        return;
                    }

                    _pendingIntent = "ApiCall";
                    _pendingScope = "API";
                    _pendingProvider = "weblab";
                    _pendingResource = "recordatorios";
                    _pendingAction = selAction;
                    _pendingParamsJson = JsonSerializer.Serialize(new { id = selected.Id });
                    _pendingRawJson = $"{{\"resource\":\"recordatorios\",\"action\":\"{selAction}\",\"id\":\"{selected.Id}\"}}";

                    var actionLabel = selAction == "delete" ? "eliminar" : "marcar como completado";
                    var confirmMsg = $"¿Seguro que deseas {actionLabel} el recordatorio {selIndex}: '{selected.Mensaje}' del {fecha} a las {localTime:HH:mm}?";

                    IsListening = false;
                    ListenOnceCommand.NotifyCanExecuteChanged();
                    UpdateUiSafe(confirmMsg, "Confirmación requerida");
                    await SpeakSafeAsync(confirmMsg);

                    Debug.WriteLine($"[REC-SEL] Pending {selAction} guardado → id={selected.Id}, mensaje='{selected.Mensaje}'");
                    return;
                }

                var requestedFromSpeech = ExtractRequestedAppFromSpeech(text);
                Debug.WriteLine("[STT] requestedFromSpeech=" + (requestedFromSpeech ?? "<null>"));

                // ===== CLASIFICACIÓN RÁPIDA (bypass IA) =====
                var (fastHandled, fastResult) = _fastClassifier.TryFastClassify(text);
                if (fastHandled && fastResult != null)
                {
                    Debug.WriteLine($"[FAST] Clasificado sin IA: {fastResult.Intent} → {fastResult.AppKey}");

                    if (fastResult.Intent == "CreateRecordatorio")
                    {
                        Debug.WriteLine("[FAST] CreateRecordatorio → delegando a IA");
                        goto HandleWithAI;
                    }

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

                    var fastRequiresConfirmation = RequiresConfirmation(fastResult.Scope, fastResult.Action);
                    Debug.WriteLine($"[FAST] requires_confirmation={fastRequiresConfirmation}");

                    if (fastRequiresConfirmation)
                    {
                        _pendingIntent = fastResult.Intent;
                        _pendingScope = fastResult.Scope;
                        _pendingAppKey = fastResult.AppKey;
                        _pendingProvider = fastResult.Provider;
                        _pendingResource = fastResult.Resource;
                        _pendingAction = fastResult.Action;
                        _pendingRawJson = JsonSerializer.Serialize(fastResult);

                        IsListening = false;
                        ListenOnceCommand.NotifyCanExecuteChanged();
                        UpdateUiSafe($"Confirmación requerida para: {fastResult.Intent}. Di 'confirmar' o 'cancelar'.", "Confirmación requerida");

                        if (_backgroundMode)
                            await SpeakSafeAsync("Confirmación requerida. Di confirmar o cancelar.");

                        Debug.WriteLine("[FAST] Acción API guardada como pending. Esperando confirmación...");
                        return;
                    }

                    if (string.Equals(fastResult.Scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
                    {
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

                    if (string.Equals(fastResult.Scope, "API", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(fastResult.Resource, "recordatorios", StringComparison.OrdinalIgnoreCase) &&
                            fastResult.Action is "list" or "today" or "tomorrow" or "pending")
                        {
                            var filter = fastResult.Action switch
                            {
                                "today" => "today",
                                "tomorrow" => "tomorrow",
                                "pending" => "pending",
                                _ => "all"
                            };

                            Debug.WriteLine($"[REC-SEL] Interceptando lista recordatorios (filter={filter})");

                            var (listResponse, list) = await Task.Run(() =>
                                _recordatoriosClient.GetMyRecordatoriosWithListAsync(filter, ct));

                            SetRecordatoriosCache(list);

                            _contextManager.AddToHistory("API:recordatorios:list", null);
                            await ResetAfterActionAsync(listResponse.PlainText, listResponse.Ok ? "Listo." : "Error", speak: listResponse.PlainText);
                            return;
                        }

                        string? fastParamsJson = null;
                        if (fastResult.Params?.Count > 0)
                            fastParamsJson = JsonSerializer.Serialize(fastResult.Params);

                        var (fastOk, fastMsg) = await _apiExecutor.ExecuteAsync(fastResult.Provider, fastResult.Resource, fastResult.Action, fastParamsJson, ct);

                        if (fastOk &&
                            string.Equals(fastResult.Provider, "weblab", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(fastResult.Resource, "actividades", StringComparison.OrdinalIgnoreCase) &&
                            fastResult.Action is "list" or "update" or "create")
                        {
                            await RefreshActivitiesCacheAsync(ct);
                        }

                        _contextManager.AddToHistory($"API:{fastResult.Resource}:{fastResult.Action}", null);
                        await ResetAfterActionAsync(fastMsg, fastOk ? "Listo." : "Error", speak: fastMsg);
                        return;
                    }
                }

            // ===== IA =====
            HandleWithAI:
                Debug.WriteLine("[IA] Comando complejo → InterpretRawAsync...");

                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[IA] Cancel/sesion inválida antes de interpretar -> abortar");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                InterpretationResult parsedResult;
                string intent;
                string scope;
                string? appKey;
                string? provider = null;
                string? resource = null;
                string? action = null;
                string? paramsJson = null;

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

                    intent = root.TryGetProperty("intent", out var intentEl) ? (intentEl.GetString() ?? "Unknown") : "Unknown";
                    scope = root.TryGetProperty("scope", out var scopeEl) ? (scopeEl.GetString() ?? "LOCAL") : "LOCAL";
                    appKey = null;

                    if (root.TryGetProperty("app_key", out var appEl) && appEl.ValueKind != JsonValueKind.Null) appKey = appEl.GetString();
                    if (root.TryGetProperty("provider", out var providerEl) && providerEl.ValueKind != JsonValueKind.Null) provider = providerEl.GetString();
                    if (root.TryGetProperty("resource", out var resourceEl) && resourceEl.ValueKind != JsonValueKind.Null) resource = resourceEl.GetString();
                    if (root.TryGetProperty("action", out var actionEl) && actionEl.ValueKind != JsonValueKind.Null) action = actionEl.GetString();

                    if (root.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
                        paramsJson = paramsEl.GetRawText();

                    Debug.WriteLine($"[IA] Parsed -> intent={intent}, scope={scope}, app_key={appKey}, provider={provider}, resource={resource}, action={action}");

                    parsedResult = new InterpretationResult
                    {
                        Intent = intent,
                        Scope = scope,
                        AppKey = appKey,
                        Confidence = root.TryGetProperty("confidence", out var confEl) ? confEl.GetDouble() : 0.5
                    };

                    if (!string.Equals(scope, "API", StringComparison.OrdinalIgnoreCase))
                        _interpretationCache.Set(text, parsedResult);
                }

                var validation = _validator.Validate(parsedResult, text);

                if (!validation.IsValid)
                {
                    var validationMsg = validation.Message ?? "Comando no válido";
                    if (!string.IsNullOrWhiteSpace(validation.SuggestAlternative))
                        validationMsg += " " + validation.SuggestAlternative;

                    await ResetAfterActionAsync(validationMsg, "Validación rechazada", speak: validationMsg);
                    return;
                }

                if (validation.WasInferred && validation.EnrichedResult != null)
                {
                    appKey = validation.EnrichedResult.AppKey;
                    intent = validation.EnrichedResult.Intent;
                    Debug.WriteLine($"[Validator] Inferido: intent={intent}, app_key={appKey}");
                }

                if (!string.IsNullOrWhiteSpace(requestedFromSpeech) &&
                    string.Equals(intent, "OpenApp", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(appKey) &&
                    !requestedFromSpeech.Equals(appKey, StringComparison.OrdinalIgnoreCase))
                {
                    await ResetAfterActionAsync($"Pediste '{requestedFromSpeech}', pero interpreté '{appKey}'. No ejecutaré nada.", "Acción no disponible", speak: "No ejecutaré nada porque no coincide lo que pediste.");
                    return;
                }

                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[EXEC] Cancel/sesion inválida antes de ejecutar -> abortar");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
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

                    UpdateUiSafe($"Confirmación requerida para: {what}. Di 'confirmar' o 'cancelar'.", "Confirmación requerida");

                    if (_backgroundMode)
                        await SpeakSafeAsync("Confirmación requerida. Di confirmar o cancelar.");

                    Debug.WriteLine($"[POLICY] Acción pendiente guardada: {what}");
                    return;
                }

                if (string.Equals(scope, "LOCAL", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_localExecutor.TryExecute(intent, scope, appKey, out var msg))
                    {
                        await ResetAfterActionAsync(msg + " " + AllowedAppsMessage(), "Acción no disponible", speak: msg);
                        return;
                    }

                    _contextManager.AddToHistory(intent, appKey);
                    if (intent.Equals("OpenApp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(appKey))
                        _contextManager.SetActiveApp(appKey);
                    else if (intent.Equals("CloseApp", StringComparison.OrdinalIgnoreCase))
                        _contextManager.ClearActiveApp();

                    await ResetAfterActionAsync(msg, msg, speak: msg);
                    return;
                }

                if (string.Equals(scope, "API", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(resource, "recordatorios", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
                    {
                        (ApiPlainResponse listResp, List<Recordatorio> listData) = await Task.Run(() =>
                            _recordatoriosClient.GetMyRecordatoriosWithListAsync("all", ct));

                        SetRecordatoriosCache(listData);

                        _contextManager.AddToHistory("API:recordatorios:list", null);
                        await ResetAfterActionAsync(listResp.PlainText, listResp.Ok ? "Listo." : "Error", speak: listResp.PlainText);
                        return;
                    }

                    var (ok, msg) = await _apiExecutor.ExecuteAsync(provider, resource, action, paramsJson ?? "{}", ct);

                    if (!ok)
                    {
                        await ResetAfterActionAsync(msg, "API no disponible", speak: msg);
                        return;
                    }

                    if (string.Equals(provider, "weblab", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(resource, "actividades", StringComparison.OrdinalIgnoreCase) &&
                        action is "list" or "update" or "create")
                    {
                        await RefreshActivitiesCacheAsync(ct);
                    }

                    if (string.Equals(resource, "recordatorios", StringComparison.OrdinalIgnoreCase) &&
                        action is "create" or "update" or "delete" or "complete")
                        InvalidateRecordatoriosCache();

                    _contextManager.AddToHistory($"API:{resource}:{action}", null);
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

        /// <summary>
        /// Detecta si el texto es un comando de drill-down sobre revisiones cacheadas.
        /// Requiere palabra de acción + bucket — sin "recordatorio".
        /// Salida: bucket = "pendientes" | "terminadas" | "confirmadas" | "todas"
        /// </summary>
        private static bool TryParseRevisionesDetail(string text, out string bucket)
        {
            bucket = "";
            var t = (text ?? "").Trim().ToLowerInvariant();

            if (t.Contains("recordatorio")) return false;

            var actionWords = new[] { "muéstrame", "muestrame", "ver", "dame",
                                      "cuáles", "cuales", "lista", "muestra", "dime" };
            bool hasAction = false;
            foreach (var w in actionWords)
                if (t.Contains(w)) { hasAction = true; break; }

            if (!hasAction) return false;

            if (t.Contains("pendiente")) { bucket = "pendientes"; return true; }
            if (t.Contains("terminada")) { bucket = "terminadas"; return true; }
            if (t.Contains("confirmada")) { bucket = "confirmadas"; return true; }

            if ((t.Contains("todas") || t.Contains("todo")) &&
                (t.Contains("revision") || t.Contains("revisión")))
            {
                bucket = "todas";
                return true;
            }

            return false;
        }
    }
}