using Anfeta.UI.Views;
using App1;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace App1
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // ✅ Arrancar en Home
            ContentFrame.Navigate(typeof(HomeView));
            AppNav.SelectedItem = AppNav.MenuItems[0];
        }

        private void AppNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
                return;

            var tag = item.Tag?.ToString();

            Type pageType = tag switch
            {
                "Home" => typeof(HomeView),
                "Commands" => typeof(CommandsView),
                "Settings" => typeof(SettingsView),
                _ => typeof(HomeView)
            };

            // Evita recargar la misma página
            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType);
            }
        }
    }
}
