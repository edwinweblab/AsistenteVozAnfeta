// ===============================
// HomeViewModel.cs (COMPLETO)
// - Gate de “modelo listo” (warmup) antes de permitir escuchar
// - Micrófono LAZY: se inicializa SOLO si se necesita (botón)
// - Cancelación real: invalida sesión + StopRecognitionAsync via CancelAsync()
// - Evita interpretar después de cancelar (aunque llegue texto tarde)
// - Confirmación pendiente por voz
// - Anti-sustitución SOLO para tus 4 apps locales (chrome/calculadora/bloc/explorador)
// - Siempre re-habilita el botón con NotifyCanExecuteChanged()
//
// + COMPATIBLE CON SEGUNDO PLANO:
//   - Agrega TriggerVoiceFromHotkeyAsync()
//   - Modo background: responde por VOZ (TTS) y no depende de UI
//   - Reutiliza exactamente el MISMO flujo (pending/confirm/cancel incluidos)
//   - CONFIRMA "mensaje recibido" al captar voz en segundo plano
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
        private readonly ITextToSpeechService _tts;
        private readonly LocalActionExecutor _localExecutor = new();

        private CancellationTokenSource? _currentRecognitionCts;

        // ====== control de sesiones para ignorar resultados tardíos ======
        private int _listenSessionId = 0;
        private volatile bool _cancelRequested = false;

        // ====== modo ejecución (Home UI vs Segundo plano) ======
        private bool _backgroundMode = false;

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

        public HomeViewModel(
            ISpeechToTextService speechService,
            ICommandInterpretationService interpreter,
            ITextToSpeechService tts)
        {
            _speechService = speechService;
            _interpreter = interpreter;
            _tts = tts;

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

        // ===============================
        // ENTRYPOINT PARA SEGUNDO PLANO (HOTKEY)
        // ===============================
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

        // ===============================
        // Helpers
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

        private async Task SpeakSafeAsync(string text)
        {
            try { await _tts.SpeakAsync(text); }
            catch (Exception ex) { Debug.WriteLine("[TTS] ERROR: " + ex); }
        }

        private void UpdateUiSafe(string infoMessage, string? statusText = null, string? recognized = null)
        {
            // En segundo plano no dependemos de UI
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

        // ===============================
        // Init Speech (botón en Home)
        // ===============================
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

        // ===============================
        // POLICY confirmación
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
        // Anti-sustitución SOLO para 4 apps
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

            await ResetAfterActionAsync(uiMessage, uiStatus, speak: uiMessage);
        }

        // ===============================
        // Confirmación pendiente
        // ===============================
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

            await ResetAfterActionAsync(msg, msg, speak: msg);
        }

        // ===============================
        // LISTEN ONCE (Home y Segundo plano)
        // ===============================
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

                // CONFIRMACION EN SEGUNDO PLANO
                if (_backgroundMode)
                {
                    // Solo confirmar:
                    await SpeakSafeAsync("Mensaje recibido.");

                    // Si quieres repetir lo que entendió, reemplaza por:
                    // await SpeakSafeAsync($"Mensaje recibido: {text}");
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

                // Antes de interpretar, valida cancel/sesion inválida
                if (_cancelRequested || ct.IsCancellationRequested || mySession != _listenSessionId)
                {
                    Debug.WriteLine("[IA] Cancel/sesion inválida antes de interpretar -> abortar");
                    await ResetAfterActionAsync("Reconocimiento cancelado.", "Cancelado", speak: "Cancelado.");
                    return;
                }

                Debug.WriteLine("[IA] InterpretRawAsync(text)...");
                var ia = await _interpreter.InterpretRawAsync(text);

                // Si cancelaron mientras interpretaba, NO uses el resultado
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
                    await ResetAfterActionAsync(
                        $"Pediste '{requestedFromSpeech}', pero interpreté '{appKey}'. No ejecutaré nada.",
                        "Acción no disponible",
                        speak: "No ejecutaré nada porque no coincide lo que pediste."
                    );
                    return;
                }

                // Antes de ejecutar/guardar pending, valida cancel/sesion
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
                    _pendingRawJson = ia.Json;

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
