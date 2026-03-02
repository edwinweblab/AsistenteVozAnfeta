using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;

namespace Anfeta.UI.Views
{
    public sealed partial class LinkSharedAccountView : Page
    {
        private readonly LinkSharedAccountViewModel _vm;
        private bool _passwordVisible = false;

        public LinkSharedAccountView()
        {
            InitializeComponent();

            try
            {
                _vm = App.AppHost.Services.GetRequiredService<LinkSharedAccountViewModel>();
                DataContext = _vm;
                _vm.PropertyChanged += ViewModel_PropertyChanged;

                // Aplicar estado inicial al cargar la vista
                UpdateAuthState(_vm.IsAuthenticated);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkSharedAccountView] Error al inicializar: {ex.Message}");
                throw;
            }
        }

        // Alterna entre el formulario de login y el panel de sesión activa.
        // Entrada: isAuthenticated — estado actual de la sesión compartida.
        private void UpdateAuthState(bool isAuthenticated)
        {
            if (isAuthenticated)
            {
                // Mostrar panel de sesión activa
                SessionActivePanel.Visibility = Visibility.Visible;
                LoginFormPanel.Visibility = Visibility.Collapsed;
                LoginButton.Visibility = Visibility.Collapsed;

                // Encabezado en verde con icono de candado
                HeaderIconBorder.Background = new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x34, 0xD3, 0x99));
                HeaderIcon.Glyph = "\uE72E"; // Candado
                SubtitleText.Text = "Sesión activa — el equipo tiene acceso a Weblab";
                SubtitleText.Foreground = new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x34, 0xD3, 0x99));

                // Mostrar usuario activo
                ActiveUserText.Text = $"Conectado como: {_vm.User}";
            }
            else
            {
                // Mostrar formulario de login
                SessionActivePanel.Visibility = Visibility.Collapsed;
                LoginFormPanel.Visibility = Visibility.Visible;
                LoginButton.Visibility = Visibility.Visible;

                // Encabezado en accent normal
                HeaderIconBorder.Background = (Brush)Application.Current.Resources["AnfetaAccentBrush"];
                HeaderIcon.Glyph = "\uE720"; // Persona
                SubtitleText.Text = "Accede con las credenciales compartidas del equipo";
                SubtitleText.Foreground = new SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B));
            }
        }

        // Reacciona a cambios del ViewModel
        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            try
            {
                // Cambio de estado de autenticación → actualizar panels
                if (e.PropertyName == nameof(LinkSharedAccountViewModel.IsAuthenticated))
                {
                    DispatcherQueue.TryEnqueue(() => UpdateAuthState(_vm.IsAuthenticated));
                    return;
                }

                if (e.PropertyName == nameof(LinkSharedAccountViewModel.StatusMessage) ||
                    e.PropertyName == nameof(LinkSharedAccountViewModel.IsStatusError))
                {
                    if (StatusBorder is null) return;

                    var hasMessage = !string.IsNullOrWhiteSpace(_vm.StatusMessage);
                    StatusBorder.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;

                    if (hasMessage)
                    {
                        StatusBorder.BorderBrush = _vm.IsStatusError
                            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x99, 0xEF, 0x44, 0x44))
                            : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x99, 0x34, 0xD3, 0x99));

                        StatusBorder.Background = _vm.IsStatusError
                            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x1A, 0xEF, 0x44, 0x44))
                            : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x1A, 0x34, 0xD3, 0x99));

                        if (StatusIcon != null)
                        {
                            StatusIcon.Glyph = _vm.IsStatusError ? "\uE7BA" : "\uE73E";
                            StatusIcon.Foreground = _vm.IsStatusError
                                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xEF, 0x44, 0x44))
                                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x34, 0xD3, 0x99));
                        }
                    }
                }

                // Limpiar campos cuando el ViewModel limpia Pass (ej. logout)
                if (e.PropertyName == nameof(LinkSharedAccountViewModel.Pass) &&
                    string.IsNullOrEmpty(_vm.Pass))
                {
                    PassBox.Password = string.Empty;
                    PassTextBox.Text = string.Empty;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkSharedAccountView] Error en PropertyChanged: {ex.Message}");
            }
        }

        // Sincroniza la contraseña del PasswordBox con el ViewModel y con el TextBox
        private void PassBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is PasswordBox pb)
                {
                    _vm.Pass = pb.Password ?? string.Empty;

                    if (PassTextBox.Text != pb.Password)
                        PassTextBox.Text = pb.Password;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkSharedAccountView] Error en PassBox_PasswordChanged: {ex.Message}");
            }
        }

        // Alterna entre mostrar y ocultar la contraseña
        private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _passwordVisible = !_passwordVisible;

                if (_passwordVisible)
                {
                    PassTextBox.Text = PassBox.Password;
                    PassBox.Visibility = Visibility.Collapsed;
                    PassTextBox.Visibility = Visibility.Visible;
                    EyeIcon.Glyph = "\uED1A"; // Ojo tachado
                    PassTextBox.Focus(FocusState.Programmatic);
                }
                else
                {
                    PassBox.Password = PassTextBox.Text;
                    PassTextBox.Visibility = Visibility.Collapsed;
                    PassBox.Visibility = Visibility.Visible;
                    EyeIcon.Glyph = "\uE7B3"; // Ojo normal
                    PassBox.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkSharedAccountView] Error en TogglePasswordVisibility: {ex.Message}");
            }
        }

        // Navega de regreso a HomeView
        private void OnBackToHomeClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Frame is null)
                {
                    System.Diagnostics.Debug.WriteLine("[LinkSharedAccountView] Frame es null.");
                    return;
                }

                Frame.Navigate(typeof(HomeView));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinkSharedAccountView] Error al navegar: {ex.Message}");
            }
        }
    }
}
