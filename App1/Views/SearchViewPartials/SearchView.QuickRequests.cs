using Anfeta.UI.Models.Weblab;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;
using Windows.Storage;
using Anfeta.UI.Models.Notion;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Search;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private bool _programasQuickFilter;
        private static readonly Regex ProgramTag = new(@"(?<![\p{L}\p{Nd}_])pprog(?![\p{L}\p{Nd}_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PriorityTag = new(
            @"(?<![\p{L}\p{Nd}_])(?<tag>jjohn|nneft|kkarl|bbria|ggena|iisai|iisaia|eemma|aandr|ssote|eedua|aacal)\s*(?<variant>001|002|00)(?![\p{L}\p{Nd}_])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PriorityPrefixTag = new(
            @"(?<![\p{L}\p{Nd}_.:\-/])(?<variant>001|002|00)\s*(?<tag>jjohn|nneft|kkarl|bbria|ggena|iisai|iisaia|eemma|aandr|ssote|eedua|aacal)(?![\p{L}\p{Nd}_])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, string> PriorityPeople = new(StringComparer.OrdinalIgnoreCase)
        {
            ["John"] = "jjohn",
            ["Neftali"] = "nneft",
            ["Karla"] = "kkarl",
            ["Brian"] = "bbria",
            ["Genaro"] = "ggena",
            ["Isaias"] = "iisai"
        };

        private static string NormalizePriorityTag(string tag) => tag.ToLowerInvariant() switch
        {
            "iisaia" => "iisai",
            "eedua" => "ssote",
            _ => tag.ToLowerInvariant()
        };

        private static IEnumerable<(string Tag, string Variant)> GetPriorityMatches(SearchResultRow row)
        {
            var name = row.Name ?? string.Empty;
            if (AssignmentChangeTracker.IsTerminated(name))
                return Array.Empty<(string Tag, string Variant)>();

            if (Regex.IsMatch(name, @"(?i)(?<![\p{L}\p{Nd}_])(?:ccale|fftf)(?![\p{L}\p{Nd}_])"))
                return Array.Empty<(string Tag, string Variant)>();

            var list = new List<(string Tag, string Variant)>();
            foreach (Match m in PriorityTag.Matches(name))
            {
                list.Add((NormalizePriorityTag(m.Groups["tag"].Value), m.Groups["variant"].Value.ToLowerInvariant()));
            }
            foreach (Match m in PriorityPrefixTag.Matches(name))
            {
                list.Add((NormalizePriorityTag(m.Groups["tag"].Value), m.Groups["variant"].Value.ToLowerInvariant()));
            }
            return list.Distinct();
        }

        private static IEnumerable<string> GetPriorityTags(SearchResultRow row) =>
            GetPriorityMatches(row).Select(m => m.Tag).Distinct();

        private static string PriorityRowKey(SearchResultRow row) =>
            !string.IsNullOrWhiteSpace(row.ExternalId) ? row.Source + ":" + row.ExternalId : row.Source + ":" + row.Target;

        private static bool IsPriority00FamilyQuery(string query)
        {
            var clean = query.Trim();
            var match = PriorityTag.Match(clean);
            if (match.Success && match.Value.Length == clean.Length) return true;
            var prefixMatch = PriorityPrefixTag.Match(clean);
            return prefixMatch.Success && prefixMatch.Value.Length == clean.Length;
        }

        private static bool IsProgramQuickFilterRow(SearchResultRow row)
        {
            if (row == null ||
                row.Source != SearchSource.Notion)
            {
                return false;
            }

            // "Programas" es un filtro por TAG pprog, no por la base
            // "Programas y proyectos". zProyectos permanece como filtro de
            // base independiente.
            //
            // El índice puede conservar el título original/tag en SearchText
            // aunque Name/DisplayName ya estén limpiados para la UI. Por eso
            // validar solo row.Name dejaba fuera muchas actividades reales.
            var searchable = string.Join(
                " ",
                new[]
                {
                    row.DisplayName,
                    row.Name,
                    row.SearchText,
                    row.Description,
                    row.ProjectUpdateStatus,
                    row.PathColumn,
                    row.Target
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));

            return ProgramTag.IsMatch(searchable);
        }

        private IEnumerable<SearchResultRow> ApplyRequestedQuickFilters(IEnumerable<SearchResultRow> rows, string query)
        {
            if (_programasQuickFilter)
            {
                rows = rows
                    .Where(IsProgramQuickFilterRow)
                    .DistinctBy(PriorityRowKey);
            }

            if (IsPriority00FamilyQuery(query))
            {
                var qClean = query.Trim();
                var m = PriorityTag.Match(qClean);
                var tag = m.Success ? NormalizePriorityTag(m.Groups["tag"].Value) : NormalizePriorityTag(PriorityPrefixTag.Match(qClean).Groups["tag"].Value);
                var variant = m.Success ? m.Groups["variant"].Value.ToLowerInvariant() : PriorityPrefixTag.Match(qClean).Groups["variant"].Value.ToLowerInvariant();
                rows = rows.Where(r => GetPriorityMatches(r).Any(match => match.Tag == tag && (string.IsNullOrEmpty(variant) || match.Variant == variant))).DistinctBy(PriorityRowKey);
            }
            return rows;
        }

        private async void ChipProgramasRapido_Click(object sender, RoutedEventArgs e)
        {
            ResetQuickRequestFileFilters();
            _programasQuickFilter = (sender as ToggleButton)?.IsChecked == true;
            _activeNotionBaseFilter = string.Empty;
            _activePaymentBaseTitleFilter = string.Empty;
            _activeSourceScope = SearchSourceScope.Notion;
            SetSourceScopeChipChecks();
            SaveSourceScopePreference();
            // Replace conflicting base queries (e.g. zproyectos); this is a direct quick filter.
            SearchBox.Text = string.Empty;
            SetNotionBaseChipChecks(string.Empty);
            await RunLocalSearchAsync(string.Empty);

            ModeText.Text = _programasQuickFilter
                ? "Modo: Notion · Programas (pprog)"
                : "Modo: Buscar (Notion)";

            StatusText.Text = _programasQuickFilter
                ? $"Estado: Programas pprog ✅ · {Results.Count} resultado(s)"
                : "Estado: Filtro Programas desactivado ✅";
        }

        private Dictionary<string, int> _priority00Counts = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _priority001Counts = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _priority002Counts = new(StringComparer.OrdinalIgnoreCase);

        private void ResetQuickRequestFileFilters()
        {
            _onlyBookmarks = false;
            _onlyFolders = false;
            _extFilter = null;
            foreach (var chip in new[] { ChipBookmarks, ChipFolders, ChipPdf, ChipDocx, ChipXlsx, ChipImg })
                if (chip != null) chip.IsChecked = false;
        }

        private readonly Dictionary<string, Button> _priority00Buttons = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _priority00UnseenCounts = new(StringComparer.OrdinalIgnoreCase);
        private List<(SearchResultRow Row, string Tag, string Variant)> _priority00CachedMatches = new();
        private DateTime _priority00LastRefresh = DateTime.MinValue;
        private bool _priority00Loading;
        private bool _priority00Loaded;
        private long _priority00IndexVersion = -1;

        private async void RefreshPriority00Counts(bool force = false)
        {
            if (_priority00Loading) return;
            if (!force && DateTime.UtcNow - _priority00LastRefresh < TimeSpan.FromSeconds(15)) return;
            var version = App.LocalIndex.Version;
            if (!force && _priority00Loaded && version == _priority00IndexVersion) return;
            _priority00Loading = true;
            _priority00LastRefresh = DateTime.UtcNow;
            try
            {
                var snapshot = App.LocalIndex.GetAll().ToArray();
                var matches = await Task.Run(() => snapshot
                    .Where(r => !IsExcludedPath(r.Target))
                    .DistinctBy(PriorityRowKey)
                    .SelectMany(r => GetPriorityMatches(r).Select(m => (Row: r, Tag: m.Tag, Variant: m.Variant)))
                    .ToList());

                _priority00CachedMatches = matches;

                _priority00Counts = matches.Where(m => m.Variant == "00")
                    .GroupBy(m => m.Tag, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                _priority001Counts = matches.Where(m => m.Variant == "001")
                    .GroupBy(m => m.Tag, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                _priority002Counts = matches.Where(m => m.Variant == "002")
                    .GroupBy(m => m.Tag, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                _priority00Loaded = true;
                _priority00IndexVersion = version;

                EnsureSeenHooked();
                await PrioritySeenTracker.EnsureLoadedAsync();

                UpdateAllPriorityBadgeStates();

                if (_priority00PanelTag != null) RenderPriority00Panel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PRIORITY00] {ex.Message}");
                _priority00LastRefresh = DateTime.MinValue;
            }
            finally { _priority00Loading = false; }
        }

        private void UpdateAllPriorityBadgeStates()
        {
            _priority00UnseenCounts = _priority00CachedMatches
                .Where(m => m.Variant == "00")
                .Where(m => !PrioritySeenTracker.IsSeen(!string.IsNullOrWhiteSpace(m.Row.ExternalId) ? m.Row.ExternalId : m.Row.Target, out _))
                .GroupBy(m => m.Tag, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var (tag, btn) in _priority00Buttons)
            {
                var count00 = _priority00Counts.GetValueOrDefault(tag);
                var count01 = _priority001Counts.GetValueOrDefault(tag);
                var count02 = _priority002Counts.GetValueOrDefault(tag);
                var unseen00 = _priority00UnseenCounts.GetValueOrDefault(tag);
                var person = PriorityPeople.FirstOrDefault(x => string.Equals(x.Value, tag, StringComparison.OrdinalIgnoreCase)).Key ?? tag;
                ApplySingleBadgeState(btn, person, tag, count00, count01, count02, unseen00);
            }
        }

        private bool _seenHooked;
        private void EnsureSeenHooked()
        {
            if (_seenHooked) return;
            _seenHooked = true;
            PrioritySeenTracker.OnSeenChanged += () =>
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    _priority00RenderedVersion = -1;
                    UpdateAllPriorityBadgeStates();
                    RenderPriority00Panel();
                });
            };
        }

        private static void ApplySingleBadgeState(Button btn, string person, string tag, int count00, int count01, int count02, int unseen00 = 0)
        {
            if (count00 > 0)
            {
                var hasUnseen = unseen00 > 0;
                btn.Content = hasUnseen ? $"👁️ {count00}" : $"{count00}";
                btn.Background = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28)); // Rojo urgente
                btn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                btn.BorderBrush = new SolidColorBrush(hasUnseen ? Color.FromArgb(255, 254, 202, 202) : Color.FromArgb(220, 248, 113, 113));
                btn.BorderThickness = new Thickness(hasUnseen ? 2 : 1.5);
                btn.Visibility = Visibility.Visible;
                var unseenDetail = hasUnseen ? $"{unseen00} pendiente(s) de ver" : "todas vistas";
                ToolTipService.SetToolTip(btn, $"🔴 {count00} Urgente(s) (00) [{unseenDetail}] · {person}. Clic para ver actividades.");
            }
            else if (count01 > 0)
            {
                btn.Content = $"01·{count01}";
                btn.Background = new SolidColorBrush(Color.FromArgb(255, 120, 53, 15)); // Ámbar / mostaza
                btn.Foreground = new SolidColorBrush(Color.FromArgb(255, 254, 243, 199));
                btn.BorderBrush = new SolidColorBrush(Color.FromArgb(220, 245, 158, 11));
                btn.BorderThickness = new Thickness(1.5);
                btn.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(btn, $"🟡 {count01} Importante(s) (01) · {person}. Clic para ver actividades.");
            }
            else if (count02 > 0)
            {
                btn.Content = $"02·{count02}";
                btn.Background = new SolidColorBrush(Color.FromArgb(255, 12, 45, 70)); // Azul secundario
                btn.Foreground = new SolidColorBrush(Color.FromArgb(255, 186, 230, 253));
                btn.BorderBrush = new SolidColorBrush(Color.FromArgb(220, 56, 189, 248));
                btn.BorderThickness = new Thickness(1.5);
                btn.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(btn, $"🔵 {count02} Secundaria(s) (02) · {person}. Clic para ver actividades.");
            }
            else
            {
                btn.Content = "0";
                btn.Visibility = Visibility.Collapsed;
                ToolTipService.SetToolTip(btn, null);
            }
        }

        private FrameworkElement CreatePriority00Button(string person)
        {
            PriorityPeople.TryGetValue(person, out var tag);
            if (tag == null) return new Grid { Visibility = Visibility.Collapsed };

            var count00Init = _priority00Counts.GetValueOrDefault(tag);
            var count01Init = _priority001Counts.GetValueOrDefault(tag);
            var count02Init = _priority002Counts.GetValueOrDefault(tag);
            var unseen00Init = _priority00UnseenCounts.GetValueOrDefault(tag);

            // 1 solo badge adaptativo que prioriza 00 (rojo con ojito si no se ha visto) > 01 (ámbar) > 02 (azul) > oculto
            var btn = new Button
            {
                Padding = new Thickness(6, 1, 6, 1),
                MinWidth = 26,
                MinHeight = 24,
                Height = 25,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Center
            };

            if (_priority00Loaded)
            {
                ApplySingleBadgeState(btn, person, tag, count00Init, count01Init, count02Init, unseen00Init);
            }
            else
            {
                btn.Content = "0";
                btn.Visibility = Visibility.Collapsed;
            }

            btn.Click += (_, __) =>
            {
                OpenPriority00Panel(person, tag);
            };

            _priority00Buttons[tag] = btn;
            return btn;
        }

        private void OpenPriority00Panel(string person, string tag)
        {
            if (CalendarPersonPreviewPanel == null || CalendarPersonPreviewItems == null) return;

            _calendarPersonPreviewCts?.Cancel();
            _calendarPersonPreviewCts?.Dispose();
            _calendarPersonPreviewCts = new CancellationTokenSource();

            _calendarPersonPreviewPerson = person;
            _priority00PanelTag = tag;
            _priority00PanelVariant = _priority00Counts.GetValueOrDefault(tag) > 0 ? "00"
                : _priority001Counts.GetValueOrDefault(tag) > 0 ? "001" : "002";
            _priority00RenderedVersion = -1;

            CalendarPersonPreviewPanel.Visibility = Visibility.Visible;
            CalendarPersonPreviewTitle.Text = $"Pendientes y Rápidas de {person}";
            CalendarPersonPreviewDate.Text = "Todas las fechas · Ordenadas por importancia";

            RenderPriority00Panel();
        }

        private Border BuildPriorityActivityCard(SearchResultRow row, Color borderTint)
        {
            var is00 = GetPriorityMatches(row).Any(m => m.Variant == "00");
            var rowKey = !string.IsNullOrWhiteSpace(row.ExternalId) ? row.ExternalId : row.Target;
            PrioritySeenEntry? seenEntry = null;
            var isSeen = is00 && PrioritySeenTracker.IsSeen(rowKey, out seenEntry);

            var stack = new StackPanel { Spacing = 7 };

            // Badge de estado Visto / Pendiente para 00
            if (is00)
            {
                if (isSeen && seenEntry != null)
                {
                    var seenBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(255, 20, 55, 30)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(200, 34, 197, 94)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 3, 8, 3),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    seenBadge.Child = new TextBlock
                    {
                        Text = $"✅ VISTO por {seenEntry.SeenBy} ({seenEntry.SeenAtUtc.ToLocalTime():dd/MM HH:mm})",
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 134, 239, 172))
                    };
                    stack.Children.Add(seenBadge);
                }
                else
                {
                    var pendingBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(255, 65, 20, 20)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(200, 239, 68, 68)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 3, 8, 3),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    pendingBadge.Child = new TextBlock
                    {
                        Text = "🔴 PENDIENTE DE VER (00 urgente no visto)",
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 252, 165, 165))
                    };
                    stack.Children.Add(pendingBadge);
                }
            }

            var titleTb = new TextBlock
            {
                Text = row.Name,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13.5
            };
            if (isSeen)
            {
                titleTb.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                titleTb.Opacity = 0.65;
            }
            stack.Children.Add(titleTb);

            stack.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(row.ScheduledDate) ? "Sin fecha registrada" : row.ScheduledDate, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 11 });
            if (!string.IsNullOrWhiteSpace(row.ProjectUpdateStatus))
                stack.Children.Add(new TextBlock { Text = "Última actualización: " + row.ProjectUpdateStatus, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.85 });

            var actions = new StackPanel { Spacing = 6 };

            // Botón para marcar / desmarcar visto
            if (is00)
            {
                if (!isSeen)
                {
                    var markSeenBtn = new Button
                    {
                        Content = "👁️ Marcar como visto",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 134, 239, 172)),
                        Background = new SolidColorBrush(Color.FromArgb(255, 20, 50, 30)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(160, 34, 197, 94)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    };
                    ToolTipService.SetToolTip(markSeenBtn, "Confirmar que ya leíste y viste este 00 urgente para que quien lo envió lo sepa");
                    markSeenBtn.Click += async (_, __) =>
                    {
                        markSeenBtn.IsEnabled = false;
                        markSeenBtn.Content = "Guardando visto...";
                        try
                        {
                            var myTag = ApplicationData.Current.LocalSettings.Values[LS_CurrentUserTag] as string;
                            var personName = !string.IsNullOrWhiteSpace(myTag) ? myTag : (_calendarPersonPreviewPerson ?? "Destinatario");
                            await PrioritySeenTracker.MarkSeenAsync(rowKey, personName, row.DisplayName ?? row.Name);
                            StatusText.Text = $"Estado: Actividad marcada como vista ✅ · {row.DisplayName ?? row.Name}";
                            _priority00RenderedVersion = -1;
                            RenderPriority00Panel();
                        }
                        catch (Exception ex)
                        {
                            markSeenBtn.IsEnabled = true;
                            markSeenBtn.Content = "👁️ Marcar como visto";
                            StatusText.Text = $"Estado: Error al marcar visto → {ex.Message}";
                        }
                    };
                    actions.Children.Add(markSeenBtn);
                }
                else
                {
                    var unmarkBtn = new Button
                    {
                        Content = "✓ Visto (clic para desmarcar)",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Foreground = new SolidColorBrush(Color.FromArgb(200, 160, 170, 180)),
                        Background = new SolidColorBrush(Color.FromArgb(100, 30, 40, 50)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(100, 100, 120, 140)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        FontSize = 11
                    };
                    ToolTipService.SetToolTip(unmarkBtn, "Desmarcar estado de visto");
                    unmarkBtn.Click += async (_, __) =>
                    {
                        unmarkBtn.IsEnabled = false;
                        await PrioritySeenTracker.UnmarkSeenAsync(rowKey);
                        StatusText.Text = $"Estado: Visto desmarcado · {row.DisplayName ?? row.Name}";
                        _priority00RenderedVersion = -1;
                        RenderPriority00Panel();
                    };
                    actions.Children.Add(unmarkBtn);
                }
            }

            var open = new Button { Content = row.Source == SearchSource.Notion ? "Abrir en Notion" : "Abrir archivo", HorizontalAlignment = HorizontalAlignment.Stretch };
            open.Click += async (_, __) =>
            {
                try
                {
                    if (row.Source == SearchSource.Notion) await OpenNotionDesktopAsync(row, true);
                    else Process.Start(new ProcessStartInfo(row.Target) { UseShellExecute = true });
                }
                catch (Exception ex) { StatusText.Text = "Estado: No se pudo abrir → " + ex.Message; }
            };
            var content = new ContentControl { Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            if (row.Source == SearchSource.Notion && !string.IsNullOrWhiteSpace(row.ExternalId))
            {
                var preview = new Button { Content = "Ver contenido", HorizontalAlignment = HorizontalAlignment.Stretch };
                var activity = _calendarActivities.FirstOrDefault(a => a.PageId.Equals(row.ExternalId, StringComparison.OrdinalIgnoreCase)) ?? new NotionCalendarActivity
                {
                    PageId = row.ExternalId,
                    PageUrl = GetRowTarget(row),
                    Title = row.Name,
                    Description = row.Description,
                    UpdateText = row.ProjectUpdateStatus
                };
                preview.Click += async (_, __) => await ToggleCalendarPersonActivityContentAsync(activity, preview, content);
                actions.Children.Add(preview);
            }
            actions.Children.Add(open);

            var deleteBtn = new Button
            {
                Content = "🗑️ Eliminar",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113)),
                Background = new SolidColorBrush(Color.FromArgb(255, 45, 18, 18)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, 248, 113, 113)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            deleteBtn.Click += async (_, __) =>
            {
                deleteBtn.IsEnabled = false;
                deleteBtn.Content = "Eliminando...";
                try
                {
                    await DeletePriorityActivityAsync(row);
                }
                catch (Exception ex)
                {
                    deleteBtn.IsEnabled = true;
                    deleteBtn.Content = "🗑️ Eliminar";
                    StatusText.Text = $"Estado: Error al eliminar → {ex.Message}";
                }
            };
            actions.Children.Add(deleteBtn);

            stack.Children.Add(actions);
            stack.Children.Add(content);

            var effectiveBorderTint = isSeen ? Color.FromArgb(140, 34, 197, 94) : borderTint;
            var effectiveBg = isSeen ? Color.FromArgb(255, 16, 26, 24) : Color.FromArgb(255, 20, 28, 38);

            var cardBorder = new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(effectiveBorderTint),
                Background = new SolidColorBrush(effectiveBg),
                Child = stack
            };

            cardBorder.PointerEntered += (_, __) =>
            {
                cardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
                cardBorder.Background = new SolidColorBrush(Color.FromArgb(255, 24, 34, 48));
            };
            cardBorder.PointerExited += (_, __) =>
            {
                cardBorder.BorderBrush = new SolidColorBrush(effectiveBorderTint);
                cardBorder.Background = new SolidColorBrush(effectiveBg);
            };

            return cardBorder;
        }

        private async Task DeletePriorityActivityAsync(SearchResultRow row)
        {
            if (row == null) return;

            StatusText.Text = $"Estado: Eliminando pendiente → {row.DisplayName ?? row.Name}";

            if (row.Source == SearchSource.Notion && !string.IsNullOrWhiteSpace(row.ExternalId))
            {
                var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    try
                    {
                        var service = new NotionPageActionsService();
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        await service.MovePageToTrashAsync(token, row.ExternalId, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        if (!NotionPageActionsService.IsMissingPageError(ex))
                        {
                            Debug.WriteLine($"[DELETE_PRIORITY_NOTION] {ex.Message}");
                        }
                    }
                }

                await RemoveNotionRowsFromIndexAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.ExternalId });
            }
            else
            {
                var rowKey = PriorityRowKey(row);
                var snapshot = App.LocalIndex.GetAll()
                    .Where(r => PriorityRowKey(r) != rowKey)
                    .ToList();
                App.LocalIndex.Set(snapshot);
                await PersistCombinedIndexIfPossibleAsync(snapshot);
            }

            _priority00IndexVersion = -1;
            _priority00RenderedVersion = -1;
            RefreshPriority00Counts(force: true);
            RenderPriority00Panel();
            StatusText.Text = "Estado: Pendiente eliminado correctamente ✅";
        }

        private async Task DeleteAllPriorityActivitiesAsync(List<SearchResultRow> rows)
        {
            if (rows == null || rows.Count == 0) return;

            StatusText.Text = $"Estado: Eliminando {rows.Count} actividades urgentes...";
            var notionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            var service = new NotionPageActionsService();

            foreach (var row in rows)
            {
                if (row.Source == SearchSource.Notion && !string.IsNullOrWhiteSpace(row.ExternalId))
                {
                    notionIds.Add(row.ExternalId);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            await service.MovePageToTrashAsync(token, row.ExternalId, cts.Token);
                        }
                        catch { }
                    }
                }
            }

            if (notionIds.Count > 0)
            {
                await RemoveNotionRowsFromIndexAsync(notionIds);
            }

            var toRemoveKeys = new HashSet<string>(rows.Select(PriorityRowKey), StringComparer.OrdinalIgnoreCase);
            var snapshot = App.LocalIndex.GetAll()
                .Where(r => !toRemoveKeys.Contains(PriorityRowKey(r)))
                .ToList();
            App.LocalIndex.Set(snapshot);
            await PersistCombinedIndexIfPossibleAsync(snapshot);

            _priority00IndexVersion = -1;
            _priority00RenderedVersion = -1;
            RefreshPriority00Counts(force: true);
            RenderPriority00Panel();
            StatusText.Text = $"Estado: {rows.Count} actividades eliminadas correctamente ✅";
        }

        private CancellationTokenSource? _priorityPurgeCts;

        private async Task PurgeDeletedPriorityActivitiesAsync(string tag, bool silent = false)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;
            var token = ApplicationData.Current.LocalSettings.Values[LS_NotionToken] as string;
            if (string.IsNullOrWhiteSpace(token))
            {
                if (!silent) StatusText.Text = "Estado: Notion no configurado para verificar actividades.";
                return;
            }

            var notionRows = App.LocalIndex.GetAll()
                .Where(r => r.Source == SearchSource.Notion && !string.IsNullOrWhiteSpace(r.ExternalId))
                .Where(r => !IsExcludedPath(r.Target))
                .Where(r => GetPriorityMatches(r).Any(m => m.Tag == tag))
                .DistinctBy(PriorityRowKey)
                .ToList();

            if (notionRows.Count == 0)
            {
                if (!silent) StatusText.Text = "Estado: No hay actividades de Notion pendientes para verificar.";
                return;
            }

            if (!silent) StatusText.Text = $"Estado: Verificando {notionRows.Count} actividades en Notion...";

            try
            {
                _priorityPurgeCts?.Cancel();
                _priorityPurgeCts?.Dispose();
            }
            catch { }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _priorityPurgeCts = cts;

            var service = new NotionPageActionsService();
            var deadIds = new List<string>();

            using var throttler = new SemaphoreSlim(4);
            var tasks = notionRows.Select(async row =>
            {
                await throttler.WaitAsync(cts.Token);
                try
                {
                    var active = await service.IsPageActiveAsync(token, row.ExternalId, cts.Token);
                    if (!active)
                    {
                        lock (deadIds) { deadIds.Add(row.ExternalId); }
                    }
                }
                catch (Exception ex) when (NotionPageActionsService.IsMissingPageError(ex))
                {
                    lock (deadIds) { deadIds.Add(row.ExternalId); }
                }
                catch
                {
                }
                finally
                {
                    throttler.Release();
                }
            });

            try
            {
                await Task.WhenAll(tasks);
            }
            catch { }

            if (deadIds.Count > 0 && !cts.IsCancellationRequested)
            {
                await RemoveNotionRowsFromIndexAsync(new HashSet<string>(deadIds, StringComparer.OrdinalIgnoreCase));
                StatusText.Text = $"Estado: Se detectaron y depuraron {deadIds.Count} actividades eliminadas en Notion ✅";
            }
            else if (!silent && !cts.IsCancellationRequested)
            {
                StatusText.Text = "Estado: Todas las actividades verificadas siguen activas en Notion ✅";
            }
        }

        private string? _priority00PanelTag;
        private string? _priority00PanelVariant;
        private long _priority00RenderedVersion = -1;
        private void RenderPriority00Panel()
        {
            if (_priority00PanelTag == null || CalendarPersonPreviewPanel.Visibility != Visibility.Visible) return;
            var version = App.LocalIndex.Version;
            if (version == _priority00RenderedVersion) return;
            _priority00RenderedVersion = version;

            EnsureSeenHooked();
            _ = PrioritySeenTracker.EnsureLoadedAsync();

            var allMatches = App.LocalIndex.GetAll()
                .Where(r => !IsExcludedPath(r.Target))
                .DistinctBy(PriorityRowKey)
                .Select(r => (Row: r, Match: GetPriorityMatches(r).FirstOrDefault(m => m.Tag == _priority00PanelTag)))
                .Where(x => x.Match != default)
                .ToList();

            var urgentes00 = allMatches.Where(x => x.Match.Variant == "00").Select(x => x.Row).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var importantes001 = allMatches.Where(x => x.Match.Variant == "001").Select(x => x.Row).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var secundarias002 = allMatches.Where(x => x.Match.Variant == "002").Select(x => x.Row).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var otras = allMatches.Where(x => x.Match.Variant != "00" && x.Match.Variant != "001" && x.Match.Variant != "002").Select(x => x.Row).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // Separar 00 en pendientes y vistos para ordenarlos y diferenciarlos
            var urgentesPendientes = urgentes00
                .Where(r => !PrioritySeenTracker.IsSeen(!string.IsNullOrWhiteSpace(r.ExternalId) ? r.ExternalId : r.Target, out _))
                .ToList();
            var urgentesVistos = urgentes00
                .Where(r => PrioritySeenTracker.IsSeen(!string.IsNullOrWhiteSpace(r.ExternalId) ? r.ExternalId : r.Target, out _))
                .ToList();
            var urgentesSorted = urgentesPendientes.Concat(urgentesVistos).ToList();

            var totalCount = urgentes00.Count + importantes001.Count + secundarias002.Count + otras.Count;

            CalendarPersonPreviewTitle.Text = $"Pendientes y Rápidas de {_calendarPersonPreviewPerson}";
            CalendarPersonPreviewDate.Text = "Todas las fechas · Ordenadas por importancia";

            var summary00 = urgentes00.Count > 0
                ? $"🔴 {urgentes00.Count} Urgentes ({urgentesPendientes.Count} pendientes · {urgentesVistos.Count} vistos)"
                : "🔴 0 Urgentes";
            CalendarPersonPreviewSummary.Text = $"{summary00} · 🟡 {importantes001.Count} Importantes · 🔵 {secundarias002.Count} Secundarias";
            CalendarPersonPreviewItems.Children.Clear();

            if (totalCount == 0)
            {
                CalendarPersonPreviewItems.Children.Add(BuildCalendarPersonPreviewMessage($"No hay actividades rápidas asignadas para {_calendarPersonPreviewPerson}.", false));
                return;
            }

            void AddSection(string sectionTitle, Color headerBg, Color headerBorder, Color headerText, List<SearchResultRow> sectionRows, bool canDeleteAll = false)
            {
                var headerCard = new Border
                {
                    Background = new SolidColorBrush(headerBg),
                    BorderBrush = new SolidColorBrush(headerBorder),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 8, 0, 4)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var titleTb = new TextBlock
                {
                    Text = sectionTitle,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(headerText),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var rightPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (canDeleteAll && sectionRows.Count > 0)
                {
                    var clearAllBtn = new Button
                    {
                        Content = "🗑️ Eliminar todos",
                        FontSize = 10,
                        Padding = new Thickness(6, 2, 6, 2),
                        Foreground = new SolidColorBrush(headerText),
                        Background = new SolidColorBrush(Color.FromArgb(160, 45, 18, 18)),
                        BorderBrush = new SolidColorBrush(headerBorder),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5)
                    };
                    ToolTipService.SetToolTip(clearAllBtn, $"Eliminar todas las {sectionRows.Count} actividades urgentes de esta lista");
                    clearAllBtn.Click += async (_, __) =>
                    {
                        clearAllBtn.IsEnabled = false;
                        clearAllBtn.Content = "Borrando...";
                        await DeleteAllPriorityActivitiesAsync(sectionRows);
                    };
                    rightPanel.Children.Add(clearAllBtn);
                }

                var countTb = new TextBlock
                {
                    Text = $"{sectionRows.Count}",
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(headerText),
                    VerticalAlignment = VerticalAlignment.Center
                };
                rightPanel.Children.Add(countTb);

                Grid.SetColumn(titleTb, 0);
                Grid.SetColumn(rightPanel, 1);
                grid.Children.Add(titleTb);
                grid.Children.Add(rightPanel);
                headerCard.Child = grid;
                CalendarPersonPreviewItems.Children.Add(headerCard);

                if (sectionRows.Count == 0)
                {
                    CalendarPersonPreviewItems.Children.Add(new TextBlock
                    {
                        Text = "Sin actividades en esta categoría",
                        FontSize = 11,
                        Opacity = 0.5,
                        Margin = new Thickness(8, 2, 0, 6)
                    });
                    return;
                }

                foreach (var row in sectionRows)
                {
                    CalendarPersonPreviewItems.Children.Add(BuildPriorityActivityCard(row, headerBorder));
                }
            }

            // 1. Urgentes (00)
            var section00Title = urgentes00.Count > 0
                ? $"🔴 Urgentes (00) · {urgentesPendientes.Count} pendientes · {urgentesVistos.Count} vistos"
                : "🔴 Urgentes (00)";
            AddSection(section00Title, Color.FromArgb(255, 45, 18, 18), Color.FromArgb(255, 248, 113, 113), Color.FromArgb(255, 254, 202, 202), urgentesSorted, canDeleteAll: true);

            // 2. Importantes (01)
            AddSection("🟡 Importantes · 01", Color.FromArgb(255, 45, 34, 12), Color.FromArgb(255, 251, 191, 36), Color.FromArgb(255, 254, 243, 199), importantes001);

            // 3. Secundarias (02)
            AddSection("🔵 Secundarias · 02", Color.FromArgb(255, 14, 38, 58), Color.FromArgb(255, 56, 189, 248), Color.FromArgb(255, 224, 242, 254), secundarias002);

            // 4. Otras
            if (otras.Count > 0)
            {
                AddSection("⚪ Otras Prioritarias", Color.FromArgb(255, 28, 35, 45), Color.FromArgb(255, 100, 120, 140), Color.FromArgb(255, 220, 230, 240), otras);
            }
        }

        private const string DefaultNotionSearchShortcut = "Ctrl+Shift+K";
        private const string DefaultNotionAiShortcut = "Ctrl+Shift+J";

        private static string GetNotionShortcut(bool ai)
        {
            var settingsKey =
                ai
                    ? "Notion.Shortcut.AI"
                    : "Notion.Shortcut.Search";

            var fallback =
                ai
                    ? DefaultNotionAiShortcut
                    : DefaultNotionSearchShortcut;

            var saved =
                ApplicationData.Current.LocalSettings.Values[
                    settingsKey] as string;

            // Si quedó guardado un valor viejo/inválido, no lo mostramos ni
            // intentamos enviarlo a Notion. Se vuelve al default conocido.
            return TryNormalizeNotionShortcut(
                    saved,
                    out var normalized)
                ? normalized
                : fallback;
        }

        private static bool TryNormalizeNotionShortcut(
            string? text,
            out string normalized)
        {
            normalized = string.Empty;

            if (!TryParseNotionShortcut(
                    text ?? string.Empty,
                    out var modifiers,
                    out var key))
            {
                return false;
            }

            // Presentación estable, independientemente del orden en que el
            // usuario lo escribió en ANFETA.
            var parts = new List<string>();

            if (modifiers.Contains((byte)0x11))
                parts.Add("Ctrl");

            if (modifiers.Contains((byte)0x10))
                parts.Add("Shift");

            if (modifiers.Contains((byte)0x12))
                parts.Add("Alt");

            if (modifiers.Contains((byte)0x5B))
                parts.Add("Win");

            parts.Add(((char)key).ToString());

            normalized = string.Join("+", parts);
            return true;
        }

        private static bool TryParseNotionShortcut(string text, out byte[] modifiers, out byte key)
        {
            modifiers = Array.Empty<byte>(); key = 0;
            var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Length > 5) return false;
            var last = parts[^1].ToUpperInvariant();
            if (last.Length != 1 || !(last[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return false;
            key = (byte)last[0];
            var list = new List<byte>();
            foreach (var part in parts.Take(parts.Length - 1))
            {
                byte code = part.ToLowerInvariant() switch { "ctrl" => 0x11, "shift" => 0x10, "alt" => 0x12, "win" => 0x5B, _ => 0 };
                if (code == 0 || list.Contains(code)) return false;
                list.Add(code);
            }
            modifiers = list.ToArray(); return true;
        }

        private void NotionShortcuts_Opening(object sender, object e)
        {
            NotionSearchShortcutItem.Text = "Buscar · " + GetNotionShortcut(false);
            NotionAiShortcutItem.Text = "IA · " + GetNotionShortcut(true);
        }

        private async void ConfigureNotionShortcuts_Click(object sender, RoutedEventArgs e)
        {
            await Task.Delay(180);
            var search = new TextBox { Header = "Búsqueda", Text = GetNotionShortcut(false), MaxLength = 40 };
            var ai = new TextBox { Header = "IA", Text = GetNotionShortcut(true), MaxLength = 40 };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var reset = new Button { Content = "Restablecer K / J" };
            reset.Click += (_, __) => { search.Text = DefaultNotionSearchShortcut; ai.Text = DefaultNotionAiShortcut; };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = $"Defaults de Notion: Buscar {DefaultNotionSearchShortcut} · IA {DefaultNotionAiShortcut}. Si los cambias en Notion, copia aquí las mismas combinaciones. ANFETA no modifica las Preferencias de Notion automáticamente; solo guarda y envía los atajos que configures aquí. Admite Ctrl, Shift, Alt, Win y una letra o número.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(search); panel.Children.Add(ai); panel.Children.Add(reset); panel.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Atajos de Notion", Content = panel, PrimaryButtonText = "Guardar", CloseButtonText = "Cancelar" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!TryNormalizeNotionShortcut(
                        search.Text,
                        out var normalizedSearch) ||
                    !TryNormalizeNotionShortcut(
                        ai.Text,
                        out var normalizedAi))
                {
                    args.Cancel = true;
                    error.Text =
                        "Usa Ctrl, Shift, Alt o Win + una letra/número. " +
                        "Ejemplo: Ctrl+Shift+K.";
                    return;
                }

                if (string.Equals(
                        normalizedSearch,
                        normalizedAi,
                        StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                    error.Text =
                        "Buscar e IA deben usar combinaciones distintas.";
                    return;
                }

                // Normaliza antes de cerrar para que el usuario vea
                // exactamente qué combinación quedará guardada.
                search.Text = normalizedSearch;
                ai.Text = normalizedAi;
                error.Text = string.Empty;
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (!TryNormalizeNotionShortcut(
                        search.Text,
                        out var normalizedSearch) ||
                    !TryNormalizeNotionShortcut(
                        ai.Text,
                        out var normalizedAi))
                {
                    return;
                }

                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Shortcut.Search"] =
                    normalizedSearch;

                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Shortcut.AI"] =
                    normalizedAi;

                // El menú refleja el cambio sin exigir cerrar/reabrir ANFETA.
                NotionSearchShortcutItem.Text =
                    "Buscar · " + normalizedSearch;

                NotionAiShortcutItem.Text =
                    "IA · " + normalizedAi;

                StatusText.Text =
                    $"Estado: Atajos de Notion guardados ✅ · " +
                    $"Buscar {normalizedSearch} · IA {normalizedAi}";
            }
        }

        [DllImport("user32.dll", EntryPoint = "keybd_event")]
        private static extern void NotionKeyEvent(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
        private static extern short NotionKeyState(int key);

        private bool _notionShortcutBusy;
        private async void NotionShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_notionShortcutBusy) return;
            _notionShortcutBusy = true;
            try
            {
                if (!IsNotionDesktopProtocolHandlerUsable())
                {
                    StatusText.Text = "Estado: Los atajos requieren Notion Desktop instalado y Command Search habilitado.";
                    return;
                }
                if (!IsNotionDesktopProcessRunning())
                {
                    await Launcher.LaunchUriAsync(new Uri("notion://www.notion.so"));
                    if (!await WaitForNotionDesktopProcessAsync(TimeSpan.FromSeconds(4)))
                    {
                        StatusText.Text = "Estado: No se pudo iniciar Notion Desktop; no se enviaron teclas.";
                        return;
                    }
                }
                await Task.Delay(250); // Let the menu close before the global shortcut.
                if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (NotionKeyState(k) & 0x8000) != 0))
                {
                    StatusText.Text = "Estado: Suelta Ctrl, Shift, Alt y Windows y vuelve a pulsar.";
                    return;
                }
                var shortcut = GetNotionShortcut((sender as FrameworkElement)?.Tag?.ToString() == "ai");
                if (!TryParseNotionShortcut(shortcut, out var modifiers, out var key))
                { StatusText.Text = "Estado: Configura un atajo válido en el menú Notion."; return; }
                try
                {
                    foreach (var modifier in modifiers) NotionKeyEvent(modifier, 0, 0, UIntPtr.Zero);
                    NotionKeyEvent(key, 0, 0, UIntPtr.Zero);
                }
                finally
                {
                    NotionKeyEvent(key, 0, 2, UIntPtr.Zero);
                    foreach (var modifier in modifiers.Reverse()) NotionKeyEvent(modifier, 0, 2, UIntPtr.Zero);
                }
                StatusText.Text = $"Estado: Atajo {shortcut} enviado. Notion debe tener esa combinación configurada.";
            }
            catch (Exception ex) { StatusText.Text = $"Estado: No se pudo enviar el atajo de Notion → {ex.Message}"; }
            finally { _notionShortcutBusy = false; }
        }
    }
}
