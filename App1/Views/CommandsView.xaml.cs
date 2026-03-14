using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace Anfeta.UI.Views
{
    // =========================================================================
    // MODELS
    // =========================================================================

    public sealed class TutorialStep
    {
        public string Number { get; init; } = "";
        public string Glyph { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string? Detail { get; init; }

        public Visibility DetailVisibility =>
            string.IsNullOrWhiteSpace(Detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed class InterfaceElement
    {
        public string Glyph { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
    }

    public sealed class CommandModule
    {
        public string Name { get; init; } = "";
        public List<CommandItem> Commands { get; init; } = new();
    }

    public sealed class CommandItem
    {
        public string Id { get; init; } = "";
        public string Module { get; init; } = "";
        public string Title { get; init; } = "";
        public string Tier { get; init; } = "Tier 1";
        public string[] Phrases { get; init; } = Array.Empty<string>();
        public string ResponseExample { get; init; } = "";
        public string? Endpoint { get; init; }
        public bool RequiresConfirmation { get; init; }
        public bool IsMultiTurn { get; init; }

        public string PhrasesLine => string.Join("  ·  ", Phrases);
        public string IdAndModule => $"{Id}  ·  {Module}";

        public Visibility ConfirmVisibility =>
            RequiresConfirmation ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MultiTurnVisibility =>
            IsMultiTurn ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EndpointVisibility =>
            string.IsNullOrWhiteSpace(Endpoint) ? Visibility.Collapsed : Visibility.Visible;

        // Tier colors
        public Brush TierForeground => Tier switch
        {
            var t when t.StartsWith("Tier 1") => new SolidColorBrush(Color.FromArgb(255, 45, 184, 126)),
            var t when t.StartsWith("Tier 2") => new SolidColorBrush(Color.FromArgb(255, 210, 140, 48)),
            _ => new SolidColorBrush(Color.FromArgb(255, 139, 126, 212))
        };

        public Brush TierBackground => Tier switch
        {
            var t when t.StartsWith("Tier 1") => new SolidColorBrush(Color.FromArgb(30, 45, 184, 126)),
            var t when t.StartsWith("Tier 2") => new SolidColorBrush(Color.FromArgb(30, 210, 140, 48)),
            _ => new SolidColorBrush(Color.FromArgb(30, 139, 126, 212))
        };
    }

    public sealed class DetailContent
    {
        public string Glyph { get; init; } = "";
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public string Note { get; init; } = "";
    }

    public sealed class StatusColorRow
    {
        public string DotColorHex { get; init; } = "#888888";
        public string Description { get; init; } = "";

        public SolidColorBrush DotBrush
        {
            get
            {
                try
                {
                    var hex = DotColorHex.TrimStart('#');
                    return new SolidColorBrush(Color.FromArgb(255,
                        Convert.ToByte(hex[0..2], 16),
                        Convert.ToByte(hex[2..4], 16),
                        Convert.ToByte(hex[4..6], 16)));
                }
                catch { return new SolidColorBrush(Colors.Gray); }
            }
        }
    }

    public sealed class StatusItem
    {
        public string Glyph { get; init; } = "";
        public string Name { get; init; } = "";
        public string DemoLabel { get; init; } = "";
        public string DemoDotHex { get; init; } = "#888888";
        public string Title { get; init; } = "";
        public List<StatusColorRow> ColorRows { get; init; } = new();

        public SolidColorBrush DemoDotBrush
        {
            get
            {
                try
                {
                    var hex = DemoDotHex.TrimStart('#');
                    return new SolidColorBrush(Color.FromArgb(255,
                        Convert.ToByte(hex[0..2], 16),
                        Convert.ToByte(hex[2..4], 16),
                        Convert.ToByte(hex[4..6], 16)));
                }
                catch { return new SolidColorBrush(Colors.Gray); }
            }
        }
    }

    public sealed class SearchSuggestion
    {
        public string Text { get; init; } = "";
        public string Category { get; init; } = "";
        public int DetailIndex { get; init; } = -1;
        public string? TabTarget { get; init; }
        public string? ModuleFilter { get; init; }
    }

    // =========================================================================
    // PAGE
    // =========================================================================

    public sealed partial class CommandsView : Page
    {
        // ── State ──────────────────────────────────────────────────────────────
        private List<CommandModule> _modules = new();
        private List<DetailContent> _details = new();
        private List<SearchSuggestion> _searchIndex = new();
        private CommandModule? _selectedModule;
        private string _query = "";
        private string _tierFilter = "";
        private int _dailyDisplayIdx;

        // Daily rotation order — índices a _details
        private static readonly int[] DailyRotation = { 0, 2, 4, 1, 3, 5 };

        // ── Computed ───────────────────────────────────────────────────────────
        public string SelectedModuleTitle =>
            _selectedModule?.Name ?? "Todos los módulos";

        public string SelectedModuleSubtitle =>
            _selectedModule is null
                ? $"{_modules.Sum(m => m.Commands.Count)} comandos disponibles"
                : $"{_selectedModule.Commands.Count} comandos en este módulo";

        // ── Constructor ────────────────────────────────────────────────────────
        public CommandsView()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _details = BuildDetails();
            _searchIndex = BuildSearchIndex();
            _modules = BuildModules();

            StatusList.ItemsSource = BuildStatusItems();
            BasicStepsList.ItemsSource = GetBasicSteps();
            InterfaceElementsList.ItemsSource = GetInterfaceElements();
            MultiTurnList.ItemsSource = GetMultiTurnFlows();

            var comboItems = new List<CommandModule> { new CommandModule { Name = "Todos" } };
            comboItems.AddRange(_modules);
            ModulesCombo.ItemsSource = comboItems;
            ModulesCombo.SelectedIndex = 0;

            // Daily card: arranca por día del año, mod cantidad
            _dailyDisplayIdx = DateTime.Now.DayOfYear % DailyRotation.Length;
            RefreshDailyCard();

            TotalCount.Text = _modules.Sum(m => m.Commands.Count).ToString();
            VisibleCount.Text = TotalCount.Text;

            ApplyFilter();
        }

        // ── Daily card ─────────────────────────────────────────────────────────
        private void RefreshDailyCard()
        {
            if (_details.Count == 0) return;

            var idx = DailyRotation[_dailyDisplayIdx % DailyRotation.Length];
            var d = _details[idx % _details.Count];

            DailyGlyphIcon.Glyph = d.Glyph;
            DailyTitle.Text = d.Title;

            // Máximo 85 caracteres para que no se vea comprimido
            DailyDesc.Text = d.Body.Length > 85
                ? d.Body[..85].TrimEnd() + "..."
                : d.Body;

            DailyCounter.Text = $"{_dailyDisplayIdx + 1} / {DailyRotation.Length}";
        }

        private void DailyPrev_Click(object sender, RoutedEventArgs e)
        {
            _dailyDisplayIdx = (_dailyDisplayIdx - 1 + DailyRotation.Length)
                               % DailyRotation.Length;
            RefreshDailyCard();
        }

        private void DailyNext_Click(object sender, RoutedEventArgs e)
        {
            _dailyDisplayIdx = (_dailyDisplayIdx + 1) % DailyRotation.Length;
            RefreshDailyCard();
        }

        private async void DailyCard_Tapped(object sender,
            Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var idx = DailyRotation[_dailyDisplayIdx % DailyRotation.Length];
            await ShowDetailAsync(idx);
        }

        // ── Tab switching ──────────────────────────────────────────────────────
        private void ShowPanel(StackPanel active)
        {
            ExplorePanel.Visibility = active == ExplorePanel ? Visibility.Visible : Visibility.Collapsed;
            TutorialPanel.Visibility = active == TutorialPanel ? Visibility.Visible : Visibility.Collapsed;
            CommandsPanel.Visibility = active == CommandsPanel ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnExplore_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(ExplorePanel);
            BtnExplore.IsChecked = true; BtnTutorial.IsChecked = false; BtnCommands.IsChecked = false;
        }

        private void BtnTutorial_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(TutorialPanel);
            BtnExplore.IsChecked = false; BtnTutorial.IsChecked = true; BtnCommands.IsChecked = false;
        }

        private void BtnCommands_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(CommandsPanel);
            BtnExplore.IsChecked = false; BtnTutorial.IsChecked = false; BtnCommands.IsChecked = true;
        }

        private void NavigateToTab(string tab, string? moduleFilter = null)
        {
            switch (tab)
            {
                case "tutorial": BtnTutorial_Click(null!, null!); break;
                case "commands":
                    BtnCommands_Click(null!, null!);
                    if (moduleFilter is not null)
                    {
                        var idx = (ModulesCombo.ItemsSource as List<CommandModule>)
                            ?.FindIndex(m => m.Name == moduleFilter) ?? -1;
                        if (idx >= 0) ModulesCombo.SelectedIndex = idx;
                    }
                    break;
                default: BtnExplore_Click(null!, null!); break;
            }
        }

        // ── Search ─────────────────────────────────────────────────────────────
        private void ExploreSearch_TextChanged(AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = sender.Text.Trim().ToLowerInvariant();
            sender.ItemsSource = string.IsNullOrWhiteSpace(q)
                ? new List<SearchSuggestion>()
                : _searchIndex.Where(s => s.Text.ToLowerInvariant().Contains(q)).Take(8).ToList();
        }

        private async void ExploreSearch_SuggestionChosen(AutoSuggestBox sender,
            AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is not SearchSuggestion s) return;
            sender.Text = "";
            sender.ItemsSource = new List<SearchSuggestion>();
            if (s.DetailIndex >= 0) await ShowDetailAsync(s.DetailIndex);
            else NavigateToTab(s.TabTarget ?? "commands", s.ModuleFilter);
        }

        private async void ExploreSearch_QuerySubmitted(AutoSuggestBox sender,
            AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is SearchSuggestion s)
            {
                sender.Text = "";
                if (s.DetailIndex >= 0) await ShowDetailAsync(s.DetailIndex);
                else NavigateToTab(s.TabTarget ?? "commands", s.ModuleFilter);
            }
        }

        // ── Quick cards ────────────────────────────────────────────────────────
        private async void QuickCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string t && int.TryParse(t, out var i))
                await ShowDetailAsync(i);
        }

        // ── Detail dialog ──────────────────────────────────────────────────────
        private async Task ShowDetailAsync(int index)
        {
            if (index < 0 || index >= _details.Count) return;
            var d = _details[index];

            // Icono + título
            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconBorder = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 128, 0)),
                Child = new FontIcon
                {
                    Glyph = d.Glyph,
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 128, 0))
                }
            };

            titleRow.Children.Add(iconBorder);
            titleRow.Children.Add(new TextBlock
            {
                Text = d.Title,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            // Cuerpo
            var bodyPanel = new StackPanel { Spacing = 16 };

            // Separador
            bodyPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Margin = new Thickness(0, 0, 0, 4)
            });

            bodyPanel.Children.Add(new TextBlock
            {
                Text = d.Body,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Opacity = 0.88,
                LineHeight = 24
            });

            // Nota con acento naranja
            bodyPanel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 128, 0)),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 8, 8, 0),
                Padding = new Thickness(14, 10, 14, 10),
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 128, 0)),
                Child = new TextBlock
                {
                    Text = d.Note,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Opacity = 0.8,
                    LineHeight = 20
                }
            });

            var scrollContent = new ScrollViewer
            {
                Content = bodyPanel,
                MaxHeight = 400,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var dialog = new ContentDialog
            {
                Title = titleRow,
                Content = scrollContent,
                CloseButtonText = "Cerrar",
                XamlRoot = this.XamlRoot,
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = this.ActualTheme
            };

            dialog.Resources["ContentDialogMaxWidth"] = (double)500;

            await dialog.ShowAsync();
        }

        // ── Filters (Commands) ─────────────────────────────────────────────────
        private void ModulesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = ModulesCombo.SelectedItem as CommandModule;
            _selectedModule = sel?.Name == "Todos" ? null : sel;
            ApplyFilter();
            RefreshHeader();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _query = SearchBox.Text?.Trim() ?? "";
            ApplyFilter();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            _query = "";
            _tierFilter = "";
            ModulesCombo.SelectedIndex = 0;
            _selectedModule = null;
            ChipAll.IsChecked = true;
            ChipT1.IsChecked = false;
            ChipT2.IsChecked = false;
            ChipT3.IsChecked = false;
            ApplyFilter();
            RefreshHeader();
        }

        private void ChipAll_Click(object sender, RoutedEventArgs e)
        { _tierFilter = ""; SetChip(ChipAll); ApplyFilter(); }

        private void ChipT1_Click(object sender, RoutedEventArgs e)
        { _tierFilter = "1"; SetChip(ChipT1); ApplyFilter(); }

        private void ChipT2_Click(object sender, RoutedEventArgs e)
        { _tierFilter = "2"; SetChip(ChipT2); ApplyFilter(); }

        private void ChipT3_Click(object sender, RoutedEventArgs e)
        { _tierFilter = "3"; SetChip(ChipT3); ApplyFilter(); }

        private void SetChip(ToggleButton active)
        {
            foreach (var c in new[] { ChipAll, ChipT1, ChipT2, ChipT3 })
                c.IsChecked = c == active;
        }

        private void ApplyFilter()
        {
            var q = _query.ToLowerInvariant();
            var baseList = _selectedModule?.Commands
                ?? _modules.SelectMany(m => m.Commands).ToList();

            var filtered = baseList.Where(c =>
            {
                if (!string.IsNullOrEmpty(_tierFilter) &&
                    !c.Tier.StartsWith("Tier " + _tierFilter)) return false;
                if (!string.IsNullOrWhiteSpace(q) &&
                    !c.Title.ToLowerInvariant().Contains(q) &&
                    !c.Module.ToLowerInvariant().Contains(q) &&
                    !c.PhrasesLine.ToLowerInvariant().Contains(q) &&
                    !c.ResponseExample.ToLowerInvariant().Contains(q)) return false;
                return true;
            }).ToList();

            CommandsList.ItemsSource = filtered;
            VisibleCount.Text = filtered.Count.ToString();
        }

        private void RefreshHeader()
        {
            DataContext = null;
            DataContext = this;
        }

        // =========================================================================
        // DATA
        // =========================================================================

        private static List<DetailContent> BuildDetails() => new()
        {
            new DetailContent // 0
            {
                Glyph = "\uE720",
                Title = "Usar el micrófono",
                Body  = "Presiona el botón naranja central para activar la escucha. El asistente indica el estado con una animación. Habla con claridad en español. Para cancelar en cualquier momento, presiona de nuevo el botón.",
                Note  = "Si el micrófono aparece en rojo en la barra, ve a Configuración de Windows → Privacidad → Micrófono y habilita el acceso para Anfeta."
            },
            new DetailContent // 1
            {
                Glyph = "\uE787",
                Title = "Conectar Google Calendar",
                Body  = "Di 'conectar Google Calendar' y el asistente abrirá el navegador con el flujo de autorización de Google. Una vez autenticado, el indicador Calendar cambiará a verde. Para ver eventos di 'qué tengo hoy' o 'próximos eventos'.",
                Note  = "Si el token expira, el asistente lo detecta y abre el navegador para reconectar automáticamente."
            },
            new DetailContent // 2
            {
                Glyph = "\uE8F9",
                Title = "Crear actividad",
                Body  = "Di 'crear actividad' o 'nueva tarea'. El asistente te pedirá: título → prioridad (Alta/Media/Baja) → fecha y hora de inicio → hora de fin → responsable. Al final muestra un resumen y pide confirmación antes de guardar.",
                Note  = "Di 'corregir título' o 'corregir fecha' antes de confirmar para editar. 'Cancelar' aborta en cualquier momento."
            },
            new DetailContent // 3
            {
                Glyph = "\uE7E7",
                Title = "Gestionar recordatorios",
                Body  = "Para crear: di 'recuérdame [mensaje] el [fecha] a las [hora]'. Para ver: di 'mis recordatorios', 'recordatorios de hoy' o 'recordatorios pendientes'. Para eliminar o completar: primero lista, luego di 'elimina el primero' o 'completa el dos'.",
                Note  = "La lista queda activa 5 minutos tras consultarla. Usa ordinales ('primero') o números directos ('el 2')."
            },
            new DetailContent // 4
            {
                Glyph = "\uE9D2",
                Title = "Ver mis revisiones",
                Body  = "Di 'revisiones de hoy' o 'revisiones de ayer' para el resumen con totales por estado. Luego di 'muéstrame las pendientes', 'ver las terminadas' o 'dame las confirmadas' para el detalle.",
                Note  = "El detalle está disponible 10 minutos. Si expiró, di 'revisiones de hoy' para recargar."
            },
            new DetailContent // 5
            {
                Glyph = "\uE8D7",
                Title = "Iniciar sesión",
                Body  = "Para usar el asistente necesitas vincular tu cuenta empresarial. Ve a Configuración e ingresa tu correo asignado (ejemplo: nombre@practicante.com) y tu número de teléfono empresarial. El indicador cambiará cuando la vinculación sea exitosa.",
                Note  = "Usa exactamente el correo y teléfono que te asignó la empresa. Si no los tienes, contacta a tu coordinador."
            },
        };

        private static List<SearchSuggestion> BuildSearchIndex() => new()
        {
            new SearchSuggestion { Text = "Cómo usar el micrófono",           Category = "Guía",     DetailIndex = 0 },
            new SearchSuggestion { Text = "Activar la escucha de voz",         Category = "Guía",     DetailIndex = 0 },
            new SearchSuggestion { Text = "Permisos de micrófono",             Category = "Guía",     DetailIndex = 0 },
            new SearchSuggestion { Text = "Conectar Google Calendar",          Category = "Guía",     DetailIndex = 1 },
            new SearchSuggestion { Text = "Ver eventos del calendario",        Category = "Comandos", TabTarget = "commands", ModuleFilter = "Google Calendar" },
            new SearchSuggestion { Text = "Crear actividad paso a paso",       Category = "Guía",     DetailIndex = 2 },
            new SearchSuggestion { Text = "Nueva tarea por voz",               Category = "Comandos", TabTarget = "commands", ModuleFilter = "Actividades" },
            new SearchSuggestion { Text = "Editar actividad",                  Category = "Comandos", TabTarget = "commands", ModuleFilter = "Actividades" },
            new SearchSuggestion { Text = "Mis recordatorios",                 Category = "Comandos", TabTarget = "commands", ModuleFilter = "Recordatorios" },
            new SearchSuggestion { Text = "Crear recordatorio",                Category = "Guía",     DetailIndex = 3 },
            new SearchSuggestion { Text = "Eliminar recordatorio",             Category = "Guía",     DetailIndex = 3 },
            new SearchSuggestion { Text = "Revisiones de hoy",                 Category = "Comandos", TabTarget = "commands", ModuleFilter = "Revisiones" },
            new SearchSuggestion { Text = "Ver mis revisiones",                Category = "Guía",     DetailIndex = 4 },
            new SearchSuggestion { Text = "Revisiones pendientes",             Category = "Guía",     DetailIndex = 4 },
            new SearchSuggestion { Text = "Iniciar sesión",                    Category = "Guía",     DetailIndex = 5 },
            new SearchSuggestion { Text = "Vincular cuenta empresarial",       Category = "Guía",     DetailIndex = 5 },
            new SearchSuggestion { Text = "Comprobatoria personal",            Category = "Comandos", TabTarget = "commands", ModuleFilter = "Reportes" },
            new SearchSuggestion { Text = "Tareas rezagadas",                  Category = "Comandos", TabTarget = "commands", ModuleFilter = "Reportes" },
            new SearchSuggestion { Text = "Últimas acciones del equipo",       Category = "Comandos", TabTarget = "commands", ModuleFilter = "Reportes" },
            new SearchSuggestion { Text = "Abrir aplicación por voz",          Category = "Comandos", TabTarget = "commands", ModuleFilter = "Local / Sistema" },
            new SearchSuggestion { Text = "Cómo funciona el asistente",        Category = "Tutorial", TabTarget = "tutorial" },
            new SearchSuggestion { Text = "Confirmar acción",                  Category = "Comandos", TabTarget = "commands", ModuleFilter = "Local / Sistema" },
        };

        private static List<StatusItem> BuildStatusItems() => new()
        {
            new StatusItem
            {
                Glyph = "\uE774", Name = "Internet", DemoLabel = "Internet", DemoDotHex = "#2DB880",
                Title = "Conexión a internet",
                ColorRows = new()
                {
                    new StatusColorRow { DotColorHex = "#2DB880", Description = "Verde — conexión activa, todos los comandos disponibles" },
                    new StatusColorRow { DotColorHex = "#E24B4A", Description = "Rojo — sin conexión, solo comandos locales funcionan" },
                }
            },
            new StatusItem
            {
                Glyph = "\uE77B", Name = "Anfeta", DemoLabel = "Anfeta", DemoDotHex = "#2DB880",
                Title = "Sesión Weblab",
                ColorRows = new()
                {
                    new StatusColorRow { DotColorHex = "#2DB880", Description = "Verde — sesión activa con tu cuenta empresarial" },
                    new StatusColorRow { DotColorHex = "#888888", Description = "Gris — no autenticado, ve a Configuración → Iniciar sesión" },
                }
            },
            new StatusItem
            {
                Glyph = "\uE8D7", Name = "Vinculación", DemoLabel = "No vinculado", DemoDotHex = "#888888",
                Title = "Vinculación de cuenta",
                ColorRows = new()
                {
                    new StatusColorRow { DotColorHex = "#2DB880", Description = "Verde — correo y teléfono empresarial vinculados correctamente" },
                    new StatusColorRow { DotColorHex = "#888888", Description = "Gris — falta ingresar tu correo y número de teléfono asignados" },
                }
            },
            new StatusItem
            {
                Glyph = "\uE720", Name = "Micrófono", DemoLabel = "Micrófono", DemoDotHex = "#2DB880",
                Title = "Dispositivo de entrada",
                ColorRows = new()
                {
                    new StatusColorRow { DotColorHex = "#2DB880", Description = "Verde — micrófono listo y con permisos concedidos" },
                    new StatusColorRow { DotColorHex = "#E24B4A", Description = "Rojo — sin permisos o dispositivo no encontrado" },
                }
            },
            new StatusItem
            {
                Glyph = "\uE8A5", Name = "Dropbox", DemoLabel = "Dropbox", DemoDotHex = "#E24B4A",
                Title = "Sincronización Dropbox",
                ColorRows = new()
                {
                    new StatusColorRow { DotColorHex = "#2DB880", Description = "Verde — Dropbox conectado y sincronizado" },
                    new StatusColorRow { DotColorHex = "#E24B4A", Description = "Rojo — no conectado, ve a Configuración → Dropbox" },
                }
            },
            new StatusItem
            {
                Glyph = "\uE787", Name = "Calendar", DemoLabel = "Calendar", DemoDotHex = "#888888",
                Title = "Google Calendar",
                ColorRows = new()
                {
                    new StatusColorRow { DotColorHex = "#2DB880", Description = "Verde — Calendar conectado, puedes ver y crear eventos" },
                    new StatusColorRow { DotColorHex = "#888888", Description = "Gris — no conectado, di 'conectar Google Calendar'" },
                }
            },
        };

        private static List<TutorialStep> GetBasicSteps() => new()
        {
            new TutorialStep
            {
                Number = "1", Glyph = "\uE720",
                Title  = "Activa el micrófono",
                Description = "Presiona el botón central naranja. El asistente entra en modo escucha e indica el estado con animación.",
                Detail      = "Presionarlo de nuevo mientras escucha cancela el reconocimiento sin ejecutar nada."
            },
            new TutorialStep
            {
                Number = "2", Glyph = "\uE8BD",
                Title  = "Habla el comando",
                Description = "Di el comando en español con claridad. No necesitas la frase exacta — el asistente entiende variaciones naturales.",
                Detail      = "'revisiones de hoy' y 'mis revisiones hoy' producen el mismo resultado."
            },
            new TutorialStep
            {
                Number = "3", Glyph = "\uE8F8",
                Title  = "Recibe la respuesta",
                Description = "La respuesta aparece en el área Detectado y se reproduce en voz. Acciones sensibles piden confirmación antes de ejecutarse.",
                Detail      = "Confirmar: 'sí' · 'ok' · 'dale'  —  Cancelar: 'no' · 'cancelar' · 'negativo'"
            },
        };

        private static List<InterfaceElement> GetInterfaceElements() => new()
        {
            new InterfaceElement { Glyph = "\uE720", Name = "Botón micrófono",     Description = "Naranja fijo = listo. Animado = escuchando. Presionar cancela." },
            new InterfaceElement { Glyph = "\uE995", Name = "Chips x1 / x1.5 / x2", Description = "Velocidad de reproducción de voz. Cambia en tiempo real sin regenerar audio." },
            new InterfaceElement { Glyph = "\uE8D6", Name = "Entrada / Salida",    Description = "Micrófono activo (entrada) y altavoz para la respuesta (salida)." },
            new InterfaceElement { Glyph = "\uE8BD", Name = "Área Detectado",      Description = "Muestra el texto reconocido y la respuesta del asistente." },
            new InterfaceElement { Glyph = "\uE9D9", Name = "Área Sistema",        Description = "Estado del modelo de IA y errores. Verde = todo listo." },
            new InterfaceElement { Glyph = "\uE81C", Name = "Actividad Reciente",  Description = "Últimos 15 comandos del día con hora. Contador de hoy." },
            new InterfaceElement { Glyph = "\uE769", Name = "Pausa / Reanudar",    Description = "Pausa o reanuda la reproducción de voz sin cancelar la acción." },
            new InterfaceElement { Glyph = "\uE8BB", Name = "Detener voz",         Description = "Corta la reproducción inmediatamente. La acción ya ejecutada no se revierte." },
        };

        private static List<TutorialStep> GetMultiTurnFlows() => new()
        {
            new TutorialStep
            {
                Number = "A", Glyph = "\uE8F9",
                Title  = "Crear actividad",
                Description = "Di 'crear actividad'. El asistente guía: título → prioridad → fecha → hora fin → responsable → confirmación.",
                Detail      = "Di 'corregir [campo]' antes de confirmar. 'Cancelar' aborta en cualquier momento."
            },
            new TutorialStep
            {
                Number = "B", Glyph = "\uE70F",
                Title  = "Editar actividad",
                Description = "Di 'editar actividad [nombre]'. El asistente busca, confirma cuál encontró y pregunta qué campo cambiar.",
                Detail      = "Campos: título, prioridad, estado, fecha inicio, fecha fin, anotaciones, pasos y links."
            },
            new TutorialStep
            {
                Number = "C", Glyph = "\uE7E7",
                Title  = "Gestionar recordatorios por número",
                Description = "Lista primero. Luego: 'elimina el primero', 'edita el segundo' o 'completa el tercero'.",
                Detail      = "Lista activa 5 minutos. Usa ordinal ('primero') o número directo ('el 2')."
            },
            new TutorialStep
            {
                Number = "D", Glyph = "\uE948",
                Title  = "Ver detalle de revisiones",
                Description = "Después de 'revisiones de hoy': 'muéstrame las pendientes', 'ver terminadas' o 'dame confirmadas'.",
                Detail      = "Detalle disponible 10 minutos. Si expiró, di 'revisiones de hoy' para recargar."
            },
        };

        private static List<CommandModule> BuildModules() => new()
        {
            new CommandModule { Name = "Reportes", Commands = new()
            {
                new CommandItem
                {
                    Id = "REP-001", Module = "Reportes", Tier = "Tier 1",
                    Title = "Comprobatoria personal",
                    Phrases = new[] { "comprobatoria", "cómo voy hoy", "mi reporte de hoy", "ver mi comprobatoria" },
                    ResponseExample = "[tu nombre]: FTF completado. Actividades en orden. Cuadrated pendiente.",
                    Endpoint = "GET /api/reportes/comprobatoria?assignee={email}"
                },
                new CommandItem
                {
                    Id = "REP-002", Module = "Reportes", Tier = "Tier 1",
                    Title = "Tareas rezagadas",
                    Phrases = new[] { "tareas rezagadas", "mis rezagadas", "actividades atrasadas", "qué está atrasado" },
                    ResponseExample = "[tu nombre], tienes 3 tareas rezagadas: 1. Revisar entregable (desde las 09:00).",
                    Endpoint = "GET /api/reportes/rezagadas?assignee={email}&time={HH:mm}"
                },
                new CommandItem
                {
                    Id = "REP-003", Module = "Reportes", Tier = "Tier 1",
                    Title = "Revisiones por fecha",
                    Phrases = new[] { "revisiones de hoy", "mis revisiones de hoy", "revisiones de ayer", "cuántas revisiones tengo hoy" },
                    ResponseExample = "[tu nombre], hoy tienes 8 revisiones: 3 terminadas, 2 confirmadas y 3 pendientes.",
                    Endpoint = "GET /api/reportes/revisiones-por-fecha?date=YYYY-MM-DD"
                },
                new CommandItem
                {
                    Id = "REP-004", Module = "Reportes", Tier = "Tier 1",
                    Title = "Últimas acciones del equipo",
                    Phrases = new[] { "últimas acciones", "qué ha pasado", "últimos cambios", "muéstrame lo último" },
                    ResponseExample = "Últimas 5 acciones: 1. juan creó revisión: Sprint Review. 2. maría actualizó actividad.",
                    Endpoint = "GET /api/reportes/ultimos"
                },
            }},

            new CommandModule { Name = "Recordatorios", Commands = new()
            {
                new CommandItem
                {
                    Id = "REC-001", Module = "Recordatorios", Tier = "Tier 1",
                    Title = "Ver todos los recordatorios",
                    Phrases = new[] { "mis recordatorios", "ver recordatorios", "listar recordatorios", "qué recordatorios tengo" },
                    ResponseExample = "Tienes 3 recordatorios: Recordatorio 1. Revisar entregable. Hoy a las 15:00. Pendiente. Calendar: Sí.",
                    Endpoint = "GET /api/recordatorios/usuario/{phone}"
                },
                new CommandItem
                {
                    Id = "REC-002", Module = "Recordatorios", Tier = "Tier 1",
                    Title = "Recordatorios de hoy",
                    Phrases = new[] { "recordatorios de hoy", "mis recordatorios de hoy", "qué recordatorios tengo hoy" },
                    ResponseExample = "Hoy tienes 2 recordatorios: Recordatorio 1. Llamar al cliente. A las 10:00.",
                    Endpoint = "GET /api/recordatorios/usuario/{phone} — filtro local por fecha"
                },
                new CommandItem
                {
                    Id = "REC-003", Module = "Recordatorios", Tier = "Tier 1",
                    Title = "Recordatorios de mañana",
                    Phrases = new[] { "recordatorios de mañana", "mis recordatorios de mañana", "qué recordatorios tengo mañana" },
                    ResponseExample = "Mañana tienes 1 recordatorio: Revisión semanal a las 09:00.",
                    Endpoint = "GET /api/recordatorios/usuario/{phone} — filtro local por fecha"
                },
                new CommandItem
                {
                    Id = "REC-004", Module = "Recordatorios", Tier = "Tier 1",
                    Title = "Recordatorios pendientes",
                    Phrases = new[] { "recordatorios pendientes", "mis recordatorios pendientes", "recordatorios sin completar" },
                    ResponseExample = "Tienes 2 recordatorios pendientes: Recordatorio 1. Enviar informe a las 16:00.",
                    Endpoint = "GET /api/recordatorios/usuario/{phone} — activo=true, enviado=false"
                },
                new CommandItem
                {
                    Id = "REC-005", Module = "Recordatorios", Tier = "Tier 1 → IA",
                    Title = "Crear recordatorio",
                    Phrases = new[] { "recuérdame...", "pon un recordatorio", "crea un recordatorio", "programa un recordatorio" },
                    ResponseExample = "Recordatorio 'Llamar al cliente' creado y sincronizado con tu Google Calendar.",
                    Endpoint = "POST /api/recordatorios"
                },
                new CommandItem
                {
                    Id = "REC-006", Module = "Recordatorios", Tier = "Tier 2",
                    Title = "Eliminar o completar recordatorio",
                    Phrases = new[] { "elimina el primero", "borra el 2", "completa el tercero", "marca el 1 como completado" },
                    ResponseExample = "¿Seguro que deseas eliminar el recordatorio 1: 'Llamar al cliente' de hoy a las 10:00? → confirmar → Eliminado correctamente.",
                    Endpoint = "DELETE /api/recordatorios/{id}  ·  PATCH /api/recordatorios/{id}/completar",
                    RequiresConfirmation = true, IsMultiTurn = true
                },
                new CommandItem
                {
                    Id = "REC-007", Module = "Recordatorios", Tier = "Tier 2",
                    Title = "Editar recordatorio",
                    Phrases = new[] { "edita el primero", "modifica el 2", "actualiza el tercero" },
                    ResponseExample = "¿Qué deseas cambiar en 'Llamar al cliente'? → (di el nuevo valor) → Recordatorio actualizado correctamente.",
                    Endpoint = "PUT /api/recordatorios/{id}",
                    RequiresConfirmation = true, IsMultiTurn = true
                },
            }},

            new CommandModule { Name = "Google Calendar", Commands = new()
            {
                new CommandItem
                {
                    Id = "CAL-001", Module = "Google Calendar", Tier = "Tier 1",
                    Title = "Eventos de hoy",
                    Phrases = new[] { "qué tengo hoy", "eventos de hoy", "agenda de hoy", "tengo algo hoy", "qué hay hoy" },
                    ResponseExample = "Hoy tienes 2 eventos: a las 10:00, Reunión de equipo. a las 15:30, Demo con cliente.",
                    Endpoint = "GET google/calendar/list (hoy 00:00–23:59, max 10)"
                },
                new CommandItem
                {
                    Id = "CAL-002", Module = "Google Calendar", Tier = "Tier 1",
                    Title = "Próximos eventos de la semana",
                    Phrases = new[] { "mis eventos", "ver calendario", "próximos eventos", "eventos de la semana", "qué tengo esta semana" },
                    ResponseExample = "Tienes 4 eventos próximos: el lunes 17 a las 09:00, Stand-up. el martes 18 a las 14:00, Revisión.",
                    Endpoint = "GET google/calendar/list (hoy + 7 días, max 10)"
                },
                new CommandItem
                {
                    Id = "CAL-003", Module = "Google Calendar", Tier = "Tier 3 — IA",
                    Title = "Crear evento en calendario",
                    Phrases = new[] { "crea un evento", "agrega evento al calendario", "programa una reunión" },
                    ResponseExample = "Evento 'Reunión de equipo' creado para el 20 de marzo a las 10:00.",
                    Endpoint = "POST google/calendar/create",
                    RequiresConfirmation = true
                },
                new CommandItem
                {
                    Id = "CAL-004", Module = "Google Calendar", Tier = "Tier 3 — IA",
                    Title = "Estado y gestión de conexión",
                    Phrases = new[] { "estado de Google Calendar", "conectar Google Calendar", "desconectar Google Calendar" },
                    ResponseExample = "Tu Google Calendar está conectado.  /  Se abrió el navegador para conectar tu cuenta.",
                    Endpoint = "google/calendar/status  ·  /connect  ·  /disconnect"
                },
            }},

            new CommandModule { Name = "Actividades", Commands = new()
            {
                new CommandItem
                {
                    Id = "ACT-001", Module = "Actividades", Tier = "Tier 2",
                    Title = "Crear actividad",
                    Phrases = new[] { "crear actividad", "nueva actividad", "crea actividad", "crear tarea", "nueva tarea" },
                    ResponseExample = "¿Cuál es el título? → ¿Qué prioridad? → ¿Cuándo inicia? → ¿Hora de fin? → ¿Para quién? → Actividad creada correctamente.",
                    Endpoint = "POST /api/actividades",
                    IsMultiTurn = true
                },
                new CommandItem
                {
                    Id = "ACT-002", Module = "Actividades", Tier = "Tier 2",
                    Title = "Editar actividad",
                    Phrases = new[] { "editar actividad [nombre]", "edita actividad", "modificar actividad", "cambiar actividad" },
                    ResponseExample = "Encontré 'Revisión de sprint'. ¿Qué campo quieres editar? → Prioridad → Alta → Actividad actualizada.",
                    Endpoint = "PUT /api/actividades/{id}",
                    RequiresConfirmation = true, IsMultiTurn = true
                },
                new CommandItem
                {
                    Id = "ACT-003", Module = "Actividades", Tier = "Tier 3 — IA",
                    Title = "Listar mis actividades",
                    Phrases = new[] { "lista mis actividades", "ver mis actividades", "qué actividades tengo" },
                    ResponseExample = "Tienes 5 actividades: 1. Revisión de sprint — Alta. 2. Actualizar documentación — Media.",
                    Endpoint = "GET /api/actividades (limit=10)"
                },
                new CommandItem
                {
                    Id = "ACT-004", Module = "Actividades", Tier = "Tier 3 — IA",
                    Title = "Buscar actividad",
                    Phrases = new[] { "busca la actividad [nombre]", "buscar actividad", "encuentra la actividad de [tema]" },
                    ResponseExample = "Encontré 2 actividades con 'sprint': 1. Revisión de sprint. 2. Planning sprint.",
                    Endpoint = "GET /api/actividades/search?q={texto}"
                },
                new CommandItem
                {
                    Id = "ACT-005", Module = "Actividades", Tier = "Tier 3 — IA",
                    Title = "Actividades de hoy",
                    Phrases = new[] { "mis actividades de hoy", "qué actividades tengo hoy", "actividades para hoy" },
                    ResponseExample = "Hoy tienes 3 actividades: 1. Stand-up diario — Alta. 2. Revisión de código — Media.",
                    Endpoint = "GET /api/actividades/today?assignee={email}"
                },
            }},

            new CommandModule { Name = "Revisiones", Commands = new()
            {
                new CommandItem
                {
                    Id = "REV-001", Module = "Revisiones", Tier = "Tier 1 + Tier 2",
                    Title = "Revisiones del día + detalle por estado",
                    Phrases = new[] { "revisiones de hoy", "revisiones de ayer", "muéstrame las pendientes", "ver las terminadas", "dame las confirmadas", "ver todas" },
                    ResponseExample = "[tu nombre], hoy tienes 8 revisiones: 3 terminadas, 2 confirmadas y 3 pendientes. → (drill-down) → tienes 3 revisiones pendientes: 1. Sprint Review.",
                    Endpoint = "GET /api/reportes/revisiones-por-fecha?date={date} — cache 10 min para drill-down",
                    IsMultiTurn = true
                },
                new CommandItem
                {
                    Id = "REV-002", Module = "Revisiones", Tier = "Tier 3 — IA",
                    Title = "Revisiones en curso",
                    Phrases = new[] { "revisiones activas", "revisiones en curso", "qué revisiones están activas" },
                    ResponseExample = "Tienes 2 revisiones en curso: 1. Revisión del módulo de pagos. 2. Revisión de entregable Q1.",
                    Endpoint = "GET /api/revisiones/activa"
                },
            }},

            new CommandModule { Name = "Local / Sistema", Commands = new()
            {
                new CommandItem
                {
                    Id = "LOC-001", Module = "Local / Sistema", Tier = "Tier 1",
                    Title = "Abrir aplicación",
                    Phrases = new[] { "abre chrome", "abre la calculadora", "abrir el explorador", "abre el bloc de notas" },
                    ResponseExample = "Acción OK: abierto chrome.",
                    Endpoint = "LOCAL — Process.Start(exe) — apps registradas en configuración"
                },
                new CommandItem
                {
                    Id = "LOC-002", Module = "Local / Sistema", Tier = "Tier 2",
                    Title = "Confirmar acción pendiente",
                    Phrases = new[] { "sí", "confirmar", "confirmo", "ok", "dale" },
                    ResponseExample = "Ejecuta la última acción que esperaba confirmación."
                },
                new CommandItem
                {
                    Id = "LOC-003", Module = "Local / Sistema", Tier = "Tier 2",
                    Title = "Cancelar acción pendiente",
                    Phrases = new[] { "no", "cancelar", "cancela", "negativo" },
                    ResponseExample = "Acción cancelada."
                },
            }},
        };
    }
}