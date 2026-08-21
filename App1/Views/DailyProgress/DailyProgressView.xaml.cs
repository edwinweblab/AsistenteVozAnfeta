using Anfeta.UI.Models.DailyProgress;
using Anfeta.UI.Services.Notion;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace Anfeta.UI.Views.DailyProgress
{
    public sealed class DailyProgressOpenActivityEventArgs : EventArgs
    {
        public string PageUrl { get; }
        public string Title { get; }

        public DailyProgressOpenActivityEventArgs(
            string pageUrl,
            string title)
        {
            PageUrl = pageUrl ?? string.Empty;
            Title = title ?? string.Empty;
        }
    }

    public sealed partial class DailyProgressView : UserControl
    {
        public event EventHandler? CloseRequested;

        public event EventHandler<DailyProgressOpenActivityEventArgs>?
            OpenActivityRequested;

        private readonly NotionDailyProgressService
            _service = new();

        private NotionCalendarService? _calendarService;
        private string _token = string.Empty;
        private DateTime _currentDate = DateTime.Today;
        private DailyProgressSnapshot? _snapshot;
        private CancellationTokenSource? _loadCts;

        // Mientras el Feed está abierto, relee únicamente la caché cada minuto.
        // Esto permite registrar deltas sin bombardear Notion.
        private Microsoft.UI.Dispatching.DispatcherQueueTimer?
            _trackingRefreshTimer;

        private bool _isLoading;

        private static readonly SolidColorBrush SurfaceBrush =
            Brush(255, 20, 28, 36);

        private static readonly SolidColorBrush SurfaceSoftBrush =
            Brush(255, 24, 33, 42);

        private static readonly SolidColorBrush BorderBrush =
            Brush(255, 47, 64, 78);

        private static readonly SolidColorBrush MutedBrush =
            Brush(255, 143, 163, 181);

        private static readonly SolidColorBrush AccentBrush =
            Brush(255, 56, 189, 248);

        private static readonly SolidColorBrush DangerBrush =
            Brush(255, 251, 113, 133);

        private static readonly SolidColorBrush DangerBackgroundBrush =
            Brush(255, 43, 20, 25);

        private static readonly SolidColorBrush ProgressBrush =
            Brush(255, 74, 222, 128);

        private static readonly SolidColorBrush ProgressBackgroundBrush =
            Brush(255, 16, 37, 27);

        private static readonly SolidColorBrush ReviewBrush =
            Brush(255, 103, 232, 249);

        private static readonly SolidColorBrush CompletedBrush =
            Brush(255, 134, 239, 172);

        public DailyProgressView()
        {
            InitializeComponent();
        }

        public void Initialize(
            NotionCalendarService calendarService,
            string token)
        {
            _calendarService =
                calendarService ??
                throw new ArgumentNullException(
                    nameof(calendarService));

            _token =
                token ?? string.Empty;
        }

        public void UpdateToken(string token)
        {
            _token =
                token ?? string.Empty;
        }

        public async Task OpenAsync(
            DateTime day)
        {
            _currentDate =
                day.Date;

            Visibility =
                Visibility.Visible;

            await LoadCurrentDateAsync(
                forceRefresh: false);

            StartTrackingRefreshTimer();
        }

        private async Task LoadCurrentDateAsync(
            bool forceRefresh)
        {
            if (_isLoading)
                return;

            if (_calendarService == null)
            {
                ShowError(
                    "El Feed no tiene conectado el servicio del calendario.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_token))
            {
                ShowError(
                    "Configura primero el token de Notion.");
                return;
            }

            try
            {
                _loadCts?.Cancel();
                _loadCts?.Dispose();
            }
            catch
            {
            }

            _loadCts =
                new CancellationTokenSource();

            var cancellationToken =
                _loadCts.Token;

            _isLoading = true;

            SetLoading(
                true,
                forceRefresh
                    ? "Actualizando avance…"
                    : "Preparando avance…");

            UpdateDateHeader();

            var progress =
                new Progress<string>(
                    message =>
                    {
                        FeedLoadingText.Text =
                            message;

                        FeedStatusText.Text =
                            message;
                    });

            try
            {
                var snapshot =
                    await _service.BuildAsync(
                        _calendarService,
                        _token,
                        _currentDate,
                        forceRefresh,
                        progress,
                        cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                _snapshot =
                    snapshot;

                RenderSnapshot(
                    snapshot);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowError(
                    ex.Message);
            }
            finally
            {
                _isLoading = false;

                SetLoading(
                    false,
                    string.Empty);
            }
        }

        private static string CanonicalPersonUi(
            string? value)
        {
            var raw =
                (value ?? string.Empty)
                    .Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "Sin asignar";

            var key =
                Regex.Replace(
                    raw.ToLowerInvariant(),
                    @"[^\p{L}\p{Nd}]",
                    string.Empty);

            key =
                Regex.Replace(
                    key,
                    @"\d+$",
                    string.Empty);

            if (key.Contains("nneft") ||
                key.Contains("nnetf") ||
                key.Contains("neftali"))
            {
                return "Neftali";
            }

            if (key.Contains("kkarl") ||
                key.Contains("karla"))
                return "Karla";

            if (key.Contains("iisai") ||
                key.Contains("isaias"))
                return "Isaias";

            if (key.Contains("aandr") ||
                key.Contains("andrade"))
                return "Andrade";

            if (key.Contains("bbria") ||
                key.Contains("brian"))
                return "Brian";

            if (key.Contains("ggena") ||
                key.Contains("genaro"))
                return "Genaro";

            if (key.Contains("jjohn") ||
                key == "john")
                return "John";

            if (key.Contains("ssote") ||
                key.Contains("eedua") ||
                key.Contains("sotelo"))
                return "Sotelo";

            if (key.Contains("aacal") ||
                key.Contains("acalli"))
                return "Acalli";

            if (key.Contains("eemma") ||
                key.Contains("emmanuel"))
                return "Emmanuel";

            if (key is "sin" or "sinasignar" or "ninguno" or "unassigned" ||
                raw.All(character =>
                    character is '-' or '—' or '–' or '_' or '·'))
            {
                return "Sin asignar";
            }

            return raw;
        }

        private static IReadOnlyList<string> BuildPersonPickerItems(
            DailyProgressSnapshot snapshot)
        {
            return snapshot.People
                .Select(person =>
                    CanonicalPersonUi(
                        person.Name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void RenderSnapshot(
            DailyProgressSnapshot snapshot)
        {
            UpdateDateHeader();

            CoverageKpiText.Text =
                $"{snapshot.CoveragePercentage}%";

            LaggingKpiText.Text =
                snapshot.LaggingCount.ToString(
                    CultureInfo.InvariantCulture);

            ReviewKpiText.Text =
                snapshot.ReviewCount.ToString(
                    CultureInfo.InvariantCulture);

            CompletedKpiText.Text =
                snapshot.CompletedCount.ToString(
                    CultureInfo.InvariantCulture);

            ScheduledKpiText.Text =
                $"{FormatMinutes(snapshot.ProgressMinutes)} / " +
                $"{FormatMinutes(snapshot.ScheduledMinutes)}";

            FeedSourceText.Text =
                snapshot.LoadedFromCalendarCache
                    ? "Calendario · caché"
                    : "Calendario · Notion";

            FeedStatusText.Text =
                snapshot.DataNote;

            var previousPerson =
                CanonicalPersonUi(
                    PersonPicker.SelectedItem as string);

            var pickerItems =
                BuildPersonPickerItems(
                    snapshot);

            // Reset explícito: un ComboBox puede conservar visualmente el valor
            // seleccionado anterior aunque ya no exista en el nuevo ItemsSource.
            PersonPicker.SelectedItem =
                null;

            PersonPicker.ItemsSource =
                null;

            PersonPicker.ItemsSource =
                pickerItems;

            var canonicalSelection =
                pickerItems.FirstOrDefault(item =>
                    string.Equals(
                        item,
                        previousPerson,
                        StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(
                    canonicalSelection))
            {
                PersonPicker.SelectedItem =
                    canonicalSelection;
            }
            else if (pickerItems.Count > 0)
            {
                PersonPicker.SelectedIndex =
                    0;
            }

            PeopleGrid.Items.Clear();

            var visiblePeople =
                snapshot.People
                    .GroupBy(
                        person =>
                            CanonicalPersonUi(
                                person.Name),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        MergePersonSnapshotsForUi(
                            group.Key,
                            group.ToList()))
                    .ToList();

            foreach (var person in visiblePeople)
            {
                PeopleGrid.Items.Add(
                    BuildPersonCard(
                        person));
            }

            UpdatePeopleGridItemWidth(
                PeopleGrid.ActualWidth);

            FeedEmptyState.Visibility =
                snapshot.TotalActivities == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (PersonModeToggle.IsChecked == true)
            {
                RenderSelectedPerson();
            }
            else
            {
                ShowGeneralMode();
            }
        }

        private void PeopleGrid_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            UpdatePeopleGridItemWidth(
                e.NewSize.Width);
        }

        private void UpdatePeopleGridItemWidth(
            double availableWidth)
        {
            if (availableWidth <= 0)
                return;

            // El objetivo no es un número fijo: elegimos cuántas columnas caben
            // cómodamente y cada tarjeta ocupa el ancho real disponible.
            const double minimumCardWidth = 330d;
            const double gapAllowance = 12d;

            var usableWidth =
                Math.Max(
                    280d,
                    availableWidth - 8d);

            var columns =
                Math.Max(
                    1,
                    (int)Math.Floor(
                        usableWidth /
                        minimumCardWidth));

            // Evitar tarjetas demasiado estrechas y también una pared de
            // micro-columnas en monitores ultra-wide.
            columns =
                Math.Min(
                    columns,
                    4);

            var itemWidth =
                Math.Max(
                    280d,
                    Math.Floor(
                        usableWidth / columns) -
                    gapAllowance);

            if (PeopleGrid.ItemsPanelRoot is
                ItemsWrapGrid wrapGrid)
            {
                wrapGrid.ItemWidth =
                    itemWidth;
            }
        }

        private static bool IsCarryOverItem(
            DailyProgressActivityItem item)
        {
            return item != null &&
                   !string.IsNullOrWhiteSpace(
                       item.MovementLabel) &&
                   item.MovementLabel.Contains(
                       "↪ De ",
                       StringComparison.OrdinalIgnoreCase);
        }

        private UIElement BuildPersonCard(
            DailyProgressPersonSnapshot person)
        {
            var root =
                new Border
                {
                    MinWidth = 0,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 10, 10),
                    Padding = new Thickness(12),
                    Background = SurfaceBrush,
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12)
                };

            var stack =
                new StackPanel
                {
                    Spacing = 9
                };

            var header =
                new Grid
                {
                    ColumnSpacing = 10
                };

            header.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            header.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            header.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var initial =
                new Border
                {
                    Width = 36,
                    Height = 36,
                    Background =
                        Brush(
                            255,
                            17,
                            47,
                            65),
                    BorderBrush =
                        Brush(
                            255,
                            39,
                            96,
                            128),
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(18)
                };

            initial.Child =
                new TextBlock
                {
                    Text = person.Initial,
                    HorizontalAlignment =
                        HorizontalAlignment.Center,
                    VerticalAlignment =
                        VerticalAlignment.Center,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold,
                    Foreground =
                        Brush(
                            255,
                            186,
                            230,
                            253)
                };

            Grid.SetColumn(
                initial,
                0);

            header.Children.Add(
                initial);

            var identity =
                new StackPanel
                {
                    Spacing = 1,
                    VerticalAlignment =
                        VerticalAlignment.Center
                };

            identity.Children.Add(
                new TextBlock
                {
                    Text =
                        CanonicalPersonUi(
                            person.Name)
                            .ToUpperInvariant(),
                    FontSize = 13,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold,
                    Foreground =
                        Brush(
                            255,
                            248,
                            250,
                            252)
                });

            identity.Children.Add(
                new TextBlock
                {
                    Text =
                        $"{person.AllActivities.Count} actividades · " +
                        $"avance {FormatMinutes(person.ProgressMinutes)} / " +
                        $"{FormatMinutes(person.ScheduledMinutes)}",
                    FontSize = 9.5,
                    Foreground = MutedBrush
                });

            Grid.SetColumn(
                identity,
                1);

            header.Children.Add(
                identity);

            var headerActions =
                new StackPanel
                {
                    Spacing = 5,
                    HorizontalAlignment =
                        HorizontalAlignment.Right,
                    VerticalAlignment =
                        VerticalAlignment.Center
                };

            var coverage =
                new Border
                {
                    Padding =
                        new Thickness(
                            9,
                            4,
                            9,
                            4),
                    HorizontalAlignment =
                        HorizontalAlignment.Right,
                    Background =
                        Brush(
                            255,
                            13,
                            36,
                            49),
                    BorderBrush =
                        Brush(
                            255,
                            35,
                            91,
                            119),
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(12)
                };

            coverage.Child =
                new TextBlock
                {
                    Text =
                        $"{person.CoveragePercentage}%",
                    FontSize = 12,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold,
                    Foreground =
                        AccentBrush
                };

            headerActions.Children.Add(
                coverage);

            // Siempre visible arriba. Antes estaba al final de la tarjeta y
            // las personas con muchas actividades lo empujaban fuera del viewport.
            var detailButton =
                new Button
                {
                    Content = "Ver detalle",
                    Tag = person.Name,
                    MinHeight = 28,
                    Padding =
                        new Thickness(
                            9,
                            0,
                            9,
                            0),
                    HorizontalAlignment =
                        HorizontalAlignment.Right,
                    Background =
                        Brush(
                            255,
                            18,
                            55,
                            75),
                    BorderBrush =
                        Brush(
                            255,
                            41,
                            103,
                            139),
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(7),
                    Foreground =
                        Brush(
                            255,
                            186,
                            230,
                            253)
                };

            detailButton.Click +=
                PersonDetailButton_Click;

            headerActions.Children.Add(
                detailButton);

            Grid.SetColumn(
                headerActions,
                2);

            header.Children.Add(
                headerActions);

            stack.Children.Add(
                header);

            var coverageBar =
                new ProgressBar
                {
                    Height = 3,
                    Minimum = 0,
                    Maximum = 100,
                    Value =
                        person.CoveragePercentage,
                    Foreground =
                        person.CoveragePercentage >= 70
                            ? ProgressBrush
                            : person.CoveragePercentage >= 40
                                ? AccentBrush
                                : DangerBrush
                };

            stack.Children.Add(
                coverageBar);

            var carryOverItems =
                person.AllActivities
                    .Where(IsCarryOverItem)
                    .OrderBy(item =>
                        item.Start)
                    .ToList();

            if (carryOverItems.Count > 0)
            {
                stack.Children.Add(
                    BuildMiniSection(
                        "VIENEN DE DÍAS ANTERIORES",
                        carryOverItems,
                        danger: false,
                        carryOver: true));
            }

            var historicalItems =
                person.AllActivities
                    .Where(item =>
                        item.IsHistoricalSnapshot)
                    .OrderBy(item =>
                        item.Start)
                    .ToList();

            if (historicalItems.Count > 0)
            {
                stack.Children.Add(
                    BuildMiniSection(
                        "HISTÓRICO DEL DÍA",
                        historicalItems,
                        danger: false,
                        carryOver: true));
            }

            stack.Children.Add(
                BuildMiniSection(
                    "REZAGOS",
                    person.Lagging,
                    danger: true));

            stack.Children.Add(
                BuildMiniSection(
                    "AVANCE HOY",
                    person.Progress,
                    danger: false));

            var footer =
                new Grid();

            var counts =
                new TextBlock
                {
                    Text =
                        $"R hoy {person.ReviewCount} · " +
                        $"Z hoy {person.CompletedCount} · " +
                        $"P {person.PendingCount} · " +
                        $"hist {person.HistoricalCount} · " +
                        $"⚠ check {person.IncompleteChecklistCount} · " +
                        $"sin checklist {person.MissingChecklistCount}",
                    VerticalAlignment =
                        VerticalAlignment.Center,
                    FontSize = 9.5,
                    Foreground = MutedBrush
                };

            footer.Children.Add(
                counts);

            stack.Children.Add(
                footer);

            root.Child =
                stack;

            return root;
        }

        private UIElement BuildMiniSection(
            string title,
            IReadOnlyList<DailyProgressActivityItem> items,
            bool danger,
            bool carryOver = false)
        {
            var section =
                new Border
                {
                    Padding =
                        new Thickness(
                            10,
                            8,
                            10,
                            8),
                    Background =
                        carryOver
                            ? Brush(
                                255,
                                12,
                                36,
                                50)
                            : danger
                                ? DangerBackgroundBrush
                                : ProgressBackgroundBrush,
                    BorderBrush =
                        carryOver
                            ? Brush(
                                255,
                                35,
                                91,
                                119)
                            : danger
                                ? Brush(
                                    255,
                                    83,
                                    41,
                                    49)
                                : Brush(
                                    255,
                                    35,
                                    83,
                                    55),
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(9)
                };

            var stack =
                new StackPanel
                {
                    Spacing = 6
                };

            stack.Children.Add(
                new TextBlock
                {
                    Text =
                        $"{(carryOver ? "↪" : danger ? "●" : "✓")} {title}",
                    FontSize = 10,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold,
                    Foreground =
                        carryOver
                            ? AccentBrush
                            : danger
                                ? DangerBrush
                                : ProgressBrush
                });

            if (items.Count == 0)
            {
                stack.Children.Add(
                    new TextBlock
                    {
                        Text =
                            danger
                                ? "Sin rezagos detectados."
                                : "Sin avance observado hoy todavía.",
                        FontSize = 9.5,
                        Foreground = MutedBrush
                    });
            }
            else
            {
                foreach (var item in
                         items.Take(2))
                {
                    stack.Children.Add(
                        BuildCompactActivity(
                            item,
                            danger));
                }

                if (items.Count > 2)
                {
                    stack.Children.Add(
                        new TextBlock
                        {
                            Text =
                                $"+ {items.Count - 2} más",
                            FontSize = 9,
                            Foreground = MutedBrush
                        });
                }
            }

            section.Child =
                stack;

            return section;
        }

        private UIElement BuildTodayTransitionBadge(
            DailyProgressActivityItem item)
        {
            var completed =
                item.IsCompletedMovement;

            var border =
                new Border
                {
                    HorizontalAlignment =
                        HorizontalAlignment.Left,
                    Margin =
                        new Thickness(0, 2, 0, 1),
                    Padding =
                        new Thickness(7, 2, 7, 2),
                    Background =
                        completed
                            ? Brush(255, 11, 70, 38)
                            : Brush(255, 8, 55, 72),
                    BorderBrush =
                        completed
                            ? Brush(255, 74, 222, 128)
                            : Brush(255, 34, 211, 238),
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(9)
                };

            border.Child =
                new TextBlock
                {
                    Text =
                        completed
                            ? "✓ PASÓ A Z HOY"
                            : "↗ PASÓ A R HOY",
                    FontSize = 9,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.Bold,
                    Foreground =
                        completed
                            ? Brush(255, 187, 247, 208)
                            : Brush(255, 165, 243, 252)
                };

            return border;
        }

        private static string BuildTodayMovementDetail(
            DailyProgressActivityItem item)
        {
            var value =
                item?.MovementLabel ??
                string.Empty;

            value =
                Regex.Replace(
                    value,
                    @"(?:^|\s*·\s*)(?:pasó a [RZ] hoy)(?=\s*·\s*|$)",
                    " ",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            value =
                Regex.Replace(
                    value,
                    @"\s*·\s*",
                    " · ")
                .Trim(' ', '·');

            return value;
        }

        private UIElement BuildCompactActivity(
            DailyProgressActivityItem item,
            bool danger)
        {
            var row =
                new Grid
                {
                    ColumnSpacing = 7
                };

            row.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            row.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var text =
                new StackPanel
                {
                    Spacing = 0
                };

            text.Children.Add(
                new TextBlock
                {
                    Text =
                        item.Domain,
                    FontSize = 10.5,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold,
                    Foreground =
                        Brush(
                            255,
                            241,
                            245,
                            249),
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                });

            text.Children.Add(
                new TextBlock
                {
                    Text =
                        item.ShortTitle,
                    FontSize = 9,
                    Foreground =
                        MutedBrush,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                });

            if (item.IsCompletedMovement ||
                item.IsReviewMovement)
            {
                text.Children.Add(
                    BuildTodayTransitionBadge(
                        item));
            }

            if (item.HasIncompleteChecklistWarning)
            {
                text.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"⚠ {item.StateCode} con checklist incompleta · {item.ChecklistLabel}",
                        FontSize = 8.7,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.Bold,
                        Foreground =
                            Brush(255, 253, 224, 71),
                        TextTrimming =
                            TextTrimming.CharacterEllipsis
                    });
            }

            var todayMovementDetail =
                BuildTodayMovementDetail(
                    item);

            if (!string.IsNullOrWhiteSpace(
                    todayMovementDetail))
            {
                text.Children.Add(
                    new TextBlock
                    {
                        Text =
                            todayMovementDetail,
                        FontSize = 8.5,
                        FontWeight =
                            Microsoft.UI.Text
                                .FontWeights.SemiBold,
                        Foreground =
                            IsCarryOverItem(item)
                                ? AccentBrush
                                : ProgressBrush,
                        TextTrimming =
                            TextTrimming.CharacterEllipsis,
                        MaxLines = 1
                    });
            }

            row.Children.Add(
                text);

            var badge =
                new Border
                {
                    Padding =
                        new Thickness(
                            7,
                            2,
                            7,
                            2),
                    VerticalAlignment =
                        VerticalAlignment.Center,
                    Background =
                        item.HasIncompleteChecklistWarning
                            ? Brush(255, 66, 51, 12)
                            : danger
                                ? Brush(
                                    255,
                                    62,
                                    25,
                                    31)
                                : Brush(
                                    255,
                                    14,
                                    51,
                                    31),
                    BorderBrush =
                        item.HasIncompleteChecklistWarning
                            ? Brush(255, 250, 204, 21)
                            : danger
                                ? DangerBrush
                                : ProgressBrush,
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(10)
                };

            badge.Child =
                new TextBlock
                {
                    Text =
                        $"{item.StateCode} {item.ChecklistLabel}",
                    FontSize = 9,
                    Foreground =
                        danger
                            ? Brush(
                                255,
                                254,
                                205,
                                211)
                            : Brush(
                                255,
                                220,
                                252,
                                231)
                };

            Grid.SetColumn(
                badge,
                1);

            row.Children.Add(
                badge);

            return row;
        }

        private static DailyProgressPersonSnapshot
            MergePersonSnapshotsForUi(
                string canonicalName,
                IReadOnlyList<DailyProgressPersonSnapshot> source)
        {
            if (source.Count == 1 &&
                string.Equals(
                    CanonicalPersonUi(source[0].Name),
                    canonicalName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return source[0];
            }

            var all =
                source
                    .SelectMany(person =>
                        person.AllActivities)
                    .GroupBy(
                        item => item.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        group.First())
                    .OrderBy(item =>
                        item.Start)
                    .ToList();

            var lagging =
                source
                    .SelectMany(person =>
                        person.Lagging)
                    .GroupBy(
                        item => item.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        group.First())
                    .ToList();

            var progress =
                source
                    .SelectMany(person =>
                        person.Progress)
                    .GroupBy(
                        item => item.PageId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        group.First())
                    .ToList();

            var scheduled =
                all
                    .Where(item =>
                        !item.IsSuspended)
                    .Sum(item =>
                        item.ScheduledMinutes);

            var progressed =
                all
                    .Where(item =>
                        !item.IsSuspended)
                    .Sum(item =>
                        item.ProgressMinutes);

            var coverage =
                scheduled > 0
                    ? Math.Clamp(
                        (int)Math.Round(
                            progressed * 100d /
                            scheduled),
                        0,
                        100)
                    : 0;

            return new DailyProgressPersonSnapshot
            {
                Name =
                    canonicalName,
                Initial =
                    string.IsNullOrWhiteSpace(canonicalName)
                        ? "?"
                        : canonicalName.Substring(0, 1).ToUpperInvariant(),
                CoveragePercentage =
                    coverage,
                ScheduledMinutes =
                    scheduled,
                ProgressMinutes =
                    progressed,
                Lagging =
                    lagging,
                Progress =
                    progress,
                AllActivities =
                    all,
                ReviewCount =
                    progress.Count(item =>
                        item.IsReviewMovement),
                CompletedCount =
                    progress.Count(item =>
                        item.IsCompletedMovement),
                PendingCount =
                    all.Count(item =>
                        item.StateCode == "P"),
                MissingChecklistCount =
                    all.Count(item =>
                        item.NeedsChecklistData),
                HistoricalCount =
                    all.Count(item =>
                        item.IsHistoricalSnapshot),
                IncompleteChecklistCount =
                    all.Count(item =>
                        item.HasIncompleteChecklistWarning)
            };
        }

        private void RenderSelectedPerson()
        {
            if (_snapshot == null ||
                _snapshot.People.Count == 0)
            {
                return;
            }

            var selectedName =
                PersonPicker.SelectedItem as string;

            var canonicalSelected =
                CanonicalPersonUi(
                    selectedName);

            var matchingPeople =
                _snapshot.People
                    .Where(item =>
                        string.Equals(
                            CanonicalPersonUi(item.Name),
                            canonicalSelected,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            var person =
                matchingPeople.Count > 0
                    ? MergePersonSnapshotsForUi(
                        canonicalSelected,
                        matchingPeople)
                    : _snapshot.People.First();

            if (PersonPicker.SelectedItem == null)
            {
                PersonPicker.SelectedItem =
                    person.Name;
            }

            DetailInitialText.Text =
                person.Initial;

            DetailPersonName.Text =
                person.Name;

            DetailPersonSummary.Text =
                $"Cobertura {person.CoveragePercentage}% · " +
                $"avance {FormatMinutes(person.ProgressMinutes)} / " +
                $"{FormatMinutes(person.ScheduledMinutes)} · " +
                $"{person.Lagging.Count} rezagadas";

            PersonDetailItems.Children.Clear();

            var carryOverItems =
                person.AllActivities
                    .Where(IsCarryOverItem)
                    .OrderBy(item =>
                        item.Start)
                    .ToList();

            if (carryOverItems.Count > 0)
            {
                PersonDetailItems.Children.Add(
                    BuildDetailSectionTitle(
                        "VIENEN DE DÍAS ANTERIORES",
                        carryOverItems.Count,
                        danger: false));

                foreach (var item in carryOverItems)
                {
                    PersonDetailItems.Children.Add(
                        BuildDetailActivityCard(
                            item,
                            danger: false));
                }
            }

            var historicalItems =
                person.AllActivities
                    .Where(item =>
                        item.IsHistoricalSnapshot)
                    .OrderBy(item =>
                        item.Start)
                    .ToList();

            if (historicalItems.Count > 0)
            {
                PersonDetailItems.Children.Add(
                    BuildDetailSectionTitle(
                        "HISTÓRICO DEL DÍA",
                        historicalItems.Count,
                        danger: false));

                foreach (var item in historicalItems)
                {
                    PersonDetailItems.Children.Add(
                        BuildDetailActivityCard(
                            item,
                            danger: false));
                }
            }

            PersonDetailItems.Children.Add(
                BuildDetailSectionTitle(
                    "NECESITAN ATENCIÓN",
                    person.Lagging.Count,
                    danger: true));

            if (person.Lagging.Count == 0)
            {
                PersonDetailItems.Children.Add(
                    BuildEmptyDetailCard(
                        "Sin actividades rezagadas para este día."));
            }
            else
            {
                foreach (var item in person.Lagging)
                {
                    PersonDetailItems.Children.Add(
                        BuildDetailActivityCard(
                            item,
                            danger: true));
                }
            }

            PersonDetailItems.Children.Add(
                BuildDetailSectionTitle(
                    "AVANCE HOY",
                    person.Progress.Count,
                    danger: false));

            if (person.Progress.Count == 0)
            {
                PersonDetailItems.Children.Add(
                    BuildEmptyDetailCard(
                        "Todavía no hay cambios observados hoy después del baseline."));
            }
            else
            {
                foreach (var item in person.Progress)
                {
                    PersonDetailItems.Children.Add(
                        BuildDetailActivityCard(
                            item,
                            danger: false));
                }
            }

            ShowPersonMode();
        }

        private UIElement BuildDetailSectionTitle(
            string title,
            int count,
            bool danger)
        {
            var grid =
                new Grid
                {
                    Margin =
                        new Thickness(
                            0,
                            5,
                            0,
                            0)
                };

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var label =
                new TextBlock
                {
                    Text =
                        $"{(danger ? "●" : "✓")} {title}",
                    FontSize = 11,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold,
                    Foreground =
                        danger
                            ? DangerBrush
                            : ProgressBrush
                };

            grid.Children.Add(
                label);

            var countBadge =
                new Border
                {
                    Padding =
                        new Thickness(
                            8,
                            2,
                            8,
                            2),
                    Background =
                        SurfaceSoftBrush,
                    CornerRadius =
                        new CornerRadius(9)
                };

            countBadge.Child =
                new TextBlock
                {
                    Text =
                        count.ToString(
                            CultureInfo.InvariantCulture),
                    FontSize = 9.5,
                    Foreground =
                        Brush(
                            255,
                            226,
                            232,
                            240)
                };

            Grid.SetColumn(
                countBadge,
                1);

            grid.Children.Add(
                countBadge);

            return grid;
        }

        private UIElement BuildDetailActivityCard(
            DailyProgressActivityItem item,
            bool danger)
        {
            var card =
                new Border
                {
                    Padding =
                        new Thickness(
                            13,
                            10,
                            13,
                            10),
                    Background =
                        item.HasIncompleteChecklistWarning
                            ? Brush(255, 45, 37, 13)
                            : danger
                                ? DangerBackgroundBrush
                                : SurfaceBrush,
                    BorderBrush =
                        item.HasIncompleteChecklistWarning
                            ? Brush(255, 250, 204, 21)
                            : danger
                                ? Brush(
                                    255,
                                    101,
                                    44,
                                    53)
                                : Brush(
                                    255,
                                    41,
                                    74,
                                    58),
                    BorderThickness =
                        new Thickness(
                            item.HasIncompleteChecklistWarning
                                ? 4
                                : danger
                                    ? 3
                                    : 2,
                            item.HasIncompleteChecklistWarning
                                ? 2
                                : 1,
                            item.HasIncompleteChecklistWarning
                                ? 2
                                : 1,
                            item.HasIncompleteChecklistWarning
                                ? 2
                                : 1),
                    CornerRadius =
                        new CornerRadius(10)
                };

            var grid =
                new Grid
                {
                    ColumnSpacing = 12
                };

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            var info =
                new StackPanel
                {
                    Spacing = 3
                };

            var domainLine =
                new StackPanel
                {
                    Orientation =
                        Orientation.Horizontal,
                    Spacing = 7
                };

            domainLine.Children.Add(
                BuildStateBadge(
                    item));

            domainLine.Children.Add(
                new TextBlock
                {
                    Text =
                        item.Domain,
                    FontSize = 12,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold,
                    Foreground =
                        Brush(
                            255,
                            248,
                            250,
                            252),
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                });

            info.Children.Add(
                domainLine);

            info.Children.Add(
                new TextBlock
                {
                    Text =
                        item.ShortTitle,
                    FontSize = 11,
                    Foreground =
                        Brush(
                            255,
                            203,
                            213,
                            225),
                    TextWrapping =
                        TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                });

            info.Children.Add(
                new TextBlock
                {
                    Text =
                        $"{item.TimeLabel} · checklist {item.ChecklistLabel}",
                    FontSize = 9.5,
                    Foreground =
                        MutedBrush
                });

            if (item.HasIncompleteChecklistWarning)
            {
                info.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"⚠ {item.StateLabel} con checklist incompleta · " +
                            $"{item.ChecklistCompleted}/{item.ChecklistTotal} ({item.ChecklistPercentage}%)",
                        FontSize = 9.5,
                        FontWeight =
                            Microsoft.UI.Text.FontWeights.Bold,
                        Foreground =
                            Brush(255, 253, 224, 71),
                        TextWrapping =
                            TextWrapping.Wrap
                    });
            }

            if (item.IsCompletedMovement ||
                item.IsReviewMovement)
            {
                info.Children.Add(
                    BuildTodayTransitionBadge(
                        item));
            }

            var todayMovementDetail =
                BuildTodayMovementDetail(
                    item);

            if (!string.IsNullOrWhiteSpace(
                    todayMovementDetail))
            {
                info.Children.Add(
                    new TextBlock
                    {
                        Text =
                            todayMovementDetail,
                        FontSize = 9.5,
                        FontWeight =
                            Microsoft.UI.Text
                                .FontWeights.SemiBold,
                        Foreground =
                            IsCarryOverItem(item)
                                ? AccentBrush
                                : ProgressBrush,
                        TextWrapping =
                            TextWrapping.Wrap
                    });
            }

            grid.Children.Add(
                info);

            var actions =
                new StackPanel
                {
                    Orientation =
                        Orientation.Horizontal,
                    VerticalAlignment =
                        VerticalAlignment.Center,
                    Spacing = 6
                };

            Grid.SetColumn(
                actions,
                1);

            var checklist =
                new Border
                {
                    Padding =
                        new Thickness(
                            9,
                            4,
                            9,
                            4),
                    Background =
                        item.HasIncompleteChecklistWarning
                            ? Brush(255, 66, 51, 12)
                            : item.IsCompleted
                                ? Brush(
                                    255,
                                    13,
                                    54,
                                    33)
                                : Brush(
                                    255,
                                    21,
                                    34,
                                    44),
                    BorderBrush =
                        item.HasIncompleteChecklistWarning
                            ? Brush(255, 250, 204, 21)
                            : item.IsCompleted
                                ? ProgressBrush
                                : BorderBrush,
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(10)
                };

            checklist.Child =
                new TextBlock
                {
                    Text =
                        item.ChecklistScanned
                            ? $"{item.ChecklistLabel} · {item.ChecklistPercentage}%"
                            : "Checklist …",
                    FontSize = 9.5,
                    Foreground =
                        item.IsCompleted
                            ? CompletedBrush
                            : Brush(
                                255,
                                203,
                                213,
                                225)
                };

            actions.Children.Add(
                checklist);

            if (!string.IsNullOrWhiteSpace(
                    item.PageUrl))
            {
                var openButton =
                    new Button
                    {
                        Content =
                            danger
                                ? "Revisar checklist"
                                : "Abrir",
                        Tag = item,
                        MinHeight = 30,
                        Padding =
                            new Thickness(
                                10,
                                0,
                                10,
                                0),
                        Background =
                            Brush(
                                255,
                                18,
                                55,
                                75),
                        BorderBrush =
                            Brush(
                                255,
                                41,
                                103,
                                139),
                        BorderThickness =
                            new Thickness(1),
                        CornerRadius =
                            new CornerRadius(7),
                        Foreground =
                            Brush(
                                255,
                                186,
                                230,
                                253)
                    };

                openButton.Click +=
                    OpenActivity_Click;

                actions.Children.Add(
                    openButton);
            }

            var canMoveTomorrow =
                item.ChecklistScanned &&
                item.ChecklistTotal > 0 &&
                item.ChecklistCompleted < item.ChecklistTotal &&
                !item.IsCompleted &&
                !item.IsSuspended;

            if (canMoveTomorrow)
            {
                var moveTomorrowButton =
                    new Button
                    {
                        Content = "Mover mañana",
                        Tag = item,
                        MinHeight = 30,
                        Padding =
                            new Thickness(
                                10,
                                0,
                                10,
                                0),
                        Background =
                            Brush(
                                255,
                                44,
                                35,
                                17),
                        BorderBrush =
                            Brush(
                                255,
                                117,
                                83,
                                25),
                        BorderThickness =
                            new Thickness(1),
                        CornerRadius =
                            new CornerRadius(7),
                        Foreground =
                            Brush(
                                255,
                                253,
                                230,
                                138)
                    };

                moveTomorrowButton.Click +=
                    MoveTomorrow_Click;

                actions.Children.Add(
                    moveTomorrowButton);
            }

            Grid.SetColumn(
                actions,
                1);

            grid.Children.Add(
                actions);

            card.Child =
                grid;

            return card;
        }

        private UIElement BuildStateBadge(
            DailyProgressActivityItem item)
        {
            SolidColorBrush foreground;
            SolidColorBrush background;
            SolidColorBrush border;

            switch (item.StateCode)
            {
                case "Z":
                    foreground = CompletedBrush;
                    background = Brush(255, 13, 54, 33);
                    border = ProgressBrush;
                    break;

                case "R":
                    foreground = ReviewBrush;
                    background = Brush(255, 11, 45, 58);
                    border = Brush(255, 34, 116, 150);
                    break;

                case "SP":
                    foreground = Brush(255, 203, 213, 225);
                    background = Brush(255, 39, 45, 54);
                    border = Brush(255, 74, 85, 99);
                    break;

                default:
                    foreground = DangerBrush;
                    background = Brush(255, 55, 25, 30);
                    border = Brush(255, 112, 45, 57);
                    break;
            }

            var badge =
                new Border
                {
                    Padding =
                        new Thickness(
                            7,
                            2,
                            7,
                            2),
                    Background = background,
                    BorderBrush = border,
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(9)
                };

            badge.Child =
                new TextBlock
                {
                    Text =
                        item.StateCode,
                    FontSize = 9.5,
                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold,
                    Foreground = foreground
                };

            return badge;
        }

        private UIElement BuildEmptyDetailCard(
            string text)
        {
            var border =
                new Border
                {
                    Padding =
                        new Thickness(
                            13,
                            11,
                            13,
                            11),
                    Background =
                        SurfaceBrush,
                    BorderBrush =
                        BorderBrush,
                    BorderThickness =
                        new Thickness(1),
                    CornerRadius =
                        new CornerRadius(9)
                };

            border.Child =
                new TextBlock
                {
                    Text = text,
                    FontSize = 10,
                    Foreground = MutedBrush
                };

            return border;
        }

        private void ShowGeneralMode()
        {
            GeneralModeToggle.IsChecked =
                true;

            PersonModeToggle.IsChecked =
                false;

            GeneralViewHost.Visibility =
                Visibility.Visible;

            PersonDetailHost.Visibility =
                Visibility.Collapsed;

            FeedFooter.Visibility =
                Visibility.Visible;
        }

        private void ShowPersonMode()
        {
            GeneralModeToggle.IsChecked =
                false;

            PersonModeToggle.IsChecked =
                true;

            GeneralViewHost.Visibility =
                Visibility.Collapsed;

            PersonDetailHost.Visibility =
                Visibility.Visible;

            // En detalle priorizamos el espacio vertical para actividades.
            FeedFooter.Visibility =
                Visibility.Collapsed;
        }

        private void GeneralMode_Click(
            object sender,
            RoutedEventArgs e)
        {
            ShowGeneralMode();
        }

        private void PersonMode_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_snapshot?.People.Count > 0 &&
                PersonPicker.SelectedIndex < 0)
            {
                PersonPicker.SelectedIndex = 0;
            }

            RenderSelectedPerson();
        }

        private void PersonPicker_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_snapshot == null ||
                PersonPicker.SelectedItem == null)
            {
                return;
            }

            if (PersonModeToggle.IsChecked == true)
            {
                RenderSelectedPerson();
            }
        }

        private void PersonDetailButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not string personName)
            {
                return;
            }

            PersonPicker.SelectedItem =
                personName;

            RenderSelectedPerson();
        }

        private async void MoveTomorrow_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not DailyProgressActivityItem item ||
                _calendarService == null ||
                string.IsNullOrWhiteSpace(_token))
            {
                return;
            }

            var sourceDate =
                item.Source.Start.Date;

            var targetDate =
                _currentDate.Date.AddDays(1);

            var confirm =
                new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Mover actividad a mañana",
                    Content =
                        $"Se moverá \"{item.ShortTitle}\" al " +
                        $"{targetDate:dd/MM/yyyy} conservando horario, BODY y checklist marcado.",
                    PrimaryButtonText = "Mover",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Primary
                };

            var result =
                await confirm.ShowAsync();

            if (result != ContentDialogResult.Primary)
                return;

            button.IsEnabled =
                false;

            FeedStatusText.Text =
                $"Moviendo {item.Domain} a mañana…";

            try
            {
                await _calendarService.MoveActivityToDateAsync(
                    _token,
                    item.Source,
                    targetDate);

                var movementTracked =
                    true;

                try
                {
                    await NotionDailyProgressService
                        .RegisterCalendarMovementAsync(
                            item.PageId,
                            sourceDate,
                            targetDate,
                            "Avance Diario · Mover mañana");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // El movimiento en Notion YA ocurrió. Un fallo del archivo
                    // local no debe fingir que el movimiento completo falló.
                    movementTracked =
                        false;
                }

                FeedStatusText.Text =
                    movementTracked
                        ? $"Actividad movida a {targetDate:dd/MM/yyyy} ✅ · origen conservado"
                        : $"Actividad movida a {targetDate:dd/MM/yyyy} ✅ · no se pudo guardar la trazabilidad local";

                // MoveActivityToDateAsync ya actualiza la caché de ambos días.
                await LoadCurrentDateAsync(
                    forceRefresh: false);
            }
            catch (Exception ex)
            {
                FeedStatusText.Text =
                    $"No se pudo mover la actividad → {ex.Message}";

                button.IsEnabled =
                    true;
            }
        }

        private void OpenActivity_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not DailyProgressActivityItem item)
            {
                return;
            }

            OpenActivityRequested?.Invoke(
                this,
                new DailyProgressOpenActivityEventArgs(
                    item.PageUrl,
                    item.FullTitle));
        }

        private void StartTrackingRefreshTimer()
        {
            if (_trackingRefreshTimer != null)
                return;

            _trackingRefreshTimer =
                DispatcherQueue.CreateTimer();

            _trackingRefreshTimer.Interval =
                TimeSpan.FromMinutes(1);

            _trackingRefreshTimer.Tick +=
                async (_, __) =>
                {
                    if (Visibility != Visibility.Visible ||
                        _isLoading)
                    {
                        return;
                    }

                    // forceRefresh:false = cache-first.
                    // El calendario sigue siendo quien actualiza Notion.
                    await LoadCurrentDateAsync(
                        forceRefresh: false);
                };

            _trackingRefreshTimer.Start();
        }

        private void StopTrackingRefreshTimer()
        {
            if (_trackingRefreshTimer == null)
                return;

            try
            {
                _trackingRefreshTimer.Stop();
            }
            catch
            {
            }

            _trackingRefreshTimer = null;
        }

        private async void PreviousDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            _currentDate =
                _currentDate.AddDays(-1);

            await LoadCurrentDateAsync(
                forceRefresh: false);
        }

        private async void NextDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            _currentDate =
                _currentDate.AddDays(1);

            await LoadCurrentDateAsync(
                forceRefresh: false);
        }

        private async void Refresh_Click(
            object sender,
            RoutedEventArgs e)
        {
            await LoadCurrentDateAsync(
                forceRefresh: true);
        }

        private void Close_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopTrackingRefreshTimer();

            try
            {
                _loadCts?.Cancel();
            }
            catch
            {
            }

            CloseRequested?.Invoke(
                this,
                EventArgs.Empty);
        }

        private void UpdateDateHeader()
        {
            FeedDateText.Text =
                _currentDate.ToString(
                    "dd/MM/yyyy",
                    CultureInfo.InvariantCulture);

            FeedDateSubtitle.Text =
                _currentDate.ToString(
                    "dddd, d 'de' MMMM 'de' yyyy",
                    new CultureInfo("es-MX"));
        }

        private void SetLoading(
            bool loading,
            string message)
        {
            FeedLoadingBadge.Visibility =
                loading
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(message))
            {
                FeedLoadingText.Text =
                    message;
            }
        }

        private void ShowError(
            string message)
        {
            FeedEmptyState.Visibility =
                Visibility.Visible;

            FeedEmptyText.Text =
                message;

            FeedStatusText.Text =
                $"No se pudo preparar Avance diario · {message}";

            FeedSourceText.Text =
                "Error";
        }

        private static string FormatMinutes(
            int minutes)
        {
            minutes =
                Math.Max(
                    0,
                    minutes);

            var hours =
                minutes / 60;

            var remainder =
                minutes % 60;

            if (hours > 0 &&
                remainder > 0)
            {
                return $"{hours}H {remainder}M";
            }

            if (hours > 0)
                return $"{hours}H";

            return $"{remainder}M";
        }

        private static SolidColorBrush Brush(
            byte a,
            byte r,
            byte g,
            byte b)
        {
            return new SolidColorBrush(
                Color.FromArgb(
                    a,
                    r,
                    g,
                    b));
        }
    }
}
