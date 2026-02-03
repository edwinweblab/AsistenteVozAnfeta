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

            _vm.RequestNavigateHome += () => Frame?.Navigate(typeof(HomeView));
            _vm.PropertyChanged += ViewModel_PropertyChanged;

            // Cargar perfil si ya está autenticado
            Loaded += OnPageLoaded;
        }

        // Carga el perfil del usuario y actualiza visibilidad de cards
        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            UpdateCardVisibility();

            if (_vm.IsAuthenticated)
            {
                await _vm.LoadProfileAsync();
            }
        }

        // Actualiza qué card mostrar según estado de autenticación
        private void UpdateCardVisibility()
        {
            if (_vm.IsAuthenticated)
            {
                // Mostrar perfil, ocultar login
                LoginCard.Visibility = Visibility.Collapsed;
                ProfileCard.Visibility = Visibility.Visible;
            }
            else
            {
                // Mostrar login, ocultar perfil
                LoginCard.Visibility = Visibility.Visible;
                ProfileCard.Visibility = Visibility.Collapsed;
            }
        }

        // Actualiza visibilidad cuando cambian propiedades del ViewModel
        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LinkAccountViewModel.ErrorMessage))
            {
                if (ErrorBorder != null)
                {
                    ErrorBorder.Visibility = string.IsNullOrWhiteSpace(_vm.ErrorMessage)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }
            }

            // Actualizar cards cuando cambia estado de autenticación
            if (e.PropertyName == nameof(LinkAccountViewModel.IsAuthenticated))
            {
                UpdateCardVisibility();
            }
        }

        // Navega de regreso a HomeView
        private void OnBackToHomeClicked(object sender, RoutedEventArgs e)
        {
            Frame?.Navigate(typeof(HomeView));
        }
    }
}