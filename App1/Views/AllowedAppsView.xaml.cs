using Anfeta.UI.Models;
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;

namespace Anfeta.UI.Views
{
    public sealed partial class AllowedAppsView : Page
    {
        public AllowedAppsViewModel VM { get; }

        public AllowedAppsView()
        {
            InitializeComponent();

            // DI
            VM = App.AppHost.Services.GetRequiredService<AllowedAppsViewModel>();
            DataContext = VM;

            // Cargar al entrar
            _ = VM.LoadAsync();
        }

        // Toggle: habilitar/deshabilitar (ya lo tienes bien)
        private void AllowedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is ToggleSwitch ts && ts.DataContext is LocalAppEntry app)
                {
                    VM.ToggleEnabledCommand.Execute(app);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] AllowedToggle_Toggled ERROR: " + ex);
            }
        }

        // Botón Sinónimos (ya lo tienes bien)
        private async void EditSynonyms_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.DataContext is LocalAppEntry app)
                {
                    await VM.OpenSynonymsDialogAsync(app, this.XamlRoot);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] EditSynonyms_Click ERROR: " + ex);
            }
        }

        // NUEVO: botón "Re-escanear apps instaladas"
        private async void Rescan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await VM.RescanCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] Rescan_Click ERROR: " + ex);
            }
        }

        // NUEVO: botón "Agregar manualmente (.exe)" (por ahora placeholder)
        private async void AddManual_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await VM.AddManualCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] AddManual_Click ERROR: " + ex);
            }
        }
    }
}