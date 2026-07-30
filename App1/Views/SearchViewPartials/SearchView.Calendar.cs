using Anfeta.UI.Models.Notion;
using Anfeta.UI.Services.Notion;
using Microsoft.UI;
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
        private const int CalendarPeopleSelectionVersion = 3;

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
        private bool _calendarSizeHandlerHooked;
        private double _calendarLastViewportWidth;
        private string _calendarSearchQuery = string.Empty;
        private string _calendarPhaseFilter = string.Empty;
        private bool _calendarReviewAlertSending;

        private ComboBox? CalendarPhaseFilterControl =>
            FindName("CalendarPhaseFilterCombo") as ComboBox;
        private string? _calendarPreviousSearchPlaceholder;
        private DispatcherTimer? _calendarChangesTimer;
        private DateTimeOffset _calendarLastChangesCheckUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        private bool _calendarChangesRefreshRunning;

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

            _calendarViewActive = true;
            _calendarSelectedDate = date.Date;

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
                _calendarActivities = cached;

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
                    Interval = TimeSpan.FromSeconds(25)
                };

                _calendarChangesTimer.Tick += async (_, __) =>
                    await RefreshCalendarChangesSilentlyAsync();
            }

            _calendarChangesTimer.Stop();
            _calendarChangesTimer.Start();

            // Al entrar al calendario se valida inmediatamente sin bloquear la UI.
            _ = RefreshCalendarChangesSilentlyAsync();
        }

        private void StopCalendarChangesTimer()
        {
            _calendarChangesTimer?.Stop();
        }

        private async Task RefreshCalendarChangesSilentlyAsync()
        {
            if (!_calendarViewActive || _calendarChangesRefreshRunning)
                return;

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
                return;

            _calendarChangesRefreshRunning = true;

            // Se deja margen para no perder cambios que Notion publique
            // con unos segundos de retraso en last_edited_time.
            var changedAfter =
                _calendarLastChangesCheckUtc.Subtract(
                    TimeSpan.FromSeconds(20));

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var changed =
                    await _notionCalendarService.RefreshChangedSinceAsync(
                        token,
                        changedAfter,
                        cts.Token);

                _calendarLastChangesCheckUtc =
                    DateTimeOffset.UtcNow;

                if (!changed || !_calendarViewActive)
                    return;

                var cached =
                    await _notionCalendarService.TryGetCachedDayAsync(
                        _calendarSelectedDate,
                        cts.Token);

                if (cached == null)
                    return;

                _calendarActivities = cached;
                DrawCalendar(_calendarActivities);

                StatusText.Text =
                    "Estado: Cambios recientes del calendario aplicados automáticamente ✅";
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // La validación automática nunca debe vaciar la vista ni
                // interrumpir el uso normal del calendario.
            }
            finally
            {
                _calendarChangesRefreshRunning = false;
            }
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
            StopCalendarChangesTimer();

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

            CalendarDateTitle.Text =
                FormatCalendarDate(_calendarSelectedDate);

            DrawCalendar(_calendarActivities);

            // Valida cambios externos al cambiar de día sin esperar al botón Actualizar.
            _ = RefreshCalendarChangesSilentlyAsync();

            await LoadCalendarDayAsync(
                preferCache: true);
        }

        private async void CalendarRefresh_Click(
            object sender,
            RoutedEventArgs e)
        {
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
                    DrawCalendar(_calendarActivities);

                    ModeText.Text =
                        "Modo: Calendario (Revisiones)";

                    CountText.Text =
                        $"{cached.Count} actividades";

                    StatusText.Text =
                        "Estado: Calendario cargado desde caché ✅";

                    // La caché se muestra al instante, pero se valida en segundo
                    // plano para traer revisiones, estados y colores recientes.
                    _ = RefreshCalendarDaySilentlyAsync(
                        requestedDate,
                        loadVersion);

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
                DrawCalendar(_calendarActivities);

                ModeText.Text =
                    "Modo: Calendario (Revisiones)";

                CountText.Text =
                    $"{activities.Count} actividades";

                StatusText.Text =
                    activities.Count > 0
                        ? $"Estado: Calendario actualizado ✅ ({activities.Count}) · {_notionCalendarService.LastDiagnostics}"
                        : $"Estado: Calendario sin coincidencias · {_notionCalendarService.LastDiagnostics}";
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

                    return;
                }

                _calendarActivities =
                    Array.Empty<NotionCalendarActivity>();

                DrawCalendar(_calendarActivities);

                CalendarEmptyText.Text =
                    "La consulta tardó demasiado y fue cancelada. " +
                    "La cuadrícula permanece disponible; pulsa Actualizar para intentarlo de nuevo.";

                CalendarEmptyState.Visibility =
                    Visibility.Visible;

                ModeText.Text =
                    "Modo: Calendario (Revisiones)";

                StatusText.Text =
                    "Estado: Carga del calendario cancelada por tiempo de espera.";
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
            }
            finally
            {
                if (loadVersion == _calendarLoadVersion)
                    HideLoadingState();
            }
        }

        private async Task RefreshCalendarDaySilentlyAsync(
            DateTime requestedDate,
            long loadVersion)
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
                        TimeSpan.FromMinutes(8));

                var activities =
                    await _notionCalendarService.GetDayAsync(
                        token,
                        requestedDate,
                        progress: null,
                        cts.Token,
                        forceRefresh: true);

                if (!_calendarViewActive ||
                    requestedDate != _calendarSelectedDate.Date ||
                    loadVersion != _calendarLoadVersion)
                {
                    return;
                }

                _calendarActivities = activities;
                DrawCalendar(_calendarActivities);

                CountText.Text =
                    $"{activities.Count} actividades";

                StatusText.Text =
                    $"Estado: Calendario actualizado ✅ ({activities.Count})";
            }
            catch
            {
                // La caché ya está visible; una validación silenciosa fallida
                // no debe vaciar el calendario ni mostrar overlay.
            }
        }

        private void DrawCalendar(
            IReadOnlyList<NotionCalendarActivity> activities)
        {
            var phaseActivities =
                string.IsNullOrWhiteSpace(
                    _calendarPhaseFilter)
                    ? activities
                    : activities
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

                var headerButton = new Button
                {
                    Content = person,
                    Width = columnWidth - 8,
                    Height = headerHeight - 4,
                    Padding = new Thickness(8, 0, 8, 0),
                    HorizontalContentAlignment =
                        HorizontalAlignment.Left,
                    VerticalContentAlignment =
                        VerticalAlignment.Center,
                    FontSize = 14 * CalendarFontScale,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Background =
                        new SolidColorBrush(
                            Darken(_calendarThemeColor, 0.02)),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    Tag = person
                };

                headerButton.ContextFlyout =
                    BuildCalendarHeaderContextFlyout(person);

                Canvas.SetLeft(headerButton, left + 2);
                Canvas.SetTop(headerButton, 2);
                Canvas.SetZIndex(headerButton, 300);
                CalendarCanvas.Children.Add(headerButton);
                _calendarStickyHeaders.Add(headerButton);

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
        }

        private static bool IsPendingReviewActivity(
            NotionCalendarActivity activity)
        {
            if (activity == null)
                return false;

            var searchable = string.Join(
                " ",
                new[]
                {
                    activity.Title,
                    activity.Project,
                    activity.Status,
                    activity.UpdateText,
                    activity.Description
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));

            // Solo rtuzREVISION se considera pendiente para estas vistas.
            // No coincide con prtuzREVISION, zREVISION ni con fragmentos.
            return Regex.IsMatch(
                searchable,
                @"(?<![\p{L}\p{Nd}_])rtuzREVISION(?![\p{L}\p{Nd}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
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

            button.Click += CalendarActivity_Click;

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

            flyout.Items.Add(
                new MenuFlyoutSeparator());

            AddItem(
                "Renombrar página…",
                CalendarContextRename_Click);

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

        private async Task OpenCalendarActivityAsync(
            NotionCalendarActivity activity)
        {
            if (string.IsNullOrWhiteSpace(activity.PageUrl) ||
                !Uri.TryCreate(
                    activity.PageUrl,
                    UriKind.Absolute,
                    out var webUri))
            {
                return;
            }

            try
            {
                var desktopUri =
                    new Uri(
                        activity.PageUrl.Replace(
                            "https://",
                            "notion://",
                            StringComparison.OrdinalIgnoreCase));

                var support =
                    await Launcher.QueryUriSupportAsync(
                        desktopUri,
                        LaunchQuerySupportType.Uri);

                if (support ==
                    LaunchQuerySupportStatus.Available &&
                    await Launcher.LaunchUriAsync(desktopUri))
                {
                    return;
                }

                await Launcher.LaunchUriAsync(webUri);
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir la actividad → {ex.Message}";
            }
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
                ("iisaia", "Isaias"), ("isaias", "Isaias"),
                ("ssote", "Sotelo"), ("sotelo", "Sotelo"),
                ("sote", "Sotelo"), ("eedua", "Sotelo"),
                ("eduardo", "Sotelo"), ("edua", "Sotelo"),
                ("aacal", "Acalli"), ("acalli", "Acalli"),
                ("acali", "Acalli"), ("acal", "Acalli"),
                ("aandr", "Andrade"), ("andrade", "Andrade"),
                ("eemma", "Emmanuel"), ("emmanuel", "Emmanuel"),
                ("bbria", "Brian"), ("brian", "Brian"),
                ("ggena", "Genaro"), ("genaro", "Genaro"),
                ("nnetf", "Neftali"), ("nneft", "Neftali"),
                ("neftali", "Neftali"), ("neft", "Neftali")
            };

            foreach (var (alias, person) in aliases)
            {
                if (clean.Contains(alias))
                    return person;
            }

            return "Sin asignar";
        }

        private async void CalendarActivity_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not NotionCalendarActivity activity)
            {
                return;
            }

            var clickIsSuppressed =
                _calendarSuppressNextActivityClick ||
                (DateTimeOffset.UtcNow <= _calendarSuppressActivityClickUntil &&
                 (string.IsNullOrWhiteSpace(_calendarSuppressedActivityPageId) ||
                  string.Equals(
                      _calendarSuppressedActivityPageId,
                      activity.PageId,
                      StringComparison.OrdinalIgnoreCase)));

            if (clickIsSuppressed)
            {
                _calendarSuppressNextActivityClick = false;
                _calendarSuppressActivityClickUntil = DateTimeOffset.MinValue;
                _calendarSuppressedActivityPageId = string.Empty;
                return;
            }

            await OpenCalendarActivityAsync(activity);
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
                    "Clic en la actividad para abrir la página en Notion.");
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
            string statusText)
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

            if (IsPendingReviewActivity(activity))
            {
                var alertButton =
                    new Button
                    {
                        Content = "🔔 Enviar alerta de revisión",
                        Width = 220,
                        Height = 30,
                        Margin = new Thickness(0, 0, 8, 6),
                        Padding = new Thickness(10, 0, 10, 0),
                        CornerRadius = new CornerRadius(6),
                        Tag = activity
                    };

                alertButton.Click +=
                    async (_, __) =>
                    {
                        alertButton.IsEnabled = false;

                        try
                        {
                            await SendCalendarReviewAlertAsync(
                                activity);

                            alertButton.Content =
                                "Alerta enviada ✅";
                        }
                        finally
                        {
                            await Task.Delay(1200);
                            alertButton.IsEnabled = true;
                            alertButton.Content =
                                "🔔 Enviar alerta de revisión";
                        }
                    };

                actionsPanel.Children.Add(
                    alertButton);
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
                    Tag = activity
                };

                button.Click += async (_, __) =>
                    await MoveCalendarActivityByDaysAsync(
                        activity,
                        days);

                actionsPanel.Children.Add(button);
                return button;
            }

            AddMoveButton("Mover a mañana", 1);
            AddMoveButton("+3 días", 3);
            AddMoveButton("+1 semana", 7);

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

        private async Task SendCalendarReviewAlertAsync(
            NotionCalendarActivity activity)
        {
            if (_calendarReviewAlertSending)
                return;

            if (!IsPendingReviewActivity(activity))
            {
                StatusText.Text =
                    "Estado: La alerta solo está disponible para rtuzREVISION.";
                return;
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
                return;
            }

            _calendarReviewAlertSending = true;

            try
            {
                StatusText.Text =
                    "Estado: Enviando alerta a John y Genaro...";

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

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

                var senderTag =
                    (values["Messaging.CurrentUserTag"] as string ??
                     string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(senderTag))
                    senderTag = "anfeta";

                var now =
                    DateTimeOffset.Now;

                var responsible =
                    (activity.Person ?? string.Empty).Trim();

                var messageParts = new[]
                {
                    "Actividad lista para revisión",
                    responsible,
                    activity.TimeLabel,
                    activity.Title
                }
                .Where(value => !string.IsNullOrWhiteSpace(value));

                var message = string.Join(" · ", messageParts);

                foreach (var recipient in
                         new[] { "jjohn", "ggena" })
                {
                    var title =
                        $"{now:yyyy-MM-dd HH:mm} " +
                        $"{recipient} de:{senderTag} [RESPUESTA] " +
                        $"{message}";

                    await CreateReviewAlertPageAsync(
                        http,
                        dataSourceId,
                        titlePropertyName,
                        title,
                        activity,
                        cts.Token);
                }

                StatusText.Text =
                    "Estado: Alerta enviada a John y Genaro ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo enviar la alerta → {ex.Message}";
            }
            finally
            {
                _calendarReviewAlertSending = false;
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

        private static async Task CreateReviewAlertPageAsync(
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
        }

        private async Task MoveCalendarActivityByDaysAsync(
            NotionCalendarActivity activity,
            int days)
        {
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
