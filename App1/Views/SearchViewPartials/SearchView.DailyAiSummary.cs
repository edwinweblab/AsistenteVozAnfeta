using Anfeta.UI.Models.DailyAi;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Reports;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Groq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.System;
using System.IO;

namespace Anfeta.UI.Views;

public sealed partial class SearchView
{
    private bool _dailySummaryOpen;
    private bool _floatingAiOpen;
    private bool _floatingAiAnimationStarted;
    private ContentDialog? _activeAiDialog;
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
        var cancellation = new CancellationTokenSource();
        var isDetaching = false;
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
            LocalAiService.ResetContext();
            var local = new LocalAiService();
            var status = await local.GetStatusAsync(cancellation.Token);
            if (status.ModelInstalled)
            {
                _ = LocalAiService.WarmupAsync(cancellation.Token);
            }
            var groqKey = await App.AppHost.Services.GetRequiredService<ApiKeyService>().GetActiveGroqKeyAsync();
            var cloudReady = !string.IsNullOrWhiteSpace(groqKey);
            var info = new InfoBar
            {
                IsOpen = true, IsClosable = false,
                Title = cloudReady ? "IA rápida lista" : status.ModelInstalled ? "IA local lista" : "Configuración requerida",
                Message = cloudReady ? $"Groq · {GroqDailyAiService.Model}" : status.Message,
                Severity = cloudReady || status.ModelInstalled ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 4, 10, 4)
            };
            var question = new TextBox
            {
                PlaceholderText = "Ejemplo: ¿Qué actividades tiene Karla hoy y cuáles requieren atención?",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 48,
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 12
            };
            var suggestions = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            for (var i = 0; i < 2; i++) suggestions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            suggestions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            suggestions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var quickQuestions = new[]
            {
                ("🔥 Prioridades reales", "Identifica las 10 actividades prioritarias de hoy en viñetas limpias numeradas (1., 2., 3.). Para cada una indica responsable, horario y motivo. No uses tablas."),
                ("👥 Agenda por persona", "Resume la agenda de hoy por responsable en viñetas limpias de hasta 10 actividades. Incluye cantidades y ejemplos con horario. No uses tablas."),
                ("⏰ Próximas actividades", "Lista las próximas 10 actividades de hoy en orden cronológico en viñetas limpias. No uses tablas."),
                ("⚠ Sin responsable", "Lista las actividades sin responsable y su proyecto, horario y estado en viñetas limpias. No uses tablas.")
            };
            var actions = new Grid { ColumnSpacing = 8 };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (!status.ModelInstalled)
            {
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var provider = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 120,
                FontSize = 12
            };
            if (cloudReady) provider.Items.Add("⚡ Rápido · Groq Cloud");
            foreach (var m in LocalAiService.InstalledModels)
            {
                var label = m.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase)
                    ? $"🧠 Privado · {m} (Detallado)"
                    : m.StartsWith("qwen2.5:0.5b", StringComparison.OrdinalIgnoreCase)
                    ? $"⚡ Privado · {m} (Ultra rápido)"
                    : $"🔒 Privado · {m}";
                provider.Items.Add(label);
            }
            if (provider.Items.Count == (cloudReady ? 1 : 0))
            {
                provider.Items.Add($"🔒 Privado · Ollama ({LocalAiService.Model})");
            }
            provider.SelectedIndex = provider.Items.Count > 1 ? 1 : 0; // Prioridad al primer modelo Ollama local (detallado)
            var configure = new Button { Content = "Configurar", Visibility = status.ModelInstalled ? Visibility.Collapsed : Visibility.Visible, FontSize = 11, Padding = new Thickness(8, 6, 8, 6) };
            var send = new Button
            {
                Content = "✦ Consultar",
                IsEnabled = cloudReady || status.ModelInstalled,
                Padding = new Thickness(14, 6, 14, 6),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Background = DailyBrush(20, 132, 190),
                Foreground = DailyBrush(255, 255, 255)
            };

            Grid.SetColumn(provider, 0);
            actions.Children.Add(provider);

            if (!status.ModelInstalled)
            {
                Grid.SetColumn(configure, 1);
                actions.Children.Add(configure);
                Grid.SetColumn(send, 2);
                actions.Children.Add(send);
            }
            else
            {
                Grid.SetColumn(send, 1);
                actions.Children.Add(send);
            }

            var memoryStatus = DailyText("Conversación nueva · contexto de la vista incluido", 10, DailyMutedBrush);
            var output = new StackPanel { Spacing = 10 };
            output.Children.Add(DailyText("Aquí aparecerá una respuesta sustentada en la agenda real cargada por ANFETA.", 12, DailyMutedBrush));

            var answerCard = new Border { Background = DailyPanelBrush, BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = output };
            var answerScroll = new ScrollViewer
            {
                Content = answerCard,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

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

            async Task RunAiQueryAsync(string queryText)
            {
                if (string.IsNullOrWhiteSpace(queryText))
                {
                    queryText = "Identifica las 10 actividades prioritarias de hoy en viñetas limpias numeradas (1., 2., 3.). Para cada una indica responsable, horario y motivo. No uses tablas.";
                }
                question.Text = queryText;
                send.IsEnabled = false;
                try
                {
                    var userQuestion = queryText.Trim();
                    var memory = string.Join("\n", conversation.TakeLast(6));
                    var selectedText = provider.SelectedItem?.ToString() ?? string.Empty;
                    var useCloud = selectedText.Contains("Groq Cloud", StringComparison.OrdinalIgnoreCase) && cloudReady;
                    if (!useCloud)
                    {
                        foreach (var m in LocalAiService.InstalledModels)
                        {
                            if (selectedText.Contains(m, StringComparison.OrdinalIgnoreCase))
                            {
                                LocalAiService.Model = m;
                                break;
                            }
                        }
                    }
                    var responseProvider = useCloud ? $"Groq Cloud · {GroqDailyAiService.Model}" : $"Ollama local · {LocalAiService.Model}";
                    info.Severity = InfoBarSeverity.Informational;
                    info.Title = "Procesando consulta…";
                    info.Message = $"\"{userQuestion}\" · {responseProvider}";
                    ShowReasoningAnimation(output, responseProvider);
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    TextBlock? liveText = null;
                    TextBlock? liveTimerText = null;
                    var accumulatedLive = new System.Text.StringBuilder();
                    Action<string> onChunk = chunk =>
                    {
                        accumulatedLive.Append(chunk);
                        var livePreview = CleanLivePreview(accumulatedLive.ToString());
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (liveText == null)
                            {
                                output.Children.Clear();
                                var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                                var header = DailyText($"Generando respuesta en tiempo real · {responseProvider}", 11, DailyBrush(42, 207, 142), true);
                                liveTimerText = new TextBlock
                                {
                                    Text = "🧠 Razonando…",
                                    FontSize = 10,
                                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                    Foreground = DailyBrush(130, 205, 255),
                                    VerticalAlignment = VerticalAlignment.Center,
                                    HorizontalTextAlignment = TextAlignment.Center
                                };
                                var timerBadge = new Border
                                {
                                    Background = DailyBrush(20, 42, 58),
                                    BorderBrush = DailyBrush(40, 80, 110),
                                    BorderThickness = new Thickness(1),
                                    CornerRadius = new CornerRadius(10),
                                    Padding = new Thickness(8, 2, 8, 2),
                                    MinWidth = 95,
                                    Child = liveTimerText
                                };
                                headerRow.Children.Add(header);
                                headerRow.Children.Add(timerBadge);

                                liveText = DailyText("", 13, DailyBrush(235, 243, 249));
                                liveText.TextWrapping = TextWrapping.Wrap;
                                var card = new Border
                                {
                                    Background = DailyBrush(14, 35, 49),
                                    BorderBrush = DailyBrush(35, 79, 105),
                                    BorderThickness = new Thickness(1),
                                    CornerRadius = new CornerRadius(8),
                                    Padding = new Thickness(14),
                                    Child = liveText
                                };
                                output.Children.Add(headerRow);
                                output.Children.Add(card);
                            }
                            liveText.Text = livePreview;
                            if (liveTimerText != null)
                            {
                                liveTimerText.Text = $"🧠 Razonando… {stopwatch.Elapsed.TotalSeconds:0.1}s";
                            }
                            answerScroll.ChangeView(null, answerScroll.ScrollableHeight, null, true);
                        });
                    };

                    DailyAiAssistantResult result;
                    if (useCloud)
                    {
                        try
                        {
                            result = await new GroqDailyAiService().AskAsync(groqKey!, snapshot, userQuestion, cancellation.Token, memory, viewContext);
                        }
                        catch (Exception cloudError) when (status.ModelInstalled)
                        {
                            info.Severity = InfoBarSeverity.Warning;
                            info.Title = "Groq no respondió, usando Ollama local";
                            info.Message = $"{FriendlyAiError(cloudError)} Se redirigió la consulta a la IA local.";
                            provider.SelectedIndex = 1;
                            ShowReasoningAnimation(output, $"Ollama local · {LocalAiService.Model}");
                            result = await local.AskAsync(snapshot, userQuestion, cancellation.Token, memory, viewContext, onChunk);
                            responseProvider = $"Ollama local · {LocalAiService.Model} (respaldo)";
                        }
                    }
                    else
                    {
                        result = await local.AskAsync(snapshot, userQuestion, cancellation.Token, memory, viewContext, onChunk);
                    }
                    stopwatch.Stop();
                    var elapsedSec = stopwatch.Elapsed.TotalSeconds;

                    var lowerUserQ = userQuestion.ToLowerInvariant();
                    var personTarget = snapshot.People.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) && (
                        lowerUserQ.Contains(p.Name.ToLowerInvariant()) ||
                        p.Name.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries).Any(part => part.Length >= 3 && lowerUserQ.Contains(part.ToLowerInvariant()))
                    ));

                    RenderAssistantResult(output, result, false, responseProvider, elapsedSec, personTarget?.Name, snapshot.Activities, TriggerAnfetaSearch);
                    answerScroll.ChangeView(null, 0, null, true);

                    // Reemplazar la barra de estado con la consulta completa
                    info.Severity = InfoBarSeverity.Success;
                    info.Title = $"Consulta completada ({elapsedSec:0.1}s)";
                    info.Message = $"\"{userQuestion}\" · {responseProvider}";

                    conversation.Add($"Usuario: {userQuestion}");
                    conversation.Add($"ANFETA: {CleanAiText(result.Answer)}");
                    while (conversation.Count > 8) conversation.RemoveAt(0);
                    memoryStatus.Text = $"Memoria activa · {conversation.Count / 2} intercambio(s) · contexto de {CurrentTabMode}";
                    // Limpiar el input de preguntas al responder
                    question.Text = string.Empty;
                }
                catch (Exception ex)
                {
                    info.Severity = InfoBarSeverity.Error;
                    info.Title = "Error en la consulta";
                    info.Message = FriendlyAiError(ex);
                    SetAiMessage(output, FriendlyAiError(ex), true);
                }
                finally { send.IsEnabled = cloudReady || status.ModelInstalled; }
            }

            for (var quickIndex = 0; quickIndex < quickQuestions.Length; quickIndex++)
            {
                var item = quickQuestions[quickIndex];
                var chip = new Button
                {
                    Content = new TextBlock
                    {
                        Text = item.Item1,
                        FontSize = 11.5,
                        TextWrapping = TextWrapping.NoWrap
                    },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Padding = new Thickness(8, 6, 8, 6),
                    Background = DailyBrush(20, 42, 58),
                    BorderBrush = DailyBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6)
                };
                chip.Click += async (_, _) => await RunAiQueryAsync(item.Item2);
                Grid.SetColumn(chip, quickIndex % 2);
                Grid.SetRow(chip, quickIndex / 2);
                suggestions.Children.Add(chip);
            }

            send.Click += async (_, _) => await RunAiQueryAsync(question.Text);
            question.KeyDown += async (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                {
                    e.Handled = true;
                    await RunAiQueryAsync(question.Text);
                }
            };

            ContentDialog? dialog = null;

            var hero = new StackPanel { Spacing = 3 };
            hero.Children.Add(DailyText("COPILOTO OPERATIVO · NUBE O LOCAL", 10, DailyBrush(64, 196, 255), true));
            hero.Children.Add(DailyText("Pregunta por tu operación de hoy", 19, DailyBrush(241, 247, 252), true));
            hero.Children.Add(DailyText($"Contexto actualizado: {snapshot.Date:dddd, dd 'de' MMMM} · {snapshot.Metrics.TotalActivities} actividades · {snapshot.Metrics.TotalProjects} proyectos", 11, DailyMutedBrush));

            var availableWidth = XamlRoot?.Size.Width ?? 1280;
            var availableHeight = XamlRoot?.Size.Height ?? 720;
            // Ocupar por defecto la ventana casi completa de forma limpia y responsiva
            var responsiveWidth = Math.Max(840, availableWidth - 28);
            var responsiveHeight = Math.Max(520, availableHeight - 36);
            var content = new Grid { Width = responsiveWidth, Height = responsiveHeight, RowSpacing = 8 };

            Button CreateSizeBtn(string label, string tooltip, Action onClick)
            {
                var btn = new Button
                {
                    Content = label,
                    Padding = new Thickness(9, 4, 9, 4),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Background = DailyBrush(20, 42, 58),
                    Foreground = DailyBrush(210, 230, 245),
                    BorderBrush = DailyBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6)
                };
                ToolTipService.SetToolTip(btn, tooltip);
                btn.Click += (_, _) => onClick();
                return btn;
            }

            void ApplyDialogSize(double w, double h)
            {
                content.Width = w;
                content.Height = h;
                if (dialog != null)
                {
                    var rootW = XamlRoot?.Size.Width ?? (w + 40);
                    var rootH = XamlRoot?.Size.Height ?? (h + 40);
                    dialog.MinWidth = w;
                    dialog.MaxWidth = rootW;
                    dialog.MinHeight = h;
                    dialog.MaxHeight = rootH;
                    dialog.Resources["ContentDialogMinWidth"] = w;
                    dialog.Resources["ContentDialogMaxWidth"] = rootW;
                    dialog.Resources["ContentDialogMinHeight"] = h;
                    dialog.Resources["ContentDialogMaxHeight"] = rootH;
                }
            }

            var sizeToolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnMediano = CreateSizeBtn("Normal", "Tamaño estándar proporcional (80%)", () =>
            {
                var curW = XamlRoot?.Size.Width ?? 1280;
                var curH = XamlRoot?.Size.Height ?? 720;
                ApplyDialogSize(Math.Max(780, curW * 0.80), Math.Max(500, curH * 0.80));
            });

            var btnAmplio = CreateSizeBtn("Amplio", "Tamaño extendido (92% de la ventana)", () =>
            {
                var curW = XamlRoot?.Size.Width ?? 1280;
                var curH = XamlRoot?.Size.Height ?? 720;
                ApplyDialogSize(Math.Max(840, curW * 0.92), Math.Max(520, curH * 0.90));
            });

            var btnMax = CreateSizeBtn("🗖 Pantalla completa", "Abarcar toda la ventana de Anfeta", () =>
            {
                var curW = XamlRoot?.Size.Width ?? 1280;
                var curH = XamlRoot?.Size.Height ?? 720;
                ApplyDialogSize(Math.Max(860, curW - 24), Math.Max(520, curH - 32));
            });

            Button btnDetach = null!;
            btnDetach = CreateSizeBtn("↗ Desacoplar", "Abrir como ventana independiente que puedes mover a cualquier pantalla o monitor", () =>
            {
                if (dialog == null) return;
                isDetaching = true;
                _activeAiDialog = null;
                dialog.Hide();
                dialog.Content = null;
                OpenDetachedAiWindow(content, cancellation, btnDetach);
            });
            btnDetach.Background = DailyBrush(18, 60, 85);
            btnDetach.Foreground = DailyBrush(80, 220, 255);

            sizeToolbar.Children.Add(DailyText("Tamaño:", 11, DailyMutedBrush, true));
            sizeToolbar.Children.Add(btnMediano);
            sizeToolbar.Children.Add(btnAmplio);
            sizeToolbar.Children.Add(btnMax);
            sizeToolbar.Children.Add(btnDetach);

            var heroLayout = new Grid();
            heroLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heroLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(hero, 0);
            Grid.SetColumn(sizeToolbar, 1);
            heroLayout.Children.Add(hero);
            heroLayout.Children.Add(sizeToolbar);

            var heroCard = new Border { Background = DailyBrush(12, 31, 44), BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10), Child = heroLayout };

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
                value.Children.Add(DailyText(metricValues[i].Item1.ToString(), 17, metricValues[i].Item3, true));
                value.Children.Add(DailyText(metricValues[i].Item2, 10, DailyMutedBrush, true));
                var card = new Border { Background = DailyCardBrush, BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 6, 9, 6), Child = value };
                Grid.SetColumn(card, i); metrics.Children.Add(card);
            }

            var composer = new StackPanel { Spacing = 7 };
            composer.Children.Add(DailyText("¿Qué necesitas entender?", 13, DailyBrush(235, 243, 249), true));
            composer.Children.Add(question); composer.Children.Add(suggestions); composer.Children.Add(actions); composer.Children.Add(memoryStatus);
            var composerCard = new Border { Background = DailyCardBrush, BorderBrush = DailyBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = composer };

            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(heroCard, 0); Grid.SetRow(metrics, 1); Grid.SetRow(info, 2);

            var workArea = new Grid { ColumnSpacing = 12 };
            workArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            workArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
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
            dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "ANFETA AI", CloseButtonText = "Cerrar",
                Content = content,
                Background = DailyPanelBrush
            };
            ApplyDialogSize(responsiveWidth, responsiveHeight);

            void OnRootChanged(XamlRoot s, XamlRootChangedEventArgs e)
            {
                var curW = s.Size.Width;
                var curH = s.Size.Height;
                var newW = Math.Max(840, curW - 28);
                var newH = Math.Max(520, curH - 36);
                ApplyDialogSize(newW, newH);
            }

            if (XamlRoot != null)
            {
                XamlRoot.Changed += OnRootChanged;
            }

            dialog.Closing += (_, _) =>
            {
                if (XamlRoot != null)
                {
                    try { XamlRoot.Changed -= OnRootChanged; } catch { }
                }
                _activeAiDialog = null;
                if (!isDetaching) cancellation.Cancel();
            };
            dialog.Closed += (_, _) =>
            {
                if (XamlRoot != null)
                {
                    try { XamlRoot.Changed -= OnRootChanged; } catch { }
                }
                _activeAiDialog = null;
            };
            _activeAiDialog = dialog;
            StatusText.Text = "Estado: Listo";
            await dialog.ShowAsync();
        }
        catch (Exception ex) { StatusText.Text = "No se pudo abrir la IA local: " + ex.Message; }
        finally
        {
            _activeAiDialog = null;
            if (!isDetaching)
            {
                try { cancellation.Cancel(); } catch { }
                try { cancellation.Dispose(); } catch { }
                _floatingAiOpen = false;
                StatusText.Text = "Estado: Listo";
            }
        }
    }

    private void OpenDetachedAiWindow(FrameworkElement content, CancellationTokenSource cancellation, Button? detachBtn = null)
    {
        try
        {
            if (detachBtn != null)
            {
                detachBtn.Visibility = Visibility.Collapsed;
            }

            var win = new Window
            {
                Title = "ANFETA AI · Copiloto Operativo"
            };

            (Application.Current as App)?.TrackSecondaryWindow(win);

            content.Width = double.NaN;
            content.Height = double.NaN;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.VerticalAlignment = VerticalAlignment.Stretch;

            var rootGrid = new Grid
            {
                Background = DailyPanelBrush,
                Padding = new Thickness(16)
            };
            rootGrid.Children.Add(content);
            win.Content = rootGrid;

            win.Closed += (_, _) =>
            {
                try
                {
                    (Application.Current as App)?.UntrackSecondaryWindow(win);
                }
                catch { }

                try
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                }
                catch { }

                try
                {
                    win.Content = null;
                }
                catch { }

                _floatingAiOpen = false;
            };

            var hwnd = WindowNative.GetWindowHandle(win);
            if (hwnd != IntPtr.Zero)
            {
                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow?.Resize(new Windows.Graphics.SizeInt32(1360, 880));
            }

            win.Activate();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error al desacoplar ventana de IA: " + ex.Message;
            _floatingAiOpen = false;
        }
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
                var lowerQ = question.Text.ToLowerInvariant();
                var personTarget = snapshot.People.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) && (
                    lowerQ.Contains(p.Name.ToLowerInvariant()) ||
                    p.Name.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries).Any(part => part.Length >= 3 && lowerQ.Contains(part.ToLowerInvariant()))
                ));
                RenderAssistantResult(output, result, false, null, null, personTarget?.Name, snapshot.Activities, TriggerAnfetaSearch);
            }
            catch (Exception ex) { SetAiMessage(output, ex is InvalidOperationException ? ex.Message : "No se pudo completar la consulta.", true); }
            finally { ask.IsEnabled = true; }
        };
        panel.Children.Add(question); panel.Children.Add(quick); panel.Children.Add(localStatus); panel.Children.Add(setup); panel.Children.Add(ask); panel.Children.Add(output);
        return CreateDailyCard("ASISTENTE LOCAL DEL REPORTE", "Gratis · privado · sin clave API", DailyBrush(90, 165, 255), panel);
    }

    private void TriggerAnfetaSearch(string searchText)
    {
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Si la ventana de IA está en modo modal (no desacoplada), cerrarla para revelar los resultados
                try
                {
                    _activeAiDialog?.Hide();
                }
                catch { }

                _ = ExecuteSearchTextFromExternalAsync(searchText);

                // Asegurar que no se quede abierto el flyout de búsquedas rápidas
                try
                {
                    if (SearchBox != null)
                    {
                        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.GetAttachedFlyout(SearchBox)?.Hide();
                    }
                }
                catch { }

                App.MainWindowInstance?.Activate();
            });
        }
        catch { }
    }

    private static void RenderAssistantResult(StackPanel output, DailyAiAssistantResult value, bool cached, string? provider = null, double? elapsedSeconds = null, string? targetPerson = null, IReadOnlyList<DailyAiActivitySnapshot>? activities = null, Action<string>? onSearch = null)
    {
        output.Children.Clear();
        var source = cached ? "Respuesta recuperada del caché" : provider is null ? "Respuesta generada por el asistente configurado" : $"Respuesta generada con {provider}";

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        topRow.Children.Add(DailyText(source, 11, cached ? DailyBrush(42, 207, 142) : DailyMutedBrush, true));

        if (elapsedSeconds.HasValue && elapsedSeconds.Value > 0)
        {
            var sec = elapsedSeconds.Value;
            var text = sec < 1.0 ? $"🧠 Razonó por {sec:0.2}s" : $"🧠 Razonó por {sec:0.1}s";
            var badge = new Border
            {
                Background = DailyBrush(20, 42, 58),
                BorderBrush = DailyBrush(40, 80, 110),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = DailyBrush(130, 205, 255)
                }
            };
            topRow.Children.Add(badge);
        }

        output.Children.Add(topRow);

        RenderFormattedAnswer(output, value.Answer, targetPerson, activities, onSearch);

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

    private static string CleanAiText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = text.Trim();

        // Limpiar delimitadores markdown ```json o ```
        if (clean.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            var lines = clean.Split('\n');
            clean = string.Join('\n', lines.Where(l => !l.Trim().StartsWith("```"))).Trim();
        }

        // Si el modelo devolvió un array JSON como [ { "actividad": ... } ], convertirlo a viñetas limpias
        if (clean.StartsWith("[") && clean.EndsWith("]"))
        {
            try
            {
                using var doc = JsonDocument.Parse(clean);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    int idx = 1;
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var act = item.TryGetProperty("actividad", out var a) ? a.GetString() : null;
                            var resp = item.TryGetProperty("responsable", out var r) ? r.GetString() : null;
                            var hor = item.TryGetProperty("horario", out var h) ? h.GetString() : null;
                            var mot = item.TryGetProperty("motivo", out var m) ? m.GetString() : null;

                            if (!string.IsNullOrWhiteSpace(act))
                            {
                                sb.Append($"{idx}. {act}");
                                if (!string.IsNullOrWhiteSpace(resp)) sb.Append($" ({resp}");
                                if (!string.IsNullOrWhiteSpace(hor)) sb.Append($", {hor}");
                                if (!string.IsNullOrWhiteSpace(resp)) sb.Append(")");
                                if (!string.IsNullOrWhiteSpace(mot)) sb.Append($" — {mot}");
                                sb.AppendLine();
                                idx++;
                            }
                        }
                    }
                    if (sb.Length > 0) return sb.ToString().Trim();
                }
            }
            catch { }
        }

        // Si el texto contiene una tabla markdown con barras | y separadores ---, convertirla a viñetas limpias
        if (clean.Contains("|") && (clean.Contains("---") || clean.Contains("-|-")))
        {
            clean = FormatMarkdownTableToBullets(clean);
        }

        return clean.Replace("**", string.Empty).Replace("__", string.Empty).Replace("```", string.Empty).Trim();
    }

    private sealed class AiDisplayItem
    {
        public int Number { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Person { get; set; }
        public string? Time { get; set; }
        public string? Details { get; set; }
        public bool IsLagging { get; set; }
        public string? OriginalTitle { get; set; }
        public string? PageUrl { get; set; }
    }

    private static void RenderFormattedAnswer(StackPanel container, string rawAnswer, string? targetPerson = null, IReadOnlyList<DailyAiActivitySnapshot>? activities = null, Action<string>? onSearch = null)
    {
        var cleaned = CleanAiText(rawAnswer);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "No se detectaron actividades prioritarias o pendientes críticas para la consulta realizada.";
        }

        var (intro, items, outro) = ExtractStructuredAiItems(rawAnswer, cleaned, targetPerson, activities);

        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(DailyText("LECTURA RÁPIDA", 10, DailyBrush(86, 199, 255), true));

        if (!string.IsNullOrWhiteSpace(intro))
        {
            var introText = DailyText(intro, 12.5, DailyBrush(220, 231, 239));
            introText.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(introText);
        }

        if (items.Count > 0)
        {
            var itemsList = new StackPanel { Spacing = 6 };
            foreach (var item in items)
            {
                itemsList.Children.Add(CreateStructuredItemCard(item, onSearch));
            }
            section.Children.Add(itemsList);
        }
        else if (string.IsNullOrWhiteSpace(intro))
        {
            var bodyText = DailyText(cleaned, 13, DailyBrush(235, 243, 249));
            bodyText.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(bodyText);
        }

        if (!string.IsNullOrWhiteSpace(outro))
        {
            var outroCard = new Border
            {
                Background = DailyBrush(18, 42, 58),
                BorderBrush = DailyBrush(35, 80, 110),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 0),
                Child = DailyText(outro, 12, DailyBrush(190, 220, 240))
            };
            section.Children.Add(outroCard);
        }

        var wrapperCard = new Border
        {
            Background = DailyBrush(14, 35, 49),
            BorderBrush = DailyBrush(35, 79, 105),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = section
        };

        container.Children.Add(wrapperCard);
    }

    private static Border CreateStructuredItemCard(AiDisplayItem item, Action<string>? onSearch = null)
    {
        bool isUnassigned = !string.IsNullOrWhiteSpace(item.Person) &&
                             (item.Person.Contains("Sin responsable", StringComparison.OrdinalIgnoreCase) ||
                             item.Person.Contains("Sin asignar", StringComparison.OrdinalIgnoreCase) ||
                             item.Person.Equals("Nadie", StringComparison.OrdinalIgnoreCase));

        var accentColor = item.IsLagging
            ? DailyBrush(255, 107, 107)
            : isUnassigned
            ? DailyBrush(180, 115, 255)
            : DailyBrush(86, 199, 255);

        var cardBg = item.IsLagging
            ? DailyBrush(25, 28, 38)
            : isUnassigned
            ? DailyBrush(23, 23, 38)
            : DailyBrush(17, 36, 50);

        var cardBorder = item.IsLagging
            ? DailyBrush(120, 50, 50)
            : isUnassigned
            ? DailyBrush(95, 55, 145)
            : DailyBrush(32, 70, 95);

        var card = new Border
        {
            Background = cardBg,
            BorderBrush = cardBorder,
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 4)
        };

        var rootGrid = new Grid { ColumnSpacing = 10 };
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badgeBg = item.IsLagging
            ? DailyBrush(48, 22, 22)
            : isUnassigned
            ? DailyBrush(44, 24, 62)
            : DailyBrush(21, 48, 66);

        var numBadge = new Border
        {
            Width = 24,
            Height = 24,
            Background = badgeBg,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Child = new TextBlock
            {
                Text = item.Number.ToString(),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = accentColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(numBadge, 0);
        rootGrid.Children.Add(numBadge);

        var detailsStack = new StackPanel { Spacing = 4 };

        // Mostrar nombre real/original de Notion si se identificó, o el título procesado
        var displayTitle = !string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.OriginalTitle : item.Title;
        var titleText = new TextBlock
        {
            Text = displayTitle,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = DailyBrush(242, 248, 254),
            TextWrapping = TextWrapping.Wrap
        };
        detailsStack.Children.Add(titleText);

        var chipsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 2) };

        if (item.IsLagging)
        {
            chipsPanel.Children.Add(CreateMiniBadge("⚠️ Rezagada", DailyBrush(255, 107, 107), DailyBrush(55, 20, 20), DailyBrush(110, 35, 35)));
        }

        if (isUnassigned)
        {
            chipsPanel.Children.Add(CreateMiniBadge("👤 Sin responsable", DailyBrush(205, 155, 255), DailyBrush(45, 25, 70), DailyBrush(110, 60, 160)));
        }
        else if (!string.IsNullOrWhiteSpace(item.Person))
        {
            chipsPanel.Children.Add(CreateMiniBadge($"👤 {item.Person}", DailyBrush(142, 209, 252), DailyBrush(20, 42, 58), DailyBrush(35, 75, 102)));
        }

        if (!string.IsNullOrWhiteSpace(item.Time))
        {
            chipsPanel.Children.Add(CreateMiniBadge($"⏰ {item.Time}", DailyBrush(180, 220, 245), DailyBrush(20, 42, 58), DailyBrush(35, 75, 102)));
        }

        if (chipsPanel.Children.Count > 0)
        {
            detailsStack.Children.Add(chipsPanel);
        }

        // Limpiar detalles y si el título de la IA difería del original de Notion, conservarlo como contexto
        var cleanDetails = (item.Details ?? string.Empty).Trim();
        if (cleanDetails.StartsWith("📌")) cleanDetails = cleanDetails.Substring("📌".Length).Trim();
        cleanDetails = cleanDetails.TrimStart(' ', '-', '•').Trim();

        if (!string.IsNullOrWhiteSpace(item.OriginalTitle) &&
            !string.IsNullOrWhiteSpace(item.Title) &&
            !item.Title.Equals("Actividad", StringComparison.OrdinalIgnoreCase) &&
            !item.Title.Equals(item.OriginalTitle, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(cleanDetails))
        {
            cleanDetails = item.Title;
        }

        if (!string.IsNullOrWhiteSpace(cleanDetails))
        {
            var detailText = new TextBlock
            {
                Text = $"📌 {cleanDetails}",
                FontSize = 11.5,
                Foreground = DailyBrush(180, 202, 218),
                TextWrapping = TextWrapping.Wrap
            };
            detailsStack.Children.Add(detailText);
        }

        // Texto que se copiará y buscará: SIEMPRE el nombre original completo de la actividad de Notion
        var textToCopy = !string.IsNullOrWhiteSpace(item.OriginalTitle)
            ? item.OriginalTitle
            : (!string.IsNullOrWhiteSpace(item.Title) && !item.Title.Equals("Actividad", StringComparison.OrdinalIgnoreCase))
                ? item.Title
                : cleanDetails;

        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var copyBtn = new Button
        {
            Content = "📋 Copiar",
            Padding = new Thickness(7, 2, 7, 2),
            FontSize = 10,
            MinHeight = 0,
            MinWidth = 0,
            CornerRadius = new CornerRadius(4),
            Background = DailyBrush(25, 45, 60),
            Foreground = DailyBrush(190, 220, 245),
            BorderThickness = new Thickness(1),
            BorderBrush = DailyBrush(45, 80, 105)
        };
        ToolTipService.SetToolTip(copyBtn, $"Copiar nombre exacto: \"{textToCopy}\"");
        copyBtn.Click += (_, _) =>
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(textToCopy);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            copyBtn.Content = "✓ Copiado";
        };

        var searchBtn = new Button
        {
            Content = "🔍 Buscar en Anfeta",
            Padding = new Thickness(7, 2, 7, 2),
            FontSize = 10,
            MinHeight = 0,
            MinWidth = 0,
            CornerRadius = new CornerRadius(4),
            Background = DailyBrush(20, 50, 45),
            Foreground = DailyBrush(160, 240, 200),
            BorderThickness = new Thickness(1),
            BorderBrush = DailyBrush(35, 100, 80)
        };
        ToolTipService.SetToolTip(searchBtn, $"Buscar \"{textToCopy}\" en la ventana principal de Anfeta");
        searchBtn.Click += (_, _) =>
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(textToCopy);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            if (onSearch != null)
            {
                onSearch(textToCopy);
            }
            else
            {
                try
                {
                    if (App.MainWindowInstance?.Content is FrameworkElement root)
                    {
                        var box = root.FindName("SearchBox") as AutoSuggestBox;
                        if (box != null) box.Text = textToCopy;
                    }
                }
                catch { }
            }
            searchBtn.Content = "✓ En búsqueda";
        };

        actionButtons.Children.Add(copyBtn);
        actionButtons.Children.Add(searchBtn);

        if (!string.IsNullOrWhiteSpace(item.PageUrl))
        {
            var notionBtn = new Button
            {
                Content = "🌐 Notion",
                Padding = new Thickness(7, 2, 7, 2),
                FontSize = 10,
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(4),
                Background = DailyBrush(35, 30, 55),
                Foreground = DailyBrush(210, 180, 255),
                BorderThickness = new Thickness(1),
                BorderBrush = DailyBrush(75, 60, 110)
            };
            ToolTipService.SetToolTip(notionBtn, "Abrir actividad directamente en Notion");
            notionBtn.Click += async (_, _) =>
            {
                try { await Launcher.LaunchUriAsync(new Uri(item.PageUrl)); } catch { }
            };
            actionButtons.Children.Add(notionBtn);
        }

        detailsStack.Children.Add(actionButtons);

        Grid.SetColumn(detailsStack, 1);
        rootGrid.Children.Add(detailsStack);

        card.Child = rootGrid;
        return card;
    }

    private static Border CreateMiniBadge(string text, SolidColorBrush fg, SolidColorBrush bg, SolidColorBrush border)
    {
        return new Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = fg
            }
        };
    }

    private static (string? Intro, List<AiDisplayItem> Items, string? Outro) ExtractStructuredAiItems(string rawAnswer, string cleanedAnswer, string? targetPerson = null, IReadOnlyList<DailyAiActivitySnapshot>? activities = null)
    {
        var items = new List<AiDisplayItem>();
        string? intro = null;
        string? outro = null;

        // 1. Caso tabla Markdown en rawAnswer
        if (rawAnswer.Contains('|') && (rawAnswer.Contains("---") || rawAnswer.Contains("-|-")))
        {
            var lines = rawAnswer.Split('\n');
            var tableRows = new List<string[]>();
            var introLines = new List<string>();
            var outroLines = new List<string>();
            bool tableStarted = false;

            foreach (var r in lines)
            {
                var l = r.Trim();
                if (l.StartsWith('|') && l.Count(c => c == '|') >= 2)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(l, @"^\|(\s*:?-+:?\s*\|)+$") || l.Contains("---"))
                    {
                        continue;
                    }
                    var cells = l.Split('|')
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Select(c => c.Trim())
                        .ToArray();

                    if (cells.Length > 0)
                    {
                        tableStarted = true;
                        tableRows.Add(cells);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(l))
                {
                    if (!tableStarted) introLines.Add(l);
                    else outroLines.Add(l);
                }
            }

            if (tableRows.Count > 1)
            {
                int num = 1;
                for (int i = 1; i < tableRows.Count; i++)
                {
                    var row = tableRows[i];
                    if (row.Length == 0) continue;
                    var title = row[0];
                    var person = row.Length > 1 ? row[1] : null;
                    var time = row.Length > 2 ? row[2] : null;
                    var details = row.Length > 3 ? row[3] : null;

                    if (string.IsNullOrWhiteSpace(person) && !string.IsNullOrWhiteSpace(targetPerson))
                    {
                        person = targetPerson;
                    }

                    bool isLag = title.Contains("[REZAGADA]", StringComparison.OrdinalIgnoreCase) ||
                                 (details?.Contains("[REZAGADA]", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 title.Contains("REZAGADA", StringComparison.OrdinalIgnoreCase) ||
                                 (details?.Contains("REZAGADA", StringComparison.OrdinalIgnoreCase) ?? false);

                    title = CleanTag(title);
                    if (details != null) details = CleanTag(details);

                    if (title.Equals("Actividad", StringComparison.OrdinalIgnoreCase) ||
                        title.Equals("Tarea", StringComparison.OrdinalIgnoreCase) ||
                        title.Equals("Pendiente", StringComparison.OrdinalIgnoreCase) ||
                        title.Equals("Item", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(details))
                        {
                            title = details;
                            details = null;
                        }
                    }

                    var matchedAct = FindMatchingActivity(title, details, person, activities);
                    string? origTitle = matchedAct?.Title;
                    string? pageUrl = matchedAct?.PageUrl;
                    if (matchedAct != null)
                    {
                        if (matchedAct.IsLagging) isLag = true;
                        if (title.Equals("Actividad", StringComparison.OrdinalIgnoreCase) || title.Length < 8)
                        {
                            title = matchedAct.Title;
                        }
                    }

                    items.Add(new AiDisplayItem
                    {
                        Number = num++,
                        Title = title,
                        Person = string.IsNullOrWhiteSpace(person) ? null : person,
                        Time = string.IsNullOrWhiteSpace(time) ? null : time,
                        Details = string.IsNullOrWhiteSpace(details) ? null : details,
                        IsLagging = isLag,
                        OriginalTitle = origTitle,
                        PageUrl = pageUrl
                    });
                }

                if (introLines.Count > 0) intro = string.Join("\n", introLines).Trim();
                if (outroLines.Count > 0) outro = string.Join("\n", outroLines).Trim();
                return (intro, items, outro);
            }
        }

        // 2. Caso listas numeradas o con viñetas en cleanedAnswer
        var cleanLines = cleanedAnswer.Split('\n');
        var nonItemLinesBefore = new List<string>();
        var nonItemLinesAfter = new List<string>();
        int itemNumber = 1;

        foreach (var rawLine in cleanLines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var matchNum = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)[\.\)\-]\s*(.+)");
            var matchBullet = System.Text.RegularExpressions.Regex.Match(line, @"^[\-\*\•]\s*(.+)");

            if (matchNum.Success || matchBullet.Success)
            {
                var content = matchNum.Success ? matchNum.Groups[2].Value.Trim() : matchBullet.Groups[1].Value.Trim();
                var num = matchNum.Success && int.TryParse(matchNum.Groups[1].Value, out var n) ? n : itemNumber;

                bool isLag = content.Contains("[REZAGADA]", StringComparison.OrdinalIgnoreCase) ||
                             content.Contains("REZAGADA", StringComparison.OrdinalIgnoreCase);

                content = CleanTag(content);

                string title = content;
                string? person = null;
                string? time = null;
                string? details = null;

                // 1. Extraer si dice explícitamente "Responsable: X" o "Persona: X"
                var respMatch = System.Text.RegularExpressions.Regex.Match(title, @"(?:Responsable|Persona):\s*([^,;\n\)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (respMatch.Success)
                {
                    person = respMatch.Groups[1].Value.Trim();
                    title = title.Replace(respMatch.Value, string.Empty);
                }

                // 2. Extraer si dice "Horario: X" o "Hora: X"
                var timeMatch = System.Text.RegularExpressions.Regex.Match(title, @"(?:Horario|Hora):\s*([^,;\n\)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (timeMatch.Success)
                {
                    time = timeMatch.Groups[1].Value.Trim();
                    title = title.Replace(timeMatch.Value, string.Empty);
                }

                // 3. Extraer si dice "Motivo: X" o "Proyecto: X" o "Detalle: X"
                var motMatch = System.Text.RegularExpressions.Regex.Match(title, @"(?:Motivo|Proyecto|Detalle):\s*([^;\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (motMatch.Success)
                {
                    details = motMatch.Groups[1].Value.Trim();
                    title = title.Replace(motMatch.Value, string.Empty);
                }

                // 4. Si aún no se extrajo persona/hora, revisar si hay paréntesis: "(Isaias, 14:00)" o "(Isaias · 14:00)"
                if (string.IsNullOrWhiteSpace(person))
                {
                    var parenMatch = System.Text.RegularExpressions.Regex.Match(title, @"\(([^,\)·]+)(?:[,\s·]+([^,\)]+))?\)");
                    if (parenMatch.Success)
                    {
                        var pCandidate = parenMatch.Groups[1].Value.Trim();
                        if (!int.TryParse(pCandidate, out _))
                        {
                            person = pCandidate;
                            if (parenMatch.Groups[2].Success && string.IsNullOrWhiteSpace(time))
                                time = parenMatch.Groups[2].Value.Trim();
                            title = title.Replace(parenMatch.Value, string.Empty);
                        }
                    }
                }

                // 5. Si aún no hay detalle/motivo, separar por " — " o " - " o " : "
                if (string.IsNullOrWhiteSpace(details))
                {
                    var dashIdx = title.IndexOf(" — ", StringComparison.Ordinal);
                    if (dashIdx < 0) dashIdx = title.IndexOf(" - ", StringComparison.Ordinal);
                    if (dashIdx < 0) dashIdx = title.IndexOf(" : ", StringComparison.Ordinal);
                    if (dashIdx >= 0 && dashIdx < title.Length - 4)
                    {
                        details = title.Substring(dashIdx + 3).Trim();
                        title = title.Substring(0, dashIdx).Trim();
                    }
                }

                // Si aún no hay persona detectada y la consulta fue por una persona específica, asignarla
                if (string.IsNullOrWhiteSpace(person) && !string.IsNullOrWhiteSpace(targetPerson))
                {
                    person = targetPerson;
                }

                // Limpiar paréntesis o corchetes vacíos residuales () o []
                title = System.Text.RegularExpressions.Regex.Replace(title, @"\(\s*\)|\[\s*\]", "").Trim();
                // Limpiar puntuación residual al final del título y detalles
                title = System.Text.RegularExpressions.Regex.Replace(title, @"[\s,\-—:]+$", "").Trim();
                if (details != null)
                {
                    details = System.Text.RegularExpressions.Regex.Replace(details, @"\(\s*\)|\[\s*\]", "").Trim();
                    details = System.Text.RegularExpressions.Regex.Replace(details, @"[\s,\-—:]+$", "").Trim();
                }

                // Si el título quedó genérico (ej. "Actividad"), intercambiar con el detalle
                if (title.Equals("Actividad", StringComparison.OrdinalIgnoreCase) ||
                    title.Equals("Tarea", StringComparison.OrdinalIgnoreCase) ||
                    title.Equals("Pendiente", StringComparison.OrdinalIgnoreCase) ||
                    title.Equals("Item", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(details))
                    {
                        title = details;
                        details = null;
                    }
                }

                var matchedAct = FindMatchingActivity(title, details, person, activities);
                string? origTitle = matchedAct?.Title;
                string? pageUrl = matchedAct?.PageUrl;
                if (matchedAct != null)
                {
                    if (matchedAct.IsLagging) isLag = true;
                    if (title.Equals("Actividad", StringComparison.OrdinalIgnoreCase) || title.Length < 8)
                    {
                        title = matchedAct.Title;
                    }
                }

                items.Add(new AiDisplayItem
                {
                    Number = num,
                    Title = title,
                    Person = person,
                    Time = time,
                    Details = details,
                    IsLagging = isLag,
                    OriginalTitle = origTitle,
                    PageUrl = pageUrl
                });
                itemNumber++;
            }
            else
            {
                if (items.Count == 0)
                {
                    nonItemLinesBefore.Add(line);
                }
                else
                {
                    nonItemLinesAfter.Add(line);
                }
            }
        }

        // Descartar el último elemento si quedó truncado a medias por el límite de tokens (ej. "17. Ceaa" cortado)
        if (items.Count > 1)
        {
            var last = items[items.Count - 1];
            if (last.Title.Length < 7 && string.IsNullOrWhiteSpace(last.Details) && string.IsNullOrWhiteSpace(last.Time))
            {
                items.RemoveAt(items.Count - 1);
            }
        }

        if (nonItemLinesBefore.Count > 0) intro = string.Join("\n", nonItemLinesBefore).Trim();
        if (nonItemLinesAfter.Count > 0) outro = string.Join("\n", nonItemLinesAfter).Trim();

        return (intro, items, outro);
    }

    private static DailyAiActivitySnapshot? FindMatchingActivity(string title, string? details, string? person, IReadOnlyList<DailyAiActivitySnapshot>? activities)
    {
        if (activities == null || activities.Count == 0) return null;

        var combinedText = $"{title} {details}".ToLowerInvariant();
        var words = combinedText
            .Split(new[] { ' ', ',', '.', ':', '-', '—', '(', ')', '[', ']', '/', '\\', '"', '\'', '·' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !IsCommonStopWord(w))
            .Distinct()
            .ToList();

        DailyAiActivitySnapshot? bestMatch = null;
        int bestScore = 0;

        foreach (var act in activities)
        {
            int score = 0;
            var actTitleLower = (act.Title ?? string.Empty).ToLowerInvariant();
            var actProjLower = (act.ProjectName ?? string.Empty).ToLowerInvariant();
            var actPersonLower = (act.Person ?? string.Empty).ToLowerInvariant();

            // 1. Coincidencia de responsable (+6 puntos)
            if (!string.IsNullOrWhiteSpace(person) && !string.IsNullOrWhiteSpace(actPersonLower))
            {
                var pLower = person.ToLowerInvariant();
                if (actPersonLower.Contains(pLower) || pLower.Contains(actPersonLower))
                {
                    score += 6;
                }
                else
                {
                    var pParts = pLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (pParts.Any(part => part.Length >= 3 && (actPersonLower.Contains(part) || actTitleLower.Contains(part))))
                    {
                        score += 5;
                    }
                }
            }

            // 2. Coincidencia de palabras clave en el título de Notion (+4 c/u) o proyecto (+2 c/u)
            foreach (var word in words)
            {
                if (actTitleLower.Contains(word))
                {
                    score += 4;
                }
                else if (actProjLower.Contains(word))
                {
                    score += 2;
                }
            }

            // 3. Subcadena completa en el título de Notion (+10 puntos)
            if (!string.IsNullOrWhiteSpace(title) && title.Length >= 6 && actTitleLower.Contains(title.ToLowerInvariant()))
            {
                score += 10;
            }
            if (!string.IsNullOrWhiteSpace(details) && details.Length >= 6 && actTitleLower.Contains(details.ToLowerInvariant()))
            {
                score += 10;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = act;
            }
        }

        return bestScore >= 5 ? bestMatch : null;
    }

    private static bool IsCommonStopWord(string word)
    {
        return word switch
        {
            "actividad" or "tarea" or "pendiente" or "para" or "con" or "por" or "las" or "los"
            or "una" or "uno" or "del" or "que" or "hoy" or "hora" or "horario" or "responsable"
            or "motivo" or "proyecto" or "detalle" or "revisar" or "rezagada" => true,
            _ => false
        };
    }

    private static string CleanTag(string text)
    {
        return text.Replace("[REZAGADA]", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("REZAGADA", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("**", string.Empty)
                   .Replace("`", string.Empty)
                   .Trim();
    }

    private static string CleanLivePreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = text.Replace("```markdown", string.Empty).Replace("```json", string.Empty).Replace("```", string.Empty);

        if (clean.Contains('|'))
        {
            var lines = clean.Split('\n');
            var resultLines = new List<string>();
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.StartsWith('|') && (t.Contains("---") || t.Contains("-|-"))) continue;
                if (t.StartsWith('|') && t.EndsWith('|'))
                {
                    var cells = t.Split('|').Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
                    if (cells.Count > 0)
                    {
                        resultLines.Add("• " + string.Join(" · ", cells));
                        continue;
                    }
                }
                resultLines.Add(l);
            }
            clean = string.Join("\n", resultLines);
        }

        return clean.Trim();
    }

    private static string FormatMarkdownTableToBullets(string text)
    {
        try
        {
            var rawLines = text.Split('\n');
            var tableRows = new List<string[]>();
            var nonTableLines = new List<string>();

            foreach (var raw in rawLines)
            {
                var line = raw.Trim();
                if (line.StartsWith("|") && line.Count(c => c == '|') >= 2)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\|(\s*:?-+:?\s*\|)+$") || line.Contains("---"))
                    {
                        continue;
                    }

                    var cells = line.Split('|')
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Select(c => c.Trim())
                        .ToArray();

                    if (cells.Length > 0)
                    {
                        tableRows.Add(cells);
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("|"))
                    {
                        nonTableLines.Add(line);
                    }
                }
            }

            if (tableRows.Count > 1)
            {
                var rows = tableRows.Skip(1).ToList();
                var sb = new System.Text.StringBuilder();
                int idx = 1;
                foreach (var r in rows)
                {
                    if (r.Length == 0) continue;
                    var act = r[0];
                    var resp = r.Length > 1 ? r[1] : "";
                    var hora = r.Length > 2 ? r[2] : "";
                    var mot = r.Length > 3 ? r[3] : "";

                    var metaList = new List<string>();
                    if (!string.IsNullOrWhiteSpace(resp)) metaList.Add(resp);
                    if (!string.IsNullOrWhiteSpace(hora)) metaList.Add(hora);

                    sb.Append($"{idx}. {act}");
                    if (metaList.Count > 0) sb.Append($" ({string.Join(" · ", metaList)})");
                    if (!string.IsNullOrWhiteSpace(mot)) sb.Append($" — {mot}");
                    sb.AppendLine();
                    idx++;
                }

                if (nonTableLines.Count > 0)
                {
                    sb.AppendLine();
                    sb.Append(string.Join("\n", nonTableLines));
                }

                return sb.ToString().Trim();
            }
        }
        catch { }

        return text;
    }

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
