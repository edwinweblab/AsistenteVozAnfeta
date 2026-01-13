using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Anfeta.UI.Views;
using App1.FunctionTests;

namespace App1
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // ✅ Arrancar en Home (solo si existe el frame)
            if (ContentFrame != null)
                ContentFrame.Navigate(typeof(HomeView));

            if (AppNav != null && AppNav.MenuItems.Count > 0)
                AppNav.SelectedItem = AppNav.MenuItems[0];
        }

        private void AppNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (ContentFrame == null) return;

            if (args.SelectedItem is not NavigationViewItem item) return;

            var tag = item.Tag as string;
            if (string.IsNullOrWhiteSpace(tag)) return;

            Type pageType = tag switch
            {
                "Home" => typeof(HomeView),
                "Commands" => typeof(CommandsView),
                "Settings" => typeof(SettingsView),
                "Tests" => typeof(TestRunner),
                _ => typeof(HomeView)
            };

            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);
        }
    }
}
