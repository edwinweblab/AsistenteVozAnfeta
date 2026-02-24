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

        private void AllowedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is ToggleSwitch ts && ts.DataContext is LocalAppEntry app)
                {
                    // El Toggle ya cambió app.Enabled por el TwoWay binding.
                    VM.ToggleEnabledCommand.Execute(app);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] AllowedToggle_Toggled ERROR: " + ex);
            }
        }

        private async void EditSynonyms_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.DataContext is LocalAppEntry app)
                {
                    // Importante: usar el XamlRoot de esta Page / ventana
                    await VM.OpenSynonymsDialogAsync(app, this.XamlRoot);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] EditSynonyms_Click ERROR: " + ex);
            }
        }
    }
}