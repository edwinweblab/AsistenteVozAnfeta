using Anfeta.UI.Models.Weblab;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private const double RemindersCalendarHourHeight = 52d;
        private const double RemindersCalendarTimeColumnWidth = 68d;
        private const double RemindersCalendarPersonColumnWidth = 168d;
        private const double RemindersCalendarHeaderHeight = 44d;

        private static readonly string[] RemindersCalendarPeople =
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
            "Todos"
        };

        private const string LS_RemindersCalendarPeople =
            "Search.RemindersCalendar.People.v1";

        private readonly HashSet<string>
            _remindersCalendarSelectedPeople =
                new(
                    RemindersCalendarPeople,
                    StringComparer.OrdinalIgnoreCase);

        private bool _remindersCalendarPeopleLoaded;

        private bool _remindersCalendarViewActive;
        private bool _remindersCalendarAutoScrollPending;
        private DateTime _remindersCalendarSelectedDate = DateTime.Today;
        private DispatcherTimer? _remindersCalendarRefreshTimer;
        private bool _remindersCalendarPointerGuideHooked;
        private Border? _remindersCalendarHoverSlot;
        private Border? _remindersCalendarHoverBadge;
        private TextBlock? _remindersCalendarHoverBadgeText;


        // Controles nuevos resueltos desde el NameScope de SearchView.
        // Evita depender de campos generados por XAML para este bloque nuevo.
        private ToggleButton? RemindersCalendarToggleControl =>
            FindName("ToggleRemindersCalendarView") as ToggleButton;

        private Border? RemindersCalendarBadgeControl =>
            FindName("RemindersCalendarBadge") as Border;

        private TextBlock? RemindersCalendarBadgeTextControl =>
            FindName("RemindersCalendarBadgeText") as TextBlock;

        private Grid? RemindersCalendarHostControl =>
            FindName("RemindersCalendarHost") as Grid;

        private TextBlock? RemindersCalendarDateTitleControl =>
            FindName("RemindersCalendarDateTitle") as TextBlock;

        private TextBlock? RemindersCalendarSummaryTextControl =>
            FindName("RemindersCalendarSummaryText") as TextBlock;

        private ScrollViewer? RemindersCalendarScrollViewerControl =>
            FindName("RemindersCalendarScrollViewer") as ScrollViewer;

        private Canvas? RemindersCalendarCanvasControl =>
            FindName("RemindersCalendarCanvas") as Canvas;

        private Border? RemindersCalendarEmptyStateControl =>
            FindName("RemindersCalendarEmptyState") as Border;

        private TextBlock? RemindersCalendarEmptyTextControl =>
            FindName("RemindersCalendarEmptyText") as TextBlock;

        private async void ToggleRemindersCalendarView_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (RemindersCalendarToggleControl.IsChecked == true)
            {
                await ShowRemindersCalendarAsync(DateTime.Today);
            }
            else
            {
                CloseRemindersCalendarView();
            }
        }

        private Task ShowRemindersCalendarAsync(
            DateTime date)
        {
            ClearSearchForModuleSwitch();
            InitializeMessagesView();

            if (_calendarViewActive)
                CloseCalendarView();

            if (_messagesViewActive)
                CloseMessagesView();

            LoadRemindersCalendarPeopleSelection();

            _remindersCalendarViewActive = true;
            _remindersCalendarSelectedDate = date.Date;
            _remindersCalendarAutoScrollPending = true;

            SaveLastSearchSubView("reminders");

            RemindersCalendarHostControl.Visibility =
                Visibility.Visible;

            RemindersCalendarToggleControl.IsChecked = true;

            EnsureRemindersCalendarPointerGuide();
            EnsureRemindersCalendarRefreshTimer();
            DrawRemindersCalendar();
            UpdateRemindersCalendarBadge();

            StatusText.Text =
                "Estado: Calendario de recordatorios abierto ✅";

            return Task.CompletedTask;
        }

        private void CloseRemindersCalendarView()
        {
            if (!_remindersCalendarViewActive &&
                RemindersCalendarHostControl?.Visibility !=
                    Visibility.Visible)
            {
                if (RemindersCalendarToggleControl != null)
                    RemindersCalendarToggleControl.IsChecked = false;

                return;
            }

            _remindersCalendarViewActive = false;
            _remindersCalendarRefreshTimer?.Stop();
            HideRemindersCalendarPointerGuide();

            if (RemindersCalendarHostControl != null)
            {
                RemindersCalendarHostControl.Visibility =
                    Visibility.Collapsed;
            }

            if (RemindersCalendarToggleControl != null)
            {
                RemindersCalendarToggleControl.IsChecked = false;
            }

            SaveLastSearchSubView("results");

            ModeText.Text =
                $"Modo: Buscar ({GetSourceScopeLabel()})";

            CountText.Text =
                $"{Results.Count} resultados";
        }

        private void LoadRemindersCalendarPeopleSelection()
        {
            if (_remindersCalendarPeopleLoaded)
                return;

            _remindersCalendarPeopleLoaded = true;

            var values =
                ApplicationData.Current.LocalSettings.Values;

            if (!values.ContainsKey(
                    LS_RemindersCalendarPeople))
            {
                _remindersCalendarSelectedPeople.Clear();

                foreach (var person in RemindersCalendarPeople)
                    _remindersCalendarSelectedPeople.Add(person);

                return;
            }

            try
            {
                var raw =
                    values[
                        LS_RemindersCalendarPeople] as string;

                var restored =
                    string.IsNullOrWhiteSpace(raw)
                        ? Array.Empty<string>()
                        : JsonSerializer.Deserialize<string[]>(raw) ??
                          Array.Empty<string>();

                _remindersCalendarSelectedPeople.Clear();

                foreach (var person in restored)
                {
                    if (RemindersCalendarPeople.Contains(
                            person,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        _remindersCalendarSelectedPeople.Add(
                            person);
                    }
                }
            }
            catch
            {
                _remindersCalendarSelectedPeople.Clear();

                foreach (var person in RemindersCalendarPeople)
                    _remindersCalendarSelectedPeople.Add(person);
            }
        }

        private void SaveRemindersCalendarPeopleSelection()
        {
            try
            {
                var selected =
                    RemindersCalendarPeople
                        .Where(person =>
                            _remindersCalendarSelectedPeople
                                .Contains(person))
                        .ToArray();

                ApplicationData.Current.LocalSettings.Values[
                    LS_RemindersCalendarPeople] =
                    JsonSerializer.Serialize(selected);
            }
            catch
            {
            }
        }

        private IReadOnlyList<string>
            GetVisibleRemindersCalendarPeople()
        {
            LoadRemindersCalendarPeopleSelection();

            return RemindersCalendarPeople
                .Where(person =>
                    _remindersCalendarSelectedPeople
                        .Contains(person))
                .ToList();
        }

        private static int FindRemindersCalendarPersonIndex(
            IReadOnlyList<string> people,
            string person)
        {
            if (people == null)
                return -1;

            for (var index = 0;
                 index < people.Count;
                 index++)
            {
                if (string.Equals(
                        people[index],
                        person,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private void ResetRemindersCalendarHorizontalScroll()
        {
            var scrollViewer =
                RemindersCalendarScrollViewerControl;

            if (scrollViewer == null)
                return;

            var vertical =
                scrollViewer.VerticalOffset;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_remindersCalendarViewActive ||
                    RemindersCalendarScrollViewerControl == null)
                {
                    return;
                }

                RemindersCalendarScrollViewerControl.ChangeView(
                    0,
                    vertical,
                    null,
                    disableAnimation: true);
            });
        }

        private void RemindersCalendarPeople_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not FrameworkElement anchor)
                return;

            LoadRemindersCalendarPeopleSelection();

            var checkboxes =
                new Dictionary<string, CheckBox>(
                    StringComparer.OrdinalIgnoreCase);

            var list =
                new StackPanel
                {
                    Spacing = 4,
                    MinWidth = 220
                };

            foreach (var person in RemindersCalendarPeople)
            {
                var checkBox =
                    new CheckBox
                    {
                        Content = person,
                        IsChecked =
                            _remindersCalendarSelectedPeople
                                .Contains(person),
                        MinHeight = 28
                    };

                checkboxes[person] = checkBox;
                list.Children.Add(checkBox);
            }

            var selectAllButton =
                new Button
                {
                    Content = "Marcar todos",
                    Padding = new Thickness(9, 4, 9, 4)
                };

            var clearButton =
                new Button
                {
                    Content = "Ninguno",
                    Padding = new Thickness(9, 4, 9, 4)
                };

            var applyButton =
                new Button
                {
                    Content = "Aplicar",
                    Padding = new Thickness(12, 4, 12, 4)
                };

            selectAllButton.Click +=
                (_, __) =>
                {
                    foreach (var checkBox in checkboxes.Values)
                        checkBox.IsChecked = true;
                };

            clearButton.Click +=
                (_, __) =>
                {
                    foreach (var checkBox in checkboxes.Values)
                        checkBox.IsChecked = false;
                };

            var actions =
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Margin = new Thickness(0, 7, 0, 0)
                };

            actions.Children.Add(selectAllButton);
            actions.Children.Add(clearButton);
            actions.Children.Add(applyButton);

            var root =
                new StackPanel
                {
                    Spacing = 4,
                    Padding = new Thickness(5)
                };

            root.Children.Add(
                new TextBlock
                {
                    Text = "Personas visibles",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 3)
                });

            root.Children.Add(list);
            root.Children.Add(actions);

            var flyout =
                new Flyout
                {
                    Content = root
                };

            applyButton.Click +=
                (_, __) =>
                {
                    _remindersCalendarSelectedPeople.Clear();

                    foreach (var item in checkboxes)
                    {
                        if (item.Value.IsChecked == true)
                            _remindersCalendarSelectedPeople.Add(item.Key);
                    }

                    SaveRemindersCalendarPeopleSelection();
                    HideRemindersCalendarPointerGuide();

                    DrawRemindersCalendar();
                    ResetRemindersCalendarHorizontalScroll();

                    var visibleCount =
                        _remindersCalendarSelectedPeople.Count;

                    StatusText.Text =
                        visibleCount == 0
                            ? "Estado: Selecciona al menos una persona para ver recordatorios."
                            : $"Estado: Filtro de personas aplicado ✅ ({visibleCount})";

                    flyout.Hide();
                };

            flyout.ShowAt(anchor);
        }

        private void EnsureRemindersCalendarRefreshTimer()
        {
            if (_remindersCalendarRefreshTimer == null)
            {
                _remindersCalendarRefreshTimer =
                    new DispatcherTimer
                    {
                        Interval =
                            TimeSpan.FromSeconds(20)
                    };

                _remindersCalendarRefreshTimer.Tick +=
                    (_, __) =>
                    {
                        if (_remindersCalendarViewActive)
                            DrawRemindersCalendar();

                        UpdateRemindersCalendarBadge();
                    };
            }

            _remindersCalendarRefreshTimer.Stop();
            _remindersCalendarRefreshTimer.Start();
        }

        private void RemindersCalendarPreviousDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            _remindersCalendarSelectedDate =
                _remindersCalendarSelectedDate.AddDays(-1);
            _remindersCalendarAutoScrollPending = true;

            DrawRemindersCalendar();
        }

        private void RemindersCalendarToday_Click(
            object sender,
            RoutedEventArgs e)
        {
            _remindersCalendarSelectedDate =
                DateTime.Today;
            _remindersCalendarAutoScrollPending = true;

            DrawRemindersCalendar();
        }

        private void RemindersCalendarNextDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            _remindersCalendarSelectedDate =
                _remindersCalendarSelectedDate.AddDays(1);
            _remindersCalendarAutoScrollPending = true;

            DrawRemindersCalendar();
        }

        private void RemindersCalendarRefresh_Click(
            object sender,
            RoutedEventArgs e)
        {
            DrawRemindersCalendar();
            UpdateRemindersCalendarBadge();

            StatusText.Text =
                "Estado: Recordatorios actualizados desde el índice local ✅";
        }

        private async void RemindersCalendarNew_Click(
            object sender,
            RoutedEventArgs e)
        {
            var now =
                DateTimeOffset.Now;

            var selectedTime =
                _remindersCalendarSelectedDate.Date ==
                    DateTime.Today
                    ? now.TimeOfDay
                    : TimeSpan.FromHours(10);

            var suggested =
                new DateTimeOffset(
                    _remindersCalendarSelectedDate.Year,
                    _remindersCalendarSelectedDate.Month,
                    _remindersCalendarSelectedDate.Day,
                    selectedTime.Hours,
                    selectedTime.Minutes,
                    0,
                    now.Offset);

            await ShowNewMessageDialogAsync(
                new NewMessageComposerContext
                {
                    SuggestedAt = suggested
                });

            RefreshReminderCalendarViewsFromIndex();
        }

        private void RemindersCalendarClose_Click(
            object sender,
            RoutedEventArgs e)
        {
            CloseRemindersCalendarView();
        }

        private IReadOnlyList<MessageViewItem>
            GetReminderCalendarItems(
                DateTime day)
        {
            var currentUser =
                GetCurrentMessagesUserTag();

            if (string.IsNullOrWhiteSpace(currentUser))
                return Array.Empty<MessageViewItem>();

            return App.LocalIndex
                .GetAll()
                .Where(row =>
                    row != null &&
                    row.Source == SearchSource.Notion &&
                    string.Equals(
                        row.ExternalSourceName,
                        "Revisiones",
                        StringComparison.OrdinalIgnoreCase))
                .Select(TryCreateMessageViewItem)
                .Where(item =>
                    item != null &&
                    !item.IsReviewAlert &&
                    !item.IsReplyNotification &&
                    MessageBelongsToCurrentUser(
                        item.SenderTag,
                        item.RecipientTag,
                        currentUser) &&
                    item.ScheduledAt.LocalDateTime.Date ==
                        day.Date)
                .Cast<MessageViewItem>()
                .GroupBy(
                    item =>
                        !string.IsNullOrWhiteSpace(
                            item.Row.ExternalId)
                            ? item.Row.ExternalId
                            : item.Row.NodeId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.ScheduledAt)
                .ThenBy(item => item.RecipientName)
                .ThenBy(item => item.Message)
                .ToList();
        }

        private IReadOnlyList<MessageViewItem>
            GetAllPendingReminderCalendarItems()
        {
            var currentUser =
                GetCurrentMessagesUserTag();

            if (string.IsNullOrWhiteSpace(currentUser))
                return Array.Empty<MessageViewItem>();

            return App.LocalIndex
                .GetAll()
                .Where(row =>
                    row != null &&
                    row.Source == SearchSource.Notion &&
                    string.Equals(
                        row.ExternalSourceName,
                        "Revisiones",
                        StringComparison.OrdinalIgnoreCase))
                .Select(TryCreateMessageViewItem)
                .Where(item =>
                    item != null &&
                    !item.IsReviewAlert &&
                    !item.IsReplyNotification &&
                    !item.IsCompleted &&
                    MessageBelongsToCurrentUser(
                        item.SenderTag,
                        item.RecipientTag,
                        currentUser))
                .Cast<MessageViewItem>()
                .GroupBy(
                    item =>
                        !string.IsNullOrWhiteSpace(
                            item.Row.ExternalId)
                            ? item.Row.ExternalId
                            : item.Row.NodeId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static string GetReminderCalendarPersonName(
            string? recipientTag)
        {
            var normalized =
                NormalizeMessagesPersonTag(recipientTag);

            if (IsGroupMessageRecipient(normalized))
                return "Todos";

            return MessagesPeople.TryGetValue(
                    normalized,
                    out var name)
                ? name
                : "Sin asignar";
        }

        private static string GetReminderCalendarRecipientTag(
            string person)
        {
            if (string.Equals(
                    person,
                    "Todos",
                    StringComparison.OrdinalIgnoreCase))
            {
                return MessagesAllRecipientsTag;
            }

            return MessagesPeople
                .FirstOrDefault(item =>
                    string.Equals(
                        item.Value,
                        person,
                        StringComparison.OrdinalIgnoreCase))
                .Key ??
                string.Empty;
        }

        private static Dictionary<string, DateTimeOffset>
            ReadReminderAcknowledgedState()
        {
            try
            {
                var raw =
                    ApplicationData.Current.LocalSettings.Values[
                        MessagesReadStateKey] as string;

                if (string.IsNullOrWhiteSpace(raw))
                {
                    return new Dictionary<string, DateTimeOffset>(
                        StringComparer.OrdinalIgnoreCase);
                }

                var restored =
                    JsonSerializer.Deserialize<
                        Dictionary<string, DateTimeOffset>>(raw);

                return restored == null
                    ? new Dictionary<string, DateTimeOffset>(
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, DateTimeOffset>(
                        restored,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, DateTimeOffset>(
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        private static bool IsReminderCalendarAcknowledged(
            MessageViewItem message,
            IReadOnlyDictionary<string, DateTimeOffset> readState)
        {
            if (message == null ||
                string.IsNullOrWhiteSpace(
                    message.Row.ExternalId))
            {
                return false;
            }

            return readState.TryGetValue(
                       message.Row.ExternalId,
                       out var readAt) &&
                   readAt >= message.ScheduledAt;
        }

        private void UpdateRemindersCalendarBadge()
        {
            if (RemindersCalendarBadgeControl == null ||
                RemindersCalendarBadgeTextControl == null)
            {
                return;
            }

            var pending =
                GetAllPendingReminderCalendarItems();

            var readState =
                ReadReminderAcknowledgedState();

            var count =
                pending.Count(item =>
                    !IsReminderCalendarAcknowledged(
                        item,
                        readState));

            RemindersCalendarBadgeControl.Visibility =
                count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            RemindersCalendarBadgeTextControl.Text =
                count > 99
                    ? "99+"
                    : count.ToString(
                        CultureInfo.InvariantCulture);
        }

        private void RefreshReminderCalendarViewsFromIndex()
        {
            UpdateRemindersCalendarBadge();

            if (_remindersCalendarViewActive)
                DrawRemindersCalendar();

            if (_calendarViewActive)
            {
                DrawCalendarPreservingView(
                    _calendarActivities,
                    force: true);
            }
        }

        private static Color GetReminderCalendarAccent(
            bool completed,
            bool acknowledged)
        {
            // Los recordatorios usan acentos deliberadamente más vivos que
            // las actividades normales para que no se pierdan en el calendario.
            if (completed)
            {
                return Color.FromArgb(
                    255,
                    100,
                    116,
                    139);
            }

            if (acknowledged)
            {
                return Color.FromArgb(
                    255,
                    34,
                    211,
                    238);
            }

            return Color.FromArgb(
                255,
                168,
                85,
                247);
        }

        private void DrawRemindersCalendar()
        {
            if (RemindersCalendarCanvasControl == null)
                return;

            RemindersCalendarDateTitleControl.Text =
                _remindersCalendarSelectedDate
                    .ToString(
                        "dddd, d 'de' MMMM 'de' yyyy",
                        CultureInfo.GetCultureInfo("es-MX"));

            var visiblePeople =
                GetVisibleRemindersCalendarPeople();

            var items =
                GetReminderCalendarItems(
                    _remindersCalendarSelectedDate)
                    .Where(item =>
                        visiblePeople.Contains(
                            GetReminderCalendarPersonName(
                                item.RecipientTag),
                            StringComparer.OrdinalIgnoreCase))
                    .ToList();

            var readState =
                ReadReminderAcknowledgedState();

            var contentWidth =
                RemindersCalendarTimeColumnWidth +
                visiblePeople.Count *
                RemindersCalendarPersonColumnWidth;

            var viewportWidth =
                RemindersCalendarScrollViewerControl?.ActualWidth ?? 0;

            var totalWidth =
                Math.Max(
                    contentWidth,
                    viewportWidth > 1
                        ? viewportWidth
                        : contentWidth);

            var totalHeight =
                RemindersCalendarHeaderHeight +
                24 * RemindersCalendarHourHeight +
                12;

            RemindersCalendarCanvasControl.Children.Clear();
            _remindersCalendarHoverSlot = null;
            _remindersCalendarHoverBadge = null;
            _remindersCalendarHoverBadgeText = null;

            RemindersCalendarCanvasControl.Width =
                Math.Max(
                    RemindersCalendarTimeColumnWidth,
                    totalWidth);
            RemindersCalendarCanvasControl.Height =
                totalHeight;

            AddRemindersCalendarRectangle(
                0,
                0,
                RemindersCalendarCanvasControl.Width,
                RemindersCalendarHeaderHeight,
                Color.FromArgb(255, 24, 31, 40),
                Color.FromArgb(255, 74, 85, 104),
                zIndex: 20);

            AddRemindersCalendarText(
                "Hora",
                9,
                13,
                RemindersCalendarTimeColumnWidth - 14,
                24,
                11.5,
                true,
                zIndex: 30);

            for (var index = 0;
                 index < visiblePeople.Count;
                 index++)
            {
                var person =
                    visiblePeople[index];

                var left =
                    RemindersCalendarTimeColumnWidth +
                    index *
                    RemindersCalendarPersonColumnWidth;

                AddRemindersCalendarRectangle(
                    left,
                    0,
                    RemindersCalendarPersonColumnWidth,
                    RemindersCalendarHeaderHeight,
                    Color.FromArgb(255, 24, 31, 40),
                    Color.FromArgb(255, 60, 70, 84),
                    zIndex: 20);

                AddRemindersCalendarText(
                    person,
                    left + 8,
                    12,
                    RemindersCalendarPersonColumnWidth - 16,
                    24,
                    12,
                    true,
                    zIndex: 30);

                AddRemindersCalendarVerticalLine(
                    left,
                    0,
                    totalHeight,
                    Color.FromArgb(255, 48, 57, 69));
            }

            AddRemindersCalendarVerticalLine(
                RemindersCalendarCanvasControl.Width - 1,
                0,
                totalHeight,
                Color.FromArgb(255, 48, 57, 69));

            for (var hour = 0;
                 hour <= 24;
                 hour++)
            {
                var top =
                    RemindersCalendarHeaderHeight +
                    hour *
                    RemindersCalendarHourHeight;

                AddRemindersCalendarHorizontalLine(
                    0,
                    top,
                    RemindersCalendarCanvasControl.Width,
                    Color.FromArgb(255, 43, 51, 61));

                if (hour < 24)
                {
                    AddRemindersCalendarText(
                        FormatReminderHour(hour),
                        7,
                        top + 5,
                        RemindersCalendarTimeColumnWidth - 12,
                        20,
                        10.5,
                        false,
                        zIndex: 10);
                }
            }

            var exactStacks =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var message in items)
            {
                var person =
                    GetReminderCalendarPersonName(
                        message.RecipientTag);

                var personIndex =
                    FindRemindersCalendarPersonIndex(
                        visiblePeople,
                        person);

                if (personIndex < 0)
                    continue;

                var local =
                    message.ScheduledAt.LocalDateTime;

                var minuteOfDay =
                    local.Hour * 60 +
                    local.Minute;

                var top =
                    RemindersCalendarHeaderHeight +
                    minuteOfDay / 60d *
                    RemindersCalendarHourHeight +
                    2;

                var columnLeft =
                    RemindersCalendarTimeColumnWidth +
                    personIndex *
                    RemindersCalendarPersonColumnWidth;

                var stackKey =
                    $"{person}|{local:HH:mm}";

                exactStacks.TryGetValue(
                    stackKey,
                    out var stackIndex);

                exactStacks[stackKey] =
                    stackIndex + 1;

                var horizontalOffset =
                    Math.Min(
                        30,
                        stackIndex * 7);

                var width =
                    Math.Max(
                        92,
                        RemindersCalendarPersonColumnWidth -
                        10 -
                        horizontalOffset);

                var acknowledged =
                    IsReminderCalendarAcknowledged(
                        message,
                        readState);

                var card =
                    BuildReminderCalendarCard(
                        message,
                        width,
                        compact: false,
                        acknowledged);

                Canvas.SetLeft(
                    card,
                    columnLeft + 5 +
                    horizontalOffset);

                Canvas.SetTop(
                    card,
                    top);

                Canvas.SetZIndex(
                    card,
                    80 + stackIndex);

                RemindersCalendarCanvasControl.Children.Add(
                    card);
            }

            if (_remindersCalendarSelectedDate ==
                    DateTime.Today)
            {
                var now =
                    DateTime.Now;

                var currentTop =
                    RemindersCalendarHeaderHeight +
                    (now.Hour * 60 + now.Minute) /
                    60d *
                    RemindersCalendarHourHeight;

                AddRemindersCalendarHorizontalLine(
                    0,
                    currentTop,
                    RemindersCalendarCanvasControl.Width,
                    Color.FromArgb(
                        255,
                        248,
                        113,
                        113),
                    thickness: 2,
                    zIndex: 120);
            }

            // La guía se vuelve a crear al final del dibujo para que quede
            // por encima de la cuadrícula y de las tarjetas, sin interceptar clics.
            EnsureRemindersCalendarPointerGuideElements();

            var acknowledgedCount =
                items.Count(item =>
                    !item.IsCompleted &&
                    IsReminderCalendarAcknowledged(
                        item,
                        readState));

            var pendingCount =
                items.Count(item =>
                    !item.IsCompleted &&
                    !IsReminderCalendarAcknowledged(
                        item,
                        readState));

            var completedCount =
                items.Count(item =>
                    item.IsCompleted);

            var currentUser =
                GetCurrentMessagesUserTag();

            var currentUserName =
                MessagesPeople.TryGetValue(
                    currentUser,
                    out var mappedName)
                    ? mappedName
                    : currentUser;

            RemindersCalendarSummaryTextControl.Text =
                $"{items.Count} evento(s) · " +
                $"{pendingCount} pendiente(s) · " +
                $"{acknowledgedCount} entendido(s) · " +
                $"{completedCount} terminado(s)" +
                (string.IsNullOrWhiteSpace(currentUserName)
                    ? string.Empty
                    : $" · Usuario: {currentUserName}");

            RemindersCalendarEmptyStateControl.Visibility =
                visiblePeople.Count == 0 ||
                items.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            RemindersCalendarEmptyTextControl.Text =
                visiblePeople.Count == 0
                    ? "Selecciona al menos una persona en el filtro Personas."
                    : string.IsNullOrWhiteSpace(currentUser)
                        ? "Selecciona un usuario en Configuración para ver sus recordatorios."
                        : "No hay recordatorios para este día.";

            ModeText.Text =
                "Modo: Calendario de recordatorios";

            CountText.Text =
                $"{items.Count} recordatorios";

            if (_remindersCalendarAutoScrollPending)
            {
                _remindersCalendarAutoScrollPending = false;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_remindersCalendarViewActive ||
                        RemindersCalendarScrollViewerControl == null)
                    {
                        return;
                    }

                    var targetHour =
                        _remindersCalendarSelectedDate ==
                            DateTime.Today
                            ? Math.Max(
                                0,
                                DateTime.Now.Hour - 2)
                            : 8;

                    var target =
                        Math.Max(
                            0,
                            RemindersCalendarHeaderHeight +
                            targetHour *
                            RemindersCalendarHourHeight);

                    RemindersCalendarScrollViewerControl.ChangeView(
                        0,
                        target,
                        null,
                        disableAnimation: true);
                });
            }
        }

        private Button BuildReminderCalendarCard(
            MessageViewItem message,
            double width,
            bool compact,
            bool acknowledged)
        {
            var accent =
                GetReminderCalendarAccent(
                    message.IsCompleted,
                    acknowledged);

            var content =
                new StackPanel
                {
                    Spacing = compact ? 0 : 1,
                    IsHitTestVisible = false
                };

            var prefix =
                message.IsCompleted
                    ? "✓ REC"
                    : acknowledged
                        ? "✓ 🔔 REC"
                        : "🔔 REC";

            content.Children.Add(
                new TextBlock
                {
                    Text =
                        compact
                            ? $"{prefix} {message.Message}"
                            : $"{prefix} {message.ScheduledAt:HH:mm}",
                    FontSize =
                        compact
                            ? 8.5
                            : 9.5,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    MaxLines = 1,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                });

            if (!compact)
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text = message.Message,
                        FontSize = 10,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        MaxLines = 2,
                        TextWrapping =
                            TextWrapping.Wrap,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis
                    });
            }

            var button =
                new Button
                {
                    Width = width,
                    Height =
                        compact
                            ? 30
                            : 44,
                    Padding =
                        compact
                            ? new Thickness(6, 2, 6, 2)
                            : new Thickness(8, 4, 8, 4),
                    HorizontalContentAlignment =
                        HorizontalAlignment.Stretch,
                    VerticalContentAlignment =
                        VerticalAlignment.Center,
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(
                                message.IsCompleted
                                    ? (byte)82
                                    : acknowledged
                                        ? (byte)150
                                        : (byte)220,
                                accent.R,
                                accent.G,
                                accent.B)),
                    BorderBrush =
                        new SolidColorBrush(accent),
                    BorderThickness =
                        new Thickness(
                            compact ? 2 : 3,
                            1,
                            1,
                            1),
                    CornerRadius =
                        new CornerRadius(
                            compact ? 5 : 7),
                    Opacity =
                        message.IsCompleted
                            ? 0.58
                            : acknowledged
                                ? 0.82
                                : 1,
                    Content = content,
                    Tag = message
                };

            ToolTipService.SetToolTip(
                button,
                $"{message.Message}\n" +
                $"{message.DirectionLabel}\n" +
                $"{message.ScheduledLabel}\n" +
                (message.IsCompleted
                    ? "Terminado"
                    : acknowledged
                        ? "Entendido"
                        : "Pendiente"));

            button.Click +=
                async (_, __) =>
                {
                    await ShowReminderCalendarDetailsAsync(
                        message);
                };

            button.DoubleTapped +=
                (_, args) =>
                {
                    args.Handled = true;
                };

            return button;
        }

        private async Task ShowReminderCalendarDetailsAsync(
            MessageViewItem message)
        {
            if (message == null)
                return;

            var readState =
                ReadReminderAcknowledgedState();

            var acknowledged =
                IsReminderCalendarAcknowledged(
                    message,
                    readState);

            var root =
                new StackPanel
                {
                    Spacing = 9,
                    Width = 500,
                    MaxWidth = 500
                };

            root.Children.Add(
                new TextBlock
                {
                    Text = message.Message,
                    FontSize = 15,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

            root.Children.Add(
                new TextBlock
                {
                    Text =
                        $"{message.DirectionLabel}\n" +
                        $"{message.ScheduledLabel}\n" +
                        $"Estado: " +
                        (message.IsCompleted
                            ? "Terminado"
                            : acknowledged
                                ? "Entendido"
                                : "Pendiente"),
                    FontSize = 11,
                    Opacity = 0.76,
                    TextWrapping = TextWrapping.Wrap
                });

            var actions =
                new Grid
                {
                    ColumnSpacing = 7,
                    RowSpacing = 7
                };

            for (var i = 0; i < 3; i++)
            {
                actions.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            new GridLength(
                                1,
                                GridUnitType.Star)
                    });
            }

            actions.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            actions.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            ContentDialog? dialog = null;

            Button AddAction(
                string text,
                int column,
                int row,
                Func<Task> action,
                bool enabled = true)
            {
                var button =
                    new Button
                    {
                        Content = text,
                        MinHeight = 36,
                        IsEnabled = enabled,
                        HorizontalAlignment =
                            HorizontalAlignment.Stretch,
                        HorizontalContentAlignment =
                            HorizontalAlignment.Center,
                        Padding =
                            new Thickness(8, 5, 8, 5)
                    };

                button.Click +=
                    async (_, __) =>
                    {
                        dialog?.Hide();
                        await Task.Delay(180);

                        await action();

                        RefreshReminderCalendarViewsFromIndex();
                    };

                Grid.SetColumn(button, column);
                Grid.SetRow(button, row);
                actions.Children.Add(button);

                return button;
            }

            AddAction(
                "Abrir",
                0,
                0,
                () => OpenMessageInNotionAsync(message));

            AddAction(
                "Conversación",
                1,
                0,
                () => OpenConversationByPageIdAsync(
                    message.Row.ExternalId),
                enabled:
                    !string.IsNullOrWhiteSpace(
                        message.Row.ExternalId));

            AddAction(
                "Reasignar",
                2,
                0,
                () => ReassignMessageAsync(message));

            AddAction(
                "Reprogramar",
                0,
                1,
                () => RescheduleMessageAsync(message));

            AddAction(
                message.IsCompleted
                    ? "Reabrir"
                    : "Terminar",
                1,
                1,
                () => CompleteMessageAsync(message));

            AddAction(
                "Eliminar",
                2,
                1,
                () => DeleteMessageAsync(message));

            root.Children.Add(actions);

            dialog =
                new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "🔔 Recordatorio ANFETA",
                    Content = root,
                    PrimaryButtonText =
                        acknowledged
                            ? "Cerrar"
                            : "✓ Entendido",
                    CloseButtonText =
                        acknowledged
                            ? null
                            : "Cerrar",
                    DefaultButton =
                        ContentDialogButton.Primary,
                    MinWidth = 550,
                    MaxWidth = 620
                };

            var result =
                await dialog.ShowAsync();

            if (result ==
                    ContentDialogResult.Primary &&
                !acknowledged)
            {
                MarkMessageAsRead(message);
                RefreshReminderCalendarViewsFromIndex();

                StatusText.Text =
                    "Estado: Recordatorio marcado como entendido ✅";
            }
        }


        private void EnsureRemindersCalendarPointerGuide()
        {
            var canvas =
                RemindersCalendarCanvasControl;

            if (canvas == null ||
                _remindersCalendarPointerGuideHooked)
            {
                return;
            }

            canvas.PointerMoved +=
                RemindersCalendarCanvas_PointerMoved;

            canvas.PointerExited +=
                RemindersCalendarCanvas_PointerExited;

            _remindersCalendarPointerGuideHooked = true;
        }

        private void EnsureRemindersCalendarPointerGuideElements()
        {
            var canvas =
                RemindersCalendarCanvasControl;

            if (canvas == null)
                return;

            if (_remindersCalendarHoverSlot == null)
            {
                _remindersCalendarHoverSlot =
                    new Border
                    {
                        Height =
                            RemindersCalendarHourHeight / 4d,
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    34,
                                    168,
                                    85,
                                    247)),
                        BorderBrush =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    210,
                                    34,
                                    211,
                                    238)),
                        BorderThickness =
                            new Thickness(0, 1, 0, 1),
                        IsHitTestVisible = false,
                        Visibility = Visibility.Collapsed
                    };

                Canvas.SetZIndex(
                    _remindersCalendarHoverSlot,
                    900);

                canvas.Children.Add(
                    _remindersCalendarHoverSlot);
            }

            if (_remindersCalendarHoverBadge == null)
            {
                _remindersCalendarHoverBadgeText =
                    new TextBlock
                    {
                        FontSize = 10.5,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    255,
                                    255,
                                    255,
                                    255)),
                        MaxLines = 1,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis,
                        VerticalAlignment =
                            VerticalAlignment.Center
                    };

                _remindersCalendarHoverBadge =
                    new Border
                    {
                        Height = 28,
                        Padding =
                            new Thickness(8, 0, 8, 0),
                        CornerRadius =
                            new CornerRadius(7),
                        Background =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    245,
                                    88,
                                    28,
                                    135)),
                        BorderBrush =
                            new SolidColorBrush(
                                Color.FromArgb(
                                    255,
                                    34,
                                    211,
                                    238)),
                        BorderThickness =
                            new Thickness(1),
                        Child =
                            _remindersCalendarHoverBadgeText,
                        IsHitTestVisible = false,
                        Visibility = Visibility.Collapsed
                    };

                ToolTipService.SetToolTip(
                    _remindersCalendarHoverBadge,
                    "Doble clic para crear un recordatorio en esta hora.");

                Canvas.SetZIndex(
                    _remindersCalendarHoverBadge,
                    910);

                canvas.Children.Add(
                    _remindersCalendarHoverBadge);
            }
        }

        private void HideRemindersCalendarPointerGuide()
        {
            if (_remindersCalendarHoverSlot != null)
            {
                _remindersCalendarHoverSlot.Visibility =
                    Visibility.Collapsed;
            }

            if (_remindersCalendarHoverBadge != null)
            {
                _remindersCalendarHoverBadge.Visibility =
                    Visibility.Collapsed;
            }
        }

        private static bool IsReminderCalendarPointerOverButton(
            DependencyObject? source,
            Canvas canvas)
        {
            var current = source;

            while (current != null &&
                   !ReferenceEquals(
                       current,
                       canvas))
            {
                if (current is Button)
                    return true;

                current =
                    VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void RemindersCalendarCanvas_PointerMoved(
            object sender,
            PointerRoutedEventArgs e)
        {
            var canvas =
                RemindersCalendarCanvasControl;

            if (!_remindersCalendarViewActive ||
                canvas == null)
            {
                HideRemindersCalendarPointerGuide();
                return;
            }

            if (IsReminderCalendarPointerOverButton(
                    e.OriginalSource as DependencyObject,
                    canvas))
            {
                HideRemindersCalendarPointerGuide();
                return;
            }

            EnsureRemindersCalendarPointerGuideElements();

            var visiblePeople =
                GetVisibleRemindersCalendarPeople();

            if (visiblePeople.Count == 0)
            {
                HideRemindersCalendarPointerGuide();
                return;
            }

            var point =
                e.GetCurrentPoint(canvas)
                    .Position;

            var maxBodyY =
                RemindersCalendarHeaderHeight +
                24 * RemindersCalendarHourHeight;

            if (point.X <
                    RemindersCalendarTimeColumnWidth ||
                point.Y <
                    RemindersCalendarHeaderHeight ||
                point.Y >= maxBodyY)
            {
                HideRemindersCalendarPointerGuide();
                return;
            }

            var personIndex =
                (int)Math.Floor(
                    (point.X -
                     RemindersCalendarTimeColumnWidth) /
                    RemindersCalendarPersonColumnWidth);

            if (personIndex < 0 ||
                personIndex >=
                    visiblePeople.Count)
            {
                HideRemindersCalendarPointerGuide();
                return;
            }

            var rawMinutes =
                (point.Y -
                 RemindersCalendarHeaderHeight) /
                RemindersCalendarHourHeight *
                60d;

            // La guía usa exactamente el mismo redondeo de 15 minutos que
            // el doble clic, para que la hora mostrada sea la que se precarga.
            var roundedMinutes =
                Math.Clamp(
                    (int)Math.Round(
                        rawMinutes / 15d) *
                    15,
                    0,
                    23 * 60 + 45);

            var hour =
                roundedMinutes / 60;

            var minute =
                roundedMinutes % 60;

            var person =
                visiblePeople[
                    personIndex];

            var columnLeft =
                RemindersCalendarTimeColumnWidth +
                personIndex *
                RemindersCalendarPersonColumnWidth;

            var slotTop =
                RemindersCalendarHeaderHeight +
                roundedMinutes / 60d *
                RemindersCalendarHourHeight;

            if (_remindersCalendarHoverSlot != null)
            {
                _remindersCalendarHoverSlot.Width =
                    RemindersCalendarPersonColumnWidth - 2;

                Canvas.SetLeft(
                    _remindersCalendarHoverSlot,
                    columnLeft + 1);

                Canvas.SetTop(
                    _remindersCalendarHoverSlot,
                    slotTop);

                _remindersCalendarHoverSlot.Visibility =
                    Visibility.Visible;
            }

            if (_remindersCalendarHoverBadge != null &&
                _remindersCalendarHoverBadgeText != null)
            {
                var hourText =
                    DateTime.Today
                        .AddHours(hour)
                        .AddMinutes(minute)
                        .ToString(
                            "h:mm tt",
                            CultureInfo.InvariantCulture);

                _remindersCalendarHoverBadgeText.Text =
                    $"🕒 {hourText} · {person}";

                var badgeWidth =
                    Math.Max(
                        118,
                        RemindersCalendarPersonColumnWidth - 12);

                _remindersCalendarHoverBadge.Width =
                    badgeWidth;

                var badgeTop =
                    slotTop - 31;

                if (badgeTop <
                    RemindersCalendarHeaderHeight + 2)
                {
                    badgeTop =
                        slotTop +
                        RemindersCalendarHourHeight / 4d +
                        3;
                }

                Canvas.SetLeft(
                    _remindersCalendarHoverBadge,
                    columnLeft + 6);

                Canvas.SetTop(
                    _remindersCalendarHoverBadge,
                    badgeTop);

                _remindersCalendarHoverBadge.Visibility =
                    Visibility.Visible;
            }
        }

        private void RemindersCalendarCanvas_PointerExited(
            object sender,
            PointerRoutedEventArgs e)
        {
            HideRemindersCalendarPointerGuide();
        }

        private async void RemindersCalendarCanvas_DoubleTapped(
            object sender,
            DoubleTappedRoutedEventArgs e)
        {
            if (!_remindersCalendarViewActive ||
                RemindersCalendarCanvasControl == null)
            {
                return;
            }

            DependencyObject? current =
                e.OriginalSource as DependencyObject;

            while (current != null &&
                   !ReferenceEquals(
                       current,
                       RemindersCalendarCanvasControl))
            {
                if (current is Button)
                    return;

                current =
                    VisualTreeHelper.GetParent(current);
            }

            var visiblePeople =
                GetVisibleRemindersCalendarPeople();

            if (visiblePeople.Count == 0)
                return;

            var point =
                e.GetPosition(
                    RemindersCalendarCanvasControl);

            if (point.X <
                    RemindersCalendarTimeColumnWidth ||
                point.Y <
                    RemindersCalendarHeaderHeight)
            {
                return;
            }

            var personIndex =
                (int)Math.Floor(
                    (point.X -
                     RemindersCalendarTimeColumnWidth) /
                    RemindersCalendarPersonColumnWidth);

            if (personIndex < 0 ||
                personIndex >=
                    visiblePeople.Count)
            {
                return;
            }

            var person =
                visiblePeople[
                    personIndex];

            var recipientTag =
                GetReminderCalendarRecipientTag(
                    person);

            if (string.IsNullOrWhiteSpace(
                    recipientTag))
            {
                return;
            }

            var rawMinutes =
                (point.Y -
                 RemindersCalendarHeaderHeight) /
                RemindersCalendarHourHeight *
                60d;

            var roundedMinutes =
                Math.Clamp(
                    (int)Math.Round(
                        rawMinutes / 15d) *
                    15,
                    0,
                    23 * 60 + 45);

            var hour =
                roundedMinutes / 60;

            var minute =
                roundedMinutes % 60;

            var now =
                DateTimeOffset.Now;

            var suggested =
                new DateTimeOffset(
                    _remindersCalendarSelectedDate.Year,
                    _remindersCalendarSelectedDate.Month,
                    _remindersCalendarSelectedDate.Day,
                    hour,
                    minute,
                    0,
                    now.Offset);

            await ShowNewMessageDialogAsync(
                new NewMessageComposerContext
                {
                    RecipientTag = recipientTag,
                    RecipientName =
                        string.Equals(
                            person,
                            "Todos",
                            StringComparison.OrdinalIgnoreCase)
                            ? MessagesAllRecipientsName
                            : person,
                    SuggestedAt = suggested
                });

            RefreshReminderCalendarViewsFromIndex();

            e.Handled = true;
        }

        private void DrawCalendarReminderOverlays(
            double headerHeight,
            IReadOnlyList<string> visiblePersons)
        {
            if (!_calendarViewActive ||
                CalendarCanvas == null ||
                visiblePersons == null ||
                visiblePersons.Count == 0)
            {
                return;
            }

            var reminders =
                GetReminderCalendarItems(
                    _calendarSelectedDate)
                    .Where(item =>
                        !item.IsCompleted)
                    .ToList();

            if (reminders.Count == 0)
                return;

            var readState =
                ReadReminderAcknowledgedState();

            var currentUser =
                GetCurrentMessagesUserTag();

            var currentUserName =
                MessagesPeople.TryGetValue(
                    currentUser,
                    out var mappedCurrent)
                    ? mappedCurrent
                    : string.Empty;

            var stackCounters =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var message in reminders)
            {
                var local =
                    message.ScheduledAt.LocalDateTime;

                if (local.Hour < CalendarStartHour ||
                    local.Hour >= CalendarEndHour)
                {
                    continue;
                }

                var person =
                    GetReminderCalendarPersonName(
                        message.RecipientTag);

                if (string.Equals(
                        person,
                        "Todos",
                        StringComparison.OrdinalIgnoreCase))
                {
                    person =
                        visiblePersons.FirstOrDefault(candidate =>
                            string.Equals(
                                candidate,
                                currentUserName,
                                StringComparison.OrdinalIgnoreCase))
                        ??
                        visiblePersons.FirstOrDefault()
                        ??
                        string.Empty;
                }

                if (string.IsNullOrWhiteSpace(person) ||
                    !visiblePersons.Contains(
                        person,
                        StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var minutesFromStart =
                    (local -
                     _calendarSelectedDate.Date
                         .AddHours(CalendarStartHour))
                    .TotalMinutes;

                var top =
                    headerHeight +
                    minutesFromStart / 60d *
                    CalendarHourHeight +
                    3;

                var columnWidth =
                    GetResolvedCalendarColumnWidth(
                        person);

                var columnLeft =
                    GetResolvedCalendarColumnLeft(
                        person);

                var cardWidth =
                    Math.Clamp(
                        columnWidth * 0.62,
                        82,
                        Math.Max(
                            82,
                            columnWidth - 12));

                var stackKey =
                    $"{person}|{local:HH:mm}";

                stackCounters.TryGetValue(
                    stackKey,
                    out var stackIndex);

                stackCounters[stackKey] =
                    stackIndex + 1;

                var acknowledged =
                    IsReminderCalendarAcknowledged(
                        message,
                        readState);

                var card =
                    BuildReminderCalendarCard(
                        message,
                        cardWidth,
                        compact: true,
                        acknowledged);

                Canvas.SetLeft(
                    card,
                    columnLeft +
                    columnWidth -
                    cardWidth -
                    5 -
                    Math.Min(18, stackIndex * 5));

                Canvas.SetTop(
                    card,
                    top +
                    Math.Min(
                        18,
                        stackIndex * 4));

                Canvas.SetZIndex(
                    card,
                    420 + stackIndex);

                CalendarCanvas.Children.Add(
                    card);
            }
        }

        private static string FormatReminderHour(
            int hour)
        {
            var value =
                DateTime.Today.AddHours(hour);

            return value.ToString(
                "h tt",
                CultureInfo.InvariantCulture);
        }

        private Border AddRemindersCalendarRectangle(
            double left,
            double top,
            double width,
            double height,
            Color background,
            Color border,
            int zIndex = 0)
        {
            var element =
                new Border
                {
                    Width = width,
                    Height = height,
                    Background =
                        new SolidColorBrush(
                            background),
                    BorderBrush =
                        new SolidColorBrush(
                            border),
                    BorderThickness =
                        new Thickness(0, 0, 1, 1)
                };

            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            Canvas.SetZIndex(element, zIndex);

            RemindersCalendarCanvasControl.Children.Add(
                element);

            return element;
        }

        private TextBlock AddRemindersCalendarText(
            string text,
            double left,
            double top,
            double width,
            double height,
            double fontSize,
            bool bold,
            int zIndex = 0)
        {
            var element =
                new TextBlock
                {
                    Text = text,
                    Width = width,
                    Height = height,
                    FontSize = fontSize,
                    FontWeight =
                        bold
                            ? Microsoft.UI.Text
                                .FontWeights.SemiBold
                            : Microsoft.UI.Text
                                .FontWeights.Normal,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                    VerticalAlignment =
                        VerticalAlignment.Center
                };

            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            Canvas.SetZIndex(element, zIndex);

            RemindersCalendarCanvasControl.Children.Add(
                element);

            return element;
        }

        private void AddRemindersCalendarHorizontalLine(
            double left,
            double top,
            double width,
            Color color,
            double thickness = 1,
            int zIndex = 0)
        {
            var line =
                new Border
                {
                    Width = width,
                    Height = thickness,
                    Background =
                        new SolidColorBrush(
                            color),
                    IsHitTestVisible = false
                };

            Canvas.SetLeft(line, left);
            Canvas.SetTop(line, top);
            Canvas.SetZIndex(line, zIndex);

            RemindersCalendarCanvasControl.Children.Add(
                line);
        }

        private void AddRemindersCalendarVerticalLine(
            double left,
            double top,
            double height,
            Color color)
        {
            var line =
                new Border
                {
                    Width = 1,
                    Height = height,
                    Background =
                        new SolidColorBrush(
                            color),
                    IsHitTestVisible = false
                };

            Canvas.SetLeft(line, left);
            Canvas.SetTop(line, top);

            RemindersCalendarCanvasControl.Children.Add(
                line);
        }
    }
}
