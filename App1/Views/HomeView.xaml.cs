using Anfeta.UI.Services;
using Anfeta.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;

namespace Anfeta.UI.Views
{
    public sealed partial class HomeView : Page
    {
        private Storyboard? _ringsStoryboard;
        private Storyboard? _micStoryboard;
        private HomeViewModel? _viewModel;
        private AppStateService? _appState;

        private DispatcherTimer? _audioMeterTimer;
        private MMDeviceEnumerator? _mmEnumerator;
        private float _smoothedLevel = 0f;

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

            SetMicButtonState(false);
            UpdateAudioDeviceDisplay();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
            => Frame.Navigate(typeof(TroubleshootView));

        private void AppState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppStateService.InputDeviceName) ||
                e.PropertyName == nameof(AppStateService.OutputDeviceName))
                DispatcherQueue.TryEnqueue(UpdateAudioDeviceDisplay);
        }

        private void UpdateAudioDeviceDisplay()
        {
            if (_appState == null) return;
            TxtInputDevice.Text = $"Entrada: {_appState.InputDeviceName}";
            TxtOutputDevice.Text = $"Salida: {_appState.OutputDeviceName}";
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HomeViewModel.IsListening))
                DispatcherQueue.TryEnqueue(() => UpdateMicVisualState(_viewModel!.IsListening));

            if (e.PropertyName == nameof(HomeViewModel.InfoMessage) ||
                e.PropertyName == nameof(HomeViewModel.ShowInfo))
                DispatcherQueue.TryEnqueue(() =>
                    RenderNotifications(_viewModel!.InfoMessage, _viewModel!.ShowInfo));
        }

        // ─────────────────────────────────────────────────────────
        // NOTIFICACIONES — LISTA VERTICAL
        // ─────────────────────────────────────────────────────────

        private sealed record ActivityItem(
            string Titulo, string Horario, string Estado, string Prioridad);

        /// Decide si renderizar lista de actividades o mensaje simple.
        private void RenderNotifications(string message, bool showInfo)
        {
            NotificationsPanel.Children.Clear();

            if (!showInfo || string.IsNullOrWhiteSpace(message))
            {
                NotifStrip.Visibility = Visibility.Collapsed;
                NotifCountBadge.Visibility = Visibility.Collapsed;
                return;
            }

            NotifStrip.Visibility = Visibility.Visible;

            if (message.TrimStart().StartsWith("Actividades de", StringComparison.OrdinalIgnoreCase))
            {
                var items = ParseActivityList(message);
                RenderActivityList(items, message);
            }
            else
            {
                RenderSimpleCard(message);
            }
        }

        /// Parsea el texto plano en lista de ActivityItem.
        private static List<ActivityItem> ParseActivityList(string message)
        {
            var result = new List<ActivityItem>();
            var blocks = message.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in blocks)
            {
                var lines = block
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .ToArray();

                if (lines.Length == 0 ||
                    !lines[0].StartsWith("Actividad ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var titulo = lines.Length > 1 ? TrimDot(lines[1]) : "Sin título";
                var horario = ExtractField(lines, "Horario:");
                var estadoRaw = ExtractField(lines, "Estado:");

                string estado, prioridad;
                if (estadoRaw.Contains("Prioridad:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = estadoRaw.Split(new[] { "Prioridad:" }, StringSplitOptions.None);
                    estado = TrimDot(parts[0]).Trim();
                    prioridad = parts.Length > 1 ? TrimDot(parts[1]).Trim() : "";
                }
                else
                {
                    estado = TrimDot(estadoRaw);
                    prioridad = "";
                }

                result.Add(new ActivityItem(titulo, horario, estado, prioridad));
            }

            return result;
        }

        private static string ExtractField(string[] lines, string prefix)
        {
            foreach (var l in lines)
                if (l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return l.Substring(prefix.Length).Trim();
            return "";
        }

        private static string TrimDot(string s) => s.TrimEnd().TrimEnd('.').Trim();

        /// Renderiza las actividades como filas en lista vertical.
        private void RenderActivityList(List<ActivityItem> items, string rawMessage)
        {
            // Header resumen
            var firstLine = rawMessage.Split('\n')[0].Trim();
            NotificationsPanel.Children.Add(BuildSummaryRow(firstLine, items.Count));

            foreach (var item in items)
                NotificationsPanel.Children.Add(BuildActivityRow(item));

            NotifCountBadge.Visibility = Visibility.Visible;
            NotifCountText.Text = items.Count.ToString();
            NotifHeaderText.Text = "ACTIVIDADES";
            NotifDot.Fill = new SolidColorBrush(
                ColorHelper.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
        }

        /// Fila de resumen con ícono + texto total.
        private static Border BuildSummaryRow(string text, int count)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(
                    ColorHelper.FromArgb(0x14, 0x60, 0xA5, 0xFA)),
                BorderBrush = new SolidColorBrush(
                    ColorHelper.FromArgb(0x20, 0x60, 0xA5, 0xFA)),
                BorderThickness = new Thickness(1)
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon
            {
                Glyph = "\uE823",
                FontSize = 13,
                Foreground = new SolidColorBrush(
                    ColorHelper.FromArgb(0xFF, 0x60, 0xA5, 0xFA)),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(
                    ColorHelper.FromArgb(0xFF, 0x93, 0xC5, 0xFD)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            border.Child = row;
            return border;
        }

        /// Fila individual de actividad: título + horario + badges en una línea compacta.
        private static Border BuildActivityRow(ActivityItem item)
        {
            var (statusColor, statusBg) = GetStatusColors(item.Estado);

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(
                    ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(
                    ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            // Layout: [dot] [título + horario] → [badges]
            var outerRow = new Grid();
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            // Dot indicador de prioridad
            var (dotColor, _) = GetStatusColors(item.Estado);
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(dotColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(dot, 0);

            // Centro: título + horario apilados
            var center = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(new TextBlock
            {
                Text = item.Titulo,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(
                    ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (!string.IsNullOrWhiteSpace(item.Horario))
            {
                var horarioRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                horarioRow.Children.Add(new FontIcon
                {
                    Glyph = "\uE787",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(
                        ColorHelper.FromArgb(0xFF, 0x47, 0x55, 0x69)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                horarioRow.Children.Add(new TextBlock
                {
                    Text = item.Horario,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(
                        ColorHelper.FromArgb(0xFF, 0x4B, 0x55, 0x63)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                center.Children.Add(horarioRow);
            }
            Grid.SetColumn(center, 1);

            // Derecha: badges estado + prioridad
            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            if (!string.IsNullOrWhiteSpace(item.Estado))
                badges.Children.Add(BuildBadge(item.Estado, statusColor, statusBg));

            if (!string.IsNullOrWhiteSpace(item.Prioridad) &&
                !item.Prioridad.Equals("Sin prioridad", StringComparison.OrdinalIgnoreCase) &&
                !item.Prioridad.Equals("ACTIVIDAD", StringComparison.OrdinalIgnoreCase))
            {
                var (pc, pb) = GetPriorityColors(item.Prioridad);
                badges.Children.Add(BuildBadge(item.Prioridad, pc, pb));
            }
            Grid.SetColumn(badges, 2);

            outerRow.Children.Add(dot);
            outerRow.Children.Add(center);
            outerRow.Children.Add(badges);

            border.Child = outerRow;
            return border;
        }

        private static Border BuildBadge(string text, Color textColor, Color bgColor) =>
            new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 3, 8, 3),
                Background = new SolidColorBrush(bgColor),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(textColor)
                }
            };

        private static (Color text, Color bg) GetStatusColors(string estado)
        {
            var s = estado.ToUpperInvariant();
            if (s.Contains("HECHO") || s.Contains("COMPLETADO"))
                return (ColorHelper.FromArgb(0xFF, 0x34, 0xD3, 0x99),
                        ColorHelper.FromArgb(0x1A, 0x34, 0xD3, 0x99));
            if (s.Contains("POR HACER"))
                return (ColorHelper.FromArgb(0xFF, 0xFB, 0xBF, 0x24),
                        ColorHelper.FromArgb(0x1A, 0xFB, 0xBF, 0x24));
            if (s.Contains("ARRANCAR") || s.Contains("P. HACER"))
                return (ColorHelper.FromArgb(0xFF, 0xFF, 0x8A, 0x4A),
                        ColorHelper.FromArgb(0x1A, 0xFF, 0x6B, 0x35));
            if (s.Contains("EN CURSO") || s.Contains("PROGRESO"))
                return (ColorHelper.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                        ColorHelper.FromArgb(0x1A, 0x60, 0xA5, 0xFA));
            return (ColorHelper.FromArgb(0xFF, 0x94, 0xA3, 0xB8),
                    ColorHelper.FromArgb(0x14, 0x94, 0xA3, 0xB8));
        }

        private static (Color text, Color bg) GetPriorityColors(string prioridad) =>
            prioridad.ToUpperInvariant() switch
            {
                "ALTA" => (ColorHelper.FromArgb(0xFF, 0xF8, 0x71, 0x71),
                            ColorHelper.FromArgb(0x1A, 0xEF, 0x44, 0x44)),
                "MEDIA" => (ColorHelper.FromArgb(0xFF, 0xFB, 0xBF, 0x24),
                            ColorHelper.FromArgb(0x1A, 0xFB, 0xBF, 0x24)),
                "BAJA" => (ColorHelper.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                            ColorHelper.FromArgb(0x1A, 0x60, 0xA5, 0xFA)),
                _ => (ColorHelper.FromArgb(0xFF, 0x94, 0xA3, 0xB8),
                            ColorHelper.FromArgb(0x12, 0x94, 0xA3, 0xB8))
            };

        /// Card de mensaje simple para estados del sistema, errores, etc.
        private void RenderSimpleCard(string message)
        {
            NotifCountBadge.Visibility = Visibility.Collapsed;
            NotifHeaderText.Text = "NOTIFICACIONES";

            var isError = message.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("no pude", StringComparison.OrdinalIgnoreCase);

            Color dotColor, bgColor, borderColor, textColor;
            if (isError)
            {
                dotColor = ColorHelper.FromArgb(0xFF, 0xEF, 0x44, 0x44);
                bgColor = ColorHelper.FromArgb(0x14, 0xEF, 0x44, 0x44);
                borderColor = ColorHelper.FromArgb(0x25, 0xEF, 0x44, 0x44);
                textColor = ColorHelper.FromArgb(0xFF, 0xFC, 0xA5, 0xA5);
            }
            else
            {
                dotColor = ColorHelper.FromArgb(0xFF, 0x34, 0xD3, 0x99);
                bgColor = ColorHelper.FromArgb(0x12, 0x34, 0xD3, 0x99);
                borderColor = ColorHelper.FromArgb(0x20, 0x34, 0xD3, 0x99);
                textColor = ColorHelper.FromArgb(0xFF, 0xA7, 0xF3, 0xD0);
            }

            NotifDot.Fill = new SolidColorBrush(dotColor);

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Background = new SolidColorBrush(bgColor),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel { Spacing = 8 };
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerRow.Children.Add(new FontIcon
            {
                Glyph = isError ? "\uE7BA" : "\uE946",
                FontSize = 12,
                Foreground = new SolidColorBrush(dotColor),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = "Sistema",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(dotColor),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(headerRow);
            sp.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = new SolidColorBrush(textColor),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            });

            border.Child = sp;
            NotificationsPanel.Children.Add(border);
        }

        // ─────────────────────────────────────────────────────────
        // ESTADO VISUAL DEL MICRÓFONO
        // ─────────────────────────────────────────────────────────

        private void UpdateMicVisualState(bool isListening)
        {
            SetMicButtonState(isListening);
            if (isListening) { StartRingsAnimation(); StartListeningAnimation(); StartAudioMeter(); }
            else { StopAllAnimations(); StopAudioMeter(); ResetRingsToIdle(); }
        }

        private void SetMicButtonState(bool isListening)
        {
            Color bgColor, hoverColor, pressColor;
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

            if (MicButton.Resources["ButtonBackground"] is SolidColorBrush bg)
                bg.Color = bgColor;
            if (MicButton.Resources["ButtonBackgroundPointerOver"] is SolidColorBrush hover)
                hover.Color = hoverColor;
            if (MicButton.Resources["ButtonBackgroundPressed"] is SolidColorBrush press)
                press.Color = pressColor;

            MicButton.Opacity = opacity;
        }

        private void StopAllAnimations() { _ringsStoryboard?.Stop(); _micStoryboard?.Stop(); }

        private void ResetRingsToIdle()
        {
            var sb = new Storyboard();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            void AddReset(ScaleTransform scale, UIElement ring, double ts, double to)
            {
                var sx = new DoubleAnimation { To = ts, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
                Storyboard.SetTarget(sx, scale); Storyboard.SetTargetProperty(sx, "ScaleX"); sb.Children.Add(sx);
                var sy = new DoubleAnimation { To = ts, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
                Storyboard.SetTarget(sy, scale); Storyboard.SetTargetProperty(sy, "ScaleY"); sb.Children.Add(sy);
                var op = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
                Storyboard.SetTarget(op, ring); Storyboard.SetTargetProperty(op, "Opacity"); sb.Children.Add(op);
            }

            AddReset(Ring1Scale, Ring1, 0.70, 0.18);
            AddReset(Ring2Scale, Ring2, 0.62, 0.12);
            AddReset(Ring3Scale, Ring3, 0.55, 0.08);

            var rx = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = ease };
            Storyboard.SetTarget(rx, MicScale); Storyboard.SetTargetProperty(rx, "ScaleX"); sb.Children.Add(rx);
            var ry = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = ease };
            Storyboard.SetTarget(ry, MicScale); Storyboard.SetTargetProperty(ry, "ScaleY"); sb.Children.Add(ry);
            sb.Begin();
        }

        // ─────────────────────────────────────────────────────────
        // AUDIO METER
        // ─────────────────────────────────────────────────────────

        private void StartAudioMeter()
        {
            StopAudioMeter();
            _smoothedLevel = 0f;
            _audioMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _audioMeterTimer.Tick += AudioMeterTimer_Tick;
            _audioMeterTimer.Start();
        }

        private void StopAudioMeter()
        {
            if (_audioMeterTimer == null) return;
            _audioMeterTimer.Stop();
            _audioMeterTimer.Tick -= AudioMeterTimer_Tick;
            _audioMeterTimer = null;
        }

        private void AudioMeterTimer_Tick(object? sender, object e)
        {
            try
            {
                float peak = 0f;
                var device = _mmEnumerator?.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                if (device != null) peak = device.AudioMeterInformation.MasterPeakValue;
                _smoothedLevel = (_smoothedLevel * 0.55f) + (peak * 0.45f);
                ApplyAudioLevelToRings(_smoothedLevel);
            }
            catch { }
        }

        private void ApplyAudioLevelToRings(float level)
        {
            float a = Math.Min(1.0f, level * 3.5f);
            Ring1Scale.ScaleX = 0.70 + (a * 0.50); Ring1Scale.ScaleY = Ring1Scale.ScaleX; Ring1.Opacity = 0.18 + (a * 0.30);
            Ring2Scale.ScaleX = 0.62 + (a * 0.38); Ring2Scale.ScaleY = Ring2Scale.ScaleX; Ring2.Opacity = 0.12 + (a * 0.25);
            Ring3Scale.ScaleX = 0.55 + (a * 0.28); Ring3Scale.ScaleY = Ring3Scale.ScaleX; Ring3.Opacity = 0.08 + (a * 0.20);
            MicGlowScale.ScaleX = 1.0 + (a * 0.30); MicGlowScale.ScaleY = MicGlowScale.ScaleX; MicGlow.Opacity = 0.18 + (a * 0.40);
            MicScale.ScaleX = 1.0 + (a * 0.06); MicScale.ScaleY = MicScale.ScaleX;
        }

        private void StartListeningAnimation()
        {
            _micStoryboard?.Stop();
            _micStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
            var ease = new CircleEase { EasingMode = EasingMode.EaseInOut };
            var mx = new DoubleAnimation { From = 1.0, To = 1.04, Duration = TimeSpan.FromMilliseconds(600), EasingFunction = ease };
            Storyboard.SetTarget(mx, MicScale); Storyboard.SetTargetProperty(mx, "ScaleX"); _micStoryboard.Children.Add(mx);
            var my = new DoubleAnimation { From = 1.0, To = 1.04, Duration = TimeSpan.FromMilliseconds(600), EasingFunction = ease };
            Storyboard.SetTarget(my, MicScale); Storyboard.SetTargetProperty(my, "ScaleY"); _micStoryboard.Children.Add(my);
            _micStoryboard.Begin();
        }

        private async void MicButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null) await _viewModel.ListenOnceCommand.ExecuteAsync(null);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null) _ = _viewModel.InitializeSpeechCommand.ExecuteAsync(null);
            if (_viewModel != null) _viewModel.PropertyChanged += ViewModel_ModelReadyChanged;
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