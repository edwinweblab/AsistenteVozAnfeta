using Anfeta.UI.Data;
using Anfeta.UI.Models;
using Anfeta.UI.Services;
using Anfeta.UI.Services.Interpretation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace Anfeta.UI.ViewModels
{
    public sealed partial class AllowedAppsViewModel : ObservableObject
    {
        private readonly LocalAppsRepository _repo;
        private readonly CapabilityRegistry _registry;
        private readonly InstalledAppsScanner _scanner;
        private readonly IFilePickerService _filePicker;

        public ObservableCollection<LocalAppEntry> AllowedApps { get; } = new();

        [ObservableProperty] private bool isLoading;
        [ObservableProperty] private string status = "Listo.";

        public AllowedAppsViewModel(
            LocalAppsRepository repo,
            CapabilityRegistry registry,
            InstalledAppsScanner scanner,
            IFilePickerService filePicker)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        }

        [RelayCommand]
        public async Task LoadAsync()
        {
            try
            {
                IsLoading = true;
                Status = "Cargando apps...";

                var apps = await Task.Run(() => _repo.GetAll());

                AllowedApps.Clear();
                foreach (var a in apps)
                    AllowedApps.Add(a);

                Status = $"Cargadas: {AllowedApps.Count}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsVM] LoadAsync ERROR: " + ex);
                Status = "Error cargando apps.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task RescanAsync()
        {
            try
            {
                IsLoading = true;
                Status = "Escaneando accesos directos del Menú Inicio...";

                var detected = await Task.Run(() => _scanner.ScanStartMenuShortcuts());

                await Task.Run(() =>
                {
                    foreach (var app in detected)
                    {
                        _repo.UpsertDetectedAppSafe(app);
                    }
                });

                await LoadAsync();
                _registry.Reload();

                Status = $"Escaneo completo. Detectadas: {detected.Count}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsVM] RescanAsync ERROR: " + ex);
                Status = "Error escaneando apps.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public void ToggleEnabled(LocalAppEntry app)
        {
            if (app == null) return;

            try
            {
                _repo.SetEnabled(app.AppKey, app.Enabled);
                _registry.Reload();
                Status = $"{app.FriendlyName}: {(app.Enabled ? "habilitada" : "deshabilitada")}.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsVM] ToggleEnabled ERROR: " + ex);
                Status = "Error cambiando estado.";
            }
        }

        [RelayCommand]
        public async Task AddManualAsync()
        {
            try
            {
                IsLoading = true;
                Status = "Selecciona un .exe...";

                var path = await _filePicker.PickExePathAsync();
                if (string.IsNullOrWhiteSpace(path))
                {
                    Status = "Cancelado.";
                    return;
                }

                if (!File.Exists(path))
                {
                    Status = "El archivo no existe.";
                    return;
                }

                var exeName = Path.GetFileName(path);
                var baseName = Path.GetFileNameWithoutExtension(path);
                var friendlyName = baseName;
                var appKey = MakeUniqueKey(baseName);

                var entry = new LocalAppEntry
                {
                    AppKey = appKey,
                    FriendlyName = friendlyName,
                    Category = "manual",
                    ExecutableName = exeName,
                    ExecutablePath = path,
                    Enabled = false,
                    Source = "manual"
                };

                await Task.Run(() => _repo.UpsertApp(entry));

                await LoadAsync();
                _registry.Reload();

                Status = $"Agregada: {friendlyName} (deshabilitada por defecto)";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsVM] AddManualAsync ERROR: " + ex);
                Status = "Error agregando app manual.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private string MakeUniqueKey(string baseText)
        {
            var keyBase = (baseText ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(keyBase)) keyBase = "app";

            var key = keyBase;
            var n = 2;

            while (_repo.ExistsAppKey(key))
            {
                key = $"{keyBase}-{n}";
                n++;
            }

            return key;
        }

        public async Task OpenSynonymsDialogAsync(LocalAppEntry app, XamlRoot xamlRoot)
        {
            try
            {
                if (app == null || xamlRoot == null)
                {
                    Status = "No se pudo abrir el diálogo.";
                    return;
                }

                var current = await Task.Run(() => _repo.GetSynonyms(app.AppKey));

                var items = new ObservableCollection<string>(
                    current.Select(s => (s ?? "").Trim().ToLowerInvariant())
                           .Where(s => !string.IsNullOrWhiteSpace(s))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                );

                bool isEditing = false;
                string editingOriginal = null;

                var title = new TextBlock
                {
                    Text = "Editar sinónimos",
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White)
                };

                var subtitle = new TextBlock
                {
                    Text = "Agrega palabras o frases que Anfeta reconocerá para abrir esta aplicación.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)),
                    TextWrapping = TextWrapping.WrapWholeWords
                };

                var appCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(255, 11, 18, 32)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14)
                };

                var appRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10
                };

                var appIcon = new FontIcon
                {
                    Glyph = "\uE71D",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var appIconBox = new Border
                {
                    Width = 36,
                    Height = 36,
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Child = appIcon
                };

                var appTextStack = new StackPanel
                {
                    Spacing = 2
                };

                appTextStack.Children.Add(new TextBlock
                {
                    Text = app.FriendlyName,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White)
                });

                appTextStack.Children.Add(new TextBlock
                {
                    Text = app.ExecutableName,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184))
                });

                appRow.Children.Add(appIconBox);
                appRow.Children.Add(appTextStack);
                appCard.Child = appRow;

                var labelInput = new TextBlock
                {
                    Text = "Nuevo sinónimo",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 203, 213, 225))
                };

                var input = new TextBox
                {
                    PlaceholderText = "Ejemplo: abre chrome",
                    MinWidth = 320
                };

                var btnAddOrSave = new Button
                {
                    Content = "Agregar",
                    MinWidth = 100
                };

                var btnEdit = new Button
                {
                    Content = "Editar",
                    IsEnabled = false,
                    MinWidth = 90
                };

                var btnDelete = new Button
                {
                    Content = "Eliminar",
                    IsEnabled = false,
                    MinWidth = 90
                };

                var btnCancelEdit = new Button
                {
                    Content = "Cancelar edición",
                    IsEnabled = false,
                    MinWidth = 130
                };

                var labelList = new TextBlock
                {
                    Text = "Sinónimos actuales",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 203, 213, 225))
                };

                var list = new ListView
                {
                    ItemsSource = items,
                    SelectionMode = ListViewSelectionMode.Single,
                    MaxHeight = 220
                };

                void SetEditMode(bool enabled, string original = null)
                {
                    isEditing = enabled;
                    editingOriginal = original;
                    btnAddOrSave.Content = enabled ? "Guardar cambio" : "Agregar";
                    btnCancelEdit.IsEnabled = enabled;
                }

                void AddOrUpdateSynonym()
                {
                    var value = (input.Text ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(value))
                        return;

                    if (!isEditing)
                    {
                        if (items.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                            return;

                        items.Add(value);
                        input.Text = "";
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(editingOriginal))
                        return;

                    if (items.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) &&
                        !string.Equals(editingOriginal, value, StringComparison.OrdinalIgnoreCase))
                        return;

                    var idx = items.IndexOf(editingOriginal);
                    if (idx >= 0)
                        items[idx] = value;

                    input.Text = "";
                    SetEditMode(false);
                }

                list.SelectionChanged += (_, __) =>
                {
                    var hasSelection = list.SelectedItem is string;
                    btnEdit.IsEnabled = hasSelection;
                    btnDelete.IsEnabled = hasSelection;
                };

                btnAddOrSave.Click += (_, __) => AddOrUpdateSynonym();

                btnEdit.Click += (_, __) =>
                {
                    if (list.SelectedItem is not string selected) return;

                    input.Text = selected;
                    input.SelectAll();
                    input.Focus(FocusState.Programmatic);
                    SetEditMode(true, selected);
                };

                btnDelete.Click += (_, __) =>
                {
                    if (list.SelectedItem is not string selected) return;

                    if (isEditing && string.Equals(editingOriginal, selected, StringComparison.OrdinalIgnoreCase))
                    {
                        input.Text = "";
                        SetEditMode(false);
                    }

                    items.Remove(selected);
                };

                btnCancelEdit.Click += (_, __) =>
                {
                    input.Text = "";
                    SetEditMode(false);
                };

                input.KeyDown += (_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter)
                        AddOrUpdateSynonym();
                };

                list.DoubleTapped += (_, __) =>
                {
                    if (list.SelectedItem is not string selected) return;

                    input.Text = selected;
                    input.SelectAll();
                    input.Focus(FocusState.Programmatic);
                    SetEditMode(true, selected);
                };

                var inputRow = new Grid
                {
                    ColumnSpacing = 8
                };

                inputRow.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
                inputRow.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

                Grid.SetColumn(input, 0);
                Grid.SetColumn(btnAddOrSave, 1);

                inputRow.Children.Add(input);
                inputRow.Children.Add(btnAddOrSave);

                var actionRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };

                actionRow.Children.Add(btnEdit);
                actionRow.Children.Add(btnDelete);
                actionRow.Children.Add(btnCancelEdit);

                var rootPanel = new StackPanel
                {
                    Spacing = 14,
                    Width = 430
                };

                rootPanel.Children.Add(title);
                rootPanel.Children.Add(subtitle);
                rootPanel.Children.Add(appCard);
                rootPanel.Children.Add(labelInput);
                rootPanel.Children.Add(inputRow);
                rootPanel.Children.Add(actionRow);
                rootPanel.Children.Add(labelList);
                rootPanel.Children.Add(list);

                var dialog = new ContentDialog
                {
                    Title = "",
                    Content = rootPanel,
                    PrimaryButtonText = "Guardar",
                    CloseButtonText = "Cerrar",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    var clean = items
                        .Select(s => (s ?? "").Trim().ToLowerInvariant())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    await Task.Run(() => _repo.ReplaceSynonyms(app.AppKey, clean));

                    _registry.Reload();
                    Status = $"Sinónimos guardados para {app.FriendlyName}.";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AllowedAppsVM] OpenSynonymsDialogAsync ERROR: " + ex);
                Status = "Error editando sinónimos.";
            }
        }
    }
}