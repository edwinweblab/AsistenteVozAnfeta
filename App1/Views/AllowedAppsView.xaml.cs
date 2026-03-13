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

            VM = App.AppHost.Services.GetRequiredService<AllowedAppsViewModel>();
            DataContext = VM;

            _ = VM.LoadAsync();
        }

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

        private async void EditSynonyms_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is LocalAppEntry app)
                {
                    await VM.OpenSynonymsDialogAsync(app, this.XamlRoot);
                    return;
                }

                Debug.WriteLine("[AllowedAppsView] EditSynonyms_Click: no se encontró LocalAppEntry en Tag.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] EditSynonyms_Click ERROR: " + ex);
            }
        }

        private async void Rescan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VM.RescanCommand != null)
                    await VM.RescanCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] Rescan_Click ERROR: " + ex);
            }
        }

        private async void AddManual_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VM.AddManualCommand != null)
                    await VM.AddManualCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsView] AddManual_Click ERROR: " + ex);
            }
        }
    }
}