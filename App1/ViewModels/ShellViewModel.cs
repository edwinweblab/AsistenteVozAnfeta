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
        private readonly SharedAuthStateService _sharedAuth;

        public ShellViewModel(AuthStateService auth, SharedAuthStateService sharedAuth)
        {
            _auth = auth;
            _sharedAuth = sharedAuth;

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
            _sharedAuth.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SharedAuthStateService.IsAuthenticated) ||
                    e.PropertyName == nameof(SharedAuthStateService.IsLinked) ||
                    e.PropertyName == nameof(SharedAuthStateService.Token))
                {
                    OnPropertyChanged(nameof(IsSharedLinked));
                    OnPropertyChanged(nameof(SharedLinkText));
                    OnPropertyChanged(nameof(SharedLinkForeground));
                }
            };

        }

        // UI binds
        public bool IsLinked => _auth.IsLinked;

        public string LinkText => _auth.IsLinked ? "Vinculado" : "No vinculado";
        public bool IsSharedLinked => _sharedAuth.IsLinked;

        public string SharedLinkText => _sharedAuth.IsLinked ? "Shared OK" : "Shared";

        public Brush SharedLinkForeground =>
            _sharedAuth.IsLinked
                ? new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue)
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);

        public Brush LinkForeground =>
            _auth.IsLinked
                ? new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);

        // Evento para que la View navegue
        public event Action? RequestOpenLinkAccount;

        public event Action? RequestOpenLinkSharedAccount;


        [RelayCommand]
        private void LinkIcon()
        {
            RequestOpenLinkAccount?.Invoke();
        }

        [RelayCommand]
        private void LinkSharedIcon()
        {
            RequestOpenLinkSharedAccount?.Invoke();
        }

    }
}
