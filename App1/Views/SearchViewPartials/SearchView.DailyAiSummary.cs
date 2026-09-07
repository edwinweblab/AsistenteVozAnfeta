using Anfeta.UI.Models.DailyAi;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Reports;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Groq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Windows.Security.Credentials;
using Windows.System;
using System.IO;

namespace Anfeta.UI.Views;

public sealed partial class SearchView
{
    private bool _dailySummaryOpen;
    private bool _floatingAiOpen;
    private bool _floatingAiAnimationStarted;
    private const string SummaryCredential = "ANFETA.OpenAI.Summary";
    private static SolidColorBrush DailyBrush(byte r, byte g, byte b) => new(ColorHelper.FromArgb(255, r, g, b));
    private static readonly SolidColorBrush DailyPanelBrush = DailyBrush(12, 22, 31);
    private static readonly SolidColorBrush DailyCardBrush = DailyBrush(17, 30, 42);
    private static readonly SolidColorBrush DailyBorderBrush = DailyBrush(38, 59, 75);
    private static readonly SolidColorBrush DailyMutedBrush = DailyBrush(157, 177, 194);

    private async void FloatingLocalAi_Click(object sender, RoutedEventArgs e)
    {
        if (_floatingAiOpen) return;
        _floatingAiOpen = true;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var token = GetSavedNotionToken();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Configura primero el token de Notion.");
            StatusText.Text = "Estado: Preparando contexto para la IA local…";
            var progress = await new NotionDailyProgressService().BuildAsync(_notionCalendarService, token, DateTime.Today,
                forceRefresh: false, requireFreshDay: false, cancellationToken: cancellation.Token);
            var snapshot = new DailyAiSnapshotBuilder().Build(progress);
            var conversation = new List<string>();
            var selectedResult = ResultsList?.SelectedItem as Anfeta.UI.Models.Weblab.SearchResultRow;
            var viewContext = JsonSerializer.Serialize(new
            {
                Modulo = CurrentTabMode,
                ModoVisible = ModeText?.Text ?? string.Empty,
                BusquedaActual = SearchBox?.Text ?? string.Empty,
                ElementoSeleccionado = selectedResult is null ? null : new
                {
                    selectedResult.Name,
                    selectedResult.ProjectUpdateStatus,
                    selectedResult.ScheduledDate,
                    selectedResult.ExternalSourceName,
                    selectedResult.ExternalUrl
                }
            });
            var local = new LocalAiService();
            var status = await local.GetStatusAsync(cancellation.Token);
            var groqKey = await App.AppHost.Services.GetRequiredService<ApiKeyService>().GetActiveGroqKeyAsync();
            var cloudReady = !string.IsNullOrWhiteSpace(groqKey);
            var info = new InfoBar
            {
                IsOpen = true, IsClosable = false,
                Title = cloudReady ? "IA rápida lista" : status.ModelInstalled ? "IA local lista" : "Configuración requerida",
                Message = cloudReady ? $"Groq · {GroqDailyAiService.Model} · Ollama queda como respaldo" : status.Message,
                Severity = cloudReady || status.ModelInstalled ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                CornerRadius = new CornerRadius(8)
            };
            var question = new TextBox { PlaceholderText = "Ejemplo: ¿Qué actividades tiene Karla hoy y cuáles requieren atención?", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 76, Padding = new Thickness(12, 8, 12, 8) };
            var suggestions = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            for (var i = 0; i < 2; i++) suggestions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            suggestions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            suggestions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var quickQuestions = new[]
            {
                ("🔥 Prioridades reales", "Identifica las 5 actividades concretas que requieren atención primero hoy. Para cada una indica actividad, proyecto, responsable, horario y motivo comprobable."),
                ("👥 Agenda por persona", "Resume la agenda de hoy por responsable. Incluye cantidades y ejemplos concretos de actividades con proyecto y horario."),
                ("⏰ Próximas actividades", "Lista las próximas actividades de hoy en orden de horario, indicando proyecto y responsable."),
                ("⚠ Sin responsable", "Lista las actividades sin responsable y su proyecto, horario y estado actual.")
            };
            for (var quickIndex = 0; quickIndex < quickQuestions.Length; quickIndex++)
            {
                var item = quickQuestions[quickIndex];
                var chip = new Button { Content = item.Item1, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(8, 7, 8, 7) };
                chip.Click += (_, _) => question.Text = item.Item2;
                Grid.SetColumn(chip, quickIndex % 2);
                Grid.SetRow(chip, quickIndex / 2);
                suggestions.Children.Add(chip);
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var provider = new ComboBox { MinWidth = 190 };
            provider.Items.Add("⚡ Rápido · Groq");
            provider.Items.Add("🔒 Privado · Ollama");
            provider.SelectedIndex = 1; // Prioridad total a Ollama local como solicita el usuario
            var configure = new Button { Content = "Configurar IA local", Visibility = status.ModelInstalled ? Visibility.Collapsed : Visibility.Visible };
            var send = new Button { Content = "✦ Consultar", IsEnabled = cloudReady || status.ModelInstalled, Padding = new Thickness(22, 9, 22, 9), Background = DailyBrush(20, 132, 190), Foreground = DailyBrush(255, 255, 255) };
            actions.Children.Add(provider); actions.Children.Add(configure); actions.Children.Add(send);
            var memoryStatus = DailyText("Conversación nueva · contexto de la vista incluido", 10, DailyMutedBrush);
            var output = new StackPanel { Spacing = 10 };
            output.Children.Add(DailyText("Aquí aparecerá una respuesta sustentada en la agenda real cargada por ANFETA.", 12, DailyMutedBrush));
            configure.Click += async (_, _) =>
            {
                configure.IsEnabled = false;
                try
                {
                    var current = await local.GetStatusAsync(cancellation.Token);
                    if (!current.ServerAvailable)
                    {
                        info.Message = "Instala Ollama, inícialo y vuelve a abrir el asistente.";
                        await Launcher.LaunchUriAsync(new Uri(LocalAiService.InstallerUrl));
                    }
                    else
                    {
                        info.Message = $"Descargando {LocalAiService.Model}…";
                        await local.PullModelAsync(cancellation.Token);
                        info.Severity = InfoBarSeverity.Success; info.Title = "IA local lista"; info.Message = $"{LocalAiService.Model} instalado correctamente.";
                        configure.Visibility = Visibility.Collapsed; send.IsEnabled = true;
                    }
                }
                catch (Exception ex) { info.Severity = InfoBarSeverity.Error; info.Message = ex.Message; }
                finally { configure.IsEnabled = true; }
            };
            send.Click += async (_, _) =>
            {
                send.IsEnabled = false;
                try
                {
                    var userQuestion = question.Text?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(userQuestion))
                    {
                        userQuestion = "Identifica las prioridades reales de hoy y resume el estado operativo.";
                        question.Text = userQuestion;
                    }
                    var memory = string.Join("\n", conversation.TakeLast(6));
                    var useCloud = provider.SelectedIndex == 0 && cloudReady;
                    var responseProvider = useCloud ? $"Groq Cloud · {GroqDailyAiService.Model}" : $"Ollama local · {LocalAiService.Model}";
                    ShowReasoningAnimation(output, responseProvider);
                    DailyAiAssistantResult result;
                    if (useCloud)
                    {
                        try
                        {
                            result = await new GroqDailyAiService().AskAsync(groqKey!, snapshot, userQuestion, cancellation.Token, memory, viewContext);
                            info.Title = "Respuesta rápida completada";
                            info.Message = $"Respondió Groq · {GroqDailyAiService.Model}";
                        }
                        catch (Exception cloudError) when (status.ModelInstalled)
                        {
                            info.Severity = InfoBarSeverity.Warning;
                            info.Title = "Groq no respondió, usando Ollama local";
                            info.Message = $"{FriendlyAiError(cloudError)} Se redirigió la consulta a la IA local.";
                            provider.SelectedIndex = 1;
                            ShowReasoningAnimation(output, $"Ollama local · {LocalAiService.Model}");
                            result = await local.AskAsync(snapshot, userQuestion, cancellation.Token, memory, viewContext);
                            responseProvider = $"Ollama local · {LocalAiService.Model} (respaldo)";
                        }
                    }
                    else
                    {
                        result = await local.AskAsync(snapshot, userQuestion, cancellation.Token, memory, viewContext);
                    }
                    RenderAssistantResult(output, result, false, responseProvider);
                    conversation.Add($"Usuario: {userQuestion}");
                    conversation.Add($"ANFETA: {CleanAiText(result.Answer)}");
                    while (conversation.Count > 8) conversation.RemoveAt(0);
                    memoryStatus.Text = $"Memoria activa · {conversation.Count / 2} intercambio(s) · contexto de {CurrentTabMode}";
                    question.Text = string.Empty;
                }
                catch (Exception ex) { SetAiMessage(output, FriendlyAiError(ex), true); }
                finally { send.IsEnabled = cloudReady || status.ModelInstalled; }
            };
            var hero = new StackPanel { Spacing = 4 };
            hero.Children.Add(DailyText("COPILOTO OPERATIVO · NUBE O LOCAL", 11, DailyBrush(64, 196, 255), true));
            hero.Children.Add(DailyText("Pregunta por tu operación de hoy", 22, DailyBrush(241, 247, 252), true));
            hero.Children.Add(DailyText($"Contexto actualizado: {snapshot.Date:dddd, dd 'de' MMMM} · {snapshot.Metrics.TotalActivities} actividades · {snapshot.Metrics.TotalProjects} proyectos", 12, DailyMutedBrush));
            var heroCard = new Border { Background = DailyBrush(12, 31, 44), BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 15, 18, 15), Child = hero };

