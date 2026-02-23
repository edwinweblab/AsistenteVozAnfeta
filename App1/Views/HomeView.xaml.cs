using Anfeta.UI.Services;
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NAudio.CoreAudioApi;
using System;
using Windows.UI;

namespace Anfeta.UI.Views
{
    public sealed partial class HomeView : Page
    {
        private Storyboard? _ringsStoryboard;
        private Storyboard? _micStoryboard;
        private HomeViewModel? _viewModel;
        private AppStateService? _appState;

        // Audio meter para animación reactiva
        private DispatcherTimer? _audioMeterTimer;
        private MMDeviceEnumerator? _mmEnumerator;
        private float _smoothedLevel = 0f;

        // Colores del botón
        private static readonly Color ColorOff = Color.FromArgb(255, 55, 65, 81);   // gris oscuro apagado
        private static readonly Color ColorReady = Color.FromArgb(255, 255, 107, 53);   // acento normal
        private static readonly Color ColorActive = Color.FromArgb(255, 255, 60, 60);   // rojo escuchando

        public HomeView()
        {
            InitializeComponent();

            _viewModel = App.AppHost.Services.GetRequiredService<HomeViewModel>();
            _appState = App.AppHost.Services.GetRequiredService<AppStateService>();

            DataContext = _viewModel;

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _appState.PropertyChanged += AppState_PropertyChanged;

            MicButton.Click += MicButton_Click;
            HelpButton.Click += HelpButton_Click;

            _mmEnumerator = new MMDeviceEnumerator();

            // Estado inicial: apagado
            SetMicButtonState(false);
            UpdateAudioDeviceDisplay();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(TroubleshootView));
        }

