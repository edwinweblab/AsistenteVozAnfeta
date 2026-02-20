using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class ApiKeysView : Page
    {
        private readonly ApiKeyRepository _repo;
        private readonly GroqKeyValidator _validator;
        private readonly ApiKeyService _apiKeyService;

        public ApiKeysView()
        {
            InitializeComponent();

            _repo = App.AppHost.Services.GetRequiredService<ApiKeyRepository>();
            _validator = App.AppHost.Services.GetRequiredService<GroqKeyValidator>();
            _apiKeyService = App.AppHost.Services.GetRequiredService<ApiKeyService>();

            Loaded += async (_, __) => await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            var all = await _repo.GetAllAsync("groq");
            LvKeys.ItemsSource = all;
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            var parent = btn.Parent as DependencyObject;
            while (parent != null && parent is not Grid)
                parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);

            if (parent is Grid grid)
            {
                var nameBox = FindChild<TextBox>(grid, "NameBox");
                if (nameBox != null)
                {
                    nameBox.Focus(FocusState.Programmatic);
                    nameBox.SelectAll();
                }
            }
        }

        private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);

                if (child is T fe && fe.Name == name)
                    return fe;

                var result = FindChild<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }

        private async void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnAdd.IsEnabled = false;

                var name = string.IsNullOrWhiteSpace(TxtName.Text) ? null : TxtName.Text.Trim();
                var key = TxtApiKey.Password?.Trim() ?? "";
                var makeActive = ChkMakeActive.IsChecked == true;

                var (ok, error) = await _validator.ValidateAsync(key);
                if (!ok)
                {
                    ShowStatus(error ?? "API key inválida.", InfoBarSeverity.Error);
                    return;
                }

                await _repo.InsertAsync("groq", name, key, makeActive, DateTime.UtcNow.ToString("o"));

                _apiKeyService.NotifyKeysChanged();

                if (makeActive)
                {
                    var runtime = App.AppHost.Services.GetRequiredService<GroqRuntimeService>();
                    var (okWarm, errWarm) = await runtime.WarmupAsync();
                    if (!okWarm)
                        ShowStatus($"Key guardada pero Groq no quedó listo: {errWarm}", InfoBarSeverity.Warning);
                }

                TxtName.Text = "";
                TxtApiKey.Password = "";
                ShowStatus("API key guardada correctamente.", InfoBarSeverity.Success);

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                BtnAdd.IsEnabled = true;
            }
        }

        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && long.TryParse(btn.Tag?.ToString(), out var id))
                {
                    await _repo.SetActiveAsync(id, "groq");
                    _apiKeyService.NotifyKeysChanged();

                    ShowStatus("API key activada.", InfoBarSeverity.Success);
                    await ReloadAsync();

                    var runtime = App.AppHost.Services.GetRequiredService<GroqRuntimeService>();
                    var (okWarm, errWarm) = await runtime.WarmupAsync();
                    if (!okWarm)
                        ShowStatus($"Key activada pero Groq no quedó listo: {errWarm}", InfoBarSeverity.Warning);
                }
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, InfoBarSeverity.Error);
            }
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && long.TryParse(btn.Tag?.ToString(), out var id))
                {
                    await _repo.DeleteAsync(id);
                    _apiKeyService.NotifyKeysChanged();

                    ShowStatus("API key eliminada.", InfoBarSeverity.Success);
                    await ReloadAsync();

                    var runtime = App.AppHost.Services.GetRequiredService<GroqRuntimeService>();
                    var (okWarm, errWarm) = await runtime.WarmupAsync();
                    if (!okWarm)
                        ShowStatus(errWarm ?? "Groq no disponible.", InfoBarSeverity.Warning);
                }
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, InfoBarSeverity.Error);
            }
        }

        private async void Name_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is TextBox tb && tb.DataContext is ApiKeyRow row)
                {
                    var name = string.IsNullOrWhiteSpace(tb.Text) ? null : tb.Text.Trim();
                    await _repo.UpdateNameAsync(row.Id, name);
                }
            }
            catch
            {
            }
        }

        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                InfoStatus.Message = message;
                InfoStatus.Severity = severity;
                InfoStatus.IsOpen = true;
            });
        }
    }
}
