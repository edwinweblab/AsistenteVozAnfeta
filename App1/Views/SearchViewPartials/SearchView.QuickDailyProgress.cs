using Anfeta.UI.Models.DailyProgress;
using Anfeta.UI.Models.Notion;
using Anfeta.UI.Services.Notion;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private enum CalendarPersonPanelViewMode
        {
            Activities,
            QuickProgress
        }

        private CalendarPersonPanelViewMode _calendarPersonPanelMode =
            CalendarPersonPanelViewMode.Activities;

        private DailyProgressPersonSnapshot? _currentPersonDailySnapshot;
        private CancellationTokenSource? _quickProgressLoadCts;

        private void InitializeCalendarPersonPanelHeader(string person)
        {
            if (CalendarPersonPreviewAvatarBadge != null)
            {
                var initial = !string.IsNullOrWhiteSpace(person)
                    ? person.Trim().Substring(0, 1).ToUpperInvariant()
                    : "?";
                CalendarPersonPreviewAvatarText.Text = initial;
            }

            // Restablecer estados de toggles según el modo activo
            UpdateCalendarPersonPanelModeVisuals();
        }

        private void UpdateCalendarPersonPanelModeVisuals()
        {
            var isActivities = _calendarPersonPanelMode == CalendarPersonPanelViewMode.Activities;

            if (CalendarPersonModeActivitiesToggle != null)
                CalendarPersonModeActivitiesToggle.IsChecked = isActivities;

            if (CalendarPersonModeQuickProgressToggle != null)
                CalendarPersonModeQuickProgressToggle.IsChecked = !isActivities;

            if (CalendarPersonActivitiesScrollViewer != null)
                CalendarPersonActivitiesScrollViewer.Visibility = isActivities ? Visibility.Visible : Visibility.Collapsed;

            if (CalendarPersonQuickProgressHost != null)
                CalendarPersonQuickProgressHost.Visibility = !isActivities ? Visibility.Visible : Visibility.Collapsed;

            // Ajustar estilos visuales de los toggles
            if (CalendarPersonModeActivitiesToggle != null)
            {
                CalendarPersonModeActivitiesToggle.Background = isActivities
                    ? new SolidColorBrush(Color.FromArgb(255, 21, 29, 37))
                    : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                CalendarPersonModeActivitiesToggle.Foreground = isActivities
                    ? new SolidColorBrush(Color.FromArgb(255, 56, 189, 248))
                    : new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
            }

            if (CalendarPersonModeQuickProgressToggle != null)
            {
                CalendarPersonModeQuickProgressToggle.Background = !isActivities
                    ? new SolidColorBrush(Color.FromArgb(255, 21, 29, 37))
                    : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                CalendarPersonModeQuickProgressToggle.Foreground = !isActivities
                    ? new SolidColorBrush(Color.FromArgb(255, 74, 222, 128))
                    : new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
            }
        }

        private void CalendarPersonModeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, CalendarPersonModeActivitiesToggle))
            {
                _calendarPersonPanelMode = CalendarPersonPanelViewMode.Activities;
            }
            else if (ReferenceEquals(sender, CalendarPersonModeQuickProgressToggle))
            {
                _calendarPersonPanelMode = CalendarPersonPanelViewMode.QuickProgress;
            }

            UpdateCalendarPersonPanelModeVisuals();

            if (_calendarPersonPanelMode == CalendarPersonPanelViewMode.QuickProgress)
            {
                _ = LoadAndRenderQuickDailyProgressAsync(_calendarPersonPreviewPerson);
            }
        }

        public void SwitchToCalendarPersonQuickProgressMode()
        {
            _calendarPersonPanelMode = CalendarPersonPanelViewMode.QuickProgress;
            UpdateCalendarPersonPanelModeVisuals();
            _ = LoadAndRenderQuickDailyProgressAsync(_calendarPersonPreviewPerson);
        }

        private void CalendarHeaderQuickDailyProgress_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var person = (element.Tag?.ToString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(person))
                return;

            _calendarPersonPanelMode = CalendarPersonPanelViewMode.QuickProgress;
            ShowCalendarPersonPreview(person);
            _ = LoadAndRenderQuickDailyProgressAsync(person);
        }

        private async Task LoadAndRenderQuickDailyProgressAsync(string person)
        {
            if (string.IsNullOrWhiteSpace(person) || CalendarPersonQuickChecklistItems == null)
                return;

            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            if (string.IsNullOrWhiteSpace(token))
            {
                CalendarPersonPreviewSummary.Text = "Configura primero el token de Notion.";
                return;
            }

            try
            {
                _quickProgressLoadCts?.Cancel();
                _quickProgressLoadCts?.Dispose();
            }
            catch { }

            _quickProgressLoadCts = new CancellationTokenSource();
            var ct = _quickProgressLoadCts.Token;

            // Mostrar estado de carga si la lista está vacía
            if (CalendarPersonQuickChecklistItems.Children.Count == 0)
            {
                CalendarPersonQuickChecklistItems.Children.Clear();
                CalendarPersonQuickChecklistItems.Children.Add(
                    new TextBlock
                    {
                        Text = $"Cargando avance y checklists de {person}…",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)),
                        Margin = new Thickness(4, 8, 4, 8)
                    });
            }

            try
            {
                var snapshot = await Task.Run(async () =>
                {
                    var dailyService = new NotionDailyProgressService();
                    return await dailyService.BuildAsync(
                        _notionCalendarService,
                        token,
                        _calendarSelectedDate,
                        forceRefresh: false,
                        requireFreshDay: false,
                        progress: null,
                        cancellationToken: ct,
                        targetPerson: person);
                }, ct);

                if (ct.IsCancellationRequested)
                    return;

                var personSnapshot = snapshot.People.FirstOrDefault(p =>
                    string.Equals(p.Name, person, StringComparison.OrdinalIgnoreCase));

                _currentPersonDailySnapshot = personSnapshot;

                DispatcherQueue.TryEnqueue(() =>
                {
                    RenderQuickDailyProgressView(person, personSnapshot);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    CalendarPersonPreviewSummary.Text = $"Error al cargar avance: {ex.Message}";
                });
            }
        }

        private void RenderQuickDailyProgressView(
            string person,
            DailyProgressPersonSnapshot? personSnapshot)
        {
            if (CalendarPersonQuickChecklistItems == null)
                return;

            CalendarPersonQuickChecklistItems.Children.Clear();

            if (personSnapshot != null)
            {
                // Subtítulo de KPIs: Cobertura X% · avance YH / ZH · N rezagadas
                var scheduledHours = Math.Round(personSnapshot.ScheduledMinutes / 60.0, 1);
                var progressHours = Math.Round(personSnapshot.ProgressMinutes / 60.0, 1);
                var laggingCount = personSnapshot.Lagging?.Count ?? 0;

                CalendarPersonPreviewSummary.Text =
                    $"Cobertura {personSnapshot.CoveragePercentage}% · avance {progressHours}H / {scheduledHours}H · {laggingCount} rezagadas";

                var completedItems = personSnapshot.EnrichedCompletedItemsToday;
                var totalCompleted = completedItems?.Count ?? personSnapshot.CompletedItemsToday?.Count ?? 0;

                if (CalendarPersonQuickCompletedCountText != null)
                {
                    CalendarPersonQuickCompletedCountText.Text = $"{totalCompleted} completados";
                }

                if (completedItems != null && completedItems.Count > 0)
                {
                    foreach (var item in completedItems)
                    {
                        CalendarPersonQuickChecklistItems.Children.Add(
                            BuildEnrichedCheckCardForSidePanel(item));
                    }
                }
                else
                {
                    CalendarPersonQuickChecklistItems.Children.Add(
                        new Border
                        {
                            Padding = new Thickness(14, 12, 14, 12),
                            Background = new SolidColorBrush(Color.FromArgb(255, 17, 24, 32)),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 43, 61, 77)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(8),
                            Child = new TextBlock
                            {
                                Text = "No se registraron casillas marcadas como hechas en este día.",
                                FontSize = 11,
                                Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)),
                                TextWrapping = TextWrapping.Wrap
                            }
                        });
                }
            }
            else
            {
                CalendarPersonPreviewSummary.Text = $"No hay datos de avance para {person} en este día.";
                if (CalendarPersonQuickCompletedCountText != null)
                    CalendarPersonQuickCompletedCountText.Text = "0 completados";

                CalendarPersonQuickChecklistItems.Children.Add(
                    new TextBlock
                    {
                        Text = "Sin actividades o avance registrado.",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)),
                        Margin = new Thickness(4, 8, 4, 8)
                    });
            }
        }

        private UIElement BuildEnrichedCheckCardForSidePanel(DailyProgressCompletedCheckItem item)
        {
            var card = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(Color.FromArgb(255, 17, 24, 32)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 36, 52, 67)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };

            var mainGrid = new Grid { RowSpacing = 6 };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Fila 0: Badges
            var topRow = new Grid { ColumnSpacing = 6 };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badgesPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Badge Verde "✓ Hecho"
            var checkBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 16, 37, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "✓ Hecho",
                    FontSize = 9.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128))
                }
            };
            badgesPanel.Children.Add(checkBadge);

            // Badge Dominio
            var domainText = !string.IsNullOrWhiteSpace(item.ActivityDomain) ? item.ActivityDomain : item.ActivityProject;
            if (!string.IsNullOrWhiteSpace(domainText))
            {
                var domainBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(255, 12, 34, 46)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 28, 77, 105)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = domainText,
                        FontSize = 9.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248))
                    }
                };
                badgesPanel.Children.Add(domainBadge);
            }

            // Badge Revisión
            var revisionTitle = !string.IsNullOrWhiteSpace(item.ActivityShortTitle)
                ? item.ActivityShortTitle
                : item.ActivityTitle;

            if (!string.IsNullOrWhiteSpace(revisionTitle))
            {
                var revBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(255, 24, 33, 44)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 51, 65, 85)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = $"Revisión: {revisionTitle}",
                        FontSize = 9.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 226, 232, 240))
                    }
                };
                badgesPanel.Children.Add(revBadge);
            }

            Grid.SetColumn(badgesPanel, 0);
            topRow.Children.Add(badgesPanel);

            // Hora si está disponible
            if (item.CompletedAt != DateTimeOffset.MinValue)
            {
                var timeText = new TextBlock
                {
                    Text = item.CompletedAt.ToLocalTime().ToString("hh:mm tt", CultureInfo.CurrentCulture),
                    FontSize = 9.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 143, 163, 181)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(timeText, 1);
                topRow.Children.Add(timeText);
            }

            Grid.SetRow(topRow, 0);
            mainGrid.Children.Add(topRow);

            // Fila 1: Texto del checklist
            var textBlock = new TextBlock
            {
                Text = item.Text,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 241, 245, 249)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(textBlock, 1);
            mainGrid.Children.Add(textBlock);

            card.Child = mainGrid;
            return card;
        }

        private IReadOnlyList<DailyProgressCompletedCheckItem> GetCalendarPersonCardCompletedChecksToday(
            NotionCalendarActivity activity)
        {
            if (activity == null || string.IsNullOrWhiteSpace(activity.PageId) || _currentPersonDailySnapshot == null)
                return Array.Empty<DailyProgressCompletedCheckItem>();

            var enriched = _currentPersonDailySnapshot.EnrichedCompletedItemsToday;
            if (enriched == null || enriched.Count == 0)
                return Array.Empty<DailyProgressCompletedCheckItem>();

            return enriched.Where(item =>
                string.Equals(item.BlockId, activity.PageId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.ActivityTitle, activity.Title, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.PageUrl) && string.Equals(item.PageUrl, activity.PageUrl, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private UIElement BuildCalendarPersonCardChecksSection(
            IReadOnlyList<DailyProgressCompletedCheckItem> checks)
        {
            var panel = new StackPanel
            {
                Spacing = 5,
                Margin = new Thickness(0, 6, 0, 4)
            };

            var header = new TextBlock
            {
                Text = $"✓ {checks.Count} checklist(s) completado(s) hoy:",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128))
            };
            panel.Children.Add(header);

            foreach (var check in checks)
            {
                var itemBorder = new Border
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = new SolidColorBrush(Color.FromArgb(18, 74, 222, 128)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 74, 222, 128)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Child = new TextBlock
                    {
                        Text = $"• {check.Text}",
                        FontSize = 10.5,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 252, 231)),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                panel.Children.Add(itemBorder);
            }

            return panel;
        }

        private async void CalendarPersonQuickViewAll_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_calendarPersonPreviewPerson))
                return;

            // Abrir el panel completo de avance diario filtrado por esta persona
            await OpenDailyProgressForPersonAsync(_calendarSelectedDate, _calendarPersonPreviewPerson);
        }
    }
}
