// FloatingMicButton.xaml.cs - COMPLETO con gestión robusta
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Anfeta.UI.Views.Dialogs
{
    public sealed partial class FloatingMicButton : Window
    {
        private AppWindow? _appWindow;
        private Storyboard? _pulseAnimation;
        private bool _isExpanded;
        private DispatcherTimer? _collapseTimer;
        private bool _isClosing;

        public event EventHandler? OpenAppRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler? VoiceActivationRequested;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public FloatingMicButton()
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            InitializeWindow();
            StartIdlePulse();

            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _collapseTimer.Tick += CollapseTimer_Tick;

            this.Closed += FloatingMicButton_Closed;
        }

        private void InitializeWindow()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            if (_appWindow != null)
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);

                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                var workArea = displayArea.WorkArea;

                _appWindow.Move(new Windows.Graphics.PointInt32(
                    (workArea.Width / 2) - 50,
                    workArea.Height - 150
                ));

                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                }

                _appWindow.Resize(new Windows.Graphics.SizeInt32(100, 100));
            }
        }

        private void StartIdlePulse()
        {
            _pulseAnimation?.Stop();
            _pulseAnimation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

            var opacityAnim = new DoubleAnimation
            {
                From = 0,
                To = 0.2,
                Duration = TimeSpan.FromMilliseconds(1200),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(opacityAnim, PulseRing);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            _pulseAnimation.Children.Add(opacityAnim);

            var scaleX = new DoubleAnimation
            {
                From = 0.5,
                To = 1.1,
                Duration = TimeSpan.FromMilliseconds(1200),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(scaleX, PulseScale);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");
            _pulseAnimation.Children.Add(scaleX);

            var scaleY = new DoubleAnimation
            {
                From = 0.5,
                To = 1.1,
                Duration = TimeSpan.FromMilliseconds(1200),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(scaleY, PulseScale);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");
            _pulseAnimation.Children.Add(scaleY);

            _pulseAnimation.Begin();
        }

        private void CollapseTimer_Tick(object? sender, object e)
        {
            _collapseTimer?.Stop();
            Collapse();
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_isClosing) return;
            _collapseTimer?.Stop();
            Expand();
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_isClosing) return;
            _collapseTimer?.Start();
        }

        private void Expand()
        {
            if (_isExpanded || _isClosing) return;
            _isExpanded = true;

            try
            {
                _appWindow?.Resize(new Windows.Graphics.SizeInt32(280, 100));
                CompactView.Visibility = Visibility.Collapsed;
                ExpandedView.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void Collapse()
        {
            if (!_isExpanded || _isClosing) return;
            _isExpanded = false;

            try
            {
                _appWindow?.Resize(new Windows.Graphics.SizeInt32(100, 100));
                ExpandedView.Visibility = Visibility.Collapsed;
                CompactView.Visibility = Visibility.Visible;
            }
            catch { }
        }

        public void SetListeningState(bool isListening)
        {
            if (_isClosing) return;

            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        isListening ? Microsoft.UI.Colors.Red : Microsoft.UI.ColorHelper.FromArgb(255, 52, 211, 153)
                    );

                    StatusText.Text = isListening ? "Escuchando..." : "Listo";
                });
            }
            catch { }
        }

        public void UpdateHotkeyDisplay(string hotkey)
        {
            if (_isClosing || string.IsNullOrWhiteSpace(hotkey)) return;

            try
            {
                DispatcherQueue.TryEnqueue(() => HotkeyDisplay.Text = hotkey);
            }
            catch { }
        }

        private void CompactView_Click(object sender, PointerRoutedEventArgs e)
        {
            if (_isClosing) return;

            var point = e.GetCurrentPoint(CompactView);
            if (point.Properties.IsLeftButtonPressed)
            {
                VoiceActivationRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void MicButton_Click(object sender, PointerRoutedEventArgs e)
        {
            if (_isClosing) return;

            var point = e.GetCurrentPoint(sender as UIElement);
            if (point.Properties.IsLeftButtonPressed)
            {
                VoiceActivationRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void OpenAppButton_Click(object sender, PointerRoutedEventArgs e)
        {
            if (_isClosing) return;

            var point = e.GetCurrentPoint(sender as UIElement);
            if (point.Properties.IsLeftButtonPressed)
            {
                OpenAppRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void OpenApp_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            OpenAppRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        private void FloatingMicButton_Closed(object sender, WindowEventArgs args)
        {
            _isClosing = true;
            Cleanup();
        }

        private void Cleanup()
        {
            try
            {
                _collapseTimer?.Stop();
                _collapseTimer = null;

                _pulseAnimation?.Stop();
                _pulseAnimation = null;

                OpenAppRequested = null;
                ExitRequested = null;
                VoiceActivationRequested = null;
            }
            catch { }
        }
    }
}