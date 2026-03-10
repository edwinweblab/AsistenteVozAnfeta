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

        // ── Records por tipo de recurso ────────────────────────────────────────────
        private sealed record ActivityItem(string Titulo, string Horario, string Estado, string Prioridad);
        private sealed record RecordatorioItem(string Mensaje, string Fecha, string Estado, bool TieneCalendar);
        private sealed record RevisionItem(string Titulo, string Hora);
        private sealed record DrillDownRevisionItem(int Index, string Titulo);
        private sealed record ComprobatoriaSeccion(string Label, bool Ok, string Detalle);
        private sealed record RezagadaItem(string Titulo, string Desde);
        private sealed record UltimaAccionItem(string Actor, string Accion, string Titulo);

        private sealed record RevisionesResumen(string Nombre, string Fecha, int Total, int Terminadas, int Confirmadas, int Pendientes);

        // ── Tipo de mensaje detectado para routing y paleta ────────────────────────
        private enum NotifType
        {
            Actividades, Recordatorios, Revisiones, ReporteResumen,
            DrillDownRevisiones, Comprobatoria, Rezagadas, UltimasAcciones,
            Pregunta, Error, Sistema
        }

        // ── Paleta oscura por tipo — acento, fondo (7%), borde (9%), glyph ──────────
        // Static para que los builders de fila puedan llamarla sin instancia.
        private static (Color accent, Color bg, Color border, string glyph) GetTypeStyle(NotifType type) =>
            type switch
            {
                NotifType.Actividades => (ColorHelper.FromArgb(0xFF, 0x4A, 0x7F, 0xA5),
                                            ColorHelper.FromArgb(0x12, 0x4A, 0x7F, 0xA5),
                                            ColorHelper.FromArgb(0x18, 0x4A, 0x7F, 0xA5),
                                            "\uE823"),
                NotifType.Recordatorios => (ColorHelper.FromArgb(0xFF, 0xA5, 0x8B, 0x4A),
                                            ColorHelper.FromArgb(0x12, 0xA5, 0x8B, 0x4A),
                                            ColorHelper.FromArgb(0x18, 0xA5, 0x8B, 0x4A),
                                            "\uE787"),
                NotifType.Revisiones => (ColorHelper.FromArgb(0xFF, 0x7A, 0x6F, 0xA5),
                                            ColorHelper.FromArgb(0x12, 0x7A, 0x6F, 0xA5),
                                            ColorHelper.FromArgb(0x18, 0x7A, 0x6F, 0xA5),
                                            "\uE946"),
                NotifType.Pregunta => (ColorHelper.FromArgb(0xFF, 0xA5, 0x72, 0x4A),
                                            ColorHelper.FromArgb(0x12, 0xA5, 0x72, 0x4A),
                                            ColorHelper.FromArgb(0x18, 0xA5, 0x72, 0x4A),
                                            "\uE946"),
                NotifType.Error => (ColorHelper.FromArgb(0xFF, 0xA5, 0x4A, 0x4A),
                                            ColorHelper.FromArgb(0x14, 0xA5, 0x4A, 0x4A),
                                            ColorHelper.FromArgb(0x20, 0xA5, 0x4A, 0x4A),
                                            "\uEA39"),
                NotifType.ReporteResumen => (ColorHelper.FromArgb(0xFF, 0x7A, 0x6F, 0xA5),
                                             ColorHelper.FromArgb(0x12, 0x7A, 0x6F, 0xA5),
                                             ColorHelper.FromArgb(0x18, 0x7A, 0x6F, 0xA5),
                                             "\uE9D9"),
                NotifType.DrillDownRevisiones => (ColorHelper.FromArgb(0xFF, 0x7A, 0x6F, 0xA5),
                                           ColorHelper.FromArgb(0x12, 0x7A, 0x6F, 0xA5),
                                           ColorHelper.FromArgb(0x18, 0x7A, 0x6F, 0xA5),
                                           "\uE946"),
                NotifType.Comprobatoria => (ColorHelper.FromArgb(0xFF, 0x4A, 0x9A, 0x8A),
                                                   ColorHelper.FromArgb(0x12, 0x4A, 0x9A, 0x8A),
                                                   ColorHelper.FromArgb(0x18, 0x4A, 0x9A, 0x8A),
                                                   "\uE930"),
                NotifType.Rezagadas => (ColorHelper.FromArgb(0xFF, 0xA5, 0x4A, 0x4A),
                                                   ColorHelper.FromArgb(0x12, 0xA5, 0x4A, 0x4A),
                                                   ColorHelper.FromArgb(0x18, 0xA5, 0x4A, 0x4A),
                                                   "\uE7BA"),
                NotifType.UltimasAcciones => (ColorHelper.FromArgb(0xFF, 0x6A, 0x7A, 0x9A),
                                                   ColorHelper.FromArgb(0x12, 0x6A, 0x7A, 0x9A),
                                                   ColorHelper.FromArgb(0x18, 0x6A, 0x7A, 0x9A),
                                                   "\uE823"),
                _ => (ColorHelper.FromArgb(0xFF, 0x34, 0xD3, 0x99),
                                            ColorHelper.FromArgb(0x12, 0x34, 0xD3, 0x99),
                                            ColorHelper.FromArgb(0x18, 0x34, 0xD3, 0x99),
                                            "\uE946"),
            };

        // Actualiza dot, label y badge del header strip.
        private void SetNotifHeader(NotifType type, int count = -1)
        {
            var (accent, _, _, _) = GetTypeStyle(type);
            NotifDot.Fill = new SolidColorBrush(accent);
            NotifHeaderText.Text = type switch
            {
                NotifType.Actividades => "ACTIVIDADES",
                NotifType.Recordatorios => "RECORDATORIOS",
                NotifType.Revisiones => "REVISIONES",
                NotifType.ReporteResumen => "REVISIONES",
                NotifType.DrillDownRevisiones => "REVISIONES",
                NotifType.Comprobatoria => "COMPROBATORIA",
                NotifType.Rezagadas => "REZAGADAS",
                NotifType.UltimasAcciones => "ACCIONES",
                NotifType.Pregunta => "ACCION",
                NotifType.Error => "SISTEMA",
                _ => "SISTEMA"
            };

            if (count >= 0)
            {
                NotifCountBadge.Visibility = Visibility.Visible;
                NotifCountText.Text = count.ToString();
            }
            else
            {
                NotifCountBadge.Visibility = Visibility.Collapsed;
            }
        }

        // Punto de entrada — limpia el panel y decide qué renderizar.
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
            var type = DetectType(message);

            switch (type)
            {
                case NotifType.Actividades:
                    var actItems = ParseActivityList(message);
                    if (actItems.Count > 0) RenderActivityList(actItems, message);
                    else RenderSimpleCard(message, NotifType.Actividades);
                    break;

                case NotifType.Recordatorios:
                    var recItems = ParseRecordatorioList(message);
                    if (recItems.Count > 0) RenderRecordatorioList(recItems, message);
                    else RenderSimpleCard(message, NotifType.Recordatorios);
                    break;

                case NotifType.Revisiones:
                    var revItems = ParseRevisionesList(message);
                    if (revItems.Count > 0) RenderRevisionesList(revItems, message);
                    else RenderSimpleCard(message, NotifType.Revisiones);
                    break;

                case NotifType.ReporteResumen:
                    var resumen = ParseRevisionesResumen(message);
                    if (resumen != null) RenderRevisionesResumen(resumen);
                    else RenderSimpleCard(message, NotifType.ReporteResumen);
                    break;

                case NotifType.DrillDownRevisiones:
                    var drillItems = ParseDrillDownList(message);
                    if (drillItems.items.Count > 0)
                        RenderDrillDownList(drillItems.header, drillItems.items);
                    else
                        RenderSimpleCard(message, NotifType.DrillDownRevisiones);
                    break;

                case NotifType.Comprobatoria:
                    var compItems = ParseComprobatoria(message);
                    if (compItems.nombre != null)
                        RenderComprobatoriaCard(compItems.nombre, compItems.secciones);
                    else
                        RenderSimpleCard(message, NotifType.Comprobatoria);
                    break;

                case NotifType.Rezagadas:
                    var rezData = ParseRezagadas(message);
                    if (rezData.items.Count > 0)
                        RenderRezagadasList(rezData.header, rezData.items);
                    else
                        RenderSimpleCard(message, NotifType.Rezagadas);
                    break;

                case NotifType.UltimasAcciones:
                    var accionItems = ParseUltimasAcciones(message);
                    if (accionItems.items.Count > 0)
                        RenderUltimasAccionesList(accionItems.header, accionItems.items);
                    else
                        RenderSimpleCard(message, NotifType.UltimasAcciones);
                    break;

                case NotifType.Pregunta:
                    RenderPreguntaCard(message);
                    break;

                default:
                    RenderSimpleCard(message, type);
                    break;
            }
        }

        // Detecta tipo por prefijo de la primera línea.
        // Sin accent-insensitive — los builders usan keywords sin tilde.
        private static NotifType DetectType(string message)
        {
            var first = message.Split('\n')[0].Trim().ToUpperInvariant();
            var upper = message.ToUpperInvariant();

            if (first.StartsWith("ACTIVIDADES DE"))
                return NotifType.Actividades;

            if (first.Contains("RECORDATORIO"))
                return NotifType.Recordatorios;

            if (first.StartsWith("LTIMAS") || first.StartsWith("ÚLTIMAS") || first.StartsWith("ULTIMAS"))
                return NotifType.UltimasAcciones;

            if (upper.Contains("FTF"))
                return NotifType.Comprobatoria;

            if (upper.Contains("REZAGADAS") || upper.Contains("REZAGADA"))
                return NotifType.Rezagadas;

            if (first.Contains("REVISIONES") || first.Contains("REVISION"))
            {
                if (upper.Contains("TERMINADAS") && upper.Contains("CONFIRMADAS") && upper.Contains("PENDIENTES")
                    && !message.Contains('\n'))
                    return NotifType.ReporteResumen;

                if (message.Contains("\n\n"))
                    return NotifType.Revisiones;

                if (message.Contains('\n'))
                    return NotifType.DrillDownRevisiones;

                return NotifType.Revisiones;
            }

            if (message.TrimStart().StartsWith("¿"))
                return NotifType.Pregunta;

            if (upper.Contains("ERROR") || upper.Contains("NO PUDE"))
                return NotifType.Error;

            return NotifType.Sistema;
        }

        // ─── ACTIVIDADES ──────────────────────────────────────────────────────────────

        private static List<ActivityItem> ParseActivityList(string message)
        {
            var result = new List<ActivityItem>();
            var blocks = message.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in blocks)
            {
                var lines = block
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).ToArray();

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

        private void RenderActivityList(List<ActivityItem> items, string rawMessage)
        {
            var firstLine = rawMessage.Split('\n')[0].Trim();
            var (accent, bg, border, glyph) = GetTypeStyle(NotifType.Actividades);

            NotificationsPanel.Children.Add(BuildSummaryRow(firstLine, accent, bg, border, glyph));
            foreach (var item in items)
                NotificationsPanel.Children.Add(BuildActivityRow(item));

            SetNotifHeader(NotifType.Actividades, items.Count);
        }

        // ─── RECORDATORIOS ────────────────────────────────────────────────────────────

        private static List<RecordatorioItem> ParseRecordatorioList(string message)
        {
            var result = new List<RecordatorioItem>();
            var blocks = message.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in blocks)
            {
                var lines = block
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).ToArray();

                if (lines.Length == 0 ||
                    !lines[0].StartsWith("Recordatorio ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var mensaje = lines.Length > 1 ? TrimDot(lines[1]) : "Sin mensaje";
                var fecha = ExtractField(lines, "Fecha:");
                var estado = ExtractField(lines, "Estado:");
                var calendarStr = ExtractField(lines, "Calendar:");
                var tieneCalendar = calendarStr.Equals("Sí", StringComparison.OrdinalIgnoreCase);

                result.Add(new RecordatorioItem(mensaje, fecha, estado, tieneCalendar));
            }

            return result;
        }

        private void RenderRecordatorioList(List<RecordatorioItem> items, string rawMessage)
        {
            var firstLine = rawMessage.Split('\n')[0].Trim();
            var (accent, bg, border, glyph) = GetTypeStyle(NotifType.Recordatorios);

            NotificationsPanel.Children.Add(BuildSummaryRow(firstLine, accent, bg, border, glyph));
            foreach (var item in items)
                NotificationsPanel.Children.Add(BuildRecordatorioRow(item));

            SetNotifHeader(NotifType.Recordatorios, items.Count);
        }

        private static Border BuildRecordatorioRow(RecordatorioItem item)
        {
            var isCompleted = item.Estado.Equals("completado", StringComparison.OrdinalIgnoreCase);
            var (accent, _, _, _) = GetTypeStyle(NotifType.Recordatorios);
            var dotColor = isCompleted
                ? ColorHelper.FromArgb(0xFF, 0x3A, 0xB0, 0x85)
                : accent;

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(dotColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(dot, 0);

            var center = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(new TextBlock
            {
                Text = item.Mensaje,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (!string.IsNullOrWhiteSpace(item.Fecha))
            {
                var fechaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                fechaRow.Children.Add(new FontIcon
                {
                    Glyph = "\uE787",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x47, 0x55, 0x69)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                fechaRow.Children.Add(new TextBlock
                {
                    Text = item.Fecha,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4B, 0x55, 0x63)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (item.TieneCalendar)
                    fechaRow.Children.Add(new FontIcon
                    {
                        Glyph = "\uE7BA",
                        FontSize = 9,
                        Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4A, 0x7F, 0xA5)),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                center.Children.Add(fechaRow);
            }
            Grid.SetColumn(center, 1);

            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            if (!string.IsNullOrWhiteSpace(item.Estado))
            {
                var tc = isCompleted
                    ? ColorHelper.FromArgb(0xFF, 0x3A, 0xB0, 0x85)
                    : accent;
                var bc = isCompleted
                    ? ColorHelper.FromArgb(0x1A, 0x3A, 0xB0, 0x85)
                    : ColorHelper.FromArgb(0x1A, accent.R, accent.G, accent.B);
                badges.Children.Add(BuildBadge(item.Estado, tc, bc));
            }
            Grid.SetColumn(badges, 2);

            grid.Children.Add(dot);
            grid.Children.Add(center);
            grid.Children.Add(badges);
            border.Child = grid;
            return border;
        }

        // ─── REVISIONES ───────────────────────────────────────────────────────────────

        private static List<RevisionItem> ParseRevisionesList(string message)
        {
            var result = new List<RevisionItem>();
            var blocks = message.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in blocks)
            {
                var lines = block
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).ToArray();

                if (lines.Length == 0 ||
                    !lines[0].StartsWith("Revision ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var titulo = lines.Length > 1 ? TrimDot(lines[1]) : "Sin título";
                var hora = ExtractField(lines, "Hora:");

                result.Add(new RevisionItem(titulo, hora));
            }

            return result;
        }

        private void RenderRevisionesList(List<RevisionItem> items, string rawMessage)
        {
            var firstLine = rawMessage.Split('\n')[0].Trim();
            var (accent, bg, border, glyph) = GetTypeStyle(NotifType.Revisiones);

            NotificationsPanel.Children.Add(BuildSummaryRow(firstLine, accent, bg, border, glyph));
            foreach (var item in items)
                NotificationsPanel.Children.Add(BuildRevisionRow(item));

            SetNotifHeader(NotifType.Revisiones, items.Count);
        }

        // ─── DRILL-DOWN REVISIONES ────────────────────────────────────────────────────

        private static (string header, List<DrillDownRevisionItem> items) ParseDrillDownList(string message)
        {
            var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Select(l => l.Trim()).ToArray();
            var header = lines.Length > 0 ? lines[0].TrimEnd(':') : "";
            var items = new List<DrillDownRevisionItem>();

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var dotIdx = line.IndexOf('.');
                if (dotIdx < 0) continue;

                if (!int.TryParse(line[..dotIdx].Trim(), out var idx)) continue;

                var titulo = TrimDot(line[(dotIdx + 1)..].Trim());
                if (!string.IsNullOrWhiteSpace(titulo))
                    items.Add(new DrillDownRevisionItem(idx, titulo));
            }

            return (header, items);
        }

        private void RenderDrillDownList(string header, List<DrillDownRevisionItem> items)
        {
            var (accent, bg, border, glyph) = GetTypeStyle(NotifType.DrillDownRevisiones);
            NotificationsPanel.Children.Add(BuildSummaryRow(header, accent, bg, border, glyph));

            foreach (var item in items)
            {
                var rowBorder = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 10, 12, 10),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1)
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var idxBadge = new Border
                {
                    CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(7, 3, 7, 3),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0x7A, 0x6F, 0xA5)),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = item.Index.ToString(),
                        FontSize = 10,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(accent)
                    }
                };
                Grid.SetColumn(idxBadge, 0);

                var titulo = new TextBlock
                {
                    Text = item.Titulo,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(titulo, 1);

                row.Children.Add(idxBadge);
                row.Children.Add(titulo);
                rowBorder.Child = row;
                NotificationsPanel.Children.Add(rowBorder);
            }

            SetNotifHeader(NotifType.DrillDownRevisiones, items.Count);
        }

        // ─── COMPROBATORIA ────────────────────────────────────────────────────────────

        private static (string? nombre, List<ComprobatoriaSeccion> secciones) ParseComprobatoria(string message)
        {
            var colonIdx = message.IndexOf(':');
            if (colonIdx < 0) return (null, new List<ComprobatoriaSeccion>());

            var nombre = message[..colonIdx].Trim();
            var resto = message[(colonIdx + 1)..].Trim();
            var partes = resto.Split('.', StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => p.Trim())
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .ToList();

            var secciones = new List<ComprobatoriaSeccion>();

            foreach (var parte in partes)
            {
                var upper = parte.ToUpperInvariant();

                string label;
                if (upper.Contains("FTF")) label = "FTF";
                else if (upper.Contains("ACTIVIDAD")) label = "Actividades";
                else if (upper.Contains("CUADRATED")) label = "Cuadrated";
                else label = parte.Length > 30 ? parte[..30] + "…" : parte;

                var ok = upper.Contains("EN ORDEN") || upper.Contains("COMPLETADO") || upper.Contains("OK");
                var detalle = parte;

                secciones.Add(new ComprobatoriaSeccion(label, ok, detalle));
            }

            return (nombre, secciones);
        }

        private void RenderComprobatoriaCard(string nombre, List<ComprobatoriaSeccion> secciones)
        {
            SetNotifHeader(NotifType.Comprobatoria);
            var (accent, bg, border, glyph) = GetTypeStyle(NotifType.Comprobatoria);

            var outerBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14, 16, 14),
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1)
            };

            var root = new StackPanel { Spacing = 12 };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerRow.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 13,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = nombre,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xA0, 0xD4, 0xC8)),
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(headerRow);

            root.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0x4A, 0x9A, 0x8A)),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            foreach (var sec in secciones)
            {
                var secRow = new Grid();
                secRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                secRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                secRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(sec.Ok
                        ? ColorHelper.FromArgb(0xFF, 0x3A, 0xB0, 0x85)
                        : ColorHelper.FromArgb(0xFF, 0xA5, 0x8B, 0x4A)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(dot, 0);

                var txt = new TextBlock
                {
                    Text = sec.Detalle,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xCB, 0xD5, 0xE1)),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txt, 1);

                var ico = new FontIcon
                {
                    Glyph = sec.Ok ? "\uE73E" : "\uE783",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(sec.Ok
                        ? ColorHelper.FromArgb(0xFF, 0x3A, 0xB0, 0x85)
                        : ColorHelper.FromArgb(0xFF, 0xA5, 0x8B, 0x4A)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                Grid.SetColumn(ico, 2);

                secRow.Children.Add(dot);
                secRow.Children.Add(txt);
                secRow.Children.Add(ico);
                root.Children.Add(secRow);
            }

            outerBorder.Child = root;
            NotificationsPanel.Children.Add(outerBorder);
        }

        // ─── REZAGADAS ────────────────────────────────────────────────────────────────

        private static (string header, List<RezagadaItem> items) ParseRezagadas(string message)
        {
            var items = new List<RezagadaItem>();
            var colonIdx = message.IndexOf(':');
            if (colonIdx < 0) return ("", items);

            var header = message[..colonIdx].Trim();
            var resto = message[(colonIdx + 1)..].Trim();

            var partes = System.Text.RegularExpressions.Regex
                .Split(resto, @"\d+\.\s")
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            foreach (var parte in partes)
            {
                var p = parte.Trim().TrimEnd('.');

                var desdeMatch = System.Text.RegularExpressions.Regex
                    .Match(p, @"\(desde las (\d{2}:\d{2})\)");

                var desde = desdeMatch.Success ? desdeMatch.Groups[1].Value : "";
                var titulo = desdeMatch.Success
                    ? p[..desdeMatch.Index].Trim().TrimEnd('.')
                    : p;

                if (!string.IsNullOrWhiteSpace(titulo))
                    items.Add(new RezagadaItem(titulo, desde));
            }

            return (header, items);
        }

        private void RenderRezagadasList(string header, List<RezagadaItem> items)
        {
            var (accent, bg, border, glyph) = GetTypeStyle(NotifType.Rezagadas);
            NotificationsPanel.Children.Add(BuildSummaryRow(header, accent, bg, border, glyph));

            foreach (var item in items)
            {
                var rowBorder = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 10, 12, 10),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(dot, 0);

                var center = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                center.Children.Add(new TextBlock
                {
                    Text = item.Titulo,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(center, 1);

                var right = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                if (!string.IsNullOrWhiteSpace(item.Desde))
                    right.Children.Add(BuildBadge(
                        $"Desde {item.Desde}",
                        ColorHelper.FromArgb(0xFF, 0xA5, 0x4A, 0x4A),
                        ColorHelper.FromArgb(0x18, 0xA5, 0x4A, 0x4A)));
                Grid.SetColumn(right, 2);

                grid.Children.Add(dot);
                grid.Children.Add(center);
                grid.Children.Add(right);
                rowBorder.Child = grid;
                NotificationsPanel.Children.Add(rowBorder);
            }

            SetNotifHeader(NotifType.Rezagadas, items.Count);
        }

        // ─── ÚLTIMAS ACCIONES ─────────────────────────────────────────────────────────

        private static (string header, List<UltimaAccionItem> items) ParseUltimasAcciones(string message)
        {
            var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Select(l => l.Trim()).ToArray();
            var header = lines.Length > 0 ? lines[0].TrimEnd(':') : "";
            var items = new List<UltimaAccionItem>();

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var dotIdx = line.IndexOf('.');
                if (dotIdx < 0) continue;
                if (!int.TryParse(line[..dotIdx].Trim(), out _)) continue;

                var content = line[(dotIdx + 1)..].Trim();

                var colonIdx = content.IndexOf(':');
                string actor, accion, titulo;

                if (colonIdx >= 0)
                {
                    var beforeColon = content[..colonIdx].Trim();
                    titulo = TrimDot(content[(colonIdx + 1)..].Trim());

                    var actorParts = beforeColon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (actorParts.Length >= 2)
                    {
                        actor = actorParts[0];
                        accion = string.Join(" ", actorParts[1..]);
                    }
                    else
                    {
                        actor = beforeColon;
                        accion = "";
                    }
                }
                else
                {
                    actor = "";
                    accion = "";
                    titulo = TrimDot(content);
                }

                if (!string.IsNullOrWhiteSpace(titulo))
                    items.Add(new UltimaAccionItem(actor, accion, titulo));
            }

            return (header, items);
        }

        private void RenderUltimasAccionesList(string header, List<UltimaAccionItem> items)
        {
            var (accent, bg, border, glyph) = GetTypeStyle(NotifType.UltimasAcciones);
            NotificationsPanel.Children.Add(BuildSummaryRow(header, accent, bg, border, glyph));

            foreach (var item in items)
            {
                var rowBorder = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 10, 12, 10),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(dot, 0);

                var center = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                center.Children.Add(new TextBlock
                {
                    Text = item.Titulo,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                if (!string.IsNullOrWhiteSpace(item.Actor))
                {
                    var subRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    subRow.Children.Add(new TextBlock
                    {
                        Text = item.Actor,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x6A, 0x7A, 0x9A))
                    });
                    if (!string.IsNullOrWhiteSpace(item.Accion))
                        subRow.Children.Add(new TextBlock
                        {
                            Text = item.Accion,
                            FontSize = 11,
                            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x47, 0x55, 0x69))
                        });
                    center.Children.Add(subRow);
                }
                Grid.SetColumn(center, 1);

                Grid.SetColumn(new StackPanel(), 2);
                grid.Children.Add(dot);
                grid.Children.Add(center);
                rowBorder.Child = grid;
                NotificationsPanel.Children.Add(rowBorder);
            }

            SetNotifHeader(NotifType.UltimasAcciones, items.Count);
        }

        private static Border BuildRevisionRow(RevisionItem item)
        {
            var (accent, _, _, _) = GetTypeStyle(NotifType.Revisiones);

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(dot, 0);

            var center = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(new TextBlock
            {
                Text = item.Titulo,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (!string.IsNullOrWhiteSpace(item.Hora) &&
                !item.Hora.Equals("Sin hora", StringComparison.OrdinalIgnoreCase))
            {
                var horaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                horaRow.Children.Add(new FontIcon
                {
                    Glyph = "\uE787",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x47, 0x55, 0x69)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                horaRow.Children.Add(new TextBlock
                {
                    Text = item.Hora,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4B, 0x55, 0x63)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                center.Children.Add(horaRow);
            }
            Grid.SetColumn(center, 1);

            var badge = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            badge.Children.Add(BuildBadge(
                "Revisión",
                accent,
                ColorHelper.FromArgb(0x18, accent.R, accent.G, accent.B)));
            Grid.SetColumn(badge, 2);

            grid.Children.Add(dot);
            grid.Children.Add(center);
            grid.Children.Add(badge);
            border.Child = grid;
            return border;
        }

        private static RevisionesResumen? ParseRevisionesResumen(string message)
        {
            try
            {
                var colonIdx = message.IndexOf(':');
                if (colonIdx < 0) return null;

                var before = message[..colonIdx].Trim();
                var after = message[(colonIdx + 1)..].Trim();

                var commaIdx = before.IndexOf(',');
                if (commaIdx < 0) return null;

                var nombre = before[..commaIdx].Trim();
                var resto = before[(commaIdx + 1)..].Trim();

                var total = ExtractNumber(resto, "revisiones");
                if (total < 0) return null;

                var fecha = resto.ToLowerInvariant().Contains("hoy") ? "hoy"
                          : resto.ToLowerInvariant().Contains("ayer") ? "ayer"
                          : "este periodo";

                var terminadas = ExtractNumber(after, "terminadas");
                var confirmadas = ExtractNumber(after, "confirmadas");
                var pendientes = ExtractNumber(after, "pendientes");

                if (terminadas < 0 || confirmadas < 0 || pendientes < 0)
                    return null;

                return new RevisionesResumen(nombre, fecha, total, terminadas, confirmadas, pendientes);
            }
            catch { return null; }
        }

        private static int ExtractNumber(string text, string keyword)
        {
            var idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            var before = text[..idx].TrimEnd();
            var parts = before.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return parts.Length > 0 && int.TryParse(parts[^1], out var n) ? n : -1;
        }

        // ─── SHARED BUILDERS ──────────────────────────────────────────────────────────

        private static Border BuildSummaryRow(string text, Color accent, Color bg, Color border, string glyph)
        {
            var textColor = ColorHelper.FromArgb(0xFF,
                (byte)Math.Min(255, accent.R + 40),
                (byte)Math.Min(255, accent.G + 40),
                (byte)Math.Min(255, accent.B + 40));

            var borderEl = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1)
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 13,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(textColor),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            borderEl.Child = row;
            return borderEl;
        }

        private static Border BuildActivityRow(ActivityItem item)
        {
            var (statusColor, statusBg) = GetStatusColors(item.Estado);
            var (dotColor, _) = GetStatusColors(item.Estado);

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            var outerRow = new Grid();
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(dotColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(dot, 0);

            var center = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(new TextBlock
            {
                Text = item.Titulo,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
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
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x47, 0x55, 0x69)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                horarioRow.Children.Add(new TextBlock
                {
                    Text = item.Horario,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4B, 0x55, 0x63)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                center.Children.Add(horarioRow);
            }
            Grid.SetColumn(center, 1);

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
                return (ColorHelper.FromArgb(0xFF, 0x3A, 0xB0, 0x85),
                        ColorHelper.FromArgb(0x18, 0x3A, 0xB0, 0x85));
            if (s.Contains("POR HACER"))
                return (ColorHelper.FromArgb(0xFF, 0x9A, 0x80, 0x40),
                        ColorHelper.FromArgb(0x18, 0x9A, 0x80, 0x40));
            if (s.Contains("ARRANCAR") || s.Contains("P. HACER") || s.Contains("P.HACER"))
                return (ColorHelper.FromArgb(0xFF, 0x9A, 0x68, 0x40),
                        ColorHelper.FromArgb(0x18, 0x9A, 0x68, 0x40));
            if (s.Contains("EN CURSO") || s.Contains("PROGRESO"))
                return (ColorHelper.FromArgb(0xFF, 0x4A, 0x7F, 0xA5),
                        ColorHelper.FromArgb(0x18, 0x4A, 0x7F, 0xA5));
            return (ColorHelper.FromArgb(0xFF, 0x64, 0x7A, 0x8A),
                    ColorHelper.FromArgb(0x14, 0x64, 0x7A, 0x8A));
        }

        private static (Color text, Color bg) GetPriorityColors(string prioridad) =>
            prioridad.ToUpperInvariant() switch
            {
                "ALTA" => (ColorHelper.FromArgb(0xFF, 0xA5, 0x4A, 0x4A),
                            ColorHelper.FromArgb(0x18, 0xA5, 0x4A, 0x4A)),
                "MEDIA" => (ColorHelper.FromArgb(0xFF, 0x9A, 0x80, 0x40),
                            ColorHelper.FromArgb(0x18, 0x9A, 0x80, 0x40)),
                "BAJA" => (ColorHelper.FromArgb(0xFF, 0x4A, 0x7F, 0xA5),
                            ColorHelper.FromArgb(0x18, 0x4A, 0x7F, 0xA5)),
                _ => (ColorHelper.FromArgb(0xFF, 0x64, 0x7A, 0x8A),
                            ColorHelper.FromArgb(0x12, 0x64, 0x7A, 0x8A))
            };

        // ─── PREGUNTA ─────────────────────────────────────────────────────────────────

        private void RenderPreguntaCard(string message)
        {
            SetNotifHeader(NotifType.Pregunta);
            var (accent, bg, border, glyph) = GetTypeStyle(NotifType.Pregunta);

            var borderEl = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel { Spacing = 8 };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerRow.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = "Esperando respuesta",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(headerRow);

            sp.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            });

            borderEl.Child = sp;
            NotificationsPanel.Children.Add(borderEl);
        }

        // ─── SIMPLE CARD ──────────────────────────────────────────────────────────────

        private void RenderRevisionesResumen(RevisionesResumen resumen)
        {
            SetNotifHeader(NotifType.ReporteResumen, resumen.Total);

            var (accent, bg, border, _) = GetTypeStyle(NotifType.ReporteResumen);

            var outerBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14, 16, 14),
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1)
            };

            var root = new StackPanel { Spacing = 14 };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerRow.Children.Add(new FontIcon
            {
                Glyph = "\uE9D9",
                FontSize = 13,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });

            var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headerText.Children.Add(new TextBlock
            {
                Text = resumen.Nombre,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xC4, 0xB8, 0xE8))
            });
            headerText.Children.Add(new TextBlock
            {
                Text = $"Revisiones de {resumen.Fecha} · {resumen.Total} en total",
                FontSize = 11,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x64, 0x5A, 0x8A))
            });
            headerRow.Children.Add(headerText);
            root.Children.Add(headerRow);

            root.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0x7A, 0x6F, 0xA5)),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            var countersGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            countersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            countersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            countersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var colTerminadas = BuildCounterBlock(resumen.Terminadas.ToString(), "Terminadas",
                ColorHelper.FromArgb(0xFF, 0x3A, 0xB0, 0x85),
                ColorHelper.FromArgb(0x14, 0x3A, 0xB0, 0x85));
            var colConfirmadas = BuildCounterBlock(resumen.Confirmadas.ToString(), "Confirmadas",
                ColorHelper.FromArgb(0xFF, 0x4A, 0x7F, 0xA5),
                ColorHelper.FromArgb(0x14, 0x4A, 0x7F, 0xA5));
            var colPendientes = BuildCounterBlock(resumen.Pendientes.ToString(), "Pendientes",
                ColorHelper.FromArgb(0xFF, 0xA5, 0x8B, 0x4A),
                ColorHelper.FromArgb(0x14, 0xA5, 0x8B, 0x4A));

            Grid.SetColumn(colTerminadas, 0);
            Grid.SetColumn(colConfirmadas, 1);
            Grid.SetColumn(colPendientes, 2);

            countersGrid.Children.Add(colTerminadas);
            countersGrid.Children.Add(colConfirmadas);
            countersGrid.Children.Add(colPendientes);

            root.Children.Add(countersGrid);
            outerBorder.Child = root;
            NotificationsPanel.Children.Add(outerBorder);
        }

        private void RenderSimpleCard(string message, NotifType type = NotifType.Sistema)
        {
            SetNotifHeader(type);
            var (accent, bg, border, glyph) = GetTypeStyle(type);
            var textColor = ColorHelper.FromArgb(0xFF,
                (byte)Math.Min(255, accent.R + 60),
                (byte)Math.Min(255, accent.G + 60),
                (byte)Math.Min(255, accent.B + 60));

            var borderEl = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel { Spacing = 8 };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerRow.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = "Sistema",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
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

            borderEl.Child = sp;
            NotificationsPanel.Children.Add(borderEl);
        }

        // ─── HELPERS COMPARTIDOS ──────────────────────────────────────────────────────

        private static StackPanel BuildCounterBlock(string number, string label, Color numColor, Color bgColor)
        {
            var wrapper = new Border
            {
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(bgColor)
            };

            var inner = new StackPanel
            {
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 10, 8, 10)
            };

            inner.Children.Add(new TextBlock
            {
                Text = number,
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(numColor),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            inner.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x7A, 0x8A, 0x9A)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            wrapper.Child = inner;
            return new StackPanel { Children = { wrapper } };
        }

        private static string ExtractField(string[] lines, string prefix)
        {
            foreach (var l in lines)
                if (l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return l.Substring(prefix.Length).Trim();
            return "";
        }

        private static string TrimDot(string s) => s.TrimEnd().TrimEnd('.').Trim();

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
            SubscribeToHistory();
            UpdateSpeedChipVisuals("1"); // x1 activo por defecto al cargar
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

        // ─────────────────────────────────────────────────────────
        // HISTORIAL DE COMANDOS
        // ─────────────────────────────────────────────────────────

        private void SubscribeToHistory()
        {
            var vm = DataContext as HomeViewModel;
            if (vm == null) return;

            vm.RecentCommands.CollectionChanged += (s, e) =>
            {
                EmptyHistoryState.Visibility = vm.RecentCommands.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            };

            EmptyHistoryState.Visibility = vm.RecentCommands.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────────────────
        // CONTROLES TTS
        // ─────────────────────────────────────────────────────────

        // Aplica visual activo/inactivo a los tres chips según el tag seleccionado.
        // Entrada: tag "1" | "2" | "3"  — sin framework de estado, 100% directo.
        private void UpdateSpeedChipVisuals(string selectedTag)
        {
            var chips = new (Border border, TextBlock text)[]
            {
                (SpeedChip1, SpeedChip1Text),
                (SpeedChip2, SpeedChip2Text),
                (SpeedChip3, SpeedChip3Text),
            };

            foreach (var (border, text) in chips)
            {
                bool active = border.Tag?.ToString() == selectedTag;
                border.Background = active
                    ? new SolidColorBrush(ColorHelper.FromArgb(0xCC, 0xFF, 0x6B, 0x35))
                    : new SolidColorBrush(ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
                border.BorderBrush = active
                    ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0x6B, 0x35))
                    : new SolidColorBrush(ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                text.Foreground = active
                    ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(ColorHelper.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            }
        }

        // Maneja tap en chip de velocidad.
        // Entrada: Border con Tag "1" | "2" | "3" → velocidad 1.0 | 2.0 | 3.0
        private void SpeedChip_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_viewModel == null) return;
            if (sender is not Border border) return;
            var tag = border.Tag?.ToString() ?? "1";
            if (!double.TryParse(tag, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var speed)) return;
            _viewModel.SpeakingRate = speed;
            UpdateSpeedChipVisuals(tag);
        }
    }
}