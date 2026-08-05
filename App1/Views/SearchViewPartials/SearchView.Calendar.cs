using Anfeta.UI.Models.Notion;
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
using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const int CalendarPeopleSelectionVersion = 4;

        private static readonly string[] ActiveCalendarPeople =
        {
            "John",
            "Karla",
            "Isaias",
            "Sotelo",
            "Acalli",
            "Andrade",
            "Emmanuel",
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

        private async Task ShowCalendarAsync(DateTime date)
        {
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

                _calendarSearchQuery =
                    (SearchBox.Text ?? string.Empty).Trim();
            }

            EnsureCalendarWheelHandler();
            EnsureCalendarSizeHandler();
            ApplyCalendarTheme(_calendarThemeColor);

            if (CalendarPhaseFilterControl != null)
                CalendarPhaseFilterControl.Visibility = Visibility.Visible;
            StartCalendarChangesTimer();

            // El ancho real del ScrollViewer todavía puede no estar disponible
            // durante el primer render. Se repinta una vez que termina el layout.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_calendarViewActive)
                    DrawCalendar(_calendarActivities);
            });

            var cached =
                await _notionCalendarService.TryGetCachedDayAsync(
                    _calendarSelectedDate);

            if (cached != null)
            {
                _calendarActivities = cached;

                ApplyCachedCalendarReviewFlow(
                    _calendarActivities);
            }

            DrawCalendar(_calendarActivities);

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
                    await RefreshCalendarChangesSilentlyAsync();
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

            var cached =
                await _notionCalendarService.TryGetCachedDayAsync(
                    _calendarSelectedDate);

            _calendarActivities =
                cached ??
                Array.Empty<NotionCalendarActivity>();

            ApplyCachedCalendarReviewFlow(
                _calendarActivities);

            CalendarDateTitle.Text =
                FormatCalendarDate(_calendarSelectedDate);

            DrawCalendar(_calendarActivities);

            // LoadCalendarDayAsync muestra la caché y programa una sola
            // validación incremental para el nuevo día.
            await LoadCalendarDayAsync(
                preferCache: true);
        }

        private async void CalendarRefresh_Click(
            object sender,
            RoutedEventArgs e)
        {
            // Actualizar usa la ruta incremental para responder rápido.
            // Ctrl + Actualizar conserva una recarga completa para diagnóstico.
            if (IsCalendarControlDown())
            {
                await LoadCalendarDayAsync(
                    preferCache: false,
                    forceRefresh: true);

                return;
            }

            await RefreshCalendarDaySilentlyAsync(
                _calendarSelectedDate.Date,
                _calendarLoadVersion,
                userInitiated: true);
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
                    ApplyCachedCalendarReviewFlow(
                        _calendarActivities);
                    DrawCalendar(_calendarActivities);

                    ModeText.Text =
                        "Modo: Calendario (Revisiones)";
                    CountText.Text =
                        $"{cached.Count} actividades";
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

                _calendarActivities = activities;

                await HydrateCalendarReviewFlowAsync(
                    _calendarActivities,
                    cancellationToken,
                    processVersion);

                DrawCalendar(_calendarActivities);
                SaveCalendarChangesCheckpoint();

                ModeText.Text =
                    "Modo: Calendario (Revisiones)";
                CountText.Text =
                    $"{activities.Count} actividades";

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
                    CountText.Text =
                        $"{cached.Count} actividades";
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
                        "Pulsa Ctrl + Actualizar para realizar una recarga completa.",
                        success: false);
                    return;
                }

                if (changed)
                {
                    await HydrateCalendarReviewFlowAsync(
                        activities,
                        cts.Token,
                        processVersion);

                    var incomingFingerprint =
                        BuildCalendarVisualFingerprint(activities);

                    var currentFingerprint =
                        BuildCalendarVisualFingerprint(
                            _calendarActivities);

                    if (!string.Equals(
                            incomingFingerprint,
                            currentFingerprint,
                            StringComparison.Ordinal))
                    {
                        _calendarActivities = activities;
                        DrawCalendarPreservingView(
                            _calendarActivities);
                    }
                }

                CountText.Text =
                    $"{activities.Count} actividades";

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
                            item.UpdateText)));
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
            _calendarStickyHeaders.Clear();
            _calendarStickyHours.Clear();
            _calendarStickyCorner = null;

            CalendarEmptyState.Visibility =
                visibleActivities.Count == 0
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

                var headerButton = new Button
                {
                    Content = person,
                    Padding = new Thickness(8, 0, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontSize = 14 * CalendarFontScale,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    Tag = person
                };

                var previewButton = new Button
                {
                    Content = "👁",
                    Width = 32,
                    Height = Math.Max(28, headerHeight - 12),
                    Margin = new Thickness(0, 4, 4, 4),
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontSize = 12 * CalendarFontScale,
                    Background =
                        new SolidColorBrush(
                            Lighten(_calendarThemeColor, 0.06)),
                    BorderBrush =
                        new SolidColorBrush(
                            Lighten(_calendarThemeColor, 0.20)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Tag = person
                };

                ToolTipService.SetToolTip(
                    previewButton,
                    $"Ver actividades de {person}");

                previewButton.Click +=
                    CalendarPersonPreview_Click;

                var headerContextFlyout =
                    BuildCalendarHeaderContextFlyout(person);

                headerButton.ContextFlyout = headerContextFlyout;

                Grid.SetColumn(headerButton, 0);
                headerContainer.Children.Add(headerButton);

                Grid.SetColumn(previewButton, 1);
                headerContainer.Children.Add(previewButton);

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

            DrawCurrentTimeLine(headerHeight);
            UpdateCalendarStickyElements();

            CalendarZoomText.Text =
                $"{Math.Round(_calendarZoom * 100):0}%";

            if (_calendarViewActive)
            {
                CountText.Text =
                    $"{visibleActivities.Count} de {activities.Count} actividades";

                var phaseLabel =
                    string.IsNullOrWhiteSpace(_calendarPhaseFilter)
                        ? "Todas"
                        : _calendarPhaseFilter;

                ModeText.Text =
                    string.IsNullOrWhiteSpace(_calendarSearchQuery)
                        ? $"Modo: Calendario · {phaseLabel}"
                        : $"Modo: Calendario · {phaseLabel} · {_calendarSearchQuery}";
            }

            _calendarLastVisualFingerprint =
                BuildCalendarVisualFingerprint(activities);

            RefreshCalendarPersonPreviewIfOpen();
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
                    activity.Description
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
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
                        part.IsExact
                            ? ContainsExactCalendarPart(
                                searchable,
                                part.Value)
                            : searchable.Contains(
                                part.Value,
                                StringComparison.OrdinalIgnoreCase));
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
            var lanes = new List<DateTime>();

            foreach (var activity in items)
            {
                var lane = 0;

                while (lane < lanes.Count &&
                       activity.Start < lanes[lane])
                {
                    lane++;
                }

                if (lane == lanes.Count)
                    lanes.Add(activity.End);
                else
                    lanes[lane] = activity.End;

                var overlappingCount = Math.Max(
                    1,
                    items.Count(other =>
                        other.Start < activity.End &&
                        other.End > activity.Start));

                AddActivityButton(
                    activity,
                    person,
                    headerHeight,
                    lane,
                    overlappingCount);
            }
        }

        private void AddActivityButton(
            NotionCalendarActivity activity,
            string person,
            double headerHeight,
            int lane,
            int laneCount)
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

            // Nunca se usa un ancho mínimo que pueda desbordar la columna.
            // Si hay muchas actividades simultáneas, se compactan dentro
            // del espacio disponible de esa misma persona.
            var laneWidth =
                usableWidth / safeLaneCount;

            var safeLane =
                Math.Clamp(lane, 0, safeLaneCount - 1);

            var left =
                columnLeft +
                5 +
                safeLane * laneWidth;

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
                FontSize = 10.5 * CalendarFontScale,
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold,
                MaxLines = _calendarZoom < 0.85 ? 1 : 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false
            };

            var timeText = new TextBlock
            {
                Text = activity.TimeLabel,
                FontSize = 9.5 * CalendarFontScale,
                Opacity = 0.82,
                IsHitTestVisible = false
            };

            var content = new StackPanel
            {
                Spacing = Math.Max(1, 2 * _calendarZoom),
                IsHitTestVisible = false
            };

            content.Children.Add(titleText);
            content.Children.Add(timeText);

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
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Opacity = 0.88,
                        MaxLines = 2,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    });
            }

            if (_calendarZoom >= 0.90 &&
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

            var button = new Button
            {
                Content = content,
                Width = Math.Max(
                    8,
                    laneWidth - 4),
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
                Background = GetActivityBrush(
                    activity.Status,
                    activity.StatusColor),
                BorderBrush =
                    new SolidColorBrush(
                        Color.FromArgb(110, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius =
                    new CornerRadius(6 * _calendarZoom),
                Tag = activity
            };

            ToolTipService.SetToolTip(
                button,
                null);

            button.KeyboardAccelerators.Clear();
            button.KeyboardAcceleratorPlacementMode =
                KeyboardAcceleratorPlacementMode.Hidden;

            button.PointerEntered +=
                CalendarActivity_PointerEntered;

            button.PointerExited +=
                CalendarActivity_PointerExited;

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

            button.DoubleTapped += CalendarActivity_DoubleTapped;

            Canvas.SetLeft(button, left);
            Canvas.SetTop(button, top);
            Canvas.SetZIndex(button, 10);
            CalendarCanvas.Children.Add(button);
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

        private void CalendarPersonPreview_Click(
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

        private MenuFlyout BuildCalendarHeaderContextFlyout(
            string person)
        {
            var flyout = new MenuFlyout();

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

                        CountText.Text =
                            $"{_calendarActivities.Count} actividades";

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
                Project = activity.Project,
                Status = activity.Status,
                StatusColor = activity.StatusColor,
                UpdateText = activity.UpdateText,
                Description = activity.Description,
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

            var alert =
                await SendCalendarReviewAlertAsync(
                    activity,
                    reviewer,
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
                    Tag = activity
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

            var targetDate =
                activity.Start.Date.AddDays(days);

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
