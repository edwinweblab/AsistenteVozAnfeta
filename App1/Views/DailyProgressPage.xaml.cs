using Anfeta.UI.Views.DailyProgress;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class DailyProgressPage : Page
    {
        private const string LS_NotionToken = "Notion.Token";
        private readonly Anfeta.UI.Services.Notion.NotionCalendarService _calendarService = new();

        public DailyProgressPage()
        {
            InitializeComponent();
            Loaded += DailyProgressPage_Loaded;
            Unloaded += DailyProgressPage_Unloaded;

            MainDailyProgressView.CloseRequested += MainDailyProgressView_CloseRequested;
            MainDailyProgressView.OpenActivityRequested += MainDailyProgressView_OpenActivityRequested;
        }

        private async void DailyProgressPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            MainDailyProgressView.Initialize(_calendarService, token);
            await MainDailyProgressView.OpenAsync(DateTime.Today);
        }

        private void DailyProgressPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
        }

        private void MainDailyProgressView_CloseRequested(object? sender, EventArgs e)
        {
            if (Frame?.CanGoBack == true)
            {
                Frame.GoBack();
            }
        }

        private async void MainDailyProgressView_OpenActivityRequested(object? sender, DailyProgressOpenActivityEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PageUrl))
                return;

            try
            {
                if (Uri.TryCreate(e.PageUrl.Trim(), UriKind.Absolute, out var uri))
                {
                    await Launcher.LaunchUriAsync(uri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DailyProgressPage] Error abriendo actividad: {ex.Message}");
            }
        }
    }
}
