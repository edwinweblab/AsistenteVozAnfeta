using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anfeta.UI.Views
{
    public sealed partial class TroubleshootView : Page
    {
        public TroubleshootView()
        {
            InitializeComponent();
            BackButton.Click += BackButton_Click;
        }

        // Volver a la página anterior
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}