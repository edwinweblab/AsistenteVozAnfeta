using Anfeta.UI.Views.DailyProgress;
using Microsoft.UI.Xaml;
using System;
using Windows.Storage;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private bool _dailyProgressEventsHooked;

        private async void CalendarDailyProgress_Click(
            object sender,
            RoutedEventArgs e)
        {
            var token =
                ApplicationData.Current.LocalSettings.Values[
                    LS_NotionToken] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            EnsureDailyProgressEvents();

            DailyProgressPanel.Initialize(
                _notionCalendarService,
                token);

            DailyProgressPanel.Visibility =
                Visibility.Visible;

            StatusText.Text =
                "Estado: Avance diario · usando caché del calendario…";

            try
            {
                await DailyProgressPanel.OpenAsync(
                    _calendarSelectedDate);
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir Avance diario → {ex.Message}";
            }
        }

        private void EnsureDailyProgressEvents()
        {
            if (_dailyProgressEventsHooked)
                return;

            DailyProgressPanel.CloseRequested +=
                DailyProgressPanel_CloseRequested;

            DailyProgressPanel.OpenActivityRequested +=
                DailyProgressPanel_OpenActivityRequested;

            _dailyProgressEventsHooked = true;
        }

        private void DailyProgressPanel_CloseRequested(
            object? sender,
            EventArgs e)
        {
            DailyProgressPanel.Visibility =
                Visibility.Collapsed;

            StatusText.Text =
                "Estado: Calendario";
        }

        private async void DailyProgressPanel_OpenActivityRequested(
            object? sender,
            DailyProgressOpenActivityEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(
                    e.PageUrl))
            {
                StatusText.Text =
                    "Estado: La actividad no tiene URL de Notion.";
                return;
            }

            await OpenNotionPageWithFallbackAsync(
                e.PageUrl,
                desktopSuccessStatus:
                    "Actividad abierta en Notion Desktop",
                browserSuccessStatus:
                    "Actividad abierta en el navegador",
                failureStatus:
                    "No se pudo abrir la actividad",
                invalidUrlStatus:
                    "La actividad no tiene una URL válida");
        }
    }
}
