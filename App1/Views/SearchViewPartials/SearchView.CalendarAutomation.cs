using Anfeta.UI.Models.Notion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private const string LS_DailyCalendarAutomationLastRun =
            "Search.Calendar.DailyAutomation.LastRun";
        private const string LS_DailyCalendarAutomationLastReport =
            "Search.Calendar.DailyAutomation.LastReport";

        private sealed record DailyCalendarMovement(
            string PageId,
            string Title,
            string Person,
            string Project,
            DateTime PreviousStart,
            DateTime NewStart);

        private sealed record DailyCalendarCompletion(
            string PageId,
            string Title,
            string Person,
            string Project,
            string CompletionType,
            DateTime ActivityDate);

        private sealed record DailyCalendarReport(
            DateTime GeneratedAt,
            int Reviewed,
            int Moved,
            int SkippedCompleted,
            int SkippedSuspended,
            int Failed,
            IReadOnlyList<DailyCalendarMovement> Movements,
            IReadOnlyList<string> Errors,
            IReadOnlyList<DailyCalendarCompletion>? CompletedForReview = null,
            IReadOnlyList<DailyCalendarCompletion>? Finalized = null);

        private async Task RunDailyCalendarAutomationIfNeededAsync()
        {
            var now = DateTime.Now;

            if (now.Hour < 7)
                return;

            var values =
                ApplicationData.Current.LocalSettings.Values;

            var lastRunRaw =
                values[LS_DailyCalendarAutomationLastRun] as string;

            if (DateTime.TryParseExact(
                    lastRunRaw,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var lastRun) &&
                lastRun.Date == now.Date)
            {
                return;
            }

            await RunDailyCalendarAutomationCoreAsync(
                showResultDialog: false);
        }

        private async void CalendarRunDailyAutomation_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunDailyCalendarAutomationCoreAsync(
                showResultDialog: true);
        }

        private async void CalendarDailyReport_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ShowLastDailyCalendarReportAsync();
        }

        private async Task RunDailyCalendarAutomationCoreAsync(
            bool showResultDialog)
        {
            var values =
                ApplicationData.Current.LocalSettings.Values;

            var token =
                values["Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var yesterday =
                DateTime.Today.AddDays(-1);

            var today =
                DateTime.Today;

            var moved =
                new List<DailyCalendarMovement>();

            var errors =
                new List<string>();

            var completedForReview =
                new List<DailyCalendarCompletion>();

            var finalized =
                new List<DailyCalendarCompletion>();

            var reviewed = 0;
            var skippedCompleted = 0;
            var skippedSuspended = 0;
            var failed = 0;

            try
            {
                ShowLoadingState(
                    "Estado: Procesando pendientes de ayer...",
                    "Se moverán a hoy conservando su horario. " +
                    "Las terminadas y suspendidas quedan fuera.");

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(15));

                var activities =
                    await _notionCalendarService.GetDayAsync(
                        token,
                        yesterday,
                        progress: null,
                        cts.Token,
                        forceRefresh: true);

                foreach (var activity in activities)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    reviewed++;

                    var searchable =
                        BuildCalendarActivitySearchableText(
                            activity);

                    var assignedPerson =
                        !string.IsNullOrWhiteSpace(
                            activity.OriginalPerson)
                            ? activity.OriginalPerson
                            : !string.IsNullOrWhiteSpace(
                                activity.Person)
                                ? activity.Person
                                : "Sin asignar";

                    var isReadyForReview =
                        ContainsExactAutomationTag(
                            searchable,
                            "rtuzREVISION");

                    var isFinalized =
                        ContainsExactAutomationTag(
                            searchable,
                            "zREVISION") ||
                        IsCompletedReviewStatus(
                            activity.Status) ||
                        activity.IsCompletedForReview;

                    if (isReadyForReview)
                    {
                        completedForReview.Add(
                            new DailyCalendarCompletion(
                                activity.PageId,
                                activity.Title,
                                assignedPerson,
                                activity.Project,
                                "Para revisión",
                                activity.Start));
                    }

                    if (isFinalized)
                    {
                        finalized.Add(
                            new DailyCalendarCompletion(
                                activity.PageId,
                                activity.Title,
                                assignedPerson,
                                activity.Project,
                                "Finalizada",
                                activity.Start));
                    }

                    if (ContainsExactAutomationTag(
                            searchable,
                            "sprtuzREVISION"))
                    {
                        skippedSuspended++;
                        continue;
                    }

                    if (isFinalized)
                    {
                        skippedCompleted++;
                        continue;
                    }

                    var isPending =
                        ContainsExactAutomationTag(
                            searchable,
                            "prtuzREVISION") ||
                        isReadyForReview;

                    if (!isPending)
                        continue;

                    try
                    {
                        var oldStart =
                            activity.Start;

                        var updated =
                            await _notionCalendarService
                                .MoveActivityToDateAsync(
                                    token,
                                    activity,
                                    today,
                                    cts.Token);

                        moved.Add(
                            new DailyCalendarMovement(
                                activity.PageId,
                                activity.Title,
                                activity.Person,
                                activity.Project,
                                oldStart,
                                updated.Start));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add(
                            $"{activity.Title}: {ex.Message}");
                    }
                }

                var report =
                    new DailyCalendarReport(
                        DateTime.Now,
                        reviewed,
                        moved.Count,
                        skippedCompleted,
                        skippedSuspended,
                        failed,
                        moved,
                        errors,
                        completedForReview,
                        finalized);

                values[LS_DailyCalendarAutomationLastRun] =
                    DateTime.Today.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

                values[LS_DailyCalendarAutomationLastReport] =
                    JsonSerializer.Serialize(report);

                var todayCache =
                    await _notionCalendarService
                        .TryGetCachedDayAsync(
                            today,
                            cts.Token);

                if (_calendarViewActive &&
                    _calendarSelectedDate.Date == today)
                {
                    _calendarActivities =
                        todayCache ??
                        Array.Empty<NotionCalendarActivity>();

                    DrawCalendar(_calendarActivities);
                }

                StatusText.Text =
                    $"Estado: Automatización diaria completa ✅ " +
                    $"Movidas: {moved.Count} · " +
                    $"Terminadas omitidas: {skippedCompleted} · " +
                    $"Suspendidas omitidas: {skippedSuspended} · " +
                    $"Errores: {failed}";

                if (showResultDialog)
                {
                    await ShowDailyCalendarReportAsync(
                        report);
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: La automatización diaria fue cancelada por tiempo de espera.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: Error en automatización diaria → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private static bool ContainsExactAutomationTag(
            string searchable,
            string tag)
        {
            return Regex.IsMatch(
                searchable ?? string.Empty,
                $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(tag)}(?![\p{{L}}\p{{Nd}}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }

        private async Task ShowLastDailyCalendarReportAsync()
        {
            var raw =
                ApplicationData.Current.LocalSettings.Values[
                    LS_DailyCalendarAutomationLastReport] as string;

            if (string.IsNullOrWhiteSpace(raw))
            {
                var emptyDialog =
                    new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "Reporte diario",
                        Content =
                            "Todavía no existe un reporte de automatización diaria.",
                        CloseButtonText = "Cerrar"
                    };

                await emptyDialog.ShowAsync();
                return;
            }

            try
            {
                var report =
                    JsonSerializer.Deserialize<
                        DailyCalendarReport>(raw);

                if (report == null)
                    throw new InvalidOperationException();

                await ShowDailyCalendarReportAsync(
                    report);
            }
            catch
            {
                var invalidDialog =
                    new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "Reporte diario",
                        Content =
                            "El reporte guardado no pudo leerse.",
                        CloseButtonText = "Cerrar"
                    };

                await invalidDialog.ShowAsync();
            }
        }

        private async Task ShowDailyCalendarReportAsync(
            DailyCalendarReport report)
        {
            var root =
                new StackPanel
                {
                    Width = 620,
                    Spacing = 10
                };

            root.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Generado: {report.GeneratedAt:dd/MM/yyyy HH:mm}",
                    Opacity = 0.72
                });

            root.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Revisadas: {report.Reviewed} · " +
                        $"Movidas: {report.Moved} · " +
                        $"Terminadas omitidas: {report.SkippedCompleted} · " +
                        $"Suspendidas omitidas: {report.SkippedSuspended} · " +
                        $"Errores: {report.Failed}",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            var completedForReview =
                report.CompletedForReview ??
                Array.Empty<DailyCalendarCompletion>();

            var finalized =
                report.Finalized ??
                Array.Empty<DailyCalendarCompletion>();

            var completionSummary =
                completedForReview
                    .Concat(finalized)
                    .GroupBy(
                        item => string.IsNullOrWhiteSpace(item.Person)
                            ? "Sin asignar"
                            : item.Person,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        new
                        {
                            Person = group.Key,
                            ReadyForReview =
                                group.Count(item =>
                                    string.Equals(
                                        item.CompletionType,
                                        "Para revisión",
                                        StringComparison.OrdinalIgnoreCase)),
                            Finalized =
                                group.Count(item =>
                                    string.Equals(
                                        item.CompletionType,
                                        "Finalizada",
                                        StringComparison.OrdinalIgnoreCase))
                        })
                    .OrderBy(item => item.Person)
                    .ToList();

            root.Children.Add(
                new TextBlock
                {
                    Text = "Actividades terminadas por persona",
                    FontSize = 15,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                });

            if (completionSummary.Count == 0)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "No se detectaron actividades para revisión ni finalizadas en el periodo.",
                        Opacity = 0.68,
                        TextWrapping = TextWrapping.Wrap
                    });
            }
            else
            {
                var summaryList =
                    new StackPanel
                    {
                        Spacing = 6
                    };

                foreach (var item in completionSummary)
                {
                    summaryList.Children.Add(
                        new Border
                        {
                            Padding =
                                new Thickness(10, 8, 10, 8),
                            CornerRadius =
                                new CornerRadius(6),
                            Background =
                                new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                    Windows.UI.Color.FromArgb(
                                        28,
                                        255,
                                        255,
                                        255)),
                            Child =
                                new TextBlock
                                {
                                    Text =
                                        $"{item.Person} · " +
                                        $"Para revisión: {item.ReadyForReview} · " +
                                        $"Finalizadas: {item.Finalized} · " +
                                        $"Total: {item.ReadyForReview + item.Finalized}",
                                    TextWrapping =
                                        TextWrapping.Wrap,
                                    FontWeight =
                                        Microsoft.UI.Text.FontWeights.SemiBold
                                }
                        });
                }

                root.Children.Add(summaryList);
            }

            var completionDetails =
                completedForReview
                    .Concat(finalized)
                    .OrderBy(item => item.Person)
                    .ThenBy(item => item.CompletionType)
                    .ThenBy(item => item.Title)
                    .ToList();

            if (completionDetails.Count > 0)
            {
                var completionExpander =
                    new Expander
                    {
                        Header =
                            $"Ver detalle de terminadas ({completionDetails.Count})",
                        IsExpanded = false
                    };

                var completionList =
                    new StackPanel
                    {
                        Spacing = 6
                    };

                foreach (var item in completionDetails)
                {
                    completionList.Children.Add(
                        new Border
                        {
                            Padding =
                                new Thickness(10, 8, 10, 8),
                            CornerRadius =
                                new CornerRadius(6),
                            Background =
                                new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                    Windows.UI.Color.FromArgb(
                                        22,
                                        255,
                                        255,
                                        255)),
                            Child =
                                new TextBlock
                                {
                                    Text =
                                        $"{item.CompletionType} · {item.Person}\n" +
                                        $"{item.Title}\n" +
                                        $"{item.Project} · {item.ActivityDate:dd/MM HH:mm}",
                                    TextWrapping =
                                        TextWrapping.Wrap
                                }
                        });
                }

                completionExpander.Content =
                    new ScrollViewer
                    {
                        Content = completionList,
                        MaxHeight = 260,
                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Auto
                    };

                root.Children.Add(completionExpander);
            }

            root.Children.Add(
                new TextBlock
                {
                    Text = "Actividades movidas de ayer a hoy",
                    FontSize = 15,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                });

            var filters =
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };

            var personFilter =
                new ComboBox
                {
                    Width = 180,
                    PlaceholderText = "Todas las personas"
                };

            personFilter.Items.Add(
                new ComboBoxItem
                {
                    Content = "Todas las personas",
                    Tag = string.Empty
                });

            foreach (var person in report.Movements
                         .Select(item => item.Person)
                         .Where(value =>
                             !string.IsNullOrWhiteSpace(value))
                         .Distinct(
                             StringComparer.OrdinalIgnoreCase)
                         .OrderBy(value => value))
            {
                personFilter.Items.Add(
                    new ComboBoxItem
                    {
                        Content = person,
                        Tag = person
                    });
            }

            personFilter.SelectedIndex = 0;

            var projectFilter =
                new ComboBox
                {
                    Width = 260,
                    PlaceholderText = "Todos los proyectos"
                };

            projectFilter.Items.Add(
                new ComboBoxItem
                {
                    Content = "Todos los proyectos",
                    Tag = string.Empty
                });

            foreach (var project in report.Movements
                         .Select(item => item.Project)
                         .Where(value =>
                             !string.IsNullOrWhiteSpace(value))
                         .Distinct(
                             StringComparer.OrdinalIgnoreCase)
                         .OrderBy(value => value))
            {
                projectFilter.Items.Add(
                    new ComboBoxItem
                    {
                        Content = project,
                        Tag = project
                    });
            }

            projectFilter.SelectedIndex = 0;

            filters.Children.Add(personFilter);
            filters.Children.Add(projectFilter);
            root.Children.Add(filters);

            var list =
                new StackPanel
                {
                    Spacing = 6
                };

            void RefreshList()
            {
                list.Children.Clear();

                var person =
                    (personFilter.SelectedItem as ComboBoxItem)?
                        .Tag?.ToString() ??
                    string.Empty;

                var project =
                    (projectFilter.SelectedItem as ComboBoxItem)?
                        .Tag?.ToString() ??
                    string.Empty;

                var visible =
                    report.Movements
                        .Where(item =>
                            string.IsNullOrWhiteSpace(person) ||
                            string.Equals(
                                item.Person,
                                person,
                                StringComparison.OrdinalIgnoreCase))
                        .Where(item =>
                            string.IsNullOrWhiteSpace(project) ||
                            string.Equals(
                                item.Project,
                                project,
                                StringComparison.OrdinalIgnoreCase))
                        .ToList();

                if (visible.Count == 0)
                {
                    list.Children.Add(
                        new TextBlock
                        {
                            Text =
                                "No hay actividades movidas con estos filtros.",
                            Opacity = 0.68
                        });

                    return;
                }

                foreach (var item in visible)
                {
                    list.Children.Add(
                        new Border
                        {
                            Padding = new Thickness(10, 8, 10, 8),
                            CornerRadius = new CornerRadius(6),
                            Background =
                                new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                    Windows.UI.Color.FromArgb(
                                        35,
                                        255,
                                        255,
                                        255)),
                            Child =
                                new TextBlock
                                {
                                    Text =
                                        $"{item.Title}\n" +
                                        $"{item.Person} · {item.Project}\n" +
                                        $"{item.PreviousStart:dd/MM HH:mm} → " +
                                        $"{item.NewStart:dd/MM HH:mm}",
                                    TextWrapping =
                                        TextWrapping.Wrap
                                }
                        });
                }
            }

            personFilter.SelectionChanged +=
                (_, __) => RefreshList();

            projectFilter.SelectionChanged +=
                (_, __) => RefreshList();

            RefreshList();

            root.Children.Add(
                new ScrollViewer
                {
                    Content = list,
                    MaxHeight = 420,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto
                });

            var dialog =
                new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Reporte diario del calendario",
                    Content = root,
                    CloseButtonText = "Cerrar"
                };

            dialog.Resources[
                "ContentDialogMaxWidth"] = 700d;

            await dialog.ShowAsync();
        }
    }
}
