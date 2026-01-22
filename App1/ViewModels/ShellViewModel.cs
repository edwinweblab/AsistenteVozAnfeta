using Anfeta.UI.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using System;

namespace Anfeta.UI.ViewModels
{
    public partial class ShellViewModel : ObservableObject
    {
        private readonly AuthStateService _auth;

        public ShellViewModel(AuthStateService auth)
        {
            _auth = auth;

            _auth.PropertyChanged += (_, e) =>
            {
                // Refrescamos solo lo que depende del estado
                if (e.PropertyName == nameof(AuthStateService.IsAuthenticated) ||
                    e.PropertyName == nameof(AuthStateService.IsLinked) ||
                    e.PropertyName == nameof(AuthStateService.Token))
                {
                    OnPropertyChanged(nameof(IsLinked));
                    OnPropertyChanged(nameof(LinkText));
                    OnPropertyChanged(nameof(LinkForeground));
                }
            };
        }

        // UI binds
        public bool IsLinked => _auth.IsLinked;

        public string LinkText => _auth.IsLinked ? "Vinculado" : "No vinculado";

        public Brush LinkForeground =>
            _auth.IsLinked
                ? new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);

        // Evento para que la View navegue
        public event Action? RequestOpenLinkAccount;

        [RelayCommand]
        private void LinkIcon()
        {
            RequestOpenLinkAccount?.Invoke();
        }
    }
}
