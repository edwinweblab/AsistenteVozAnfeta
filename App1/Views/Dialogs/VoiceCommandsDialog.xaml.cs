using Anfeta.UI.Services.VoiceCommands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Anfeta.UI.Views.Dialogs;

public sealed partial class VoiceCommandsDialog : ContentDialog
{
    private readonly VoiceCommandsRepository _repo;
    private readonly VoiceCommandEngine _engine;
    private readonly ObservableCollection<VoiceCommandRow> _rows = new();
    private VoiceCommandRow? _editingRow;
    private readonly TokenGenerator _tokenGen = new();

    public VoiceCommandsDialog(VoiceCommandsRepository repo, VoiceCommandEngine engine)
    {
        InitializeComponent();
        _repo = repo;
        _engine = engine;

        CommandsList.ItemsSource = _rows;

        Loaded += async (_, __) => await LoadAsync();
        PrimaryButtonClick += async (_, __) => await SaveAsync();
    }

    private async Task LoadAsync()
    {
        _rows.Clear();
        var items = await _repo.LoadAsync();

        foreach (var c in items)
            _rows.Add(VoiceCommandRow.From(c));
    }

    private async Task SaveAsync()
    {
        var items = _rows.Select(r => r.ToModel()).ToList();
        await _repo.SaveAsync(items);

        // refresca engine para que quede activo al instante
        await _engine.ReloadAsync();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var row = new VoiceCommandRow
        {
            Name = "",
            Token = "",
            Synonyms = "",
            IsEnabled = true
        };

        _rows.Add(row);
        ShowEditor(row);
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is VoiceCommandRow row)
            _rows.Remove(row);
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is VoiceCommandRow row)
            ShowEditor(row);
    }
    private void BtnSaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is null) return;

        _editingRow.Name = EditNameBox.Text?.Trim() ?? "";
        _editingRow.Synonyms = EditSynonymsBox.Text ?? "";

        var existing = _rows
            .Where(r => !ReferenceEquals(r, _editingRow))
            .Select(r => r.Token)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        _editingRow.Token = _tokenGen.Generate(_editingRow.Name, existing);
        EditTokenBox.Text = _editingRow.Token;

        // refrescar lista
        CommandsList.ItemsSource = null;
        CommandsList.ItemsSource = _rows;

        HideEditor();
    }

    private void BtnCancelEdit_Click(object sender, RoutedEventArgs e)
    {
        HideEditor();
    }

    // Row simple para UI
    private sealed class VoiceCommandRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Token { get; set; } = "";
        public string Synonyms { get; set; } = "";
        public bool IsEnabled { get; set; } = true;

        public string SynonymsText => Synonyms;

        public static VoiceCommandRow From(VoiceCommand c) => new()
        {
            Id = c.Id,
            Name = c.Name,
            Token = c.Token,
            Synonyms = string.Join(", ", c.Synonyms ?? new()),
            IsEnabled = c.IsEnabled
        };

        public VoiceCommand ToModel()
        {
            var syns = (Synonyms ?? "")
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();

            return new VoiceCommand
            {
                Id = string.IsNullOrWhiteSpace(Id) ? System.Guid.NewGuid().ToString("N") : Id,
                Name = Name ?? "",
                Token = Token ?? "",
                Synonyms = syns,
                IsEnabled = IsEnabled,
                UpdatedAtUtc = System.DateTime.UtcNow
            };
        }
    
    }
    private void ShowEditor(VoiceCommandRow row)
    {
        _editingRow = row;

        EditNameBox.Text = row.Name ?? "";
        EditSynonymsBox.Text = row.Synonyms ?? "";

        // Si ya tenía token, lo respetamos; si no, generamos
        if (string.IsNullOrWhiteSpace(row.Token))
        {
            var existing = _rows.Where(r => !ReferenceEquals(r, row))
                                .Select(r => r.Token)
                                .Where(t => !string.IsNullOrWhiteSpace(t));

            row.Token = _tokenGen.Generate(row.Name, existing);
        }

        EditTokenBox.Text = row.Token ?? "";

        ListPanel.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Visible;

        BtnAdd.Visibility = Visibility.Collapsed;
        BtnSaveEdit.Visibility = Visibility.Visible;
        BtnCancelEdit.Visibility = Visibility.Visible;
    }

    private void HideEditor()
    {
        _editingRow = null;

        EditNameBox.Text = "";
        EditSynonymsBox.Text = "";
        EditTokenBox.Text = "";

        EditPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;

        BtnSaveEdit.Visibility = Visibility.Collapsed;
        BtnCancelEdit.Visibility = Visibility.Collapsed;
        BtnAdd.Visibility = Visibility.Visible;
    }
}