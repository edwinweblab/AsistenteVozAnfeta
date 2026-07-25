using Anfeta.UI.Models.Notion;
using Anfeta.UI.Services.Notion;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private const int CalendarEndHour = 19;
        private const string LS_CalendarZoom = "Search.Calendar.Zoom";
        private const string LS_CalendarPeople = "Search.Calendar.People";
        private const string LS_CalendarOrder = "Search.Calendar.Order";
        private const string LS_CalendarColumnWidths =
            "Search.Calendar.ColumnWidths";

        private static readonly string[] ActiveCalendarPeople =
        {
            "John",
            "Karla",
            "Isaias",
            "Eduardo",
            "Acalli",
            "Andrade",
            "Emmanuel",
            "Brian",
            "Genaro",
            "Neftali"
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
                await ShowCalendarAsync(_calendarSelectedDate);
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

            EnsureCalendarWheelHandler();
            ApplyCalendarTheme(_calendarThemeColor);

            var cached =
                await _notionCalendarService.TryGetCachedDayAsync(
                    _calendarSelectedDate);

            if (cached != null)
                _calendarActivities = cached;

            DrawCalendar(_calendarActivities);

            await LoadCalendarDayAsync(
                preferCache: true);
        }

        private void CloseCalendarView()
        {
            _calendarViewActive = false;

            try
            {
                _calendarCts?.Cancel();
            }
            catch
            {
            }

            CalendarHost.Visibility = Visibility.Collapsed;
            ToggleCalendarView.IsChecked = false;

            ModeText.Text = $"Modo: Buscar ({GetSourceScopeLabel()})";
            CountText.Text = $"{Results.Count} resultados";
        }

        private void CalendarClose_Click(object sender, RoutedEventArgs e)
            => CloseCalendarView();

        private async void CalendarPreviousDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            _calendarSelectedDate = _calendarSelectedDate.AddDays(-1);
            await LoadCalendarDayAsync(preferCache: true);
        }

        private async void CalendarNextDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            _calendarSelectedDate = _calendarSelectedDate.AddDays(1);
            await LoadCalendarDayAsync(preferCache: true);
        }

        private async void CalendarToday_Click(
            object sender,
            RoutedEventArgs e)
        {
            _calendarSelectedDate = DateTime.Today;
            await LoadCalendarDayAsync(preferCache: true);
        }

        private async void CalendarRefresh_Click(
            object sender,
            RoutedEventArgs e)
        {
            await LoadCalendarDayAsync();
        }

        private async Task LoadCalendarDayAsync(
            bool preferCache = true,
            bool forceRefresh = false)
        {
            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            CalendarDateTitle.Text =
                FormatCalendarDate(_calendarSelectedDate);

            DrawCalendar(_calendarActivities);
            CalendarEmptyState.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(token))
            {
                CalendarEmptyText.Text =
                    "Configura primero el token de Notion.";
                CalendarEmptyState.Visibility = Visibility.Visible;
                return;
            }

            if (preferCache && !forceRefresh)
            {
                var cached =
                    await _notionCalendarService.TryGetCachedDayAsync(
                        _calendarSelectedDate);

                if (cached != null)
                {
                    _calendarActivities = cached;
                    DrawCalendar(_calendarActivities);

                    ModeText.Text =
                        "Modo: Calendario (Revisiones)";

                    CountText.Text =
                        $"{cached.Count} actividades";

                    StatusText.Text =
                        "Estado: Calendario cargado desde caché ✅";

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

            var cancellationToken = _calendarCts.Token;

            try
            {
                ShowLoadingState(
                    "Estado: Cargando calendario de Revisiones...",
                    FormatCalendarDate(_calendarSelectedDate));

                var progress =
                    new Progress<NotionCalendarProgress>(
                        report =>
                        {
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
                        _calendarSelectedDate,
                        progress,
                        cancellationToken,
                        forceRefresh);

                if (!_calendarViewActive)
                    return;

                _calendarActivities = activities;
                DrawCalendar(_calendarActivities);

                ModeText.Text = "Modo: Calendario (Revisiones)";
                CountText.Text = $"{activities.Count} actividades";

                StatusText.Text =
                    activities.Count > 0
                        ? $"Estado: Calendario actualizado ✅ ({activities.Count})"
                        : $"Estado: Calendario sin coincidencias · {_notionCalendarService.LastDiagnostics}";
            }
            catch (OperationCanceledException)
            {
                DrawCalendar(_calendarActivities);

                CalendarEmptyText.Text =
                    "La consulta tardó demasiado y fue cancelada. " +
                    "La cuadrícula permanece disponible; pulsa Actualizar para intentarlo de nuevo.";

                CalendarEmptyState.Visibility = Visibility.Visible;
                ModeText.Text = "Modo: Calendario (Revisiones)";
                StatusText.Text =
                    "Estado: Carga del calendario cancelada por tiempo de espera.";
            }
            catch (Exception ex)
            {
                DrawCalendar(_calendarActivities);

                CalendarEmptyText.Text =
                    $"No se pudo cargar el calendario.\n{ex.Message}";

                CalendarEmptyState.Visibility = Visibility.Visible;
                StatusText.Text =
                    $"Estado: Error en calendario → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private void DrawCalendar(
            IReadOnlyList<NotionCalendarActivity> activities)
        {
            CalendarCanvas.Children.Clear();
            _calendarStickyHeaders.Clear();
            _calendarStickyHours.Clear();
            _calendarStickyCorner = null;

            CalendarEmptyState.Visibility =
                activities.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            CalendarEmptyText.Text =
                "No hay actividades programadas para este día." +
                (string.IsNullOrWhiteSpace(
                    _notionCalendarService.LastDiagnostics)
                    ? string.Empty
                    : $"\n\n{_notionCalendarService.LastDiagnostics}");

            var persons = _calendarPeopleOrder
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

            var filteredActivities = activities
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
                Background = GetActivityBrush(activity.Status),
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
                BuildActivityToolTip(activity));

            button.Click += CalendarActivity_Click;

            Canvas.SetLeft(button, left);
            Canvas.SetTop(button, top);
            Canvas.SetZIndex(button, 10);
            CalendarCanvas.Children.Add(button);
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

            flyout.Items.Add(moveLeft);
            flyout.Items.Add(moveRight);
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
                        TimeSpan.FromMinutes(10));

                await _notionCalendarService.PreloadDayAsync(
                    token,
                    DateTime.Today,
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
                ("eedua", "Eduardo"), ("eduardo", "Eduardo"),
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

            return string.Empty;
        }

        private async void CalendarActivity_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not NotionCalendarActivity activity ||
                string.IsNullOrWhiteSpace(activity.PageUrl) ||
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

        private static Brush GetActivityBrush(string status)
        {
            var normalized =
                (status ?? string.Empty).ToLowerInvariant();

            var color =
                normalized.Contains("termin")
                    ? Color.FromArgb(230, 30, 110, 72)
                    : normalized.Contains("bloq")
                        ? Color.FromArgb(230, 125, 56, 56)
                        : normalized.Contains("proceso") ||
                          normalized.Contains("trabaj")
                            ? Color.FromArgb(230, 48, 92, 145)
                            : Color.FromArgb(230, 71, 77, 102);

            return new SolidColorBrush(color);
        }

        private static string BuildActivityToolTip(
            NotionCalendarActivity activity)
        {
            var parts = new List<string>
            {
                activity.Title,
                activity.TimeLabel,
                $"Persona: {activity.Person}"
            };

            if (!string.IsNullOrWhiteSpace(activity.Project))
                parts.Add($"Proyecto: {activity.Project}");

            if (!string.IsNullOrWhiteSpace(activity.Status))
                parts.Add($"Estado: {activity.Status}");

            parts.Add("Clic para abrir en Notion");

            return string.Join(Environment.NewLine, parts);
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
                : Array.Empty<string>();
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
