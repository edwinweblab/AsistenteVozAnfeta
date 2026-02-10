using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anfeta.UI.Views
{
    public sealed partial class LinkSharedAccountView : Page
    {
        private readonly LinkSharedAccountViewModel _vm;

        public LinkSharedAccountView()
        {
            InitializeComponent();

            _vm = App.AppHost.Services.GetRequiredService<LinkSharedAccountViewModel>();
            DataContext = _vm;
        }

        private void PassBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb)
            {
                _vm.Pass = pb.Password ?? "";
            }
        }
    }
}
