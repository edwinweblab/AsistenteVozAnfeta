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
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;
using Windows.Storage;
using Anfeta.UI.Models.Notion;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private bool _programasQuickFilter;
        private static readonly Regex ProgramTag = new(@"(?<![\p{L}\p{Nd}_])pprog(?![\p{L}\p{Nd}_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PriorityTag = new(@"(?<![\p{L}\p{Nd}_])(?<tag>jjohn|nneft|kkarl|bbria|ggena|iisai|iisaia|eemma|aandr|ssote|eedua|aacal)00\d*(?![\p{L}\p{Nd}_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, string> PriorityPeople = new(StringComparer.OrdinalIgnoreCase)
        {
            ["John"] = "jjohn",
            ["Neftali"] = "nneft",
            ["Karla"] = "kkarl",
            ["Brian"] = "bbria",
            ["Genaro"] = "ggena",
            ["Isaias"] = "iisai",
            ["Emmanuel"] = "eemma",
            ["Andrade"] = "aandr",
            ["Sotelo"] = "ssote",
            ["Acalli"] = "aacal"
        };

        private static string NormalizePriorityTag(string tag) => tag.ToLowerInvariant() switch
        {
            "iisaia" => "iisai",
            "eedua" => "ssote",
            _ => tag.ToLowerInvariant()
        };

        private static IEnumerable<string> GetPriorityTags(SearchResultRow row)
        {
            // Match explicit title tags only, not hidden URLs/body or arbitrary 00s.
            // Calendar documents are not quick activities, even if tagged jjohn00.
            var name = row.Name ?? string.Empty;
            if (Regex.IsMatch(name, @"(?i)(?<![\p{L}\p{Nd}_])(?:ccale|fftf)(?![\p{L}\p{Nd}_])"))
                return Array.Empty<string>();
            return PriorityTag.Matches(name).Cast<Match>()
                .Select(m => NormalizePriorityTag(m.Groups["tag"].Value)).Distinct();
        }

        private static string PriorityRowKey(SearchResultRow row) =>
            !string.IsNullOrWhiteSpace(row.ExternalId) ? row.Source + ":" + row.ExternalId : row.Source + ":" + row.Target;

        private static bool IsPriority00FamilyQuery(string query)
        {
            var clean = query.Trim();
            var match = PriorityTag.Match(clean);
            return match.Success && match.Value.Length == clean.Length && clean.Length == match.Groups["tag"].Length + 2;
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

            var match = PriorityTag.Match(query.Trim());
            if (IsPriority00FamilyQuery(query))
            {
                var tag = NormalizePriorityTag(match.Groups["tag"].Value);
                rows = rows.Where(r => GetPriorityTags(r).Contains(tag)).DistinctBy(PriorityRowKey);
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

        private void ResetQuickRequestFileFilters()
        {
            _onlyBookmarks = false;
            _onlyFolders = false;
            _extFilter = null;
            foreach (var chip in new[] { ChipBookmarks, ChipFolders, ChipPdf, ChipDocx, ChipXlsx, ChipImg })
                if (chip != null) chip.IsChecked = false;
        }
        private readonly Dictionary<string, Button> _priority00Buttons = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _priority00LastRefresh = DateTime.MinValue;
        private bool _priority00Loading;
        private bool _priority00Loaded;
        private long _priority00IndexVersion = -1;

        private async void RefreshPriority00Counts()
        {
            if (_priority00Loading || DateTime.UtcNow - _priority00LastRefresh < TimeSpan.FromSeconds(15)) return;
            var version = App.LocalIndex.Version;
            if (_priority00Loaded && version == _priority00IndexVersion) return;
            _priority00Loading = true;
            _priority00LastRefresh = DateTime.UtcNow;
            try
            {
                var snapshot = App.LocalIndex.GetAll().ToArray();
                _priority00Counts = await Task.Run(() => snapshot
                    .Where(r => !IsExcludedPath(r.Target))
                    .DistinctBy(PriorityRowKey)
                    .SelectMany(GetPriorityTags)
                    .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
                _priority00Loaded = true;
                _priority00IndexVersion = version;
                foreach (var pair in _priority00Buttons)
                    pair.Value.Content = $"00 · {_priority00Counts.GetValueOrDefault(pair.Key)}";
                if (_priority00PanelTag != null) RenderPriority00Panel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PRIORITY00] {ex.Message}");
                _priority00LastRefresh = DateTime.MinValue;
            }
            finally { _priority00Loading = false; }
        }

        private Button CreatePriority00Button(string person)
        {
            PriorityPeople.TryGetValue(person, out var tag);
            var button = new Button
            {
                Content = _priority00Loaded && tag != null ? $"00 · {_priority00Counts.GetValueOrDefault(tag)}" : "00 · …",
                Visibility = tag == null ? Visibility.Collapsed : Visibility.Visible,
                Padding = new Thickness(4, 1, 4, 1),
                MinWidth = 0,
                MinHeight = 0,
                FontSize = Math.Max(9, 10 * CalendarFontScale),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(255, 91, 56, 12)),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 221, 145)),
                CornerRadius = new CornerRadius(5)
            };
            ToolTipService.SetToolTip(button, $"Prioritarios {tag}00 y sufijos numéricos · Notion y Dropbox indexados, sin filtro de fecha ni estado. Excluye documentos de calendario. Clic para ver resultados; actualización periódica mientras el calendario está activo.");
            if (tag != null)
            {
                _priority00Buttons[tag] = button;
                button.Click += (_, __) =>
                {
                    ShowCalendarPersonPreview(person);
                    _priority00PanelTag = tag;
                    _priority00RenderedVersion = -1;
                    RenderPriority00Panel();
                };
            }
            return button;
        }

        private string? _priority00PanelTag;
        private long _priority00RenderedVersion = -1;
        private void RenderPriority00Panel()
        {
            if (_priority00PanelTag == null || CalendarPersonPreviewPanel.Visibility != Visibility.Visible) return;
            var version = App.LocalIndex.Version;
            if (version == _priority00RenderedVersion) return;
            _priority00RenderedVersion = version;
            var rows = App.LocalIndex.GetAll().Where(r => !IsExcludedPath(r.Target) && GetPriorityTags(r).Contains(_priority00PanelTag))
                .DistinctBy(PriorityRowKey).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            CalendarPersonPreviewTitle.Text = $"Prioritarias de {_calendarPersonPreviewPerson} · 00";
            CalendarPersonPreviewDate.Text = "Todas las fechas · Notion y Dropbox indexados";
            CalendarPersonPreviewSummary.Text = $"{rows.Count} actividades etiquetadas · contenido bajo demanda · sin filtro de estado";
            CalendarPersonPreviewItems.Children.Clear();
            if (rows.Count == 0)
                CalendarPersonPreviewItems.Children.Add(BuildCalendarPersonPreviewMessage("No hay actividades con este tag 00 en el índice local.", false));
            foreach (var row in rows)
            {
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(new TextBlock { Text = row.Name, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 });
                stack.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(row.ScheduledDate) ? "Sin fecha registrada" : row.ScheduledDate, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
                if (!string.IsNullOrWhiteSpace(row.ProjectUpdateStatus))
                    stack.Children.Add(new TextBlock { Text = "Última actualización: " + row.ProjectUpdateStatus, TextWrapping = TextWrapping.Wrap });
                var actions = new StackPanel { Spacing = 6 };
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
                stack.Children.Add(actions);
                stack.Children.Add(content);
                CalendarPersonPreviewItems.Children.Add(new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 145, 106, 37)),
                    Background = new SolidColorBrush(Color.FromArgb(255, 27, 36, 45)),
                    Child = stack
                });
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
