using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Anfeta.UI.ViewModels;
using Windows.UI;

namespace Anfeta.UI.Views
{
    public sealed partial class HomeView : Page
    {
        private Storyboard? _ringsStoryboard;
        private Storyboard? _micStoryboard;
        private HomeViewModel? _viewModel;

        public HomeView()
        {
            InitializeComponent();

            _viewModel = App.AppHost.Services.GetRequiredService<HomeViewModel>();
            DataContext = _viewModel;

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            MicButton.Click += MicButton_Click;
        }

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
                // ROJO + animaciones
                MicButton.Background = new SolidColorBrush(Colors.Red);
                StartRingsAnimation();
                StartListeningAnimation();
            }
            else
            {
                // NARANJA + detener animaciones
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

            var micX = new DoubleAnimation
            {
                From = 1.0,
                To = 1.08,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = ease
            };
            Storyboard.SetTarget(micX, MicScale);
            Storyboard.SetTargetProperty(micX, "ScaleX");
            _micStoryboard.Children.Add(micX);

            var micY = new DoubleAnimation
            {
                From = 1.0,
                To = 1.08,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = ease
            };
            Storyboard.SetTarget(micY, MicScale);
            Storyboard.SetTargetProperty(micY, "ScaleY");
            _micStoryboard.Children.Add(micY);

            _micStoryboard.Begin();
        }

        private async void MicButton_Click(object sender, RoutedEventArgs e)
        {
            // USA ListenOnceCommand
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
            _ringsStoryboard = null;
            _micStoryboard = null;

            MicButton.Click -= MicButton_Click;

            if (_viewModel != null)
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        private void StartRingsAnimation()
        {
            _ringsStoryboard?.Stop();
            _ringsStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

            AddRingAnimations(_ringsStoryboard, Ring1Scale, Ring1,
                beginMs: 0, durationMs: 1500,
                fromScale: 0.62, toScale: 1.18,
                fromOpacity: 0.18, toOpacity: 0.00);

            AddRingAnimations(_ringsStoryboard, Ring2Scale, Ring2,
                beginMs: 300, durationMs: 1500,
                fromScale: 0.56, toScale: 1.12,
                fromOpacity: 0.12, toOpacity: 0.00);

            AddRingAnimations(_ringsStoryboard, Ring3Scale, Ring3,
                beginMs: 600, durationMs: 1500,
                fromScale: 0.50, toScale: 1.06,
                fromOpacity: 0.08, toOpacity: 0.00);

            _ringsStoryboard.Begin();
        }

        private static void AddRingAnimations(
            Storyboard sb,
            ScaleTransform scale,
            UIElement ring,
            int beginMs,
            int durationMs,
            double fromScale,
            double toScale,
            double fromOpacity,
            double toOpacity)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseOut };

            var sx = new DoubleAnimation
            {
                From = fromScale,
                To = toScale,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                EasingFunction = ease
            };
            Storyboard.SetTarget(sx, scale);
            Storyboard.SetTargetProperty(sx, "ScaleX");
            sb.Children.Add(sx);

            var sy = new DoubleAnimation
            {
                From = fromScale,
                To = toScale,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                EasingFunction = ease
            };
            Storyboard.SetTarget(sy, scale);
            Storyboard.SetTargetProperty(sy, "ScaleY");
            sb.Children.Add(sy);

            var op = new DoubleAnimation
            {
                From = fromOpacity,
                To = toOpacity,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                EasingFunction = ease
            };
            Storyboard.SetTarget(op, ring);
            Storyboard.SetTargetProperty(op, "Opacity");
            sb.Children.Add(op);
        }
    }
}