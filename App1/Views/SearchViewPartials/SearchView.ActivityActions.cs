using Anfeta.UI.Models.Notion;
using Anfeta.UI.Models.Weblab;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private readonly HashSet<string> _pendingActivityActions = new(StringComparer.OrdinalIgnoreCase);
        private async void CalendarContextComplete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: NotionCalendarActivity activity } control || !_pendingActivityActions.Add(activity.PageId)) return;
            try
            {
                if (await CompleteCalendarProjectActivityAsync(activity, control.XamlRoot))
                {
                    UpdateActionIndexRow(activity, false);
                    await RefreshCalendarDayAfterProjectActionAsync();
                }
            }
            catch (Exception ex) { StatusText.Text = $"Estado: No se pudo terminar → {ex.Message}"; }
            finally { _pendingActivityActions.Remove(activity.PageId); }
        }
        private async void ResultsActivityState_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem { Tag: SearchResultRow row } item || string.IsNullOrWhiteSpace(row.ExternalId) || !_pendingActivityActions.Add(row.ExternalId)) return;
            item.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var activity = await _notionCalendarService.GetActivityByIdAsync(GetSavedNotionToken(), row.ExternalId, cts.Token);
                if (activity == null || GetCalendarProjectPhaseInfo(activity) == null)
                { StatusText.Text = "Estado: Esta página no es una actividad compatible con las reglas del calendario."; return; }
                await HydrateCalendarReviewFlowAsync(new[] { activity }, cts.Token);
                var changed = item.Name == "CtxActivityComplete"
                    ? await CompleteCalendarProjectActivityAsync(activity, XamlRoot)
                    : await PromptAndSendCalendarActivityToReviewAsync(activity, XamlRoot);
                if (!changed) return;
                UpdateActionIndexRow(activity, false);
                row.Name = activity.Title;
                RefreshResultsListView();
                await RefreshCalendarDayAfterProjectActionAsync();
            }
            catch (Exception ex) { StatusText.Text = $"Estado: No se pudo cambiar el estado → {ex.Message}"; }
            finally { item.IsEnabled = true; _pendingActivityActions.Remove(row.ExternalId); }
        }
        private void UpdateActionIndexRow(NotionCalendarActivity activity, bool updateDate)
        {
            var snapshot = App.LocalIndex.GetAll();
            foreach (var row in snapshot)
            {
                if (!string.Equals(row.ExternalId, activity.PageId, StringComparison.OrdinalIgnoreCase)) continue;
                if (updateDate) row.ScheduledDate = $"{activity.Start:yyyy-MM-dd HH:mm} - {activity.End:yyyy-MM-dd HH:mm}";
                else { row.Name = activity.Title; row.SearchText = $"{activity.Title} {activity.Person} {activity.Project} {activity.Status} {activity.UpdateText}"; }
            }
            App.LocalIndex.Set(snapshot);
        }
        private async Task MovePaymentDateAsync(SearchResultRow row, bool nextDay)
        {
            if (string.IsNullOrWhiteSpace(row.ExternalId) || !_pendingActivityActions.Add(row.ExternalId)) return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var token = GetSavedNotionToken();
                var activity = await _notionCalendarService.GetActivityByIdAsync(token, row.ExternalId, cts.Token, datePropertyAlias: "Due Fecha Recordatorio");
                if (activity == null || activity.Start == default) throw new InvalidOperationException("El pago no tiene una fecha legible para moverlo.");
                var date = new DatePicker { Header = "Nueva fecha", Date = new DateTimeOffset(activity.Start.Date.AddDays(nextDay ? 1 : 0)), HorizontalAlignment = HorizontalAlignment.Stretch };
                var panel = new StackPanel { Spacing = 10 };
                panel.Children.Add(new TextBlock { Text = activity.Title, TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(date);
                panel.Children.Add(new TextBlock { Text = "Se conservan la hora y duración. No se marca como pagado.", TextWrapping = TextWrapping.Wrap });
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = nextDay ? "Mover pago al siguiente día" : "Cambiar fecha del pago", Content = panel, PrimaryButtonText = "Mover", CloseButtonText = "Cancelar" };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                var updated = await _notionCalendarService.MoveActivityToDateAsync(token, activity, date.Date.Date, cts.Token, updateActivityCaches: false);
                row.ScheduledDate = $"{updated.Start:yyyy-MM-dd HH:mm} - {updated.End:yyyy-MM-dd HH:mm}";
                UpdateActionIndexRow(updated, true);
                RefreshCalendarExternalOverlaysIfNeeded(true);
                StatusText.Text = $"Estado: Pago movido al {updated.Start:dd/MM/yyyy} ✅";
            }
            catch (Exception ex) { StatusText.Text = $"Estado: No se pudo mover el pago → {ex.Message}"; }
            finally { _pendingActivityActions.Remove(row.ExternalId); }
        }
    }
}
