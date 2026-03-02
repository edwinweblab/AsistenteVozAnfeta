// Views/GoogleCalendarView.xaml.cs
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anfeta.UI.Views
{
    public sealed partial class GoogleCalendarView : Page
    {
        public GoogleCalendarViewModel ViewModel { get; }

        public GoogleCalendarView()
        {
            ViewModel = App.AppHost.Services.GetRequiredService<GoogleCalendarViewModel>();
            InitializeComponent();
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await ViewModel.InitializeAsync();
        }
    }
}