            var metrics = new Grid { ColumnSpacing = 8 };
            for (var i = 0; i < 4; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var metricValues = new[]
            {
                (snapshot.Metrics.PendingToday, "Pendientes", DailyBrush(245,185,55)),
                (snapshot.Metrics.LaggingActivities, "Rezagadas", DailyBrush(255,112,112)),
                (snapshot.Metrics.UnassignedActivities, "Sin responsable", DailyBrush(180,115,255)),
                (snapshot.Metrics.CompletedToday, "Terminadas hoy", DailyBrush(42,207,142))
            };
            for (var i = 0; i < metricValues.Length; i++)
            {
                var value = new StackPanel { Spacing = 1 };
                value.Children.Add(DailyText(metricValues[i].Item1.ToString(), 19, metricValues[i].Item3, true));
                value.Children.Add(DailyText(metricValues[i].Item2, 10, DailyMutedBrush, true));
                var card = new Border { Background = DailyCardBrush, BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 8, 11, 8), Child = value };
                Grid.SetColumn(card, i); metrics.Children.Add(card);
            }

            var composer = new StackPanel { Spacing = 9 };
            composer.Children.Add(DailyText("¿Qué necesitas entender?", 13, DailyBrush(235, 243, 249), true));
            composer.Children.Add(question); composer.Children.Add(suggestions); composer.Children.Add(actions); composer.Children.Add(memoryStatus);
            var composerCard = new Border { Background = DailyCardBrush, BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = composer };
            var answerCard = new Border { Background = DailyPanelBrush, BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = output };

