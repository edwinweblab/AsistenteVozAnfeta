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
        private const string LS_DailyCalendarAutomationVersion =
            "Search.Calendar.DailyAutomation.Version";
        private const int DailyCalendarAutomationVersion = 3;
        private const int DailyCalendarRepairLookbackDays = 14;

        // Hora objetivo acordada: dejar las actividades listas antes de que
        // el equipo empiece a conectarse. Si ANFETA estaba cerrada a esta hora,
        // la ejecución pendiente se realiza al abrir el calendario después.
        private const int DailyCalendarAutomationStartHour = 5;

        private bool _dailyCalendarAutomationRunning;

        private const string DailyCalendarReportFileName =
            "calendar_daily_report.json";

        private sealed record DailyCalendarMovement(
            string PageId,
            string Title,
            string Person,
            string Project,
            DateTime PreviousStart,
            DateTime NewStart,
            string SourceState = "");

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
            IReadOnlyList<DailyCalendarCompletion>? Finalized = null,
            int MovedSuspended = 0,
            int SkippedReview = 0,
            int SkippedLocked = 0,
            int DaysScanned = 1,
            DateTime? ScanStart = null,
            DateTime? ScanEnd = null);

        private async Task RunDailyCalendarAutomationIfNeededAsync()
        {
            var now = DateTime.Now;

            // Antes de las 05:00 no se mueve nada automáticamente.
            // Desde las 05:00 en adelante, la primera oportunidad disponible
            // procesa el día si todavía no existe una ejecución válida de hoy.
            if (now.Hour < DailyCalendarAutomationStartHour)
                return;

            var values =
                ApplicationData.Current.LocalSettings.Values;

            var lastRunRaw =
                values[LS_DailyCalendarAutomationLastRun] as string;

            var automationVersion =
                values[LS_DailyCalendarAutomationVersion] is int savedVersion
                    ? savedVersion
                    : 0;

            if (DateTime.TryParseExact(
                    lastRunRaw,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var lastRun) &&
                lastRun.Date == now.Date &&
                automationVersion >= DailyCalendarAutomationVersion)
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
            // El timer, la apertura del calendario y el botón manual pueden
            // coincidir. Solo permitimos una ejecución a la vez para evitar
            // mover la misma actividad dos veces o duplicar consultas a Notion.
            if (_dailyCalendarAutomationRunning)
            {
                if (showResultDialog)
                {
                    StatusText.Text =
                        "Estado: La automatización diaria ya se está ejecutando...";
                }

                return;
            }

            _dailyCalendarAutomationRunning = true;

            var values =
                ApplicationData.Current.LocalSettings.Values;

            var token =
                values["Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                _dailyCalendarAutomationRunning = false;
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var today =
                DateTime.Today;

            var lastRunRaw =
                values[LS_DailyCalendarAutomationLastRun] as string;

            var savedAutomationVersion =
                values[LS_DailyCalendarAutomationVersion] is int version
                    ? version
                    : 0;

            var hasLastRun =
                DateTime.TryParseExact(
                    lastRunRaw,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var lastRun);

            // Primera ejecución de esta corrección o ejecución manual:
            // revisa 14 días para rescatar pendientes que pudieron quedarse
            // atrás por fines de semana o porque la versión anterior solo veía ayer.
            //
            // Ejecuciones automáticas posteriores:
            // revisan desde un día antes de la última ejecución hasta ayer.
            // Así se cubren cambios hechos después de que corrió la automatización
            // sin volver a consultar dos semanas completas todos los días.
            var repairMode =
                showResultDialog ||
                savedAutomationVersion < DailyCalendarAutomationVersion;

            var scanStart =
                repairMode
                    ? today.AddDays(-DailyCalendarRepairLookbackDays)
                    : hasLastRun
                        ? lastRun.Date.AddDays(-1)
                        : today.AddDays(-2);

            var oldestAllowed =
                today.AddDays(-DailyCalendarRepairLookbackDays);

            if (scanStart < oldestAllowed)
                scanStart = oldestAllowed;

            if (scanStart >= today)
                scanStart = today.AddDays(-1);

            var scanEnd =
                today.AddDays(-1);

            var scanDays =
                Enumerable.Range(
                        0,
                        Math.Max(
                            0,
                            (scanEnd - scanStart).Days + 1))
                    .Select(offset =>
                        scanStart.AddDays(offset))
                    .ToList();

            var moved =
                new List<DailyCalendarMovement>();

            var errors =
                new List<string>();

            var completedForReview =
                new List<DailyCalendarCompletion>();

            var finalized =
                new List<DailyCalendarCompletion>();

            var processedPageIds =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            var reviewed = 0;
            var skippedCompleted = 0;
            var movedSuspended = 0;
            var skippedReview = 0;
            var skippedLocked = 0;
            var failed = 0;

            try
            {
                ShowLoadingState(
                    "Estado: Procesando actividades rezagadas...",
                    $"Revisando {scanDays.Count} día(s): " +
                    $"{scanStart:dd/MM} → {scanEnd:dd/MM}. " +
                    "Se moverán prtuzREVISION y sprtuzREVISION a hoy.");

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(20));

                for (var dayIndex = 0;
                     dayIndex < scanDays.Count;
                     dayIndex++)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    var sourceDay =
                        scanDays[dayIndex];

                    StatusText.Text =
                        $"Estado: Revisando rezagadas " +
                        $"{dayIndex + 1}/{scanDays.Count} · " +
                        $"{sourceDay:dd/MM/yyyy}...";

                    // La comprobación manual / de reparación consulta el día real
                    // para no mover una página basándonos en una caché antigua.
                    // En el ciclo automático normal se aprovecha la caché cuando existe.
                    IReadOnlyList<NotionCalendarActivity> activities;

                    if (!repairMode)
                    {
                        var cached =
                            await _notionCalendarService
                                .TryGetCachedDayAsync(
                                    sourceDay,
                                    cts.Token);

                        activities =
                            cached ??
                            await _notionCalendarService
                                .GetDayAsync(
                                    token,
                                    sourceDay,
                                    progress: null,
                                    cts.Token,
                                    forceRefresh: true);
                    }
                    else
                    {
                        activities =
                            await _notionCalendarService
                                .GetDayAsync(
                                    token,
                                    sourceDay,
                                    progress: null,
                                    cts.Token,
                                    forceRefresh: true);
                    }

                    foreach (var activity in activities)
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        if (activity == null ||
                            string.IsNullOrWhiteSpace(
                                activity.PageId) ||
                            !processedPageIds.Add(
                                activity.PageId))
                        {
                            continue;
                        }

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

                        var isPending =
                            ContainsExactAutomationTag(
                                searchable,
                                "prtuzREVISION");

                        var isSuspended =
                            ContainsExactAutomationTag(
                                searchable,
                                "sprtuzREVISION");

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

                        if (isFinalized)
                        {
                            skippedCompleted++;
                            continue;
                        }

                        // rtuzREVISION ya está en la etapa de revisión.
                        // No debe regresar/moverse por la automatización diaria.
                        if (isReadyForReview)
                        {
                            skippedReview++;
                            continue;
                        }

                        if (!isPending &&
                            !isSuspended)
                        {
                            continue;
                        }

                        if (activity.IsAutomationLocked)
                        {
                            skippedLocked++;
                            continue;
                        }

                        // Seguridad adicional: solo se procesan fechas anteriores a hoy.
                        // Si una página aparece duplicada en cachés de varios días,
                        // processedPageIds impide moverla dos veces.
                        if (activity.Start.Date >= today)
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

                            if (isSuspended)
                                movedSuspended++;

                            moved.Add(
                                new DailyCalendarMovement(
                                    activity.PageId,
                                    activity.Title,
                                    assignedPerson,
                                    activity.Project,
                                    oldStart,
                                    updated.Start,
                                    isSuspended
                                        ? "sprtuzREVISION"
                                        : "prtuzREVISION"));
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            errors.Add(
                                $"{activity.Title} ({activity.Start:dd/MM}): " +
                                ex.Message);
                        }
                    }
                }

                var report =
                    new DailyCalendarReport(
                        DateTime.Now,
                        reviewed,
                        moved.Count,
                        skippedCompleted,
                        0,
                        failed,
                        moved,
                        errors,
                        completedForReview,
                        finalized,
                        MovedSuspended: movedSuspended,
                        SkippedReview: skippedReview,
                        SkippedLocked: skippedLocked,
                        DaysScanned: scanDays.Count,
                        ScanStart: scanStart,
                        ScanEnd: scanEnd);

                values[LS_DailyCalendarAutomationLastRun] =
                    today.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

                values[LS_DailyCalendarAutomationVersion] =
                    DailyCalendarAutomationVersion;

                values.Remove(
                    "Search.Calendar.DailyAutomation.LastReport");

                await SaveDailyCalendarReportAsync(
                    report,
                    cts.Token);

                // IMPORTANTE:
                // MoveActivityToDateAsync puede dejar en la caché de HOY solamente
                // las actividades que fueron movidas durante esta automatización.
                // Si dibujamos esa caché directamente, el usuario ve únicamente
                // esas actividades (por ejemplo 7) hasta pulsar "Actualizar".
                //
                // Al terminar la automatización reconstruimos HOY desde Notion
                // para que la primera vista ya contenga TODAS las actividades.
                IReadOnlyList<NotionCalendarActivity> refreshedToday;

                try
                {
                    StatusText.Text =
                        "Estado: Actualizando calendario de hoy después de mover rezagadas...";

                    refreshedToday =
                        await _notionCalendarService
                            .GetDayAsync(
                                token,
                                today,
                                progress: null,
                                cts.Token,
                                forceRefresh: true);
                }
                catch
                {
                    // Respaldo: si falla la recarga completa, conservamos lo que
                    // exista en caché en lugar de perder completamente la vista.
                    refreshedToday =
                        await _notionCalendarService
                            .TryGetCachedDayAsync(
                                today,
                                cts.Token) ??
                        Array.Empty<NotionCalendarActivity>();
                }

                if (_calendarViewActive &&
                    _calendarSelectedDate.Date == today)
                {
                    _calendarActivities =
                        refreshedToday;

                    DrawCalendarPreservingView(
                        _calendarActivities,
                        force: true);
                }

                StatusText.Text =
                    $"Estado: Rezagadas procesadas ✅ " +
                    $"Movidas: {moved.Count} " +
                    $"(suspendidas: {movedSuspended}) · " +
                    $"En revisión omitidas: {skippedReview} · " +
                    $"Bloqueadas: {skippedLocked} · " +
                    $"Terminadas: {skippedCompleted} · " +
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
                    "Estado: El procesamiento de rezagadas fue cancelado por tiempo de espera.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: Error procesando rezagadas → {ex.Message}";
            }
            finally
            {
                _dailyCalendarAutomationRunning = false;
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


        private static async Task SaveDailyCalendarReportAsync(
            DailyCalendarReport report,
            CancellationToken cancellationToken)
        {
            var file =
                await ApplicationData.Current.LocalFolder
                    .CreateFileAsync(
                        DailyCalendarReportFileName,
                        CreationCollisionOption.ReplaceExisting);

            var json =
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions
                    {
                        WriteIndented = false
                    });

            await FileIO.WriteTextAsync(
                file,
                json)
                .AsTask(cancellationToken);
        }

        private static async Task<DailyCalendarReport?>
            LoadDailyCalendarReportAsync()
        {
            StorageFile file;

            try
            {
                file =
                    await ApplicationData.Current.LocalFolder
                        .GetFileAsync(
                            DailyCalendarReportFileName);
            }
            catch
            {
                return null;
            }

            var raw =
                await FileIO.ReadTextAsync(file);

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return JsonSerializer.Deserialize<
                DailyCalendarReport>(raw);
        }

        private async Task ShowLastDailyCalendarReportAsync()
        {
            try
            {
                var report =
                    await LoadDailyCalendarReportAsync();

                if (report == null)
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
                        $"Periodo: " +
                        $"{(report.ScanStart ?? report.GeneratedAt.Date.AddDays(-1)):dd/MM} → " +
                        $"{(report.ScanEnd ?? report.GeneratedAt.Date.AddDays(-1)):dd/MM} · " +
                        $"Días revisados: {report.DaysScanned} · " +
                        $"Actividades revisadas: {report.Reviewed}\n" +
                        $"Movidas: {report.Moved} · " +
                        $"Suspendidas movidas: {report.MovedSuspended} · " +
                        $"En revisión omitidas: {report.SkippedReview} · " +
                        $"Bloqueadas: {report.SkippedLocked} · " +
                        $"Terminadas omitidas: {report.SkippedCompleted} · " +
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
                    Text = "Actividades rezagadas movidas a hoy",
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
                                        $"{item.Person} · {item.Project}" +
                                        (string.IsNullOrWhiteSpace(item.SourceState)
                                            ? string.Empty
                                            : $" · {item.SourceState}") +
                                        $"\n{item.PreviousStart:dd/MM HH:mm} → " +
                                        $"{item.NewStart:dd/MM HH:mm} · " +
                                        $"{Math.Max(1, (item.NewStart.Date - item.PreviousStart.Date).Days)} día(s) rezagada",
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
