using Anfeta.UI.FunctionTests;
using Anfeta.UI.ViewModels;
using Anfeta.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;

namespace Anfeta.UI
{
    public sealed partial class MainWindow : Window
    {
        private readonly ShellViewModel _shell;

        public MainWindow()
        {
            InitializeComponent();

            _shell = App.AppHost.Services.GetRequiredService<ShellViewModel>();

            // WinUI 3: Window no tiene DataContext -> asigna al root visual
            Root.DataContext = _shell;

            _shell.RequestOpenLinkAccount += () =>
            {
                if (ContentFrame?.CurrentSourcePageType != typeof(LinkAccountView))
                    ContentFrame?.Navigate(typeof(LinkAccountView));
            };
            _shell.RequestOpenLinkSharedAccount += () =>
            {
                if (ContentFrame?.CurrentSourcePageType != typeof(LinkSharedAccountView))
                    ContentFrame?.Navigate(typeof(LinkSharedAccountView));
            };


            if (ContentFrame != null)
                ContentFrame.Navigate(typeof(HomeView));

            if (AppNav != null && AppNav.MenuItems.Count > 0)
                AppNav.SelectedItem = AppNav.MenuItems[0];

            this.Closed += MainWindow_Closed;

            Debug.WriteLine("MAINWINDOW: constructor OK (sin segundo plano)");
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
                "AllowedApps" => typeof(AllowedAppsView),
                "Search" => typeof(SearchView),
                "Settings" => typeof(SettingsView),
                "Tests" => typeof(TestRunner),
                _ => typeof(HomeView)
            };

            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            Debug.WriteLine("MAINWINDOW: Closed");
            ((App)Application.Current).CleanupAndExit();
        }
    }
}
