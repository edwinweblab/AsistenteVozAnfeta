// ViewModels/LinkSharedAccountViewModel.cs
using Anfeta.UI.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace Anfeta.UI.ViewModels
{
    public sealed partial class LinkSharedAccountViewModel : ObservableObject
    {
        private readonly WeblabSharedAuthClient _sharedApi;
        private readonly SharedAuthStateService _sharedState;

        [ObservableProperty]
        private string _user = "";

        [ObservableProperty]
        private string _pass = "";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "";

        public LinkSharedAccountViewModel(WeblabSharedAuthClient sharedApi, SharedAuthStateService sharedState)
        {
            _sharedApi = sharedApi;
            _sharedState = sharedState;
        }

        public bool CanLogin => !IsBusy
                                && !string.IsNullOrWhiteSpace(User)
                                && !string.IsNullOrWhiteSpace(Pass);

        partial void OnUserChanged(string value) => LoginSharedCommand.NotifyCanExecuteChanged();
        partial void OnPassChanged(string value) => LoginSharedCommand.NotifyCanExecuteChanged();
        partial void OnIsBusyChanged(bool value) => LoginSharedCommand.NotifyCanExecuteChanged();

        [RelayCommand(CanExecute = nameof(CanLogin))]
        private async Task LoginSharedAsync()
        {
            IsBusy = true;
            StatusMessage = "";

            try
            {
                var res = await _sharedApi.LoginAsync(User.Trim(), Pass);
                if (!res.Ok)
                {
                    StatusMessage = $"Error: {res.RawError}";
                    return;
                }

                await _sharedState.SetSignedInAsync(res.Token!, res.RefreshToken!);
                StatusMessage = "Conectado (Shared Auth).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task LogoutSharedAsync()
        {
            IsBusy = true;
            try
            {
                await _sharedState.SignOutAsync();
                StatusMessage = "Sesión cerrada.";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
