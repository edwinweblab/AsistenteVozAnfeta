using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;

namespace Anfeta.UI.Views
{
    public sealed partial class LinkAccountView : Page
    {
        private readonly LinkAccountViewModel _vm;

        public LinkAccountView()
        {
            InitializeComponent();

            try
            {
                _vm = App.AppHost.Services.GetRequiredService<LinkAccountViewModel>();
                DataContext = _vm;

                // Navega a home solo tras vinculación exitosa
                _vm.RequestNavigateHome += () => Frame?.Navigate(typeof(HomeView));
                _vm.PropertyChanged += ViewModel_PropertyChanged;

                Loaded += OnPageLoaded;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkAccountView] Error al inicializar: {ex.Message}");
                throw;
            }
        }

        // Carga perfil y actualiza cards al cargar la página
        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateCardVisibility();

                if (_vm.IsAuthenticated)
                    await _vm.LoadProfileAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkAccountView] Error en OnPageLoaded: {ex.Message}");
            }
        }

        // Alterna entre LoginCard y ProfileCard según estado de autenticación
        private void UpdateCardVisibility()
        {
            try
            {
                if (_vm.IsAuthenticated)
                {
                    LoginCard.Visibility = Visibility.Collapsed;
                    ProfileCard.Visibility = Visibility.Visible;
                }
                else
                {
                    LoginCard.Visibility = Visibility.Visible;
                    ProfileCard.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkAccountView] Error en UpdateCardVisibility: {ex.Message}");
            }
        }

        // Reacciona a cambios del ViewModel para actualizar UI
        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(LinkAccountViewModel.ErrorMessage))
                {
                    if (ErrorBorder != null)
                        ErrorBorder.Visibility = string.IsNullOrWhiteSpace(_vm.ErrorMessage)
                            ? Visibility.Collapsed
                            : Visibility.Visible;
                }

                if (e.PropertyName == nameof(LinkAccountViewModel.IsAuthenticated))
                    UpdateCardVisibility();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkAccountView] Error en PropertyChanged: {ex.Message}");
            }
        }

        // Navega de regreso a HomeView
        private void OnBackToHomeClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Frame is null)
                {
                    System.Diagnostics.Debug.WriteLine("[LinkAccountView] Frame es null.");
                    return;
                }

                Frame.Navigate(typeof(HomeView));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkAccountView] Error al navegar: {ex.Message}");
            }
        }
    }
}