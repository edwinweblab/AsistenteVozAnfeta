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
            var completed = new Expander
            {
                Header = $"Completados {dayLabel}", IsExpanded = true,
                Content = completedContent, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var pending = new Expander
            {
                Header = "Pendientes", IsExpanded = false,
                Content = pendingContent, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var refresh = new Button { Content = "Actualizar", Padding = new Thickness(8, 3, 8, 3) };
            var status = new TextBlock { FontSize = 10, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
            var root = new StackPanel { Spacing = 6 };
            root.Children.Add(completed);
            root.Children.Add(pending);
            root.Children.Add(refresh);
            root.Children.Add(status);

            TextBlock Message(string text) => new TextBlock
            {
                Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8
            };

            Border Row(string text, bool isCompleted, string? time = null)
            {
                var content = new StackPanel { Spacing = 3 };
                content.Children.Add(new TextBlock
                {
                    Text = (isCompleted ? "✓ " : "○ ") + text,
                    FontSize = 12, TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrWhiteSpace(time))
                    content.Children.Add(Message($"Completado · {time}"));
                return new Border
                {
                    Child = content, Padding = new Thickness(8), CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(isCompleted
                        ? Color.FromArgb(35, 16, 185, 129) : Color.FromArgb(24, 148, 163, 184))
                };
            }

            void Render(NotionChecklistStats stats)
            {
                completedContent.Children.Clear();
                pendingContent.Children.Clear();
                // Use recorded completion dates, never last-edited timestamps:
                // editing an old completed task must not count as work today.
                var done = stats.GetCompletedItemsOn(day)
                    .GroupBy(item => item.BlockId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(item => item.CompletedAt).ToList();
                completed.Header = $"Completados {dayLabel} ({done.Count})";
                foreach (var item in done)
                    completedContent.Children.Add(Row(item.Text, true,
                        item.CompletedAt == DateTimeOffset.MinValue ? null :
                        item.CompletedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)));
                if (done.Count == 0)
                    completedContent.Children.Add(Message(stats.CompletedItems == null && stats.GetCompletedOn(day) > 0
                        ? "Cargando el detalle de los completados del día…"
                        : $"Sin checklists completados {dayLabel}."));

                var todo = stats.PendingItems?.GroupBy(b => b.BlockId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).ToList();
                pending.Header = $"Pendientes ({todo?.Count ?? stats.Pending})";
                if (todo == null)
                    pendingContent.Children.Add(Message("Cargando pendientes…"));
                else if (todo.Count == 0)
                    pendingContent.Children.Add(Message("Sin checklists pendientes."));
                else
                    foreach (var item in todo)
                        pendingContent.Children.Add(Row(item.Text, false));
            }

            Render(GetCalendarChecklistStats(activity));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            CancellationTokenSource? lifetime = null;
            var busy = false;

            async Task LoadAsync()
            {
                if (busy || lifetime == null || lifetime.IsCancellationRequested)
                    return;
                var activeLifetime = lifetime;
                var token = ApplicationData.Current.LocalSettings.Values["Notion.Token"] as string;
                if (string.IsNullOrWhiteSpace(token))
                {
                    status.Text = "Configura el token de Notion para actualizar esta actividad.";
                    return;
                }
                busy = true;
                refresh.IsEnabled = false;
                status.Text = "Actualizando checklists…";
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
                    Render(stats);
                    onUpdated?.Invoke(stats);
                    status.Text = $"Actualizado {DateTime.Now:HH:mm:ss}";
                }
                catch (OperationCanceledException)
                {
                    if (!activeLifetime.IsCancellationRequested)
                        status.Text = "La actualización tardó demasiado. Puedes volver a intentarlo.";
                }
                catch (Exception)
                {
                    if (!activeLifetime.IsCancellationRequested)
                        status.Text = "No se pudo actualizar. Se conserva la última información disponible.";
                }
                finally
                {
                    busy = false;
                    if (ReferenceEquals(lifetime, activeLifetime)) refresh.IsEnabled = true;
                }
            }

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
            return root;
        }
    }
}
