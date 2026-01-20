using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using WinRT.Interop;

namespace Anfeta.UI.Views.Dialogs
{
    public sealed partial class FloatingMicButton : Window
    {
        private AppWindow? _appWindow;
        private Storyboard? _pulseAnimation;
        private bool _isExpanded;
        private DispatcherTimer? _collapseTimer;

        public event EventHandler? OpenAppRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler? VoiceActivationRequested;

        public FloatingMicButton()
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            InitializeWindow();
            StartIdlePulse();

            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _collapseTimer.Tick += (s, e) =>
            {
                _collapseTimer.Stop();
                Collapse();
            };
        }

        private void InitializeWindow()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow != null)
            {
                _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                _appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                _appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;

                _appWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);

                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                var workArea = displayArea.WorkArea;

                _appWindow.Move(new Windows.Graphics.PointInt32(
                    workArea.Width - 80,
                    workArea.Height / 2 - 28
                ));

                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                }

                _appWindow.Resize(new Windows.Graphics.SizeInt32(56, 56));
            }
        }

        private void StartIdlePulse()
        {
            _pulseAnimation?.Stop();
            _pulseAnimation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

            // Animación con KeyFrames para opacity
            var opacityAnim = new DoubleAnimationUsingKeyFrames();
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(800), Value = 0.2 });
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1200), Value = 0 });
            Storyboard.SetTarget(opacityAnim, PulseRing);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            _pulseAnimation.Children.Add(opacityAnim);

            var scaleX = new DoubleAnimation { From = 0.5, To = 11.0, Duration = TimeSpan.FromMilliseconds(1200) };
            Storyboard.SetTarget(scaleX, PulseScale);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");
            _pulseAnimation.Children.Add(scaleX);

            var scaleY = new DoubleAnimation { From = 0.5, To = 11.0, Duration = TimeSpan.FromMilliseconds(1200) };
            Storyboard.SetTarget(scaleY, PulseScale);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");
            _pulseAnimation.Children.Add(scaleY);

            _pulseAnimation.Begin();
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _collapseTimer?.Stop();
            Expand();
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _collapseTimer?.Start();
        }

        private void Expand()
        {
            if (_isExpanded) return;
            _isExpanded = true;

            _appWindow?.Resize(new Windows.Graphics.SizeInt32(200, 64));
            CompactView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
        }

        private void Collapse()
        {
            if (!_isExpanded) return;
            _isExpanded = false;

            _appWindow?.Resize(new Windows.Graphics.SizeInt32(64, 64));
            ExpandedView.Visibility = Visibility.Collapsed;
            CompactView.Visibility = Visibility.Visible;
        }

        public void SetListeningState(bool isListening)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    isListening ? Microsoft.UI.Colors.Red : Microsoft.UI.ColorHelper.FromArgb(255, 52, 211, 153)
                );

                StatusText.Text = isListening ? "Escuchando..." : "Listo";
            });
        }

        public void UpdateHotkeyDisplay(string hotkey)
        {
            DispatcherQueue.TryEnqueue(() => HotkeyDisplay.Text = hotkey);
        }

        private void CompactView_Click(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(CompactView).Properties.IsLeftButtonPressed)
                VoiceActivationRequested?.Invoke(this, EventArgs.Empty);
        }

        private void MicButton_Click(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
            {
                VoiceActivationRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void ExpandedView_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
                OpenAppRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OpenApp_Click(object sender, RoutedEventArgs e)
        {
            OpenAppRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}