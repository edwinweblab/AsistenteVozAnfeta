using Anfeta.UI.Services.VoiceCommands;
using Anfeta.UI.Views.VoiceCommands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
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
    private readonly VoiceCommandsTextImportService _importService;
    private List<VoiceCommand> _pendingImported = new();
    public VoiceCommandsDialog(VoiceCommandsRepository repo, VoiceCommandEngine engine)
    {
        InitializeComponent();
        _repo = repo;
        _engine = engine;

        CommandsList.ItemsSource = _rows;
        _importService = App.AppHost.Services.GetRequiredService<VoiceCommandsTextImportService>();
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
    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        ListPanel.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Collapsed;
        ImportPanel.Visibility = Visibility.Visible;

        BtnAdd.Visibility = Visibility.Collapsed;
        BtnImport.Visibility = Visibility.Collapsed;

        BtnSaveEdit.Visibility = Visibility.Collapsed;
        BtnCancelEdit.Visibility = Visibility.Collapsed;
    }
    private void BtnImportAnalyze_Click(object sender, RoutedEventArgs e)
    {
        var raw = ImportBox.Text ?? "";
        var parsed = _importService.Parse(raw);
        _pendingImported = _importService.BuildCommandsGroupedByToken(parsed);

        var top = _pendingImported
        .Take(8)
        .Select(c => $"{c.Token}  ({c.Synonyms?.Count ?? 0} sinónimos)")
        .ToList();

        ImportPreviewText.Text =
            $"Detectados: {_pendingImported.Count} tokens. " +
            $"Líneas ignoradas: {parsed.SkippedLines.Count}.\n" +
            string.Join("\n", top) +
            (_pendingImported.Count > 8 ? "\n…" : "");
    }

    private async void BtnImportApply_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingImported.Count == 0)
            BtnImportAnalyze_Click(sender, e);

        if (_pendingImported.Count == 0) return;

        var replace = ImportReplace.IsChecked == true;

        if (replace)
        {
            await _repo.SaveAsync(_pendingImported);
        }
        else
        {
            var existing = await _repo.LoadAsync();
            var merged = MergeByToken(existing, _pendingImported);
            await _repo.SaveAsync(merged);
        }

        await _engine.ReloadAsync();

        // recarga UI
        await LoadAsync();

        ExitImportMode();
    }

    private void BtnImportCancel_Click(object sender, RoutedEventArgs e)
    {
        ExitImportMode();
    }

    private void ExitImportMode()
    {
        ImportPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;

        BtnAdd.Visibility = Visibility.Visible;
        BtnImport.Visibility = Visibility.Visible;

        ImportPreviewText.Text = "Aún no analizado.";
        ImportBox.Text = "";
        _pendingImported.Clear();
    }
    private static List<VoiceCommand> MergeByToken(
    List<VoiceCommand> existing,
    List<VoiceCommand> incoming)
    {
        // index por token (clave)
        var map = new Dictionary<string, VoiceCommand>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in existing ?? new List<VoiceCommand>())
        {
            if (string.IsNullOrWhiteSpace(e?.Token)) continue;
            if (!map.ContainsKey(e.Token))
                map[e.Token] = e;
        }

        foreach (var inc in incoming ?? new List<VoiceCommand>())
        {
            if (inc is null) continue;
            if (string.IsNullOrWhiteSpace(inc.Token)) continue;

            if (!map.TryGetValue(inc.Token, out var cur))
            {
                // nuevo token -> entra tal cual
                cur = new VoiceCommand
                {
                    Name = string.IsNullOrWhiteSpace(inc.Name) ? inc.Token : inc.Name,
                    Token = inc.Token,
                    IsEnabled = inc.IsEnabled,
                    Synonyms = new List<string>()
                };
                map[inc.Token] = cur;
            }

            // habilita si llega algo nuevo (import suele ser verdad)
            cur.IsEnabled = true;

            // merge synonyms
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (cur.Synonyms != null)
            {
                foreach (var s in cur.Synonyms)
                    AddSyn(set, s);
            }

            if (inc.Synonyms != null)
            {
                foreach (var s in inc.Synonyms)
                    AddSyn(set, s);
            }

            // opcional: asegura que el token también sea un "match" por voz
            // (si NO lo quieres como sinónimo, comenta esta línea)
            AddSyn(set, inc.Token);

            cur.Synonyms = set
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return map.Values
            .OrderBy(c => c.Token, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static void AddSyn(HashSet<string> set, string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (s.Length == 0) return;
            set.Add(s);
        }
    }

}