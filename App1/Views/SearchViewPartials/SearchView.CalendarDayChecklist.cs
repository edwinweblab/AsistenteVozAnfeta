using Anfeta.UI.Models.Notion;
using Anfeta.UI.Services.Notion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private FrameworkElement BuildCalendarActivityDayChecklist(NotionCalendarActivity activity, Action<NotionChecklistStats>? onUpdated = null)
        {
            var day = _calendarSelectedDate.Date;
            var dayLabel = day == DateTime.Today ? "hoy" : day.ToString("dd/MM/yyyy");
            var completedContent = new StackPanel { Spacing = 5 };
            var pendingContent = new StackPanel { Spacing = 5 };
            var commentsContent = new StackPanel { Spacing = 4 };

            // Entrada para nuevo checklist / comentario:
            var inputCard = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(35, 30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 148, 163, 184)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var inputLayout = new StackPanel { Spacing = 6 };

            var inputTextBox = new TextBox
            {
                PlaceholderText = "Nuevo pendiente o comentario para esta actividad...",
                FontSize = 12,
                Height = 32,
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(6)
            };

            var controlsRow = new Grid();
            controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chkHighlight = new CheckBox
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE7BA", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11)) },
                        new TextBlock { Text = "Resaltar", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 253, 230, 138)) }
                    }
                },
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 0)
            };
            ToolTipService.SetToolTip(chkHighlight, "Destaca este check con color ámbar, negrita y badge [⚡ REVISAR] en Notion y Anfeta");

            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnAddCheck = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE710", FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) },
                        new TextBlock { Text = "Agregar Check", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) }
                    }
                },
                Background = new SolidColorBrush(Color.FromArgb(255, 2, 132, 199)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 4, 10, 4)
            };

            var btnAddComment = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE8BD", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(255, 167, 139, 250)) },
                        new TextBlock { Text = "Comentario", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(255, 226, 232, 240)) }
                    }
                },
                Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 148, 163, 184)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4)
            };

            buttonsPanel.Children.Add(btnAddCheck);
            buttonsPanel.Children.Add(btnAddComment);

            controlsRow.Children.Add(chkHighlight);
            Grid.SetColumn(chkHighlight, 0);
            controlsRow.Children.Add(buttonsPanel);
            Grid.SetColumn(buttonsPanel, 1);

            inputLayout.Children.Add(inputTextBox);
            inputLayout.Children.Add(controlsRow);

            var status = new TextBlock
            {
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 2, 0),
                Visibility = Visibility.Collapsed
            };
            inputLayout.Children.Add(status);
            inputCard.Child = inputLayout;

            var completed = new Expander
            {
                Header = $"Completados {dayLabel}", IsExpanded = true,
                Content = completedContent, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var pending = new Expander
            {
                Header = "Pendientes", IsExpanded = true,
                Content = pendingContent, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var comments = new Expander
            {
                Header = "Comentarios", IsExpanded = false,
                Content = commentsContent, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var refresh = new Button { Content = "Actualizar", Padding = new Thickness(8, 3, 8, 3) };
            var root = new StackPanel { Spacing = 6 };

            root.Children.Add(inputCard);
            root.Children.Add(pending);
            root.Children.Add(completed);
            root.Children.Add(comments);
            root.Children.Add(refresh);

            TextBlock Message(string text) => new TextBlock
            {
                Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8
            };

            Border Row(string text, bool isCompleted, DateTimeOffset createdAt, DateTimeOffset completedAt)
            {
                var content = new StackPanel { Spacing = 4 };

                var isHighlighted = text.StartsWith("⚡") ||
                                    text.Contains("[URGENTE", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("[IMPORTANTE", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("[REVISAR", StringComparison.OrdinalIgnoreCase);

                var cleanText = text;
                if (cleanText.StartsWith("⚡"))
                    cleanText = cleanText.Substring(1).TrimStart();

                if (isHighlighted)
                {
                    var highlightBadge = new Border
                    {
                        Padding = new Thickness(6, 1.5, 6, 1.5),
                        Margin = new Thickness(0, 0, 0, 2),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.FromArgb(50, 245, 158, 11)), // Amber 500
                        BorderBrush = new SolidColorBrush(Color.FromArgb(180, 245, 158, 11)),
                        BorderThickness = new Thickness(1),
                        Child = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 4,
                            Children =
                            {
                                new FontIcon { Glyph = "\uE7BA", FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromArgb(255, 253, 230, 138)) },
                                new TextBlock
                                {
                                    Text = "⚡ REVISAR / DESTACADO",
                                    FontSize = 9.5,
                                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                                    Foreground = new SolidColorBrush(Color.FromArgb(255, 253, 230, 138))
                                }
                            }
                        }
                    };
                    content.Children.Add(highlightBadge);
                }

                content.Children.Add(new TextBlock
                {
                    Text = (isCompleted ? "✓ " : "○ ") + cleanText,
                    FontSize = isHighlighted ? 13 : 12.5,
                    FontWeight = isHighlighted ? Microsoft.UI.Text.FontWeights.Bold : (isCompleted ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(isCompleted
                        ? Color.FromArgb(255, 240, 253, 244)
                        : (isHighlighted ? Color.FromArgb(255, 254, 240, 138) : Color.FromArgb(255, 241, 245, 249)))
                });

                var metaPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(0, 1, 0, 0)
                };

                // Días transcurridos:
                // Si se agrega hoy (ej. hoy 08/10) dice "0 DÍAS". Si pasan más días: "1 DÍA", "2 DÍAS", etc.
                var refDate = createdAt != DateTimeOffset.MinValue
                    ? createdAt.ToLocalTime().Date
                    : (completedAt != DateTimeOffset.MinValue ? completedAt.ToLocalTime().Date : DateTime.Today);

                var elapsedDays = Math.Max(0, (DateTime.Today - refDate).Days);
                var daysBadgeText = elapsedDays == 1 ? "1 DÍA" : $"{elapsedDays} DÍAS";

                var daysBadge = new Border
                {
                    Padding = new Thickness(5, 1, 5, 1),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(isCompleted
                        ? Color.FromArgb(50, 16, 185, 129)
                        : (elapsedDays > 0
                            ? Color.FromArgb(50, 245, 158, 11)
                            : Color.FromArgb(50, 56, 189, 248))),
                    BorderBrush = new SolidColorBrush(isCompleted
                        ? Color.FromArgb(120, 16, 185, 129)
                        : (elapsedDays > 0
                            ? Color.FromArgb(120, 245, 158, 11)
                            : Color.FromArgb(120, 56, 189, 248))),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = daysBadgeText,
                        FontSize = 9.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(isCompleted
                            ? Color.FromArgb(255, 52, 211, 153)
                            : (elapsedDays > 0
                                ? Color.FromArgb(255, 251, 191, 36)
                                : Color.FromArgb(255, 125, 211, 252)))
                    }
                };
                metaPanel.Children.Add(daysBadge);

                // Horas de agregado y completado
                static string FormatDt(DateTimeOffset dt)
                {
                    var local = dt.ToLocalTime();
                    var isToday = local.Date == DateTime.Today;
                    var isYesterday = local.Date == DateTime.Today.AddDays(-1);
                    var dateLabel = isToday
                        ? $"Hoy {local:dd/MM}"
                        : (isYesterday ? $"Ayer {local:dd/MM}" : local.ToString("dd/MM/yyyy"));
                    return $"{dateLabel} {local:HH:mm}";
                }

                var timeDetails = new List<string>();
                if (createdAt != DateTimeOffset.MinValue)
                {
                    timeDetails.Add($"Agregado: {FormatDt(createdAt)}");
                }
                if (isCompleted && completedAt != DateTimeOffset.MinValue)
                {
                    timeDetails.Add($"Completado: {FormatDt(completedAt)}");
                }

                if (timeDetails.Count > 0)
                {
                    metaPanel.Children.Add(new TextBlock
                    {
                        Text = string.Join("  ·  ", timeDetails),
                        FontSize = 10.5,
                        Opacity = 0.85,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184))
                    });
                }

                content.Children.Add(metaPanel);

                return new Border
                {
                    Child = content,
                    Padding = new Thickness(8, 6, 8, 6),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(isHighlighted ? 1.5 : 0),
                    BorderBrush = isHighlighted
                        ? new SolidColorBrush(Color.FromArgb(180, 245, 158, 11)) // Glowing amber border
                        : null,
                    Background = new SolidColorBrush(isCompleted
                        ? Color.FromArgb(32, 16, 185, 129)
                        : (isHighlighted
                            ? Color.FromArgb(42, 245, 158, 11) // Distinct amber highlight background
                            : Color.FromArgb(22, 148, 163, 184)))
                };
            }

            void Render(NotionChecklistStats stats)
            {
                completedContent.Children.Clear();
                pendingContent.Children.Clear();
                commentsContent.Children.Clear();

                // Use recorded completion dates, never last-edited timestamps:
                // editing an old completed task must not count as work today.
                var done = stats.GetCompletedItemsOn(day)
                    .GroupBy(item => item.BlockId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(item => item.CompletedAt).ToList();
                completed.Header = $"Completados {dayLabel} ({done.Count})";
                foreach (var item in done)
                    completedContent.Children.Add(Row(item.Text, true, item.CreatedAt, item.CompletedAt));
                if (done.Count == 0)
                    completedContent.Children.Add(Message(stats.CompletedItems == null && stats.GetCompletedOn(day) > 0
                        ? "Cargando el detalle de los completados del día…"
                        : $"Sin checklists completados {dayLabel}."));

                // Pendientes: resaltados en la parte superior
                var todo = stats.PendingItems?.GroupBy(b => b.BlockId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderByDescending(b => b.Text.StartsWith("⚡") ||
                                           b.Text.Contains("[IMPORTANTE", StringComparison.OrdinalIgnoreCase) ||
                                           b.Text.Contains("[REVISAR", StringComparison.OrdinalIgnoreCase) ||
                                           b.Text.Contains("[URGENTE", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                pending.Header = $"Pendientes ({todo?.Count ?? stats.Pending})";
                if (todo == null)
                    pendingContent.Children.Add(Message("Cargando pendientes…"));
                else if (todo.Count == 0)
                    pendingContent.Children.Add(Message("Sin checklists pendientes."));
                else
                    foreach (var item in todo)
                        pendingContent.Children.Add(Row(item.Text, false, item.CreatedAt, DateTimeOffset.MinValue));

                // Comentarios:
                comments.Header = $"Comentarios ({stats.CommentCount})";
                if (stats.CommentCount == 0 && string.IsNullOrWhiteSpace(stats.LatestCommentText))
                {
                    commentsContent.Children.Add(Message("Sin comentarios en esta actividad."));
                }
                else if (!string.IsNullOrWhiteSpace(stats.LatestCommentText))
                {
                    commentsContent.Children.Add(new TextBlock
                    {
                        Text = stats.LatestCommentText,
                        FontSize = 11.5,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 203, 213, 225)),
                        Padding = new Thickness(4)
                    });
                }
            }

            Render(GetCalendarChecklistStats(activity));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            CancellationTokenSource? lifetime = new CancellationTokenSource();
            var busy = false;

            async Task LoadAsync()
            {
                if (busy) return;
                lifetime ??= new CancellationTokenSource();
                var activeLifetime = lifetime;
                if (activeLifetime.IsCancellationRequested) return;

                var token = GetSavedNotionToken();
                if (string.IsNullOrWhiteSpace(token))
                {
                    status.Visibility = Visibility.Visible;
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 251, 191, 36));
                    status.Text = "Configura el token de Notion en Configuración.";
                    return;
                }
                busy = true;
                refresh.IsEnabled = false;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(activeLifetime.Token);
                request.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    var stats = await _notionCalendarService.GetChecklistStatsAsync(
                        token, activity.PageId, request.Token, forceRefresh: true);
                    request.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(lifetime, activeLifetime)) return;
                    _oneClickChecklistStats[activity.PageId] = stats;
                    activity.ChecklistScanned = true;
                    activity.ChecklistTotal = stats.Total;
                    activity.ChecklistCompleted = stats.Completed;
                    activity.CommentCount = stats.CommentCount;
                    activity.LatestCommentText = stats.LatestCommentText;
                    Render(stats);
                    onUpdated?.Invoke(stats);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (!activeLifetime.IsCancellationRequested)
                    {
                        status.Visibility = Visibility.Visible;
                        status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                        status.Text = $"Aviso: No se pudo actualizar Notion ({ex.Message}). Se conserva la última información.";
                    }
                }
                finally
                {
                    busy = false;
                    if (ReferenceEquals(lifetime, activeLifetime)) refresh.IsEnabled = true;
                }
            }

            async Task SubmitNewCheckAsync()
            {
                if (!btnAddCheck.IsEnabled) return;
                var text = (inputTextBox.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    status.Visibility = Visibility.Visible;
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 251, 191, 36));
                    status.Text = "⚠️ Escribe primero el texto del check o pendiente.";
                    inputTextBox.Focus(FocusState.Programmatic);
                    return;
                }

                if (string.IsNullOrWhiteSpace(activity?.PageId))
                {
                    status.Visibility = Visibility.Visible;
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                    status.Text = "⚠️ Esta actividad no tiene un ID de Notion asociado.";
                    return;
                }

                var token = GetSavedNotionToken();
                if (string.IsNullOrWhiteSpace(token))
                {
                    status.Visibility = Visibility.Visible;
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                    status.Text = "❌ Configura el token de Notion en Configuración para agregar pendientes.";
                    return;
                }

                var isHigh = chkHighlight.IsChecked == true;

                // Optimistic UI update: mostrar inmediatamente en la lista
                var displayText = isHigh && !text.StartsWith("⚡") ? $"⚡ {text}" : text;
                var optimisticRow = Row(displayText, false, DateTimeOffset.Now, DateTimeOffset.MinValue);
                if (pendingContent.Children.Count == 1 && pendingContent.Children[0] is TextBlock)
                {
                    pendingContent.Children.Clear();
                }
                pendingContent.Children.Insert(0, optimisticRow);
                pending.IsExpanded = true;

                status.Visibility = Visibility.Visible;
                status.Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
                status.Text = isHigh ? "Guardando check resaltado en Notion... ⏳" : "Guardando check en Notion... ⏳";
                inputTextBox.Text = "";

                btnAddCheck.IsEnabled = false;
                btnAddComment.IsEnabled = false;
                inputTextBox.IsEnabled = false;
                chkHighlight.IsEnabled = false;
                var confirmed = false;

                try
                {
                    var ok = await _notionCalendarService.AddChecklistItemAsync(token, activity.PageId, text, isHighlighted: isHigh);
                    confirmed = ok;
                    if (ok)
                    {
                        status.Foreground = new SolidColorBrush(Color.FromArgb(255, 52, 211, 153));
                        status.Text = isHigh ? "✅ Check resaltado guardado en Notion ⚡" : "✅ Check guardado en Notion";
                        await LoadAsync();
                    }
                    else
                    {
                        pendingContent.Children.Remove(optimisticRow);
                        inputTextBox.Text = text;
                        status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                        status.Text = "❌ No se pudo guardar el check en Notion.";
                    }
                }
                catch (Exception ex)
                {
                    if (!confirmed) { pendingContent.Children.Remove(optimisticRow); inputTextBox.Text = text; }
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                    status.Text = confirmed ? "✅ Check guardado; no se pudo actualizar la vista. Usa refrescar, no vuelvas a enviarlo." : $"❌ No se confirmó el check. Revisa Notion antes de repetir: {ex.Message}";
                }
                finally
                {
                    btnAddCheck.IsEnabled = true;
                    btnAddComment.IsEnabled = true;
                    inputTextBox.IsEnabled = true;
                    chkHighlight.IsEnabled = true;
                }
            }

            async Task SubmitNewCommentAsync()
            {
                if (!btnAddComment.IsEnabled) return;
                var text = (inputTextBox.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    status.Visibility = Visibility.Visible;
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 251, 191, 36));
                    status.Text = "⚠️ Escribe primero el comentario que deseas enviar.";
                    inputTextBox.Focus(FocusState.Programmatic);
                    return;
                }

                if (string.IsNullOrWhiteSpace(activity?.PageId))
                {
                    status.Visibility = Visibility.Visible;
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                    status.Text = "⚠️ Esta actividad no tiene un ID de Notion asociado.";
                    return;
                }

                var token = GetSavedNotionToken();
                if (string.IsNullOrWhiteSpace(token))
                {
                    status.Visibility = Visibility.Visible;
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                    status.Text = "❌ Configura el token de Notion en Configuración para enviar comentarios.";
                    return;
                }

                // Optimistic UI update: mostrar inmediatamente en la lista de comentarios
                comments.IsExpanded = true;
                if (commentsContent.Children.Count == 1 && commentsContent.Children[0] is TextBlock tb && tb.Text.StartsWith("Sin comentarios"))
                {
                    commentsContent.Children.Clear();
                }
                var optimisticComment = new TextBlock
                {
                    Text = $"💬 {(chkHighlight.IsChecked == true && !text.StartsWith("⚡") ? "⚡ " : "")}{text} (guardando...)",
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(chkHighlight.IsChecked == true ? Color.FromArgb(255,254,240,138) : Color.FromArgb(255,203,213,225)),
                    FontWeight = chkHighlight.IsChecked == true ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    Padding = new Thickness(4)
                };
                commentsContent.Children.Insert(0, optimisticComment);

                status.Visibility = Visibility.Visible;
                status.Foreground = new SolidColorBrush(Color.FromArgb(255, 167, 139, 250));
                status.Text = "Enviando comentario a Notion... ⏳";
                inputTextBox.Text = "";

                btnAddCheck.IsEnabled = false;
                btnAddComment.IsEnabled = false;
                inputTextBox.IsEnabled = false;
                chkHighlight.IsEnabled = false;
                var confirmed = false;

                try
                {
                    var ok = await _notionCalendarService.AddCommentAsync(token, activity.PageId, text, isHighlighted: chkHighlight.IsChecked == true);
                    confirmed = ok;
                    if (ok)
                    {
                        status.Foreground = new SolidColorBrush(Color.FromArgb(255, 52, 211, 153));
                        status.Text = "✅ Comentario guardado en Notion 💬";
                        await LoadAsync();
                    }
                    else
                    {
                        commentsContent.Children.Remove(optimisticComment);
                        inputTextBox.Text = text;
                        status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                        status.Text = "❌ No se pudo enviar el comentario a Notion.";
                    }
                }
                catch (Exception ex)
                {
                    if (!confirmed) { commentsContent.Children.Remove(optimisticComment); inputTextBox.Text = text; }
                    status.Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
                    status.Text = confirmed ? "✅ Comentario guardado; no se pudo actualizar la vista. Usa refrescar, no vuelvas a enviarlo." : $"❌ No se confirmó el comentario. Revisa Notion antes de repetir: {ex.Message}";
                }
                finally
                {
                    btnAddCheck.IsEnabled = true;
                    btnAddComment.IsEnabled = true;
                    inputTextBox.IsEnabled = true;
                    chkHighlight.IsEnabled = true;
                }
            }

            btnAddCheck.Click += async (_, _) => await SubmitNewCheckAsync();
            btnAddComment.Click += async (_, _) => await SubmitNewCommentAsync();
            inputTextBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    await SubmitNewCheckAsync();
                }
            };

            root.Loaded += async (_, __) =>
            {
                lifetime?.Cancel();
                lifetime?.Dispose();
                lifetime = new CancellationTokenSource();
                timer.Start();
                await LoadAsync();
            };
            root.Unloaded += (_, __) =>
            {
                timer.Stop();
                lifetime?.Cancel();
                lifetime?.Dispose();
                lifetime = null;
            };
            timer.Tick += async (_, __) => await LoadAsync();
            refresh.Click += async (_, __) => await LoadAsync();

            _ = LoadAsync();

            return root;
        }
    }
}
