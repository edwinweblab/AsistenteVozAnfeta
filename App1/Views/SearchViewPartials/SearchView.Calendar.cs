using Anfeta.UI.Models.Notion;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Search;
using Microsoft.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private const int CalendarStartHour = 8;
        private const int CalendarEndHour = 22;
        private const string LS_CalendarZoom = "Search.Calendar.Zoom";
        private const string LS_CalendarPeople = "Search.Calendar.People";
        private const string LS_CalendarOrder = "Search.Calendar.Order";
        private const string LS_CalendarColumnWidths =
            "Search.Calendar.ColumnWidths";
        private const string LS_CalendarPeopleSelectionVersion =
            "Search.Calendar.PeopleSelectionVersion";
        private const string LS_SearchLastSubView =
            "Search.LastSubView";
        private const string LS_CalendarLastSyncUtc =
            "Search.Calendar.LastSyncUtc";
        private const string LS_MeetBrowserName =
            "Search.Meet.BrowserName";
        private const string LS_MeetBrowserPath =
            "Search.Meet.BrowserPath";
        private const int CalendarPeopleSelectionVersion = 5;

        private static readonly string[] ActiveCalendarPeople =
        {
            "John",
            "Karla",
            "Isaias",
            "Sotelo",
            "Acalli",
            "Andrade",
            "Brian",
            "Genaro",
            "Neftali",
            "Sin asignar"
        };

        private readonly HashSet<string> _calendarSelectedPeople =
            new(ActiveCalendarPeople, StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _calendarPeopleOrder =
            new(ActiveCalendarPeople);

        private IReadOnlyList<NotionCalendarActivity> _calendarActivities =
            Array.Empty<NotionCalendarActivity>();

        private readonly List<FrameworkElement> _calendarStickyHeaders = new();
        private readonly List<FrameworkElement> _calendarStickyHours = new();
        private FrameworkElement? _calendarStickyCorner;

        private double _calendarZoom = 1.0;
        private bool _calendarPreloadStarted;
        private Task? _calendarPreloadTask;
        private double _calendarStableVerticalOffset;
        private bool _calendarPreferencesLoaded;
        private bool _calendarWheelHandlerHooked;
        private DispatcherTimer? _calendarZoomDebounceTimer;
        private double _calendarPendingZoomDelta;
        private long _calendarLoadVersion;
        private Button? _calendarHoveredActivityButton;
        private CancellationTokenSource? _calendarHoverPreviewCts;
        private DispatcherTimer? _calendarPreviewCloseTimer;
        private bool _calendarPointerOverActivity;
        private bool _calendarPointerOverPreview;
        private Button? _calendarPendingActivityButton;
        private DispatcherTimer? _calendarActivityHoverTimer;
        private Popup? _calendarActivityPreviewPopup;
        private Border? _calendarActivityPreviewPopupCard;
        private ContentControl? _calendarActivityPreviewHost;

        // Panel fijo de actividades por persona. El listado usa únicamente
        // los datos ya cargados del calendario; cada contenido de página se
        // obtiene bajo demanda al pulsar su botón.
        private CancellationTokenSource? _calendarPersonPreviewCts;
        private string _calendarPersonPreviewPerson = string.Empty;

        private bool _calendarSizeHandlerHooked;
        private double _calendarLastViewportWidth;
        private string _calendarSearchQuery = string.Empty;
        private string _calendarPhaseFilter = string.Empty;
        private bool _calendarReviewAlertSending;
        private readonly NotionMessageThreadService
            _calendarReviewFlowService = new();
        private readonly Dictionary<string, ReviewFlowMetadata?>
            _calendarReviewFlowCache =
                new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string>
            _calendarReviewAssigneeRepairAttempted =
                new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, NotionChecklistStats>
            _oneClickChecklistStats =
                new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource?
            _calendarChecklistHydrationCts;

        private CancellationTokenSource?
            _calendarIncrementalChecklistCts;

        private const string LS_CalendarShowCobros =
            "Search.Calendar.ShowCobros";

        private bool _calendarShowCobros;
        private bool _calendarCobrosPreferenceLoaded;

        private readonly Dictionary<string, IReadOnlyList<CalendarCobroOverlayItem>>
            _calendarCobroOverlayCache =
                new(StringComparer.OrdinalIgnoreCase);

        private bool _oneClickScheduleDialogOpen;

        // Conserva temporalmente las personas que tenía cada actividad antes
        // de que Notion cambie manualmente sus tags. Esto permite recuperar al
        // responsable original cuando la actividad pasa a John o Genaro.
        private readonly Dictionary<string, string>
            _calendarLastKnownPeople =
                new(StringComparer.OrdinalIgnoreCase);

        private bool _calendarReviewFlowHydrating;

        private const string CalendarReviewFlowCacheFileName =
            "calendar_review_flow_cache_v2.json";

        private readonly SemaphoreSlim
            _calendarReviewFlowHydrationLock =
                new(1, 1);

        private readonly SemaphoreSlim
            _calendarReviewFlowLocalCacheLock =
                new(1, 1);

        private bool
            _calendarReviewFlowLocalCacheLoaded;

        private ComboBox? CalendarPhaseFilterControl =>
            FindName("CalendarPhaseFilterCombo") as ComboBox;
        private string? _calendarPreviousSearchPlaceholder;
        private DispatcherTimer? _calendarChangesTimer;
        private DateTimeOffset _calendarLastChangesCheckUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        private bool _calendarChangesRefreshRunning;
        private string _calendarLastVisualFingerprint = string.Empty;

        // Estado independiente para detectar una transición real hacia RTUZ.
        // No depende de que la caché/índice conserve el objeto anterior: otras
        // sincronizaciones pueden actualizarlo antes que el calendario.
        private readonly Dictionary<string, bool>
            _calendarObservedRtuzState =
                new(StringComparer.OrdinalIgnoreCase);

        // Historial real de movimientos entre días realizados POR ANFETA.
        // No se infiere por CreatedTime/LastEditedTime porque eso produciría
        // falsos positivos en actividades antiguas o editadas manualmente.
        private const string CalendarMoveHistoryFileName =
            "calendar_move_history_v1.json";

        private readonly Dictionary<string, CalendarMoveHistoryEntry>
            _calendarMoveHistory =
                new(StringComparer.OrdinalIgnoreCase);

        private readonly object _calendarMoveHistorySync = new();
        private bool _calendarMoveHistoryLoaded;

        private sealed class CalendarMoveHistoryEntry
        {
            public string PageId { get; set; } = string.Empty;
            public DateTime SourceDate { get; set; }
            public DateTime TargetDate { get; set; }
            public DateTimeOffset MovedAt { get; set; }
            public string Reason { get; set; } = string.Empty;
        }

        // Los overlays externos (mensajes/recordatorios y Cobrar) dependen del
        // índice global, no de la consulta de Revisiones del calendario.
        // Se actualizan por separado para evitar reconstruir todo el Canvas.
        private long _calendarExternalOverlayIndexVersion = -1;
        private long _calendarCobroCacheIndexVersion = -1;

        // Primer paso del render incremental: los porcentajes de checklist
        // actualizan únicamente el badge de las tarjetas ya dibujadas.
        private sealed class CalendarActivityVisual
        {
            public Button Button { get; init; } = null!;
            public TextBlock TitleText { get; init; } = null!;
            public Border ChecklistBadge { get; init; } = null!;
            public TextBlock ChecklistText { get; init; } = null!;
            public bool CompactChecklistBadge { get; init; }
        }

        private readonly Dictionary<string, List<CalendarActivityVisual>>
            _calendarActivityVisuals =
                new(StringComparer.OrdinalIgnoreCase);

        private DispatcherTimer? _calendarProcessElapsedTimer;
        private DateTimeOffset _calendarProcessStartedAt;
        private string _calendarProcessBaseDetail = string.Empty;
        private long _calendarProcessUiVersion;

        private Button? _calendarDraggingButton;
        private NotionCalendarActivity? _calendarDraggingActivity;
        private uint _calendarDragPointerId;
        private double _calendarDragStartPointerY;
        private double _calendarDragStartTop;
        private bool _calendarDragActive;
        private bool _calendarSuppressNextActivityClick;
        private DateTimeOffset _calendarSuppressActivityClickUntil = DateTimeOffset.MinValue;
        private string _calendarSuppressedActivityPageId = string.Empty;
        private TextBlock? _calendarDragTimeText;
        private string _calendarDragOriginalTimeLabel = string.Empty;


        private readonly Dictionary<string, double> _calendarColumnWidths =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, double> _calendarResolvedColumnWidths =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, double> _calendarResolvedColumnLefts =
            new(StringComparer.OrdinalIgnoreCase);

        private const double CalendarBasePersonColumnWidth = 170;
        private const double CalendarSmallPersonColumnWidth = 125;
        private const double CalendarBasePersonColumnWidthNormal = 170;
        private const double CalendarLargePersonColumnWidth = 240;
        private const double CalendarMinPersonColumnWidth = 110;
        private const double CalendarMaxPersonColumnWidth = 280;

        private double CalendarHourHeight => 82 * _calendarZoom;
        private double CalendarTimeColumnWidth => 76 * _calendarZoom;
        private double CalendarDefaultPersonColumnWidth =>
            CalendarBasePersonColumnWidth * _calendarZoom;
        private double CalendarHeaderHeight => 54 * _calendarZoom;
        private double CalendarFontScale => Math.Clamp(_calendarZoom, 0.70, 1.35);

        private async Task RestoreLastSearchSubViewAsync()
        {
            var lastView =
                (ApplicationData.Current.LocalSettings.Values[
                    LS_SearchLastSubView] as string ??
                 string.Empty).Trim();

            if (string.Equals(
                    lastView,
                    "calendar",
                    StringComparison.OrdinalIgnoreCase))
            {
                await ShowCalendarAsync(DateTime.Today);
                return;
            }

            if (string.Equals(
                    lastView,
                    "reminders",
                    StringComparison.OrdinalIgnoreCase))
            {
                await ShowRemindersCalendarAsync(DateTime.Today);
            }
        }

        private static void SaveLastSearchSubView(
            string value)
        {
            ApplicationData.Current.LocalSettings.Values[
                LS_SearchLastSubView] =
                (value ?? string.Empty).Trim();
        }

        private async void ToggleCalendarView_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (ToggleCalendarView.IsChecked == true)
            {
                await ShowCalendarAsync(DateTime.Today);
            }
            else
            {
                CloseCalendarView();
            }
        }

        private void ClearSearchForModuleSwitch()
        {
            _calendarSearchQuery = string.Empty;

            if (SearchBox == null)
                return;

            SearchBox.Text = string.Empty;
            SearchBox.IsSuggestionListOpen = false;
        }

        private async Task ShowCalendarAsync(DateTime date)
        {
            // Al cambiar de Buscador/Mensajes/Recordatorios a Calendario no se
            // arrastra el texto anterior ni se dejan vistas superpuestas.
            ClearSearchForModuleSwitch();

            if (_remindersCalendarViewActive)
                CloseRemindersCalendarView();

            if (_messagesViewActive)
                CloseMessagesView();

            LoadCalendarCobrosPreference();
            LoadCalendarPreferences();

            await EnsureCalendarReviewFlowLocalCacheLoadedAsync();

            _calendarViewActive = true;
            _calendarSelectedDate = date.Date;
            SaveLastSearchSubView("calendar");

            CalendarHost.Visibility = Visibility.Visible;
            ToggleCalendarView.IsChecked = true;
            CalendarDateTitle.Text = FormatCalendarDate(_calendarSelectedDate);

            if (SearchBox != null)
            {
                _calendarPreviousSearchPlaceholder ??=
                    SearchBox.PlaceholderText;

                SearchBox.PlaceholderText =
                    "Filtrar actividades del calendario...";

                _calendarSearchQuery = string.Empty;
            }

            EnsureCalendarWheelHandler();
            EnsureCalendarSizeHandler();
            ApplyCalendarTheme(_calendarThemeColor);

            if (CalendarPhaseFilterControl != null)
                CalendarPhaseFilterControl.Visibility = Visibility.Visible;
            StartCalendarChangesTimer();

            // LoadCalendarDayAsync aplica la caché una sola vez y después
            // comprueba cambios en background. Antes esta ruta dibujaba el
            // calendario hasta tres veces durante una sola apertura.
            await LoadCalendarDayAsync(
                preferCache: true);
        }

        private void StartCalendarChangesTimer()
        {
            if (_calendarChangesTimer == null)
            {
                _calendarChangesTimer = new DispatcherTimer
                {
                    // Menos consultas automáticas y más margen para Notion.
                    // El botón Actualizar sigue disponible en todo momento.
                    Interval = TimeSpan.FromSeconds(180)
                };

                _calendarChangesTimer.Tick += async (_, __) =>
                {
                    await RefreshCalendarChangesSilentlyAsync();
                    RefreshCalendarExternalOverlaysIfNeeded();
                };
            }

            _calendarChangesTimer.Stop();
            _calendarChangesTimer.Start();
        }

        private void StopCalendarChangesTimer()
        {
            _calendarChangesTimer?.Stop();
        }

        private long BeginCalendarProcess(
            string stage,
            string detail)
        {
            var version = Interlocked.Increment(
                ref _calendarProcessUiVersion);

            _calendarProcessStartedAt = DateTimeOffset.Now;
            _calendarProcessBaseDetail = detail ?? string.Empty;

            if (CalendarProcessPanel == null)
                return version;

            CalendarProcessPanel.Visibility = Visibility.Visible;
            CalendarProcessRing.IsActive = true;
            CalendarProcessStageText.Text = stage;
            CalendarProcessDetailText.Text = _calendarProcessBaseDetail;
            CalendarProcessElapsedText.Text = "00:00";
            CalendarProcessBar.IsIndeterminate = true;
            CalendarProcessBar.Value = 0;

            if (_calendarProcessElapsedTimer == null)
            {
                _calendarProcessElapsedTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };

                _calendarProcessElapsedTimer.Tick += (_, __) =>
                {
                    if (CalendarProcessPanel?.Visibility !=
                        Visibility.Visible)
                    {
                        return;
                    }

                    var elapsed =
                        DateTimeOffset.Now - _calendarProcessStartedAt;

                    CalendarProcessElapsedText.Text =
                        elapsed.TotalHours >= 1
                            ? elapsed.ToString(@"hh\:mm\:ss")
                            : elapsed.ToString(@"mm\:ss");

                    if (NotionRequestCoordinator.IsCoolingDown)
                    {
                        var seconds = Math.Max(1,
                            (int)Math.Ceiling(
                                NotionRequestCoordinator
                                    .CooldownRemaining
                                    .TotalSeconds));

                        CalendarProcessDetailText.Text =
                            $"Notion está regulando las solicitudes · " +
                            $"reintento en {seconds}s · " +
                            _calendarProcessBaseDetail;
                    }
                    else
                    {
                        CalendarProcessDetailText.Text =
                            _calendarProcessBaseDetail;
                    }
                };
            }

            _calendarProcessElapsedTimer.Stop();
            _calendarProcessElapsedTimer.Start();

            return version;
        }

        private void UpdateCalendarProcess(
            long version,
            NotionCalendarProgress report)
        {
            if (version != _calendarProcessUiVersion ||
                CalendarProcessPanel == null)
            {
                return;
            }

            CalendarProcessPanel.Visibility = Visibility.Visible;
            CalendarProcessRing.IsActive = true;
            CalendarProcessStageText.Text = report.Stage;
            _calendarProcessBaseDetail = report.Detail ?? string.Empty;
            CalendarProcessDetailText.Text = _calendarProcessBaseDetail;

            var hasTotal = report.Total > 0;
            CalendarProcessBar.IsIndeterminate = !hasTotal;

            if (hasTotal)
                CalendarProcessBar.Value = report.Percentage;

            StatusText.Text = hasTotal
                ? $"Estado: {report.Stage} {report.Percentage}%"
                : $"Estado: {report.Stage}...";
        }

        private void UpdateCalendarReviewProgress(
            long version,
            int current,
            int total)
        {
            if (version <= 0 ||
                version != _calendarProcessUiVersion ||
                CalendarProcessPanel == null)
            {
                return;
            }

            CalendarProcessStageText.Text =
                "Obteniendo datos de revisión";

            _calendarProcessBaseDetail =
                $"Actividad de revisión {current} de {total}";

            CalendarProcessDetailText.Text =
                _calendarProcessBaseDetail;

            CalendarProcessBar.IsIndeterminate = total <= 0;

            if (total > 0)
            {
                CalendarProcessBar.Value =
                    Math.Clamp(
                        current * 100d / total,
                        0,
                        100);
            }
        }

        private void CompleteCalendarProcess(
            long version,
            string stage,
            string detail,
            bool success = true)
        {
            if (version != _calendarProcessUiVersion ||
                CalendarProcessPanel == null)
            {
                return;
            }

            _calendarProcessElapsedTimer?.Stop();
            CalendarProcessRing.IsActive = false;
            CalendarProcessStageText.Text = stage;
            _calendarProcessBaseDetail = detail ?? string.Empty;
            CalendarProcessDetailText.Text = _calendarProcessBaseDetail;
            CalendarProcessBar.IsIndeterminate = false;
            CalendarProcessBar.Value = success ? 100 : 0;

            DispatcherQueue.TryEnqueue(
                async () =>
                {
                    await Task.Delay(
                        success
                            ? TimeSpan.FromSeconds(2.5)
                            : TimeSpan.FromSeconds(5));

                    if (version == _calendarProcessUiVersion &&
                        CalendarProcessPanel != null)
                    {
                        CalendarProcessPanel.Visibility =
                            Visibility.Collapsed;
                    }
                });
        }

        private DateTimeOffset GetCalendarChangesAnchorUtc()
        {
            var raw =
                ApplicationData.Current.LocalSettings.Values[
                    LS_CalendarLastSyncUtc] as string;

            if (DateTimeOffset.TryParse(
                    raw,
                    out var saved))
            {
                return saved
                    .ToUniversalTime()
                    .Subtract(TimeSpan.FromSeconds(20));
            }

            return _calendarLastChangesCheckUtc
                .Subtract(TimeSpan.FromSeconds(20));
        }

        private void SaveCalendarChangesCheckpoint()
        {
            _calendarLastChangesCheckUtc =
                DateTimeOffset.UtcNow;

            ApplicationData.Current.LocalSettings.Values[
                LS_CalendarLastSyncUtc] =
                _calendarLastChangesCheckUtc.ToString("O");
        }

        private async Task RefreshCalendarChangesSilentlyAsync()
        {
            if (!_calendarViewActive ||
                _calendarChangesRefreshRunning)
            {
                return;
            }

            await RefreshCalendarDaySilentlyAsync(
                _calendarSelectedDate.Date,
                _calendarLoadVersion,
                userInitiated: false);

            // Recordatorios y cobros se refrescan aparte únicamente si cambió
            // App.LocalIndex. No fuerzan un DrawCalendar completo.
            RefreshCalendarExternalOverlaysIfNeeded();
        }

        private void EnsureCalendarSizeHandler()
        {
            if (_calendarSizeHandlerHooked ||
                CalendarScrollViewer == null)
            {
                return;
            }

            CalendarScrollViewer.SizeChanged +=
                CalendarScrollViewer_SizeChanged;

            _calendarSizeHandlerHooked = true;
        }

        private void CalendarScrollViewer_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            if (!_calendarViewActive)
                return;

            var width =
                e.NewSize.Width;

            if (width <= 0 ||
                Math.Abs(
                    width -
                    _calendarLastViewportWidth) < 8)
            {
                return;
            }

            _calendarLastViewportWidth = width;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_calendarViewActive)
                    DrawCalendar(_calendarActivities);
            });
        }

        private void CloseCalendarView()
        {
            _calendarViewActive = false;
            ClearSearchForModuleSwitch();
            SaveLastSearchSubView("results");
            StopCalendarChangesTimer();
            _calendarProcessElapsedTimer?.Stop();
            Interlocked.Increment(ref _calendarProcessUiVersion);

            if (CalendarProcessPanel != null)
                CalendarProcessPanel.Visibility = Visibility.Collapsed;

            CloseCalendarPersonPreviewPanel(redrawCalendar: false);

            try
            {
                _calendarCts?.Cancel();
                _calendarHoverPreviewCts?.Cancel();
                _calendarChecklistHydrationCts?.Cancel();
                _calendarIncrementalChecklistCts?.Cancel();
                HideCalendarActivityPreviewFlyout();
            }
            catch
            {
            }

            _calendarHoveredActivityButton = null;

            CalendarHost.Visibility = Visibility.Collapsed;
            ToggleCalendarView.IsChecked = false;

            if (CalendarPhaseFilterControl != null)
                CalendarPhaseFilterControl.Visibility = Visibility.Collapsed;

            if (SearchBox != null &&
                _calendarPreviousSearchPlaceholder != null)
            {
                SearchBox.PlaceholderText =
                    _calendarPreviousSearchPlaceholder;
            }

            ModeText.Text = $"Modo: Buscar ({GetSourceScopeLabel()})";
            CountText.Text = $"{Results.Count} resultados";
        }

        private void CalendarClose_Click(object sender, RoutedEventArgs e)
            => CloseCalendarView();

        private async void CalendarPreviousDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            await NavigateCalendarToDateAsync(
                _calendarSelectedDate.AddDays(-1));
        }

        private async void CalendarNextDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            await NavigateCalendarToDateAsync(
                _calendarSelectedDate.AddDays(1));
        }

        private async void CalendarYesterday_Click(
            object sender,
            RoutedEventArgs e)
        {
            await NavigateCalendarToDateAsync(
                DateTime.Today.AddDays(-1));
        }

        private async void CalendarToday_Click(
            object sender,
            RoutedEventArgs e)
        {
            await NavigateCalendarToDateAsync(
                DateTime.Today);
        }

        private async void CalendarTomorrow_Click(
            object sender,
            RoutedEventArgs e)
        {
            await NavigateCalendarToDateAsync(
                DateTime.Today.AddDays(1));
        }

        private async Task NavigateCalendarToDateAsync(
            DateTime date)
        {
            _calendarSelectedDate = date.Date;

            // Cancela cualquier consulta del día anterior para impedir que
            // una respuesta atrasada sobrescriba el día que ya está visible.
            try
            {
                _calendarCts?.Cancel();
            }
            catch
            {
            }

            CalendarDateTitle.Text =
                FormatCalendarDate(_calendarSelectedDate);

            // Una sola ruta de carga: muestra caché y programa la validación
            // incremental sin hacer un segundo DrawCalendar inmediato.
            await LoadCalendarDayAsync(
                preferCache: true);
        }

        private async void CalendarRefresh_Click(
            object sender,
            RoutedEventArgs e)
        {
            _calendarCobroOverlayCache.Clear();

            // El botón manual siempre reconstruye el día directamente desde
            // Notion. La actualización automática conserva la ruta incremental.
            // Esto evita que una caché incompleta deje actividades ocultas.
            await LoadCalendarDayAsync(
                preferCache: false,
                forceRefresh: true);
        }

        private async Task LoadCalendarDayAsync(
            bool preferCache = true,
            bool forceRefresh = false)
        {
            var requestedDate =
                _calendarSelectedDate.Date;

            var loadVersion =
                Interlocked.Increment(
                    ref _calendarLoadVersion);

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            CalendarDateTitle.Text =
                FormatCalendarDate(requestedDate);

            CalendarEmptyState.Visibility =
                Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(token))
            {
                CalendarEmptyText.Text =
                    "Configura primero el token de Notion.";
                CalendarEmptyState.Visibility =
                    Visibility.Visible;
                return;
            }

            if (preferCache && !forceRefresh)
            {
                var cached =
                    await _notionCalendarService.TryGetCachedDayAsync(
                        requestedDate);

                if (cached != null)
                {
                    if (loadVersion != _calendarLoadVersion ||
                        requestedDate != _calendarSelectedDate.Date)
                    {
                        return;
                    }

                    _calendarActivities = cached;
                    EnsureCalendarRtuzObservationBaseline(
                        _calendarActivities);
                    ApplyCachedCalendarReviewFlow(
                        _calendarActivities);
                    DrawCalendar(_calendarActivities);
                    StartCalendarChecklistHydration(
                        requestedDate,
                        loadVersion);

                    ModeText.Text =
                        "Modo: Calendario (Revisiones)";
                    StatusText.Text =
                        "Estado: Calendario cargado desde caché ✅ · comprobando cambios...";

                    // Ya no se descarga toda la base al mostrar la caché.
                    // Solo se solicitan páginas modificadas desde el último sync.
                    _ = RefreshCalendarDaySilentlyAsync(
                        requestedDate,
                        loadVersion,
                        userInitiated: false);

                    return;
                }
            }

            try
            {
                _calendarCts?.Cancel();
                _calendarCts?.Dispose();
            }
            catch
            {
            }

            _calendarCts =
                new CancellationTokenSource(
                    TimeSpan.FromMinutes(10));

            var cancellationToken =
                _calendarCts.Token;

            var processVersion =
                BeginCalendarProcess(
                    forceRefresh
                        ? "Recarga completa del calendario"
                        : "Preparando calendario",
                    "Esperando turno para consultar Notion...");

            try
            {
                ShowLoadingState(
                    "Estado: Cargando calendario de Revisiones...",
                    FormatCalendarDate(requestedDate));

                var progress =
                    new Progress<NotionCalendarProgress>(
                        report =>
                        {
                            if (loadVersion != _calendarLoadVersion ||
                                requestedDate != _calendarSelectedDate.Date)
                            {
                                return;
                            }

                            UpdateCalendarProcess(
                                processVersion,
                                report);

                            var percentText =
                                report.Total > 0
                                    ? $" {report.Percentage}%"
                                    : string.Empty;

                            UpdateLoadingState(
                                $"Estado: {report.Stage}{percentText}",
                                report.Detail);

                            CountText.Text =
                                report.Total > 0
                                    ? $"{report.Current} de {report.Total}"
                                    : "Consultando...";
                        });

                var activities =
                    await _notionCalendarService.GetDayAsync(
                        token,
                        requestedDate,
                        progress,
                        cancellationToken,
                        forceRefresh);

                if (!_calendarViewActive ||
                    loadVersion != _calendarLoadVersion ||
                    requestedDate != _calendarSelectedDate.Date)
                {
                    return;
                }

                // También se detecta aquí. El botón “Actualizar” usa esta
                // recarga completa y antes omitía por completo la transición
                // automática hacia RTUZ.
                await ProcessAutomaticCalendarReviewTransitionsAsync(
                    activities,
                    activities
                        .Where(item =>
                            item != null &&
                            !item.IsReviewMirror &&
                            !string.IsNullOrWhiteSpace(item.PageId))
                        .Select(item => item.PageId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());

                _calendarActivities = activities;

                await HydrateCalendarReviewFlowAsync(
                    _calendarActivities,
                    cancellationToken,
                    processVersion);

                DrawCalendar(_calendarActivities);
                StartCalendarChecklistHydration(
                    requestedDate,
                    loadVersion);
                SaveCalendarChangesCheckpoint();

                ModeText.Text =
                    "Modo: Calendario (Revisiones)";
                StatusText.Text =
                    activities.Count > 0
                        ? $"Estado: Calendario actualizado ✅ ({activities.Count})"
                        : "Estado: Calendario sin coincidencias";

                CompleteCalendarProcess(
                    processVersion,
                    "Calendario actualizado",
                    $"{activities.Count} actividades listas.");
            }
            catch (OperationCanceledException)
            {
                if (loadVersion != _calendarLoadVersion ||
                    requestedDate != _calendarSelectedDate.Date)
                {
                    return;
                }

                var cached =
                    await _notionCalendarService.TryGetCachedDayAsync(
                        requestedDate);

                if (cached != null)
                {
                    _calendarActivities = cached;
                    DrawCalendar(_calendarActivities);
                    StatusText.Text =
                        "Estado: Se conservó la información guardada del calendario ✅";

                    CompleteCalendarProcess(
                        processVersion,
                        "Consulta cancelada",
                        "Se conserva la caché visible.",
                        success: false);
                    return;
                }

                _calendarActivities =
                    Array.Empty<NotionCalendarActivity>();
                DrawCalendar(_calendarActivities);

                CalendarEmptyText.Text =
                    "La consulta tardó demasiado y fue cancelada. " +
                    "Pulsa Actualizar para intentarlo de nuevo.";
                CalendarEmptyState.Visibility =
                    Visibility.Visible;
                StatusText.Text =
                    "Estado: Carga del calendario cancelada por tiempo de espera.";

                CompleteCalendarProcess(
                    processVersion,
                    "Carga cancelada",
                    "Notion tardó demasiado en responder.",
                    success: false);
            }
            catch (Exception ex)
            {
                if (loadVersion != _calendarLoadVersion ||
                    requestedDate != _calendarSelectedDate.Date)
                {
                    return;
                }

                var cached =
                    await _notionCalendarService.TryGetCachedDayAsync(
                        requestedDate);

                if (cached != null)
                {
                    _calendarActivities = cached;
                    DrawCalendar(_calendarActivities);
                    StatusText.Text =
                        "Estado: No se pudo actualizar; se conserva la caché del calendario.";

                    CompleteCalendarProcess(
                        processVersion,
                        "No se pudo actualizar",
                        "La información guardada sigue visible.",
                        success: false);
                    return;
                }

                _calendarActivities =
                    Array.Empty<NotionCalendarActivity>();
                DrawCalendar(_calendarActivities);
                CalendarEmptyText.Text =
                    $"No se pudo cargar el calendario.\n{ex.Message}";
                CalendarEmptyState.Visibility =
                    Visibility.Visible;
                StatusText.Text =
                    $"Estado: Error en calendario → {ex.Message}";

                CompleteCalendarProcess(
                    processVersion,
                    "Error en calendario",
                    ex.Message,
                    success: false);
            }
            finally
            {
                if (loadVersion == _calendarLoadVersion)
                    HideLoadingState();
            }
        }

        private async Task RefreshCalendarDaySilentlyAsync(
            DateTime requestedDate,
            long loadVersion,
            bool userInitiated = false)
        {
            if (_calendarChangesRefreshRunning)
            {
                if (userInitiated)
                {
                    StatusText.Text =
                        "Estado: El calendario ya se está actualizando...";
                }

                return;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
                return;

            _calendarChangesRefreshRunning = true;

            var processVersion =
                BeginCalendarProcess(
                    userInitiated
                        ? "Actualizando calendario"
                        : "Mostrando caché",
                    "Esperando turno para comprobar cambios recientes...");

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(4));

                var progress =
                    new Progress<NotionCalendarProgress>(
                        report =>
                            UpdateCalendarProcess(
                                processVersion,
                                report));

                var changed =
                    await _notionCalendarService.RefreshChangedSinceAsync(
                        token,
                        GetCalendarChangesAnchorUtc(),
                        cts.Token,
                        progress);

                SaveCalendarChangesCheckpoint();

                if (!_calendarViewActive ||
                    requestedDate != _calendarSelectedDate.Date ||
                    loadVersion != _calendarLoadVersion)
                {
                    return;
                }

                var activities =
                    await _notionCalendarService.TryGetCachedDayAsync(
                        requestedDate,
                        cts.Token);

                if (activities == null)
                {
                    CompleteCalendarProcess(
                        processVersion,
                        "Sin caché disponible",
                        "Pulsa Actualizar para realizar una recarga completa del día.",
                        success: false);
                    return;
                }

                if (changed)
                {
                    await HydrateCalendarReviewFlowAsync(
                        activities,
                        cts.Token,
                        processVersion);

                    var changedPageIds =
                        _notionCalendarService
                            .LastChangedPageIds
                            .ToList();

                    var incomingFingerprint =
                        BuildCalendarVisualFingerprint(activities);

                    var currentFingerprint =
                        BuildCalendarVisualFingerprint(
                            _calendarActivities);

                    // Detecta transiciones reales hacia rtuzREVISION usando un
                    // estado independiente por PageId. Así no se pierde el cambio
                    // aunque otra sincronización ya haya actualizado la caché.
                    // No agrega consultas de lectura.
                    await ProcessAutomaticCalendarReviewTransitionsAsync(
                        activities,
                        changedPageIds);

                    _calendarActivities = activities;

                    if (!string.Equals(
                            incomingFingerprint,
                            currentFingerprint,
                            StringComparison.Ordinal))
                    {
                        DrawCalendarPreservingView(
                            _calendarActivities);
                    }

                    // Incluso si solo cambió un bloque to_do y las propiedades
                    // visibles son idénticas, se refresca SOLO ese PageId.
                    StartCalendarIncrementalChecklistRefresh(
                        changedPageIds,
                        requestedDate,
                        loadVersion);
                }

                StatusText.Text = changed
                    ? $"Estado: Calendario actualizado ✅ ({activities.Count})"
                    : $"Estado: Calendario al día ✅ ({activities.Count})";

                CompleteCalendarProcess(
                    processVersion,
                    changed
                        ? "Cambios aplicados"
                        : "Calendario al día",
                    changed
                        ? $"{activities.Count} actividades listas."
                        : "No se encontraron cambios nuevos en Notion.");
            }
            catch (OperationCanceledException)
            {
                CompleteCalendarProcess(
                    processVersion,
                    "Comprobación cancelada",
                    "La caché continúa visible.",
                    success: false);
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    "Estado: No se pudo comprobar Notion; se conserva la caché.";

                CompleteCalendarProcess(
                    processVersion,
                    "No se pudo comprobar Notion",
                    ex.Message,
                    success: false);
            }
            finally
            {
                _calendarChangesRefreshRunning = false;
            }
        }

        private void EnsureCalendarMoveHistoryLoaded()
        {
            lock (_calendarMoveHistorySync)
            {
                if (_calendarMoveHistoryLoaded)
                    return;

                _calendarMoveHistoryLoaded = true;
                _calendarMoveHistory.Clear();

                try
                {
                    var path = Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        CalendarMoveHistoryFileName);

                    if (!File.Exists(path))
                        return;

                    var raw = File.ReadAllText(path);

                    if (string.IsNullOrWhiteSpace(raw))
                        return;

                    var restored =
                        JsonSerializer.Deserialize<
                            Dictionary<string, CalendarMoveHistoryEntry>>(raw);

                    if (restored == null)
                        return;

                    foreach (var item in restored)
                    {
                        if (string.IsNullOrWhiteSpace(item.Key) ||
                            item.Value == null)
                        {
                            continue;
                        }

                        item.Value.PageId = item.Key;
                        _calendarMoveHistory[item.Key] = item.Value;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[CALENDAR_MOVE_HISTORY_LOAD] {ex.Message}");
                }
            }
        }

        private void SaveCalendarMoveHistory()
        {
            lock (_calendarMoveHistorySync)
            {
                try
                {
                    var path = Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        CalendarMoveHistoryFileName);

                    var json = JsonSerializer.Serialize(
                        _calendarMoveHistory,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                    File.WriteAllText(path, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[CALENDAR_MOVE_HISTORY_SAVE] {ex.Message}");
                }
            }
        }

        private void RegisterCalendarDayMovement(
            NotionCalendarActivity activity,
            DateTime sourceDate,
            DateTime targetDate,
            string reason)
        {
            if (activity == null ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                return;
            }

            var source = sourceDate.Date;
            var target = targetDate.Date;

            // Este indicador describe arrastre hacia días posteriores.
            // Movimientos hacia atrás no deben aparecer como “De ayer”.
            if (target <= source)
            {
                ClearCalendarDayMovement(activity.PageId);
                return;
            }

            EnsureCalendarMoveHistoryLoaded();

            lock (_calendarMoveHistorySync)
            {
                _calendarMoveHistory[activity.PageId] =
                    new CalendarMoveHistoryEntry
                    {
                        PageId = activity.PageId,
                        SourceDate = source,
                        TargetDate = target,
                        MovedAt = DateTimeOffset.Now,
                        Reason = (reason ?? string.Empty).Trim()
                    };
            }

            SaveCalendarMoveHistory();
        }

        private void ClearCalendarDayMovement(string pageId)
        {
            var clean = (pageId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return;

            EnsureCalendarMoveHistoryLoaded();

            var removed = false;

            lock (_calendarMoveHistorySync)
            {
                removed = _calendarMoveHistory.Remove(clean);
            }

            if (removed)
                SaveCalendarMoveHistory();
        }

        private bool TryGetCalendarDayMovement(
            NotionCalendarActivity activity,
            out CalendarMoveHistoryEntry movement)
        {
            movement = null!;

            if (activity == null ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                return false;
            }

            EnsureCalendarMoveHistoryLoaded();

            CalendarMoveHistoryEntry? stored;

            lock (_calendarMoveHistorySync)
            {
                _calendarMoveHistory.TryGetValue(
                    activity.PageId,
                    out stored);
            }

            if (stored == null)
                return false;

            // Si Notion cambió luego la fecha manualmente, la marca anterior
            // deja de ser válida. Nunca mostramos un movimiento que ANFETA ya
            // no puede demostrar que corresponde al día visible actual.
            if (stored.TargetDate.Date != activity.Start.Date)
            {
                ClearCalendarDayMovement(activity.PageId);
                return false;
            }

            if (stored.TargetDate.Date <= stored.SourceDate.Date)
            {
                ClearCalendarDayMovement(activity.PageId);
                return false;
            }

            movement = stored;
            return true;
        }

        private string GetCalendarDayMovementBadge(
            NotionCalendarActivity activity)
        {
            if (!TryGetCalendarDayMovement(activity, out var movement))
                return string.Empty;

            var days = Math.Max(
                1,
                (movement.TargetDate.Date -
                 movement.SourceDate.Date).Days);

            return days == 1
                ? "↪ De ayer"
                : $"↪ De hace {days} días";
        }

        private string GetCalendarDayMovementDetail(
            NotionCalendarActivity activity)
        {
            if (!TryGetCalendarDayMovement(activity, out var movement))
                return string.Empty;

            var days = Math.Max(
                1,
                (movement.TargetDate.Date -
                 movement.SourceDate.Date).Days);

            var relative = days == 1
                ? "desde ayer"
                : $"desde hace {days} días";

            var reason = string.IsNullOrWhiteSpace(movement.Reason)
                ? string.Empty
                : $" · {movement.Reason}";

            return
                $"Movida por ANFETA {relative} " +
                $"({movement.SourceDate:dd/MM/yyyy} → " +
                $"{movement.TargetDate:dd/MM/yyyy}){reason}";
        }

        private static string BuildCalendarVisualFingerprint(
            IReadOnlyList<NotionCalendarActivity> activities)
        {
            if (activities == null || activities.Count == 0)
                return string.Empty;

            return string.Join(
                "\n",
                activities
                    .OrderBy(item => item.PageId)
                    .ThenBy(item => item.Person)
                    .Select(item =>
                        string.Join(
                            "|",
                            item.PageId,
                            item.Title,
                            item.Person,
                            item.OriginalPerson,
                            item.ReviewAssignee,
                            item.ReviewState,
                            item.Start.ToString("O"),
                            item.End.ToString("O"),
                            item.Status,
                            item.StatusColor,
                            item.UpdateText,
                            item.IsAutomationLocked,
                            item.ChecklistScanned,
                            item.ChecklistTotal,
                            item.ChecklistCompleted,
                            item.EstimatedWorkMinutes,
                            item.WorkedMinutes,
                            item.WorkLogDetail,
                            item.ActivityCreatedDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                            item.InternalDeadlineDate?.ToString("yyyy-MM-dd") ?? string.Empty)));
        }

        private void DrawCalendarPreservingView(
            IReadOnlyList<NotionCalendarActivity> activities,
            bool force = false)
        {
            var fingerprint =
                BuildCalendarVisualFingerprint(activities);

            if (!force &&
                string.Equals(
                    fingerprint,
                    _calendarLastVisualFingerprint,
                    StringComparison.Ordinal))
            {
                return;
            }

            var horizontalOffset =
                CalendarScrollViewer?.HorizontalOffset ?? 0;

            var verticalOffset =
                CalendarScrollViewer?.VerticalOffset ?? 0;

            DrawCalendar(activities);

            _calendarLastVisualFingerprint = fingerprint;

            DispatcherQueue.TryEnqueue(() =>
            {
                CalendarScrollViewer?.ChangeView(
                    horizontalOffset,
                    verticalOffset,
                    null,
                    disableAnimation: true);
            });
        }

        private void DrawCalendar(
            IReadOnlyList<NotionCalendarActivity> activities)
        {
            ApplyCachedCalendarReviewFlow(activities);

            var expandedActivities =
                ExpandCalendarReviewActivities(activities);

            var phaseActivities =
                string.IsNullOrWhiteSpace(
                    _calendarPhaseFilter)
                    ? expandedActivities
                    : expandedActivities
                        .Where(activity =>
                            ContainsExactCalendarPart(
                                BuildCalendarActivitySearchableText(
                                    activity),
                                _calendarPhaseFilter))
                        .ToList();

            var visibleActivities =
                FilterCalendarActivities(
                    phaseActivities,
                    _calendarSearchQuery);

            CalendarCanvas.Children.Clear();
            _calendarActivityVisuals.Clear();
            _calendarStickyHeaders.Clear();
            _calendarStickyHours.Clear();
            _calendarStickyCorner = null;

            var hasCobroOverlayEvents =
                _calendarShowCobros &&
                GetCalendarCobroItems(
                    _calendarSelectedDate).Count > 0;

            CalendarEmptyState.Visibility =
                visibleActivities.Count == 0 &&
                !hasCobroOverlayEvents
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            CalendarEmptyText.Text =
                string.IsNullOrWhiteSpace(_calendarSearchQuery)
                    ? (string.IsNullOrWhiteSpace(_calendarPhaseFilter)
                        ? "No hay actividades programadas para este día."
                        : $"No hay actividades con la fase exacta {_calendarPhaseFilter}.") +
                      (string.IsNullOrWhiteSpace(
                          _notionCalendarService.LastDiagnostics)
                          ? string.Empty
                          : $"\n\n{_notionCalendarService.LastDiagnostics}")
                    : $"No se encontraron actividades que coincidan con “{_calendarSearchQuery}”.";

            var persons =
                _calendarPeopleOrder
                    .Where(person =>
                        _calendarSelectedPeople.Contains(person))
                    .ToList();

            if (persons.Count == 0)
            {
                CalendarEmptyText.Text =
                    "Selecciona al menos una persona en el filtro.";
                CalendarEmptyState.Visibility = Visibility.Visible;
            }

            var totalHours = CalendarEndHour - CalendarStartHour;
            var headerHeight = CalendarHeaderHeight;
            var bodyHeight = totalHours * CalendarHourHeight;

            var viewportWidth =
                CalendarScrollViewer?.ActualWidth > 0
                    ? CalendarScrollViewer.ActualWidth
                    : CalendarHost.ActualWidth;

            ResolveCalendarColumnLayout(
                persons,
                viewportWidth);

            var totalWidth =
                CalendarTimeColumnWidth +
                persons.Sum(GetResolvedCalendarColumnWidth);

            CalendarCanvas.Width =
                Math.Max(
                    Math.Max(900, viewportWidth),
                    totalWidth);

            CalendarCanvas.Height =
                headerHeight + bodyHeight + 16;

            CalendarCanvas.Background =
                new SolidColorBrush(Darken(_calendarThemeColor, 0.12));

            var headerBackground = AddCalendarRectangle(
                0,
                0,
                CalendarCanvas.Width,
                headerHeight,
                Darken(_calendarThemeColor, 0.02),
                Lighten(_calendarThemeColor, 0.18));

            Canvas.SetZIndex(headerBackground, 100);

            var corner = AddCalendarText(
                "Hora",
                10,
                17 * _calendarZoom,
                CalendarTimeColumnWidth - 20,
                24 * _calendarZoom,
                12 * CalendarFontScale,
                true);

            corner.Tag = "CalendarCorner";
            _calendarStickyCorner = corner;
            Canvas.SetZIndex(corner, 310);

            for (var personIndex = 0;
                 personIndex < persons.Count;
                 personIndex++)
            {
                var person = persons[personIndex];
                var left =
                    GetResolvedCalendarColumnLeft(person);

                var columnWidth =
                    GetResolvedCalendarColumnWidth(person);

                var headerContainer = new Grid
                {
                    Width = columnWidth - 8,
                    Height = headerHeight - 4,
                    Background =
                        new SolidColorBrush(
                            Darken(_calendarThemeColor, 0.02)),
                    Tag = person
                };

                headerContainer.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(
                            1,
                            GridUnitType.Star)
                    });

                headerContainer.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });

                headerContainer.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });

                var currentCoverage =
                    CalculateCurrentCalendarCoverage(person);

                var headerContent = new StackPanel
                {
                    Spacing = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };

                headerContent.Children.Add(
                    new TextBlock
                    {
                        Text = person,
                        FontSize = 13.5 * CalendarFontScale,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1
                    });

                headerContent.Children.Add(
                    new TextBlock
                    {
                        Text = _calendarZoom < 0.85
                            ? $"Cob. {currentCoverage:0}%"
                            : $"Cobertura {currentCoverage:0}%",
                        FontSize = 8.8 * CalendarFontScale,
                        Foreground =
                            GetOneClickCoverageBrush(currentCoverage),
                        Opacity = 0.92,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                var headerButton = new Button
                {
                    Content = headerContent,
                    Padding = new Thickness(7, 0, 2, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    Tag = person
                };

                ToolTipService.SetToolTip(
                    headerButton,
                    $"Ver actividades de {person}");

                // El encabezado completo reemplaza al antiguo botón del ojo.
                headerButton.Click +=
                    CalendarPersonPreview_Click;

                headerButton.ContextFlyout =
                    BuildCalendarHeaderContextFlyout(person);

                var optimizeButton = new Button
                {
                    Content = "⚡",
                    Width = 28,
                    Height = Math.Max(26, headerHeight - 14),
                    Margin = new Thickness(0, 5, 3, 5),
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontSize = 12.5 * CalendarFontScale,
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(55, 250, 204, 21)),
                    BorderBrush =
                        new SolidColorBrush(
                            Color.FromArgb(155, 250, 204, 21)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Tag = person
                };

                ToolTipService.SetToolTip(
                    optimizeButton,
                    $"Generar vista previa de One Click Schedule para {person}");

                optimizeButton.Click +=
                    CalendarOneClickSchedule_Click;

                var moreButton = new Button
                {
                    Content = "⋯",
                    Width = 26,
                    Height = Math.Max(26, headerHeight - 14),
                    Margin = new Thickness(0, 5, 3, 5),
                    Padding = new Thickness(0, 0, 0, 5),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontSize = 15 * CalendarFontScale,
                    Background =
                        new SolidColorBrush(
                            Lighten(_calendarThemeColor, 0.06)),
                    BorderBrush =
                        new SolidColorBrush(
                            Lighten(_calendarThemeColor, 0.20)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Tag = person,
                    Flyout = BuildCalendarHeaderContextFlyout(person)
                };

                ToolTipService.SetToolTip(
                    moreButton,
                    $"Más opciones de {person}");

                Grid.SetColumn(headerButton, 0);
                headerContainer.Children.Add(headerButton);

                Grid.SetColumn(optimizeButton, 1);
                headerContainer.Children.Add(optimizeButton);

                Grid.SetColumn(moreButton, 2);
                headerContainer.Children.Add(moreButton);

                Canvas.SetLeft(headerContainer, left + 2);
                Canvas.SetTop(headerContainer, 2);
                Canvas.SetZIndex(headerContainer, 300);
                CalendarCanvas.Children.Add(headerContainer);
                _calendarStickyHeaders.Add(headerContainer);

                AddVerticalLine(
                    left,
                    0,
                    headerHeight + bodyHeight,
                    Lighten(_calendarThemeColor, 0.20));
            }

            AddVerticalLine(
                CalendarCanvas.Width - 1,
                0,
                headerHeight + bodyHeight,
                Lighten(_calendarThemeColor, 0.20));

            for (var hour = CalendarStartHour;
                 hour <= CalendarEndHour;
                 hour++)
            {
                var top =
                    headerHeight +
                    (hour - CalendarStartHour) *
                    CalendarHourHeight;

                AddHorizontalLine(
                    0,
                    top,
                    CalendarCanvas.Width,
                    Lighten(_calendarThemeColor, 0.15));

                if (hour < CalendarEndHour)
                {
                    var hourLabel = AddCalendarText(
                        FormatHour(hour),
                        8,
                        top + 5,
                        CalendarTimeColumnWidth - 14,
                        22 * _calendarZoom,
                        11 * CalendarFontScale,
                        false);

                    _calendarStickyHours.Add(hourLabel);
                    Canvas.SetZIndex(hourLabel, 210);
                }
            }

            var filteredActivities =
                visibleActivities
                    .SelectMany(activity =>
                        SplitPersons(activity.Person)
                            .Select(person =>
                                (Activity: activity,
                                 Person: NormalizeCalendarPerson(person))))
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(x.Person) &&
                        _calendarSelectedPeople.Contains(x.Person))
                    .ToList();


            foreach (var person in persons)
            {
                var personIndex =
                    persons.FindIndex(x =>
                        string.Equals(
                            x,
                            person,
                            StringComparison.OrdinalIgnoreCase));

                var items = filteredActivities
                    .Where(x =>
                        string.Equals(
                            x.Person,
                            person,
                            StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Activity)
                    .OrderBy(x => x.Start)
                    .ThenBy(x => x.End)
                    .ToList();

                DrawPersonActivities(
                    items,
                    person,
                    headerHeight);
            }

            // Los recordatorios se dibujan como eventos especiales sobre el
            // calendario normal sin formar parte de One Click ni de la carga
            // de actividades de Notion.
            DrawCalendarReminderOverlays(
                headerHeight,
                persons);

            // Capa visual independiente de BD COBRAR Y PAGAR.
            DrawCalendarCobroOverlays(
                headerHeight,
                persons);

            _calendarExternalOverlayIndexVersion =
                App.LocalIndex.Version;

            DrawCurrentTimeLine(headerHeight);
            UpdateCalendarStickyElements();

            CalendarZoomText.Text =
                $"{Math.Round(_calendarZoom * 100):0}%";

            if (_calendarViewActive)
            {
                var drawnActivityCount =
                    filteredActivities
                        .Select(item =>
                            string.Join(
                                "|",
                                item.Activity.PageId,
                                item.Person,
                                item.Activity.IsReviewMirror,
                                item.Activity.Start.ToString("O")))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                var visibleCobros =
                    _calendarShowCobros
                        ? GetCalendarCobroItems(
                            _calendarSelectedDate).Count
                        : 0;

                CountText.Text =
                    $"{drawnActivityCount} visibles · {activities.Count} cargadas" +
                    (_calendarShowCobros
                        ? $" · {visibleCobros} cobro(s)"
                        : string.Empty);

                var phaseLabel =
                    string.IsNullOrWhiteSpace(_calendarPhaseFilter)
                        ? "Todas"
                        : _calendarPhaseFilter;

                var cobrosMode =
                    _calendarShowCobros
                        ? " · 💰 Cobros"
                        : string.Empty;

                ModeText.Text =
                    (string.IsNullOrWhiteSpace(_calendarSearchQuery)
                        ? $"Modo: Calendario · {phaseLabel}"
                        : $"Modo: Calendario · {phaseLabel} · {_calendarSearchQuery}") +
                    cobrosMode;
            }

            _calendarLastVisualFingerprint =
                BuildCalendarVisualFingerprint(activities);

            RefreshCalendarPersonPreviewIfOpen();
        }

        private sealed record CalendarCobroOverlayItem(
            SearchResultRow Row,
            DateTime Start,
            DateTime End,
            string Person,
            string Title);

        private ToggleButton? CalendarCobrosToggleControl =>
            FindName("CalendarCobrosToggle") as ToggleButton;

        private void LoadCalendarCobrosPreference()
        {
            if (!_calendarCobrosPreferenceLoaded)
            {
                _calendarCobrosPreferenceLoaded = true;

                var stored =
                    ApplicationData.Current.LocalSettings.Values[
                        LS_CalendarShowCobros];

                _calendarShowCobros =
                    stored is bool enabled && enabled;
            }

            UpdateCalendarCobrosToggleVisual();
        }

        private void UpdateCalendarCobrosToggleVisual()
        {
            if (CalendarCobrosToggleControl == null)
                return;

            CalendarCobrosToggleControl.IsChecked = _calendarShowCobros;
            CalendarCobrosToggleControl.Content =
                _calendarShowCobros ? "💰 Cobros ✓" : "💰 Cobros";

            ToolTipService.SetToolTip(
                CalendarCobrosToggleControl,
                _calendarShowCobros
                    ? "Ocultar eventos de BD COBRAR Y PAGAR"
                    : "Mostrar eventos con Due Fecha Recordatorio de BD COBRAR Y PAGAR");
        }

        private void CalendarCobrosToggle_Click(
            object sender,
            RoutedEventArgs e)
        {
            _calendarShowCobros =
                sender is ToggleButton toggle
                    ? toggle.IsChecked == true
                    : !_calendarShowCobros;

            ApplicationData.Current.LocalSettings.Values[
                LS_CalendarShowCobros] = _calendarShowCobros;

            _calendarCobroOverlayCache.Clear();
            _calendarCobroCacheIndexVersion =
                App.LocalIndex.Version;

            UpdateCalendarCobrosToggleVisual();

            if (_calendarViewActive)
            {
                RefreshCalendarExternalOverlaysIfNeeded(
                    force: true);

                StatusText.Text =
                    _calendarShowCobros
                        ? $"Estado: BdCOBRAR visible ✅ ({GetCalendarCobroItems(_calendarSelectedDate).Count} evento(s) con hora)"
                        : "Estado: BdCOBRAR oculto ✅";
            }
        }

        private static bool IsCobrarPagarIndexRow(
            SearchResultRow row)
        {
            if (row == null || row.Source != SearchSource.Notion)
                return false;

            var source =
                NormalizeCalendarSearchText(
                    row.ExternalSourceName);

            if (!source.Contains("cobrar", StringComparison.OrdinalIgnoreCase) ||
                !source.Contains("pagar", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Regex.IsMatch(
                row.DisplayName ?? string.Empty,
                @"(?<![\p{L}\p{Nd}_])(?:sprtuz|prtuz|rtuz|z)?cobrar(?![\p{L}\p{Nd}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool TryParseCobroScheduledRange(
            SearchResultRow row,
            out DateTime start,
            out DateTime end)
        {
            start = default;
            end = default;

            var raw = (row?.ScheduledDate ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            // Solo eventos con hora explícita. Las fechas sin hora no se dibujan.
            if (!Regex.IsMatch(
                    raw,
                    @"(?:T|\s)\d{1,2}:\d{2}",
                    RegexOptions.CultureInvariant))
            {
                return false;
            }

            var separatorIndex = raw.IndexOf(" - ", StringComparison.Ordinal);
            var startRaw = separatorIndex > 0
                ? raw.Substring(0, separatorIndex).Trim()
                : raw;
            var endRaw = separatorIndex > 0
                ? raw.Substring(separatorIndex + 3).Trim()
                : string.Empty;

            if (DateTimeOffset.TryParse(
                    startRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var startOffset))
            {
                start = startOffset.LocalDateTime;
            }
            else if (!DateTime.TryParse(
                         startRaw,
                         CultureInfo.InvariantCulture,
                         DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                         out start))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(endRaw) &&
                DateTimeOffset.TryParse(
                    endRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var endOffset))
            {
                end = endOffset.LocalDateTime;
            }
            else if (!string.IsNullOrWhiteSpace(endRaw) &&
                     DateTime.TryParse(
                         endRaw,
                         CultureInfo.InvariantCulture,
                         DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                         out var endDate))
            {
                end = endDate;
            }
            else
            {
                end = start.AddHours(1);
            }

            if (end <= start)
                end = start.AddHours(1);

            return true;
        }

        private IReadOnlyList<CalendarCobroOverlayItem> GetCalendarCobroItems(
            DateTime day)
        {
            if (!_calendarShowCobros)
                return Array.Empty<CalendarCobroOverlayItem>();

            var indexVersion =
                App.LocalIndex.Version;

            if (_calendarCobroCacheIndexVersion != indexVersion)
            {
                _calendarCobroOverlayCache.Clear();
                _calendarCobroCacheIndexVersion = indexVersion;
            }

            var key = day.Date.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);

            if (_calendarCobroOverlayCache.TryGetValue(key, out var cached))
                return cached;

            var dayStart = day.Date;
            var dayEnd = dayStart.AddDays(1);

            var items =
                App.LocalIndex
                    .GetAll()
                    .Where(IsCobrarPagarIndexRow)
                    .Select(row =>
                    {
                        if (!TryParseCobroScheduledRange(
                                row,
                                out var start,
                                out var end) ||
                            start >= dayEnd || end <= dayStart)
                        {
                            return null;
                        }

                        var title = row.DisplayName?.Trim() ?? string.Empty;
                        var person = GetActiveCalendarPersonFromTitle(title);
                        if (string.IsNullOrWhiteSpace(person))
                            person = "Sin asignar";

                        return new CalendarCobroOverlayItem(
                            row, start, end, person, title);
                    })
                    .Where(item => item != null)
                    .Cast<CalendarCobroOverlayItem>()
                    .OrderBy(item => item.Start)
                    .ThenBy(item => item.Title)
                    .ToList();

            _calendarCobroOverlayCache[key] = items;
            return items;
        }

        private void RefreshCalendarExternalOverlaysIfNeeded(
            bool force = false)
        {
            if (!_calendarViewActive ||
                CalendarCanvas == null)
            {
                return;
            }

            var indexVersion =
                App.LocalIndex.Version;

            if (!force &&
                _calendarExternalOverlayIndexVersion ==
                    indexVersion)
            {
                return;
            }

            var removable =
                CalendarCanvas.Children
                    .OfType<Button>()
                    .Where(button =>
                        button.Tag is MessageViewItem ||
                        (button.Tag is SearchResultRow row &&
                         IsCobrarPagarIndexRow(row)))
                    .ToList();

            foreach (var element in removable)
                CalendarCanvas.Children.Remove(element);

            _calendarCobroOverlayCache.Clear();
            _calendarCobroCacheIndexVersion = indexVersion;

            var persons =
                _calendarPeopleOrder
                    .Where(person =>
                        _calendarSelectedPeople.Contains(person))
                    .ToList();

            DrawCalendarReminderOverlays(
                CalendarHeaderHeight,
                persons);

            DrawCalendarCobroOverlays(
                CalendarHeaderHeight,
                persons);

            _calendarExternalOverlayIndexVersion =
                indexVersion;
        }

        private async Task OpenCalendarCobroAsync(
            SearchResultRow row)
        {
            if (row == null)
                return;

            await OpenNotionPageWithFallbackAsync(
                !string.IsNullOrWhiteSpace(row.ExternalUrl)
                    ? row.ExternalUrl
                    : row.Target,
                desktopSuccessStatus: "Cobro abierto en Notion Desktop",
                browserSuccessStatus: "Cobro abierto en el navegador",
                failureStatus: "No se pudo abrir el cobro",
                invalidUrlStatus: "El cobro no tiene una URL válida de Notion");
        }

        private void DrawCalendarCobroOverlays(
            double headerHeight,
            IReadOnlyList<string> visiblePersons)
        {
            if (!_calendarShowCobros || !_calendarViewActive ||
                CalendarCanvas == null || visiblePersons == null ||
                visiblePersons.Count == 0)
            {
                return;
            }

            var cobros = GetCalendarCobroItems(_calendarSelectedDate);
            if (cobros.Count == 0)
                return;

            var stackCounters = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var cobro in cobros)
            {
                var localStart = cobro.Start;
                var localEnd = cobro.End;

                if (localEnd <= _calendarSelectedDate.Date.AddHours(CalendarStartHour) ||
                    localStart >= _calendarSelectedDate.Date.AddHours(CalendarEndHour))
                {
                    continue;
                }

                var person = NormalizeCalendarPerson(cobro.Person);
                if (string.IsNullOrWhiteSpace(person) ||
                    string.Equals(person, "Sin asignar", StringComparison.OrdinalIgnoreCase))
                {
                    person = visiblePersons.Contains(
                        "Sin asignar",
                        StringComparer.OrdinalIgnoreCase)
                        ? "Sin asignar"
                        : visiblePersons.FirstOrDefault() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(person) ||
                    !visiblePersons.Contains(person, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dayStart = _calendarSelectedDate.Date.AddHours(CalendarStartHour);
                var visibleStart = localStart < dayStart ? dayStart : localStart;
                var minutesFromStart = (visibleStart - dayStart).TotalMinutes;
                var top = headerHeight + minutesFromStart / 60d * CalendarHourHeight + 4;

                var columnWidth = GetResolvedCalendarColumnWidth(person);
                var columnLeft = GetResolvedCalendarColumnLeft(person);
                var width = Math.Clamp(
                    columnWidth * 0.70,
                    88,
                    Math.Max(88, columnWidth - 12));

                var durationMinutes = Math.Max(15, (localEnd - localStart).TotalMinutes);
                var height = Math.Clamp(
                    durationMinutes / 60d * CalendarHourHeight - 7,
                    28,
                    42);

                var stackKey = $"{person}|{localStart:HH:mm}";
                stackCounters.TryGetValue(stackKey, out var stackIndex);
                stackCounters[stackKey] = stackIndex + 1;

                var title = string.IsNullOrWhiteSpace(cobro.Title)
                    ? "Cobro"
                    : cobro.Title;

                var content = new StackPanel
                {
                    Spacing = 0,
                    IsHitTestVisible = false
                };

                content.Children.Add(new TextBlock
                {
                    Text = $"💰 COBRO · {localStart:HH:mm}",
                    FontSize = Math.Max(8.2, 8.8 * CalendarFontScale),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(
                        Color.FromArgb(255, 209, 250, 229))
                });

                if (height >= 34)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = title,
                        FontSize = Math.Max(8.0, 8.4 * CalendarFontScale),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = new SolidColorBrush(
                            Color.FromArgb(255, 236, 253, 245))
                    });
                }

                var button = new Button
                {
                    Width = width,
                    Height = height,
                    Padding = new Thickness(7, 2, 7, 2),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(
                        Color.FromArgb(235, 5, 78, 59)),
                    BorderBrush = new SolidColorBrush(
                        Color.FromArgb(255, 52, 211, 153)),
                    BorderThickness = new Thickness(3, 1, 1, 1),
                    CornerRadius = new CornerRadius(6),
                    Content = content,
                    Tag = cobro.Row
                };

                var statusLine = string.IsNullOrWhiteSpace(cobro.Row.ProjectUpdateStatus)
                    ? string.Empty
                    : $"\nEstado: {cobro.Row.ProjectUpdateStatus}";

                ToolTipService.SetToolTip(
                    button,
                    $"💰 BD COBRAR\n{title}\n" +
                    $"{localStart:dd/MM/yyyy HH:mm} – {localEnd:HH:mm}" +
                    statusLine +
                    "\nClic para abrir en Notion");

                button.Click += async (_, __) =>
                    await OpenCalendarCobroAsync(cobro.Row);

                Canvas.SetLeft(
                    button,
                    columnLeft + 5 + Math.Min(16, stackIndex * 5));
                Canvas.SetTop(
                    button,
                    top + Math.Min(18, stackIndex * 4));
                Canvas.SetZIndex(button, 440 + stackIndex);
                CalendarCanvas.Children.Add(button);
            }
        }

        private static bool HasExactCalendarPhase(
            NotionCalendarActivity activity,
            string phase)
        {
            if (activity == null ||
                string.IsNullOrWhiteSpace(phase))
            {
                return false;
            }

            return Regex.IsMatch(
                activity.Title ?? string.Empty,
                $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(phase)}(?![\p{{L}}\p{{Nd}}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private static bool HasCurrentReviewMirrorPhase(
            NotionCalendarActivity activity)
        {
            // La copia visual existe exclusivamente durante rtuzREVISION.
            // zREVISION ya representa una revisión aprobada y debe mostrarse
            // como una sola actividad normal del responsable original.
            return HasExactCalendarPhase(
                activity,
                "rtuzREVISION");
        }

        private static bool IsReviewEligibleActivity(
            NotionCalendarActivity activity)
        {
            // Estas fases contienen o conservan metadata del flujo.
            // prtuzREVISION no se hidrata como revisión para evitar consultas
            // innecesarias y nunca genera copia visual.
            return HasExactCalendarPhase(
                       activity,
                       "rtuzREVISION") ||
                   HasExactCalendarPhase(
                       activity,
                       "zREVISION");
        }

        private static bool CanSendCalendarActivityToReview(
            NotionCalendarActivity activity)
        {
            if (activity == null || activity.IsReviewMirror)
                return false;

            // Una actividad vuelve a estar lista para revisión cuando está
            // en prtuzREVISION, o puede reenviarse desde zREVISION.
            // sprtuzREVISION queda excluido por coincidencia exacta.
            return HasExactCalendarPhase(
                       activity,
                       "prtuzREVISION") ||
                   HasExactCalendarPhase(
                       activity,
                       "zREVISION") ||
                   (HasExactCalendarPhase(
                        activity,
                        "rtuzREVISION") &&
                    (!activity.IsPendingReview ||
                     activity.IsReturnedForCorrections));
        }

        private static bool IsPendingReviewActivity(
            NotionCalendarActivity activity)
        {
            return HasExactCalendarPhase(
                       activity,
                       "rtuzREVISION") &&
                   activity.IsPendingReview;
        }

        private static string BuildCalendarActivitySearchableText(
            NotionCalendarActivity activity)
        {
            return string.Join(
                " ",
                new[]
                {
                    activity.Title,
                    activity.Person,
                    activity.OriginalPerson,
                    activity.Project,
                    activity.Status,
                    activity.UpdateText,
                    activity.Description,
                    activity.IsAutomationLocked
                        ? "bloqueada automatizacion"
                        : string.Empty,
                    activity.ChecklistScanned
                        ? $"checklist {activity.ChecklistCompleted} de {activity.ChecklistTotal} {activity.ChecklistPercentage} porcentaje"
                        : string.Empty
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
        }


        private sealed class MeetBrowserOption
        {
            public string Name { get; init; } = string.Empty;
            public string ExecutablePath { get; init; } = string.Empty;

            public bool IsSystemDefault =>
                string.IsNullOrWhiteSpace(ExecutablePath);

            public override string ToString() => Name;
        }

        private static string ReadMeetBrowserExecutable(
            string command)
        {
            command =
                Environment.ExpandEnvironmentVariables(
                    command ?? string.Empty)
                .Trim();

            if (string.IsNullOrWhiteSpace(command))
                return string.Empty;

            if (command.StartsWith('"'))
            {
                var closingQuote =
                    command.IndexOf('"', 1);

                if (closingQuote > 1)
                {
                    return command
                        .Substring(1, closingQuote - 1)
                        .Trim();
                }
            }

            var executableEnd =
                command.IndexOf(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase);

            return executableEnd >= 0
                ? command
                    .Substring(0, executableEnd + 4)
                    .Trim()
                    .Trim('"')
                : string.Empty;
        }

        private static string NormalizeMeetBrowserDisplayName(
            string name,
            string executablePath)
        {
            var searchable =
                $"{name} {Path.GetFileNameWithoutExtension(executablePath)}";

            if (searchable.Contains(
                    "msedge",
                    StringComparison.OrdinalIgnoreCase) ||
                searchable.Contains(
                    "edge",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Microsoft Edge";
            }

            if (searchable.Contains(
                    "chrome",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Google Chrome";
            }

            if (searchable.Contains(
                    "firefox",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Mozilla Firefox";
            }

            if (searchable.Contains(
                    "brave",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Brave";
            }

            if (searchable.Contains(
                    "opera gx",
                    StringComparison.OrdinalIgnoreCase) ||
                searchable.Contains(
                    "operagx",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Opera GX";
            }

            if (searchable.Contains(
                    "opera",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Opera";
            }

            if (searchable.Contains(
                    "vivaldi",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Vivaldi";
            }

            if (searchable.Contains(
                    "arc",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Arc";
            }

            if (searchable.Contains(
                    "helium",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Helium";
            }

            return string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(executablePath)
                : name.Trim();
        }

        private static void AddMeetBrowserCandidate(
            IDictionary<string, MeetBrowserOption> browsers,
            string name,
            string executablePath)
        {
            executablePath =
                Environment.ExpandEnvironmentVariables(
                    executablePath ?? string.Empty)
                .Trim()
                .Trim('"');

            if (string.IsNullOrWhiteSpace(executablePath) ||
                !File.Exists(executablePath))
            {
                return;
            }

            try
            {
                executablePath =
                    Path.GetFullPath(executablePath);
            }
            catch
            {
                return;
            }

            if (browsers.ContainsKey(executablePath))
                return;

            browsers[executablePath] =
                new MeetBrowserOption
                {
                    Name = NormalizeMeetBrowserDisplayName(
                        name,
                        executablePath),
                    ExecutablePath = executablePath
                };
        }

        private static void ReadRegisteredMeetBrowsers(
            IDictionary<string, MeetBrowserOption> browsers,
            RegistryHive hive,
            RegistryView view)
        {
            try
            {
                using var baseKey =
                    RegistryKey.OpenBaseKey(hive, view);

                using var clients =
                    baseKey.OpenSubKey(
                        @"SOFTWARE\Clients\StartMenuInternet");

                if (clients == null)
                    return;

                foreach (var browserKeyName in
                         clients.GetSubKeyNames())
                {
                    using var browserKey =
                        clients.OpenSubKey(browserKeyName);

                    using var commandKey =
                        browserKey?.OpenSubKey(
                            @"shell\open\command");

                    var command =
                        commandKey?.GetValue(null)?.ToString() ??
                        string.Empty;

                    var executablePath =
                        ReadMeetBrowserExecutable(command);

                    var displayName =
                        browserKey?.GetValue(null)?.ToString() ??
                        browserKeyName;

                    AddMeetBrowserCandidate(
                        browsers,
                        displayName,
                        executablePath);
                }
            }
            catch
            {
                // Algunos equipos restringen ciertas vistas del registro.
                // La detección continúa con rutas conocidas y el navegador
                // predeterminado de Windows siempre permanece disponible.
            }
        }

        private static IReadOnlyList<MeetBrowserOption>
            DetectInstalledMeetBrowsers()
        {
            var discovered =
                new Dictionary<string, MeetBrowserOption>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var hive in new[]
                     {
                         RegistryHive.CurrentUser,
                         RegistryHive.LocalMachine
                     })
            {
                foreach (var view in new[]
                         {
                             RegistryView.Registry64,
                             RegistryView.Registry32
                         })
                {
                    ReadRegisteredMeetBrowsers(
                        discovered,
                        hive,
                        view);
                }
            }

            var programFiles =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles);

            var programFilesX86 =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86);

            var localAppData =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);

            var knownBrowsers = new[]
            {
                ("Microsoft Edge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")),
                ("Microsoft Edge", Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")),
                ("Google Chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe")),
                ("Google Chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe")),
                ("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")),
                ("Mozilla Firefox", Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe")),
                ("Mozilla Firefox", Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe")),
                ("Brave", Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
                ("Brave", Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
                ("Brave", Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
                ("Opera", Path.Combine(localAppData, "Programs", "Opera", "launcher.exe")),
                ("Opera GX", Path.Combine(localAppData, "Programs", "Opera GX", "launcher.exe")),
                ("Vivaldi", Path.Combine(programFiles, "Vivaldi", "Application", "vivaldi.exe")),
                ("Vivaldi", Path.Combine(localAppData, "Vivaldi", "Application", "vivaldi.exe")),
                ("Arc", Path.Combine(localAppData, "Programs", "Arc", "Arc.exe")),
                ("Helium", Path.Combine(localAppData, "Programs", "Helium", "helium.exe")),
                ("Helium", Path.Combine(programFiles, "Helium", "helium.exe"))
            };

            foreach (var browser in knownBrowsers)
            {
                AddMeetBrowserCandidate(
                    discovered,
                    browser.Item1,
                    browser.Item2);
            }

            return new[]
                {
                    new MeetBrowserOption
                    {
                        Name = "Predeterminado de Windows"
                    }
                }
                .Concat(
                    discovered.Values
                        .OrderBy(browser => browser.Name)
                        .ThenBy(browser => browser.ExecutablePath))
                .ToList();
        }

        private static void ClearSavedMeetBrowser()
        {
            var values =
                ApplicationData.Current.LocalSettings.Values;

            values.Remove(LS_MeetBrowserName);
            values.Remove(LS_MeetBrowserPath);
        }

        private async Task<MeetBrowserOption?>
            SelectMeetBrowserAsync(
                bool alwaysShowDialog)
        {
            var values =
                ApplicationData.Current.LocalSettings.Values;

            var hasSavedSelection =
                values.ContainsKey(LS_MeetBrowserName) ||
                values.ContainsKey(LS_MeetBrowserPath);

            var savedName =
                values[LS_MeetBrowserName] as string ??
                string.Empty;

            var savedPath =
                values[LS_MeetBrowserPath] as string ??
                string.Empty;

            if (!alwaysShowDialog && hasSavedSelection)
            {
                if (string.IsNullOrWhiteSpace(savedPath))
                {
                    return new MeetBrowserOption
                    {
                        Name = string.IsNullOrWhiteSpace(savedName)
                            ? "Predeterminado de Windows"
                            : savedName
                    };
                }

                if (File.Exists(savedPath))
                {
                    return new MeetBrowserOption
                    {
                        Name = string.IsNullOrWhiteSpace(savedName)
                            ? NormalizeMeetBrowserDisplayName(
                                string.Empty,
                                savedPath)
                            : savedName,
                        ExecutablePath = savedPath
                    };
                }

                ClearSavedMeetBrowser();
                StatusText.Text =
                    "Estado: El navegador guardado ya no está instalado. Elige otro para Meet.";
            }

            var browsers =
                DetectInstalledMeetBrowsers();

            var browserCombo = new ComboBox
            {
                Width = 360,
                MaxDropDownHeight = 320,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var browser in browsers)
            {
                browserCombo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = browser.Name,
                        Tag = browser
                    });
            }

            var selectedIndex = 0;

            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                for (var index = 0;
                     index < browserCombo.Items.Count;
                     index++)
                {
                    if (browserCombo.Items[index] is not
                            ComboBoxItem item ||
                        item.Tag is not MeetBrowserOption browser)
                    {
                        continue;
                    }

                    if (string.Equals(
                            browser.ExecutablePath,
                            savedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }

            browserCombo.SelectedIndex = selectedIndex;

            var content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 370
            };

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        "Selecciona dónde abrir los tres enlaces de Google Meet. " +
                        "ANFETA guardará esta elección para las siguientes reuniones.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.78
                });

            content.Children.Add(browserCombo);

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        browsers.Count > 1
                            ? "Puedes cambiarlo después desde Meet → Cambiar navegador."
                            : "No se detectaron otros navegadores; se usará el predeterminado de Windows.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.60
                });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Elegir navegador para Meet",
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                Content = content
            };

            var result =
                await dialog.ShowAsync();

            if (result != ContentDialogResult.Primary)
                return null;

            var selected =
                (browserCombo.SelectedItem as ComboBoxItem)?
                    .Tag as MeetBrowserOption ??
                browsers[0];

            values[LS_MeetBrowserName] = selected.Name;
            values[LS_MeetBrowserPath] = selected.ExecutablePath;

            return selected;
        }

        private static bool LaunchMeetWithExecutable(
            MeetBrowserOption browser,
            Uri uri)
        {
            if (browser.IsSystemDefault ||
                !File.Exists(browser.ExecutablePath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = browser.ExecutablePath,
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add(uri.AbsoluteUri);

            return Process.Start(startInfo) != null;
        }

        private async void ConfigureMeetBrowser_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                var selected =
                    await SelectMeetBrowserAsync(
                        alwaysShowDialog: true);

                StatusText.Text = selected == null
                    ? "Estado: No se cambió el navegador de Meet."
                    : $"Estado: Meet se abrirá con {selected.Name} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo configurar el navegador → {ex.Message}";
            }
        }

        private async void OpenMeetQuickLink_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var url =
                (element.Tag?.ToString() ?? string.Empty).Trim();

            var label =
                sender is MenuFlyoutItem menuItem &&
                !string.IsNullOrWhiteSpace(menuItem.Text)
                    ? menuItem.Text
                    : "Google Meet";

            if (!Uri.TryCreate(
                    url,
                    UriKind.Absolute,
                    out var uri) ||
                !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text =
                    "Estado: El enlace de Meet no es válido.";
                return;
            }

            try
            {
                var browser =
                    await SelectMeetBrowserAsync(
                        alwaysShowDialog: false);

                if (browser == null)
                {
                    StatusText.Text =
                        "Estado: Apertura de Meet cancelada.";
                    return;
                }

                var opened = browser.IsSystemDefault
                    ? await Launcher.LaunchUriAsync(uri)
                    : LaunchMeetWithExecutable(browser, uri);

                if (!opened && !browser.IsSystemDefault)
                {
                    // Si el ejecutable dejó de funcionar, se elimina la
                    // preferencia y se intenta con el navegador predeterminado.
                    ClearSavedMeetBrowser();
                    opened = await Launcher.LaunchUriAsync(uri);

                    StatusText.Text = opened
                        ? $"Estado: {label} abierto con el navegador predeterminado; vuelve a elegir tu navegador de Meet."
                        : $"Estado: Windows no pudo abrir {label}.";
                    return;
                }

                StatusText.Text = opened
                    ? $"Estado: {label} abierto con {browser.Name} ✅"
                    : $"Estado: Windows no pudo abrir {label}.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir {label} → {ex.Message}";
            }
        }

        private void CalendarPhaseFilterCombo_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (CalendarPhaseFilterControl?.SelectedItem is not
                ComboBoxItem item)
            {
                return;
            }

            _calendarPhaseFilter =
                (item.Tag?.ToString() ??
                 string.Empty).Trim();

            if (!_calendarViewActive)
                return;

            HideCalendarActivityPreviewFlyout();
            DrawCalendar(_calendarActivities);

            StatusText.Text =
                string.IsNullOrWhiteSpace(_calendarPhaseFilter)
                    ? "Estado: Mostrando todas las fases ✅"
                    : $"Estado: Filtro exacto {_calendarPhaseFilter} aplicado ✅";
        }

        private void ApplyCalendarSearchFilter(
            string query)
        {
            _calendarSearchQuery =
                (query ?? string.Empty).Trim();

            HideCalendarActivityPreviewFlyout();
            DrawCalendar(_calendarActivities);

            StatusText.Text =
                string.IsNullOrWhiteSpace(_calendarSearchQuery)
                    ? $"Estado: Filtro del calendario limpiado ✅ ({_calendarActivities.Count} actividades)"
                    : $"Estado: Calendario filtrado por “{_calendarSearchQuery}” ✅";
        }

        private static IReadOnlyList<NotionCalendarActivity>
            FilterCalendarActivities(
                IReadOnlyList<NotionCalendarActivity> activities,
                string query)
        {
            var parts =
                ParseCalendarSearchParts(query);

            if (parts.Count == 0)
                return activities;

            return activities
                .Where(activity =>
                {
                    var searchable = string.Join(
                        " ",
                        new[]
                        {
                            activity.Title,
                            activity.Person,
                            activity.OriginalPerson,
                            activity.Project,
                            activity.Status,
                            activity.UpdateText,
                            activity.Description,
                            activity.PageUrl,
                            activity.TimeLabel,
                            activity.Start.ToString(
                                "dd/MM/yyyy HH:mm",
                                CultureInfo.InvariantCulture),
                            activity.End.ToString(
                                "dd/MM/yyyy HH:mm",
                                CultureInfo.InvariantCulture)
                        });

                    return parts.All(part =>
                    {
                        // Los estados de revisión siempre son tokens exactos.
                        // Así rtuzREVISION no coincide dentro de prtuzREVISION
                        // ni sprtuzREVISION aunque se escriba sin comillas.
                        if (IsCalendarPhaseSearchToken(part.Value))
                        {
                            return ContainsExactCalendarPart(
                                searchable,
                                part.Value);
                        }

                        return part.IsExact
                            ? ContainsExactCalendarPart(
                                searchable,
                                part.Value)
                            : searchable.Contains(
                                part.Value,
                                StringComparison.OrdinalIgnoreCase);
                    });
                })
                .ToList();
        }

        private sealed record CalendarSearchPart(
            string Value,
            bool IsExact);

        private static IReadOnlyList<CalendarSearchPart>
            ParseCalendarSearchParts(string query)
        {
            var result =
                new List<CalendarSearchPart>();

            foreach (Match match in Regex.Matches(
                query ?? string.Empty,
                "\\\"(?<exact>[^\\\"]+)\\\"|(?<normal>\\S+)",
                RegexOptions.CultureInvariant))
            {
                var exact =
                    match.Groups["exact"].Success;

                var value = exact
                    ? match.Groups["exact"].Value
                    : match.Groups["normal"].Value;

                value = value.Trim();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(
                        new CalendarSearchPart(
                            value,
                            exact));
                }
            }

            return result;
        }

        private static bool IsCalendarPhaseSearchToken(
            string value)
        {
            return Regex.IsMatch(
                (value ?? string.Empty).Trim(),
                @"^(?:sprtuz|prtuz|rtuz|z)REVISION$",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private static bool ContainsExactCalendarPart(
            string searchable,
            string value)
        {
            value =
                (value ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(value))
                return true;

            if (value.Any(char.IsWhiteSpace) ||
                value.Contains('.') ||
                value.Contains('/'))
            {
                return searchable.Contains(
                    value,
                    StringComparison.OrdinalIgnoreCase);
            }

            return Regex.IsMatch(
                searchable,
                $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(value)}(?![\p{{L}}\p{{Nd}}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private static string NormalizeCalendarSearchText(
            string value)
        {
            var normalized =
                (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Normalize(NormalizationForm.FormD);

            var result =
                new StringBuilder(
                    normalized.Length);

            foreach (var character in normalized)
            {
                var category =
                    CharUnicodeInfo.GetUnicodeCategory(
                        character);

                if (category ==
                    UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                result.Append(
                    char.IsLetterOrDigit(character)
                        ? character
                        : ' ');
            }

            return string.Join(
                " ",
                result.ToString()
                    .Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries));
        }

        private void DrawPersonActivities(
            IReadOnlyList<NotionCalendarActivity> items,
            string person,
            double headerHeight)
        {
            var ordered =
                (items ?? Array.Empty<NotionCalendarActivity>())
                    .OrderBy(activity => activity.Start)
                    .ThenBy(activity => activity.End)
                    .ThenBy(activity => activity.Title)
                    .ToList();

            if (ordered.Count == 0)
                return;

            var overlapGroup =
                new List<NotionCalendarActivity>();

            var overlapGroupEnd =
                DateTime.MinValue;

            void DrawOverlapGroup()
            {
                if (overlapGroup.Count == 0)
                    return;

                var laneEnds =
                    new List<DateTime>();

                var layouts =
                    new List<(
                        NotionCalendarActivity Activity,
                        int Lane,
                        int Sequence)>();

                for (var sequence = 0;
                     sequence < overlapGroup.Count;
                     sequence++)
                {
                    var activity =
                        overlapGroup[sequence];

                    var lane = 0;

                    while (lane < laneEnds.Count &&
                           activity.Start < laneEnds[lane])
                    {
                        lane++;
                    }

                    if (lane == laneEnds.Count)
                        laneEnds.Add(activity.End);
                    else
                        laneEnds[lane] = activity.End;

                    layouts.Add(
                        (activity, lane, sequence));
                }

                var maxConcurrency =
                    Math.Max(
                        1,
                        laneEnds.Count);

                foreach (var layout in layouts)
                {
                    var directOverlapCount =
                        Math.Max(
                            1,
                            overlapGroup.Count(other =>
                                other.Start <
                                    layout.Activity.End &&
                                other.End >
                                    layout.Activity.Start));

                    AddActivityButton(
                        layout.Activity,
                        person,
                        headerHeight,
                        layout.Lane,
                        maxConcurrency,
                        directOverlapCount,
                        layout.Sequence);
                }

                overlapGroup.Clear();
                overlapGroupEnd =
                    DateTime.MinValue;
            }

            foreach (var activity in ordered)
            {
                if (overlapGroup.Count == 0)
                {
                    overlapGroup.Add(activity);
                    overlapGroupEnd = activity.End;
                    continue;
                }

                // Agrupa cadenas completas de empalmes. Aunque una actividad
                // no se cruce directamente con la primera, permanece en el
                // mismo grupo cuando se conecta por otra actividad intermedia.
                if (activity.Start < overlapGroupEnd)
                {
                    overlapGroup.Add(activity);

                    if (activity.End > overlapGroupEnd)
                        overlapGroupEnd = activity.End;

                    continue;
                }

                DrawOverlapGroup();

                overlapGroup.Add(activity);
                overlapGroupEnd = activity.End;
            }

            DrawOverlapGroup();
        }

        private static string BuildActivityDayRangeTooltip(
            NotionCalendarActivity activity)
        {
            if (activity == null ||
                !activity.HasActivityDayRange)
            {
                return string.Empty;
            }

            var start =
                activity.ActivityCreatedDate!.Value.Date;

            var deadline =
                activity.InternalDeadlineDate!.Value.Date;

            var elapsed =
                activity.ActivityElapsedDays;

            var budget =
                activity.ActivityBudgetDays;

            var overdueDays =
                Math.Max(0, elapsed - budget);

            return string.Join(
                "\n",
                new[]
                {
                    $"Inicio: {start:dd/MM/yyyy}",
                    $"Hoy: {DateTime.Today:dd/MM/yyyy}",
                    $"Límite: {deadline:dd/MM/yyyy}",
                    $"Avance de tiempo: día {elapsed} de {budget}",
                    overdueDays > 0
                        ? $"Vencida por {overdueDays} día(s)"
                        : $"Restan {Math.Max(0, budget - elapsed)} día(s) del presupuesto"
                });
        }

        private FrameworkElement? BuildActivityDayRangeIndicator(
            NotionCalendarActivity activity,
            double cardWidth,
            double cardHeight,
            bool compact)
        {
            if (activity == null ||
                !activity.HasActivityDayRange ||
                activity.ActivityBudgetDays <= 0)
            {
                return null;
            }

            var elapsed =
                activity.ActivityElapsedDays;

            var budget =
                activity.ActivityBudgetDays;

            var overdue =
                activity.IsActivityOverdue;

            // Cada bloque representa un día siempre que el rango sea razonable.
            // En rangos muy largos se comprime para no destruir el ancho de la
            // tarjeta, pero el texto conserva siempre el valor real X/Y días.
            const int MaxExactDaySegments = 16;

            var maxSegmentsByWidth =
                Math.Clamp(
                    (int)Math.Floor(
                        Math.Max(36, cardWidth - 12) /
                        Math.Max(6, 9 * _calendarZoom)),
                    3,
                    MaxExactDaySegments);

            var segmentCount =
                budget <= MaxExactDaySegments
                    ? Math.Max(1, budget)
                    : Math.Max(
                        1,
                        Math.Min(
                            budget,
                            maxSegmentsByWidth));

            var completedSegments =
                budget <= 0
                    ? 0
                    : Math.Clamp(
                        budget <= MaxExactDaySegments
                            ? Math.Min(elapsed, budget)
                            : (int)Math.Ceiling(
                                Math.Min(elapsed, budget) *
                                segmentCount /
                                (double)budget),
                        0,
                        segmentCount);

            var root = new Grid
            {
                Height = compact
                    ? Math.Max(14, 15 * _calendarZoom)
                    : Math.Max(17, 19 * _calendarZoom),
                Margin = new Thickness(
                    0,
                    2,
                    0,
                    0),
                HorizontalAlignment =
                    HorizontalAlignment.Stretch,
                VerticalAlignment =
                    VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };

            root.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            root.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var segments = new Grid
            {
                Height = compact
                    ? Math.Max(6, 6.5 * _calendarZoom)
                    : Math.Max(7, 8 * _calendarZoom),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };

            for (var index = 0;
                 index < segmentCount;
                 index++)
            {
                segments.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(
                            1,
                            GridUnitType.Star)
                    });

                var filled =
                    index < completedSegments;

                var segment = new Border
                {
                    // Separación visible entre días. La línea oscura hace que
                    // incluso dos días (1/2d) se perciban como dos bloques y
                    // no como una sola barra continua partida por color.
                    Margin = new Thickness(
                        index == 0 ? 0 : 1.35,
                        0,
                        index == segmentCount - 1 ? 0 : 1.35,
                        0),
                    CornerRadius = new CornerRadius(2),
                    BorderBrush = new SolidColorBrush(
                        Color.FromArgb(185, 17, 24, 39)),
                    BorderThickness = new Thickness(0.8),
                    Background =
                        new SolidColorBrush(
                            filled
                                ? Color.FromArgb(255, 255, 153, 0)
                                : Color.FromArgb(255, 47, 128, 237))
                };

                Grid.SetColumn(segment, index);
                segments.Children.Add(segment);
            }

            Grid.SetColumn(segments, 0);
            root.Children.Add(segments);

            var label = new TextBlock
            {
                Text = overdue
                    ? $"{elapsed}/{budget}d ⚠"
                    : $"{elapsed}/{budget}d",
                FontSize = Math.Max(
                    7.6,
                    (compact ? 7.7 : 8.2) *
                    CalendarFontScale),
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(
                    overdue
                        ? Color.FromArgb(255, 248, 113, 113)
                        : Color.FromArgb(235, 229, 231, 235)),
                VerticalAlignment =
                    VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming =
                    TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };

            Grid.SetColumn(label, 1);
            root.Children.Add(label);

            ToolTipService.SetToolTip(
                root,
                BuildActivityDayRangeTooltip(activity));

            return root;
        }

        private string BuildCalendarChecklistBadgeText(
            NotionCalendarActivity activity,
            bool compactBadge)
        {
            var stats =
                GetCalendarChecklistStats(activity);

            if (!activity.ChecklistScanned)
            {
                return compactBadge
                    ? "…"
                    : "☑ …";
            }

            if (!stats.HasChecklist)
                return string.Empty;

            var percentage =
                GetChecklistPercentage(stats);

            return compactBadge
                ? $"{percentage}%"
                : $"☑ {percentage}%";
        }

        private void UpdateCalendarChecklistVisuals(
            IEnumerable<string> pageIds)
        {
            if (pageIds == null)
                return;

            foreach (var pageId in
                     pageIds
                         .Where(id =>
                             !string.IsNullOrWhiteSpace(id))
                         .Distinct(
                             StringComparer.OrdinalIgnoreCase))
            {
                if (!_calendarActivityVisuals.TryGetValue(
                        pageId,
                        out var visuals))
                {
                    continue;
                }

                var activity =
                    _calendarActivities
                        .FirstOrDefault(item =>
                            string.Equals(
                                item.PageId,
                                pageId,
                                StringComparison.OrdinalIgnoreCase));

                if (activity == null)
                    continue;

                var stats =
                    GetCalendarChecklistStats(activity);

                foreach (var visual in visuals)
                {
                    visual.Button.Tag = activity;

                    var badgeText =
                        BuildCalendarChecklistBadgeText(
                            activity,
                            visual.CompactChecklistBadge);

                    var showBadge =
                        !string.IsNullOrWhiteSpace(
                            badgeText);

                    visual.ChecklistText.Text =
                        badgeText;

                    visual.ChecklistBadge.Visibility =
                        showBadge
                            ? Visibility.Visible
                            : Visibility.Collapsed;

                    visual.TitleText.Margin =
                        showBadge
                            ? new Thickness(
                                0,
                                0,
                                visual.CompactChecklistBadge
                                    ? Math.Max(
                                        28,
                                        34 * _calendarZoom)
                                    : Math.Max(
                                        34,
                                        43 * _calendarZoom),
                                0)
                            : new Thickness(0);

                    visual.ChecklistBadge.BorderBrush =
                        new SolidColorBrush(
                            activity.ChecklistScanned
                                ? Color.FromArgb(
                                    255, 74, 222, 128)
                                : Color.FromArgb(
                                    255, 125, 211, 252));

                    visual.ChecklistText.Foreground =
                        new SolidColorBrush(
                            activity.ChecklistScanned
                                ? Color.FromArgb(
                                    255, 134, 239, 172)
                                : Color.FromArgb(
                                    255, 186, 230, 253));

                    ToolTipService.SetToolTip(
                        visual.ChecklistBadge,
                        activity.ChecklistScanned
                            ? stats.HasChecklist
                                ? $"Checklist: {stats.Completed}/{stats.Total} completadas · {stats.Pending} pendientes"
                                : "Checklist revisado · sin tareas activas"
                            : "Calculando porcentaje del checklist…");
                }
            }
        }

        private void AddActivityButton(
            NotionCalendarActivity activity,
            string person,
            double headerHeight,
            int lane,
            int laneCount,
            int overlapCount,
            int sequenceIndex)
        {
            var dayStart =
                _calendarSelectedDate.Date.AddHours(CalendarStartHour);

            var dayEnd =
                _calendarSelectedDate.Date.AddHours(CalendarEndHour);

            var visibleStart =
                activity.Start < dayStart ? dayStart : activity.Start;

            var visibleEnd =
                activity.End > dayEnd ? dayEnd : activity.End;

            if (visibleEnd <= dayStart ||
                visibleStart >= dayEnd)
            {
                return;
            }

            var minutesFromStart =
                (visibleStart - dayStart).TotalMinutes;

            var durationMinutes =
                Math.Max(
                    30,
                    (visibleEnd - visibleStart).TotalMinutes);

            var columnWidth =
                GetResolvedCalendarColumnWidth(person);

            var columnLeft =
                GetResolvedCalendarColumnLeft(person);

            var usableWidth =
                Math.Max(20, columnWidth - 10);

            var safeLaneCount =
                Math.Max(1, laneCount);

            var safeLane =
                Math.Clamp(
                    lane,
                    0,
                    safeLaneCount - 1);

            // Con una o dos actividades simultáneas se conserva la división
            // lado a lado. A partir de tres se usa una pila escalonada para
            // evitar tarjetas extremadamente angostas.
            var useStackedLayout =
                safeLaneCount >= 3;

            double cardWidth;
            double left;

            if (!useStackedLayout)
            {
                var laneWidth =
                    usableWidth / safeLaneCount;

                cardWidth =
                    Math.Max(
                        8,
                        laneWidth - 4);

                left =
                    columnLeft +
                    5 +
                    safeLane * laneWidth;
            }
            else
            {
                var desiredOffset =
                    Math.Clamp(
                        14 * _calendarZoom,
                        9,
                        16);

                var minimumReadableWidth =
                    Math.Min(
                        Math.Max(
                            54,
                            74 * _calendarZoom),
                        usableWidth * 0.74);

                var maximumOffset =
                    safeLaneCount <= 1
                        ? 0
                        : Math.Max(
                            2,
                            (usableWidth -
                             minimumReadableWidth -
                             4) /
                            (safeLaneCount - 1));

                var stackOffset =
                    Math.Min(
                        desiredOffset,
                        maximumOffset);

                cardWidth =
                    Math.Max(
                        34,
                        usableWidth -
                        stackOffset *
                        (safeLaneCount - 1) -
                        4);

                left =
                    columnLeft +
                    5 +
                    safeLane * stackOffset;
            }

            var top =
                headerHeight +
                minutesFromStart / 60d *
                CalendarHourHeight +
                3;

            var height =
                Math.Max(
                    34 * _calendarZoom,
                    durationMinutes / 60d *
                    CalendarHourHeight -
                    5);

            var titleText = new TextBlock
            {
                Text = activity.Title,
                FontSize =
                    (useStackedLayout
                        ? 9.8
                        : 10.5) *
                    CalendarFontScale,
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold,
                MaxLines =
                    useStackedLayout ||
                    _calendarZoom < 0.85
                        ? 1
                        : 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false
            };

            var timeText = new TextBlock
            {
                Text = activity.TimeLabel,
                FontSize =
                    (useStackedLayout
                        ? 8.8
                        : 9.5) *
                    CalendarFontScale,
                Opacity = 0.82,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };

            var content = new StackPanel
            {
                Spacing = Math.Max(1, 2 * _calendarZoom),
                IsHitTestVisible = false
            };

            content.Children.Add(titleText);
            content.Children.Add(timeText);

            if (activity.HasWorkLog &&
                height >= 48 * _calendarZoom &&
                cardWidth >= 92)
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text =
                            activity.WorkProgressLabel,
                        FontSize =
                            (useStackedLayout
                                ? 8.2
                                : 8.8) *
                            CalendarFontScale,
                        Opacity = 0.86,
                        MaxLines = 1,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    });
            }

            var overdueMinutes =
                GetCalendarOverdueMinutes(activity);

            if (overdueMinutes > 0 &&
                height >= 44 * _calendarZoom)
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"⚠ Retraso {FormatCalendarDelayMinutes(overdueMinutes)}",
                        FontSize =
                            (useStackedLayout
                                ? 8.1
                                : 8.7) *
                            CalendarFontScale,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    255, 248, 113, 113)),
                        Opacity = 0.95,
                        MaxLines = 1,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    });
            }

            var movementBadge =
                GetCalendarDayMovementBadge(activity);

            if (!string.IsNullOrWhiteSpace(movementBadge) &&
                height >= 36 * _calendarZoom)
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text = movementBadge,
                        FontSize =
                            (useStackedLayout
                                ? 8.1
                                : 8.7) *
                            CalendarFontScale,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    255, 125, 211, 252)),
                        Opacity = 0.95,
                        MaxLines = 1,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    });
            }

            var activityDayRange =
                BuildActivityDayRangeIndicator(
                    activity,
                    cardWidth,
                    height,
                    useStackedLayout || cardWidth < 105);

            if (activityDayRange != null)
                content.Children.Add(activityDayRange);

            var calendarChecklist =
                GetCalendarChecklistStats(activity);

            var compactBadge =
                useStackedLayout ||
                cardWidth < 92;

            // En tarjetas estrechas se muestra solo el porcentaje para no
            // cubrir el título. El detalle completo permanece en el tooltip.
            var checklistBadgeText =
                BuildCalendarChecklistBadgeText(
                    activity,
                    compactBadge);

            if (!string.IsNullOrWhiteSpace(checklistBadgeText))
            {
                titleText.Margin = new Thickness(
                    0,
                    0,
                    compactBadge
                        ? Math.Max(
                            28,
                            34 * _calendarZoom)
                        : Math.Max(
                            34,
                            43 * _calendarZoom),
                    0);
            }

            if (activity.IsAutomationLocked)
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text = "🔒 Bloqueada",
                        FontSize = 8.4 * CalendarFontScale,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(
                            Color.FromArgb(255, 216, 180, 254)),
                        Opacity = 0.90,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    });
            }

            if (string.Equals(
                    person,
                    "Sin asignar",
                    StringComparison.OrdinalIgnoreCase) &&
                ContainsAnyCalendarPersonTag(activity.Title))
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Tag activo no reconocido",
                        FontSize = 8.4 * CalendarFontScale,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Opacity = 0.90,
                        MaxLines = 1,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    });
            }

            if (!string.IsNullOrWhiteSpace(
                    activity.ReviewBadgeLabel))
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text = activity.ReviewBadgeLabel,
                        FontSize = 8.4 * CalendarFontScale,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Opacity = 0.88,
                        MaxLines = useStackedLayout ? 1 : 2,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    });
            }

            if (!useStackedLayout &&
                _calendarZoom >= 0.90 &&
                !string.IsNullOrWhiteSpace(activity.Project))
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text = activity.Project,
                        FontSize = 8.5 * CalendarFontScale,
                        Opacity = 0.68,
                        MaxLines = 1,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    });
            }

            var cardContent = new Grid
            {
                IsHitTestVisible = false
            };

            cardContent.Children.Add(content);

            var checklistText =
                new TextBlock
                {
                    Text = checklistBadgeText,
                    FontSize = Math.Max(
                        8,
                        8.5 * CalendarFontScale),
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(
                        activity.ChecklistScanned
                            ? Color.FromArgb(
                                255, 134, 239, 172)
                            : Color.FromArgb(
                                255, 186, 230, 253)),
                    HorizontalAlignment =
                        HorizontalAlignment.Center,
                    VerticalAlignment =
                        VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    MaxLines = 1,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };

            var checklistBadge = new Border
            {
                MinWidth =
                    compactBadge
                        ? 27
                        : 36,
                Height = Math.Max(
                    17,
                    19 * _calendarZoom),
                Padding =
                    compactBadge
                        ? new Thickness(3, 0, 3, 0)
                        : new Thickness(4, 0, 4, 0),
                Margin = new Thickness(0, 2, 2, 0),
                HorizontalAlignment =
                    HorizontalAlignment.Right,
                VerticalAlignment =
                    VerticalAlignment.Top,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(
                    Color.FromArgb(220, 17, 24, 39)),
                BorderBrush = new SolidColorBrush(
                    activity.ChecklistScanned
                        ? Color.FromArgb(
                            255, 74, 222, 128)
                        : Color.FromArgb(
                            255, 125, 211, 252)),
                BorderThickness = new Thickness(1),
                Child = checklistText,
                Visibility =
                    string.IsNullOrWhiteSpace(
                        checklistBadgeText)
                        ? Visibility.Collapsed
                        : Visibility.Visible
            };

            ToolTipService.SetToolTip(
                checklistBadge,
                activity.ChecklistScanned
                    ? calendarChecklist.HasChecklist
                        ? $"Checklist: {calendarChecklist.Completed}/{calendarChecklist.Total} completadas · {calendarChecklist.Pending} pendientes"
                        : "Checklist revisado · sin tareas activas"
                    : "Calculando porcentaje del checklist…");

            cardContent.Children.Add(checklistBadge);

            if (useStackedLayout &&
                overlapCount > 1)
            {
                var hiddenCount =
                    Math.Max(
                        1,
                        overlapCount - 1);

                var overlapBadge = new Border
                {
                    MinWidth = 24,
                    Height = Math.Max(17, 19 * _calendarZoom),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(0, 0, 2, 2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(9),
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(
                                225,
                                88,
                                28,
                                135)),
                    BorderBrush =
                        new SolidColorBrush(
                            Color.FromArgb(
                                255,
                                192,
                                132,
                                252)),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = $"+{hiddenCount}",
                        FontSize = Math.Max(8, 8.5 * CalendarFontScale),
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.Bold,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    255,
                                    233,
                                    213,
                                    255)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        MaxLines = 1,
                        IsHitTestVisible = false
                    }
                };

                ToolTipService.SetToolTip(
                    overlapBadge,
                    $"{overlapCount} actividades se empalman en este horario. Pasa el cursor sobre cada tarjeta para traerla al frente.");

                cardContent.Children.Add(
                    overlapBadge);
            }

            var activityPriority =
                GetOneClickPriorityInfo(
                    activity,
                    person);

            // Visual especial únicamente para rtuzREVISION.
            // La detección es exacta, por lo que no afecta
            // prtuzREVISION, sprtuzREVISION ni zREVISION.
            var isRtuzReviewVisual =
                HasExactCalendarPhase(
                    activity,
                    "rtuzREVISION");

            // Capa verde interior suave para destacar revisiones activas
            // sin reemplazar la franja izquierda de prioridad.
            if (isRtuzReviewVisual)
            {
                cardContent.Children.Insert(
                    0,
                    new Border
                    {
                        Margin = new Thickness(1.5),
                        CornerRadius =
                            new CornerRadius(
                                Math.Max(
                                    4,
                                    5 * _calendarZoom)),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    38,
                                    52,
                                    211,
                                    153)),
                        BorderBrush =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    205,
                                    52,
                                    211,
                                    153)),
                        BorderThickness =
                            new Thickness(
                                Math.Max(
                                    1,
                                    1.35 * _calendarZoom)),
                        IsHitTestVisible = false
                    });
            }

            var baseZIndex =
                useStackedLayout
                    ? 20 +
                      Math.Min(
                          90,
                          safeLane * 6 +
                          sequenceIndex)
                    : 10;

            var button = new Button
            {
                Content = cardContent,
                Width = cardWidth,
                Height = height,
                Padding = new Thickness(
                    6 * _calendarZoom,
                    4 * _calendarZoom,
                    6 * _calendarZoom,
                    4 * _calendarZoom),
                HorizontalContentAlignment =
                    HorizontalAlignment.Stretch,
                VerticalContentAlignment =
                    VerticalAlignment.Top,
                Background =
                    isRtuzReviewVisual
                        ? new SolidColorBrush(
                            Color.FromArgb(
                                175,
                                6,
                                78,
                                59))
                        : GetNeutralCalendarActivityBrush(
                            activity.Status,
                            activity.StatusColor),
                // El fondo de la tarjeta se mantiene neutro para reducir
                // ruido visual. La franja izquierda conserva la prioridad
                // y los recordatorios usan un color fuerte independiente.
                BorderBrush =
                    new SolidColorBrush(
                        activityPriority.Color),
                BorderThickness = new Thickness(4, 1, 1, 1),
                CornerRadius =
                    new CornerRadius(6 * _calendarZoom),
                Tag = activity
            };

            var activityTooltipParts =
                new List<string>();

            if (activity.HasActivityDayRange)
            {
                activityTooltipParts.Add(
                    BuildActivityDayRangeTooltip(activity));
            }

            if (activity.HasWorkLog)
            {
                activityTooltipParts.Add(
                    $"Tiempo trabajado: {activity.WorkProgressLabel}" +
                    (string.IsNullOrWhiteSpace(activity.WorkLogDetail)
                        ? string.Empty
                        : $"\nSesiones: {activity.WorkLogDetail}"));
            }

            ToolTipService.SetToolTip(
                button,
                activityTooltipParts.Count > 0
                    ? string.Join(
                        "\n\n",
                        activityTooltipParts)
                    : null);

            button.KeyboardAccelerators.Clear();
            button.KeyboardAcceleratorPlacementMode =
                KeyboardAcceleratorPlacementMode.Hidden;

            button.PointerEntered +=
                (sender, args) =>
                {
                    // En grupos apilados la tarjeta bajo el cursor sube al
                    // frente sin alterar su posición ni la de las demás.
                    if (useStackedLayout)
                    {
                        Canvas.SetZIndex(
                            button,
                            650);
                    }

                    CalendarActivity_PointerEntered(
                        sender,
                        args);
                };

            button.PointerExited +=
                (sender, args) =>
                {
                    if (useStackedLayout &&
                        !ReferenceEquals(
                            _calendarDraggingButton,
                            button))
                    {
                        Canvas.SetZIndex(
                            button,
                            baseZIndex);
                    }

                    CalendarActivity_PointerExited(
                        sender,
                        args);
                };

            button.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(
                    CalendarActivityDrag_PointerPressed),
                handledEventsToo: true);

            button.AddHandler(
                UIElement.PointerMovedEvent,
                new PointerEventHandler(
                    CalendarActivityDrag_PointerMoved),
                handledEventsToo: true);

            button.AddHandler(
                UIElement.PointerReleasedEvent,
                new PointerEventHandler(
                    CalendarActivityDrag_PointerReleased),
                handledEventsToo: true);

            button.AddHandler(
                UIElement.PointerCanceledEvent,
                new PointerEventHandler(
                    CalendarActivityDrag_PointerCanceled),
                handledEventsToo: true);

            button.ContextFlyout =
                BuildCalendarActivityContextFlyout(
                    activity);

            button.DoubleTapped +=
                CalendarActivity_DoubleTapped;

            Canvas.SetLeft(button, left);
            Canvas.SetTop(button, top);
            Canvas.SetZIndex(
                button,
                baseZIndex);
            CalendarCanvas.Children.Add(button);

            if (!string.IsNullOrWhiteSpace(
                    activity.PageId))
            {
                if (!_calendarActivityVisuals.TryGetValue(
                        activity.PageId,
                        out var visuals))
                {
                    visuals =
                        new List<CalendarActivityVisual>();

                    _calendarActivityVisuals[
                        activity.PageId] = visuals;
                }

                visuals.Add(
                    new CalendarActivityVisual
                    {
                        Button = button,
                        TitleText = titleText,
                        ChecklistBadge = checklistBadge,
                        ChecklistText = checklistText,
                        CompactChecklistBadge =
                            compactBadge
                    });
            }
        }

        private void CalendarActivityDrag_PointerPressed(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not NotionCalendarActivity activity)
            {
                return;
            }

            if (activity.IsReviewMirror)
            {
                StatusText.Text =
                    "Estado: Esta es una tarjeta de seguimiento. Ábrela con doble clic; no se puede mover.";
                return;
            }

            if (activity.IsAutomationLocked)
            {
                StatusText.Text =
                    "Estado: La actividad está bloqueada para automatizaciones. Desbloquéala para moverla.";
                return;
            }

            var point =
                e.GetCurrentPoint(CalendarCanvas);

            if (!point.Properties.IsLeftButtonPressed)
                return;

            _calendarDraggingButton = button;
            _calendarDraggingActivity = activity;
            _calendarDragPointerId = e.Pointer.PointerId;
            _calendarDragStartPointerY = point.Position.Y;
            _calendarDragStartTop = Canvas.GetTop(button);
            _calendarDragActive = false;

            _calendarDragTimeText =
                (button.Content as StackPanel)?
                    .Children
                    .OfType<TextBlock>()
                    .FirstOrDefault(item =>
                        string.Equals(
                            item.Text,
                            activity.TimeLabel,
                            StringComparison.Ordinal));

            _calendarDragOriginalTimeLabel =
                _calendarDragTimeText?.Text ??
                activity.TimeLabel;

            button.CapturePointer(e.Pointer);
        }

        private void CalendarActivityDrag_PointerMoved(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (_calendarDraggingButton == null ||
                _calendarDraggingActivity == null ||
                e.Pointer.PointerId !=
                    _calendarDragPointerId)
            {
                return;
            }

            var point =
                e.GetCurrentPoint(CalendarCanvas);

            if (!point.Properties.IsLeftButtonPressed)
                return;

            var delta =
                point.Position.Y -
                _calendarDragStartPointerY;

            if (!_calendarDragActive &&
                Math.Abs(delta) < 5)
            {
                return;
            }

            _calendarDragActive = true;
            HideCalendarActivityPreviewFlyout();

            var minTop =
                CalendarHeaderHeight + 3;

            var maxTop =
                CalendarHeaderHeight +
                (CalendarEndHour - CalendarStartHour) *
                CalendarHourHeight -
                _calendarDraggingButton.Height;

            var top =
                Math.Clamp(
                    _calendarDragStartTop + delta,
                    minTop,
                    Math.Max(minTop, maxTop));

            Canvas.SetTop(
                _calendarDraggingButton,
                top);

            _calendarDraggingButton.Opacity = 0.78;
            Canvas.SetZIndex(
                _calendarDraggingButton,
                500);

            var minutes =
                Math.Round(
                    ((top - CalendarHeaderHeight - 3) /
                     CalendarHourHeight * 60d) / 15d) * 15d;

            var previewStart =
                _calendarSelectedDate.Date
                    .AddHours(CalendarStartHour)
                    .AddMinutes(minutes);

            var duration =
                _calendarDraggingActivity.End >
                _calendarDraggingActivity.Start
                    ? _calendarDraggingActivity.End -
                      _calendarDraggingActivity.Start
                    : TimeSpan.FromHours(1);

            var previewEnd =
                previewStart.Add(duration);

            if (_calendarDragTimeText != null)
            {
                _calendarDragTimeText.Text =
                    $"Mover a {previewStart:HH:mm} – {previewEnd:HH:mm}";
                _calendarDragTimeText.FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold;
                _calendarDragTimeText.Opacity = 1;
            }

            StatusText.Text =
                $"Estado: Mover a {previewStart:HH:mm}–" +
                $"{previewEnd:HH:mm}";

            e.Handled = true;
        }

        private async void CalendarActivityDrag_PointerReleased(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (_calendarDraggingButton == null ||
                _calendarDraggingActivity == null ||
                e.Pointer.PointerId !=
                    _calendarDragPointerId)
            {
                return;
            }

            var button =
                _calendarDraggingButton;

            var activity =
                _calendarDraggingActivity;

            button.ReleasePointerCapture(e.Pointer);
            button.Opacity = 1;

            var wasDragging =
                _calendarDragActive;

            if (!wasDragging &&
                _calendarDragTimeText != null)
            {
                _calendarDragTimeText.Text =
                    _calendarDragOriginalTimeLabel;
            }

            _calendarDraggingButton = null;
            _calendarDraggingActivity = null;
            _calendarDragPointerId = 0;
            _calendarDragActive = false;
            _calendarDragTimeText = null;
            _calendarDragOriginalTimeLabel = string.Empty;

            if (!wasDragging)
                return;

            _calendarSuppressNextActivityClick = true;
            _calendarSuppressActivityClickUntil =
                DateTimeOffset.UtcNow.AddMilliseconds(900);
            _calendarSuppressedActivityPageId =
                activity.PageId ?? string.Empty;
            e.Handled = true;

            var top =
                Canvas.GetTop(button);

            var minutes =
                Math.Round(
                    ((top - CalendarHeaderHeight - 3) /
                     CalendarHourHeight * 60d) / 15d) * 15d;

            var duration =
                activity.End > activity.Start
                    ? activity.End - activity.Start
                    : TimeSpan.FromHours(1);

            var totalMinutes =
                (CalendarEndHour - CalendarStartHour) * 60d;

            minutes = Math.Clamp(
                minutes,
                0,
                Math.Max(
                    0,
                    totalMinutes -
                    duration.TotalMinutes));

            var targetStart =
                _calendarSelectedDate.Date
                    .AddHours(CalendarStartHour)
                    .AddMinutes(minutes);

            await MoveCalendarActivityToTimeAsync(
                activity,
                targetStart);
        }

        private void CalendarActivityDrag_PointerCanceled(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (_calendarDraggingButton != null)
            {
                _calendarDraggingButton.Opacity = 1;
                _calendarDraggingButton.ReleasePointerCapture(
                    e.Pointer);
            }

            var shouldRedraw =
                _calendarDragActive;

            _calendarDraggingButton = null;
            _calendarDraggingActivity = null;
            _calendarDragPointerId = 0;
            _calendarDragActive = false;
            _calendarDragTimeText = null;
            _calendarDragOriginalTimeLabel = string.Empty;
            _calendarSuppressNextActivityClick = false;
            _calendarSuppressActivityClickUntil = DateTimeOffset.MinValue;
            _calendarSuppressedActivityPageId = string.Empty;

            if (shouldRedraw)
                DrawCalendar(_calendarActivities);
        }

        private async Task MoveCalendarActivityToTimeAsync(
            NotionCalendarActivity activity,
            DateTime targetStart)
        {
            if (activity?.IsAutomationLocked == true)
            {
                DrawCalendar(_calendarActivities);
                StatusText.Text =
                    "Estado: La actividad está bloqueada. Desbloquéala antes de cambiar su horario.";
                return;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                DrawCalendar(_calendarActivities);
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var oldStart =
                activity.Start;

            var oldEnd =
                activity.End;

            try
            {
                StatusText.Text =
                    $"Estado: Guardando nueva hora {targetStart:HH:mm}...";

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var updated =
                    await _notionCalendarService
                        .UpdateActivityScheduleAsync(
                            token,
                            activity,
                            targetStart,
                            cts.Token);

                activity.Start = updated.Start;
                activity.End = updated.End;
                activity.DatePropertyName =
                    updated.DatePropertyName;

                var currentDay =
                    await _notionCalendarService
                        .TryGetCachedDayAsync(
                            _calendarSelectedDate,
                            cts.Token);

                _calendarActivities =
                    currentDay ??
                    Array.Empty<NotionCalendarActivity>();

                DrawCalendar(_calendarActivities);

                StatusText.Text =
                    $"Estado: Actividad movida a " +
                    $"{updated.Start:HH:mm}–{updated.End:HH:mm} ✅";
            }
            catch (Exception ex)
            {
                activity.Start = oldStart;
                activity.End = oldEnd;
                DrawCalendar(_calendarActivities);

                StatusText.Text =
                    $"Estado: No se pudo cambiar la hora → {ex.Message}";
            }
        }

        private MenuFlyout BuildCalendarActivityContextFlyout(
            NotionCalendarActivity activity)
        {
            var flyout = new MenuFlyout();

            MenuFlyoutItem AddItem(
                string text,
                RoutedEventHandler handler)
            {
                var item = new MenuFlyoutItem
                {
                    Text = text,
                    Tag = activity
                };

                item.Click += handler;
                flyout.Items.Add(item);
                return item;
            }

            AddItem(
                "Abrir en Notion",
                CalendarContextOpen_Click);

            AddItem(
                "Enviar mensaje…",
                CalendarContextSendMessage_Click);

            AddItem(
                "Copiar nombre",
                CalendarContextCopyName_Click);

            AddItem(
                "Copiar URL de Notion",
                CalendarContextCopyUrl_Click);

            flyout.Items.Add(
                new MenuFlyoutSeparator());

            AddItem(
                "Copiar dominio",
                CalendarContextCopyDomain_Click);

            AddItem(
                "Ir al dominio",
                CalendarContextOpenDomain_Click);

            // La tarjeta espejo conserva el historial visual, pero no
            // puede modificar la actividad real.
            if (activity.IsReviewMirror)
                return flyout;

            flyout.Items.Add(
                new MenuFlyoutSeparator());

            AddItem(
                "Renombrar página…",
                CalendarContextRename_Click);

            AddItem(
                "Duplicar actividad…",
                CalendarContextDuplicate_Click);

            AddItem(
                "⏱ Registrar tiempo trabajado…",
                CalendarContextRegisterWork_Click);

            AddItem(
                activity.IsAutomationLocked
                    ? "🔓 Desbloquear automatización"
                    : "🔒 Bloquear automatización",
                CalendarContextToggleAutomationLock_Click);

            flyout.Items.Add(
                new MenuFlyoutSeparator());

            if (HasExactCalendarPhase(
                    activity,
                    "rtuzREVISION") &&
                activity.IsPendingReview &&
                !activity.IsReviewMirror &&
                CanCurrentUserResolveReview(activity))
            {
                AddItem(
                    "Aprobar revisión",
                    CalendarContextApproveReview_Click);

                AddItem(
                    "Regresar con correcciones…",
                    CalendarContextReturnReview_Click);
            }
            else if (CanSendCalendarActivityToReview(activity))
            {
                AddItem(
                    "Enviar a revisión…",
                    CalendarContextSendToReview_Click);
            }

            if (HasExactCalendarPhase(
                    activity,
                    "zREVISION"))
            {
                AddItem(
                    "Reasignar y pasar a prtuzREVISION…",
                    CalendarContextReassignApproved_Click);
            }

            AddItem(
                "Mover a papelera…",
                CalendarContextTrash_Click);

            flyout.Items.Add(
                new MenuFlyoutSeparator());

            AddItem(
                "Agregar / quitar Favoritos",
                CalendarContextBookmark_Click);

            return flyout;
        }

        private static NotionCalendarActivity?
            GetCalendarActivityFromMenuSender(
                object sender)
        {
            return sender is FrameworkElement element
                ? element.Tag as NotionCalendarActivity
                : null;
        }


        private async void CalendarContextRegisterWork_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null ||
                activity.IsReviewMirror)
            {
                return;
            }

            var currentEstimateMinutes =
                activity.EstimatedWorkMinutes > 0
                    ? activity.EstimatedWorkMinutes
                    : Math.Max(
                        1,
                        (int)Math.Round(
                            activity.EstimatedDuration.TotalMinutes));

            var suggestedSessionMinutes =
                Math.Max(
                    1,
                    Math.Min(
                        activity.RemainingWorkMinutes > 0
                            ? activity.RemainingWorkMinutes
                            : currentEstimateMinutes,
                        currentEstimateMinutes));

            var hoursBox =
                new NumberBox
                {
                    Header = "Horas",
                    Minimum = 0,
                    Maximum = 24,
                    SpinButtonPlacementMode =
                        NumberBoxSpinButtonPlacementMode.Compact,
                    Value =
                        suggestedSessionMinutes / 60
                };

            var minutesBox =
                new NumberBox
                {
                    Header = "Minutos",
                    Minimum = 0,
                    Maximum = 59,
                    SmallChange = 15,
                    SpinButtonPlacementMode =
                        NumberBoxSpinButtonPlacementMode.Compact,
                    Value =
                        suggestedSessionMinutes % 60
                };

            var workDate =
                new DatePicker
                {
                    Header = "Día trabajado",
                    Date =
                        new DateTimeOffset(
                            _calendarSelectedDate.Year,
                            _calendarSelectedDate.Month,
                            _calendarSelectedDate.Day,
                            0,
                            0,
                            0,
                            DateTimeOffset.Now.Offset),
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch
                };

            var continueTomorrow =
                new CheckBox
                {
                    Content =
                        "Continuar mañana con el tiempo restante",
                    IsChecked = false
                };

            var summary =
                new TextBlock
                {
                    Text =
                        $"Estimado original: " +
                        $"{FormatCalendarWorkMinutes(currentEstimateMinutes)}\\n" +
                        $"Ya registrado: " +
                        $"{FormatCalendarWorkMinutes(activity.WorkedMinutes)}" +
                        (string.IsNullOrWhiteSpace(
                            activity.WorkLogDetail)
                            ? string.Empty
                            : $"\\nHistorial: {activity.WorkLogDetail}"),
                    TextWrapping =
                        TextWrapping.Wrap,
                    Opacity = 0.78
                };

            var timeGrid =
                new Grid
                {
                    ColumnSpacing = 8
                };

            timeGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            timeGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            Grid.SetColumn(
                hoursBox,
                0);
            Grid.SetColumn(
                minutesBox,
                1);

            timeGrid.Children.Add(hoursBox);
            timeGrid.Children.Add(minutesBox);

            var panel =
                new StackPanel
                {
                    Spacing = 10,
                    Width = 430
                };

            panel.Children.Add(summary);
            panel.Children.Add(workDate);
            panel.Children.Add(timeGrid);
            panel.Children.Add(continueTomorrow);

            var dialog =
                new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "⏱ Registrar tiempo trabajado",
                    Content = panel,
                    PrimaryButtonText = "Guardar tiempo",
                    CloseButtonText = "Cancelar",
                    DefaultButton =
                        ContentDialogButton.Primary
                };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            var hours =
                double.IsNaN(hoursBox.Value)
                    ? 0
                    : Math.Max(
                        0,
                        (int)Math.Round(hoursBox.Value));

            var minutes =
                double.IsNaN(minutesBox.Value)
                    ? 0
                    : Math.Clamp(
                        (int)Math.Round(minutesBox.Value),
                        0,
                        59);

            var workedMinutes =
                (int)hours * 60 +
                (int)minutes;

            if (workedMinutes <= 0)
            {
                StatusText.Text =
                    "Estado: Indica al menos 1 minuto trabajado.";
                return;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            try
            {
                StatusText.Text =
                    "Estado: Guardando tiempo trabajado...";

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var result =
                    await _notionCalendarService
                        .RegisterActivityWorkAsync(
                            token,
                            activity,
                            workDate.Date.Date,
                            workedMinutes,
                            cts.Token);

                activity.EstimatedWorkMinutes =
                    result.Activity.EstimatedWorkMinutes;
                activity.WorkedMinutes =
                    result.Activity.WorkedMinutes;
                activity.WorkLogDetail =
                    result.Activity.WorkLogDetail;

                var continued =
                    continueTomorrow.IsChecked == true &&
                    activity.RemainingWorkMinutes > 0;

                if (continued)
                {
                    var nextDate =
                        _calendarSelectedDate.Date.AddDays(1);

                    var nextStart =
                        nextDate.Add(
                            activity.Start.TimeOfDay);

                    var nextEnd =
                        nextStart.AddMinutes(
                            activity.RemainingWorkMinutes);

                    var audit =
                        $"ANFETA · continuidad · " +
                        $"{DateTime.Now:yyyy-MM-dd HH:mm} · " +
                        $"restante {FormatCalendarWorkMinutes(activity.RemainingWorkMinutes)}";

                    var scheduleResult =
                        await _notionCalendarService
                            .UpdateActivityScheduleWithAuditAsync(
                                token,
                                activity,
                                nextStart,
                                nextEnd,
                                audit,
                                cts.Token);

                    activity.Start =
                        scheduleResult.Activity.Start;
                    activity.End =
                        scheduleResult.Activity.End;
                    activity.DatePropertyName =
                        scheduleResult.Activity.DatePropertyName;
                }

                var currentDay =
                    await _notionCalendarService
                        .TryGetCachedDayAsync(
                            _calendarSelectedDate,
                            cts.Token);

                _calendarActivities =
                    currentDay ??
                    Array.Empty<NotionCalendarActivity>();

                DrawCalendarPreservingView(
                    _calendarActivities,
                    force: true);

                StatusText.Text =
                    continued
                        ? $"Estado: Tiempo registrado ✅ · " +
                          $"Trabajado {FormatCalendarWorkMinutes(activity.WorkedMinutes)} · " +
                          $"continúa mañana {activity.Start:HH:mm}–{activity.End:HH:mm}"
                        : $"Estado: Tiempo registrado ✅ · " +
                          $"{activity.WorkProgressLabel}";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: Notion tardó demasiado en guardar el tiempo.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo registrar el tiempo → {ex.Message}";
            }
        }

        private static string FormatCalendarWorkMinutes(
            int totalMinutes)
        {
            totalMinutes =
                Math.Max(
                    0,
                    totalMinutes);

            var hours =
                totalMinutes / 60;

            var minutes =
                totalMinutes % 60;

            if (hours > 0 && minutes > 0)
                return $"{hours}H {minutes}M";

            if (hours > 0)
                return $"{hours}H";

            return $"{minutes}M";
        }

        private static Anfeta.UI.Models.Weblab.SearchResultRow
            BuildCalendarSearchRow(
                NotionCalendarActivity activity)
        {
            return new Anfeta.UI.Models.Weblab.SearchResultRow
            {
                NodeId = activity.PageId,
                ExternalId = activity.PageId,
                ExternalUrl = activity.PageUrl,
                ExternalSourceName = "Revisiones",
                Name = $"[Revisiones] {activity.Title}",
                Target = activity.PageUrl,
                Type = "NOTION_PAGE",
                Source =
                    Anfeta.UI.Models.Weblab.SearchSource.Notion,
                ProjectUpdateStatus =
                    string.IsNullOrWhiteSpace(
                        activity.UpdateText)
                        ? activity.Status
                        : activity.UpdateText,
                ScheduledDate =
                    activity.Start.ToString(
                        "yyyy-MM-dd HH:mm",
                        CultureInfo.InvariantCulture),
                SearchText =
                    $"Revisiones {activity.Title} {activity.Person} " +
                    $"{activity.Project} {activity.Status} " +
                    $"{activity.UpdateText}"
            };
        }

        private async void CalendarContextToggleAutomationLock_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            await ToggleCalendarActivityAutomationLockAsync(
                activity);
        }

        private async Task<bool> ToggleCalendarActivityAutomationLockAsync(
            NotionCalendarActivity activity)
        {
            if (activity == null ||
                activity.IsReviewMirror ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                return false;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return false;
            }

            var nextLocked =
                !activity.IsAutomationLocked;

            try
            {
                ShowLoadingState(
                    nextLocked
                        ? "Estado: Bloqueando actividad…"
                        : "Estado: Desbloqueando actividad…",
                    activity.Title);

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                await _notionCalendarService
                    .UpdateActivityAutomationLockAsync(
                        token,
                        activity,
                        nextLocked,
                        cts.Token);

                foreach (var item in _calendarActivities.Where(item =>
                             string.Equals(
                                 item.PageId,
                                 activity.PageId,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    item.IsAutomationLocked = nextLocked;
                }

                HideCalendarActivityPreviewFlyout();
                DrawCalendarPreservingView(
                    _calendarActivities,
                    force: true);

                StatusText.Text = nextLocked
                    ? "Estado: Actividad bloqueada. One Click Schedule y Procesar ayer no podrán moverla ✅"
                    : "Estado: Actividad desbloqueada para automatizaciones ✅";

                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo cambiar el bloqueo → {ex.Message}";
                return false;
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async void CalendarContextOpen_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            await OpenCalendarActivityAsync(activity);
        }

        private async void CalendarContextSendMessage_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            HideCalendarActivityPreviewFlyout();

            await ShowCalendarMessageComposerAsync(
                activity);
        }

        private async Task OpenCalendarActivityAsync(
            NotionCalendarActivity activity)
        {
            if (activity == null)
                return;

            await OpenNotionPageWithFallbackAsync(
                activity.PageUrl,
                desktopSuccessStatus:
                    "Actividad abierta en Notion Desktop",
                browserSuccessStatus:
                    "Actividad abierta en el navegador",
                failureStatus:
                    "No se pudo abrir la actividad",
                invalidUrlStatus:
                    "La actividad no tiene una URL válida de Notion");
        }


        private void CalendarContextCopyName_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            CopyCalendarText(
                activity.Title,
                "Estado: Nombre copiado ✅");
        }

        private void CalendarContextCopyUrl_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            CopyCalendarText(
                activity.PageUrl,
                "Estado: URL de Notion copiada ✅");
        }

        private void CalendarContextCopyDomain_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            var row =
                BuildCalendarSearchRow(activity);

            var domain =
                TryExtractFirstDomain(row);

            if (string.IsNullOrWhiteSpace(domain))
            {
                StatusText.Text =
                    "Estado: No se encontró un dominio en esta revisión.";
                return;
            }

            CopyCalendarText(
                domain,
                $"Estado: Dominio copiado ✅ {domain}");
        }

        private async void CalendarContextOpenDomain_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            var domain =
                TryExtractFirstDomain(
                    BuildCalendarSearchRow(activity));

            if (string.IsNullOrWhiteSpace(domain))
            {
                StatusText.Text =
                    "Estado: No se encontró un dominio en esta revisión.";
                return;
            }

            try
            {
                var opened =
                    await Launcher.LaunchUriAsync(
                        new Uri($"https://{domain}"));

                StatusText.Text = opened
                    ? $"Estado: Dominio abierto ✅ {domain}"
                    : $"Estado: No se pudo abrir {domain}.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: Error abriendo dominio → {ex.Message}";
            }
        }

        private async void CalendarContextRename_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            await RenameNotionPageAsync(
                BuildCalendarSearchRow(activity));

            await LoadCalendarDayAsync(
                preferCache: false,
                forceRefresh: true);
        }

        private string BuildNextCalendarDuplicateTitle(
            NotionCalendarActivity activity)
        {
            var originalTitle =
                (activity?.Title ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(originalTitle))
                originalTitle = "Actividad";

            // Si se vuelve a duplicar una copia, "Actividad (1)" se trata
            // como parte de la misma serie y continúa con (2), (3), etc.
            var sourceMatch =
                Regex.Match(
                    originalTitle,
                    @"^(?<base>.+?)\s+\((?<number>\d+)\)$",
                    RegexOptions.CultureInvariant);

            var baseTitle =
                sourceMatch.Success
                    ? sourceMatch.Groups["base"].Value.Trim()
                    : originalTitle;

            var nextNumber = 1;

            foreach (var candidate in
                     _calendarActivities ??
                     Array.Empty<NotionCalendarActivity>())
            {
                var candidateTitle =
                    (candidate?.Title ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(candidateTitle))
                    continue;

                var match =
                    Regex.Match(
                        candidateTitle,
                        $@"^{Regex.Escape(baseTitle)}\s+\((?<number>\d+)\)$",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);

                if (!match.Success ||
                    !int.TryParse(
                        match.Groups["number"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var number))
                {
                    continue;
                }

                nextNumber =
                    Math.Max(
                        nextNumber,
                        number + 1);
            }

            return $"{baseTitle} ({nextNumber})";
        }

        private async void CalendarContextDuplicate_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null ||
                activity.IsReviewMirror ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                return;
            }

            var duplicateTitle =
                BuildNextCalendarDuplicateTitle(activity);

            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Duplicar actividad",
                Content =
                    $"Se creará una actividad nueva como:\n\n{duplicateTitle}\n\n" +
                    "Conservará las mismas propiedades editables " +
                    "(asignación, Fecha POR Hacer, estado, etc.). " +
                    "El contenido/body e instrucciones de la página NO se copiarán.",
                PrimaryButtonText = "Duplicar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await confirmation.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            try
            {
                HideCalendarActivityPreviewFlyout();

                ShowLoadingState(
                    "Estado: Duplicando actividad…",
                    duplicateTitle);

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(3));

                // Se toma un ancla anterior a la creación para que la consulta
                // incremental pueda incorporar la nueva página sin reconstruir
                // toda la base de Revisiones.
                var refreshAnchor =
                    DateTimeOffset.UtcNow.AddSeconds(-8);

                var actions =
                    new NotionPageActionsService();

                var duplicate =
                    await actions.DuplicatePageWithoutBodyAsync(
                        token,
                        activity.PageId,
                        NotionFilePageService.RevisionesDataSourceId,
                        duplicateTitle,
                        cts.Token);

                var refreshed = false;

                try
                {
                    refreshed =
                        await _notionCalendarService
                            .RefreshChangedSinceAsync(
                                token,
                                refreshAnchor,
                                cts.Token);

                    if (refreshed)
                    {
                        var current =
                            await _notionCalendarService
                                .TryGetCachedDayAsync(
                                    _calendarSelectedDate,
                                    cts.Token);

                        if (current != null)
                        {
                            _calendarActivities = current;

                            ApplyCachedCalendarReviewFlow(
                                _calendarActivities);

                            DrawCalendarPreservingView(
                                _calendarActivities,
                                force: true);

                            StartCalendarIncrementalChecklistRefresh(
                                _notionCalendarService.LastChangedPageIds,
                                _calendarSelectedDate.Date,
                                _calendarLoadVersion);
                        }
                    }
                }
                catch (Exception refreshException)
                {
                    Debug.WriteLine(
                        $"[CALENDAR_DUPLICATE_REFRESH] " +
                        refreshException);
                }

                var skippedNote =
                    duplicate.SkippedProperties.Count == 0
                        ? string.Empty
                        :
                            $" · {duplicate.SkippedProperties.Count} propiedad(es) " +
                            "automáticas/no editables se regenerarán en Notion";

                StatusText.Text =
                    $"Estado: Actividad duplicada como {duplicateTitle} sin body ✅" +
                    skippedNote +
                    (refreshed
                        ? string.Empty
                        : " · pulsa Actualizar si todavía no aparece en el calendario");
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: La duplicación tardó demasiado y fue cancelada.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo duplicar la actividad → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async void CalendarContextTrash_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            await MoveNotionPagesToTrashAsync(
                new List<
                    Anfeta.UI.Models.Weblab.SearchResultRow>
                {
                    BuildCalendarSearchRow(activity)
                });

            await LoadCalendarDayAsync(
                preferCache: false,
                forceRefresh: true);
        }

        private async void CalendarContextBookmark_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            await ToggleBookmarkAsync(
                BuildCalendarSearchRow(activity));
        }

        private void CopyCalendarText(
            string text,
            string status)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var package =
                new Windows.ApplicationModel.DataTransfer
                    .DataPackage();

            package.SetText(text);

            Windows.ApplicationModel.DataTransfer
                .Clipboard.SetContent(package);

            StatusText.Text = status;
        }

        private void CalendarPeopleButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var all = new MenuFlyoutItem
            {
                Text = "Seleccionar todas"
            };
            all.Click += (_, __) =>
            {
                _calendarSelectedPeople.Clear();

                foreach (var person in ActiveCalendarPeople)
                    _calendarSelectedPeople.Add(person);

                SaveCalendarPreferences();
                DrawCalendar(_calendarActivities);
            };
            flyout.Items.Add(all);

            var clear = new MenuFlyoutItem
            {
                Text = "Limpiar selección"
            };
            clear.Click += (_, __) =>
            {
                _calendarSelectedPeople.Clear();
                SaveCalendarPreferences();
                DrawCalendar(_calendarActivities);
            };
            flyout.Items.Add(clear);
            flyout.Items.Add(new MenuFlyoutSeparator());

            foreach (var person in _calendarPeopleOrder)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = person,
                    IsChecked =
                        _calendarSelectedPeople.Contains(person),
                    Tag = person
                };

                item.Click += (_, __) =>
                {
                    if (item.IsChecked)
                        _calendarSelectedPeople.Add(person);
                    else
                        _calendarSelectedPeople.Remove(person);

                    SaveCalendarPreferences();
                    DrawCalendar(_calendarActivities);
                };

                flyout.Items.Add(item);
            }

            flyout.ShowAt(CalendarPeopleButton);
        }

        private void CalendarZoomIn_Click(
            object sender,
            RoutedEventArgs e)
            => ChangeCalendarZoom(0.10);

        private void CalendarZoomOut_Click(
            object sender,
            RoutedEventArgs e)
            => ChangeCalendarZoom(-0.10);

        private void ChangeCalendarZoom(double delta)
        {
            var nextZoom =
                Math.Clamp(
                    Math.Round(
                        _calendarZoom + delta,
                        2),
                    0.70,
                    1.40);

            if (Math.Abs(
                    nextZoom -
                    _calendarZoom) < 0.001)
            {
                return;
            }

            _calendarZoom = nextZoom;

            SaveCalendarPreferences();
            DrawCalendar(_calendarActivities);
        }

        private void QueueCalendarWheelZoom(
            double delta)
        {
            _calendarPendingZoomDelta += delta;

            if (_calendarZoomDebounceTimer == null)
            {
                _calendarZoomDebounceTimer =
                    new DispatcherTimer
                    {
                        Interval =
                            TimeSpan.FromMilliseconds(140)
                    };

                _calendarZoomDebounceTimer.Tick +=
                    (_, __) =>
                    {
                        _calendarZoomDebounceTimer.Stop();

                        var pending =
                            _calendarPendingZoomDelta;

                        _calendarPendingZoomDelta = 0;

                        if (Math.Abs(pending) < 0.001)
                            return;

                        ChangeCalendarZoom(
                            pending > 0
                                ? 0.10
                                : -0.10);
                    };
            }

            _calendarZoomDebounceTimer.Stop();
            _calendarZoomDebounceTimer.Start();
        }

        private void EnsureCalendarWheelHandler()
        {
            if (_calendarWheelHandlerHooked ||
                CalendarCanvas == null)
            {
                return;
            }

            // Se engancha al contenido interno para interceptar la rueda
            // antes de que el ScrollViewer aplique desplazamiento vertical.
            CalendarCanvas.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(
                    CalendarScrollViewer_PointerWheelChanged),
                handledEventsToo: true);

            _calendarWheelHandlerHooked = true;
        }

        private void CalendarScrollViewer_PointerWheelChanged(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (CalendarScrollViewer == null)
                return;

            var delta =
                e.GetCurrentPoint(CalendarCanvas)
                    .Properties
                    .MouseWheelDelta;

            if (delta == 0)
                return;

            if (IsCalendarControlDown())
            {
                // Se acumulan varios pulsos y se redibuja una sola vez.
                // Esto evita reconstruir cientos de controles por cada
                // pequeña señal de la rueda.
                QueueCalendarWheelZoom(
                    delta > 0
                        ? 0.10
                        : -0.10);

                e.Handled = true;
                return;
            }

            if (!IsCalendarShiftDown())
                return;

            var verticalBefore =
                CalendarScrollViewer.VerticalOffset;

            var step =
                Math.Max(
                    42,
                    CalendarScrollViewer.ViewportWidth * 0.055);

            var nextHorizontal =
                Math.Clamp(
                    CalendarScrollViewer.HorizontalOffset -
                    Math.Sign(delta) * step,
                    0,
                    CalendarScrollViewer.ScrollableWidth);

            // Movimiento horizontal corto para evitar brincos.
            CalendarScrollViewer.ChangeView(
                nextHorizontal,
                verticalBefore,
                null,
                disableAnimation: false);

            _calendarStableVerticalOffset =
                verticalBefore;

            UpdateCalendarStickyElements();
            e.Handled = true;
        }

        private void CalendarScrollViewer_ViewChanged(
            object sender,
            ScrollViewerViewChangedEventArgs e)
        {
            if (CalendarScrollViewer == null)
                return;

            if (!IsCalendarShiftDown())
            {
                _calendarStableVerticalOffset =
                    CalendarScrollViewer.VerticalOffset;
            }

            UpdateCalendarStickyElements();
        }

        private void UpdateCalendarStickyElements()
        {
            if (CalendarScrollViewer == null)
                return;

            var horizontal =
                CalendarScrollViewer.HorizontalOffset;

            var vertical =
                CalendarScrollViewer.VerticalOffset;

            foreach (var header in _calendarStickyHeaders)
                Canvas.SetTop(header, vertical + 2);

            foreach (var hour in _calendarStickyHours)
            {
                var baseLeft =
                    hour.Tag is CalendarStickyPosition position
                        ? position.Left
                        : 8;

                Canvas.SetLeft(hour, horizontal + baseLeft);
            }

            if (_calendarStickyCorner != null)
            {
                Canvas.SetLeft(
                    _calendarStickyCorner,
                    horizontal + 10);

                Canvas.SetTop(
                    _calendarStickyCorner,
                    vertical + 17 * _calendarZoom);
            }
        }

        private sealed record CalendarStickyPosition(
            double Left,
            double Top);

        private void LoadCalendarPreferences()
        {
            if (_calendarPreferencesLoaded)
                return;

            _calendarPreferencesLoaded = true;

            var values =
                ApplicationData.Current.LocalSettings.Values;

            if (values.TryGetValue(
                    LS_CalendarZoom,
                    out var zoomRaw) &&
                double.TryParse(
                    zoomRaw?.ToString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var zoom))
            {
                _calendarZoom =
                    Math.Clamp(zoom, 0.70, 1.40);
            }

            var savedSelectionVersion =
                values.TryGetValue(
                    LS_CalendarPeopleSelectionVersion,
                    out var selectionVersionRaw) &&
                int.TryParse(
                    selectionVersionRaw?.ToString(),
                    out var selectionVersion)
                    ? selectionVersion
                    : 0;

            if (savedSelectionVersion < CalendarPeopleSelectionVersion)
            {
                _calendarSelectedPeople.Clear();

                foreach (var person in ActiveCalendarPeople)
                    _calendarSelectedPeople.Add(person);

                values[LS_CalendarPeopleSelectionVersion] =
                    CalendarPeopleSelectionVersion;

                values[LS_CalendarPeople] =
                    string.Join("|", ActiveCalendarPeople);
            }

            var selected =
                values[LS_CalendarPeople] as string;

            if (!string.IsNullOrWhiteSpace(selected))
            {
                _calendarSelectedPeople.Clear();

                foreach (var person in selected.Split('|'))
                {
                    if (ActiveCalendarPeople.Contains(
                            person,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        _calendarSelectedPeople.Add(person);
                    }
                }

                // Las personas/columnas agregadas en versiones nuevas no deben
                // quedar ocultas por preferencias antiguas.
                foreach (var person in ActiveCalendarPeople)
                {
                    if (!_calendarSelectedPeople.Contains(person) &&
                        string.Equals(
                            person,
                            "Sin asignar",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _calendarSelectedPeople.Add(person);
                    }
                }
            }

            var savedWidths =
                values[LS_CalendarColumnWidths] as string;

            if (!string.IsNullOrWhiteSpace(savedWidths))
            {
                foreach (var entry in savedWidths.Split('|'))
                {
                    var parts = entry.Split('=');

                    if (parts.Length != 2)
                        continue;

                    var person = parts[0].Trim();

                    if (!ActiveCalendarPeople.Contains(
                            person,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (double.TryParse(
                            parts[1],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var width))
                    {
                        _calendarColumnWidths[person] =
                            Math.Clamp(
                                width,
                                CalendarMinPersonColumnWidth,
                                CalendarMaxPersonColumnWidth);
                    }
                }
            }

            var order =
                values[LS_CalendarOrder] as string;

            if (!string.IsNullOrWhiteSpace(order))
            {
                var saved = order
                    .Split('|')
                    .Where(person =>
                        ActiveCalendarPeople.Contains(
                            person,
                            StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var person in ActiveCalendarPeople)
                {
                    if (!saved.Contains(
                            person,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        saved.Add(person);
                    }
                }

                _calendarPeopleOrder.Clear();
                _calendarPeopleOrder.AddRange(saved);
            }
        }

        private void SaveCalendarPreferences()
        {
            var values =
                ApplicationData.Current.LocalSettings.Values;

            values[LS_CalendarZoom] =
                _calendarZoom.ToString(
                    CultureInfo.InvariantCulture);

            values[LS_CalendarPeopleSelectionVersion] =
                CalendarPeopleSelectionVersion;

            values[LS_CalendarPeople] =
                string.Join(
                    "|",
                    _calendarPeopleOrder.Where(person =>
                        _calendarSelectedPeople.Contains(person)));

            values[LS_CalendarOrder] =
                string.Join("|", _calendarPeopleOrder);

            values[LS_CalendarColumnWidths] =
                string.Join(
                    "|",
                    _calendarPeopleOrder.Select(person =>
                        $"{person}={GetStoredCalendarColumnWidth(person).ToString(CultureInfo.InvariantCulture)}"));
        }

        private void ResolveCalendarColumnLayout(
            IReadOnlyList<string> persons,
            double viewportWidth)
        {
            _calendarResolvedColumnWidths.Clear();
            _calendarResolvedColumnLefts.Clear();

            if (persons.Count == 0)
                return;

            var storedScaledWidths = persons
                .Select(person =>
                    GetStoredCalendarColumnWidth(person) *
                    _calendarZoom)
                .ToList();

            var available =
                Math.Max(
                    0,
                    viewportWidth -
                    CalendarTimeColumnWidth);

            var missing =
                Math.Max(
                    0,
                    available -
                    storedScaledWidths.Sum());

            var equalExtra =
                missing / persons.Count;

            var currentLeft =
                CalendarTimeColumnWidth;

            for (var index = 0;
                 index < persons.Count;
                 index++)
            {
                var person = persons[index];

                var resolvedWidth =
                    storedScaledWidths[index] +
                    equalExtra;

                _calendarResolvedColumnLefts[person] =
                    currentLeft;

                _calendarResolvedColumnWidths[person] =
                    resolvedWidth;

                currentLeft += resolvedWidth;
            }
        }

        private double GetStoredCalendarColumnWidth(
            string person)
        {
            return _calendarColumnWidths.TryGetValue(
                    person,
                    out var width)
                ? Math.Clamp(
                    width,
                    CalendarMinPersonColumnWidth,
                    CalendarMaxPersonColumnWidth)
                : CalendarBasePersonColumnWidth;
        }

        private double GetResolvedCalendarColumnWidth(
            string person)
        {
            return _calendarResolvedColumnWidths.TryGetValue(
                    person,
                    out var width)
                ? width
                : CalendarDefaultPersonColumnWidth;
        }

        private double GetResolvedCalendarColumnLeft(
            string person)
        {
            return _calendarResolvedColumnLefts.TryGetValue(
                    person,
                    out var left)
                ? left
                : CalendarTimeColumnWidth;
        }

        private async void CalendarPersonPreview_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var person =
                (element.Tag?.ToString() ??
                 string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(person))
                return;

            ShowCalendarPersonPreview(person);

            var activities =
                GetCalendarPersonPreviewActivities(person);

            await HydrateCalendarChecklistStatsAsync(
                activities,
                $"Cargando avance de {person}",
                forceRefresh: false);

            if (CalendarPersonPreviewPanel?.Visibility ==
                    Visibility.Visible &&
                string.Equals(
                    _calendarPersonPreviewPerson,
                    person,
                    StringComparison.OrdinalIgnoreCase))
            {
                RenderCalendarPersonPreviewItems(person);
                UpdateCalendarChecklistVisuals(
                    activities.Select(
                        activity => activity.PageId));
            }
        }

        private void CalendarPersonPreviewClose_Click(
            object sender,
            RoutedEventArgs e)
        {
            CloseCalendarPersonPreviewPanel();
        }

        private void ShowCalendarPersonPreview(
            string person)
        {
            if (CalendarPersonPreviewPanel == null ||
                CalendarPersonPreviewItems == null)
            {
                return;
            }

            person =
                NormalizeCalendarPerson(person);

            if (string.IsNullOrWhiteSpace(person))
                return;

            var personChanged =
                !string.Equals(
                    _calendarPersonPreviewPerson,
                    person,
                    StringComparison.OrdinalIgnoreCase);

            if (personChanged ||
                _calendarPersonPreviewCts == null ||
                _calendarPersonPreviewCts.IsCancellationRequested)
            {
                try
                {
                    _calendarPersonPreviewCts?.Cancel();
                    _calendarPersonPreviewCts?.Dispose();
                }
                catch
                {
                }

                _calendarPersonPreviewCts =
                    new CancellationTokenSource();
            }

            _calendarPersonPreviewPerson = person;

            CalendarPersonPreviewPanel.Visibility =
                Visibility.Visible;

            CalendarPersonPreviewTitle.Text =
                $"Actividades de {person}";

            CalendarPersonPreviewDate.Text =
                FormatCalendarDate(_calendarSelectedDate);

            RenderCalendarPersonPreviewItems(person);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_calendarViewActive)
                    DrawCalendar(_calendarActivities);
            });
        }

        private IReadOnlyList<NotionCalendarActivity>
            GetCalendarPersonPreviewActivities(
                string person)
        {
            var expanded =
                ExpandCalendarReviewActivities(
                    _calendarActivities);

            var phaseFiltered =
                string.IsNullOrWhiteSpace(
                    _calendarPhaseFilter)
                    ? expanded
                    : expanded
                        .Where(activity =>
                            ContainsExactCalendarPart(
                                BuildCalendarActivitySearchableText(
                                    activity),
                                _calendarPhaseFilter))
                        .ToList();

            var visible =
                FilterCalendarActivities(
                    phaseFiltered,
                    _calendarSearchQuery);

            return visible
                .Where(activity =>
                    SplitPersons(activity.Person)
                        .Select(NormalizeCalendarPerson)
                        .Any(candidate =>
                            string.Equals(
                                candidate,
                                person,
                                StringComparison.OrdinalIgnoreCase)))
                .GroupBy(activity =>
                    string.Join(
                        "|",
                        activity.PageId,
                        activity.Person,
                        activity.IsReviewMirror,
                        activity.Start.ToString("O")),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(activity => activity.Start)
                .ThenBy(activity => activity.End)
                .ThenBy(activity => activity.Title)
                .ToList();
        }

        private NotionChecklistStats GetCalendarChecklistStats(
            NotionCalendarActivity activity)
        {
            if (activity == null)
                return new NotionChecklistStats(0, 0);

            if (!string.IsNullOrWhiteSpace(activity.PageId) &&
                _oneClickChecklistStats.TryGetValue(
                    activity.PageId,
                    out var cached))
            {
                return cached;
            }

            return new NotionChecklistStats(
                activity.ChecklistTotal,
                activity.ChecklistCompleted);
        }

        private static int GetChecklistPercentage(
            NotionChecklistStats stats)
        {
            return stats.Total <= 0
                ? 0
                : Math.Clamp(
                    (int)Math.Round(
                        stats.Completed * 100d / stats.Total),
                    0,
                    100);
        }

        private static string FormatCalendarChecklistLabel(
            NotionCalendarActivity activity,
            NotionChecklistStats stats)
        {
            if (activity == null ||
                !activity.ChecklistScanned)
            {
                return "Pendiente de cargar";
            }

            return stats.HasChecklist
                ? $"{stats.Completed}/{stats.Total} · {GetChecklistPercentage(stats)}% · {stats.Pending} pendiente(s)"
                : "Sin checklist nativo en el contenido de Notion";
        }

        private async Task HydrateCalendarChecklistStatsAsync(
            IReadOnlyList<NotionCalendarActivity> activities,
            string stage,
            bool forceRefresh,
            CancellationToken cancellationToken = default,
            Action<NotionCalendarActivity, int, int>?
                itemCompleted = null)
        {
            var unique =
                (activities ?? Array.Empty<NotionCalendarActivity>())
                    .Where(activity =>
                        activity != null &&
                        !activity.IsReviewMirror &&
                        !string.IsNullOrWhiteSpace(activity.PageId))
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            if (unique.Count == 0)
                return;

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
                return;

            for (var index = 0;
                 index < unique.Count;
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var activity = unique[index];

                if (!forceRefresh &&
                    activity.ChecklistScanned)
                {
                    _oneClickChecklistStats[activity.PageId] =
                        new NotionChecklistStats(
                            activity.ChecklistTotal,
                            activity.ChecklistCompleted);

                    itemCompleted?.Invoke(
                        activity,
                        index + 1,
                        unique.Count);

                    continue;
                }

                StatusText.Text =
                    $"Estado: {stage} {index + 1} de {unique.Count}…";

                try
                {
                    using var cts =
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                cancellationToken);

                    cts.CancelAfter(
                        TimeSpan.FromMinutes(2));

                    var stats =
                        await _notionCalendarService
                            .GetChecklistStatsAsync(
                                token,
                                activity.PageId,
                                cts.Token,
                                forceRefresh);

                    activity.ChecklistScanned = true;
                    activity.ChecklistTotal = stats.Total;
                    activity.ChecklistCompleted = stats.Completed;
                    _oneClickChecklistStats[activity.PageId] = stats;
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    activity.ChecklistScanned = false;
                    activity.ChecklistTotal = 0;
                    activity.ChecklistCompleted = 0;
                    _oneClickChecklistStats.Remove(
                        activity.PageId);
                }
                catch
                {
                    // Un error temporal no se interpreta como "sin checklist".
                    // Se deja pendiente para permitir un nuevo intento.
                    activity.ChecklistScanned = false;
                    activity.ChecklistTotal = 0;
                    activity.ChecklistCompleted = 0;
                    _oneClickChecklistStats.Remove(
                        activity.PageId);
                }

                itemCompleted?.Invoke(
                    activity,
                    index + 1,
                    unique.Count);
            }
        }

        private void StartCalendarIncrementalChecklistRefresh(
            IEnumerable<string> pageIds,
            DateTime requestedDate,
            long loadVersion)
        {
            var ids =
                (pageIds ?? Array.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (ids.Count == 0)
                return;

            // Se consultan únicamente los PageId que Notion reportó como
            // modificados y que siguen visibles en este día. Esto también
            // cubre páginas nuevas (por ejemplo, una actividad duplicada).
            var snapshot =
                _calendarActivities
                    .Where(activity =>
                        activity != null &&
                        !activity.IsReviewMirror &&
                        !string.IsNullOrWhiteSpace(activity.PageId) &&
                        ids.Contains(activity.PageId))
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            if (snapshot.Count == 0)
                return;

            try
            {
                _calendarIncrementalChecklistCts?.Cancel();
                _calendarIncrementalChecklistCts?.Dispose();
            }
            catch
            {
            }

            var cts = new CancellationTokenSource();
            _calendarIncrementalChecklistCts = cts;

            _ = RefreshCalendarChecklistForChangedPagesAsync(
                snapshot,
                requestedDate.Date,
                loadVersion,
                cts.Token);
        }

        private async Task RefreshCalendarChecklistForChangedPagesAsync(
            IReadOnlyList<NotionCalendarActivity> activities,
            DateTime requestedDate,
            long loadVersion,
            CancellationToken cancellationToken)
        {
            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token) ||
                activities == null || activities.Count == 0)
            {
                return;
            }

            var changedCount = 0;

            try
            {
                for (var index = 0; index < activities.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var activity = activities[index];
                    var previous = new NotionChecklistStats(
                        activity.ChecklistTotal,
                        activity.ChecklistCompleted);

                    try
                    {
                        using var itemCts =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken);
                        itemCts.CancelAfter(TimeSpan.FromMinutes(2));

                        var stats =
                            await _notionCalendarService.GetChecklistStatsAsync(
                                token,
                                activity.PageId,
                                itemCts.Token,
                                forceRefresh: true);

                        foreach (var instance in _calendarActivities.Where(item =>
                                     string.Equals(
                                         item.PageId,
                                         activity.PageId,
                                         StringComparison.OrdinalIgnoreCase)))
                        {
                            instance.ChecklistScanned = true;
                            instance.ChecklistTotal = stats.Total;
                            instance.ChecklistCompleted = stats.Completed;
                        }

                        _oneClickChecklistStats[activity.PageId] = stats;

                        if (previous.Total != stats.Total ||
                            previous.Completed != stats.Completed)
                        {
                            changedCount++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[CHECKLIST_INCREMENTAL] {activity.PageId}: {ex.Message}");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!_calendarViewActive ||
                    requestedDate != _calendarSelectedDate.Date ||
                    loadVersion != _calendarLoadVersion)
                {
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_calendarViewActive ||
                        requestedDate != _calendarSelectedDate.Date ||
                        loadVersion != _calendarLoadVersion)
                    {
                        return;
                    }

                    if (changedCount > 0)
                    {
                        UpdateCalendarChecklistVisuals(
                            activities.Select(
                                activity => activity.PageId));
                    }

                    StatusText.Text =
                        activities.Count == 1
                            ? changedCount > 0
                                ? "Estado: Checklist actualizado ✅ · 1 actividad"
                                : "Estado: Checklist comprobado ✅ · sin cambio"
                            : changedCount > 0
                                ? $"Estado: Checklist actualizado ✅ · {changedCount} de {activities.Count} actividad(es)"
                                : $"Estado: Checklist comprobado ✅ · {activities.Count} actividad(es) sin cambio";
                });
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void StartCalendarChecklistHydration(
            DateTime requestedDate,
            long loadVersion)
        {
            try
            {
                _calendarChecklistHydrationCts?.Cancel();
                _calendarChecklistHydrationCts?.Dispose();
            }
            catch
            {
            }

            var cts = new CancellationTokenSource();
            _calendarChecklistHydrationCts = cts;

            var unique =
                _calendarActivities
                    .Where(activity =>
                        activity != null &&
                        !activity.IsReviewMirror &&
                        !string.IsNullOrWhiteSpace(activity.PageId))
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            // Lo ya persistido en caché no vuelve a recorrer el pipeline.
            // Solo alimenta One Click desde memoria. La lectura completa se
            // reserva para páginas que todavía nunca han sido escaneadas.
            foreach (var activity in unique.Where(item => item.ChecklistScanned))
            {
                _oneClickChecklistStats[activity.PageId] =
                    new NotionChecklistStats(
                        activity.ChecklistTotal,
                        activity.ChecklistCompleted);
            }

            var snapshot =
                unique
                    .Where(activity => !activity.ChecklistScanned)
                    .ToList();

            if (snapshot.Count == 0)
                return;

            _ = HydrateCalendarChecklistForCardsAsync(
                snapshot,
                requestedDate.Date,
                loadVersion,
                cts.Token);
        }

        private async Task HydrateCalendarChecklistForCardsAsync(
            IReadOnlyList<NotionCalendarActivity> activities,
            DateTime requestedDate,
            long loadVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                await HydrateCalendarChecklistStatsAsync(
                    activities,
                    "Analizando checklist",
                    forceRefresh: false,
                    cancellationToken: cancellationToken,
                    itemCompleted: (activity, current, total) =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!_calendarViewActive ||
                                requestedDate !=
                                    _calendarSelectedDate.Date ||
                                loadVersion !=
                                    _calendarLoadVersion)
                            {
                                return;
                            }

                            UpdateCalendarChecklistVisuals(
                                new[] { activity.PageId });

                            StatusText.Text =
                                $"Estado: Calculando checklist " +
                                $"{current} de {total}…";
                        });
                    });

                cancellationToken.ThrowIfCancellationRequested();

                if (!_calendarViewActive ||
                    requestedDate != _calendarSelectedDate.Date ||
                    loadVersion != _calendarLoadVersion)
                {
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_calendarViewActive &&
                        requestedDate == _calendarSelectedDate.Date &&
                        loadVersion == _calendarLoadVersion)
                    {
                        UpdateCalendarChecklistVisuals(
                            activities.Select(
                                activity => activity.PageId));

                        StatusText.Text =
                            $"Estado: Porcentajes de checklist actualizados ✅ ({activities.Count})";
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static string BuildCalendarActivitySpeechText(
            NotionCalendarActivity activity,
            IReadOnlyList<NotionPreviewBlock>? blocks = null)
        {
            if (activity == null)
                return string.Empty;

            var parts = new List<string>();

            var duration =
                activity.End > activity.Start
                    ? activity.End - activity.Start
                    : TimeSpan.FromHours(1);

            var durationText =
                duration.TotalMinutes < 60
                    ? $"Duración aproximada: {Math.Max(1, (int)Math.Round(duration.TotalMinutes))} minutos"
                    : duration.TotalMinutes % 60 < 1
                        ? $"Duración aproximada: {Math.Max(1, (int)Math.Round(duration.TotalHours))} horas"
                        : $"Duración aproximada: {(int)duration.TotalHours} horas y {duration.Minutes} minutos";

            parts.Add(
                $"Horario: {activity.Start:HH:mm} a {activity.End:HH:mm}. {durationText}");

            var title = CleanSpeechText(activity.Title);
            if (!string.IsNullOrWhiteSpace(title))
                parts.Add($"Actividad: {title}");

            var status = CleanSpeechText(activity.Status);
            if (!string.IsNullOrWhiteSpace(status))
                parts.Add($"Estado: {status}");

            var update = CleanSpeechText(activity.UpdateText);
            if (!string.IsNullOrWhiteSpace(update))
                parts.Add($"Última actualización: {update}");

            var description = CleanSpeechText(activity.Description);
            if (!string.IsNullOrWhiteSpace(description))
                parts.Add($"Resumen: {description}");

            foreach (var block in
                     (blocks ?? Array.Empty<NotionPreviewBlock>())
                     .Where(block =>
                         !block.IsStrikethrough &&
                         !(block.Kind == NotionPreviewBlockKind.ToDo && block.IsChecked) &&
                         block.Kind != NotionPreviewBlockKind.Divider &&
                         block.Kind != NotionPreviewBlockKind.Image &&
                         block.Kind != NotionPreviewBlockKind.Pdf &&
                         block.Kind != NotionPreviewBlockKind.File &&
                         block.Kind != NotionPreviewBlockKind.Audio &&
                         block.Kind != NotionPreviewBlockKind.Video &&
                         block.Kind != NotionPreviewBlockKind.Embed)
                     .Take(12))
            {
                var text = CleanSpeechText(block.Text);
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }

            return string.Join(
                ". ",
                parts
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private async Task StartCalendarActivitySpeechAsync(
            NotionCalendarActivity activity,
            IReadOnlyList<NotionPreviewBlock>? blocks,
            Button readButton,
            Button stopButton)
        {
            var speechText =
                BuildCalendarActivitySpeechText(
                    activity,
                    blocks);

            if (string.IsNullOrWhiteSpace(speechText))
            {
                StatusText.Text =
                    "Estado: Esta actividad no tiene contenido para leer.";
                return;
            }

            try
            {
                StopNotionPreviewSpeech();

                readButton.Content = "🔊 Leyendo...";
                readButton.IsEnabled = false;
                stopButton.IsEnabled = true;

                var stream =
                    await _previewSpeechSynth
                        .SynthesizeTextToStreamAsync(
                            speechText);

                var player = new MediaPlayer
                {
                    Source =
                        MediaSource.CreateFromStream(
                            stream,
                            stream.ContentType)
                };

                _previewSpeechPlayer = player;
                _previewSpeechPlaying = true;

                void ResetButtons()
                {
                    if (ReferenceEquals(
                            _previewSpeechPlayer,
                            player))
                    {
                        StopNotionPreviewSpeech();
                    }

                    readButton.Content = "▶ Leer resumen";
                    readButton.IsEnabled = true;
                    stopButton.IsEnabled = false;
                }

                player.MediaEnded +=
                    (_, __) =>
                        DispatcherQueue.TryEnqueue(
                            ResetButtons);

                player.MediaFailed +=
                    (_, __) =>
                        DispatcherQueue.TryEnqueue(
                            ResetButtons);

                StatusText.Text =
                    "Estado: Leyendo resumen de la actividad...";

                player.Play();
            }
            catch (Exception ex)
            {
                StopNotionPreviewSpeech();
                readButton.Content = "▶ Leer resumen";
                readButton.IsEnabled = true;
                stopButton.IsEnabled = false;
                StatusText.Text =
                    $"Estado: No se pudo leer la actividad → {ex.Message}";
            }
        }

        private FrameworkElement BuildCalendarPersonPreviewCard(
            NotionCalendarActivity activity)
        {
            var root = new StackPanel
            {
                Spacing = 7
            };

            var heading = new Grid
            {
                ColumnSpacing = 8
            };

            heading.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            heading.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            var time = new TextBlock
            {
                Text = activity.TimeLabel,
                MinWidth = 76,
                FontSize = 11,
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.82
            };

            var title = new TextBlock
            {
                Text = activity.Title,
                FontSize = 12.5,
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None
            };

            Grid.SetColumn(time, 0);
            heading.Children.Add(time);

            Grid.SetColumn(title, 1);
            heading.Children.Add(title);

            root.Children.Add(heading);

            void AddSummary(
                string label,
                string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                root.Children.Add(
                    new TextBlock
                    {
                        Text = $"{label}: {value}",
                        FontSize = 10.5,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.76
                    });
            }

            AddSummary("Estado", activity.Status);
            AddSummary(
                "Última actualización",
                activity.UpdateText);

            var previewChecklist =
                GetCalendarChecklistStats(activity);

            AddSummary(
                "Checklist",
                FormatCalendarChecklistLabel(
                    activity,
                    previewChecklist));

            if (activity.HasActivityDayRange)
            {
                AddSummary(
                    "Tiempo de actividad",
                    activity.IsActivityOverdue
                        ? $"Día {activity.ActivityElapsedDays} de {activity.ActivityBudgetDays} · vencida +{activity.ActivityElapsedDays - activity.ActivityBudgetDays} día(s)"
                        : $"Día {activity.ActivityElapsedDays} de {activity.ActivityBudgetDays}");
            }

            if (activity.IsAutomationLocked)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "🔒 Bloqueada para automatizaciones",
                        FontSize = 10.5,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(255, 216, 180, 254)),
                        TextWrapping = TextWrapping.Wrap
                    });
            }

            if (activity.IsReviewMirror)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Copia visual de seguimiento",
                        FontSize = 10,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Opacity = 0.70
                    });
            }

            var contentHost = new ContentControl
            {
                HorizontalContentAlignment =
                    HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };

            var actions = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 3,
                ItemWidth = 116,
                ItemHeight = 36,
                Width = 370
            };

            Button BuildCardActionButton(
                string text)
            {
                return new Button
                {
                    Content = text,
                    Width = 110,
                    Height = 32,
                    Margin = new Thickness(0, 0, 6, 4),
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(6),
                    Tag = activity
                };
            }

            var previewButton =
                BuildCardActionButton(
                    "Ver contenido");

            previewButton.Click +=
                async (_, __) =>
                {
                    await ToggleCalendarPersonActivityContentAsync(
                        activity,
                        previewButton,
                        contentHost);
                };

            var messageButton =
                BuildCardActionButton(
                    "Mensaje");

            messageButton.Click +=
                async (_, __) =>
                {
                    await ShowCalendarMessageComposerAsync(
                        activity,
                        _calendarPersonPreviewPerson);
                };

            ToolTipService.SetToolTip(
                messageButton,
                "Enviar mensaje con los datos de esta actividad");

            var openButton =
                BuildCardActionButton(
                    "Abrir");

            openButton.Click +=
                async (_, __) =>
                {
                    await OpenCalendarActivityAsync(activity);
                };

            var lockButton =
                BuildCardActionButton(
                    activity.IsAutomationLocked
                        ? "🔓 Desbloquear"
                        : "🔒 Bloquear");

            lockButton.IsEnabled =
                !activity.IsReviewMirror;

            lockButton.Click +=
                async (_, __) =>
                {
                    if (await ToggleCalendarActivityAutomationLockAsync(
                            activity))
                    {
                        lockButton.Content =
                            activity.IsAutomationLocked
                                ? "🔓 Desbloquear"
                                : "🔒 Bloquear";

                        RenderCalendarPersonPreviewItems(
                            _calendarPersonPreviewPerson);
                    }
                };

            var readButton =
                BuildCardActionButton(
                    "▶ Leer resumen");

            var stopSpeechButton =
                BuildCardActionButton(
                    "■ Detener");

            stopSpeechButton.IsEnabled = false;

            readButton.Click +=
                async (_, __) =>
                {
                    await StartCalendarActivitySpeechAsync(
                        activity,
                        blocks: null,
                        readButton,
                        stopSpeechButton);
                };

            stopSpeechButton.Click +=
                (_, __) =>
                {
                    StopNotionPreviewSpeech();
                    readButton.Content = "▶ Leer resumen";
                    readButton.IsEnabled = true;
                    stopSpeechButton.IsEnabled = false;
                };

            actions.Children.Add(previewButton);
            actions.Children.Add(messageButton);
            actions.Children.Add(openButton);
            actions.Children.Add(lockButton);
            actions.Children.Add(readButton);
            actions.Children.Add(stopSpeechButton);

            root.Children.Add(actions);
            root.Children.Add(contentHost);

            return new Border
            {
                Padding = new Thickness(11),
                CornerRadius = new CornerRadius(9),
                Background =
                    new SolidColorBrush(
                        Color.FromArgb(
                            46,
                            255,
                            255,
                            255)),
                BorderBrush =
                    GetActivityBrush(
                        activity.Status,
                        activity.StatusColor),
                BorderThickness = new Thickness(1),
                Child = root
            };
        }

        private async Task ToggleCalendarPersonActivityContentAsync(
            NotionCalendarActivity activity,
            Button previewButton,
            ContentControl contentHost)
        {
            if (contentHost.Visibility == Visibility.Visible)
            {
                contentHost.Visibility = Visibility.Collapsed;
                previewButton.Content = "Ver contenido";
                return;
            }

            contentHost.Visibility = Visibility.Visible;
            previewButton.Content = "Ocultar contenido";

            var loading = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 2)
            };

            loading.Children.Add(
                new ProgressRing
                {
                    Width = 18,
                    Height = 18,
                    IsActive = true
                });

            loading.Children.Add(
                new TextBlock
                {
                    Text =
                        "Cargando únicamente esta actividad...",
                    VerticalAlignment =
                        VerticalAlignment.Center,
                    FontSize = 10.5,
                    Opacity = 0.72
                });

            contentHost.Content = loading;

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                contentHost.Content =
                    BuildCalendarPersonPreviewMessage(
                        "Configura primero el token de Notion.",
                        isError: true);
                return;
            }

            var panelToken =
                _calendarPersonPreviewCts?.Token ??
                CancellationToken.None;

            using var cts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    panelToken);

            cts.CancelAfter(
                TimeSpan.FromMinutes(3));

            previewButton.IsEnabled = false;

            try
            {
                var blocks =
                    await _notionPreviewService
                        .GetPagePreviewAsync(
                            token,
                            activity.PageId,
                            cts.Token);

                cts.Token.ThrowIfCancellationRequested();

                contentHost.Content =
                    BuildCalendarPersonActivityContent(
                        activity,
                        blocks);
            }
            catch (OperationCanceledException)
            {
                contentHost.Content =
                    BuildCalendarPersonPreviewMessage(
                        "La carga se canceló.",
                        isError: false);
            }
            catch (Exception ex)
            {
                contentHost.Content =
                    BuildCalendarPersonPreviewMessage(
                        $"No se pudo cargar el contenido.\n{ex.Message}",
                        isError: true);
            }
            finally
            {
                previewButton.IsEnabled = true;
            }
        }

        private FrameworkElement BuildCalendarPersonActivityContent(
            NotionCalendarActivity activity,
            IReadOnlyList<NotionPreviewBlock> blocks)
        {
            var content = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(0, 5, 0, 0)
            };

            if (!string.IsNullOrWhiteSpace(
                    activity.Description))
            {
                content.Children.Add(
                    CreateSectionLabel(
                        "DESCRIPCIÓN"));

                content.Children.Add(
                    CreatePreviewText(
                        activity.Description,
                        10.5,
                        Microsoft.UI.Text.FontWeights.Normal,
                        0.86,
                        0));
            }

            var visibleBlocks = blocks
                .Where(block =>
                    block.Kind ==
                        NotionPreviewBlockKind.Divider ||
                    !string.IsNullOrWhiteSpace(
                        block.Text) ||
                    !string.IsNullOrWhiteSpace(
                        block.Url))
                .Take(120)
                .ToList();

            if (visibleBlocks.Count > 0)
            {
                content.Children.Add(
                    CreateSectionLabel(
                        "CONTENIDO DE LA ACTIVIDAD"));
            }

            var number = 0;

            foreach (var block in visibleBlocks)
            {
                if (block.Kind ==
                    NotionPreviewBlockKind.NumberedListItem)
                {
                    number++;
                }
                else
                {
                    number = 0;
                }

                var element =
                    CreateBlockElement(
                        block,
                        number);

                if (element == null)
                    continue;

                ConstrainCalendarPreviewElement(
                    element,
                    350);

                content.Children.Add(element);
            }

            if (visibleBlocks.Count == 0 &&
                string.IsNullOrWhiteSpace(
                    activity.Description))
            {
                content.Children.Add(
                    CreatePreviewText(
                        "La actividad no contiene bloques visibles.",
                        10.5,
                        Microsoft.UI.Text.FontWeights.Normal,
                        0.64,
                        0));
            }

            return new Border
            {
                Padding = new Thickness(9),
                CornerRadius = new CornerRadius(7),
                Background =
                    new SolidColorBrush(
                        Color.FromArgb(
                            30,
                            255,
                            255,
                            255)),
                Child = content
            };
        }

        private static FrameworkElement
            BuildCalendarPersonPreviewMessage(
                string message,
                bool isError)
        {
            return new Border
            {
                Margin = new Thickness(0, 5, 0, 0),
                Padding = new Thickness(9),
                CornerRadius = new CornerRadius(7),
                Background =
                    new SolidColorBrush(
                        isError
                            ? Color.FromArgb(
                                34,
                                248,
                                113,
                                113)
                            : Color.FromArgb(
                                30,
                                255,
                                255,
                                255)),
                Child = new TextBlock
                {
                    Text = message,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.78
                }
            };
        }

        private void RefreshCalendarPersonPreviewIfOpen()
        {
            if (CalendarPersonPreviewPanel?.Visibility !=
                    Visibility.Visible ||
                string.IsNullOrWhiteSpace(
                    _calendarPersonPreviewPerson))
            {
                return;
            }

            // Solo se reconstruye el listado si el panel no está siendo creado
            // por esta misma llamada de DrawCalendar. La actualización diferida
            // evita modificar el árbol visual mientras se recorre el calendario.
            var person =
                _calendarPersonPreviewPerson;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_calendarViewActive &&
                    CalendarPersonPreviewPanel?.Visibility ==
                        Visibility.Visible &&
                    string.Equals(
                        person,
                        _calendarPersonPreviewPerson,
                        StringComparison.OrdinalIgnoreCase))
                {
                    RenderCalendarPersonPreviewItems(person);
                }
            });
        }

        private void RenderCalendarPersonPreviewItems(
            string person)
        {
            if (CalendarPersonPreviewItems == null ||
                CalendarPersonPreviewPanel?.Visibility !=
                    Visibility.Visible)
            {
                return;
            }

            CalendarPersonPreviewTitle.Text =
                $"Actividades de {person}";

            CalendarPersonPreviewDate.Text =
                FormatCalendarDate(_calendarSelectedDate);

            var activities =
                GetCalendarPersonPreviewActivities(person);

            CalendarPersonPreviewSummary.Text =
                activities.Count == 0
                    ? "No hay actividades visibles para esta persona en el día y filtros actuales."
                    : activities.Count == 1
                        ? "1 actividad · el contenido de Notion se carga solo al solicitarlo."
                        : $"{activities.Count} actividades · el contenido de Notion se carga solo al solicitarlo.";

            CalendarPersonPreviewItems.Children.Clear();

            if (activities.Count == 0)
            {
                CalendarPersonPreviewItems.Children.Add(
                    BuildCalendarPersonPreviewMessage(
                        "No se encontraron actividades para mostrar.",
                        isError: false));
                return;
            }

            foreach (var activity in activities)
            {
                CalendarPersonPreviewItems.Children.Add(
                    BuildCalendarPersonPreviewCard(
                        activity));
            }
        }

        private void CloseCalendarPersonPreviewPanel(
            bool redrawCalendar = true)
        {
            StopNotionPreviewSpeech();
            try
            {
                _calendarPersonPreviewCts?.Cancel();
                _calendarPersonPreviewCts?.Dispose();
            }
            catch
            {
            }

            _calendarPersonPreviewCts = null;
            _calendarPersonPreviewPerson = string.Empty;

            if (CalendarPersonPreviewItems != null)
                CalendarPersonPreviewItems.Children.Clear();

            if (CalendarPersonPreviewPanel != null)
            {
                CalendarPersonPreviewPanel.Visibility =
                    Visibility.Collapsed;
            }

            if (redrawCalendar)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_calendarViewActive)
                        DrawCalendar(_calendarActivities);
                });
            }
        }


        private enum OneClickPriorityLevel
        {
            Urgent = 0,
            Today = 1,
            ThisWeek = 2,
            Normal = 3,
            Waiting = 4,
            Closed = 5,
            Undefined = 6
        }

        private sealed class OneClickPriorityInfo
        {
            public OneClickPriorityLevel Level { get; init; }
            public string Label { get; init; } = string.Empty;
            public string Reason { get; init; } = string.Empty;
            public Color Color { get; init; }
            public bool IsMissing { get; init; }
            public bool IsQuickWinReview { get; init; }
            public NotionChecklistStats Checklist { get; init; } =
                new(0, 0);
        }

        private sealed class OneClickSchedulePreviewItem
        {
            public NotionCalendarActivity Activity { get; init; } = new();
            public DateTime Start { get; init; }
            public DateTime End { get; init; }
            public int DurationMinutes { get; init; }
            public bool IsOverflow { get; init; }
            public bool IsOvertime { get; init; }
            public int OvertimeMinutes { get; init; }
            public string Note { get; init; } = string.Empty;

            public bool HasScheduleChange =>
                !IsOverflow &&
                (Math.Abs(
                     (Activity.Start - Start).TotalMinutes) >= 0.5 ||
                 Math.Abs(
                     (Activity.End - End).TotalMinutes) >= 0.5);
        }

        private sealed class OneClickSchedulePreviewResult
        {
            public string Person { get; init; } = string.Empty;
            public DateTime Day { get; init; }
            public DateTime EffectiveStart { get; init; }
            public DateTime EffectiveEnd { get; init; }
            public DateTime StandardEnd { get; init; }
            public DateTime AbsoluteEnd { get; init; }
            public bool IsToday { get; init; }
            public bool IsLateStart { get; init; }
            public IReadOnlyList<OneClickSchedulePreviewItem> Scheduled { get; init; } =
                Array.Empty<OneClickSchedulePreviewItem>();
            public IReadOnlyList<OneClickSchedulePreviewItem> Overflow { get; init; } =
                Array.Empty<OneClickSchedulePreviewItem>();
            public IReadOnlyList<NotionCalendarActivity> DeferredToTomorrow { get; init; } =
                Array.Empty<NotionCalendarActivity>();
            public IReadOnlyList<NotionCalendarActivity> InProgressUnchanged { get; init; } =
                Array.Empty<NotionCalendarActivity>();
            public int AvailableMinutes { get; init; }
            public int ScheduledMinutes { get; init; }
            public int ChangedCount =>
                Scheduled.Count(item => item.HasScheduleChange);
            public int DeferredCount =>
                DeferredToTomorrow.Count;
            public int TotalActionCount =>
                ChangedCount + DeferredCount;
            public int OvertimeCount =>
                Scheduled.Count(item => item.IsOvertime);
            public int OvertimeMinutes =>
                Scheduled.Count == 0
                    ? 0
                    : Math.Max(
                        0,
                        (int)Math.Ceiling(
                            (Scheduled.Max(item => item.End) -
                             StandardEnd).TotalMinutes));
            public double CoveragePercentage =>
                AvailableMinutes <= 0
                    ? 0
                    : ScheduledMinutes * 100d / AvailableMinutes;
        }

        private sealed class OneClickSchedulePreviewUiState
        {
            public string Person { get; init; } = string.Empty;
            public List<NotionCalendarActivity> OrderedActivities { get; } = new();
            public OneClickSchedulePreviewResult Result { get; set; } = new();
            public ContentControl? Host { get; set; }
            public ContentDialog? Dialog { get; set; }
            public string DraggedPageId { get; set; } = string.Empty;
        }

        private static bool IsOneClickPm(string person)
        {
            person = NormalizeCalendarPerson(person);

            return string.Equals(
                       person,
                       "John",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       person,
                       "Genaro",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       person,
                       "Isaias",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsOneClickToken(
            string text,
            string token)
        {
            return Regex.IsMatch(
                text ?? string.Empty,
                $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(token)}(?![\p{{L}}\p{{Nd}}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private OneClickPriorityInfo GetOneClickPriorityInfo(
            NotionCalendarActivity activity,
            string person)
        {
            var title = activity?.Title ?? string.Empty;
            var searchable = NormalizeCalendarSearchText(
                string.Join(
                    " ",
                    title,
                    activity?.Status ?? string.Empty,
                    activity?.Description ?? string.Empty));

            var checklist =
                GetCalendarChecklistStats(activity);

            OneClickPriorityInfo Build(
                OneClickPriorityLevel level,
                string label,
                string reason,
                Color color,
                bool missing = false,
                bool quickWin = false)
            {
                return new OneClickPriorityInfo
                {
                    Level = level,
                    Label = label,
                    Reason = reason,
                    Color = color,
                    IsMissing = missing,
                    IsQuickWinReview = quickWin,
                    Checklist = checklist
                };
            }

            if (searchable.Contains("espera", StringComparison.OrdinalIgnoreCase) ||
                searchable.Contains("depende", StringComparison.OrdinalIgnoreCase))
            {
                return Build(
                    OneClickPriorityLevel.Waiting,
                    "En espera",
                    "Depende de otra persona o condición",
                    Color.FromArgb(255, 96, 165, 250));
            }

            if (searchable.Contains("urgente", StringComparison.OrdinalIgnoreCase))
            {
                return Build(
                    OneClickPriorityLevel.Urgent,
                    "Urgente",
                    "Marcada como urgente",
                    Color.FromArgb(255, 248, 113, 113));
            }

            if (IsOneClickPm(person) &&
                HasExactCalendarPhase(activity, "rtuzREVISION"))
            {
                var reason = checklist.HasChecklist
                    ? $"RTUZ · {checklist.Pending} pendiente(s) de {checklist.Total}"
                    : "RTUZ · sin checklist visible";

                return Build(
                    OneClickPriorityLevel.Urgent,
                    "RTUZ primero",
                    reason,
                    Color.FromArgb(255, 248, 113, 113),
                    quickWin: true);
            }

            var activeProjectType =
                new[]
                {
                    "wwebs", "sseo", "aads",
                    "aapli", "pprog"
                }
                .FirstOrDefault(token =>
                    ContainsOneClickToken(title, token));

            if (HasExactCalendarPhase(activity, "prtuzREVISION") &&
                !string.IsNullOrWhiteSpace(activeProjectType))
            {
                return Build(
                    OneClickPriorityLevel.Today,
                    "Alta · hoy",
                    $"Proyecto activo {activeProjectType.ToUpperInvariant()}",
                    Color.FromArgb(255, 251, 146, 60));
            }

            if (searchable.Contains("hoy", StringComparison.OrdinalIgnoreCase) ||
                searchable.Contains("muy importante", StringComparison.OrdinalIgnoreCase))
            {
                return Build(
                    OneClickPriorityLevel.Today,
                    "Muy importante",
                    "Debe realizarse hoy",
                    Color.FromArgb(255, 251, 146, 60));
            }

            if (HasExactCalendarPhase(activity, "prtuzREVISION"))
            {
                return Build(
                    OneClickPriorityLevel.ThisWeek,
                    "Importante",
                    "Actividad por hacer",
                    Color.FromArgb(255, 250, 204, 21));
            }

            var generalType =
                new[]
                {
                    "wwebs", "sseo", "aads", "aapli",
                    "pprog", "ddise", "rrede"
                }
                .FirstOrDefault(token =>
                    ContainsOneClickToken(title, token));

            if (!string.IsNullOrWhiteSpace(generalType))
            {
                return Build(
                    OneClickPriorityLevel.ThisWeek,
                    "Importante",
                    $"Actividad {generalType.ToUpperInvariant()}",
                    Color.FromArgb(255, 250, 204, 21));
            }

            var duration =
                activity == null
                    ? 60
                    : RoundOneClickDurationMinutes(activity);

            if (duration <= 30 ||
                Regex.IsMatch(
                    title,
                    @"(?<![\p{L}\p{Nd}_])[a-z]{4,5}00(?![\p{L}\p{Nd}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant))
            {
                return Build(
                    OneClickPriorityLevel.Normal,
                    "Rápida / delegable",
                    "Ajuste corto de 15 a 30 minutos",
                    Color.FromArgb(255, 74, 222, 128));
            }

            return Build(
                OneClickPriorityLevel.Undefined,
                "Sin prioridad",
                "ANFETA no encontró una regla de prioridad",
                Color.FromArgb(255, 148, 163, 184),
                missing: true);
        }

        private IReadOnlyList<NotionCalendarActivity>
            PrepareOneClickPriorityOrder(
                string person,
                IReadOnlyList<NotionCalendarActivity> activities)
        {
            var list =
                (activities ?? Array.Empty<NotionCalendarActivity>())
                    .ToList();

            if (list.Count == 0)
                return list;

            // One Click NO dispara ni espera la lectura de checklist.
            // El calendario ya hidrata esos datos automáticamente en segundo
            // plano. Si una actividad todavía no termina de analizarse, se
            // usa la información disponible y el desempate cae en duración
            // y horario, permitiendo abrir el preview inmediatamente.
            return list
                .Select(activity =>
                {
                    var priority =
                        GetOneClickPriorityInfo(
                            activity,
                            person);

                    var pendingChecklist =
                        priority.IsQuickWinReview
                            ? priority.Checklist.HasChecklist
                                ? priority.Checklist.Pending
                                : int.MaxValue
                            : 0;

                    return new
                    {
                        Activity = activity,
                        Priority = priority,
                        PendingChecklist = pendingChecklist,
                        Duration =
                            RoundOneClickDurationMinutes(activity)
                    };
                })
                .OrderBy(item => (int)item.Priority.Level)
                .ThenBy(item => item.PendingChecklist)
                .ThenBy(item => item.Duration)
                .ThenBy(item => item.Activity.Start)
                .ThenBy(item => item.Activity.Title)
                .Select(item => item.Activity)
                .ToList();
        }

        private IReadOnlyList<string> BuildOneClickScheduleWarnings(
            OneClickSchedulePreviewUiState state)
        {
            var warnings = new List<string>();

            var all =
                state.Result.Scheduled
                    .Concat(state.Result.Overflow)
                    .ToList();

            if (state.Result.DeferredToTomorrow.Count > 0)
            {
                warnings.Add(
                    $"{state.Result.DeferredToTomorrow.Count} actividad(es) cuyo horario ya terminó se pasarán a mañana al confirmar.");
            }

            if (state.Result.InProgressUnchanged.Count > 0)
            {
                warnings.Add(
                    $"{state.Result.InProgressUnchanged.Count} actividad(es) en curso se conservarán sin cambios.");
            }

            var missingPriority = all.Count(item =>
                GetOneClickPriorityInfo(
                    item.Activity,
                    state.Person)
                    .IsMissing);

            if (missingPriority > 0)
            {
                warnings.Add(
                    $"{missingPriority} actividad(es) sin una prioridad reconocida.");
            }

            if (IsOneClickPm(state.Person))
            {
                var rtuzChecklistPending = all.Count(item =>
                {
                    var priority =
                        GetOneClickPriorityInfo(
                            item.Activity,
                            state.Person);

                    return priority.IsQuickWinReview &&
                           !item.Activity.ChecklistScanned;
                });

                var rtuzWithoutChecklist = all.Count(item =>
                {
                    var priority =
                        GetOneClickPriorityInfo(
                            item.Activity,
                            state.Person);

                    return priority.IsQuickWinReview &&
                           item.Activity.ChecklistScanned &&
                           !priority.Checklist.HasChecklist;
                });

                if (rtuzChecklistPending > 0)
                {
                    warnings.Add(
                        $"{rtuzChecklistPending} revisión(es) RTUZ todavía están calculando checklist en segundo plano; el preview abrió sin esperar y usa duración/horario como desempate temporal.");
                }

                if (rtuzWithoutChecklist > 0)
                {
                    warnings.Add(
                        $"{rtuzWithoutChecklist} revisión(es) RTUZ no tienen checklist nativo visible; se ordenaron por duración y horario.");
                }
            }

            if (state.Result.OvertimeCount > 0)
            {
                warnings.Add(
                    $"{state.Result.OvertimeCount} actividad(es) se acomodaron con tiempo extra. " +
                    $"La jornada se extiende {FormatOneClickDuration(state.Result.OvertimeMinutes)} " +
                    $"después de las 6:00 PM, sin pasar de las {state.Result.AbsoluteEnd:h:mm tt}.");
            }

            if (state.Result.Overflow.Count > 0)
            {
                warnings.Add(
                    $"{state.Result.Overflow.Count} actividad(es) no caben antes del límite absoluto de las " +
                    $"{state.Result.AbsoluteEnd:h:mm tt}.");
            }

            return warnings;
        }

        private static bool IsOneClickScheduleExcluded(
            NotionCalendarActivity activity)
        {
            if (activity == null ||
                activity.IsReviewMirror ||
                activity.IsAutomationLocked ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                return true;
            }

            if (HasExactCalendarPhase(activity, "zREVISION") ||
                HasExactCalendarPhase(activity, "sprtuzREVISION"))
            {
                return true;
            }

            // Las páginas FTF son resúmenes/presentaciones del día, no
            // actividades ejecutables. Se excluyen aunque tengan Fecha POR
            // Hacer y una fase pendiente, para que no consuman cobertura ni
            // aparezcan dentro del acomodo automático.
            if (Regex.IsMatch(
                    activity.Title ?? string.Empty,
                    @"(?<![\p{L}\p{Nd}_])F{1,2}TF(?![\p{L}\p{Nd}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant))
            {
                return true;
            }

            var status =
                NormalizeCalendarSearchText(activity.Status);

            return status.Contains("terminad", StringComparison.OrdinalIgnoreCase) ||
                   status.Contains("completad", StringComparison.OrdinalIgnoreCase) ||
                   status.Contains("cancelad", StringComparison.OrdinalIgnoreCase) ||
                   status.Contains("archivad", StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<NotionCalendarActivity>
            GetOneClickScheduleActivities(string person)
        {
            person = NormalizeCalendarPerson(person);

            return ExpandCalendarReviewActivities(_calendarActivities)
                .Where(activity =>
                    activity != null &&
                    !IsOneClickScheduleExcluded(activity) &&
                    SplitPersons(activity.Person)
                        .Select(NormalizeCalendarPerson)
                        .Any(candidate =>
                            string.Equals(
                                candidate,
                                person,
                                StringComparison.OrdinalIgnoreCase)))
                .GroupBy(
                    activity => activity.PageId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(activity => activity.Start)
                .ThenBy(activity => activity.End)
                .ThenBy(activity => activity.Title)
                .ToList();
        }

        private static int RoundOneClickDurationMinutes(
            NotionCalendarActivity activity)
        {
            // La duración sale del rango que la persona ya configuró en
            // “Fecha POR Hacer”. One Click Schedule puede cambiar la hora de
            // inicio, pero no debe recalcular el tiempo por WEB, SEO, ADS u
            // otro tipo de proyecto. Solo se aplica el mínimo operativo de
            // 15 minutos cuando el rango es menor o inválido.
            var rawMinutes =
                activity.End > activity.Start
                    ? (activity.End - activity.Start).TotalMinutes
                    : 60;

            if (!double.IsFinite(rawMinutes) || rawMinutes <= 0)
                rawMinutes = 60;

            var assignedMinutes =
                (int)Math.Ceiling(rawMinutes);

            return Math.Max(15, assignedMinutes);
        }

        private static DateTime RoundOneClickStartUpToQuarterHour(
            DateTime value)
        {
            var clean = new DateTime(
                value.Year,
                value.Month,
                value.Day,
                value.Hour,
                value.Minute,
                0,
                value.Kind);

            var remainder = clean.Minute % 15;
            var hasPartialMinute =
                value.Second > 0 || value.Millisecond > 0;

            if (remainder == 0 && !hasPartialMinute)
                return clean;

            return clean.AddMinutes(
                remainder == 0
                    ? 15
                    : 15 - remainder);
        }

        private static int CalculateOneClickAvailableMinutes(
            DateTime start,
            DateTime end,
            DateTime lunchStart,
            DateTime lunchEnd)
        {
            if (end <= start)
                return 0;

            var total = (end - start).TotalMinutes;

            var overlapStart = start > lunchStart
                ? start
                : lunchStart;

            var overlapEnd = end < lunchEnd
                ? end
                : lunchEnd;

            if (overlapEnd > overlapStart)
                total -= (overlapEnd - overlapStart).TotalMinutes;

            return Math.Max(0, (int)Math.Round(total));
        }

        private static bool TryFindOneClickScheduleSlot(
            DateTime cursor,
            TimeSpan duration,
            DateTime workEnd,
            DateTime lunchStart,
            DateTime lunchEnd,
            out DateTime start,
            out DateTime end)
        {
            start = cursor;
            end = cursor;

            // El cursor siempre avanza. El límite evita cualquier ciclo
            // accidental si las reglas de horario cambian más adelante.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (start >= lunchStart && start < lunchEnd)
                    start = lunchEnd;

                if (start < lunchStart &&
                    start.Add(duration) > lunchStart)
                {
                    start = lunchEnd;
                }

                end = start.Add(duration);

                if (end <= workEnd)
                    return true;

                return false;
            }

            return false;
        }

        private OneClickSchedulePreviewResult
            BuildOneClickSchedulePreview(
                string person,
                IReadOnlyList<NotionCalendarActivity>? orderedActivities = null)
        {
            var day = _calendarSelectedDate.Date;
            var standardStart = day.AddHours(10);
            var lunchStart = day.AddHours(13);
            var lunchEnd = day.AddHours(14);
            var standardEnd = day.AddHours(18);
            var absoluteEnd = day.AddHours(22);

            var today = DateTime.Today;
            var isToday = day == today;
            var now = DateTime.Now;

            // Antes de las 10:00 AM, y en días futuros, el acomodo inicia
            // con la jornada estándar. Si hoy ya comenzó la jornada, se usa
            // la hora actual redondeada hacia arriba al siguiente bloque de
            // 15 minutos; nunca se propone trabajo en una hora ya pasada.
            var effectiveStart = standardStart;

            if (isToday && now >= standardStart)
            {
                effectiveStart =
                    RoundOneClickStartUpToQuarterHour(now);

                if (effectiveStart >= lunchStart &&
                    effectiveStart < lunchEnd)
                {
                    effectiveStart = lunchEnd;
                }
            }

            // La jornada base termina a las 6:00 PM. Si el día comenzó tarde,
            // se conserva la salida recorrida que ya utilizaba ANFETA, pero
            // ningún acomodo puede rebasar las 10:00 PM. Las actividades que
            // usen cualquier minuto después de las 6:00 PM quedan marcadas
            // explícitamente como tiempo extra.
            var startDelay = effectiveStart > standardStart
                ? effectiveStart - standardStart
                : TimeSpan.Zero;

            var effectiveEnd = standardEnd.Add(startDelay);

            if (effectiveEnd > absoluteEnd)
                effectiveEnd = absoluteEnd;

            if (effectiveStart > absoluteEnd)
                effectiveStart = absoluteEnd;

            var availableMinutes =
                CalculateOneClickAvailableMinutes(
                    effectiveStart,
                    effectiveEnd,
                    lunchStart,
                    lunchEnd);

            var scheduled =
                new List<OneClickSchedulePreviewItem>();

            var overflow =
                new List<OneClickSchedulePreviewItem>();

            var cursor = effectiveStart;

            var activitiesToSchedule =
                orderedActivities ??
                GetOneClickScheduleActivities(person);

            var deferredToTomorrow =
                new List<NotionCalendarActivity>();

            var inProgressUnchanged =
                new List<NotionCalendarActivity>();

            var futureActivities =
                new List<NotionCalendarActivity>();

            foreach (var activity in activitiesToSchedule)
            {
                if (isToday &&
                    activity.End <= now)
                {
                    // Si el horario completo ya terminó, One Click no vuelve
                    // a acomodarlo hoy. Al confirmar se pasa al día siguiente
                    // conservando su hora y duración.
                    deferredToTomorrow.Add(activity);
                    continue;
                }

                if (isToday &&
                    activity.Start < now &&
                    activity.End > now)
                {
                    // Una actividad que está corriendo en este momento no se
                    // toca ni se reacomoda; se conserva exactamente como está.
                    inProgressUnchanged.Add(activity);
                    continue;
                }

                futureActivities.Add(activity);
            }

            foreach (var activity in futureActivities)
            {
                var durationMinutes =
                    RoundOneClickDurationMinutes(activity);

                var duration =
                    TimeSpan.FromMinutes(durationMinutes);

                // Se permite usar hasta cuatro horas después de la jornada base,
                // siempre con aviso visual y sin rebasar las 10:00 PM.
                if (!TryFindOneClickScheduleSlot(
                        cursor,
                        duration,
                        absoluteEnd,
                        lunchStart,
                        lunchEnd,
                        out var start,
                        out var end))
                {
                    overflow.Add(
                        new OneClickSchedulePreviewItem
                        {
                            Activity = activity,
                            DurationMinutes = durationMinutes,
                            IsOverflow = true,
                            Note = effectiveStart >= absoluteEnd
                                ? "La jornada de hoy ya alcanzó el límite absoluto de las 10:00 PM."
                                : $"No cabe antes de las {absoluteEnd:h:mm tt} sin rebasar el máximo de 4 horas de tiempo extra."
                        });

                    continue;
                }

                var overtimeStart =
                    start > standardEnd
                        ? start
                        : standardEnd;

                var overtimeMinutes =
                    end > standardEnd
                        ? Math.Max(
                            0,
                            (int)Math.Ceiling(
                                (end - overtimeStart).TotalMinutes))
                        : 0;

                scheduled.Add(
                    new OneClickSchedulePreviewItem
                    {
                        Activity = activity,
                        Start = start,
                        End = end,
                        DurationMinutes = durationMinutes,
                        IsOverflow = false,
                        IsOvertime = overtimeMinutes > 0,
                        OvertimeMinutes = overtimeMinutes,
                        Note = overtimeMinutes > 0
                            ? $"ANFETA la acomodó usando {FormatOneClickDuration(overtimeMinutes)} de tiempo extra. Límite absoluto: {absoluteEnd:h:mm tt}."
                            : string.Empty
                    });

                // Se conserva la duración original. La siguiente actividad
                // comienza en el próximo bloque de 15 minutos para mantener
                // el calendario ordenado y evitar empalmes.
                cursor =
                    RoundOneClickStartUpToQuarterHour(end);

                if (cursor >= lunchStart && cursor < lunchEnd)
                    cursor = lunchEnd;
            }

            return new OneClickSchedulePreviewResult
            {
                Person = NormalizeCalendarPerson(person),
                Day = day,
                EffectiveStart = effectiveStart,
                EffectiveEnd = effectiveEnd,
                StandardEnd = standardEnd,
                AbsoluteEnd = absoluteEnd,
                IsToday = isToday,
                IsLateStart = isToday && effectiveStart > day.AddHours(12),
                Scheduled = scheduled,
                Overflow = overflow,
                DeferredToTomorrow = deferredToTomorrow,
                InProgressUnchanged = inProgressUnchanged,
                AvailableMinutes = availableMinutes,
                ScheduledMinutes = scheduled.Sum(item => item.DurationMinutes)
            };
        }

        private double CalculateCurrentCalendarCoverage(string person)
        {
            var day = _calendarSelectedDate.Date;
            var windows = new[]
            {
                (Start: day.AddHours(10), End: day.AddHours(13)),
                (Start: day.AddHours(14), End: day.AddHours(18))
            };

            var intervals =
                new List<(DateTime Start, DateTime End)>();

            foreach (var activity in
                     GetOneClickScheduleActivities(person))
            {
                foreach (var window in windows)
                {
                    var start =
                        activity.Start > window.Start
                            ? activity.Start
                            : window.Start;

                    var end =
                        activity.End < window.End
                            ? activity.End
                            : window.End;

                    if (end > start)
                        intervals.Add((start, end));
                }
            }

            if (intervals.Count == 0)
                return 0;

            var ordered =
                intervals
                    .OrderBy(item => item.Start)
                    .ThenBy(item => item.End)
                    .ToList();

            var coveredMinutes = 0d;
            var currentStart = ordered[0].Start;
            var currentEnd = ordered[0].End;

            foreach (var interval in ordered.Skip(1))
            {
                if (interval.Start <= currentEnd)
                {
                    if (interval.End > currentEnd)
                        currentEnd = interval.End;
                }
                else
                {
                    coveredMinutes +=
                        (currentEnd - currentStart).TotalMinutes;

                    currentStart = interval.Start;
                    currentEnd = interval.End;
                }
            }

            coveredMinutes +=
                (currentEnd - currentStart).TotalMinutes;

            return Math.Clamp(
                coveredMinutes * 100d / (7 * 60d),
                0,
                999);
        }

        private static SolidColorBrush GetOneClickCoverageBrush(
            double percentage)
        {
            var color = percentage switch
            {
                < 50 => Color.FromArgb(255, 248, 113, 113),
                < 80 => Color.FromArgb(255, 250, 204, 21),
                <= 100 => Color.FromArgb(255, 74, 222, 128),
                _ => Color.FromArgb(255, 192, 132, 252)
            };

            return new SolidColorBrush(color);
        }

        private static string FormatOneClickDuration(int minutes)
        {
            if (minutes < 60)
                return $"{minutes} min";

            var hours = minutes / 60;
            var remainder = minutes % 60;

            return remainder == 0
                ? hours == 1 ? "1 hora" : $"{hours} horas"
                : $"{hours} h {remainder} min";
        }

        private FrameworkElement BuildOneClickPreviewCard(
            OneClickSchedulePreviewItem item,
            bool overflow,
            OneClickSchedulePreviewUiState? state = null)
        {
            var priority =
                GetOneClickPriorityInfo(
                    item.Activity,
                    state?.Person ?? item.Activity.Person);

            var contentGrid = new Grid
            {
                ColumnSpacing = 9
            };

            contentGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(78)
                });

            contentGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            var timeText = new TextBlock
            {
                Text = overflow
                    ? "Excedente"
                    : $"{item.Start:h:mm tt}\n{item.End:h:mm tt}",
                FontSize = 10.5,
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = overflow
                    ? new SolidColorBrush(
                        Color.FromArgb(255, 248, 113, 113))
                    : item.IsOvertime
                        ? new SolidColorBrush(
                            Color.FromArgb(255, 192, 132, 252))
                        : new SolidColorBrush(
                            Color.FromArgb(255, 125, 211, 252)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };

            Grid.SetColumn(timeText, 0);
            contentGrid.Children.Add(timeText);

            var details = new StackPanel
            {
                Spacing = 2
            };

            var priorityRow = new Grid();
            priorityRow.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });
            priorityRow.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var priorityBrightness =
                priority.Color.R * 0.299 +
                priority.Color.G * 0.587 +
                priority.Color.B * 0.114;

            var priorityBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(10),
                Background =
                    new SolidColorBrush(
                        Color.FromArgb(
                            225,
                            priority.Color.R,
                            priority.Color.G,
                            priority.Color.B)),
                Child = new TextBlock
                {
                    Text = priority.Label,
                    FontSize = 9.3,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.Bold,
                    Foreground =
                        new SolidColorBrush(
                            priorityBrightness >= 145
                                ? Colors.Black
                                : Colors.White),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1
                }
            };

            ToolTipService.SetToolTip(
                priorityBadge,
                string.IsNullOrWhiteSpace(priority.Reason)
                    ? priority.Label
                    : $"{priority.Label}: {priority.Reason}");

            Grid.SetColumn(priorityBadge, 0);
            priorityRow.Children.Add(priorityBadge);

            if (!overflow && state != null)
            {
                var dragIcon = new TextBlock
                {
                    Text = "☰",
                    Margin = new Thickness(6, 0, 0, 0),
                    FontSize = 12,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground =
                        new SolidColorBrush(
                            Color.FromArgb(210, 125, 211, 252)),
                    VerticalAlignment = VerticalAlignment.Center
                };

                ToolTipService.SetToolTip(
                    dragIcon,
                    "Arrastra esta tarjeta sobre otra para cambiar el orden.");

                Grid.SetColumn(dragIcon, 1);
                priorityRow.Children.Add(dragIcon);
            }

            details.Children.Add(priorityRow);

            details.Children.Add(
                new TextBlock
                {
                    Text = item.Activity.Title,
                    FontSize = 12,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

            var statusText =
                string.IsNullOrWhiteSpace(item.Activity.Status)
                    ? string.Empty
                    : $" · {item.Activity.Status}";

            details.Children.Add(
                new TextBlock
                {
                    Text =
                        $"{FormatOneClickDuration(item.DurationMinutes)}{statusText}",
                    FontSize = 10,
                    Opacity = 0.78,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1
                });

            var checklist =
                GetCalendarChecklistStats(item.Activity);

            details.Children.Add(
                new TextBlock
                {
                    Text = item.Activity.ChecklistScanned
                        ? checklist.HasChecklist
                            ? $"☑ Checklist {checklist.Completed}/{checklist.Total} · {GetChecklistPercentage(checklist)}% · {checklist.Pending} pendiente(s)"
                            : "☑ Sin checklist nativo en Notion"
                        : "☑ Checklist pendiente de cargar",
                    FontSize = 9.7,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground =
                        new SolidColorBrush(
                            checklist.HasChecklist
                                ? Color.FromArgb(255, 125, 211, 252)
                                : Color.FromArgb(255, 148, 163, 184)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2
                });

            if (!overflow && item.IsOvertime)
            {
                details.Children.Add(
                    new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 2, 0, 0),
                        Padding = new Thickness(7, 3, 7, 3),
                        CornerRadius = new CornerRadius(8),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(50, 192, 132, 252)),
                        BorderBrush =
                            new SolidColorBrush(
                                Color.FromArgb(150, 192, 132, 252)),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            Text =
                                $"⏱ Tiempo extra +{FormatOneClickDuration(item.OvertimeMinutes)} · límite 10:00 PM",
                            FontSize = 9.4,
                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromArgb(255, 216, 180, 254)),
                            TextWrapping = TextWrapping.Wrap,
                            MaxLines = 2
                        }
                    });
            }

            if (!overflow && item.HasScheduleChange)
            {
                details.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"Actual {item.Activity.Start:h:mm tt}–{item.Activity.End:h:mm tt} → se actualizará",
                        FontSize = 9.7,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(255, 74, 222, 128)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1
                    });
            }

            if (overflow &&
                !string.IsNullOrWhiteSpace(item.Note))
            {
                details.Children.Add(
                    new TextBlock
                    {
                        Text = item.Note,
                        FontSize = 9.7,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(255, 251, 146, 60)),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
            }

            Grid.SetColumn(details, 1);
            contentGrid.Children.Add(details);

            var cardContent = new Grid
            {
                ColumnSpacing = 8
            };

            cardContent.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(5)
                });

            cardContent.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            var priorityStripe = new Border
            {
                Background =
                    new SolidColorBrush(priority.Color),
                CornerRadius = new CornerRadius(3)
            };

            Grid.SetColumn(priorityStripe, 0);
            cardContent.Children.Add(priorityStripe);

            Grid.SetColumn(contentGrid, 1);
            cardContent.Children.Add(contentGrid);

            var card = new Border
            {
                Padding = new Thickness(7, 7, 10, 7),
                CornerRadius = new CornerRadius(7),
                Background =
                    new SolidColorBrush(
                        overflow
                            ? Color.FromArgb(42, 248, 113, 113)
                            : item.IsOvertime
                                ? Color.FromArgb(42, 192, 132, 252)
                                : Color.FromArgb(34, 255, 255, 255)),
                BorderBrush =
                    new SolidColorBrush(
                        overflow
                            ? Color.FromArgb(150, 248, 113, 113)
                            : item.IsOvertime
                                ? Color.FromArgb(165, 192, 132, 252)
                                : Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = cardContent,
                Tag = item.Activity.PageId
            };

            ToolTipService.SetToolTip(
                card,
                string.Join(
                    "\n",
                    new[]
                    {
                        priority.Reason,
                        item.IsOvertime
                            ? item.Note
                            : overflow
                                ? item.Note
                                : "Arrastra sobre otra actividad para cambiar el orden."
                    }.Where(value =>
                        !string.IsNullOrWhiteSpace(value))));

            if (!overflow && state != null)
            {
                card.CanDrag = true;
                card.AllowDrop = true;

                card.DragStarting += (_, args) =>
                {
                    state.DraggedPageId =
                        item.Activity.PageId ?? string.Empty;

                    args.Data.RequestedOperation =
                        Windows.ApplicationModel.DataTransfer
                            .DataPackageOperation.Move;

                    args.Data.SetText(
                        state.DraggedPageId);
                };

                card.DragOver += (_, args) =>
                {
                    args.AcceptedOperation =
                        Windows.ApplicationModel.DataTransfer
                            .DataPackageOperation.Move;

                    args.DragUIOverride.Caption =
                        "Cambiar orden y compactar horarios";
                    args.Handled = true;
                };

                card.Drop += async (_, args) =>
                {
                    var sourcePageId =
                        state.DraggedPageId;

                    try
                    {
                        if (args.DataView.Contains(
                                Windows.ApplicationModel.DataTransfer
                                    .StandardDataFormats.Text))
                        {
                            sourcePageId =
                                await args.DataView.GetTextAsync();
                        }
                    }
                    catch
                    {
                    }

                    var point = args.GetPosition(card);
                    var insertAfter =
                        point.Y >= card.ActualHeight / 2d;

                    ReorderOneClickSchedulePreview(
                        state,
                        sourcePageId,
                        item.Activity.PageId,
                        insertAfter);

                    args.Handled = true;
                };
            }

            return card;
        }


        private FrameworkElement BuildOneClickSchedulePreviewContent(
            OneClickSchedulePreviewUiState state)
        {
            var host = new ContentControl
            {
                HorizontalContentAlignment =
                    HorizontalAlignment.Stretch
            };

            state.Host = host;
            host.Content =
                BuildOneClickSchedulePreviewVisual(state);

            return host;
        }

        private FrameworkElement BuildOneClickSchedulePreviewVisual(
            OneClickSchedulePreviewUiState state)
        {
            var result = state.Result;
            var scheduleWarnings =
                BuildOneClickScheduleWarnings(state);

            var root = new StackPanel
            {
                Spacing = 8,
                Width = 500,
                MaxWidth = 500
            };

            Button BuildCompactInfoButton(
                string icon,
                string title,
                string detail,
                Color accent,
                double width = 34)
            {
                var button = new Button
                {
                    Content = icon,
                    Width = width,
                    Height = 30,
                    Padding = new Thickness(4, 1, 4, 1),
                    HorizontalContentAlignment =
                        HorizontalAlignment.Center,
                    VerticalContentAlignment =
                        VerticalAlignment.Center,
                    FontSize = 11,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    CornerRadius = new CornerRadius(6),
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(
                                28,
                                accent.R,
                                accent.G,
                                accent.B)),
                    BorderBrush =
                        new SolidColorBrush(
                            Color.FromArgb(
                                95,
                                accent.R,
                                accent.G,
                                accent.B)),
                    BorderThickness = new Thickness(1)
                };

                ToolTipService.SetToolTip(
                    button,
                    title);

                button.Flyout = new Flyout
                {
                    Content = new Border
                    {
                        Width = 330,
                        MaxWidth = 330,
                        Padding = new Thickness(12),
                        CornerRadius = new CornerRadius(8),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(255, 42, 42, 42)),
                        BorderBrush =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    120,
                                    accent.R,
                                    accent.G,
                                    accent.B)),
                        BorderThickness = new Thickness(1),
                        Child = new StackPanel
                        {
                            Spacing = 6,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = title,
                                    FontSize = 12.5,
                                    FontWeight =
                                        Microsoft.UI.Text.FontWeights.SemiBold,
                                    Foreground =
                                        new SolidColorBrush(accent)
                                },
                                new TextBlock
                                {
                                    Text = detail,
                                    FontSize = 10.5,
                                    TextWrapping = TextWrapping.Wrap,
                                    MaxWidth = 300
                                }
                            }
                        }
                    }
                };

                return button;
            }

            var summary = new StackPanel
            {
                Spacing = 5,
                Padding = new Thickness(12),
                Background =
                    new SolidColorBrush(
                        Color.FromArgb(35, 56, 189, 248))
            };

            summary.Children.Add(
                new TextBlock
                {
                    Text = $"Acomodo generado para {result.Person}",
                    FontSize = 15.5,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

            summary.Children.Add(
                new TextBlock
                {
                    Text =
                        $"{result.Day:dddd, d 'de' MMMM 'de' yyyy} · " +
                        $"{result.Scheduled.Count} programada(s) hoy · " +
                        $"{result.DeferredToTomorrow.Count} → mañana · " +
                        $"{result.Overflow.Count} excedente(s) · " +
                        $"{result.TotalActionCount} acción(es)\n" +
                        $"{result.EffectiveStart:h:mm tt}–{result.EffectiveEnd:h:mm tt} · " +
                        $"{FormatOneClickDuration(result.AvailableMinutes)} disponibles · " +
                        $"límite absoluto {result.AbsoluteEnd:h:mm tt}",
                    FontSize = 10.5,
                    Opacity = 0.76,
                    TextWrapping = TextWrapping.Wrap
                });

            var coverageRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7
            };

            coverageRow.Children.Add(
                new Border
                {
                    Padding = new Thickness(11, 6, 11, 6),
                    CornerRadius = new CornerRadius(16),
                    Background = GetOneClickCoverageBrush(
                        result.CoveragePercentage),
                    Child = new TextBlock
                    {
                        Text = $"Cobertura {result.CoveragePercentage:0}%",
                        FontSize = 11,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.Bold,
                        Foreground =
                            new SolidColorBrush(Colors.Black)
                    }
                });

            if (result.Overflow.Count > 0)
            {
                coverageRow.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(11, 6, 11, 6),
                        CornerRadius = new CornerRadius(16),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(55, 192, 132, 252)),
                        Child = new TextBlock
                        {
                            Text = $"{result.Overflow.Count} excedente(s)",
                            FontSize = 10.5,
                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromArgb(255, 216, 180, 254))
                        }
                    });
            }

            if (result.DeferredToTomorrow.Count > 0)
            {
                coverageRow.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(11, 6, 11, 6),
                        CornerRadius = new CornerRadius(16),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(58, 251, 146, 60)),
                        Child = new TextBlock
                        {
                            Text = $"{result.DeferredToTomorrow.Count} → mañana",
                            FontSize = 10.5,
                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromArgb(255, 253, 186, 116))
                        }
                    });
            }

            if (result.OvertimeCount > 0)
            {
                coverageRow.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(11, 6, 11, 6),
                        CornerRadius = new CornerRadius(16),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(65, 192, 132, 252)),
                        Child = new TextBlock
                        {
                            Text =
                                $"Tiempo extra +{FormatOneClickDuration(result.OvertimeMinutes)}",
                            FontSize = 10.5,
                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromArgb(255, 216, 180, 254))
                        }
                    });
            }

            summary.Children.Add(coverageRow);
            root.Children.Add(summary);

            var compactStatusText =
                "Vista previa · aún no se modifica Notion · " +
                "duración original conservada · " +
                $"comida 1:00–2:00 PM · {result.TotalActionCount} acción(es) al confirmar" +
                (result.IsLateStart
                    ? " · ⚠ inicio tardío"
                    : string.Empty) +
                (result.OvertimeCount > 0
                    ? $" · ⚠ tiempo extra +{FormatOneClickDuration(result.OvertimeMinutes)}"
                    : string.Empty);

            root.Children.Add(
                new Border
                {
                    Padding = new Thickness(10, 7, 10, 7),
                    CornerRadius = new CornerRadius(7),
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(26, 250, 204, 21)),
                    Child = new TextBlock
                    {
                        Text = compactStatusText,
                        FontSize = 9.8,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                });

            if (result.OvertimeCount > 0)
            {
                root.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(10, 8, 10, 8),
                        CornerRadius = new CornerRadius(7),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(42, 192, 132, 252)),
                        BorderBrush =
                            new SolidColorBrush(
                                Color.FromArgb(150, 192, 132, 252)),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            Text =
                                $"⚠ ANFETA acomodó {result.OvertimeCount} actividad(es) usando tiempo extra. " +
                                $"La jornada se extiende {FormatOneClickDuration(result.OvertimeMinutes)} " +
                                $"después de las 6:00 PM y nunca rebasará las {result.AbsoluteEnd:h:mm tt}.",
                            FontSize = 10.2,
                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromArgb(255, 216, 180, 254)),
                            TextWrapping = TextWrapping.Wrap
                        }
                    });
            }

            if (result.DeferredToTomorrow.Count > 0)
            {
                root.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(10, 8, 10, 8),
                        CornerRadius = new CornerRadius(7),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(42, 251, 146, 60)),
                        BorderBrush =
                            new SolidColorBrush(
                                Color.FromArgb(145, 251, 146, 60)),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            Text =
                                $"↪ {result.DeferredToTomorrow.Count} actividad(es) cuyo horario ya terminó " +
                                $"no se volverán a acomodar hoy. Al confirmar se moverán al " +
                                $"{result.Day.AddDays(1):dddd d 'de' MMMM} conservando su horario y duración.",
                            FontSize = 10.2,
                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromArgb(255, 253, 186, 116)),
                            TextWrapping = TextWrapping.Wrap
                        }
                    });
            }

            if (result.InProgressUnchanged.Count > 0)
            {
                root.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(10, 7, 10, 7),
                        CornerRadius = new CornerRadius(7),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(28, 125, 211, 252)),
                        Child = new TextBlock
                        {
                            Text =
                                $"⏳ {result.InProgressUnchanged.Count} actividad(es) están en curso ahora mismo y se conservarán sin mover.",
                            FontSize = 9.9,
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromArgb(255, 186, 230, 253)),
                            TextWrapping = TextWrapping.Wrap
                        }
                    });
            }

            var toolsGrid = new Grid
            {
                ColumnSpacing = 8
            };

            toolsGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            toolsGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var priorityLegend = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };

            priorityLegend.Children.Add(
                new TextBlock
                {
                    Text = "Prioridad",
                    FontSize = 9.2,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.72,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 2, 0)
                });

            foreach (var legend in new[]
                     {
                         (Icon: "!", Label: "Urgente", Color: Color.FromArgb(255, 248, 113, 113)),
                         (Icon: "H", Label: "Hoy", Color: Color.FromArgb(255, 251, 146, 60)),
                         (Icon: "S", Label: "Esta semana", Color: Color.FromArgb(255, 250, 204, 21)),
                         (Icon: "N", Label: "Normal", Color: Color.FromArgb(255, 74, 222, 128)),
                         (Icon: "…", Label: "En espera", Color: Color.FromArgb(255, 96, 165, 250))
                     })
            {
                var legendBrightness =
                    legend.Color.R * 0.299 +
                    legend.Color.G * 0.587 +
                    legend.Color.B * 0.114;

                var legendChip = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background =
                        new SolidColorBrush(legend.Color),
                    Child = new TextBlock
                    {
                        Text = legend.Icon,
                        FontSize = 9.5,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.Bold,
                        Foreground =
                            new SolidColorBrush(
                                legendBrightness >= 145
                                    ? Colors.Black
                                    : Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    }
                };

                ToolTipService.SetToolTip(
                    legendChip,
                    legend.Label);

                priorityLegend.Children.Add(legendChip);
            }

            Grid.SetColumn(priorityLegend, 0);
            toolsGrid.Children.Add(priorityLegend);

            var tools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            tools.Children.Add(
                BuildCompactInfoButton(
                    "ⓘ",
                    "Horario y límites",
                    $"El acomodo inicia a las {result.EffectiveStart:h:mm tt}, respeta la comida de 1:00 PM a 2:00 PM y conserva la duración original. La jornada base termina a las 6:00 PM; ANFETA puede usar hasta 4 horas de tiempo extra, siempre avisando y sin rebasar las {result.AbsoluteEnd:h:mm tt}. Lo que no quepa antes de ese límite se conserva como excedente.",
                    Color.FromArgb(255, 250, 204, 21)));

            tools.Children.Add(
                BuildCompactInfoButton(
                    "↕",
                    "Cómo reordenar",
                    "Arrastra una tarjeta sobre otra. Si la sueltas en la mitad superior se coloca antes; en la mitad inferior se coloca después. ANFETA recalcula y compacta los horarios sin empalmes.",
                    Color.FromArgb(255, 125, 211, 252)));

            tools.Children.Add(
                BuildCompactInfoButton(
                    "⚡",
                    "Regla de prioridad",
                    IsOneClickPm(state.Person)
                        ? "Para PM se colocan primero las RTUZ. Entre revisiones se aplican Quick-Wins: menos checklist pendientes y menor duración. Después se acomodan las demás actividades."
                        : "Para operativos se priorizan proyectos activos WEB, SEO, ADS, Aplicación y Programa; después actividades generales y ajustes rápidos.",
                    Color.FromArgb(255, 74, 222, 128)));

            if (scheduleWarnings.Count > 0)
            {
                tools.Children.Add(
                    BuildCompactInfoButton(
                        $"⚠{scheduleWarnings.Count}",
                        "Advertencias del acomodo",
                        "• " + string.Join("\n• ", scheduleWarnings),
                        Color.FromArgb(255, 251, 146, 60),
                        44));
            }

            Grid.SetColumn(tools, 1);
            toolsGrid.Children.Add(tools);
            root.Children.Add(toolsGrid);

            var list = new StackPanel
            {
                Spacing = 6,
                Padding = new Thickness(0, 0, 4, 0),
                AllowDrop = true
            };

            if (result.DeferredToTomorrow.Count > 0)
            {
                list.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"↪ Pasarán a mañana ({result.DeferredToTomorrow.Count})",
                        Margin = new Thickness(0, 2, 0, 2),
                        FontSize = 11,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(255, 253, 186, 116))
                    });

                foreach (var activity in result.DeferredToTomorrow)
                {
                    list.Children.Add(
                        new Border
                        {
                            Padding = new Thickness(9, 7, 9, 7),
                            CornerRadius = new CornerRadius(7),
                            Background =
                                new SolidColorBrush(
                                    Color.FromArgb(32, 251, 146, 60)),
                            BorderBrush =
                                new SolidColorBrush(
                                    Color.FromArgb(100, 251, 146, 60)),
                            BorderThickness = new Thickness(1),
                            Child = new StackPanel
                            {
                                Spacing = 2,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = activity.Title,
                                        FontSize = 10.8,
                                        FontWeight =
                                            Microsoft.UI.Text.FontWeights.SemiBold,
                                        TextWrapping = TextWrapping.Wrap,
                                        MaxLines = 2,
                                        TextTrimming = TextTrimming.CharacterEllipsis
                                    },
                                    new TextBlock
                                    {
                                        Text =
                                            $"{activity.Start:h:mm tt}–{activity.End:h:mm tt} → " +
                                            $"{result.Day.AddDays(1):dd/MM/yyyy}",
                                        FontSize = 9.5,
                                        Foreground =
                                            new SolidColorBrush(
                                                Color.FromArgb(255, 253, 186, 116))
                                    }
                                }
                            }
                        });
                }

                list.Children.Add(
                    new Border
                    {
                        Height = 4,
                        Opacity = 0,
                        IsHitTestVisible = false
                    });
            }

            if (result.Scheduled.Count == 0)
            {
                list.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(10),
                        CornerRadius = new CornerRadius(7),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(24, 255, 255, 255)),
                        Child = new TextBlock
                        {
                            Text =
                                "No se encontraron actividades elegibles. " +
                                "Las bloqueadas, terminadas, suspendidas, copias visuales, FTF y notificaciones se excluyen.",
                            FontSize = 10.5,
                            Opacity = 0.72,
                            TextWrapping = TextWrapping.Wrap
                        }
                    });
            }
            else
            {
                foreach (var item in result.Scheduled)
                {
                    list.Children.Add(
                        BuildOneClickPreviewCard(
                            item,
                            overflow: false,
                            state));
                }
            }

            if (result.Overflow.Count > 0)
            {
                list.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"⚠ {result.Overflow.Count} excedente(s) · no se guardarán en este acomodo",
                        Margin = new Thickness(0, 6, 0, 0),
                        FontSize = 11,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(255, 192, 132, 252)),
                        TextWrapping = TextWrapping.Wrap
                    });

                foreach (var item in result.Overflow)
                {
                    list.Children.Add(
                        BuildOneClickPreviewCard(
                            item,
                            overflow: true,
                            state));
                }
            }

            // Espacio final real dentro del contenido desplazable. Esto
            // permite llevar completamente a la vista la última tarjeta aun
            // cuando el pie del ContentDialog ocupa parte de la ventana.
            list.Children.Add(
                new Border
                {
                    Height = 34,
                    IsHitTestVisible = false,
                    Opacity = 0
                });

            var availableDialogHeight =
                XamlRoot == null
                    ? 760d
                    : XamlRoot.Size.Height;

            // Se reserva más espacio para el título y los botones fijos del
            // ContentDialog. La lista queda más pequeña, pero siempre tiene
            // desplazamiento propio y nunca termina detrás del pie.
            var activityListHeight =
                Math.Clamp(
                    availableDialogHeight - 470d,
                    210d,
                    340d);

            root.Children.Add(
                new ScrollViewer
                {
                    Height = activityListHeight,
                    MinHeight = 210,
                    MaxHeight = 340,
                    Margin = new Thickness(0, 0, 0, 4),
                    Padding = new Thickness(0, 0, 10, 8),
                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Disabled,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Visible,
                    VerticalScrollMode = ScrollMode.Enabled,
                    ZoomMode = ZoomMode.Disabled,
                    IsTabStop = true,
                    Content = list
                });

            return root;
        }


        private void ReorderOneClickSchedulePreview(
            OneClickSchedulePreviewUiState state,
            string sourcePageId,
            string targetPageId,
            bool insertAfter)
        {
            sourcePageId =
                (sourcePageId ?? string.Empty).Trim();

            targetPageId =
                (targetPageId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(sourcePageId) ||
                string.IsNullOrWhiteSpace(targetPageId) ||
                string.Equals(
                    sourcePageId,
                    targetPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var sourceIndex =
                state.OrderedActivities.FindIndex(activity =>
                    string.Equals(
                        activity.PageId,
                        sourcePageId,
                        StringComparison.OrdinalIgnoreCase));

            var targetIndex =
                state.OrderedActivities.FindIndex(activity =>
                    string.Equals(
                        activity.PageId,
                        targetPageId,
                        StringComparison.OrdinalIgnoreCase));

            if (sourceIndex < 0 || targetIndex < 0)
                return;

            var moved =
                state.OrderedActivities[sourceIndex];

            state.OrderedActivities.RemoveAt(sourceIndex);

            targetIndex =
                state.OrderedActivities.FindIndex(activity =>
                    string.Equals(
                        activity.PageId,
                        targetPageId,
                        StringComparison.OrdinalIgnoreCase));

            if (targetIndex < 0)
                targetIndex = state.OrderedActivities.Count;
            else if (insertAfter)
                targetIndex++;

            targetIndex = Math.Clamp(
                targetIndex,
                0,
                state.OrderedActivities.Count);

            state.OrderedActivities.Insert(
                targetIndex,
                moved);

            state.Result =
                BuildOneClickSchedulePreview(
                    state.Person,
                    state.OrderedActivities);

            if (state.Host != null)
            {
                state.Host.Content =
                    BuildOneClickSchedulePreviewVisual(state);
            }

            if (state.Dialog != null)
            {
                state.Dialog.IsPrimaryButtonEnabled =
                    state.Result.TotalActionCount > 0;

                state.Dialog.DefaultButton =
                    state.Result.TotalActionCount > 0
                        ? ContentDialogButton.Primary
                        : ContentDialogButton.Close;
            }

            state.DraggedPageId = string.Empty;

            StatusText.Text =
                $"Estado: Orden actualizado para {state.Person} · " +
                $"{state.Result.Scheduled.Count} actividades compactadas · " +
                $"{state.Result.OvertimeCount} con tiempo extra · " +
                $"{state.Result.Overflow.Count} excedente(s).";
        }

        private FrameworkElement BuildOneClickConfirmationContent(
            OneClickSchedulePreviewResult result,
            IReadOnlyList<OneClickSchedulePreviewItem> changedItems)
        {
            var root = new StackPanel
            {
                Spacing = 10,
                Width = 470,
                MaxWidth = 470
            };

            root.Children.Add(
                new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(8),
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(40, 250, 204, 21)),
                    Child = new TextBlock
                    {
                        Text =
                            $"Se actualizarán {changedItems.Count} actividad(es) de {result.Person} " +
                            $"para {result.Day:dd/MM/yyyy}. A partir de este paso sí se modificarán " +
                            "las propiedades Fecha POR Hacer en Notion.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11.5
                    }
                });

            root.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Inicio efectivo: {result.EffectiveStart:h:mm tt} · " +
                        $"jornada base: hasta {result.StandardEnd:h:mm tt} · " +
                        $"límite absoluto: {result.AbsoluteEnd:h:mm tt}\n" +
                        $"Cobertura propuesta: {result.CoveragePercentage:0}% · " +
                        $"Tiempo agendado: {FormatOneClickDuration(result.ScheduledMinutes)} · " +
                        $"Tiempo extra: {FormatOneClickDuration(result.OvertimeMinutes)}",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

            if (result.OvertimeCount > 0)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"⚠ {result.OvertimeCount} actividad(es) se guardarán usando " +
                            $"{FormatOneClickDuration(result.OvertimeMinutes)} de extensión después de las 6:00 PM.",
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(255, 216, 180, 254)),
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    });
            }

            if (result.Overflow.Count > 0)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"⚠ {result.Overflow.Count} actividad(es) excedente(s) no se modificarán en este bloque.",
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(255, 251, 146, 60)),
                        TextWrapping = TextWrapping.Wrap
                    });
            }

            var changes = new StackPanel
            {
                Spacing = 7
            };

            foreach (var item in changedItems.Take(12))
            {
                changes.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"• {item.Activity.Title}\n" +
                            $"  {item.Activity.Start:h:mm tt}–{item.Activity.End:h:mm tt}  →  " +
                            $"{item.Start:h:mm tt}–{item.End:h:mm tt}" +
                            (item.IsOvertime
                                ? $" · tiempo extra +{FormatOneClickDuration(item.OvertimeMinutes)}"
                                : string.Empty),
                        FontSize = 10.5,
                        TextWrapping = TextWrapping.Wrap
                    });
            }

            if (changedItems.Count > 12)
            {
                changes.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"…y {changedItems.Count - 12} cambio(s) adicional(es).",
                        FontSize = 10.5,
                        Opacity = 0.72
                    });
            }

            root.Children.Add(
                new ScrollViewer
                {
                    MaxHeight = 300,
                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,
                    Content = changes
                });

            return root;
        }

        private static string BuildOneClickAuditLog(
            OneClickSchedulePreviewResult result,
            OneClickSchedulePreviewItem item)
        {
            return
                $"{DateTimeOffset.Now:dd/MM/yyyy HH:mm} · One Click Schedule · " +
                $"Usuario: {result.Person} · Día: {result.Day:dd/MM/yyyy} · " +
                $"Anterior: {item.Activity.Start:HH:mm}-{item.Activity.End:HH:mm} · " +
                $"Nuevo: {item.Start:HH:mm}-{item.End:HH:mm} · " +
                $"Inicio efectivo: {result.EffectiveStart:HH:mm} · " +
                $"Jornada base: {result.StandardEnd:HH:mm} · " +
                $"Límite absoluto: {result.AbsoluteEnd:HH:mm} · " +
                $"Tiempo extra: {result.OvertimeMinutes} min · " +
                $"Cobertura: {result.CoveragePercentage:0}% · " +
                $"Excedentes: {result.Overflow.Count} · " +
                $"Pasadas a mañana: {result.DeferredToTomorrow.Count}";
        }

        private async Task SaveOneClickScheduleAsync(
            OneClickSchedulePreviewResult result,
            IReadOnlyList<OneClickSchedulePreviewItem> changedItems)
        {
            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var totalActions =
                changedItems.Count +
                result.DeferredToTomorrow.Count;

            if (totalActions == 0)
            {
                StatusText.Text =
                    "Estado: El acomodo ya coincide con los horarios actuales.";
                return;
            }

            var processVersion =
                BeginCalendarProcess(
                    "Guardando acomodo",
                    $"Preparando {totalActions} acción(es) para {result.Person}...");

            var saved =
                new List<NotionCalendarActivity>();

            var failures =
                new List<string>();

            var auditMissing = 0;
            var movedTomorrow = 0;

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(12));

                for (var index = 0;
                     index < result.DeferredToTomorrow.Count;
                     index++)
                {
                    var activity =
                        result.DeferredToTomorrow[index];

                    UpdateCalendarProcess(
                        processVersion,
                        new NotionCalendarProgress(
                            "Pasando pendientes a mañana",
                            index + 1,
                            totalActions,
                            $"Moviendo {index + 1} de {totalActions} · {activity.Title}"));

                    try
                    {
                        if (activity.IsAutomationLocked)
                        {
                            failures.Add(
                                $"{activity.Title}: la actividad quedó bloqueada antes de moverla a mañana.");
                            continue;
                        }

                        var oneClickSourceDate =
                            activity.Start.Date;

                        var oneClickTargetDate =
                            result.Day.AddDays(1).Date;

                        await _notionCalendarService
                            .MoveActivityToDateAsync(
                                token,
                                activity,
                                oneClickTargetDate,
                                cts.Token);

                        RegisterCalendarDayMovement(
                            activity,
                            oneClickSourceDate,
                            oneClickTargetDate,
                            "One Click Schedule");

                        movedTomorrow++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[ONE_CLICK_DEFER] {activity.PageId} · " +
                            $"{activity.Title}\n{ex}");

                        failures.Add(
                            $"{activity.Title}: no se pudo pasar a mañana → {ex.Message}");
                    }
                }

                for (var index = 0;
                     index < changedItems.Count;
                     index++)
                {
                    var item = changedItems[index];

                    UpdateCalendarProcess(
                        processVersion,
                        new NotionCalendarProgress(
                            "Guardando acomodo",
                            result.DeferredToTomorrow.Count + index + 1,
                            totalActions,
                            $"Guardando {result.DeferredToTomorrow.Count + index + 1} de {totalActions} · {item.Activity.Title}"));

                    try
                    {
                        if (item.Activity.IsAutomationLocked)
                        {
                            failures.Add(
                                $"{item.Activity.Title}: la actividad quedó bloqueada antes de guardar.");
                            continue;
                        }

                        var update =
                            await _notionCalendarService
                                .UpdateActivityScheduleWithAuditAsync(
                                    token,
                                    item.Activity,
                                    item.Start,
                                    item.End,
                                    BuildOneClickAuditLog(
                                        result,
                                        item),
                                    cts.Token);

                        saved.Add(update.Activity);

                        if (!update.AuditLogWritten)
                            auditMissing++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[ONE_CLICK_SAVE] {item.Activity.PageId} · " +
                            $"{item.Activity.Title}\n{ex}");

                        failures.Add(
                            $"{item.Activity.Title}: {ex.Message}");
                    }
                }

                var cached =
                    await _notionCalendarService
                        .TryGetCachedDayAsync(
                            result.Day,
                            cts.Token);

                if (cached != null)
                {
                    _calendarActivities = cached;
                    ApplyCachedCalendarReviewFlow(
                        _calendarActivities);
                    DrawCalendarPreservingView(
                        _calendarActivities,
                        force: true);
                }

                var allSaved =
                    failures.Count == 0;

                CompleteCalendarProcess(
                    processVersion,
                    allSaved
                        ? "Acomodo guardado"
                        : "Acomodo guardado parcialmente",
                    allSaved
                        ? $"{saved.Count} reacomodadas hoy · {movedTomorrow} pasadas a mañana."
                        : $"{saved.Count} reacomodadas · {movedTomorrow} a mañana · {failures.Count} con error.",
                    success: allSaved);

                StatusText.Text = allSaved
                    ? $"Estado: One Click Schedule guardado ✅ ({saved.Count} reacomodadas · {movedTomorrow} a mañana)"
                    : $"Estado: Acomodo parcial · {saved.Count} reacomodadas · {movedTomorrow} a mañana · {failures.Count} con error.";

                var resultPanel = new StackPanel
                {
                    Spacing = 8,
                    Width = 470,
                    MaxWidth = 470
                };

                resultPanel.Children.Add(
                    new TextBlock
                    {
                        Text = allSaved
                            ? $"Se guardó el acomodo correctamente: {saved.Count} actividad(es) reacomodadas hoy y {movedTomorrow} pasadas a mañana."
                            : $"Se reacomodaron {saved.Count}, se pasaron {movedTomorrow} a mañana y {failures.Count} no pudieron actualizarse.",
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    });

                if (auditMissing > 0)
                {
                    resultPanel.Children.Add(
                        new TextBlock
                        {
                            Text =
                                $"Aviso: {auditMissing} actividad(es) no registraron auditoría porque " +
                                "la propiedad Audit_FTF_Log no existe o no es de tipo Texto. " +
                                "Los horarios sí fueron guardados.",
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromArgb(255, 250, 204, 21)),
                            TextWrapping = TextWrapping.Wrap
                        });
                }

                if (failures.Count > 0)
                {
                    resultPanel.Children.Add(
                        new TextBlock
                        {
                            Text = "Errores:",
                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold
                        });

                    resultPanel.Children.Add(
                        new ScrollViewer
                        {
                            MaxHeight = 260,
                            Content = new TextBlock
                            {
                                Text = string.Join(
                                    "\n\n",
                                    failures),
                                FontSize = 10.5,
                                TextWrapping = TextWrapping.Wrap
                            }
                        });
                }

                var resultDialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = allSaved
                        ? "Acomodo guardado"
                        : "Acomodo guardado parcialmente",
                    Content = resultPanel,
                    CloseButtonText = "Cerrar",
                    DefaultButton =
                        ContentDialogButton.Close,
                    MinWidth = 530,
                    MaxWidth = 620
                };

                // ContentDialog necesita terminar por completo la animación
                // de cierre del preview antes de mostrar el resultado.
                await Task.Delay(220);
                await resultDialog.ShowAsync();
            }
            catch (OperationCanceledException)
            {
                CompleteCalendarProcess(
                    processVersion,
                    "Guardado cancelado",
                    "La operación fue cancelada o excedió el tiempo permitido.",
                    success: false);

                StatusText.Text =
                    "Estado: Se canceló el guardado del acomodo.";
            }
            catch (Exception ex)
            {
                CompleteCalendarProcess(
                    processVersion,
                    "No se pudo guardar el acomodo",
                    ex.Message,
                    success: false);

                StatusText.Text =
                    $"Estado: No se pudo guardar el acomodo → {ex.Message}";
            }
        }

        private async void CalendarOneClickSchedule_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            if (_oneClickScheduleDialogOpen)
            {
                StatusText.Text =
                    "Estado: One Click Schedule ya está abierto. Cierra la vista actual antes de abrir otra.";
                return;
            }

            var person =
                NormalizeCalendarPerson(
                    element.Tag?.ToString() ?? string.Empty);

            if (string.IsNullOrWhiteSpace(person) ||
                string.Equals(
                    person,
                    "Sin asignar",
                    StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text =
                    "Estado: Selecciona una persona asignada para optimizar su día.";
                return;
            }

            if (_calendarSelectedDate.Date < DateTime.Today)
            {
                StatusText.Text =
                    "Estado: One Click Schedule no modifica días anteriores. Selecciona Hoy o una fecha futura.";
                return;
            }

            _oneClickScheduleDialogOpen = true;

            var sourceButton = sender as Button;

            if (sourceButton != null)
                sourceButton.IsEnabled = false;

            try
            {
                HideCalendarActivityPreviewFlyout();

                var state =
                    new OneClickSchedulePreviewUiState
                    {
                        Person = person
                    };

                var eligibleActivities =
                    GetOneClickScheduleActivities(person);

                var priorityOrderedActivities =
                    PrepareOneClickPriorityOrder(
                        person,
                        eligibleActivities);

                state.OrderedActivities.AddRange(
                    priorityOrderedActivities);

                state.Result =
                    BuildOneClickSchedulePreview(
                        person,
                        state.OrderedActivities);

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"⚡ One Click Schedule · {person}",
                    Content =
                        BuildOneClickSchedulePreviewContent(state),
                    PrimaryButtonText =
                        "Confirmar y guardar en Notion",
                    CloseButtonText = "Cancelar",
                    DefaultButton = state.Result.TotalActionCount > 0
                        ? ContentDialogButton.Primary
                        : ContentDialogButton.Close,
                    IsPrimaryButtonEnabled =
                        state.Result.TotalActionCount > 0,
                    MinWidth = 560,
                    MaxWidth = 620
                };

                state.Dialog = dialog;

                var previewChoice =
                    await dialog.ShowAsync();

                if (previewChoice !=
                    ContentDialogResult.Primary)
                {
                    StatusText.Text =
                        $"Estado: Vista previa de {person} generada · " +
                        $"cobertura {state.Result.CoveragePercentage:0}% · " +
                        "sin cambios en Notion.";
                    return;
                }

                var result = state.Result;

                var changedItems =
                    result.Scheduled
                        .Where(item => item.HasScheduleChange)
                        .ToList();

                if (result.TotalActionCount == 0)
                {
                    StatusText.Text =
                        "Estado: El acomodo ya coincide con los horarios actuales; no hay cambios para guardar.";
                    return;
                }

                // El propio botón del preview es la confirmación final. Se evita
                // abrir un segundo ContentDialog que podía chocar con el primero.
                await Task.Delay(220);

                await SaveOneClickScheduleAsync(
                    result,
                    changedItems);
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains(
                    "ContentDialog",
                    StringComparison.OrdinalIgnoreCase) ||
                      ex.Message.Contains(
                    "single ContentDialog",
                    StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine(
                    $"[ONE_CLICK_DIALOG] {ex}");

                StatusText.Text =
                    "Estado: Ya existe una ventana abierta. Ciérrala y vuelve a pulsar el rayito.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ONE_CLICK] {ex}");

                StatusText.Text =
                    $"Estado: No se pudo preparar One Click Schedule → {ex.Message}";
            }
            finally
            {
                _oneClickScheduleDialogOpen = false;

                if (sourceButton != null)
                    sourceButton.IsEnabled = true;
            }
        }

        private MenuFlyout BuildCalendarHeaderContextFlyout(
            string person)
        {
            var flyout = new MenuFlyout();

            var viewActivities = new MenuFlyoutItem
            {
                Text = "👁 Ver actividades",
                Tag = person
            };

            viewActivities.Click +=
                CalendarPersonPreview_Click;

            flyout.Items.Add(viewActivities);

            var personSummary = new MenuFlyoutItem
            {
                Text = $"📋 Resumen de {person}",
                Tag = person
            };

            personSummary.Click +=
                CalendarHeaderPersonSummary_Click;

            flyout.Items.Add(personSummary);

            var teamSummary = new MenuFlyoutItem
            {
                Text = "📋 Resumen general del día",
                Tag = string.Empty
            };

            teamSummary.Click +=
                CalendarHeaderPersonSummary_Click;

            flyout.Items.Add(teamSummary);

            var returnActivities = new MenuFlyoutItem
            {
                Text = "↩ Devolver actividades al día anterior…",
                Tag = person
            };

            returnActivities.Click +=
                CalendarHeaderReturnActivities_Click;

            flyout.Items.Add(returnActivities);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var moveLeft = new MenuFlyoutItem
            {
                Text = "Mover a la izquierda"
            };

            moveLeft.Click += (_, __) =>
                MoveCalendarPerson(person, -1);

            var moveRight = new MenuFlyoutItem
            {
                Text = "Mover a la derecha"
            };

            moveRight.Click += (_, __) =>
                MoveCalendarPerson(person, 1);

            var moveStart = new MenuFlyoutItem
            {
                Text = "Mover al inicio"
            };

            moveStart.Click += (_, __) =>
                MoveCalendarPersonToEdge(person, toStart: true);

            var moveEnd = new MenuFlyoutItem
            {
                Text = "Mover al final"
            };

            moveEnd.Click += (_, __) =>
                MoveCalendarPersonToEdge(person, toStart: false);

            flyout.Items.Add(moveLeft);
            flyout.Items.Add(moveRight);
            flyout.Items.Add(moveStart);
            flyout.Items.Add(moveEnd);
            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(
                BuildCalendarWidthMenuItem(
                    person,
                    "Ancho pequeño",
                    CalendarSmallPersonColumnWidth));

            flyout.Items.Add(
                BuildCalendarWidthMenuItem(
                    person,
                    "Ancho normal",
                    CalendarBasePersonColumnWidthNormal));

            flyout.Items.Add(
                BuildCalendarWidthMenuItem(
                    person,
                    "Ancho grande",
                    CalendarLargePersonColumnWidth));

            var resetWidth = new MenuFlyoutItem
            {
                Text = "Restablecer ancho"
            };

            resetWidth.Click += (_, __) =>
            {
                _calendarColumnWidths.Remove(person);
                SaveCalendarPreferences();
                DrawCalendar(_calendarActivities);
            };

            flyout.Items.Add(resetWidth);

            return flyout;
        }

        private static bool IsCalendarPendingForReview(
            NotionCalendarActivity activity)
        {
            if (activity == null || activity.IsReviewMirror)
                return false;

            return HasExactCalendarPhase(
                       activity,
                       "prtuzREVISION") ||
                   HasExactCalendarPhase(
                       activity,
                       "sprtuzREVISION");
        }

        private int GetCalendarOverdueMinutes(
            NotionCalendarActivity activity,
            DateTime? referenceTime = null)
        {
            if (!IsCalendarPendingForReview(activity))
                return 0;

            // Una actividad suspendida no se considera atrasada aunque su
            // horario original ya haya terminado. Mientras conserve
            // sprtuzREVISION se muestra únicamente como SUSPENDIDA.
            if (HasExactCalendarPhase(
                    activity,
                    "sprtuzREVISION"))
            {
                return 0;
            }

            var selectedDay = _calendarSelectedDate.Date;
            var now = referenceTime ?? DateTime.Now;

            // Un día futuro todavía no puede considerarse atrasado.
            if (selectedDay > now.Date)
                return 0;

            var effectiveNow = selectedDay < now.Date
                ? selectedDay.AddDays(1).AddTicks(-1)
                : now;

            if (activity.End >= effectiveNow)
                return 0;

            return Math.Max(
                1,
                (int)Math.Floor(
                    (effectiveNow - activity.End).TotalMinutes));
        }

        private static string FormatCalendarDelayMinutes(
            int minutes)
        {
            minutes = Math.Max(0, minutes);

            var hours = minutes / 60;
            var remainder = minutes % 60;

            if (hours > 0 && remainder > 0)
                return $"{hours}H {remainder}M";

            if (hours > 0)
                return $"{hours}H";

            return $"{remainder}M";
        }

        private string ResolveAutomaticReviewRecipient(
            NotionCalendarActivity activity)
        {
            if (activity == null)
                return "John";

            if (!string.IsNullOrWhiteSpace(
                    activity.ReviewAssignee))
            {
                var reviewer = NormalizeCalendarPerson(
                    activity.ReviewAssignee);

                if (string.Equals(
                        reviewer,
                        "John",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        reviewer,
                        "Genaro",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return reviewer;
                }
            }

            if (!string.IsNullOrWhiteSpace(activity.PageId) &&
                _calendarReviewFlowCache.TryGetValue(
                    activity.PageId,
                    out var metadata) &&
                metadata != null)
            {
                var reviewer = NormalizeCalendarPerson(
                    metadata.ReviewAssignee);

                if (string.Equals(
                        reviewer,
                        "John",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        reviewer,
                        "Genaro",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return reviewer;
                }
            }

            var people = SplitPersons(activity.Person)
                .Select(NormalizeCalendarPerson)
                .ToList();

            if (people.Contains(
                    "John",
                    StringComparer.OrdinalIgnoreCase))
            {
                return "John";
            }

            if (people.Contains(
                    "Genaro",
                    StringComparer.OrdinalIgnoreCase))
            {
                return "Genaro";
            }

            // La solicitud de reunión fue que John reciba el aviso cuando
            // una actividad cambie a RTUZ. Si el cambio manual en Notion no
            // deja un revisor identificable, John queda como respaldo.
            return "John";
        }

        private void EnsureCalendarRtuzObservationBaseline(
            IReadOnlyList<NotionCalendarActivity> activities)
        {
            if (activities == null)
                return;

            foreach (var activity in activities
                         .Where(item =>
                             item != null &&
                             !item.IsReviewMirror &&
                             !string.IsNullOrWhiteSpace(item.PageId))
                         .GroupBy(
                             item => item.PageId,
                             StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                if (!_calendarObservedRtuzState.ContainsKey(
                        activity.PageId))
                {
                    _calendarObservedRtuzState[activity.PageId] =
                        HasExactCalendarPhase(
                            activity,
                            "rtuzREVISION");
                }
            }
        }

        private async Task ProcessAutomaticCalendarReviewTransitionsAsync(
            IReadOnlyList<NotionCalendarActivity> incomingActivities,
            IReadOnlyCollection<string> changedPageIds)
        {
            if (incomingActivities == null ||
                changedPageIds == null ||
                changedPageIds.Count == 0)
            {
                return;
            }

            var incomingByPage = incomingActivities
                .Where(activity =>
                    activity != null &&
                    !activity.IsReviewMirror &&
                    !string.IsNullOrWhiteSpace(activity.PageId))
                .GroupBy(
                    activity => activity.PageId,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pageId in changedPageIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!incomingByPage.TryGetValue(
                        pageId,
                        out var current))
                {
                    continue;
                }

                var isRtuz = HasExactCalendarPhase(
                    current,
                    "rtuzREVISION");

                // Primera vez que vemos este PageId: se establece baseline y
                // NO se genera una alerta retrospectiva. A partir de ahí sí
                // detectamos cualquier false -> true aunque cambie la caché.
                if (!_calendarObservedRtuzState.TryGetValue(
                        pageId,
                        out var wasRtuz))
                {
                    _calendarObservedRtuzState[pageId] = isRtuz;
                    continue;
                }

                _calendarObservedRtuzState[pageId] = isRtuz;

                if (wasRtuz || !isRtuz)
                    continue;

                // El revisor real se conserva para el flujo y la metadata.
                // Regla definitiva: toda entrada a rtuzREVISION notifica a John.
                // El revisor real se conserva por separado en ReviewAssignee;
                // el destinatario de esta alarma no depende del revisor.
                var reviewer = ResolveAutomaticReviewRecipient(
                    current);

                const string automaticAlertRecipient = "John";

                var token =
                    ApplicationData.Current.LocalSettings.Values[
                        "Notion.Token"] as string;

                if (string.IsNullOrWhiteSpace(token))
                    continue;

                try
                {
                    _calendarReviewFlowCache.TryGetValue(
                        current.PageId,
                        out var existingMetadata);

                    var original = ResolveOriginalReviewPerson(
                        current,
                        reviewer);

                    var metadata = new ReviewFlowMetadata
                    {
                        OriginalPerson = original,
                        ReviewAssignee = reviewer,
                        State = "pending",
                        SubmittedAt =
                            existingMetadata?.SubmittedAt ??
                            DateTimeOffset.Now,
                        UpdatedAt = DateTimeOffset.Now,
                        UpdatedBy = "Notion / detección automática",
                        Note =
                            "ANFETA detectó cambio manual a rtuzREVISION.",
                        AlertPageId =
                            existingMetadata?.AlertPageId ??
                            string.Empty,
                        AlertPageUrl =
                            existingMetadata?.AlertPageUrl ??
                            string.Empty
                    };

                    using var cts =
                        new CancellationTokenSource(
                            TimeSpan.FromMinutes(2));

                    await _calendarReviewFlowService
                        .SaveReviewFlowAsync(
                            token,
                            current.PageId,
                            metadata,
                            cts.Token);

                    _calendarReviewFlowCache[
                        current.PageId] = metadata;

                    ApplyReviewFlowMetadata(
                        current,
                        metadata);

                    var alert =
                        await SendCalendarReviewAlertAsync(
                            current,
                            automaticAlertRecipient,
                            "Actividad lista para revisión");

                    if (alert != null &&
                        !string.IsNullOrWhiteSpace(alert.PageId))
                    {
                        var linkedMetadata = new ReviewFlowMetadata
                        {
                            OriginalPerson = metadata.OriginalPerson,
                            ReviewAssignee = metadata.ReviewAssignee,
                            State = metadata.State,
                            SubmittedAt = metadata.SubmittedAt,
                            UpdatedAt = DateTimeOffset.Now,
                            UpdatedBy = metadata.UpdatedBy,
                            Note = metadata.Note,
                            AlertPageId = alert.PageId,
                            AlertPageUrl = alert.PageUrl
                        };

                        await _calendarReviewFlowService
                            .SaveReviewFlowAsync(
                                token,
                                current.PageId,
                                linkedMetadata,
                                cts.Token);

                        _calendarReviewFlowCache[
                            current.PageId] = linkedMetadata;

                        ApplyReviewFlowMetadata(
                            current,
                            linkedMetadata);
                    }

                    await PersistCalendarReviewFlowLocalCacheAsync(
                        cts.Token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[CALENDAR_RTUZ_AUTO_ALERT] {pageId}: {ex.Message}");
                }
            }
        }

        private IReadOnlyList<NotionCalendarActivity>
            GetCalendarDailySummaryActivities(
                string? person = null)
        {
            var rawPerson =
                (person ?? string.Empty).Trim();

            var normalizedPerson =
                string.IsNullOrWhiteSpace(rawPerson)
                    ? string.Empty
                    : NormalizeCalendarPerson(rawPerson);

            // IMPORTANTE:
            // El calendario NO dibuja directamente _calendarActivities.
            // Antes resuelve responsables, copias de revisión y persona visible
            // mediante ExpandCalendarReviewActivities(...). El Resumen/FTF debe
            // usar exactamente la misma fuente visual; de lo contrario una
            // tarjeta puede verse bajo Neftali/Karla/etc. y no aparecer en su
            // resumen porque activity.Person conserva todavía el valor crudo
            // devuelto por Notion.
            var expanded = ExpandCalendarReviewActivities(
                    (_calendarActivities ??
                     Array.Empty<NotionCalendarActivity>())
                    .Where(activity =>
                        activity != null &&
                        !string.IsNullOrWhiteSpace(activity.PageId))
                    .Where(activity =>
                        activity.Start.Date ==
                        _calendarSelectedDate.Date)
                    .Where(activity =>
                        !Regex.IsMatch(
                            activity.Title ?? string.Empty,
                            @"(?<![\p{L}\p{Nd}_])F{1,2}TF(?![\p{L}\p{Nd}_])",
                            RegexOptions.IgnoreCase |
                            RegexOptions.CultureInvariant))
                    .ToList())
                .Where(activity =>
                    activity != null &&
                    !string.IsNullOrWhiteSpace(activity.PageId))
                .ToList();

            if (!string.IsNullOrWhiteSpace(normalizedPerson))
            {
                // En un resumen individual sí se conserva la copia visual de
                // revisión si esa es precisamente la tarjeta que aparece en la
                // columna de la persona. Después del filtro se deduplica por
                // PageId para evitar dos tarjetas iguales si responsable y
                // revisor coincidieran accidentalmente.
                return expanded
                    .Where(activity =>
                        SplitPersons(activity.Person)
                            .Select(NormalizeCalendarPerson)
                            .Any(candidate =>
                                string.Equals(
                                    candidate,
                                    normalizedPerson,
                                    StringComparison.OrdinalIgnoreCase)))
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(activity => activity.Start)
                    .ThenBy(activity => activity.End)
                    .ThenBy(activity => activity.Title)
                    .ToList();
            }

            // El resumen general representa actividades reales, no duplica la
            // misma página por la copia visual de revisión. Se toma la tarjeta
            // principal resuelta y se mantiene una sola entrada por PageId.
            return expanded
                .Where(activity => !activity.IsReviewMirror)
                .GroupBy(
                    activity => activity.PageId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(activity => activity.Start)
                .ThenBy(activity => activity.End)
                .ThenBy(activity => activity.Title)
                .ToList();
        }

        private static string GetCalendarSummaryPhaseLabel(
            NotionCalendarActivity activity)
        {
            if (HasExactCalendarPhase(
                    activity,
                    "rtuzREVISION"))
            {
                return "EN REVISIÓN";
            }

            if (HasExactCalendarPhase(
                    activity,
                    "zREVISION"))
            {
                return "TERMINADA";
            }

            if (HasExactCalendarPhase(
                    activity,
                    "sprtuzREVISION"))
            {
                return "SUSPENDIDA";
            }

            if (HasExactCalendarPhase(
                    activity,
                    "prtuzREVISION"))
            {
                return "PENDIENTE";
            }

            return "OTRA FASE";
        }

        private string BuildCalendarDailySummaryText(
            string? person = null)
        {
            var rawPerson =
                (person ?? string.Empty).Trim();

            person = string.IsNullOrWhiteSpace(rawPerson)
                ? string.Empty
                : NormalizeCalendarPerson(rawPerson);

            var source =
                GetCalendarDailySummaryActivities(person)
                    .ToList();

            var suspended = source
                .Where(activity =>
                    HasExactCalendarPhase(
                        activity,
                        "sprtuzREVISION"))
                .ToList();

            var overdue = source
                .Where(activity =>
                    !HasExactCalendarPhase(
                        activity,
                        "sprtuzREVISION") &&
                    GetCalendarOverdueMinutes(activity) > 0)
                .ToList();

            var review = source.Count(activity =>
                HasExactCalendarPhase(
                    activity,
                    "rtuzREVISION"));

            var completed = source.Count(activity =>
                HasExactCalendarPhase(
                    activity,
                    "zREVISION"));

            var pending = source.Count(activity =>
                HasExactCalendarPhase(
                    activity,
                    "prtuzREVISION") &&
                GetCalendarOverdueMinutes(activity) == 0);

            var title = string.IsNullOrWhiteSpace(person)
                ? "RESUMEN GENERAL DEL DÍA"
                : $"RESUMEN DEL DÍA · {person}";

            var builder = new StringBuilder();
            builder.AppendLine(title);
            builder.AppendLine(
                _calendarSelectedDate.ToString(
                    "dddd, d 'de' MMMM 'de' yyyy",
                    new CultureInfo("es-MX")));
            builder.AppendLine();
            builder.AppendLine($"Actividades: {source.Count}");
            builder.AppendLine($"En revisión (rtuz): {review}");
            builder.AppendLine($"Terminadas (zREVISION): {completed}");
            builder.AppendLine($"Pendientes dentro de horario: {pending}");
            builder.AppendLine($"Suspendidas: {suspended.Count}");
            builder.AppendLine($"Atrasadas: {overdue.Count}");

            if (source.Count == 0)
            {
                builder.AppendLine();
                builder.AppendLine(
                    "No hay actividades cargadas para este día y filtro.");
                return builder.ToString().Trim();
            }

            builder.AppendLine();
            builder.AppendLine("DETALLE");

            foreach (var activity in source)
            {
                var overdueMinutes =
                    GetCalendarOverdueMinutes(activity);

                var stats = GetCalendarChecklistStats(
                    activity);

                var checklistText = activity.ChecklistScanned &&
                                    stats.HasChecklist
                    ? $" · checklist {GetChecklistPercentage(stats)}%"
                    : string.Empty;

                var workText = activity.HasWorkLog
                    ? $" · {activity.WorkProgressLabel}"
                    : string.Empty;

                var delayText = overdueMinutes > 0
                    ? $" · ⚠ retraso {FormatCalendarDelayMinutes(overdueMinutes)}"
                    : string.Empty;

                builder.AppendLine(
                    $"• {activity.Start:HH:mm}–{activity.End:HH:mm} · " +
                    $"{GetCalendarSummaryPhaseLabel(activity)} · {activity.Title}" +
                    checklistText +
                    workText +
                    delayText);
            }

            return builder.ToString().Trim();
        }

        private static Color GetCalendarSummaryPhaseColor(
            NotionCalendarActivity activity,
            bool isOverdue)
        {
            if (isOverdue)
            {
                return Color.FromArgb(
                    255,
                    239,
                    68,
                    68);
            }

            if (HasExactCalendarPhase(
                    activity,
                    "rtuzREVISION"))
            {
                return Color.FromArgb(
                    255,
                    52,
                    211,
                    153);
            }

            if (HasExactCalendarPhase(
                    activity,
                    "zREVISION"))
            {
                return Color.FromArgb(
                    255,
                    96,
                    165,
                    250);
            }

            if (HasExactCalendarPhase(
                    activity,
                    "sprtuzREVISION"))
            {
                return Color.FromArgb(
                    255,
                    167,
                    139,
                    250);
            }

            if (HasExactCalendarPhase(
                    activity,
                    "prtuzREVISION"))
            {
                return Color.FromArgb(
                    255,
                    250,
                    204,
                    21);
            }

            return Color.FromArgb(
                255,
                148,
                163,
                184);
        }

        private Border BuildCalendarSummaryMetricCard(
            string icon,
            string label,
            int value,
            Color accent)
        {
            var panel = new StackPanel
            {
                Spacing = 2
            };

            panel.Children.Add(
                new TextBlock
                {
                    Text = $"{icon} {value}",
                    FontSize = 20,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground =
                        new SolidColorBrush(accent)
                });

            panel.Children.Add(
                new TextBlock
                {
                    Text = label,
                    FontSize = 10.5,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                });

            return new Border
            {
                Width = 142,
                MinHeight = 62,
                Padding = new Thickness(11, 8, 11, 8),
                Margin = new Thickness(0, 0, 7, 7),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(
                    Color.FromArgb(
                        70,
                        accent.R,
                        accent.G,
                        accent.B)),
                Background = new SolidColorBrush(
                    Color.FromArgb(
                        22,
                        accent.R,
                        accent.G,
                        accent.B)),
                Child = panel
            };
        }

        private Border BuildCalendarSummaryActivityCard(
            NotionCalendarActivity activity,
            bool includePerson)
        {
            var overdueMinutes =
                GetCalendarOverdueMinutes(activity);

            var isOverdue = overdueMinutes > 0;

            var accent =
                GetCalendarSummaryPhaseColor(
                    activity,
                    isOverdue);

            var root = new Grid
            {
                ColumnSpacing = 10
            };

            root.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(82)
                });

            root.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            root.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var timePanel = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Top
            };

            timePanel.Children.Add(
                new TextBlock
                {
                    Text = $"{activity.Start:HH:mm}",
                    FontSize = 12,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            timePanel.Children.Add(
                new TextBlock
                {
                    Text = $"{activity.End:HH:mm}",
                    FontSize = 10.5,
                    Opacity = 0.60
                });

            Grid.SetColumn(timePanel, 0);
            root.Children.Add(timePanel);

            var body = new StackPanel
            {
                Spacing = 3
            };

            body.Children.Add(
                new TextBlock
                {
                    Text = activity.Title,
                    FontSize = 12.5,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                });

            var detailParts = new List<string>();

            if (includePerson)
            {
                var people = string.Join(
                    ", ",
                    SplitPersons(activity.Person)
                        .Select(NormalizeCalendarPerson)
                        .Where(value =>
                            !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(people))
                    detailParts.Add(people);
            }

            if (!string.IsNullOrWhiteSpace(activity.Project))
                detailParts.Add(activity.Project.Trim());

            var stats = GetCalendarChecklistStats(activity);

            if (activity.ChecklistScanned &&
                stats.HasChecklist)
            {
                detailParts.Add(
                    $"Checklist {GetChecklistPercentage(stats)}%");
            }

            if (activity.HasWorkLog &&
                !string.IsNullOrWhiteSpace(
                    activity.WorkProgressLabel))
            {
                detailParts.Add(activity.WorkProgressLabel);
            }

            if (detailParts.Count > 0)
            {
                body.Children.Add(
                    new TextBlock
                    {
                        Text = string.Join(" · ", detailParts),
                        FontSize = 10.5,
                        Opacity = 0.68,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis
                    });
            }

            Grid.SetColumn(body, 1);
            root.Children.Add(body);

            var right = new StackPanel
            {
                Spacing = 5,
                HorizontalAlignment =
                    HorizontalAlignment.Right
            };

            right.Children.Add(
                new Border
                {
                    Padding = new Thickness(7, 2, 7, 2),
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(
                        Color.FromArgb(
                            38,
                            accent.R,
                            accent.G,
                            accent.B)),
                    BorderBrush = new SolidColorBrush(
                        Color.FromArgb(
                            140,
                            accent.R,
                            accent.G,
                            accent.B)),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = GetCalendarSummaryPhaseLabel(activity),
                        FontSize = 9.5,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(accent)
                    }
                });

            if (isOverdue)
            {
                right.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"⚠ +{FormatCalendarDelayMinutes(overdueMinutes)}",
                        FontSize = 10,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(accent),
                        HorizontalAlignment =
                            HorizontalAlignment.Right
                    });
            }

            Grid.SetColumn(right, 2);
            root.Children.Add(right);

            return new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 5),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(
                    Color.FromArgb(
                        80,
                        accent.R,
                        accent.G,
                        accent.B)),
                Background = new SolidColorBrush(
                    Color.FromArgb(16, 255, 255, 255)),
                Child = root,
                Tag = activity
            };
        }

        private FrameworkElement BuildCalendarSummarySection(
            string title,
            string icon,
            IReadOnlyList<NotionCalendarActivity> activities,
            Color accent,
            bool includePerson,
            IDictionary<string, Border> cardMap)
        {
            var root = new StackPanel
            {
                Spacing = 6
            };

            var heading = new Grid();

            heading.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            heading.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var headingText = new TextBlock
            {
                Text = $"{icon} {title}",
                FontSize = 13,
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accent)
            };

            Grid.SetColumn(headingText, 0);
            heading.Children.Add(headingText);

            var countText = new TextBlock
            {
                Text = activities.Count.ToString(
                    CultureInfo.InvariantCulture),
                FontSize = 11,
                Opacity = 0.66,
                VerticalAlignment =
                    VerticalAlignment.Center
            };

            Grid.SetColumn(countText, 1);
            heading.Children.Add(countText);
            root.Children.Add(heading);

            var itemsPanel = new StackPanel
            {
                Spacing = 0
            };

            root.Children.Add(itemsPanel);

            var collapsedLimit = includePerson
                ? 7
                : int.MaxValue;
            var expanded = false;

            void RenderItems()
            {
                itemsPanel.Children.Clear();

                var visible = expanded
                    ? activities
                    : activities
                        .Take(collapsedLimit)
                        .ToList();

                foreach (var activity in visible)
                {
                    var card =
                        BuildCalendarSummaryActivityCard(
                            activity,
                            includePerson);

                    itemsPanel.Children.Add(card);

                    if (!string.IsNullOrWhiteSpace(activity.PageId))
                        cardMap[activity.PageId] = card;
                }
            }

            RenderItems();

            if (activities.Count > collapsedLimit)
            {
                var toggle = new Button
                {
                    Content =
                        $"Ver {activities.Count - collapsedLimit} restantes",
                    HorizontalAlignment =
                        HorizontalAlignment.Left,
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 10.5
                };

                toggle.Click +=
                    (_, __) =>
                    {
                        expanded = !expanded;
                        toggle.Content = expanded
                            ? "Mostrar menos"
                            : $"Ver {activities.Count - collapsedLimit} restantes";
                        RenderItems();
                    };

                root.Children.Add(toggle);
            }

            return new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(
                    Color.FromArgb(
                        52,
                        accent.R,
                        accent.G,
                        accent.B)),
                Background = new SolidColorBrush(
                    Color.FromArgb(10, 255, 255, 255)),
                Child = root
            };
        }

        private string BuildCalendarFtfSpeechText(
            NotionCalendarActivity activity,
            int position,
            int total)
        {
            var parts = new List<string>
            {
                $"Actividad {position} de {total}",
                $"Horario de {activity.Start:HH:mm} a {activity.End:HH:mm}",
                $"Estado: {GetCalendarSummaryPhaseLabel(activity)}"
            };

            var title = CleanSpeechText(activity.Title);
            if (!string.IsNullOrWhiteSpace(title))
                parts.Add($"Actividad: {title}");

            var project = CleanSpeechText(activity.Project);
            if (!string.IsNullOrWhiteSpace(project))
                parts.Add($"Proyecto: {project}");

            var description = CleanSpeechText(activity.Description);
            if (!string.IsNullOrWhiteSpace(description))
                parts.Add($"Descripción: {description}");

            var stats = GetCalendarChecklistStats(activity);
            if (activity.ChecklistScanned && stats.HasChecklist)
            {
                parts.Add(
                    $"Checklist: {GetChecklistPercentage(stats)} por ciento");
            }

            if (activity.HasWorkLog &&
                !string.IsNullOrWhiteSpace(
                    activity.WorkProgressLabel))
            {
                parts.Add(
                    $"Tiempo trabajado: {CleanSpeechText(activity.WorkProgressLabel)}");
            }

            var overdueMinutes =
                GetCalendarOverdueMinutes(activity);

            if (overdueMinutes > 0)
            {
                parts.Add(
                    $"Atención: retraso de {FormatCalendarDelayMinutes(overdueMinutes)}");
            }

            return string.Join(
                ". ",
                parts.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
        }

        private async Task SpeakCalendarFtfTextAsync(
            string speechText,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(speechText))
                return;

            cancellationToken.ThrowIfCancellationRequested();

            StopNotionPreviewSpeech();

            var stream =
                await _previewSpeechSynth
                    .SynthesizeTextToStreamAsync(
                        speechText);

            cancellationToken.ThrowIfCancellationRequested();

            var player = new MediaPlayer
            {
                Source =
                    MediaSource.CreateFromStream(
                        stream,
                        stream.ContentType)
            };

            _previewSpeechPlayer = player;
            _previewSpeechPlaying = true;

            var completion =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            player.MediaEnded +=
                (_, __) => completion.TrySetResult(true);

            player.MediaFailed +=
                (_, __) => completion.TrySetResult(false);

            using var registration =
                cancellationToken.Register(
                    () =>
                    {
                        DispatcherQueue.TryEnqueue(
                            () =>
                            {
                                if (ReferenceEquals(
                                        _previewSpeechPlayer,
                                        player))
                                {
                                    StopNotionPreviewSpeech();
                                }

                                completion.TrySetCanceled(
                                    cancellationToken);
                            });
                    });

            player.Play();

            try
            {
                await completion.Task;
            }
            finally
            {
                if (ReferenceEquals(
                        _previewSpeechPlayer,
                        player))
                {
                    StopNotionPreviewSpeech();
                }
            }
        }

        private async Task ShowCalendarDailySummaryDialogAsync(
            string person)
        {
            person = string.IsNullOrWhiteSpace(person)
                ? string.Empty
                : NormalizeCalendarPerson(person);

            var activities =
                GetCalendarDailySummaryActivities(person)
                    .ToList();

            var suspended = activities
                .Where(activity =>
                    HasExactCalendarPhase(
                        activity,
                        "sprtuzREVISION"))
                .ToList();

            var overdue = activities
                .Where(activity =>
                    !HasExactCalendarPhase(
                        activity,
                        "sprtuzREVISION") &&
                    GetCalendarOverdueMinutes(activity) > 0)
                .ToList();

            var review = activities
                .Where(activity =>
                    HasExactCalendarPhase(
                        activity,
                        "rtuzREVISION"))
                .ToList();

            var completed = activities
                .Where(activity =>
                    HasExactCalendarPhase(
                        activity,
                        "zREVISION"))
                .ToList();

            var pending = activities
                .Where(activity =>
                    HasExactCalendarPhase(
                        activity,
                        "prtuzREVISION") &&
                    GetCalendarOverdueMinutes(activity) == 0)
                .ToList();

            var categorizedIds = new HashSet<string>(
                overdue
                    .Concat(suspended)
                    .Concat(review)
                    .Concat(completed)
                    .Concat(pending)
                    .Select(activity => activity.PageId),
                StringComparer.OrdinalIgnoreCase);

            var other = activities
                .Where(activity =>
                    !categorizedIds.Contains(activity.PageId))
                .ToList();

            var summary = BuildCalendarDailySummaryText(person);
            var includePerson = string.IsNullOrWhiteSpace(person);
            var cardMap = new Dictionary<string, Border>(
                StringComparer.OrdinalIgnoreCase);

            var root = new StackPanel
            {
                Width = 760,
                Spacing = 8
            };

            root.Children.Add(
                new TextBlock
                {
                    Text = _calendarSelectedDate.ToString(
                        "dddd, d 'de' MMMM 'de' yyyy",
                        new CultureInfo("es-MX")),
                    FontSize = 11.5,
                    Opacity = 0.68
                });

            var metrics = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 5,
                ItemWidth = 149,
                ItemHeight = 70,
                Width = 760
            };

            metrics.Children.Add(
                BuildCalendarSummaryMetricCard(
                    "📌",
                    "Actividades",
                    activities.Count,
                    Color.FromArgb(255, 148, 163, 184)));

            metrics.Children.Add(
                BuildCalendarSummaryMetricCard(
                    "🚨",
                    "Atrasadas",
                    overdue.Count,
                    Color.FromArgb(255, 239, 68, 68)));

            metrics.Children.Add(
                BuildCalendarSummaryMetricCard(
                    "🟡",
                    "Pendientes",
                    pending.Count,
                    Color.FromArgb(255, 250, 204, 21)));

            metrics.Children.Add(
                BuildCalendarSummaryMetricCard(
                    "🟢",
                    "En revisión",
                    review.Count,
                    Color.FromArgb(255, 52, 211, 153)));

            metrics.Children.Add(
                BuildCalendarSummaryMetricCard(
                    "✅",
                    "Terminadas",
                    completed.Count,
                    Color.FromArgb(255, 96, 165, 250)));

            root.Children.Add(metrics);

            CancellationTokenSource? ftfCts = null;
            var ftfIndex = -1;

            if (!string.IsNullOrWhiteSpace(person) &&
                activities.Count > 0)
            {
                var ftfStatus = new TextBlock
                {
                    Text =
                        "FTF listo · usa Leer FTF para recorrer el día en orden.",
                    FontSize = 10.5,
                    Opacity = 0.68,
                    TextWrapping = TextWrapping.Wrap
                };

                var readFtf = new Button
                {
                    Content = "▶ Leer FTF",
                    Padding = new Thickness(11, 5, 11, 5)
                };

                var previousFtf = new Button
                {
                    Content = "⏮ Anterior",
                    Padding = new Thickness(11, 5, 11, 5)
                };

                var nextFtf = new Button
                {
                    Content = "⏭ Siguiente",
                    Padding = new Thickness(11, 5, 11, 5)
                };

                var stopFtf = new Button
                {
                    Content = "■ Detener",
                    Padding = new Thickness(11, 5, 11, 5),
                    IsEnabled = false
                };

                void ResetCardHighlight()
                {
                    foreach (var card in cardMap.Values)
                    {
                        if (card.Tag is not NotionCalendarActivity item)
                            continue;

                        var itemAccent =
                            GetCalendarSummaryPhaseColor(
                                item,
                                GetCalendarOverdueMinutes(item) > 0);

                        card.BorderThickness = new Thickness(1);
                        card.BorderBrush = new SolidColorBrush(
                            Color.FromArgb(
                                80,
                                itemAccent.R,
                                itemAccent.G,
                                itemAccent.B));
                    }
                }

                void HighlightActivity(
                    NotionCalendarActivity activity)
                {
                    ResetCardHighlight();

                    if (!string.IsNullOrWhiteSpace(activity.PageId) &&
                        cardMap.TryGetValue(
                            activity.PageId,
                            out var selectedCard))
                    {
                        selectedCard.BorderThickness =
                            new Thickness(2);
                        selectedCard.BorderBrush =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    255,
                                    56,
                                    189,
                                    248));
                    }
                }

                async Task ReadSingleFtfAsync(
                    int requestedIndex)
                {
                    if (activities.Count == 0)
                        return;

                    requestedIndex = Math.Clamp(
                        requestedIndex,
                        0,
                        activities.Count - 1);

                    try
                    {
                        ftfCts?.Cancel();
                        ftfCts?.Dispose();
                        ftfCts = new CancellationTokenSource();

                        ftfIndex = requestedIndex;
                        var activity = activities[ftfIndex];
                        HighlightActivity(activity);
                        stopFtf.IsEnabled = true;

                        ftfStatus.Text =
                            $"Leyendo {ftfIndex + 1}/{activities.Count} · {activity.Start:HH:mm} · {activity.Title}";

                        await SpeakCalendarFtfTextAsync(
                            BuildCalendarFtfSpeechText(
                                activity,
                                ftfIndex + 1,
                                activities.Count),
                            ftfCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        stopFtf.IsEnabled = false;
                    }
                }

                readFtf.Click +=
                    async (_, __) =>
                    {
                        try
                        {
                            ftfCts?.Cancel();
                            ftfCts?.Dispose();
                            ftfCts = new CancellationTokenSource();

                            readFtf.IsEnabled = false;
                            stopFtf.IsEnabled = true;

                            var token = ftfCts.Token;

                            for (var index = 0;
                                 index < activities.Count;
                                 index++)
                            {
                                token.ThrowIfCancellationRequested();

                                ftfIndex = index;
                                var activity = activities[index];
                                HighlightActivity(activity);

                                ftfStatus.Text =
                                    $"Leyendo {index + 1}/{activities.Count} · {activity.Start:HH:mm} · {activity.Title}";

                                await SpeakCalendarFtfTextAsync(
                                    BuildCalendarFtfSpeechText(
                                        activity,
                                        index + 1,
                                        activities.Count),
                                    token);
                            }

                            token.ThrowIfCancellationRequested();

                            ftfStatus.Text = "FTF TERMINADO ✅";

                            await SpeakCalendarFtfTextAsync(
                                "FTF terminado",
                                token);
                        }
                        catch (OperationCanceledException)
                        {
                            ftfStatus.Text = "Lectura detenida.";
                        }
                        finally
                        {
                            readFtf.IsEnabled = true;
                            stopFtf.IsEnabled = false;
                            ResetCardHighlight();
                        }
                    };

                previousFtf.Click +=
                    async (_, __) =>
                    {
                        var target = ftfIndex <= 0
                            ? 0
                            : ftfIndex - 1;

                        await ReadSingleFtfAsync(target);
                    };

                nextFtf.Click +=
                    async (_, __) =>
                    {
                        var target = ftfIndex < 0
                            ? 0
                            : Math.Min(
                                activities.Count - 1,
                                ftfIndex + 1);

                        await ReadSingleFtfAsync(target);
                    };

                stopFtf.Click +=
                    (_, __) =>
                    {
                        ftfCts?.Cancel();
                        StopNotionPreviewSpeech();
                        stopFtf.IsEnabled = false;
                        readFtf.IsEnabled = true;
                        ftfStatus.Text = "Lectura detenida.";
                        ResetCardHighlight();
                    };

                var ftfActions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7
                };

                ftfActions.Children.Add(readFtf);
                ftfActions.Children.Add(previousFtf);
                ftfActions.Children.Add(nextFtf);
                ftfActions.Children.Add(stopFtf);

                var ftfPanel = new StackPanel
                {
                    Spacing = 7
                };

                ftfPanel.Children.Add(ftfActions);
                ftfPanel.Children.Add(ftfStatus);

                root.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(11, 9, 11, 9),
                        CornerRadius = new CornerRadius(8),
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(
                            Color.FromArgb(
                                80,
                                56,
                                189,
                                248)),
                        Background = new SolidColorBrush(
                            Color.FromArgb(
                                18,
                                56,
                                189,
                                248)),
                        Child = ftfPanel
                    });
            }

            if (overdue.Count > 0)
            {
                root.Children.Add(
                    BuildCalendarSummarySection(
                        "ATRASADAS",
                        "🚨",
                        overdue,
                        Color.FromArgb(255, 239, 68, 68),
                        includePerson,
                        cardMap));
            }

            if (pending.Count > 0)
            {
                root.Children.Add(
                    BuildCalendarSummarySection(
                        "PENDIENTES",
                        "🟡",
                        pending,
                        Color.FromArgb(255, 250, 204, 21),
                        includePerson,
                        cardMap));
            }

            if (suspended.Count > 0)
            {
                root.Children.Add(
                    BuildCalendarSummarySection(
                        "SUSPENDIDAS",
                        "⏸",
                        suspended,
                        Color.FromArgb(255, 167, 139, 250),
                        includePerson,
                        cardMap));
            }

            if (review.Count > 0)
            {
                root.Children.Add(
                    BuildCalendarSummarySection(
                        "EN REVISIÓN",
                        "🟢",
                        review,
                        Color.FromArgb(255, 52, 211, 153),
                        includePerson,
                        cardMap));
            }

            if (completed.Count > 0)
            {
                root.Children.Add(
                    BuildCalendarSummarySection(
                        "TERMINADAS",
                        "✅",
                        completed,
                        Color.FromArgb(255, 96, 165, 250),
                        includePerson,
                        cardMap));
            }

            if (other.Count > 0)
            {
                root.Children.Add(
                    BuildCalendarSummarySection(
                        "OTRAS FASES",
                        "•",
                        other,
                        Color.FromArgb(255, 148, 163, 184),
                        includePerson,
                        cardMap));
            }

            if (activities.Count == 0)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "No hay actividades cargadas para este día y filtro.",
                        Margin = new Thickness(0, 20, 0, 20),
                        HorizontalAlignment =
                            HorizontalAlignment.Center,
                        Opacity = 0.68
                    });
            }

            var scroll = new ScrollViewer
            {
                Content = root,
                MinWidth = 780,
                MaxWidth = 820,
                MinHeight = 420,
                MaxHeight = 650,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Disabled,
                HorizontalContentAlignment =
                    HorizontalAlignment.Stretch
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = string.IsNullOrWhiteSpace(person)
                    ? "📋 Resumen general del día"
                    : $"📋 Resumen de {person}",
                Content = scroll,
                PrimaryButtonText = "Copiar resumen",
                CloseButtonText = "Cerrar",
                DefaultButton = ContentDialogButton.Close,
                MinWidth = 840,
                MaxWidth = 840
            };

            dialog.Resources["ContentDialogMinWidth"] = 840d;
            dialog.Resources["ContentDialogMaxWidth"] = 840d;

            dialog.Closed +=
                (_, __) =>
                {
                    ftfCts?.Cancel();
                    ftfCts?.Dispose();
                    StopNotionPreviewSpeech();
                };

            if (await dialog.ShowAsync() ==
                ContentDialogResult.Primary)
            {
                CopyCalendarText(
                    summary,
                    "Estado: Resumen del día copiado ✅");
            }
        }

        private async void CalendarHeaderPersonSummary_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var rawPerson =
                (element.Tag?.ToString() ??
                 string.Empty).Trim();

            var person = string.IsNullOrWhiteSpace(rawPerson)
                ? string.Empty
                : NormalizeCalendarPerson(rawPerson);

            await ShowCalendarDailySummaryDialogAsync(person);
        }

        private async void CalendarHeaderReturnActivities_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var person =
                NormalizeCalendarPerson(
                    element.Tag?.ToString() ??
                    string.Empty);

            if (string.IsNullOrWhiteSpace(person))
                return;

            await ShowReturnCalendarActivitiesDialogAsync(
                person);
        }

        private static bool IsCalendarBulkReturnEligible(
            NotionCalendarActivity activity)
        {
            return activity != null &&
                   !activity.IsReviewMirror &&
                   !activity.IsAutomationLocked &&
                   !IsOneClickScheduleExcluded(activity);
        }

        private async Task ShowReturnCalendarActivitiesDialogAsync(
            string person)
        {
            var allForPerson =
                ExpandCalendarReviewActivities(_calendarActivities)
                    .Where(activity =>
                        activity != null &&
                        SplitPersons(activity.Person)
                            .Select(NormalizeCalendarPerson)
                            .Any(candidate =>
                                string.Equals(
                                    candidate,
                                    person,
                                    StringComparison.OrdinalIgnoreCase)))
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(activity => activity.Start)
                    .ThenBy(activity => activity.Title)
                    .ToList();

            var eligible =
                allForPerson
                    .Where(IsCalendarBulkReturnEligible)
                    .ToList();

            var lockedCount =
                allForPerson.Count(activity =>
                    activity.IsAutomationLocked);

            if (eligible.Count == 0)
            {
                StatusText.Text = lockedCount > 0
                    ? $"Estado: No hay actividades para devolver. {lockedCount} están bloqueadas."
                    : "Estado: No hay actividades elegibles para devolver al día anterior.";
                return;
            }

            var targetDate =
                _calendarSelectedDate.Date.AddDays(-1);

            var root = new StackPanel
            {
                Width = 610,
                MaxWidth = 610,
                Spacing = 10
            };

            root.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Selecciona las actividades de {person} que regresarán al {targetDate:dddd d 'de' MMMM 'de' yyyy}. " +
                        "Se conserva la hora y duración originales.",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            if (lockedCount > 0)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"🔒 {lockedCount} actividad(es) bloqueada(s) quedaron fuera de la selección.",
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(255, 216, 180, 254)),
                        TextWrapping = TextWrapping.Wrap
                    });
            }

            var selectionActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };

            var selectAllButton = new Button
            {
                Content = "Seleccionar todas"
            };

            var clearButton = new Button
            {
                Content = "Limpiar selección"
            };

            selectionActions.Children.Add(selectAllButton);
            selectionActions.Children.Add(clearButton);
            root.Children.Add(selectionActions);

            var itemsPanel = new StackPanel
            {
                Spacing = 6
            };

            var selections =
                new List<(
                    NotionCalendarActivity Activity,
                    CheckBox CheckBox)>();

            foreach (var activity in eligible)
            {
                var checkBox = new CheckBox
                {
                    IsChecked = true,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = activity.Title,
                                FontWeight =
                                    Microsoft.UI.Text.FontWeights.SemiBold,
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text =
                                    $"{activity.TimeLabel} · {activity.Status}",
                                FontSize = 10.5,
                                Opacity = 0.70,
                                TextWrapping = TextWrapping.Wrap
                            }
                        }
                    }
                };

                selections.Add((activity, checkBox));

                itemsPanel.Children.Add(
                    new Border
                    {
                        Padding = new Thickness(9, 7, 9, 7),
                        CornerRadius = new CornerRadius(7),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(28, 255, 255, 255)),
                        Child = checkBox
                    });
            }

            selectAllButton.Click += (_, __) =>
            {
                foreach (var selection in selections)
                    selection.CheckBox.IsChecked = true;
            };

            clearButton.Click += (_, __) =>
            {
                foreach (var selection in selections)
                    selection.CheckBox.IsChecked = false;
            };

            root.Children.Add(
                new ScrollViewer
                {
                    MinHeight = 260,
                    MaxHeight = 430,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled,
                    Content = itemsPanel
                });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Devolver actividades · {person}",
                Content = root,
                PrimaryButtonText = "Devolver seleccionadas",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                MinWidth = 670,
                MaxWidth = 720
            };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            var selected =
                selections
                    .Where(selection =>
                        selection.CheckBox.IsChecked == true)
                    .Select(selection => selection.Activity)
                    .ToList();

            if (selected.Count == 0)
            {
                StatusText.Text =
                    "Estado: No seleccionaste actividades para devolver.";
                return;
            }

            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Confirmar devolución",
                Content =
                    $"Se moverán {selected.Count} actividad(es) de {person} al {targetDate:dd/MM/yyyy}. " +
                    "Las bloqueadas, terminadas, suspendidas y copias visuales no se modificarán.",
                PrimaryButtonText = "Sí, devolver",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close
            };

            if (await confirmation.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            await ReturnCalendarActivitiesAsync(
                person,
                selected,
                targetDate);
        }

        private async Task ReturnCalendarActivitiesAsync(
            string person,
            IReadOnlyList<NotionCalendarActivity> activities,
            DateTime targetDate)
        {
            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var processVersion =
                BeginCalendarProcess(
                    "Devolviendo actividades",
                    $"Preparando {activities.Count} actividad(es) de {person}…");

            var moved = 0;
            var failures = new List<string>();

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(15));

                for (var index = 0;
                     index < activities.Count;
                     index++)
                {
                    var activity = activities[index];

                    UpdateCalendarProcess(
                        processVersion,
                        new NotionCalendarProgress(
                            "Devolviendo actividades",
                            index + 1,
                            activities.Count,
                            $"{index + 1} de {activities.Count} · {activity.Title}"));

                    if (activity.IsAutomationLocked)
                    {
                        failures.Add(
                            $"{activity.Title}: está bloqueada.");
                        continue;
                    }

                    try
                    {
                        await _notionCalendarService
                            .MoveActivityToDateAsync(
                                token,
                                activity,
                                targetDate,
                                cts.Token);

                        ClearCalendarDayMovement(
                            activity.PageId);

                        moved++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(
                            $"{activity.Title}: {ex.Message}");
                    }
                }

                var current =
                    await _notionCalendarService
                        .TryGetCachedDayAsync(
                            _calendarSelectedDate,
                            cts.Token);

                _calendarActivities =
                    current ??
                    Array.Empty<NotionCalendarActivity>();

                DrawCalendarPreservingView(
                    _calendarActivities,
                    force: true);

                var success =
                    failures.Count == 0;

                CompleteCalendarProcess(
                    processVersion,
                    success
                        ? "Actividades devueltas"
                        : "Devolución parcial",
                    $"{moved} movidas · {failures.Count} con error.",
                    success);

                StatusText.Text =
                    $"Estado: {moved} actividad(es) devueltas al {targetDate:dd/MM/yyyy}" +
                    (failures.Count == 0
                        ? " ✅"
                        : $" · {failures.Count} con error.");

                if (failures.Count > 0)
                {
                    var resultDialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "Devolución parcial",
                        Content = new ScrollViewer
                        {
                            MaxHeight = 320,
                            Content = new TextBlock
                            {
                                Text =
                                    $"Movidas: {moved}\nErrores: {failures.Count}\n\n" +
                                    string.Join("\n\n", failures),
                                TextWrapping = TextWrapping.Wrap
                            }
                        },
                        CloseButtonText = "Cerrar"
                    };

                    await resultDialog.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                CompleteCalendarProcess(
                    processVersion,
                    "No se pudieron devolver",
                    ex.Message,
                    success: false);

                StatusText.Text =
                    $"Estado: No se pudieron devolver las actividades → {ex.Message}";
            }
        }

        private MenuFlyoutItem BuildCalendarWidthMenuItem(
            string person,
            string label,
            double width)
        {
            var item = new MenuFlyoutItem
            {
                Text = label
            };

            item.Click += (_, __) =>
            {
                _calendarColumnWidths[person] = width;
                SaveCalendarPreferences();
                DrawCalendar(_calendarActivities);
            };

            return item;
        }

        private void MoveCalendarPersonToEdge(
            string person,
            bool toStart)
        {
            var index = _calendarPeopleOrder.FindIndex(x =>
                string.Equals(
                    x,
                    person,
                    StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                return;

            _calendarPeopleOrder.RemoveAt(index);

            if (toStart)
                _calendarPeopleOrder.Insert(0, person);
            else
                _calendarPeopleOrder.Add(person);

            SaveCalendarPreferences();
            DrawCalendar(_calendarActivities);
        }

        private void MoveCalendarPerson(
            string person,
            int direction)
        {
            var currentIndex =
                _calendarPeopleOrder.FindIndex(x =>
                    string.Equals(
                        x,
                        person,
                        StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0)
                return;

            var targetIndex =
                Math.Clamp(
                    currentIndex + direction,
                    0,
                    _calendarPeopleOrder.Count - 1);

            if (targetIndex == currentIndex)
                return;

            _calendarPeopleOrder.RemoveAt(currentIndex);
            _calendarPeopleOrder.Insert(targetIndex, person);

            SaveCalendarPreferences();
            DrawCalendar(_calendarActivities);
        }

        private async Task PreloadCalendarOnStartupAsync()
        {
            if (_calendarPreloadStarted)
            {
                if (_calendarPreloadTask != null)
                    await _calendarPreloadTask;

                return;
            }

            _calendarPreloadStarted = true;

            _calendarPreloadTask =
                PreloadCalendarCoreAsync();

            await _calendarPreloadTask;
        }

        private async Task PreloadCalendarCoreAsync()
        {
            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
                return;

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(20));

                // Hoy se prepara primero para que sea la vista inicial.
                await _notionCalendarService.PreloadDayAsync(
                    token,
                    DateTime.Today,
                    cts.Token);

                // Después se preparan los accesos rápidos sin bloquear la UI.
                await _notionCalendarService.PreloadDayAsync(
                    token,
                    DateTime.Today.AddDays(-1),
                    cts.Token);

                await _notionCalendarService.PreloadDayAsync(
                    token,
                    DateTime.Today.AddDays(1),
                    cts.Token);
            }
            catch
            {
                // La precarga es silenciosa y nunca debe bloquear el buscador.
            }
        }

        private async Task RefreshCalendarAfterNotionChangesAsync(
            DateTimeOffset changedAfterUtc)
        {
            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
                return;

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(3));

                var changed =
                    await _notionCalendarService.RefreshChangedSinceAsync(
                        token,
                        changedAfterUtc,
                        cts.Token);

                if (!changed)
                    return;

                var cached =
                    await _notionCalendarService.TryGetCachedDayAsync(
                        _calendarSelectedDate);

                if (cached != null)
                {
                    _calendarActivities = cached;

                    if (_calendarViewActive)
                    {
                        DrawCalendar(_calendarActivities);

                        StartCalendarIncrementalChecklistRefresh(
                            _notionCalendarService.LastChangedPageIds,
                            _calendarSelectedDate.Date,
                            _calendarLoadVersion);

                        StatusText.Text =
                            "Estado: Calendario actualizado por cambios de Notion ✅";
                    }
                }
            }
            catch
            {
                // El sync principal de Notion ya informa sus propios errores.
            }
        }

        private async Task RebuildCalendarCacheAfterFullSyncAsync()
        {
            _notionCalendarService.ClearCache();

            _calendarPreloadStarted = false;
            _calendarPreloadTask = null;

            await PreloadCalendarOnStartupAsync();

            if (!_calendarViewActive)
                return;

            var cached =
                await _notionCalendarService.TryGetCachedDayAsync(
                    _calendarSelectedDate);

            if (cached != null)
            {
                _calendarActivities = cached;
                DrawCalendar(_calendarActivities);
            }
        }

        private void ApplyCalendarTheme(Color background)
        {
            _calendarThemeColor = background;

            if (CalendarHost != null)
            {
                CalendarHost.Background =
                    new SolidColorBrush(
                        Darken(background, 0.08));
            }

            if (CalendarCanvas != null)
            {
                CalendarCanvas.Background =
                    new SolidColorBrush(
                        Darken(background, 0.12));
            }

            if (_calendarViewActive)
                DrawCalendar(_calendarActivities);
        }

        private static Color Darken(Color color, double amount)
        {
            var factor = Math.Clamp(1 - amount, 0, 1);

            return Color.FromArgb(
                color.A,
                (byte)(color.R * factor),
                (byte)(color.G * factor),
                (byte)(color.B * factor));
        }

        private static Color Lighten(Color color, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);

            return Color.FromArgb(
                color.A,
                (byte)(color.R + (255 - color.R) * amount),
                (byte)(color.G + (255 - color.G) * amount),
                (byte)(color.B + (255 - color.B) * amount));
        }

        private static bool IsCalendarControlDown()
        {
            return IsCalendarKeyDown(VirtualKey.LeftControl) ||
                   IsCalendarKeyDown(VirtualKey.RightControl);
        }

        private static bool IsCalendarShiftDown()
        {
            return IsCalendarKeyDown(VirtualKey.LeftShift) ||
                   IsCalendarKeyDown(VirtualKey.RightShift);
        }

        private static bool IsCalendarKeyDown(VirtualKey key)
        {
            var state =
                InputKeyboardSource
                    .GetKeyStateForCurrentThread(key);

            return (state &
                    Windows.UI.Core.CoreVirtualKeyStates.Down) ==
                   Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        private static string NormalizeCalendarPerson(string value)
        {
            var clean = (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace(" ", string.Empty);

            var aliases = new (string Alias, string Person)[]
            {
                ("jjohn", "John"), ("john", "John"),
                ("kkarl", "Karla"), ("karla", "Karla"),
                ("iisai", "Isaias"), ("isaias", "Isaias"),
                ("ssote", "Sotelo"), ("sotelo", "Sotelo"),
                ("sote", "Sotelo"), ("eedua", "Sotelo"),
                ("eduardo", "Sotelo"), ("edua", "Sotelo"),
                ("aacal", "Acalli"), ("acalli", "Acalli"),
                ("acali", "Acalli"), ("acal", "Acalli"),
                ("aandr", "Andrade"), ("andrade", "Andrade"),
                ("eemma", "Emmanuel"), ("emmanuel", "Emmanuel"),
                ("bbria", "Brian"), ("brian", "Brian"),
                ("ggena", "Genaro"), ("genaro", "Genaro"),
                ("nneft", "Neftali"),
                ("neftali", "Neftali"), ("neft", "Neftali")
            };

            foreach (var (alias, person) in aliases)
            {
                if (clean.Contains(alias))
                    return person;
            }

            return "Sin asignar";
        }

        private async void CalendarActivity_DoubleTapped(
            object sender,
            DoubleTappedRoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not NotionCalendarActivity activity)
            {
                return;
            }

            var clickIsSuppressed =
                _calendarDragActive ||
                _calendarSuppressNextActivityClick ||
                DateTimeOffset.UtcNow <=
                    _calendarSuppressActivityClickUntil;

            if (clickIsSuppressed)
            {
                _calendarSuppressNextActivityClick = false;
                _calendarSuppressActivityClickUntil =
                    DateTimeOffset.MinValue;
                _calendarSuppressedActivityPageId =
                    string.Empty;
                e.Handled = true;
                return;
            }

            e.Handled = true;
            await OpenCalendarActivityAsync(activity);
        }

        private async Task EnsureCalendarReviewFlowLocalCacheLoadedAsync(
            CancellationToken cancellationToken = default)
        {
            if (_calendarReviewFlowLocalCacheLoaded)
                return;

            await _calendarReviewFlowLocalCacheLock.WaitAsync(
                cancellationToken);

            try
            {
                if (_calendarReviewFlowLocalCacheLoaded)
                    return;

                try
                {
                    var file =
                        await ApplicationData.Current.LocalFolder
                            .GetFileAsync(
                                CalendarReviewFlowCacheFileName);

                    var json =
                        await FileIO.ReadTextAsync(file);

                    var restored =
                        JsonSerializer.Deserialize<
                            Dictionary<string, ReviewFlowMetadata?>>(json);

                    if (restored != null)
                    {
                        foreach (var item in restored)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Key))
                            {
                                _calendarReviewFlowCache[
                                    item.Key] = item.Value;
                            }
                        }
                    }
                }
                catch
                {
                    // La primera ejecución todavía no tiene archivo local.
                }

                _calendarReviewFlowLocalCacheLoaded = true;
            }
            finally
            {
                _calendarReviewFlowLocalCacheLock.Release();
            }
        }

        private async Task PersistCalendarReviewFlowLocalCacheAsync(
            CancellationToken cancellationToken = default)
        {
            await _calendarReviewFlowLocalCacheLock.WaitAsync(
                cancellationToken);

            try
            {
                var snapshot =
                    _calendarReviewFlowCache
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(item.Key) &&
                            item.Value != null)
                        .ToDictionary(
                            item => item.Key,
                            item => item.Value,
                            StringComparer.OrdinalIgnoreCase);

                var json =
                    JsonSerializer.Serialize(
                        snapshot,
                        new JsonSerializerOptions
                        {
                            WriteIndented = false
                        });

                var file =
                    await ApplicationData.Current.LocalFolder
                        .CreateFileAsync(
                            CalendarReviewFlowCacheFileName,
                            CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteTextAsync(
                    file,
                    json)
                    .AsTask(cancellationToken);
            }
            finally
            {
                _calendarReviewFlowLocalCacheLock.Release();
            }
        }

        private void ApplyCachedCalendarReviewFlow(
            IReadOnlyList<NotionCalendarActivity> activities)
        {
            if (activities == null)
                return;

            foreach (var activity in activities)
            {
                if (activity == null ||
                    string.IsNullOrWhiteSpace(activity.PageId))
                {
                    continue;
                }

                if (_calendarReviewFlowCache.TryGetValue(
                        activity.PageId,
                        out var metadata) &&
                    metadata != null)
                {
                    ApplyReviewFlowMetadata(
                        activity,
                        metadata);
                }
            }
        }

        private static string GetActiveCalendarPersonFromTitle(
            string title)
        {
            var value =
                title ?? string.Empty;

            var activeTags =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["jjohn"] = "John",
                    ["kkarl"] = "Karla",
                    ["iisai"] = "Isaias",
                    ["ssote"] = "Sotelo",
                    ["eedua"] = "Sotelo",
                    ["aacal"] = "Acalli",
                    ["aandr"] = "Andrade",
                    ["eemma"] = "Emmanuel",
                    ["bbria"] = "Brian",
                    ["ggena"] = "Genaro",
                    ["nneft"] = "Neftali"
                };

            var matches =
                Regex.Matches(
                    value,
                    @"(?<![\p{L}\p{Nd}_])(?<tag>[a-z]{5})(?<suffix>\d*)(?![\p{L}\p{Nd}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            for (var index = matches.Count - 1;
                 index >= 0;
                 index--)
            {
                var tag =
                    matches[index]
                        .Groups["tag"]
                        .Value;

                if (activeTags.TryGetValue(
                        tag,
                        out var person))
                {
                    return person;
                }
            }

            return string.Empty;
        }

        private static bool ContainsAnyCalendarPersonTag(
            string title)
        {
            var value = title ?? string.Empty;

            var tags = new[]
            {
                "jjohn", "john",
                "kkarl", "karl",
                "iisai", "isai",
                "ssote", "sote", "eedua", "edua",
                "aacal", "acal",
                "aandr", "andr",
                "eemma", "emma",
                "bbria", "bria",
                "ggena", "gena",
                "nneft", "neft"
            };

            return tags.Any(tag =>
                Regex.IsMatch(
                    value,
                    $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(tag)}\d*(?![\p{{L}}\p{{Nd}}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant));
        }

        private IReadOnlyList<NotionCalendarActivity>
            ExpandCalendarReviewActivities(
                IReadOnlyList<NotionCalendarActivity> activities)
        {
            var result =
                new List<NotionCalendarActivity>();

            foreach (var activity in activities)
            {
                if (activity == null)
                    continue;

                var activePerson =
                    GetActiveCalendarPersonFromTitle(
                        activity.Title);

                var personFromAssignee =
                    NormalizeCalendarPerson(
                        activity.Person);

                var reviewFinished =
                    string.Equals(
                        activity.ReviewState,
                        "approved",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        activity.ReviewState,
                        "returned",
                        StringComparison.OrdinalIgnoreCase);

                var originalFromFlow =
                    NormalizeCalendarPerson(
                        activity.OriginalPerson);

                var resolvedPerson =
                    reviewFinished &&
                    !string.IsNullOrWhiteSpace(originalFromFlow) &&
                    !string.Equals(
                        originalFromFlow,
                        "Sin asignar",
                        StringComparison.OrdinalIgnoreCase)
                        ? originalFromFlow
                        : !string.IsNullOrWhiteSpace(personFromAssignee) &&
                          !string.Equals(
                              personFromAssignee,
                              "Sin asignar",
                              StringComparison.OrdinalIgnoreCase)
                            ? personFromAssignee
                            : activePerson;

                if (activity.IsPendingReview &&
                    HasCurrentReviewMirrorPhase(activity) &&
                    !string.IsNullOrWhiteSpace(
                        activity.OriginalPerson) &&
                    !string.IsNullOrWhiteSpace(
                        activity.ReviewAssignee))
                {
                    // La copia histórica existe únicamente mientras la fase
                    // actual siga siendo rtuzREVISION. Al aprobar o regresar,
                    // la actividad vuelve a mostrarse como una sola tarjeta.
                    var originalCard =
                        CloneCalendarActivity(activity);

                    originalCard.Person =
                        NormalizeCalendarPerson(
                            activity.OriginalPerson);

                    originalCard.IsReviewMirror = true;
                    result.Add(originalCard);

                    // En una revisión pendiente el título puede cambiar
                    // antes de que Notion recalcule Assignee. Por eso aquí sí
                    // se permite usar el tag activo como señal inmediata.
                    var reviewerPerson =
                        !string.IsNullOrWhiteSpace(activePerson)
                            ? activePerson
                            : resolvedPerson;

                    if (string.Equals(
                            reviewerPerson,
                            NormalizeCalendarPerson(
                                activity.ReviewAssignee),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var reviewerCard =
                            CloneCalendarActivity(activity);

                        reviewerCard.Person =
                            reviewerPerson;

                        reviewerCard.IsReviewMirror = false;
                        result.Add(reviewerCard);
                    }

                    continue;
                }

                // Para actividades normales o regresadas:
                // Assignee resuelto por el servicio primero; tag activo solo como respaldo.
                if (!string.IsNullOrWhiteSpace(resolvedPerson) &&
                    !string.Equals(
                        resolvedPerson,
                        "Sin asignar",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var assignedCard =
                        CloneCalendarActivity(activity);

                    assignedCard.Person =
                        resolvedPerson;

                    if (string.IsNullOrWhiteSpace(
                            assignedCard.OriginalPerson) ||
                        string.Equals(
                            NormalizeCalendarPerson(
                                assignedCard.OriginalPerson),
                            "Sin asignar",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        assignedCard.OriginalPerson =
                            resolvedPerson;
                    }

                    assignedCard.IsReviewMirror = false;
                    result.Add(assignedCard);
                    continue;
                }

                // No se elimina ninguna actividad silenciosamente.
                // Si el título contiene tags pero ninguno activo fue válido,
                // se conserva en Sin asignar para que el problema sea visible
                // y pueda corregirse, en vez de desaparecer del calendario.
                if (ContainsAnyCalendarPersonTag(activity.Title))
                {
                    var unresolved =
                        CloneCalendarActivity(activity);

                    unresolved.Person =
                        "Sin asignar";

                    unresolved.OriginalPerson =
                        string.Empty;

                    unresolved.IsReviewMirror = false;
                    result.Add(unresolved);
                    continue;
                }

                result.Add(activity);
            }

            return result;
        }

        private static NotionCalendarActivity CloneCalendarActivity(
            NotionCalendarActivity activity)
        {
            return new NotionCalendarActivity
            {
                PageId = activity.PageId,
                PageUrl = activity.PageUrl,
                Title = activity.Title,
                Person = activity.Person,
                OriginalPerson = activity.OriginalPerson,
                ReviewAssignee = activity.ReviewAssignee,
                ReviewState = activity.ReviewState,
                ReviewSubmittedAt = activity.ReviewSubmittedAt,
                ReviewUpdatedAt = activity.ReviewUpdatedAt,
                ReviewUpdatedBy = activity.ReviewUpdatedBy,
                ReviewNote = activity.ReviewNote,
                IsReviewMirror = activity.IsReviewMirror,
                IsCompletedForReview = activity.IsCompletedForReview,
                IsAutomationLocked = activity.IsAutomationLocked,
                ChecklistScanned = activity.ChecklistScanned,
                ChecklistTotal = activity.ChecklistTotal,
                ChecklistCompleted = activity.ChecklistCompleted,
                Project = activity.Project,
                Status = activity.Status,
                StatusColor = activity.StatusColor,
                UpdateText = activity.UpdateText,
                Description = activity.Description,
                EstimatedWorkMinutes = activity.EstimatedWorkMinutes,
                WorkedMinutes = activity.WorkedMinutes,
                WorkLogDetail = activity.WorkLogDetail,
                ActivityCreatedDate = activity.ActivityCreatedDate,
                InternalDeadlineDate = activity.InternalDeadlineDate,
                DatePropertyName = activity.DatePropertyName,
                Start = activity.Start,
                End = activity.End
            };
        }

        private async Task RepairCompletedReviewAssigneesAsync(
            string token,
            IReadOnlyList<NotionCalendarActivity> activities,
            CancellationToken cancellationToken)
        {
            foreach (var activity in activities)
            {
                if (activity == null ||
                    string.IsNullOrWhiteSpace(activity.PageId) ||
                    _calendarReviewAssigneeRepairAttempted.Contains(
                        activity.PageId))
                {
                    continue;
                }

                var isCompletedFlow =
                    string.Equals(
                        activity.ReviewState,
                        "approved",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        activity.ReviewState,
                        "returned",
                        StringComparison.OrdinalIgnoreCase);

                if (!isCompletedFlow)
                    continue;

                var reviewer =
                    NormalizeCalendarPerson(
                        activity.ReviewAssignee);

                var original =
                    NormalizeCalendarPerson(
                        activity.OriginalPerson);

                var currentAssignee =
                    NormalizeCalendarPerson(
                        activity.Person);

                if (!IsUsableOriginalReviewPerson(
                        original,
                        reviewer) ||
                    string.Equals(
                        currentAssignee,
                        original,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _calendarReviewAssigneeRepairAttempted.Add(
                    activity.PageId);

                try
                {
                    await _notionCalendarService
                        .UpdateActivityAssigneeAsync(
                            token,
                            activity.PageId,
                            original,
                            cancellationToken);

                    activity.Person =
                        original;
                }
                catch
                {
                    // La vista seguirá usando OriginalPerson para no dejar la
                    // tarjeta con el revisor. Se intentará de nuevo al reiniciar.
                }
            }
        }

        private async Task HydrateCalendarReviewFlowAsync(
            IReadOnlyList<NotionCalendarActivity> activities,
            CancellationToken cancellationToken = default,
            long processVersion = 0)
        {
            if (activities == null ||
                activities.Count == 0)
            {
                return;
            }

            await EnsureCalendarReviewFlowLocalCacheLoadedAsync(
                cancellationToken);

            await _calendarReviewFlowHydrationLock.WaitAsync(
                cancellationToken);

            _calendarReviewFlowHydrating = true;

            try
            {
                ApplyCachedCalendarReviewFlow(activities);

                var token =
                    ApplicationData.Current.LocalSettings.Values[
                        "Notion.Token"] as string;

                if (string.IsNullOrWhiteSpace(token))
                    return;

                await RepairCompletedReviewAssigneesAsync(
                    token,
                    activities,
                    cancellationToken);

                // Antes se consultaba metadata para todas las actividades del
                // día. Ahora solo se consulta un PageId desconocido cuando su
                // fase actual realmente participa en rtuz/zREVISION.
                var candidates = activities
                    .Where(activity =>
                        activity != null &&
                        !string.IsNullOrWhiteSpace(activity.PageId) &&
                        IsReviewEligibleActivity(activity) &&
                        (!_calendarReviewFlowCache.TryGetValue(
                             activity.PageId,
                             out var cachedMetadata) ||
                         cachedMetadata == null))
                    .GroupBy(
                        activity => activity.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                if (candidates.Count == 0)
                    return;

                var completed = 0;

                // Dos productores son suficientes; el coordinador global
                // conserva una sola petición activa y el ritmo seguro.
                using var gate =
                    new SemaphoreSlim(2, 2);

                var tasks = candidates.Select(async activity =>
                {
                    await gate.WaitAsync(cancellationToken);

                    try
                    {
                        _calendarLastKnownPeople.TryGetValue(
                            activity.PageId,
                            out var previousPeople);

                        if (string.IsNullOrWhiteSpace(previousPeople))
                        {
                            previousPeople =
                                GetPersistedCalendarPeople(
                                    activity.PageId);
                        }

                        var metadata =
                            await _calendarReviewFlowService
                                .GetReviewFlowAsync(
                                    token,
                                    activity.PageId,
                                    cancellationToken);

                        if (metadata == null)
                        {
                            metadata = TryInferManualReviewFlow(
                                activity,
                                previousPeople);

                            if (metadata != null)
                            {
                                await _calendarReviewFlowService
                                    .SaveReviewFlowAsync(
                                        token,
                                        activity.PageId,
                                        metadata,
                                        cancellationToken);
                            }
                        }

                        _calendarReviewFlowCache[
                            activity.PageId] = metadata;

                        ApplyReviewFlowMetadata(
                            activity,
                            metadata);

                        var knownPeople = metadata != null
                            ? string.Join(
                                ", ",
                                new[]
                                {
                                    metadata.OriginalPerson,
                                    metadata.ReviewAssignee
                                }.Where(value =>
                                    !string.IsNullOrWhiteSpace(value)))
                            : activity.Person;

                        _calendarLastKnownPeople[
                            activity.PageId] = knownPeople;

                        PersistCalendarPeople(
                            activity.PageId,
                            knownPeople);
                    }
                    catch
                    {
                        // Una página sin historial no bloquea el resto.
                    }
                    finally
                    {
                        gate.Release();

                        var current =
                            Interlocked.Increment(ref completed);

                        DispatcherQueue.TryEnqueue(() =>
                            UpdateCalendarReviewProgress(
                                processVersion,
                                current,
                                candidates.Count));
                    }
                });

                await Task.WhenAll(tasks);

                await PersistCalendarReviewFlowLocalCacheAsync(
                    cancellationToken);

                ApplyCachedCalendarReviewFlow(activities);
            }
            finally
            {
                _calendarReviewFlowHydrating = false;
                _calendarReviewFlowHydrationLock.Release();
            }
        }

        private static ReviewFlowMetadata?
            TryInferManualReviewFlow(
                NotionCalendarActivity activity,
                string? previousPeople)
        {
            var current = SplitPersons(activity.Person)
                .Select(NormalizeCalendarPerson)
                .Where(person =>
                    !string.IsNullOrWhiteSpace(person) &&
                    !string.Equals(
                        person,
                        "Sin asignar",
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var previous = SplitPersons(previousPeople ?? string.Empty)
                .Select(NormalizeCalendarPerson)
                .Where(person =>
                    !string.IsNullOrWhiteSpace(person) &&
                    !string.Equals(
                        person,
                        "Sin asignar",
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            static bool IsReviewer(string person) =>
                string.Equals(
                    person,
                    "Genaro",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    person,
                    "John",
                    StringComparison.OrdinalIgnoreCase);

            var reviewer = current.FirstOrDefault(IsReviewer) ??
                           previous.FirstOrDefault(IsReviewer) ??
                           string.Empty;

            var original = previous
                .FirstOrDefault(person => !IsReviewer(person)) ??
                current.FirstOrDefault(person => !IsReviewer(person)) ??
                string.Empty;

            if (string.IsNullOrWhiteSpace(original) ||
                string.IsNullOrWhiteSpace(reviewer) ||
                string.Equals(
                    original,
                    reviewer,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new ReviewFlowMetadata
            {
                OriginalPerson = original,
                ReviewAssignee = reviewer,
                State = "pending",
                SubmittedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
                UpdatedBy = "ANFETA",
                Note =
                    "Flujo reconstruido automáticamente después de un cambio manual de tags."
            };
        }

        private static bool IsUsableOriginalReviewPerson(
            string? person,
            string reviewer)
        {
            var normalizedPerson =
                NormalizeCalendarPerson(
                    person ?? string.Empty);

            var normalizedReviewer =
                NormalizeCalendarPerson(
                    reviewer ?? string.Empty);

            return !string.IsNullOrWhiteSpace(
                       normalizedPerson) &&
                   !string.Equals(
                       normalizedPerson,
                       "Sin asignar",
                       StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(
                       normalizedPerson,
                       normalizedReviewer,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveLastNonReviewerPersonFromTitle(
            string title,
            string reviewer)
        {
            var normalizedReviewer =
                NormalizeCalendarPerson(
                    reviewer ?? string.Empty);

            var matches =
                new List<(int Index, string Person)>();

            foreach (var pair in CalendarReviewPersonTags)
            {
                if (string.Equals(
                        pair.Key,
                        normalizedReviewer,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (Match match in Regex.Matches(
                             title ?? string.Empty,
                             $@"(?<![\p{{L}}\p{{Nd}}_])(?:{Regex.Escape(pair.Value.Active)}|{Regex.Escape(pair.Value.Passive)})\d*(?![\p{{L}}\p{{Nd}}_])",
                             RegexOptions.IgnoreCase |
                             RegexOptions.CultureInvariant))
                {
                    matches.Add(
                        (match.Index, pair.Key));
                }
            }

            return matches
                .OrderByDescending(item => item.Index)
                .Select(item => item.Person)
                .FirstOrDefault() ??
                string.Empty;
        }

        private static string ResolveOriginalReviewPerson(
            NotionCalendarActivity activity,
            string reviewer)
        {
            // La persona que pulsa "Enviar a revisión" es la fuente más
            // confiable antes de cambiar Assignee al revisor. Si intenta
            // enviársela a sí misma, se usa el responsable actual de la
            // actividad como respaldo.
            var candidates = new List<string>
            {
                GetCurrentCalendarUserName(),
                activity.Person,
                activity.OriginalPerson,
                ResolveLastNonReviewerPersonFromTitle(
                    activity.Title,
                    reviewer)
            };

            candidates.AddRange(
                SplitPersons(
                    GetPersistedCalendarPeople(
                        activity.PageId)));

            foreach (var candidate in candidates)
            {
                if (!IsUsableOriginalReviewPerson(
                        candidate,
                        reviewer))
                {
                    continue;
                }

                return NormalizeCalendarPerson(
                    candidate);
            }

            return NormalizeCalendarPerson(
                activity.Person);
        }

        private static string ResolveOriginalReviewPersonForCompletion(
            NotionCalendarActivity activity,
            string reviewer,
            ReviewFlowMetadata? previousMetadata)
        {
            // Mientras está en rtuzREVISION, BuildReviewTitleForState deja
            // al responsable original inmediatamente antes del revisor.
            // Esta señal corrige también metadata antigua que haya guardado
            // por error al propio revisor como OriginalPerson.
            var candidates = new List<string>
            {
                ResolveLastNonReviewerPersonFromTitle(
                    activity.Title,
                    reviewer),
                previousMetadata?.OriginalPerson ?? string.Empty,
                activity.OriginalPerson,
                activity.Person
            };

            candidates.AddRange(
                SplitPersons(
                    GetPersistedCalendarPeople(
                        activity.PageId)));

            foreach (var candidate in candidates)
            {
                if (!IsUsableOriginalReviewPerson(
                        candidate,
                        reviewer))
                {
                    continue;
                }

                return NormalizeCalendarPerson(
                    candidate);
            }

            return NormalizeCalendarPerson(
                activity.OriginalPerson);
        }

        private static void ApplyReviewFlowMetadata(
            NotionCalendarActivity activity,
            ReviewFlowMetadata? metadata)
        {
            if (activity == null || metadata == null)
                return;

            var normalizedReviewer =
                NormalizeCalendarPerson(
                    metadata.ReviewAssignee);

            var normalizedOriginal =
                NormalizeCalendarPerson(
                    metadata.OriginalPerson);

            // El título pendiente conserva al responsable real justo
            // antes del revisor. Se prioriza esa señal para reparar metadata
            // antigua, incluso si OriginalPerson quedó con otra persona válida.
            var originalFromTitle =
                ResolveLastNonReviewerPersonFromTitle(
                    activity.Title,
                    normalizedReviewer);

            if (IsUsableOriginalReviewPerson(
                    originalFromTitle,
                    normalizedReviewer))
            {
                normalizedOriginal =
                    originalFromTitle;
            }
            else if (!IsUsableOriginalReviewPerson(
                         normalizedOriginal,
                         normalizedReviewer))
            {
                normalizedOriginal =
                    string.Empty;
            }

            if (IsUsableOriginalReviewPerson(
                    normalizedOriginal,
                    normalizedReviewer))
            {
                activity.OriginalPerson =
                    normalizedOriginal;
            }
            else if (string.IsNullOrWhiteSpace(
                         activity.OriginalPerson))
            {
                activity.OriginalPerson =
                    activity.Person;
            }

            activity.ReviewAssignee =
                normalizedReviewer;

            activity.ReviewState =
                metadata.State ?? string.Empty;

            activity.ReviewSubmittedAt =
                metadata.SubmittedAt == default
                    ? null
                    : metadata.SubmittedAt;

            activity.ReviewUpdatedAt =
                metadata.UpdatedAt == default
                    ? null
                    : metadata.UpdatedAt;

            activity.ReviewUpdatedBy =
                metadata.UpdatedBy ?? string.Empty;

            activity.ReviewNote =
                metadata.Note ?? string.Empty;
        }

        private async void CalendarContextSendToReview_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity = GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            await PromptAndSendCalendarActivityToReviewAsync(
                activity);
        }

        private async Task<bool>
            PromptAndSendCalendarActivityToReviewAsync(
                NotionCalendarActivity activity)
        {
            if (activity == null)
                return false;

            if (!CanSendCalendarActivityToReview(activity))
            {
                StatusText.Text =
                    "Estado: Enviar a revisión aplica a prtuzREVISION, zREVISION o una revisión regresada.";
                return false;
            }

            var combo = new ComboBox
            {
                Header = "Enviar para revisión a",
                MinWidth = 320
            };

            combo.Items.Add(new ComboBoxItem
            {
                Content = "Genaro",
                Tag = "Genaro"
            });

            combo.Items.Add(new ComboBoxItem
            {
                Content = "John",
                Tag = "John"
            });

            combo.SelectedIndex = 0;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Enviar actividad a revisión",
                Content = combo,
                PrimaryButtonText = "Enviar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return false;
            }

            var reviewer =
                (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                "Genaro";

            var original =
                ResolveOriginalReviewPerson(
                    activity,
                    reviewer);

            _calendarReviewFlowCache.TryGetValue(
                activity.PageId,
                out var existingReviewMetadata);

            var pendingMetadata =
                new ReviewFlowMetadata
                {
                    OriginalPerson = original,
                    ReviewAssignee = reviewer,
                    State = "pending",
                    SubmittedAt = activity.ReviewSubmittedAt ??
                        existingReviewMetadata?.SubmittedAt ??
                        DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now,
                    UpdatedBy = GetCurrentCalendarUserName(),
                    Note = "Enviada a revisión desde ANFETA.",
                    AlertPageId =
                        existingReviewMetadata?.AlertPageId ??
                        string.Empty,
                    AlertPageUrl =
                        existingReviewMetadata?.AlertPageUrl ??
                        string.Empty
                };

            await SaveCalendarReviewFlowAsync(
                activity,
                pendingMetadata,
                "Actividad enviada a revisión");

            // Regla definitiva: el revisor seleccionado se conserva en
            // ReviewAssignee y en el flujo de revisión, pero la alarma de
            // entrada a RTUZ siempre se envía a John.
            const string reviewAlertDeliveryPerson = "John";

            var alert =
                await SendCalendarReviewAlertAsync(
                    activity,
                    reviewAlertDeliveryPerson,
                    "Actividad lista para revisión");

            if (alert != null &&
                !string.IsNullOrWhiteSpace(alert.PageId))
            {
                var linkedMetadata =
                    new ReviewFlowMetadata
                    {
                        OriginalPerson = pendingMetadata.OriginalPerson,
                        ReviewAssignee = pendingMetadata.ReviewAssignee,
                        State = pendingMetadata.State,
                        SubmittedAt = pendingMetadata.SubmittedAt,
                        UpdatedAt = DateTimeOffset.Now,
                        UpdatedBy = pendingMetadata.UpdatedBy,
                        Note = pendingMetadata.Note,
                        AlertPageId = alert.PageId,
                        AlertPageUrl = alert.PageUrl
                    };

                var token =
                    ApplicationData.Current.LocalSettings.Values[
                        "Notion.Token"] as string;

                if (!string.IsNullOrWhiteSpace(token))
                {
                    using var cts =
                        new CancellationTokenSource(
                            TimeSpan.FromMinutes(2));

                    await _calendarReviewFlowService.SaveReviewFlowAsync(
                        token,
                        activity.PageId,
                        linkedMetadata,
                        cts.Token);

                    _calendarReviewFlowCache[
                        activity.PageId] = linkedMetadata;

                    await PersistCalendarReviewFlowLocalCacheAsync(
                        cts.Token);

                    foreach (var item in _calendarActivities.Where(item =>
                                 string.Equals(
                                     item.PageId,
                                     activity.PageId,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        ApplyReviewFlowMetadata(
                            item,
                            linkedMetadata);
                    }
                }
            }

            return true;
        }

        private async void CalendarContextApproveReview_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity = GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            if (!CanCurrentUserResolveReview(activity))
            {
                StatusText.Text =
                    $"Estado: Solo {activity.ReviewAssignee} puede aprobar esta revisión.";
                return;
            }

            _calendarReviewFlowCache.TryGetValue(
                activity.PageId,
                out var previousMetadata);

            var resolvedReviewer =
                NormalizeCalendarPerson(
                    !string.IsNullOrWhiteSpace(
                        previousMetadata?.ReviewAssignee)
                        ? previousMetadata.ReviewAssignee
                        : activity.ReviewAssignee);

            var resolvedOriginal =
                ResolveOriginalReviewPersonForCompletion(
                    activity,
                    resolvedReviewer,
                    previousMetadata);

            var approvedMetadata =
                new ReviewFlowMetadata
                {
                    OriginalPerson = resolvedOriginal,
                    ReviewAssignee = resolvedReviewer,
                    State = "approved",
                    SubmittedAt = activity.ReviewSubmittedAt ??
                        previousMetadata?.SubmittedAt ??
                        DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now,
                    UpdatedBy = GetCurrentCalendarUserName(),
                    Note = "Revisión aprobada desde ANFETA.",
                    AlertPageId =
                        previousMetadata?.AlertPageId ??
                        string.Empty,
                    AlertPageUrl =
                        previousMetadata?.AlertPageUrl ??
                        string.Empty
                };

            await SaveCalendarReviewFlowAsync(
                activity,
                approvedMetadata,
                "Actividad aprobada");

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var token =
                    ApplicationData.Current.LocalSettings.Values[
                        "Notion.Token"] as string;

                var threadIsActive =
                    !string.IsNullOrWhiteSpace(
                        approvedMetadata.AlertPageId) &&
                    !string.IsNullOrWhiteSpace(token) &&
                    await _calendarReviewFlowService
                        .IsPageActiveAsync(
                            token,
                            approvedMetadata.AlertPageId,
                            cts.Token);

                if (threadIsActive)
                {
                    await AppendReviewHistoryEntryAsync(
                        approvedMetadata,
                        approvedMetadata.OriginalPerson,
                        "Revisión aprobada.",
                        retargetNotification: true,
                        cts.Token);
                }
            }
            catch
            {
            }
        }

        private async void CalendarContextReturnReview_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity = GetCalendarActivityFromMenuSender(sender);

            if (activity == null)
                return;

            if (!CanCurrentUserResolveReview(activity))
            {
                StatusText.Text =
                    $"Estado: Solo {activity.ReviewAssignee} puede regresar esta revisión.";
                return;
            }

            var noteBox = new TextBox
            {
                Header = "Correcciones solicitadas",
                PlaceholderText = "Describe brevemente qué debe corregirse…",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 380,
                MinHeight = 90
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Regresar actividad",
                Content = noteBox,
                PrimaryButtonText = "Regresar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            var correctionText =
                string.IsNullOrWhiteSpace(noteBox.Text)
                    ? "Regresada con correcciones desde ANFETA."
                    : noteBox.Text.Trim();

            _calendarReviewFlowCache.TryGetValue(
                activity.PageId,
                out var previousMetadata);

            var resolvedReviewer =
                NormalizeCalendarPerson(
                    !string.IsNullOrWhiteSpace(
                        previousMetadata?.ReviewAssignee)
                        ? previousMetadata.ReviewAssignee
                        : activity.ReviewAssignee);

            var resolvedOriginal =
                ResolveOriginalReviewPersonForCompletion(
                    activity,
                    resolvedReviewer,
                    previousMetadata);

            var returnedMetadata =
                new ReviewFlowMetadata
                {
                    OriginalPerson = resolvedOriginal,
                    ReviewAssignee = resolvedReviewer,
                    State = "returned",
                    SubmittedAt = activity.ReviewSubmittedAt ??
                        previousMetadata?.SubmittedAt ??
                        DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now,
                    UpdatedBy = GetCurrentCalendarUserName(),
                    Note = correctionText,
                    AlertPageId =
                        previousMetadata?.AlertPageId ??
                        string.Empty,
                    AlertPageUrl =
                        previousMetadata?.AlertPageUrl ??
                        string.Empty
                };

            await SaveCalendarReviewFlowAsync(
                activity,
                returnedMetadata,
                "Actividad regresada con correcciones");

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var token =
                    ApplicationData.Current.LocalSettings.Values[
                        "Notion.Token"] as string;

                var threadIsActive =
                    !string.IsNullOrWhiteSpace(
                        returnedMetadata.AlertPageId) &&
                    !string.IsNullOrWhiteSpace(token) &&
                    await _calendarReviewFlowService
                        .IsPageActiveAsync(
                            token,
                            returnedMetadata.AlertPageId,
                            cts.Token);

                if (threadIsActive)
                {
                    await AppendReviewHistoryEntryAsync(
                        returnedMetadata,
                        returnedMetadata.OriginalPerson,
                        $"Correcciones solicitadas: {correctionText}",
                        retargetNotification: true,
                        cts.Token);

                    RequestOpenConversation(
                        returnedMetadata.AlertPageId);
                }
                else
                {
                    var newAlert =
                        await SendCalendarReviewAlertAsync(
                            activity,
                            returnedMetadata.OriginalPerson,
                            $"Correcciones solicitadas: {correctionText}");

                    if (newAlert != null &&
                        !string.IsNullOrWhiteSpace(
                            newAlert.PageId))
                    {
                        var relinkedMetadata =
                            new ReviewFlowMetadata
                            {
                                OriginalPerson =
                                    returnedMetadata.OriginalPerson,
                                ReviewAssignee =
                                    returnedMetadata.ReviewAssignee,
                                State =
                                    returnedMetadata.State,
                                SubmittedAt =
                                    returnedMetadata.SubmittedAt,
                                UpdatedAt =
                                    DateTimeOffset.Now,
                                UpdatedBy =
                                    returnedMetadata.UpdatedBy,
                                Note =
                                    returnedMetadata.Note,
                                AlertPageId =
                                    newAlert.PageId,
                                AlertPageUrl =
                                    newAlert.PageUrl
                            };

                        await _calendarReviewFlowService
                            .SaveReviewFlowAsync(
                                token!,
                                activity.PageId,
                                relinkedMetadata,
                                cts.Token);

                        _calendarReviewFlowCache[
                            activity.PageId] =
                            relinkedMetadata;

                        await PersistCalendarReviewFlowLocalCacheAsync(
                            cts.Token);

                        RequestOpenConversation(
                            newAlert.PageId);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: La actividad regresó, pero no se pudo crear o actualizar la notificación → {ex.Message}";
            }
        }


        private async void CalendarContextReassignApproved_Click(
            object sender,
            RoutedEventArgs e)
        {
            var activity =
                GetCalendarActivityFromMenuSender(sender);

            if (activity == null ||
                activity.IsReviewMirror)
            {
                return;
            }

            if (!HasExactCalendarPhase(
                    activity,
                    "zREVISION"))
            {
                StatusText.Text =
                    "Estado: Esta opción solo está disponible en zREVISION.";
                return;
            }

            var combo = new ComboBox
            {
                Header = "Nuevo responsable",
                MinWidth = 340,
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            foreach (var person in
                     CalendarReviewPersonTags.Keys
                         .OrderBy(value => value))
            {
                combo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = person,
                        Tag = person
                    });
            }

            var currentPerson =
                NormalizeCalendarPerson(activity.Person);

            combo.SelectedItem = combo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                    string.Equals(
                        item.Tag?.ToString(),
                        currentPerson,
                        StringComparison.OrdinalIgnoreCase));

            if (combo.SelectedIndex < 0)
                combo.SelectedIndex = 0;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title =
                    "Reasignar y pasar a prtuzREVISION",
                Content = combo,
                PrimaryButtonText = "Reasignar",
                CloseButtonText = "Cancelar",
                DefaultButton =
                    ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() !=
                    ContentDialogResult.Primary ||
                combo.SelectedItem is not ComboBoxItem selected)
            {
                return;
            }

            var newPerson =
                NormalizeCalendarPerson(
                    selected.Tag?.ToString() ??
                    string.Empty);

            await ReassignApprovedCalendarActivityAsync(
                activity,
                newPerson);
        }

        private static string BuildCalendarReassignedTitle(
            string currentTitle,
            string newPerson)
        {
            var normalizedPerson =
                NormalizeCalendarPerson(newPerson);

            if (!CalendarReviewPersonTags.TryGetValue(
                    normalizedPerson,
                    out var selectedTags))
            {
                throw new InvalidOperationException(
                    $"No se encontró la configuración de tags para {newPerson}.");
            }

            var result = ReplaceCalendarReviewPhase(
                currentTitle,
                "prtuzREVISION");

            var selectedSuffix =
                ReadCalendarPersonTagSuffix(
                    result,
                    selectedTags.Active,
                    selectedTags.Passive);

            result = RemoveCalendarPersonTags(
                result,
                selectedTags.Active,
                selectedTags.Passive);

            foreach (var pair in CalendarReviewPersonTags)
            {
                if (string.Equals(
                        pair.Key,
                        normalizedPerson,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result = Regex.Replace(
                    result,
                    $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(pair.Value.Active)}(?<suffix>\d*)(?![\p{{L}}\p{{Nd}}_])",
                    match =>
                        pair.Value.Passive +
                        match.Groups["suffix"].Value,
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);
            }

            result = Regex.Replace(
                    result,
                    @"\s{2,}",
                    " ")
                .Trim();

            return string.Join(
                " ",
                new[]
                {
                    result,
                    selectedTags.Active + selectedSuffix
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
        }

        private async Task ReassignApprovedCalendarActivityAsync(
            NotionCalendarActivity activity,
            string newPerson)
        {
            if (string.IsNullOrWhiteSpace(newPerson) ||
                string.Equals(
                    newPerson,
                    "Sin asignar",
                    StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text =
                    "Estado: Selecciona un responsable válido.";
                return;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            try
            {
                ShowLoadingState(
                    "Estado: Reasignando actividad…",
                    activity.Title);

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var updatedTitle =
                    BuildCalendarReassignedTitle(
                        activity.Title,
                        newPerson);

                await _notionCalendarService
                    .UpdateActivityTitleAsync(
                        token,
                        activity.PageId,
                        updatedTitle,
                        cts.Token);

                try
                {
                    await _notionCalendarService
                        .UpdateActivityAssigneeAsync(
                            token,
                            activity.PageId,
                            newPerson,
                            cts.Token);
                }
                catch
                {
                    // El título ya quedó actualizado. La propiedad puede ser
                    // de solo lectura y recalcularse unos segundos después.
                }

                _calendarReviewFlowCache.TryGetValue(
                    activity.PageId,
                    out var previousMetadata);

                var metadata = new ReviewFlowMetadata
                {
                    OriginalPerson = newPerson,
                    ReviewAssignee =
                        activity.ReviewAssignee,
                    State = "reassigned",
                    SubmittedAt =
                        activity.ReviewSubmittedAt ??
                        previousMetadata?.SubmittedAt ??
                        DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now,
                    UpdatedBy =
                        GetCurrentCalendarUserName(),
                    Note =
                        $"Actividad reasignada a {newPerson} y enviada a prtuzREVISION desde ANFETA.",
                    AlertPageId =
                        previousMetadata?.AlertPageId ??
                        string.Empty,
                    AlertPageUrl =
                        previousMetadata?.AlertPageUrl ??
                        string.Empty
                };

                await _calendarReviewFlowService
                    .SaveReviewFlowAsync(
                        token,
                        activity.PageId,
                        metadata,
                        cts.Token);

                _calendarReviewFlowCache[
                    activity.PageId] = metadata;

                await PersistCalendarReviewFlowLocalCacheAsync(
                    cts.Token);

                foreach (var item in _calendarActivities.Where(item =>
                             string.Equals(
                                 item.PageId,
                                 activity.PageId,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    item.Title = updatedTitle;
                    item.Person = newPerson;
                    item.OriginalPerson = newPerson;
                    item.IsReviewMirror = false;
                    ApplyReviewFlowMetadata(
                        item,
                        metadata);
                }

                activity.Title = updatedTitle;
                activity.Person = newPerson;
                activity.OriginalPerson = newPerson;
                activity.IsReviewMirror = false;
                ApplyReviewFlowMetadata(
                    activity,
                    metadata);

                var assignmentNotification =
                    await SendCalendarReviewAlertAsync(
                        activity,
                        newPerson,
                        "Actividad reasignada para continuar",
                        requireReviewPhase: false);

                if (assignmentNotification != null &&
                    !string.IsNullOrWhiteSpace(
                        assignmentNotification.PageId))
                {
                    metadata = new ReviewFlowMetadata
                    {
                        OriginalPerson = metadata.OriginalPerson,
                        ReviewAssignee = metadata.ReviewAssignee,
                        State = metadata.State,
                        SubmittedAt = metadata.SubmittedAt,
                        UpdatedAt = DateTimeOffset.Now,
                        UpdatedBy = metadata.UpdatedBy,
                        Note = metadata.Note,
                        AlertPageId =
                            assignmentNotification.PageId,
                        AlertPageUrl =
                            assignmentNotification.PageUrl
                    };

                    await _calendarReviewFlowService
                        .SaveReviewFlowAsync(
                            token,
                            activity.PageId,
                            metadata,
                            cts.Token);

                    _calendarReviewFlowCache[
                        activity.PageId] = metadata;

                    await PersistCalendarReviewFlowLocalCacheAsync(
                        cts.Token);

                    foreach (var item in _calendarActivities.Where(item =>
                                 string.Equals(
                                     item.PageId,
                                     activity.PageId,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        ApplyReviewFlowMetadata(
                            item,
                            metadata);
                    }

                    ApplyReviewFlowMetadata(
                        activity,
                        metadata);
                }

                DrawCalendar(_calendarActivities);

                StatusText.Text =
                    assignmentNotification != null
                        ? $"Estado: Actividad reasignada a {newPerson}, cambiada a prtuzREVISION y recordatorio enviado ✅"
                        : $"Estado: Actividad reasignada a {newPerson} y cambiada a prtuzREVISION; no se pudo crear el recordatorio.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo reasignar la actividad → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }


        private enum ReviewTitleTagState
        {
            Pending,
            Returned,
            Approved
        }

        private static string ReplaceCalendarReviewPhase(
            string title,
            string targetPhase)
        {
            var value = title ?? string.Empty;

            var pattern =
                @"(?<![\p{L}\p{Nd}_])(?:sprtuzREVISION|prtuzREVISION|rtuzREVISION|zREVISION)(?![\p{L}\p{Nd}_])";

            var replaced = Regex.Replace(
                value,
                pattern,
                targetPhase,
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);

            if (string.Equals(
                    replaced,
                    value,
                    StringComparison.Ordinal))
            {
                replaced = $"{targetPhase} {value}";
            }

            return Regex.Replace(
                    replaced,
                    @"\s{2,}",
                    " ")
                .Trim();
        }

        private static readonly Dictionary<string, (string Active, string Passive)>
            CalendarReviewPersonTags =
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["John"] = ("jjohn", "john"),
                    ["Karla"] = ("kkarl", "karl"),
                    ["Isaias"] = ("iisai", "isai"),
                    ["Sotelo"] = ("ssote", "sote"),
                    ["Acalli"] = ("aacal", "acal"),
                    ["Andrade"] = ("aandr", "andr"),
                    ["Emmanuel"] = ("eemma", "emma"),
                    ["Brian"] = ("bbria", "bria"),
                    ["Genaro"] = ("ggena", "gena"),
                    ["Neftali"] = ("nneft", "neft")
                };

        private static string BuildReviewTitleForState(
            string currentTitle,
            string originalPerson,
            string reviewer,
            ReviewTitleTagState state)
        {
            if (!CalendarReviewPersonTags.TryGetValue(
                    NormalizeCalendarPerson(originalPerson),
                    out var originalTags))
            {
                throw new InvalidOperationException(
                    $"No se encontró la configuración de tags para {originalPerson}.");
            }

            if (!CalendarReviewPersonTags.TryGetValue(
                    NormalizeCalendarPerson(reviewer),
                    out var reviewerTags))
            {
                throw new InvalidOperationException(
                    $"No se encontró la configuración de tags para {reviewer}.");
            }

            // En revisión pendiente y al aprobar, el responsable original
            // conserva el tag pasivo. Solo al regresar con correcciones
            // recupera el tag activo para indicar que vuelve a trabajarla.
            var originalTarget =
                state == ReviewTitleTagState.Returned
                    ? originalTags.Active
                    : originalTags.Passive;

            var reviewerTarget =
                state == ReviewTitleTagState.Pending
                    ? reviewerTags.Active
                    : reviewerTags.Passive;

            var originalSuffix =
                ReadCalendarPersonTagSuffix(
                    currentTitle,
                    originalTags.Active,
                    originalTags.Passive);

            var reviewerSuffix =
                ReadCalendarPersonTagSuffix(
                    currentTitle,
                    reviewerTags.Active,
                    reviewerTags.Passive);

            var result =
                RemoveCalendarPersonTags(
                    currentTitle,
                    originalTags.Active,
                    originalTags.Passive);

            result =
                RemoveCalendarPersonTags(
                    result,
                    reviewerTags.Active,
                    reviewerTags.Passive);

            var targetPhase =
                state == ReviewTitleTagState.Pending
                    ? "rtuzREVISION"
                    : state == ReviewTitleTagState.Approved
                        ? "zREVISION"
                        : "prtuzREVISION";

            result = ReplaceCalendarReviewPhase(
                result,
                targetPhase);

            return string.Join(
                " ",
                new[]
                {
                    result,
                    originalTarget + originalSuffix,
                    reviewerTarget + reviewerSuffix
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
        }

        private static string ReadCalendarPersonTagSuffix(
            string title,
            string activeTag,
            string passiveTag)
        {
            var match =
                Regex.Match(
                    title ?? string.Empty,
                    $@"(?<![\p{{L}}\p{{Nd}}_])(?:{Regex.Escape(activeTag)}|{Regex.Escape(passiveTag)})(?<suffix>\d*)(?![\p{{L}}\p{{Nd}}_])",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            return match.Success
                ? match.Groups["suffix"].Value
                : string.Empty;
        }

        private static string RemoveCalendarPersonTags(
            string title,
            string activeTag,
            string passiveTag)
        {
            return Regex.Replace(
                title ?? string.Empty,
                $@"(?<![\p{{L}}\p{{Nd}}_])(?:{Regex.Escape(activeTag)}|{Regex.Escape(passiveTag)})\d*(?![\p{{L}}\p{{Nd}}_])",
                " ",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private async Task UpdateCalendarReviewTitleAsync(
            NotionCalendarActivity activity,
            ReviewFlowMetadata metadata,
            ReviewTitleTagState state,
            string token,
            CancellationToken cancellationToken)
        {
            var currentTitle =
                activity.Title ?? string.Empty;

            var updatedTitle =
                BuildReviewTitleForState(
                    currentTitle,
                    metadata.OriginalPerson,
                    metadata.ReviewAssignee,
                    state);

            var titleChanged =
                !string.Equals(
                    currentTitle,
                    updatedTitle,
                    StringComparison.Ordinal);

            var targetPerson =
                state == ReviewTitleTagState.Pending
                    ? NormalizeCalendarPerson(
                        metadata.ReviewAssignee)
                    : NormalizeCalendarPerson(
                        metadata.OriginalPerson);

            if (string.IsNullOrWhiteSpace(targetPerson) ||
                string.Equals(
                    targetPerson,
                    "Sin asignar",
                    StringComparison.OrdinalIgnoreCase))
            {
                targetPerson =
                    GetActiveCalendarPersonFromTitle(
                        updatedTitle);
            }

            if (titleChanged)
            {
                await _notionCalendarService.UpdateActivityTitleAsync(
                    token,
                    activity.PageId,
                    updatedTitle,
                    cancellationToken);
            }

            try
            {
                await _notionCalendarService.UpdateActivityAssigneeAsync(
                    token,
                    activity.PageId,
                    targetPerson,
                    cancellationToken);
            }
            catch
            {
                // Si Assignee es fórmula o Notion todavía no permite
                // modificarlo, el título y la caché local conservan el
                // responsable correcto mientras Notion recalcula.
            }

            foreach (var item in _calendarActivities.Where(item =>
                         string.Equals(
                             item.PageId,
                             activity.PageId,
                             StringComparison.OrdinalIgnoreCase)))
            {
                item.Title = updatedTitle;
                item.Person = targetPerson;
                item.IsReviewMirror = false;
            }

            activity.Title = updatedTitle;
            activity.Person = targetPerson;
            activity.IsReviewMirror = false;
        }

        private async Task SaveCalendarReviewFlowAsync(
            NotionCalendarActivity activity,
            ReviewFlowMetadata metadata,
            string successText)
        {
            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            try
            {
                ShowLoadingState(
                    "Estado: Guardando flujo de revisión…",
                    activity.Title);

                using var cts = new CancellationTokenSource(
                    TimeSpan.FromMinutes(2));

                var titleState =
                    string.Equals(
                        metadata.State,
                        "returned",
                        StringComparison.OrdinalIgnoreCase)
                        ? ReviewTitleTagState.Returned
                        : string.Equals(
                            metadata.State,
                            "approved",
                            StringComparison.OrdinalIgnoreCase)
                            ? ReviewTitleTagState.Approved
                            : ReviewTitleTagState.Pending;

                await UpdateCalendarReviewTitleAsync(
                    activity,
                    metadata,
                    titleState,
                    token,
                    cts.Token);

                if (!string.IsNullOrWhiteSpace(activity.PageId))
                {
                    _calendarObservedRtuzState[activity.PageId] =
                        HasExactCalendarPhase(
                            activity,
                            "rtuzREVISION");
                }

                await _calendarReviewFlowService.SaveReviewFlowAsync(
                    token,
                    activity.PageId,
                    metadata,
                    cts.Token);

                _calendarReviewFlowCache[activity.PageId] = metadata;

                await PersistCalendarReviewFlowLocalCacheAsync(
                    cts.Token);

                foreach (var item in _calendarActivities.Where(item =>
                             string.Equals(
                                 item.PageId,
                                 activity.PageId,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    ApplyReviewFlowMetadata(item, metadata);
                }

                DrawCalendar(_calendarActivities);

                StatusText.Text =
                    $"Estado: {successText} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo guardar el flujo → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private static bool CanCurrentUserResolveReview(
            NotionCalendarActivity activity)
        {
            if (activity == null ||
                string.IsNullOrWhiteSpace(activity.ReviewAssignee))
            {
                return false;
            }

            return string.Equals(
                GetCurrentCalendarUserName(),
                NormalizeCalendarPerson(activity.ReviewAssignee),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string CalendarPeopleSettingsKey(string pageId)
        {
            return $"Calendar.ReviewPeople.{pageId}";
        }

        private static string GetPersistedCalendarPeople(string pageId)
        {
            if (string.IsNullOrWhiteSpace(pageId))
                return string.Empty;

            return ApplicationData.Current.LocalSettings.Values[
                       CalendarPeopleSettingsKey(pageId)] as string ??
                   string.Empty;
        }

        private static void PersistCalendarPeople(
            string pageId,
            string people)
        {
            if (string.IsNullOrWhiteSpace(pageId) ||
                string.IsNullOrWhiteSpace(people))
            {
                return;
            }

            ApplicationData.Current.LocalSettings.Values[
                CalendarPeopleSettingsKey(pageId)] = people;
        }

        private static string GetCurrentCalendarUserName()
        {
            var tag =
                ApplicationData.Current.LocalSettings.Values[
                    "Messaging.CurrentUserTag"] as string ??
                string.Empty;

            return NormalizeCalendarPerson(tag);
        }

        private void DrawCurrentTimeLine(double headerHeight)
        {
            if (_calendarSelectedDate.Date != DateTime.Today)
                return;

            var now = DateTime.Now;

            if (now.Hour < CalendarStartHour ||
                now.Hour >= CalendarEndHour)
            {
                return;
            }

            var minutes =
                (now -
                 DateTime.Today.AddHours(CalendarStartHour))
                .TotalMinutes;

            var top =
                headerHeight +
                minutes / 60d *
                CalendarHourHeight;

            AddHorizontalLine(
                0,
                top,
                CalendarCanvas.Width,
                Color.FromArgb(255, 255, 74, 74),
                thickness: 2);
        }

        private Border AddCalendarRectangle(
            double left,
            double top,
            double width,
            double height,
            Color fill,
            Color border)
        {
            var rectangle = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(fill),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, top);
            CalendarCanvas.Children.Add(rectangle);
            return rectangle;
        }

        private TextBlock AddCalendarText(
            string text,
            double left,
            double top,
            double width,
            double height,
            double fontSize,
            bool bold)
        {
            var block = new TextBlock
            {
                Text = text,
                Width = width,
                Height = height,
                FontSize = fontSize,
                FontWeight = bold
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Tag = new CalendarStickyPosition(left, top)
            };

            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, top);
            CalendarCanvas.Children.Add(block);
            return block;
        }

        private void AddHorizontalLine(
            double left,
            double top,
            double width,
            Color color,
            double thickness = 1)
        {
            var line = new Border
            {
                Width = width,
                Height = thickness,
                Background = new SolidColorBrush(color)
            };

            Canvas.SetLeft(line, left);
            Canvas.SetTop(line, top);
            CalendarCanvas.Children.Add(line);
        }

        private void AddVerticalLine(
            double left,
            double top,
            double height,
            Color color)
        {
            var line = new Border
            {
                Width = 1,
                Height = height,
                Background = new SolidColorBrush(color)
            };

            Canvas.SetLeft(line, left);
            Canvas.SetTop(line, top);
            CalendarCanvas.Children.Add(line);
        }

        private static Brush GetNeutralCalendarActivityBrush(
            string status,
            string notionColor)
        {
            // Las actividades normales usan una base gris/neutra. El estado
            // solo modifica ligeramente el matiz; los colores intensos quedan
            // reservados para la franja de prioridad, revisiones y recordatorios.
            var normalizedStatus =
                NormalizeCalendarSearchText(status);

            Color background;

            if (IsCalendarCompletedStatus(normalizedStatus))
            {
                background =
                    Color.FromArgb(
                        238,
                        47,
                        61,
                        56);
            }
            else if (IsCalendarInProgressStatus(normalizedStatus))
            {
                background =
                    Color.FromArgb(
                        238,
                        62,
                        61,
                        55);
            }
            else if (IsCalendarPendingStatus(normalizedStatus))
            {
                background =
                    Color.FromArgb(
                        238,
                        61,
                        55,
                        58);
            }
            else
            {
                var notion =
                    (notionColor ?? string.Empty)
                        .Trim()
                        .ToLowerInvariant();

                background = notion switch
                {
                    "green" =>
                        Color.FromArgb(238, 47, 60, 56),
                    "yellow" or "orange" =>
                        Color.FromArgb(238, 61, 59, 53),
                    "red" or "pink" =>
                        Color.FromArgb(238, 61, 54, 58),
                    "blue" or "purple" =>
                        Color.FromArgb(238, 52, 57, 67),
                    _ =>
                        Color.FromArgb(238, 51, 55, 62)
                };
            }

            return new SolidColorBrush(background);
        }

        private static Brush GetActivityBrush(
            string status,
            string notionColor)
        {
            // Semáforo solicitado para el calendario:
            // Pendiente = rojo, En curso = amarillo, Completado = verde.
            // Se evalúa primero el nombre del estado para no depender del
            // color individual configurado en Notion.
            var normalizedStatus =
                NormalizeCalendarSearchText(status);

            if (IsCalendarPendingStatus(normalizedStatus))
            {
                return new SolidColorBrush(
                    Color.FromArgb(232, 145, 56, 56));
            }

            if (IsCalendarInProgressStatus(normalizedStatus))
            {
                return new SolidColorBrush(
                    Color.FromArgb(232, 154, 123, 35));
            }

            if (IsCalendarCompletedStatus(normalizedStatus))
            {
                return new SolidColorBrush(
                    Color.FromArgb(232, 30, 110, 72));
            }

            var colorName =
                (notionColor ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

            var notionMapped =
                colorName switch
                {
                    "gray" =>
                        Color.FromArgb(232, 78, 83, 96),
                    "brown" =>
                        Color.FromArgb(232, 116, 78, 62),
                    "orange" =>
                        Color.FromArgb(232, 172, 92, 38),
                    "yellow" =>
                        Color.FromArgb(232, 154, 123, 35),
                    "green" =>
                        Color.FromArgb(232, 30, 110, 72),
                    "blue" =>
                        Color.FromArgb(232, 48, 92, 145),
                    "purple" =>
                        Color.FromArgb(232, 102, 72, 145),
                    "pink" =>
                        Color.FromArgb(232, 150, 65, 112),
                    "red" =>
                        Color.FromArgb(232, 145, 56, 56),
                    _ =>
                        default
                };

            if (notionMapped != default)
                return new SolidColorBrush(notionMapped);

            // Respaldo para páginas cuya propiedad no exponga color.
            var normalized =
                (status ?? string.Empty)
                    .ToLowerInvariant();

            var fallback =
                normalized.Contains("suspe") &&
                normalized.Contains("pago")
                    ? Color.FromArgb(232, 154, 123, 35)
                    : normalized.Contains("arrancar") &&
                      normalized.Contains("asignar")
                        ? Color.FromArgb(232, 145, 56, 56)
                        : normalized.Contains("prtuz") &&
                          normalized.Contains("por hacer")
                            ? Color.FromArgb(232, 102, 72, 145)
                            : normalized.Contains("revisar") &&
                              normalized.Contains("revisiones")
                                ? Color.FromArgb(232, 48, 92, 145)
                                : normalized.Contains("pendiente") &&
                                  normalized.Contains("cobrar")
                                    ? Color.FromArgb(232, 30, 110, 72)
                                    : normalized.Contains("cobrado") &&
                                      normalized.Contains("terminado")
                                        ? Color.FromArgb(232, 30, 110, 72)
                                        : normalized.Contains("termin")
                                            ? Color.FromArgb(230, 30, 110, 72)
                                            : normalized.Contains("bloq")
                                                ? Color.FromArgb(230, 125, 56, 56)
                                                : normalized.Contains("proceso") ||
                                                  normalized.Contains("trabaj")
                                                    ? Color.FromArgb(230, 48, 92, 145)
                                                    : Color.FromArgb(230, 71, 77, 102);

            return new SolidColorBrush(fallback);
        }

        private static bool IsCalendarPendingStatus(
            string normalizedStatus)
        {
            return
                normalizedStatus.Contains(
                    "suspe x pago info",
                    StringComparison.Ordinal) ||
                normalizedStatus.Contains(
                    "arrancar asignar",
                    StringComparison.Ordinal) ||
                normalizedStatus.Contains(
                    "prtuz por hacer",
                    StringComparison.Ordinal);
        }

        private static bool IsCalendarInProgressStatus(
            string normalizedStatus)
        {
            return
                normalizedStatus.Contains(
                    "revisar revisiones",
                    StringComparison.Ordinal) ||
                normalizedStatus.Contains(
                    "terminado rev cobro",
                    StringComparison.Ordinal);
        }

        private static bool IsCalendarCompletedStatus(
            string normalizedStatus)
        {
            return
                normalizedStatus.Contains(
                    "pendiente cobrar",
                    StringComparison.Ordinal) ||
                normalizedStatus.Contains(
                    "cobrado terminado",
                    StringComparison.Ordinal);
        }

        private void CalendarActivity_PointerEntered(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not NotionCalendarActivity)
            {
                return;
            }

            _calendarPointerOverActivity = true;
            _calendarPendingActivityButton = button;

            StopCalendarPreviewCloseTimer();

            if (_calendarActivityHoverTimer == null)
            {
                _calendarActivityHoverTimer =
                    new DispatcherTimer
                    {
                        // Debe mantenerse sobre una actividad antes de
                        // cambiar o abrir el preview.
                        Interval =
                            TimeSpan.FromMilliseconds(650)
                    };

                _calendarActivityHoverTimer.Tick +=
                    CalendarActivityHoverTimer_Tick;
            }

            _calendarActivityHoverTimer.Stop();
            _calendarActivityHoverTimer.Start();
        }

        private async void CalendarActivityHoverTimer_Tick(
            object? sender,
            object e)
        {
            _calendarActivityHoverTimer?.Stop();

            var button =
                _calendarPendingActivityButton;

            if (!_calendarPointerOverActivity ||
                button == null ||
                button.Tag is not NotionCalendarActivity activity)
            {
                return;
            }

            // Mientras el cursor cruza otras tarjetas para llegar al popup,
            // no se cambia el contenido que ya está abierto. Solo cambia
            // después de permanecer 650 ms sobre la nueva actividad.
            _calendarHoveredActivityButton = button;

            try
            {
                _calendarHoverPreviewCts?.Cancel();
                _calendarHoverPreviewCts?.Dispose();
            }
            catch
            {
            }

            _calendarHoverPreviewCts =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(90));

            var localCts =
                _calendarHoverPreviewCts;

            try
            {
                ShowCalendarActivityPreviewFlyout(
                    button,
                    BuildCalendarActivityLoadingPreview(
                        activity));

                var token =
                    ApplicationData.Current.LocalSettings.Values[
                        "Notion.Token"] as string;

                if (string.IsNullOrWhiteSpace(token) ||
                    string.IsNullOrWhiteSpace(activity.PageId))
                {
                    UpdateCalendarActivityPreviewContent(
                        BuildCalendarActivitySummary(
                            activity));

                    return;
                }

                var blocks =
                    await _notionPreviewService.GetPagePreviewAsync(
                        token,
                        activity.PageId,
                        localCts.Token);

                if (localCts.IsCancellationRequested ||
                    _calendarHoveredActivityButton != button)
                {
                    return;
                }

                UpdateCalendarActivityPreviewContent(
                    BuildCalendarActivityPagePreview(
                        activity,
                        blocks));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_calendarHoveredActivityButton != button)
                    return;

                UpdateCalendarActivityPreviewContent(
                    BuildCalendarActivityErrorPreview(
                        activity,
                        ex.Message));
            }
        }

        private void CalendarActivity_PointerExited(
            object sender,
            PointerRoutedEventArgs e)
        {
            _calendarPointerOverActivity = false;

            if (_calendarPendingActivityButton == sender)
                _calendarPendingActivityButton = null;

            _calendarActivityHoverTimer?.Stop();
            StartCalendarPreviewCloseTimer();
        }

        private void ShowCalendarActivityPreviewFlyout(
            Button anchor,
            FrameworkElement content)
        {
            StopCalendarPreviewCloseTimer();

            _calendarHoveredActivityButton = anchor;

            if (_calendarActivityPreviewPopup?.IsOpen == true &&
                _calendarActivityPreviewHost != null)
            {
                _calendarActivityPreviewHost.Content =
                    content;

                PositionCalendarActivityPreviewPopup(
                    anchor);

                return;
            }

            var host = new ContentControl
            {
                Content = content,
                IsTabStop = true
            };

            host.PointerEntered +=
                CalendarPreviewContent_PointerEntered;

            host.PointerExited +=
                CalendarPreviewContent_PointerExited;

            var card = new Border
            {
                Child = host,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Background =
                    new SolidColorBrush(
                        Color.FromArgb(
                            255,
                            42,
                            42,
                            42)),
                BorderBrush =
                    new SolidColorBrush(
                        Color.FromArgb(
                            70,
                            255,
                            255,
                            255)),
                BorderThickness =
                    new Thickness(1),
                Shadow =
                    new Microsoft.UI.Xaml.Media.ThemeShadow()
            };

            card.PointerEntered +=
                CalendarPreviewContent_PointerEntered;

            card.PointerExited +=
                CalendarPreviewContent_PointerExited;

            var popup = new Popup
            {
                Child = card,
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = true,
                XamlRoot = anchor.XamlRoot
            };

            _calendarActivityPreviewHost = host;
            _calendarActivityPreviewPopupCard = card;
            _calendarActivityPreviewPopup = popup;

            popup.IsOpen = true;

            PositionCalendarActivityPreviewPopup(
                anchor);
        }

        private void PositionCalendarActivityPreviewPopup(
            Button anchor)
        {
            var popup =
                _calendarActivityPreviewPopup;

            if (popup == null ||
                !popup.IsOpen ||
                RootLayout == null)
            {
                return;
            }

            try
            {
                var transform =
                    anchor.TransformToVisual(
                        RootLayout);

                var point =
                    transform.TransformPoint(
                        new Windows.Foundation.Point(
                            0,
                            0));

                const double previewWidth = 560;
                const double previewHeight = 740;
                const double gap = 10;

                var rootWidth =
                    Math.Max(
                        1,
                        RootLayout.ActualWidth);

                var rootHeight =
                    Math.Max(
                        1,
                        RootLayout.ActualHeight);

                var left =
                    point.X +
                    anchor.ActualWidth +
                    gap;

                if (left + previewWidth >
                    rootWidth - 12)
                {
                    left =
                        point.X -
                        previewWidth -
                        gap;
                }

                left =
                    Math.Clamp(
                        left,
                        12,
                        Math.Max(
                            12,
                            rootWidth -
                            previewWidth -
                            12));

                var top =
                    point.Y;

                if (top + previewHeight >
                    rootHeight - 12)
                {
                    top =
                        rootHeight -
                        previewHeight -
                        12;
                }

                top =
                    Math.Clamp(
                        top,
                        12,
                        Math.Max(
                            12,
                            rootHeight -
                            180));

                popup.HorizontalOffset =
                    left;

                popup.VerticalOffset =
                    top;
            }
            catch
            {
                popup.HorizontalOffset = 140;
                popup.VerticalOffset = 140;
            }
        }

        private void UpdateCalendarActivityPreviewContent(
            FrameworkElement content)
        {
            if (_calendarActivityPreviewPopup?.IsOpen != true ||
                _calendarActivityPreviewHost == null)
            {
                return;
            }

            _calendarActivityPreviewHost.Content =
                content;
        }

        private void CalendarPreviewContent_PointerEntered(
            object sender,
            PointerRoutedEventArgs e)
        {
            _calendarPointerOverPreview = true;
            _calendarPointerOverActivity = false;
            _calendarPendingActivityButton = null;
            _calendarActivityHoverTimer?.Stop();

            StopCalendarPreviewCloseTimer();
        }

        private void CalendarPreviewContent_PointerExited(
            object sender,
            PointerRoutedEventArgs e)
        {
            _calendarPointerOverPreview = false;
            StartCalendarPreviewCloseTimer();
        }

        private void StartCalendarPreviewCloseTimer()
        {
            if (_calendarPreviewCloseTimer == null)
            {
                _calendarPreviewCloseTimer =
                    new DispatcherTimer
                    {
                        Interval =
                            TimeSpan.FromMilliseconds(320)
                    };

                _calendarPreviewCloseTimer.Tick +=
                    (_, __) =>
                    {
                        _calendarPreviewCloseTimer.Stop();

                        if (!_calendarPointerOverActivity &&
                            !_calendarPointerOverPreview)
                        {
                            HideCalendarActivityPreviewFlyout();
                        }
                    };
            }

            _calendarPreviewCloseTimer.Stop();
            _calendarPreviewCloseTimer.Start();
        }

        private void StopCalendarPreviewCloseTimer()
        {
            _calendarPreviewCloseTimer?.Stop();
        }

        private void HideCalendarActivityPreviewFlyout()
        {
            StopCalendarPreviewCloseTimer();
            StopNotionPreviewSpeech();

            try
            {
                _calendarHoverPreviewCts?.Cancel();
            }
            catch
            {
            }

            if (_calendarActivityPreviewPopup != null)
                _calendarActivityPreviewPopup.IsOpen = false;

            _calendarActivityPreviewPopup = null;
            _calendarActivityPreviewPopupCard = null;
            _calendarActivityPreviewHost = null;
            _calendarHoveredActivityButton = null;
            _calendarPendingActivityButton = null;
            _calendarActivityHoverTimer?.Stop();
            _calendarPointerOverActivity = false;
            _calendarPointerOverPreview = false;
        }

        private FrameworkElement BuildCalendarActivitySummary(
            NotionCalendarActivity activity)
        {
            return BuildCalendarActivityPreviewShell(
                activity,
                content: null,
                statusText:
                    "Pasa el cursor un momento para cargar el contenido de la página.");
        }

        private FrameworkElement BuildCalendarActivityLoadingPreview(
            NotionCalendarActivity activity)
        {
            var loading = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };

            loading.Children.Add(
                new ProgressRing
                {
                    Width = 18,
                    Height = 18,
                    IsActive = true
                });

            loading.Children.Add(
                new TextBlock
                {
                    Text =
                        "Cargando contenido de Notion...",
                    VerticalAlignment =
                        VerticalAlignment.Center,
                    FontSize = 11,
                    Opacity = 0.78
                });

            return BuildCalendarActivityPreviewShell(
                activity,
                loading,
                statusText: string.Empty);
        }

        private FrameworkElement BuildCalendarActivityPagePreview(
            NotionCalendarActivity activity,
            IReadOnlyList<NotionPreviewBlock> blocks)
        {
            var content = new StackPanel
            {
                Spacing = 7
            };

            if (!string.IsNullOrWhiteSpace(
                    activity.Description))
            {
                content.Children.Add(
                    CreateSectionLabel(
                        "DESCRIPCIÓN"));

                content.Children.Add(
                    CreatePreviewText(
                        activity.Description,
                        11,
                        Microsoft.UI.Text.FontWeights.Normal,
                        0.88,
                        0));
            }

            var visibleBlocks = blocks
                .Where(block =>
                    block.Kind ==
                        NotionPreviewBlockKind.Divider ||
                    !string.IsNullOrWhiteSpace(
                        block.Text) ||
                    !string.IsNullOrWhiteSpace(
                        block.Url))
                .ToList();

            if (visibleBlocks.Count > 0)
            {
                content.Children.Add(
                    CreateSectionLabel(
                        "CONTENIDO DE LA PÁGINA"));
            }

            var number = 0;

            foreach (var block in visibleBlocks)
            {
                if (block.Kind ==
                    NotionPreviewBlockKind.NumberedListItem)
                {
                    number++;
                }
                else
                {
                    number = 0;
                }

                var element =
                    CreateBlockElement(
                        block,
                        number);

                if (element != null)
                {
                    ConstrainCalendarPreviewElement(
                        element,
                        450);

                    content.Children.Add(element);
                }
            }

            if (visibleBlocks.Count == 0 &&
                string.IsNullOrWhiteSpace(
                    activity.Description))
            {
                content.Children.Add(
                    CreatePreviewText(
                        "La página no contiene bloques visibles.",
                        11,
                        Microsoft.UI.Text.FontWeights.Normal,
                        0.64,
                        0));
            }

            return BuildCalendarActivityPreviewShell(
                activity,
                content,
                statusText:
                    "Clic en la actividad para abrir la página en Notion.",
                speechBlocks: blocks);
        }

        private static void ConstrainCalendarPreviewElement(
            UIElement element,
            double availableWidth)
        {
            if (element is FrameworkElement framework)
            {
                framework.MaxWidth =
                    Math.Max(
                        120,
                        availableWidth);
            }

            switch (element)
            {
                case TextBlock text:
                    text.TextWrapping =
                        TextWrapping.Wrap;

                    text.TextTrimming =
                        TextTrimming.None;

                    text.MaxLines = 0;

                    text.MaxWidth =
                        Math.Max(
                            100,
                            availableWidth);
                    break;

                case CheckBox checkBox:
                    // El cuadro conserva su tamaño y el texto recibe el resto.
                    checkBox.MaxWidth = 34;
                    break;

                case Border border
                    when border.Child is UIElement child:
                    ConstrainCalendarPreviewElement(
                        child,
                        Math.Max(
                            100,
                            availableWidth - 18));
                    break;

                case ContentControl contentControl
                    when contentControl.Content is UIElement child:
                    ConstrainCalendarPreviewElement(
                        child,
                        availableWidth);
                    break;

                case Panel panel:
                    var childWidth =
                        panel is StackPanel stack &&
                        stack.Orientation ==
                            Orientation.Horizontal
                            ? Math.Max(
                                100,
                                availableWidth - 48)
                            : availableWidth;

                    foreach (var child in panel.Children)
                    {
                        ConstrainCalendarPreviewElement(
                            child,
                            childWidth);
                    }
                    break;

                case Expander expander:
                    if (expander.Header is UIElement header)
                    {
                        ConstrainCalendarPreviewElement(
                            header,
                            Math.Max(
                                100,
                                availableWidth - 30));
                    }

                    if (expander.Content is UIElement expanderContent)
                    {
                        ConstrainCalendarPreviewElement(
                            expanderContent,
                            Math.Max(
                                100,
                                availableWidth - 18));
                    }
                    break;
            }
        }

        private FrameworkElement BuildCalendarActivityErrorPreview(
            NotionCalendarActivity activity,
            string message)
        {
            var content = new Border
            {
                Padding = new Thickness(9),
                CornerRadius = new CornerRadius(6),
                Background =
                    new SolidColorBrush(
                        Color.FromArgb(
                            32,
                            255,
                            90,
                            90)),
                Child = new TextBlock
                {
                    Text =
                        $"No se pudo cargar el contenido completo.\n{message}",
                    MaxWidth = 430,
                    TextWrapping =
                        TextWrapping.Wrap,
                    FontSize = 10.5,
                    Opacity = 0.82
                }
            };

            return BuildCalendarActivityPreviewShell(
                activity,
                content,
                statusText:
                    "Clic en la actividad para abrir la página en Notion.");
        }

        private FrameworkElement BuildCalendarActivityPreviewShell(
            NotionCalendarActivity activity,
            UIElement? content,
            string statusText,
            IReadOnlyList<NotionPreviewBlock>? speechBlocks = null)
        {
            var root = new StackPanel
            {
                Width = 500,
                MaxWidth = 500,
                Spacing = 8,
                Padding = new Thickness(10)
            };

            root.Children.Add(
                new TextBlock
                {
                    Text = activity.Title,
                    MaxWidth = 450,
                    FontSize = 14,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping =
                        TextWrapping.Wrap
                });

            root.Children.Add(
                new TextBlock
                {
                    Text = activity.TimeLabel,
                    FontSize = 11.5,
                    Opacity = 0.78
                });

            void AddMeta(
                string label,
                string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                var row = new Grid
                {
                    ColumnSpacing = 8
                };

                row.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });

                row.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = new GridLength(
                            1,
                            GridUnitType.Star)
                    });

                var labelText =
                    new TextBlock
                    {
                        Text = $"{label}:",
                        FontSize = 10.5,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Opacity = 0.62
                    };

                var valueText =
                    new TextBlock
                    {
                        Text = value,
                        MaxWidth = 360,
                        FontSize = 10.5,
                        TextWrapping =
                            TextWrapping.Wrap,
                        Opacity = 0.90
                    };

                Grid.SetColumn(labelText, 0);
                Grid.SetColumn(valueText, 1);

                row.Children.Add(labelText);
                row.Children.Add(valueText);
                root.Children.Add(row);
            }

            AddMeta("Persona actual", activity.Person);
            AddMeta(
                "Calendario de origen",
                GetCalendarOrigin(activity));
            AddMeta("Proyecto", activity.Project);
            AddMeta("Estado", activity.Status);
            AddMeta(
                "Última actualización",
                activity.UpdateText);

            var shellChecklist =
                GetCalendarChecklistStats(activity);

            AddMeta(
                "Checklist",
                FormatCalendarChecklistLabel(
                    activity,
                    shellChecklist));

            AddMeta(
                "Automatización",
                activity.IsAutomationLocked
                    ? "Bloqueada"
                    : "Permitida");

            AddMeta(
                "Movimiento ANFETA",
                GetCalendarDayMovementDetail(activity));

            var actionsPanel = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 2,
                ItemWidth = 228,
                ItemHeight = 36,
                Width = 470,
                Margin = new Thickness(0, 4, 0, 2)
            };

            if (!activity.IsReviewMirror &&
                HasExactCalendarPhase(
                    activity,
                    "rtuzREVISION") &&
                activity.IsPendingReview &&
                CanCurrentUserResolveReview(activity))
            {
                Button AddReviewerButton(
                    string text,
                    RoutedEventHandler handler)
                {
                    var button = new Button
                    {
                        Content = text,
                        Width = 220,
                        Height = 30,
                        Margin = new Thickness(0, 0, 8, 6),
                        Padding = new Thickness(10, 0, 10, 0),
                        CornerRadius = new CornerRadius(6),
                        Tag = activity
                    };

                    button.Click += handler;
                    actionsPanel.Children.Add(button);
                    return button;
                }

                AddReviewerButton(
                    "Aprobar revisión",
                    CalendarContextApproveReview_Click);

                AddReviewerButton(
                    "Regresar con correcciones…",
                    CalendarContextReturnReview_Click);
            }
            else if (CanSendCalendarActivityToReview(activity))
            {
                var reviewButton =
                    new Button
                    {
                        Content = "Enviar a revisión…",
                        Width = 220,
                        Height = 30,
                        Margin = new Thickness(0, 0, 8, 6),
                        Padding = new Thickness(10, 0, 10, 0),
                        CornerRadius = new CornerRadius(6),
                        Tag = activity
                    };

                reviewButton.Click +=
                    async (_, __) =>
                    {
                        reviewButton.IsEnabled = false;

                        try
                        {
                            var sent =
                                await PromptAndSendCalendarActivityToReviewAsync(
                                    activity);

                            if (sent)
                            {
                                reviewButton.Content =
                                    "Enviada a revisión ✅";
                            }
                        }
                        finally
                        {
                            await Task.Delay(900);
                            reviewButton.IsEnabled = true;
                            reviewButton.Content =
                                "Enviar a revisión…";
                        }
                    };

                actionsPanel.Children.Add(
                    reviewButton);
            }

            if (!activity.IsReviewMirror &&
                HasExactCalendarPhase(
                    activity,
                    "zREVISION"))
            {
                var reassignButton = new Button
                {
                    Content =
                        "Reasignar → prtuzREVISION",
                    Width = 220,
                    Height = 30,
                    Margin =
                        new Thickness(0, 0, 8, 6),
                    Padding =
                        new Thickness(10, 0, 10, 0),
                    CornerRadius =
                        new CornerRadius(6),
                    Tag = activity
                };

                reassignButton.Click +=
                    CalendarContextReassignApproved_Click;

                actionsPanel.Children.Add(
                    reassignButton);
            }

            var messageButton =
                new Button
                {
                    Content = "Enviar mensaje…",
                    Width = 220,
                    Height = 30,
                    Margin = new Thickness(0, 0, 8, 6),
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    Tag = activity
                };

            messageButton.Click +=
                async (_, __) =>
                {
                    HideCalendarActivityPreviewFlyout();

                    await ShowCalendarMessageComposerAsync(
                        activity);
                };

            ToolTipService.SetToolTip(
                messageButton,
                "Crear un mensaje relacionado con esta actividad");

            actionsPanel.Children.Add(messageButton);

            if (!activity.IsReviewMirror)
            {
                var lockButton = new Button
                {
                    Content = activity.IsAutomationLocked
                        ? "🔓 Desbloquear automatización"
                        : "🔒 Bloquear automatización",
                    Width = 220,
                    Height = 30,
                    Margin = new Thickness(0, 0, 8, 6),
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    Tag = activity
                };

                lockButton.Click +=
                    async (_, __) =>
                    {
                        if (await ToggleCalendarActivityAutomationLockAsync(
                                activity))
                        {
                            lockButton.Content =
                                activity.IsAutomationLocked
                                    ? "🔓 Desbloquear automatización"
                                    : "🔒 Bloquear automatización";
                        }
                    };

                actionsPanel.Children.Add(lockButton);
            }

            Button AddMoveButton(
                string text,
                int days)
            {
                var button = new Button
                {
                    Content = text,
                    Width = 220,
                    Height = 30,
                    Margin = new Thickness(0, 0, 8, 6),
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    Tag = activity,
                    IsEnabled = !activity.IsAutomationLocked
                };

                button.Click += async (_, __) =>
                    await MoveCalendarActivityByDaysAsync(
                        activity,
                        days);

                actionsPanel.Children.Add(button);
                return button;
            }

            if (!activity.IsReviewMirror)
            {
                AddMoveButton("Mover a mañana", 1);
                AddMoveButton("+3 días", 3);
                AddMoveButton("+1 semana", 7);
            }
            else
            {
                actionsPanel.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Copia visual de seguimiento · solo lectura",
                        Width = 448,
                        Margin = new Thickness(0, 6, 0, 8),
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Opacity = 0.78,
                        TextWrapping = TextWrapping.Wrap
                    });
            }

            var readSpeechButton = new Button
            {
                Content = "▶ Leer resumen",
                Width = 220,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                Tag = activity
            };

            var stopSpeechButton = new Button
            {
                Content = "■ Detener lectura",
                Width = 220,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                IsEnabled = false,
                Tag = activity
            };

            readSpeechButton.Click +=
                async (_, __) =>
                {
                    await StartCalendarActivitySpeechAsync(
                        activity,
                        speechBlocks,
                        readSpeechButton,
                        stopSpeechButton);
                };

            stopSpeechButton.Click +=
                (_, __) =>
                {
                    StopNotionPreviewSpeech();
                    readSpeechButton.Content = "▶ Leer resumen";
                    readSpeechButton.IsEnabled = true;
                    stopSpeechButton.IsEnabled = false;
                };

            actionsPanel.Children.Add(readSpeechButton);
            actionsPanel.Children.Add(stopSpeechButton);

            root.Children.Add(actionsPanel);

            if (content != null)
            {
                root.Children.Add(
                    new Border
                    {
                        Height = 1,
                        Margin =
                            new Thickness(0, 3, 0, 3),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    40,
                                    255,
                                    255,
                                    255))
                    });

                root.Children.Add(content);
            }

            if (!string.IsNullOrWhiteSpace(
                    statusText))
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text = statusText,
                        FontSize = 10,
                        Opacity = 0.55,
                        Margin =
                            new Thickness(0, 4, 0, 0),
                        TextWrapping =
                            TextWrapping.Wrap
                    });
            }

            var scrollViewer = new ScrollViewer
            {
                Content = root,
                Width = 540,
                MaxHeight = 720,
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Visible,
                VerticalScrollMode =
                    ScrollMode.Enabled,
                IsTabStop = true,
                Padding = new Thickness(0)
            };

            scrollViewer.PointerEntered +=
                (_, __) =>
                {
                    scrollViewer.Focus(
                        FocusState.Pointer);
                };

            scrollViewer.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(
                    (_, args) =>
                    {
                        var delta =
                            args.GetCurrentPoint(
                                scrollViewer)
                                .Properties
                                .MouseWheelDelta;

                        if (delta == 0)
                            return;

                        var next =
                            Math.Clamp(
                                scrollViewer.VerticalOffset -
                                Math.Sign(delta) * 70,
                                0,
                                scrollViewer.ScrollableHeight);

                        scrollViewer.ChangeView(
                            null,
                            next,
                            null,
                            disableAnimation: false);

                        args.Handled = true;
                    }),
                handledEventsToo: true);

            return scrollViewer;
        }

        private static string GetCalendarMessageRecipientTag(
            string person)
        {
            return NormalizeCalendarPerson(person) switch
            {
                "John" => "jjohn",
                "Karla" => "kkarl",
                "Isaias" => "iisai",
                "Sotelo" => "eedua",
                "Acalli" => "aacal",
                "Andrade" => "aandr",
                "Emmanuel" => "eemma",
                "Brian" => "bbria",
                "Genaro" => "ggena",
                "Neftali" => "nneft",
                _ => string.Empty
            };
        }

        private static string GetCalendarMessagePersonName(
            string person)
        {
            var normalized = NormalizeCalendarPerson(person);
            return string.IsNullOrWhiteSpace(normalized)
                ? "Sin identificar"
                : normalized;
        }

        private async Task<ReviewAlertSourceLink?>
            SendCalendarReviewAlertAsync(
                NotionCalendarActivity activity,
                string recipientPerson,
                string messagePrefix,
                bool requireReviewPhase = true)
        {
            if (_calendarReviewAlertSending)
                return null;

            if (requireReviewPhase &&
                !IsReviewEligibleActivity(activity))
            {
                StatusText.Text =
                    "Estado: La revisión debe estar en rtuzREVISION o zREVISION.";
                return null;
            }

            recipientPerson =
                NormalizeCalendarPerson(recipientPerson);

            var recipientTag =
                GetCalendarMessageRecipientTag(recipientPerson);

            if (string.IsNullOrWhiteSpace(recipientTag))
            {
                StatusText.Text =
                    "Estado: No se pudo identificar al destinatario.";
                return null;
            }

            var values =
                ApplicationData.Current.LocalSettings.Values;

            var token =
                values["Notion.Token"] as string;

            var dataSourceId =
                values["Notion.DataSourceId"] as string;

            if (string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(dataSourceId))
            {
                StatusText.Text =
                    "Estado: Configura Notion antes de enviar alertas.";
                return null;
            }

            _calendarReviewAlertSending = true;

            try
            {
                StatusText.Text =
                    $"Estado: Enviando alerta a {recipientPerson}...";

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var senderTag =
                    (values["Messaging.CurrentUserTag"] as string ??
                     string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(senderTag))
                    senderTag = "anfeta";

                var senderPerson =
                    GetCurrentCalendarUserName();

                var now =
                    DateTimeOffset.Now;

                var message =
                    string.Join(
                        " · ",
                        new[]
                        {
                            string.IsNullOrWhiteSpace(messagePrefix)
                                ? "Actividad de revisión"
                                : messagePrefix,
                            activity.TimeLabel,
                            activity.Title
                        }.Where(value =>
                            !string.IsNullOrWhiteSpace(value)));

                _calendarReviewFlowCache.TryGetValue(
                    activity.PageId,
                    out var existingMetadata);

                // Una actividad conserva una sola página auxiliar. Al volver
                // a enviarla, se actualiza el destinatario y se agrega otra
                // entrada al mismo historial; no se crea un segundo mensaje.
                if (existingMetadata != null &&
                    !string.IsNullOrWhiteSpace(
                        existingMetadata.AlertPageId))
                {
                    var existingThreadIsActive =
                        await _calendarReviewFlowService
                            .IsPageActiveAsync(
                                token,
                                existingMetadata.AlertPageId,
                                cts.Token);

                    if (existingThreadIsActive)
                    {
                        await AppendReviewHistoryEntryAsync(
                            existingMetadata,
                            recipientPerson,
                            message,
                            retargetNotification: true,
                            cts.Token);

                        var existingNotification =
                            new ReviewAlertSourceLink
                            {
                                PageId = existingMetadata.AlertPageId,
                                PageUrl = existingMetadata.AlertPageUrl,
                                Title =
                                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} " +
                                    $"{recipientTag} de:{senderTag} [RESPUESTA] " +
                                    $"{message}"
                            };

                        await UpsertCalendarNotificationInLocalIndexAsync(
                            existingNotification);

                        StatusText.Text =
                            $"Estado: Hilo existente enviado a {recipientPerson} ✅";

                        return existingNotification;
                    }

                    // La notificación anterior ya fue atendida y enviada a
                    // papelera. Se cierra ese hilo y se crea uno nuevo.
                }

                using var http =
                    new HttpClient
                    {
                        BaseAddress =
                            new Uri("https://api.notion.com/v1/"),
                        Timeout =
                            TimeSpan.FromMinutes(2)
                    };

                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        token.Trim());

                http.DefaultRequestHeaders.Add(
                    "Notion-Version",
                    "2026-03-11");

                var titlePropertyName =
                    await ResolveReviewAlertTitlePropertyAsync(
                        http,
                        dataSourceId,
                        cts.Token);

                var title =
                    $"{now:yyyy-MM-dd HH:mm} " +
                    $"{recipientTag} de:{senderTag} [RESPUESTA] " +
                    $"{message}";

                var created =
                    await CreateReviewAlertPageAsync(
                        http,
                        dataSourceId,
                        titlePropertyName,
                        title,
                        activity,
                        cts.Token);

                await _calendarReviewFlowService.AppendEntryAsync(
                    token,
                    created.PageId,
                    new MessageThreadEntry
                    {
                        Kind = MessageThreadKind.System,
                        AuthorTag = senderTag,
                        AuthorName = senderPerson,
                        RecipientTag = recipientTag,
                        RecipientName = recipientPerson,
                        CreatedAt = DateTimeOffset.Now,
                        Text = message
                    },
                    cts.Token);

                await UpsertCalendarNotificationInLocalIndexAsync(
                    created);

                StatusText.Text =
                    $"Estado: Alerta enviada a {recipientPerson} ✅";

                return created;
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo enviar la alerta → {ex.Message}";
                return null;
            }
            finally
            {
                _calendarReviewAlertSending = false;
            }
        }

        private async Task UpsertCalendarNotificationInLocalIndexAsync(
            ReviewAlertSourceLink notification)
        {
            if (notification == null ||
                string.IsNullOrWhiteSpace(notification.PageId) ||
                string.IsNullOrWhiteSpace(notification.Title))
            {
                return;
            }

            var snapshot =
                App.LocalIndex.GetAll();

            var row = snapshot.FirstOrDefault(item =>
                item.Source ==
                    Anfeta.UI.Models.Weblab.SearchSource.Notion &&
                string.Equals(
                    item.ExternalId,
                    notification.PageId,
                    StringComparison.OrdinalIgnoreCase));

            if (row == null)
            {
                row = new Anfeta.UI.Models.Weblab.SearchResultRow
                {
                    ExternalId = notification.PageId,
                    NodeId = notification.PageId,
                    ExternalSourceName = "Revisiones",
                    ExternalUrl = notification.PageUrl,
                    Target = notification.PageUrl,
                    Type = "NOTION_PAGE",
                    Source =
                        Anfeta.UI.Models.Weblab.SearchSource.Notion
                };

                snapshot.Add(row);
            }

            row.ExternalSourceName = "Revisiones";
            row.ExternalUrl = notification.PageUrl;
            row.Target = notification.PageUrl;
            row.Name =
                $"[Revisiones] {notification.Title}";
            row.SearchText =
                $"Revisiones {notification.Title}";
            row.ServerModified =
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture);

            App.LocalIndex.Set(snapshot);

            await PersistCombinedIndexIfPossibleAsync(
                snapshot);

            RefreshMessagesView();

            try
            {
                var reminderService =
                    App.AppHost.Services.GetRequiredService<
                        IndexedFileReminderService>();

                reminderService.ScanNow();
            }
            catch
            {
                // El índice ya quedó actualizado. El timer normal volverá
                // a revisar el recordatorio aunque el disparo inmediato falle.
            }
        }

        private async Task AppendReviewHistoryEntryAsync(
            ReviewFlowMetadata metadata,
            string recipientPerson,
            string text,
            bool retargetNotification,
            CancellationToken cancellationToken)
        {
            if (metadata == null ||
                string.IsNullOrWhiteSpace(metadata.AlertPageId))
            {
                return;
            }

            var values =
                ApplicationData.Current.LocalSettings.Values;

            var token =
                values["Notion.Token"] as string;

            var dataSourceId =
                values["Notion.DataSourceId"] as string;

            if (string.IsNullOrWhiteSpace(token))
                return;

            var recipientTag =
                GetCalendarMessageRecipientTag(recipientPerson);

            var authorTag =
                (values["Messaging.CurrentUserTag"] as string ??
                 string.Empty).Trim();

            await _calendarReviewFlowService.AppendEntryAsync(
                token,
                metadata.AlertPageId,
                new MessageThreadEntry
                {
                    Kind = MessageThreadKind.Message,
                    AuthorTag = authorTag,
                    AuthorName = GetCurrentCalendarUserName(),
                    RecipientTag = recipientTag,
                    RecipientName = recipientPerson,
                    CreatedAt = DateTimeOffset.Now,
                    Text = text
                },
                cancellationToken);

            if (!retargetNotification ||
                string.IsNullOrWhiteSpace(dataSourceId))
            {
                return;
            }

            using var http =
                new HttpClient
                {
                    BaseAddress =
                        new Uri("https://api.notion.com/v1/"),
                    Timeout =
                        TimeSpan.FromMinutes(2)
                };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    token.Trim());

            http.DefaultRequestHeaders.Add(
                "Notion-Version",
                "2026-03-11");

            var titlePropertyName =
                await ResolveReviewAlertTitlePropertyAsync(
                    http,
                    dataSourceId,
                    cancellationToken);

            var title =
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} " +
                $"{recipientTag} de:{authorTag} [RESPUESTA] " +
                $"{text}";

            var payload =
                new Dictionary<string, object?>
                {
                    ["properties"] =
                        new Dictionary<string, object?>
                        {
                            [titlePropertyName] =
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "title",
                                    ["title"] =
                                        new object[]
                                        {
                                            new Dictionary<string, object?>
                                            {
                                                ["type"] = "text",
                                                ["text"] =
                                                    new Dictionary<string, object?>
                                                    {
                                                        ["content"] = title
                                                    }
                                            }
                                        }
                                }
                        }
                };

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Patch,
                    $"pages/{metadata.AlertPageId}")
                {
                    Content =
                        new StringContent(
                            JsonSerializer.Serialize(payload),
                            Encoding.UTF8,
                            "application/json")
                };

            using var response =
                await http.SendAsync(
                    request,
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                throw new InvalidOperationException(
                    $"No se pudo actualizar la notificación ({(int)response.StatusCode}): {body}");
            }
        }

        private static async Task<string>
            ResolveReviewAlertTitlePropertyAsync(
                HttpClient http,
                string dataSourceId,
                CancellationToken cancellationToken)
        {
            using var response =
                await http.GetAsync(
                    $"data_sources/{dataSourceId.Trim()}",
                    cancellationToken);

            var json =
                await response.Content
                    .ReadAsStringAsync(
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Notion no permitió leer la base ({(int)response.StatusCode}).");
            }

            using var document =
                JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty(
                    "properties",
                    out var properties))
            {
                throw new InvalidOperationException(
                    "No se encontró la propiedad de título de la base.");
            }

            foreach (var property in
                     properties.EnumerateObject())
            {
                if (property.Value.TryGetProperty(
                        "type",
                        out var type) &&
                    string.Equals(
                        type.GetString(),
                        "title",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return property.Name;
                }
            }

            throw new InvalidOperationException(
                "La base de Revisiones no expone una propiedad de título.");
        }

        private static async Task<ReviewAlertSourceLink> CreateReviewAlertPageAsync(
            HttpClient http,
            string dataSourceId,
            string titlePropertyName,
            string title,
            NotionCalendarActivity activity,
            CancellationToken cancellationToken)
        {
            var children =
                new List<object>();

            var sourceMetadata =
                new ReviewAlertSourceLink
                {
                    PageId = activity.PageId,
                    PageUrl = activity.PageUrl,
                    Title = activity.Title
                };

            var sourceEncoded =
                Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(sourceMetadata)));

            children.Add(
                new
                {
                    @object = "block",
                    type = "toggle",
                    toggle = new
                    {
                        rich_text = new[]
                        {
                            new
                            {
                                type = "text",
                                text = new
                                {
                                    content =
                                        "Datos internos de ANFETA"
                                },
                                annotations = new
                                {
                                    bold = false,
                                    italic = false,
                                    strikethrough = false,
                                    underline = false,
                                    code = false,
                                    color = "gray"
                                }
                            }
                        },
                        children = new object[]
                        {
                            new
                            {
                                @object = "block",
                                type = "paragraph",
                                paragraph = new
                                {
                                    rich_text = new[]
                                    {
                                        new
                                        {
                                            type = "text",
                                            text = new
                                            {
                                                content =
                                                    NotionMessageThreadService.ReviewSourcePrefix +
                                                    sourceEncoded
                                            },
                                            annotations = new
                                            {
                                                bold = false,
                                                italic = false,
                                                strikethrough = false,
                                                underline = false,
                                                code = true,
                                                color = "gray"
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                });

            if (!string.IsNullOrWhiteSpace(
                    activity.PageUrl))
            {
                children.Add(
                    new
                    {
                        @object = "block",
                        type = "paragraph",
                        paragraph = new
                        {
                            rich_text = new object[]
                            {
                                new
                                {
                                    type = "text",
                                    text = new
                                    {
                                        content =
                                            "Abrir actividad original: ",
                                    }
                                },
                                new
                                {
                                    type = "text",
                                    text = new
                                    {
                                        content =
                                            activity.PageUrl,
                                        link = new
                                        {
                                            url =
                                                activity.PageUrl
                                        }
                                    }
                                }
                            }
                        }
                    });
            }

            children.Add(
                new
                {
                    @object = "block",
                    type = "paragraph",
                    paragraph = new
                    {
                        rich_text = new[]
                        {
                            new
                            {
                                type = "text",
                                text = new
                                {
                                    content =
                                        $"Responsable: {activity.Person}\n" +
                                        $"Horario: {activity.TimeLabel}\n" +
                                        $"Estado: {activity.Status}\n" +
                                        $"Última actualización: {activity.UpdateText}"
                                }
                            }
                        }
                    }
                });

            var payload =
                new
                {
                    parent = new
                    {
                        type = "data_source_id",
                        data_source_id =
                            dataSourceId.Trim()
                    },
                    properties = new Dictionary<
                        string,
                        object>
                    {
                        [titlePropertyName] =
                            new
                            {
                                type = "title",
                                title = new[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = new
                                        {
                                            content = title
                                        }
                                    }
                                }
                            }
                    },
                    children
                };

            using var content =
                new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

            using var response =
                await http.PostAsync(
                    "pages",
                    content,
                    cancellationToken);

            var json =
                await response.Content
                    .ReadAsStringAsync(
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Notion rechazó la alerta ({(int)response.StatusCode}): {json}");
            }

            using var document =
                JsonDocument.Parse(json);

            return new ReviewAlertSourceLink
            {
                PageId =
                    document.RootElement.TryGetProperty(
                        "id",
                        out var id)
                        ? id.GetString() ?? string.Empty
                        : string.Empty,
                PageUrl =
                    document.RootElement.TryGetProperty(
                        "url",
                        out var url)
                        ? url.GetString() ?? string.Empty
                        : string.Empty,
                Title = title
            };
        }

        private async Task MoveCalendarActivityByDaysAsync(
            NotionCalendarActivity activity,
            int days)
        {
            if (activity?.IsReviewMirror == true)
            {
                StatusText.Text =
                    "Estado: La copia de seguimiento es solo visual.";
                return;
            }

            if (activity?.IsAutomationLocked == true)
            {
                StatusText.Text =
                    "Estado: La actividad está bloqueada. Desbloquéala antes de moverla.";
                return;
            }

            if (activity == null ||
                string.IsNullOrWhiteSpace(activity.PageId))
            {
                return;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var sourceDate = activity.Start.Date;

            var targetDate =
                sourceDate.AddDays(days);

            try
            {
                StatusText.Text =
                    $"Estado: Moviendo actividad al {targetDate:dd/MM/yyyy}...";

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var updated =
                    await _notionCalendarService.MoveActivityToDateAsync(
                        token,
                        activity,
                        targetDate,
                        cts.Token);

                RegisterCalendarDayMovement(
                    activity,
                    sourceDate,
                    targetDate,
                    days == 1
                        ? "Mover a mañana"
                        : days == 3
                            ? "+3 días"
                            : days == 7
                                ? "+1 semana"
                                : $"Mover +{days} día(s)");

                activity.Start = updated.Start;
                activity.End = updated.End;
                activity.DatePropertyName =
                    updated.DatePropertyName;

                HideCalendarActivityPreviewFlyout();

                // MoveActivityToDateAsync ya actualizó la caché de ambos días.
                // Se repinta de inmediato para que la tarjeta desaparezca del
                // día anterior sin esperar una consulta completa a Notion.
                var currentDay =
                    await _notionCalendarService.TryGetCachedDayAsync(
                        _calendarSelectedDate,
                        cts.Token);

                _calendarActivities =
                    currentDay ?? Array.Empty<NotionCalendarActivity>();

                DrawCalendar(_calendarActivities);

                // Después se confirma silenciosamente cualquier cambio adicional.
                _ = RefreshCalendarChangesSilentlyAsync();

                StatusText.Text =
                    days == 1
                        ? "Estado: Actividad movida a mañana ✅"
                        : $"Estado: Actividad movida al {targetDate:dd/MM/yyyy} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo mover la actividad → {ex.Message}";
            }
        }

        private static string GetCalendarOrigin(
            NotionCalendarActivity activity)
        {
            var origin =
                string.IsNullOrWhiteSpace(
                    activity.OriginalPerson)
                    ? activity.Person
                    : activity.OriginalPerson;

            return string.IsNullOrWhiteSpace(origin)
                ? string.Empty
                : origin;
        }

        private static bool IsCompletedReviewStatus(
            string status)
        {
            var normalized =
                NormalizeCalendarSearchText(status);

            return
                normalized.Contains(
                    "pendiente cobrar",
                    StringComparison.Ordinal) ||
                normalized.Contains(
                    "cobrado terminado",
                    StringComparison.Ordinal);
        }

        private static IReadOnlyList<string> SplitPersons(string value)
        {
            var persons =
                (value ?? string.Empty)
                .Split(
                    new[] { ',', ';', '|' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return persons.Count > 0
                ? persons
                : new[] { "Sin asignar" };
        }

        private static string FormatHour(int hour)
        {
            var suffix = hour < 12 ? "AM" : "PM";
            var display = hour % 12;

            if (display == 0)
                display = 12;

            return $"{display}:00 {suffix}";
        }

        private static string FormatCalendarDate(DateTime date)
        {
            var culture = new CultureInfo("es-MX");

            var text = date.ToString(
                "dddd, d 'de' MMMM 'de' yyyy",
                culture);

            return char.ToUpper(text[0], culture) +
                   text.Substring(1);
        }
    }
}
