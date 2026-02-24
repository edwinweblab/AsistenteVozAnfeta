using Anfeta.UI.Data;
using Anfeta.UI.Models;
using Anfeta.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Anfeta.UI.ViewModels
{
    public sealed partial class AllowedAppsViewModel : ObservableObject
    {
        private readonly LocalAppsRepository _repo;
        private readonly CapabilityRegistry _registry;

        public ObservableCollection<LocalAppEntry> AllowedApps { get; } = new();

        [ObservableProperty] private bool isLoading;
        [ObservableProperty] private string status = "Listo.";

        public AllowedAppsViewModel(LocalAppsRepository repo, CapabilityRegistry registry)
        {
            _repo = repo;
            _registry = registry;
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
        public void ToggleEnabled(LocalAppEntry app)
        {
            if (app == null) return;

            try
            {
                _repo.SetEnabled(app.AppKey, app.Enabled);

                // Recargar registry para reflejar cambios en runtime
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
            // Lo dejamos listo para después (file picker)
            await Task.CompletedTask;
            Status = "Agregar manual: pendiente.";
        }

        // ===============================
        // DIALOG: EDITAR SINÓNIMOS
        // ===============================
        public async Task OpenSynonymsDialogAsync(LocalAppEntry app, XamlRoot xamlRoot)
        {
            try
            {
                if (app == null) return;

                // cargar sinónimos actuales
                var current = await Task.Run(() => _repo.GetSynonyms(app.AppKey));
                var items = new ObservableCollection<string>(
                    current.Select(s => (s ?? "").Trim())
                           .Where(s => !string.IsNullOrWhiteSpace(s))
                );

                // estado edición
                bool isEditing = false;
                string? editingOriginal = null;

                // UI controls (todo dentro del mismo dialog)
                var input = new TextBox
                {
                    PlaceholderText = "Escribe un sinónimo (ej: navegador, office, etc.)",
                    MinWidth = 340
                };

                var btnAddOrSave = new Button
                {
                    Content = "Agregar",
                    Margin = new Thickness(8, 0, 0, 0)
                };

                var list = new ListView
                {
                    ItemsSource = items,
                    SelectionMode = ListViewSelectionMode.Single,
                    Margin = new Thickness(0, 10, 0, 0)
                };

                var btnEdit = new Button
                {
                    Content = "Editar",
                    IsEnabled = false,
                    Margin = new Thickness(0, 10, 8, 0)
                };

                var btnDel = new Button
                {
                    Content = "Eliminar",
                    IsEnabled = false,
                    Margin = new Thickness(0, 10, 0, 0)
                };

                var btnCancelEdit = new Button
                {
                    Content = "Cancelar edición",
                    IsEnabled = false,
                    Margin = new Thickness(8, 10, 0, 0)
                };

                void SetEditMode(bool enabled, string? original = null)
                {
                    isEditing = enabled;
                    editingOriginal = original;

                    btnAddOrSave.Content = enabled ? "Guardar" : "Agregar";
                    btnCancelEdit.IsEnabled = enabled;
                }

                void NormalizeAndAddOrEdit()
                {
                    var v = (input.Text ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(v)) return;

                    if (!isEditing)
                    {
                        // agregar
                        if (items.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) return;
                        items.Add(v);
                        input.Text = "";
                        return;
                    }

                    // editar
                    if (string.IsNullOrWhiteSpace(editingOriginal)) return;

                    // evitar duplicados (excepto si es el mismo)
                    if (items.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)) &&
                        !string.Equals(editingOriginal, v, StringComparison.OrdinalIgnoreCase))
                        return;

                    var idx = items.IndexOf(editingOriginal);
                    if (idx < 0) return;

                    items[idx] = v;
                    input.Text = "";
                    SetEditMode(false);
                }

                // Habilitar botones según selección
                list.SelectionChanged += (_, __) =>
                {
                    var has = list.SelectedItem != null;
                    btnEdit.IsEnabled = has;
                    btnDel.IsEnabled = has;
                };

                // Agregar o Guardar edición (mismo botón)
                btnAddOrSave.Click += (_, __) => NormalizeAndAddOrEdit();

                // Enter agrega/guarda
                input.KeyDown += (_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter)
                        NormalizeAndAddOrEdit();
                };

                // Editar (inline)
                btnEdit.Click += (_, __) =>
                {
                    if (list.SelectedItem is not string selected) return;

                    input.Text = selected;
                    input.Focus(FocusState.Programmatic);
                    SetEditMode(true, selected);
                };

                // Cancelar edición
                btnCancelEdit.Click += (_, __) =>
                {
                    input.Text = "";
                    SetEditMode(false);
                };

                // Eliminar
                btnDel.Click += (_, __) =>
                {
                    if (list.SelectedItem is not string selected) return;

                    // si estaban editando el mismo, cancelar
                    if (isEditing && string.Equals(editingOriginal, selected, StringComparison.OrdinalIgnoreCase))
                    {
                        input.Text = "";
                        SetEditMode(false);
                    }

                    items.Remove(selected);
                };

                // Doble click = editar
                list.DoubleTapped += (_, __) =>
                {
                    if (list.SelectedItem is not string selected) return;

                    input.Text = selected;
                    input.Focus(FocusState.Programmatic);
                    SetEditMode(true, selected);
                };

                // Layout dialog
                var header = new TextBlock
                {
                    Text = "Sinónimos actuales",
                    FontSize = 12
                };

                var inputRow = new Grid { ColumnSpacing = 8 };
                inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(input, 0);
                Grid.SetColumn(btnAddOrSave, 1);
                inputRow.Children.Add(input);
                inputRow.Children.Add(btnAddOrSave);

                var actionRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                actionRow.Children.Add(btnEdit);
                actionRow.Children.Add(btnDel);
                actionRow.Children.Add(btnCancelEdit);

                var root = new StackPanel();
                root.Children.Add(header);
                root.Children.Add(inputRow);
                root.Children.Add(actionRow);
                root.Children.Add(list);

                var dialog = new ContentDialog
                {
                    Title = $"Sinónimos: {app.FriendlyName}",
                    Content = root,
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