            var availableWidth = XamlRoot?.Size.Width ?? 1280;
            var availableHeight = XamlRoot?.Size.Height ?? 720;
            var responsiveWidth = Math.Max(640, Math.Min(1080, availableWidth - 120));
            var responsiveHeight = Math.Max(380, Math.Min(530, availableHeight - 170));
            var content = new Grid { Width = responsiveWidth, Height = responsiveHeight, RowSpacing = 8 };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(heroCard, 0); Grid.SetRow(metrics, 1); Grid.SetRow(info, 2);

            var workArea = new Grid { ColumnSpacing = 12 };
            workArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            workArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
            var answerScroll = new ScrollViewer
            {
                Content = answerCard,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var composerScroll = new ScrollViewer
            {
                Content = composerCard,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetColumn(answerScroll, 0); Grid.SetColumn(composerScroll, 1);
            workArea.Children.Add(answerScroll); workArea.Children.Add(composerScroll);
            Grid.SetRow(workArea, 3);
            content.Children.Add(heroCard); content.Children.Add(metrics); content.Children.Add(info); content.Children.Add(workArea);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "ANFETA AI", CloseButtonText = "Cerrar",
                Content = content,
                Background = DailyPanelBrush,
                MaxHeight = availableHeight - 40
            };
            dialog.Resources["ContentDialogMinWidth"] = responsiveWidth + 40;
            dialog.Resources["ContentDialogMaxWidth"] = responsiveWidth + 40;
            dialog.Closing += (_, _) => cancellation.Cancel();
            StatusText.Text = "Estado: Listo";
            await dialog.ShowAsync();
        }
        catch (Exception ex) { StatusText.Text = "No se pudo abrir la IA local: " + ex.Message; }
        finally { cancellation.Cancel(); _floatingAiOpen = false; StatusText.Text = "Estado: Listo"; }
    }

    private async void DailyAiSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_dailySummaryOpen) return;
        _dailySummaryOpen = true;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var token = GetSavedNotionToken();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Configura primero el token de Notion.");
            StatusText.Text = "Estado: Preparando Resumen IA desde Avance Diario…";

            var progress = await new NotionDailyProgressService().BuildAsync(_notionCalendarService, token, DateTime.Today,
                forceRefresh: false, requireFreshDay: false, cancellationToken: cancellation.Token);
            var snapshot = new DailyAiSnapshotBuilder().Build(progress);
            var paths = await new DailyReportDatasetService().ExportAsync(snapshot, cancellation.Token);
            var m = snapshot.Metrics;

            // -- CONTROLES DE TAMAÑO (CORREGIDOS PARA WINUI 3) --
            var sizeControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10) };
            var btnNormal = new Button { Content = "Normal" };
            var btnLarge = new Button { Content = "Grande" };
            var btnXL = new Button { Content = "Extra grande" };
            sizeControls.Children.Add(btnNormal);
            sizeControls.Children.Add(btnLarge);
            sizeControls.Children.Add(btnXL);

            var dashboard = new StackPanel { Spacing = 14 };
            dashboard.Children.Add(sizeControls);
            dashboard.Children.Add(CreateDailyHeader(snapshot));
            dashboard.Children.Add(CreateDailyKpis(m));
            dashboard.Children.Add(CreateCriticalProjectsCard(snapshot));
            dashboard.Children.Add(CreatePeopleCard(snapshot));

            var dataPanel = new StackPanel { Spacing = 8 };
            dataPanel.Children.Add(DailyText("Los cálculos se guardaron localmente. OpenAI recibe el snapshot operativo, pero no cuerpos de Notion, comentarios ni Dropbox.", 13, DailyMutedBrush));
            var openFolder = new Button { Content = "📂 Abrir datos JSON/CSV", HorizontalAlignment = HorizontalAlignment.Left };
            openFolder.Click += async (_, _) => await Launcher.LaunchFolderPathAsync(paths.OutputDirectory);
            dataPanel.Children.Add(openFolder);
            dashboard.Children.Add(CreateDailyCard("DATOS DEL REPORTE", "Salida auditable", DailyBrush(29, 180, 242), dataPanel));

            var miaoService = new MiaoVisionInstallerService();
            var miaoStatus = await miaoService.GetStatusAsync(cancellation.Token);
            var miaoPanel = new StackPanel { Spacing = 9 };
            var miaoInfo = new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = miaoStatus.IsInstalled ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
                Title = miaoStatus.IsInstalled ? "Motor listo" : "Instalación requerida",
                Message = miaoStatus.Message
            };
            miaoPanel.Children.Add(miaoInfo);

            if (!miaoStatus.IsInstalled)
            {
                AddMiaoInstallerControls(miaoPanel, miaoService, miaoInfo, cancellation.Token);
            }
            {
                miaoPanel.Children.Add(DailyText(miaoStatus.IsInstalled
                    ? "El reporte HTML se genera localmente y Microsoft Edge crea el PDF. Miao queda disponible para visualizaciones posteriores."
                    : "HTML y PDF funcionan sin esperar la instalación. Miao es opcional para visualizaciones posteriores.", 13, DailyMutedBrush));
                var exportPanel = new StackPanel { Spacing = 8 };
                var exportButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var btnGenerateHtml = new Button { Content = "🌐 Abrir HTML" };
                var btnGeneratePdf = new Button { Content = "📄 Exportar PDF", Background = DailyBrush(42, 207, 142), Foreground = DailyBrush(0, 0, 0) };
                var btnMiao = new Button { Content = "📊 Vista Miao", IsEnabled = miaoStatus.IsInstalled };
                var btnMiaoPdf = new Button { Content = "📑 PDF Miao", IsEnabled = miaoStatus.IsInstalled };
                var pdfStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = DailyMutedBrush, TextWrapping = TextWrapping.Wrap };

                exportButtons.Children.Add(btnGenerateHtml);
                exportButtons.Children.Add(btnGeneratePdf);
                exportButtons.Children.Add(btnMiao);
                exportButtons.Children.Add(btnMiaoPdf);
                exportPanel.Children.Add(exportButtons);
                exportPanel.Children.Add(pdfStatus);
                miaoPanel.Children.Add(exportPanel);

                btnGenerateHtml.Click += async (_, _) =>
                {
                    btnGenerateHtml.IsEnabled = false;
                    pdfStatus.Text = "Generando HTML…";
                    try
                    {
                        var htmlPath = Path.Combine(paths.OutputDirectory, $"Resumen_Anfeta_{snapshot.Date:yyyyMMdd}.html");
                        await DailyReportGenerator.GenerateHtmlAsync(paths.JsonPath, htmlPath, cancellation.Token);
                        pdfStatus.Text = "HTML listo.";
                        await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(htmlPath));
                    }
                    catch (Exception ex) { pdfStatus.Text = $"Error: {ex.Message}"; }
                    finally { btnGenerateHtml.IsEnabled = true; }
                };

                btnMiao.Click += async (_, _) =>
                {
                    btnMiao.IsEnabled = false;
                    pdfStatus.Text = "Miao está analizando los datos…";
                    try
                    {
                        var htmlPath = await new MiaoVisionReportService().GenerateDashboardAsync(snapshot, paths.OutputDirectory, cancellation.Token);
                        pdfStatus.Text = "Vista Miao lista.";
                        await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(htmlPath));
                    }
                    catch (Exception ex) { pdfStatus.Text = $"Error Miao: {ex.Message}"; }
                    finally { btnMiao.IsEnabled = true; }
                };

                btnMiaoPdf.Click += async (_, _) =>
                {
                    btnMiaoPdf.IsEnabled = false;
                    pdfStatus.Text = "Generando Vista Miao y PDF…";
                    try
                    {
                        var htmlPath = await new MiaoVisionReportService().GenerateDashboardAsync(snapshot, paths.OutputDirectory, cancellation.Token);
                        var pdfPath = Path.Combine(paths.OutputDirectory, $"Resumen_Anfeta_Miao_{snapshot.Date:yyyyMMdd}.pdf");
                        await DailyReportGenerator.GeneratePdfFromHtmlAsync(htmlPath, pdfPath, cancellation.Token);
                        pdfStatus.Text = "PDF Miao listo.";
                        await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(pdfPath));
                    }
                    catch (Exception ex) { pdfStatus.Text = $"Error PDF Miao: {ex.Message}"; }
                    finally { btnMiaoPdf.IsEnabled = true; }
                };

                btnGeneratePdf.Click += async (_, _) =>
                {
                    btnGeneratePdf.IsEnabled = false;
                    pdfStatus.Text = "Generando HTML y PDF, espera...";
                    try
                    {
                        var pdfPath = Path.Combine(paths.OutputDirectory, $"Resumen_Anfeta_{snapshot.Date:yyyyMMdd}.pdf");
                        await DailyReportGenerator.GeneratePdfAsync(paths.JsonPath, pdfPath, cancellation.Token);

                        pdfStatus.Text = "PDF listo.";
                        await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(pdfPath));
                    }
                    catch (Exception ex)
                    {
                        pdfStatus.Text = $"Error: {ex.Message}";
                    }
                    finally
                    {
                        btnGeneratePdf.IsEnabled = true;
                    }
                };
            }
            dashboard.Children.Add(CreateDailyCard("MIAO VISION", "Motor local de HTML y PDF", DailyBrush(42, 207, 142), miaoPanel));

            dashboard.Children.Add(CreateReportAssistantCard(snapshot, cancellation.Token));
            dashboard.Children.Add(CreateAiCard(snapshot, cancellation.Token));

            var rootWidth = XamlRoot?.Size.Width ?? 1100d;
            var dialogWidth = Math.Max(520d, rootWidth - 72d);
            var availableWidth = Math.Max(420d, dialogWidth - 48d);
            var contentBorder = new Border
            {
                Background = DailyPanelBrush,
                BorderBrush = DailyBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                MinWidth = 0,
                Width = Math.Min(920d, availableWidth),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = dashboard
            };

            var scroller = new ScrollViewer
            {
                Content = contentBorder,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(320, XamlRoot.Size.Height - 130)
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Resumen del día · IA",
                CloseButtonText = "Cerrar",
                Background = DailyPanelBrush, // Elimina el fondo gris contenedor nativo
                Foreground = DailyBrush(241, 247, 252),
                Content = scroller
            };

            // ContentDialog no vuelve a medir su marco después de ShowAsync. Reservamos
            // desde el inicio el ancho mayor y solo cambiamos el tablero interior.
            dialog.MinWidth = dialogWidth;
            dialog.MaxWidth = dialogWidth;
            dialog.Resources["ContentDialogMinWidth"] = dialogWidth;
            dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;

            void ApplyDialogWidth(double requested)
            {
                var width = Math.Min(requested, availableWidth);
                contentBorder.Width = width;
            }
            ApplyDialogWidth(contentBorder.Width);
            btnNormal.Click += (_, _) => ApplyDialogWidth(760d);
            btnLarge.Click += (_, _) => ApplyDialogWidth(1100d);
            btnXL.Click += (_, _) => ApplyDialogWidth(Math.Max(420d, (XamlRoot?.Size.Width ?? 1300d) - 100d));

            dialog.Closing += (_, _) => cancellation.Cancel();
            StatusText.Text = $"Estado: Resumen diario listo · {m.TotalProjects} proyectos · {m.TotalActivities} actividades";
            await dialog.ShowAsync();
        }
        catch (Exception ex) { StatusText.Text = "No se pudo abrir el resumen: " + ex.Message; }
        finally { cancellation.Cancel(); _dailySummaryOpen = false; }
    }

    private static UIElement CreateDailyHeader(DailyAiSnapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(DailyText("ANFETA · RESUMEN OPERATIVO", 12, DailyBrush(72, 190, 245), true));
        panel.Children.Add(DailyText(snapshot.Date.ToString("dddd, dd 'de' MMMM 'de' yyyy"), 23, DailyBrush(241, 247, 252), true));
        panel.Children.Add(DailyText("Métricas reales calculadas por Avance Diario", 13, DailyMutedBrush));
        return panel;
    }

    private static UIElement CreateDailyKpis(DailyAiMetrics m)
    {
        var values = new (string Label, int Value, SolidColorBrush Accent)[]
        {
            ("PROYECTOS", m.TotalProjects, DailyBrush(29,180,242)), ("ACTIVIDADES", m.TotalActivities, DailyBrush(90,165,255)),
            ("REZAGADAS", m.LaggingActivities, DailyBrush(255,88,96)), ("PENDIENTES", m.PendingToday, DailyBrush(245,185,55)),
            ("TERMINADAS HOY", m.CompletedToday, DailyBrush(42,207,142)), ("EN REVISIÓN", m.ProjectsInReview, DailyBrush(180,115,255)),
            ("SIN RESPONSABLE", m.UnassignedActivities, DailyBrush(255,145,70)), ("SIN CHECKLIST", m.MissingChecklistActivities, DailyBrush(135,151,165))
        };
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var i = 0; i < 2; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < values.Length; i++)
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(DailyText(values[i].Value.ToString(), 22, values[i].Accent, true));
            stack.Children.Add(DailyText(values[i].Label, 10, DailyMutedBrush, true));
            var border = new Border
            {
                Background = DailyCardBrush,
                BorderBrush = DailyBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 9, 12, 9),
                Child = stack
            };
            Grid.SetColumn(border, i % 2); Grid.SetRow(border, i / 2); grid.Children.Add(border);
        }
        return grid;
    }

    private static UIElement CreateCriticalProjectsCard(DailyAiSnapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 6 };
        var rows = snapshot.Projects.Where(x => x.IsCritical).Take(10).ToList();
        if (rows.Count == 0) panel.Children.Add(DailyText("No hay proyectos críticos con las reglas actuales.", 13, DailyMutedBrush));
        foreach (var project in rows)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.34, GridUnitType.Star), MinWidth = 105 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.66, GridUnitType.Star), MinWidth = 0 });
            var name = DailyText(project.ProjectName, 13, DailyBrush(241, 247, 252), true);
            var reason = DailyText(string.Join("  ·  ", project.CriticalReasons), 12, DailyBrush(255, 159, 95));
            Grid.SetColumn(name, 0); Grid.SetColumn(reason, 1); row.Children.Add(name); row.Children.Add(reason);
            panel.Children.Add(new Border
            {
                Background = DailyBrush(20, 32, 43),
                CornerRadius = new CornerRadius(5),
                BorderBrush = DailyBrush(81, 48, 46),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(9, 7, 9, 7),
                Child = row
            });
        }
        return CreateDailyCard("PROYECTOS QUE REQUIEREN ATENCIÓN", $"{rows.Count} mostrados", DailyBrush(255, 88, 96), panel);
    }

    private static UIElement CreatePeopleCard(DailyAiSnapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(CreatePeopleRow("RESPONSABLE", "ACT.", "PEND.", "REZ.", true));
        foreach (var person in snapshot.People.Take(12))
            panel.Children.Add(CreatePeopleRow(person.Name, person.ActivitiesToday.ToString(), person.PendingToday.ToString(), person.LaggingActivities.ToString(), false));
        return CreateDailyCard("CARGA POR PERSONA", "Actividad operativa del día", DailyBrush(29, 180, 242), panel);
    }

    private static UIElement CreatePeopleRow(string name, string activities, string pending, string lagging, bool header)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(6, 4, 6, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        var values = new[] { name, activities, pending, lagging };
        for (var i = 0; i < values.Length; i++) { var text = DailyText(values[i], header ? 10 : 12, header ? DailyMutedBrush : DailyBrush(224, 234, 242), header); Grid.SetColumn(text, i); grid.Children.Add(text); }
        return grid;
    }

    private void AddMiaoInstallerControls(StackPanel panel, MiaoVisionInstallerService service, InfoBar info, CancellationToken cancellationToken)
    {
        var consent = new CheckBox { Content = new TextBlock { Text = "Autorizo descargar Miao Vision 0.6.2 para Windows (aprox. 91 MB) desde GitHub.", TextWrapping = TextWrapping.Wrap } };
        var install = new Button { Content = "Instalar motor de reportes", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
        consent.Checked += (_, _) => install.IsEnabled = true;
        consent.Unchecked += (_, _) => install.IsEnabled = false;
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false; consent.IsEnabled = false;
            var finished = false;
            try
            {
                var downloadProgress = new Progress<double>(value =>
                {
                    if (finished) return;
                    install.Content = $"Descargando y verificando… {value:0}%";
                    info.Title = "Instalando motor"; info.Message = $"Descarga oficial en curso · {value:0}%";
                });
                var installed = await service.InstallAsync(downloadProgress, cancellationToken);
                finished = true;
                info.Severity = InfoBarSeverity.Success; info.Title = "Instalación completada";
                info.Message = installed.Message + " Ya puede cerrarse este resumen y volver a abrirse.";
                install.Content = "Motor instalado ✓"; consent.Visibility = Visibility.Collapsed;
                StatusText.Text = "Estado: Miao Vision instalado y verificado correctamente ✅";
            }
            catch (OperationCanceledException) { finished = true; info.Severity = InfoBarSeverity.Warning; info.Title = "Instalación cancelada"; info.Message = "No se realizaron cambios incompletos."; install.Content = "Reintentar instalación"; }
            catch (Exception ex) { finished = true; info.Severity = InfoBarSeverity.Error; info.Title = "No se pudo instalar"; info.Message = ex.Message; install.Content = "Reintentar instalación"; }
            finally { consent.IsEnabled = true; if (consent.Visibility == Visibility.Visible) install.IsEnabled = consent.IsChecked == true; }
        };
        panel.Children.Add(consent); panel.Children.Add(install);
    }

    private UIElement CreateAiCard(DailyAiSnapshot snapshot, CancellationToken cancellationToken)
    {
        var panel = new StackPanel { Spacing = 9 };
        var keyBox = new PasswordBox { Header = "Clave de OpenAI", PlaceholderText = "Solo para configurarla o cambiarla" };
        var remember = new CheckBox { Content = "Guardar en Credenciales de Windows" };
        var consent = new CheckBox { Content = new TextBlock { Text = "Autorizo enviar el snapshot operativo a OpenAI y el consumo de mi cuenta API.", TextWrapping = TextWrapping.Wrap } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var generate = new Button { Content = "✦ Generar interpretación", IsEnabled = false };
        var forget = new Button { Content = "Borrar clave" };
        actions.Children.Add(generate); actions.Children.Add(forget);
        var output = new StackPanel { Spacing = 8 };
        output.Children.Add(DailyText("El resumen operativo ya está disponible sin IA.", 13, DailyMutedBrush));
        var vault = new PasswordVault();
        try { vault.Retrieve(SummaryCredential, "user"); output.Children.Clear(); output.Children.Add(DailyText("Hay una clave guardada. No se enviará nada hasta que lo autorices.", 13, DailyMutedBrush)); } catch { }
        consent.Checked += (_, _) => generate.IsEnabled = snapshot.Activities.Count > 0;
        consent.Unchecked += (_, _) => generate.IsEnabled = false;
        forget.Click += (_, _) => { try { vault.Remove(vault.Retrieve(SummaryCredential, "user")); SetAiMessage(output, "Clave guardada eliminada.", false); } catch { SetAiMessage(output, "No hay una clave guardada.", false); } keyBox.Password = ""; };
        generate.Click += async (_, _) =>
        {
            generate.IsEnabled = false; consent.IsEnabled = false;
            try
            {
                var key = keyBox.Password.Trim();
                if (key.Length == 0) { var credential = vault.Retrieve(SummaryCredential, "user"); credential.RetrievePassword(); key = credential.Password; }
                if (remember.IsChecked == true) vault.Add(new PasswordCredential(SummaryCredential, "user", key));
                keyBox.Password = ""; SetAiMessage(output, "Generando interpretación…", false);
                RenderNarrative(output, await new DailyAiSummaryService().GenerateAsync(key, snapshot, cancellationToken));
            }
            catch (OperationCanceledException) { SetAiMessage(output, "Solicitud cancelada o tiempo agotado.", true); }
            catch (Exception ex) { SetAiMessage(output, ex is InvalidOperationException ? ex.Message : "No se obtuvo una interpretación válida; los datos locales siguen disponibles.", true); }
            finally { consent.IsEnabled = true; generate.IsEnabled = consent.IsChecked == true && snapshot.Activities.Count > 0; }
        };
        panel.Children.Add(keyBox); panel.Children.Add(remember); panel.Children.Add(consent); panel.Children.Add(actions); panel.Children.Add(output);
        return CreateDailyCard("OPENAI (OPCIONAL)", "Alternativa con clave propia · no necesaria para la IA local", DailyBrush(180, 115, 255), panel);
    }

    private UIElement CreateReportAssistantCard(DailyAiSnapshot snapshot, CancellationToken cancellationToken)
    {
        var panel = new StackPanel { Spacing = 9 };
        var question = new TextBox { Header = "Pregunta o instrucción", PlaceholderText = "Ejemplo: ¿Qué debería atender John primero?", TextWrapping = TextWrapping.Wrap };
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var value in new[] { "Prioridades", "Plan del día", "Resumen para compartir" })
        {
            var button = new Button { Content = value };
            button.Click += (_, _) => question.Text = value switch
            {
                "Prioridades" => "Ordena las prioridades del día y explica brevemente por qué.",
                "Plan del día" => "Propón un plan prudente para hoy por responsable.",
                _ => "Genera un resumen ejecutivo listo para compartir."
            };
            quick.Children.Add(button);
        }
        var localStatus = DailyText("IA local: comprobando al usarla…", 12, DailyMutedBrush);
        var setup = new Button { Content = "Configurar IA local", HorizontalAlignment = HorizontalAlignment.Left };
        var ask = new Button { Content = "✦ Consultar IA local", HorizontalAlignment = HorizontalAlignment.Left };
        var output = new StackPanel { Spacing = 8 };
        output.Children.Add(DailyText("Las respuestas son sugerencias de solo lectura. No modifican Notion.", 13, DailyMutedBrush));
        setup.Click += async (_, _) =>
        {
            setup.IsEnabled = false;
            try
            {
                var service = new LocalAiService();
                var status = await service.GetStatusAsync(cancellationToken);
                if (!status.ServerAvailable)
                {
                    localStatus.Text = "Se abrió el instalador oficial. Instala Ollama, inícialo y vuelve a pulsar Configurar.";
                    await Launcher.LaunchUriAsync(new Uri(LocalAiService.InstallerUrl));
                }
                else if (!status.ModelInstalled)
                {
                    localStatus.Text = $"Descargando {LocalAiService.Model}; puede tardar varios minutos…";
                    await service.PullModelAsync(cancellationToken);
                    localStatus.Text = $"IA local lista · {LocalAiService.Model} ✓";
                }
                else localStatus.Text = status.Message + " ✓";
            }
            catch (Exception ex) { localStatus.Text = "No se pudo configurar: " + ex.Message; }
            finally { setup.IsEnabled = true; }
        };
        ask.Click += async (_, _) =>
        {
            ask.IsEnabled = false;
            try
            {
                SetAiMessage(output, $"{LocalAiService.Model} está analizando el reporte localmente…", false);
                var result = await new LocalAiService().AskAsync(snapshot, question.Text, cancellationToken);
                RenderAssistantResult(output, result, false);
            }
            catch (Exception ex) { SetAiMessage(output, ex is InvalidOperationException ? ex.Message : "No se pudo completar la consulta.", true); }
            finally { ask.IsEnabled = true; }
        };
        panel.Children.Add(question); panel.Children.Add(quick); panel.Children.Add(localStatus); panel.Children.Add(setup); panel.Children.Add(ask); panel.Children.Add(output);
        return CreateDailyCard("ASISTENTE LOCAL DEL REPORTE", "Gratis · privado · sin clave API", DailyBrush(90, 165, 255), panel);
    }

    private static void RenderAssistantResult(StackPanel output, DailyAiAssistantResult value, bool cached, string? provider = null)
    {
        output.Children.Clear();
        var source = cached ? "Respuesta recuperada del caché" : provider is null ? "Respuesta generada por el asistente configurado" : $"Respuesta generada con {provider}";
        output.Children.Add(DailyText(source, 11, cached ? DailyBrush(42, 207, 142) : DailyMutedBrush, true));
        var conclusion = new StackPanel { Spacing = 5 };
        conclusion.Children.Add(DailyText("LECTURA RÁPIDA", 10, DailyBrush(86, 199, 255), true));
        conclusion.Children.Add(DailyText(CleanAiText(value.Answer), 13, DailyBrush(235, 243, 249)));
        output.Children.Add(new Border
        {
            Background = DailyBrush(14, 35, 49), BorderBrush = DailyBrush(35, 79, 105), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = conclusion
        });

        if (value.Priorities.Count > 0 || value.DayPlan.Count > 0)
        {
            var lists = new StackPanel { Spacing = 8 };
            if (value.Priorities.Count > 0) lists.Children.Add(CreateAssistantListCard("EVIDENCIA RELACIONADA", value.Priorities, DailyBrush(245, 185, 55)));
            if (value.DayPlan.Count > 0) lists.Children.Add(CreateAssistantListCard("CONTEXTO OPERATIVO", value.DayPlan, DailyBrush(29, 180, 242)));
            output.Children.Add(lists);
        }
        if (!string.IsNullOrWhiteSpace(value.WhatsAppMessage)) output.Children.Add(CreateShareBlock("WHATSAPP", value.WhatsAppMessage));
        if (!string.IsNullOrWhiteSpace(value.EmailMessage)) output.Children.Add(CreateShareBlock("CORREO", value.EmailMessage));
    }

    private static Border CreateAssistantListCard(string title, IReadOnlyList<string> rows, SolidColorBrush accent)
    {
        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(DailyText(title, 10, accent, true));
        for (var index = 0; index < rows.Count; index++)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var number = new Border
            {
                Width = 22, Height = 22, Background = DailyBrush(25, 48, 64), CornerRadius = new CornerRadius(11),
                Child = new TextBlock { Text = (index + 1).ToString(), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var text = DailyText(CleanAiText(rows[index]), 11, DailyBrush(220, 231, 239));
            Grid.SetColumn(number, 0); Grid.SetColumn(text, 1); line.Children.Add(number); line.Children.Add(text);
            content.Children.Add(line);
        }
        return new Border
        {
            Background = DailyBrush(15, 27, 37), BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(11), Child = content
        };
    }

    private static UIElement CreateShareBlock(string title, string text)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(DailyText(title, 10, DailyBrush(42, 207, 142), true));
        text = CleanAiText(text);
        content.Children.Add(DailyText(text, 12, DailyBrush(220, 231, 239)));
        var copy = new Button { Content = "Copiar", HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            copy.Content = "Copiado ✓";
        };
        content.Children.Add(copy);
        return new Border { Background = DailyBrush(15, 27, 37), CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Child = content };
    }

    private static void RenderNarrative(StackPanel output, DailyAiNarrative value)
    {
        output.Children.Clear();
        output.Children.Add(DailyText(value.Summary, 14, DailyBrush(241, 247, 252), true));
        AddNarrativeSection(output, "ATENCIÓN", value.AttentionItems, DailyBrush(255, 112, 112));
        AddNarrativeSection(output, "SEÑALES POSITIVAS", value.PositiveSignals, DailyBrush(42, 207, 142));
        AddNarrativeSection(output, "PRIORIDADES", value.Priorities, DailyBrush(245, 185, 55));
        AddNarrativeSection(output, "CARGA", value.WorkloadObservations, DailyBrush(29, 180, 242));
    }

    private static void AddNarrativeSection(StackPanel output, string title, IReadOnlyList<string> rows, SolidColorBrush accent)
    {
        if (rows.Count == 0) return;
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(DailyText(title, 10, accent, true));
        foreach (var row in rows) stack.Children.Add(DailyText("• " + CleanAiText(row), 12, DailyBrush(220, 231, 239)));
        output.Children.Add(new Border { Background = DailyBrush(15, 27, 37), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 8, 10, 8), Child = stack });
    }

    private static string CleanAiText(string text) => (text ?? string.Empty)
        .Replace("**", string.Empty).Replace("__", string.Empty).Replace("```", string.Empty).Trim();

    private static string FriendlyAiError(Exception error)
    {
        if (error is OperationCanceledException) return "La consulta fue cancelada.";
        if (error is JsonException || error.Message.Contains("JSON", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("end of", StringComparison.OrdinalIgnoreCase))
            return "La respuesta quedó incompleta. Intenta nuevamente o cambia al modo Rápido · Groq.";
        return error is InvalidOperationException ? error.Message : "No se pudo completar la consulta. Revisa la conexión e intenta otra vez.";
    }

    private void FloatingLocalAiButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (_floatingAiAnimationStarted || FloatingAiPulse is null) return;
        _floatingAiAnimationStarted = true;
        var glow = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0.18,
            To = 0.72,
            Duration = new Duration(TimeSpan.FromSeconds(1.35)),
            AutoReverse = true,
            RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(glow, FloatingAiPulse);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(glow, "Opacity");
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(glow);
        storyboard.Begin();
    }

    private static void SetAiMessage(StackPanel output, string message, bool error)
    {
        output.Children.Clear(); output.Children.Add(DailyText(message, 13, error ? DailyBrush(255, 112, 112) : DailyMutedBrush));
    }

    private static void ShowReasoningAnimation(StackPanel output, string providerName)
    {
        output.Children.Clear();

        var ring = new ProgressRing
        {
            IsActive = true,
            Width = 26,
            Height = 26,
            Foreground = DailyBrush(64, 196, 255),
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = DailyText($"{providerName} está razonando la respuesta…", 13, DailyBrush(241, 247, 252), true);
        var subtitle = DailyText("Analizando métricas del día, prioridades y contexto de Notion...", 11, DailyMutedBrush);

        var textStack = new StackPanel { Spacing = 3 };
        textStack.Children.Add(title);
        textStack.Children.Add(subtitle);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(ring);
        row.Children.Add(textStack);

        var card = new Border
        {
            Background = DailyBrush(14, 32, 46),
            BorderBrush = DailyBrush(35, 79, 105),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Child = row
        };

        output.Children.Add(card);
    }

    private static Border CreateDailyCard(string title, string subtitle, SolidColorBrush accent, UIElement content)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var stripe = new Border { Background = accent, CornerRadius = new CornerRadius(2) };
        var labels = new StackPanel { Spacing = 1 };
        labels.Children.Add(DailyText(title, 12, DailyBrush(235, 243, 249), true));
        labels.Children.Add(DailyText(subtitle, 11, DailyMutedBrush));
        Grid.SetColumn(stripe, 0); Grid.SetColumn(labels, 1); header.Children.Add(stripe); header.Children.Add(labels);
        var panel = new StackPanel { Spacing = 10 }; panel.Children.Add(header); panel.Children.Add(content);
        return new Border
        {
            Background = DailyCardBrush,
            BorderBrush = DailyBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13),
            Child = panel
        };
    }

    private static TextBlock DailyText(string text, double size, SolidColorBrush brush, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
}
