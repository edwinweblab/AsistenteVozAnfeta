using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Anfeta.UI.Views
{
    public sealed partial class HomeView : Page
    {
        private Storyboard? _ringsStoryboard;
        private Storyboard? _micStoryboard;

        public HomeView()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            StartRingsAnimation();
            StartMicBreathing();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _ringsStoryboard?.Stop();
            _ringsStoryboard = null;

            _micStoryboard?.Stop();
            _micStoryboard = null;
        }

        private void StartRingsAnimation()
        {
            _ringsStoryboard?.Stop();

            _ringsStoryboard = new Storyboard
            {
                RepeatBehavior = RepeatBehavior.Forever
            };

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

        private void StartMicBreathing()
        {
            _micStoryboard?.Stop();

            _micStoryboard = new Storyboard
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true
            };

            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            // Mic scale
            var micX = new DoubleAnimation
            {
                From = 1.0,
                To = 1.035,
                Duration = TimeSpan.FromMilliseconds(900),
                EasingFunction = ease
            };
            Storyboard.SetTarget(micX, MicScale);
            Storyboard.SetTargetProperty(micX, "ScaleX");
            _micStoryboard.Children.Add(micX);

            var micY = new DoubleAnimation
            {
                From = 1.0,
                To = 1.035,
                Duration = TimeSpan.FromMilliseconds(900),
                EasingFunction = ease
            };
            Storyboard.SetTarget(micY, MicScale);
            Storyboard.SetTargetProperty(micY, "ScaleY");
            _micStoryboard.Children.Add(micY);

            // Glow scale + opacity
            var glowX = new DoubleAnimation
            {
                From = 1.0,
                To = 1.10,
                Duration = TimeSpan.FromMilliseconds(900),
                EasingFunction = ease
            };
            Storyboard.SetTarget(glowX, MicGlowScale);
            Storyboard.SetTargetProperty(glowX, "ScaleX");
            _micStoryboard.Children.Add(glowX);

            var glowY = new DoubleAnimation
            {
                From = 1.0,
                To = 1.10,
                Duration = TimeSpan.FromMilliseconds(900),
                EasingFunction = ease
            };
            Storyboard.SetTarget(glowY, MicGlowScale);
            Storyboard.SetTargetProperty(glowY, "ScaleY");
            _micStoryboard.Children.Add(glowY);

            var glowOp = new DoubleAnimation
            {
                From = 0.14,
                To = 0.22,
                Duration = TimeSpan.FromMilliseconds(900),
                EasingFunction = ease
            };
            Storyboard.SetTarget(glowOp, MicGlow);
            Storyboard.SetTargetProperty(glowOp, "Opacity");
            _micStoryboard.Children.Add(glowOp);

            _micStoryboard.Begin();
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
