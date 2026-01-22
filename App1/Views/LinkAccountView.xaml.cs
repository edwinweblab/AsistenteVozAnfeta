using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace Anfeta.UI.Views
{
    public sealed partial class LinkAccountView : Page
    {
        private readonly LinkAccountViewModel _vm;

        public LinkAccountView()
        {
            InitializeComponent();

            _vm = App.AppHost.Services.GetRequiredService<LinkAccountViewModel>();
            DataContext = _vm;

            _vm.RequestNavigateHome += () =>
            {
                Frame?.Navigate(typeof(HomeView));
            };

            _vm.PropertyChanged += ViewModel_PropertyChanged;

            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LinkAccountViewModel.ErrorMessage))
            {
                ErrorText.Visibility = string.IsNullOrWhiteSpace(_vm.ErrorMessage)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            if (Frame?.CanGoBack == true)
                Frame.GoBack();
            else
                Frame?.Navigate(typeof(HomeView));
        }
    }
}
