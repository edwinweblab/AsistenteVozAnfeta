using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Anfeta.UI.ViewModels;
using Anfeta.UI.Services;
using Windows.UI;
using System;

namespace Anfeta.UI.Views
{
    public sealed partial class HomeView : Page
    {
        private Storyboard? _ringsStoryboard;
        private Storyboard? _micStoryboard;
        private HomeViewModel? _viewModel;
        private AppStateService? _appState;

        public HomeView()
        {
            InitializeComponent();

            _viewModel = App.AppHost.Services.GetRequiredService<HomeViewModel>();
            _appState = App.AppHost.Services.GetRequiredService<AppStateService>();

            // Bindear ViewModel para IsListening, RecognizedText, etc.
            DataContext = _viewModel;

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _appState.PropertyChanged += AppState_PropertyChanged;

            MicButton.Click += MicButton_Click;
            HelpButton.Click += HelpButton_Click;

            UpdateAudioDeviceDisplay();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(TroubleshootView));   
        }

        // Reaccionar a cambios en AppStateService (audio devices)
        private void AppState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppStateService.InputDeviceName) ||
                e.PropertyName == nameof(AppStateService.OutputDeviceName))
            {
                DispatcherQueue.TryEnqueue(UpdateAudioDeviceDisplay);
            }
        }

        // Actualizar display de audio devices (footer)
        private void UpdateAudioDeviceDisplay()
        {
            if (_appState != null)
            {
                TxtInputDevice.Text = $"Entrada: {_appState.InputDeviceName}";
                TxtOutputDevice.Text = $"Salida: {_appState.OutputDeviceName}";
            }
        }

        // Reaccionar a IsListening para animaciones
        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HomeViewModel.IsListening))
            {
                UpdateMicVisualState(_viewModel!.IsListening);
            }
        }

        private void UpdateMicVisualState(bool isListening)
        {
            if (isListening)
            {
                MicButton.Background = new SolidColorBrush(Colors.Red);
                StartRingsAnimation();
                StartListeningAnimation();
            }
            else
            {
                MicButton.Background = new SolidColorBrush(Color.FromArgb(255, 255, 107, 53));
                StopAllAnimations();
            }
        }

        private void StopAllAnimations()
        {
            _ringsStoryboard?.Stop();
            _micStoryboard?.Stop();
        }

        private void StartListeningAnimation()
        {
            _micStoryboard?.Stop();
            _micStoryboard = new Storyboard
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true
            };

            var ease = new CircleEase { EasingMode = EasingMode.EaseInOut };

            var micX = new DoubleAnimation { From = 1.0, To = 1.08, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
            Storyboard.SetTarget(micX, MicScale);
            Storyboard.SetTargetProperty(micX, "ScaleX");
            _micStoryboard.Children.Add(micX);

            var micY = new DoubleAnimation { From = 1.0, To = 1.08, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
            Storyboard.SetTarget(micY, MicScale);
            Storyboard.SetTargetProperty(micY, "ScaleY");
            _micStoryboard.Children.Add(micY);

            _micStoryboard.Begin();
        }

        private async void MicButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
                await _viewModel.ListenOnceCommand.ExecuteAsync(null);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
                _ = _viewModel.InitializeSpeechCommand.ExecuteAsync(null);
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            StopAllAnimations();
            MicButton.Click -= MicButton_Click;
            HelpButton.Click -= HelpButton_Click;
            if (_viewModel != null)
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

            if (_appState != null)
                _appState.PropertyChanged -= AppState_PropertyChanged;

            _ringsStoryboard = null;
            _micStoryboard = null;
        }

        private void StartRingsAnimation()
        {
            _ringsStoryboard?.Stop();
            _ringsStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

            AddRingAnimations(_ringsStoryboard, Ring1Scale, Ring1, 0, 1500, 0.62, 1.18, 0.18, 0.00);
            AddRingAnimations(_ringsStoryboard, Ring2Scale, Ring2, 300, 1500, 0.56, 1.12, 0.12, 0.00);
            AddRingAnimations(_ringsStoryboard, Ring3Scale, Ring3, 600, 1500, 0.50, 1.06, 0.08, 0.00);

            _ringsStoryboard.Begin();
        }

        private static void AddRingAnimations(Storyboard sb, ScaleTransform scale, UIElement ring,
            int beginMs, int durationMs, double fromScale, double toScale, double fromOpacity, double toOpacity)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseOut };

            var sx = new DoubleAnimation { From = fromScale, To = toScale, Duration = TimeSpan.FromMilliseconds(durationMs), BeginTime = TimeSpan.FromMilliseconds(beginMs), EasingFunction = ease };
            Storyboard.SetTarget(sx, scale);
            Storyboard.SetTargetProperty(sx, "ScaleX");
            sb.Children.Add(sx);

            var sy = new DoubleAnimation { From = fromScale, To = toScale, Duration = TimeSpan.FromMilliseconds(durationMs), BeginTime = TimeSpan.FromMilliseconds(beginMs), EasingFunction = ease };
            Storyboard.SetTarget(sy, scale);
            Storyboard.SetTargetProperty(sy, "ScaleY");
            sb.Children.Add(sy);

            var op = new DoubleAnimation { From = fromOpacity, To = toOpacity, Duration = TimeSpan.FromMilliseconds(durationMs), BeginTime = TimeSpan.FromMilliseconds(beginMs), EasingFunction = ease };
            Storyboard.SetTarget(op, ring);
            Storyboard.SetTargetProperty(op, "Opacity");
            sb.Children.Add(op);
        }
    }
}