        private void AppState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppStateService.InputDeviceName) ||
                e.PropertyName == nameof(AppStateService.OutputDeviceName))
            {
                DispatcherQueue.TryEnqueue(UpdateAudioDeviceDisplay);
            }
        }

        private void UpdateAudioDeviceDisplay()
        {
            if (_appState != null)
            {
                TxtInputDevice.Text = $"Entrada: {_appState.InputDeviceName}";
                TxtOutputDevice.Text = $"Salida: {_appState.OutputDeviceName}";
            }
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HomeViewModel.IsListening))
                DispatcherQueue.TryEnqueue(() => UpdateMicVisualState(_viewModel!.IsListening));
        }

        /// Actualiza colores, animaciones e indicador visual según estado de escucha.
        private void UpdateMicVisualState(bool isListening)
        {
            SetMicButtonState(isListening);

            if (isListening)
            {
                StartRingsAnimation();
                StartListeningAnimation();
                StartAudioMeter();
            }
            else
            {
                StopAllAnimations();
                StopAudioMeter();
                ResetRingsToIdle();
            }
        }

        /// Aplica color al botón vía ButtonBackground resource (WinUI 3 no respeta Background directo).
        /// Sin inicializar/modelo no listo: gris opaco | Listo: acento | Escuchando: rojo
        private void SetMicButtonState(bool isListening)
        {
            Color bgColor;
            Color hoverColor;
            Color pressColor;
            double opacity;

            if (isListening)
            {
                bgColor = Color.FromArgb(255, 220, 50, 50);
                hoverColor = Color.FromArgb(255, 240, 70, 70);
                pressColor = Color.FromArgb(255, 190, 30, 30);
                opacity = 1.0;
                MicGlow.Opacity = 0.35;
            }
            else
            {
                bool modelReady = _viewModel?.IsModelReady ?? false;

                if (modelReady)
                {
                    bgColor = Color.FromArgb(255, 255, 107, 53);
                    hoverColor = Color.FromArgb(255, 255, 138, 90);
                    pressColor = Color.FromArgb(255, 220, 80, 30);
                    opacity = 1.0;
                }
                else
                {
                    bgColor = Color.FromArgb(255, 55, 65, 81);
                    hoverColor = Color.FromArgb(255, 71, 82, 99);
                    pressColor = Color.FromArgb(255, 40, 48, 60);
                    opacity = 0.5;
                }

                MicGlow.Opacity = 0.08;
            }

            // WinUI 3: actualizar los resource keys del botón, no la propiedad Background
            if (MicButton.Resources["ButtonBackground"] is SolidColorBrush bg)
                bg.Color = bgColor;
            if (MicButton.Resources["ButtonBackgroundPointerOver"] is SolidColorBrush hover)
                hover.Color = hoverColor;
            if (MicButton.Resources["ButtonBackgroundPressed"] is SolidColorBrush press)
                press.Color = pressColor;

            MicButton.Opacity = opacity;
        }

        private void StopAllAnimations()
        {
            _ringsStoryboard?.Stop();
            _micStoryboard?.Stop();
        }

        /// Resetea los anillos a su estado en reposo suavemente.
        private void ResetRingsToIdle()
        {
            var sb = new Storyboard();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            void AddReset(ScaleTransform scale, UIElement ring, double targetScale, double targetOpacity)
            {
                var sx = new DoubleAnimation { To = targetScale, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
                Storyboard.SetTarget(sx, scale); Storyboard.SetTargetProperty(sx, "ScaleX"); sb.Children.Add(sx);

                var sy = new DoubleAnimation { To = targetScale, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
                Storyboard.SetTarget(sy, scale); Storyboard.SetTargetProperty(sy, "ScaleY"); sb.Children.Add(sy);

                var op = new DoubleAnimation { To = targetOpacity, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
                Storyboard.SetTarget(op, ring); Storyboard.SetTargetProperty(op, "Opacity"); sb.Children.Add(op);
            }

            AddReset(Ring1Scale, Ring1, 0.70, 0.18);
            AddReset(Ring2Scale, Ring2, 0.62, 0.12);
            AddReset(Ring3Scale, Ring3, 0.55, 0.08);

            var micReset = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = ease };
            Storyboard.SetTarget(micReset, MicScale); Storyboard.SetTargetProperty(micReset, "ScaleX"); sb.Children.Add(micReset);
            var micResetY = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = ease };
            Storyboard.SetTarget(micResetY, MicScale); Storyboard.SetTargetProperty(micResetY, "ScaleY"); sb.Children.Add(micResetY);

            sb.Begin();
        }

        // ═══════════════════════════════════════════
        // Audio meter reactivo (no abre el device exclusivamente)
        // ═══════════════════════════════════════════

        /// Inicia lectura del nivel de audio del dispositivo predeterminado de Windows.
        /// Usa MMDevice.AudioMeterInformation (no exclusivo, compatible con SpeechRecognizer).
        private void StartAudioMeter()
        {
            StopAudioMeter();
            _smoothedLevel = 0f;

            _audioMeterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(40) // ~25fps
            };

            _audioMeterTimer.Tick += AudioMeterTimer_Tick;
            _audioMeterTimer.Start();
        }

        private void StopAudioMeter()
        {
            if (_audioMeterTimer != null)
            {
                _audioMeterTimer.Stop();
                _audioMeterTimer.Tick -= AudioMeterTimer_Tick;
                _audioMeterTimer = null;
            }
        }

        /// Lee el nivel de audio y actualiza las animaciones de los anillos reactivamente.
        private void AudioMeterTimer_Tick(object? sender, object e)
        {
            try
            {
                float peak = 0f;

                var device = _mmEnumerator?.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                if (device != null)
                    peak = device.AudioMeterInformation.MasterPeakValue;

                // Suavizado exponencial para evitar saltos bruscos
                _smoothedLevel = (_smoothedLevel * 0.55f) + (peak * 0.45f);

                ApplyAudioLevelToRings(_smoothedLevel);
            }
            catch
            {
                // Silencioso — el device puede cambiar durante la escucha
            }
        }

        /// Escala los anillos y el glow proporcionalmente al nivel de audio detectado.
        /// level: valor entre 0.0 y 1.0
        private void ApplyAudioLevelToRings(float level)
        {
            // Amplificar para que niveles bajos sean visibles
            float amplified = Math.Min(1.0f, level * 3.5f);

            Ring1Scale.ScaleX = 0.70 + (amplified * 0.50);
            Ring1Scale.ScaleY = Ring1Scale.ScaleX;
            Ring1.Opacity = 0.18 + (amplified * 0.30);

            Ring2Scale.ScaleX = 0.62 + (amplified * 0.38);
            Ring2Scale.ScaleY = Ring2Scale.ScaleX;
            Ring2.Opacity = 0.12 + (amplified * 0.25);

            Ring3Scale.ScaleX = 0.55 + (amplified * 0.28);
            Ring3Scale.ScaleY = Ring3Scale.ScaleX;
            Ring3.Opacity = 0.08 + (amplified * 0.20);

            MicGlowScale.ScaleX = 1.0 + (amplified * 0.30);
            MicGlowScale.ScaleY = MicGlowScale.ScaleX;
            MicGlow.Opacity = 0.18 + (amplified * 0.40);

            // Escala del botón: pulsación sutil
            MicScale.ScaleX = 1.0 + (amplified * 0.06);
            MicScale.ScaleY = MicScale.ScaleX;
        }

        private void StartListeningAnimation()
        {
            // Con audio meter activo esta animación es solo fallback
            // si el nivel de audio es 0 (silencio total)
            _micStoryboard?.Stop();
            _micStoryboard = new Storyboard
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true
            };

            var ease = new CircleEase { EasingMode = EasingMode.EaseInOut };

            var micX = new DoubleAnimation { From = 1.0, To = 1.04, Duration = TimeSpan.FromMilliseconds(600), EasingFunction = ease };
            Storyboard.SetTarget(micX, MicScale); Storyboard.SetTargetProperty(micX, "ScaleX");
            _micStoryboard.Children.Add(micX);

            var micY = new DoubleAnimation { From = 1.0, To = 1.04, Duration = TimeSpan.FromMilliseconds(600), EasingFunction = ease };
            Storyboard.SetTarget(micY, MicScale); Storyboard.SetTargetProperty(micY, "ScaleY");
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

            // Suscribir a IsModelReady para actualizar color del botón cuando cargue
            if (_viewModel != null)
                _viewModel.PropertyChanged += ViewModel_ModelReadyChanged;
        }

        private void ViewModel_ModelReadyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HomeViewModel.IsModelReady))
                DispatcherQueue.TryEnqueue(() => SetMicButtonState(false));
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            StopAllAnimations();
            StopAudioMeter();

            MicButton.Click -= MicButton_Click;
            HelpButton.Click -= HelpButton_Click;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.PropertyChanged -= ViewModel_ModelReadyChanged;
            }

            if (_appState != null)
                _appState.PropertyChanged -= AppState_PropertyChanged;

            try { _mmEnumerator?.Dispose(); } catch { }
            _mmEnumerator = null;

            _ringsStoryboard = null;
            _micStoryboard = null;
        }

        private void StartRingsAnimation()
        {
            // Con audio meter activo, este storyboard solo se usa como idle fallback
            _ringsStoryboard?.Stop();
            _ringsStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

            AddRingAnimations(_ringsStoryboard, Ring1Scale, Ring1, 0, 1200, 0.70, 0.90, 0.18, 0.05);
            AddRingAnimations(_ringsStoryboard, Ring2Scale, Ring2, 200, 1200, 0.62, 0.82, 0.12, 0.03);
            AddRingAnimations(_ringsStoryboard, Ring3Scale, Ring3, 400, 1200, 0.55, 0.72, 0.08, 0.01);

            _ringsStoryboard.Begin();
        }

        private static void AddRingAnimations(Storyboard sb, ScaleTransform scale, UIElement ring,
            int beginMs, int durationMs, double fromScale, double toScale, double fromOpacity, double toOpacity)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseOut };

            var sx = new DoubleAnimation { From = fromScale, To = toScale, Duration = TimeSpan.FromMilliseconds(durationMs), BeginTime = TimeSpan.FromMilliseconds(beginMs), EasingFunction = ease };
            Storyboard.SetTarget(sx, scale); Storyboard.SetTargetProperty(sx, "ScaleX"); sb.Children.Add(sx);

            var sy = new DoubleAnimation { From = fromScale, To = toScale, Duration = TimeSpan.FromMilliseconds(durationMs), BeginTime = TimeSpan.FromMilliseconds(beginMs), EasingFunction = ease };
            Storyboard.SetTarget(sy, scale); Storyboard.SetTargetProperty(sy, "ScaleY"); sb.Children.Add(sy);

            var op = new DoubleAnimation { From = fromOpacity, To = toOpacity, Duration = TimeSpan.FromMilliseconds(durationMs), BeginTime = TimeSpan.FromMilliseconds(beginMs), EasingFunction = ease };
            Storyboard.SetTarget(op, ring); Storyboard.SetTargetProperty(op, "Opacity"); sb.Children.Add(op);
        }
    }
}