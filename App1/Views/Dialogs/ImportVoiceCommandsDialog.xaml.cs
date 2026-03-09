using Anfeta.UI.Services.VoiceCommands;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anfeta.UI.Views.VoiceCommands;

public sealed partial class ImportVoiceCommandsDialog : ContentDialog
{
    private readonly VoiceCommandsRepository _repo;
    private readonly VoiceCommandEngine _engine;
    private readonly VoiceCommandsTextImportService _import;

    private List<VoiceCommand> _parsedCommands = new();

    public ImportVoiceCommandsDialog(
        VoiceCommandsRepository repo,
        VoiceCommandEngine engine,
        VoiceCommandsTextImportService import)
    {
        InitializeComponent();
        _repo = repo;
        _engine = engine;
        _import = import;

        PrimaryButtonClick += ImportVoiceCommandsDialog_PrimaryButtonClick;
    }

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var raw = InputBox.Text ?? "";
        var parsed = _import.Parse(raw);
        _parsedCommands = _import.BuildCommandsGroupedByToken(parsed);

        var skipped = parsed.SkippedLines.Count;
        PreviewText.Text =
            $"Detectados: {_parsedCommands.Count} tokens (comandos). " +
            $"Líneas ignoradas: {skipped}.";
    }

    private async void ImportVoiceCommandsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Si no analizaron, analizamos aquí
        if (_parsedCommands.Count == 0)
        {
            Analyze_Click(this, new RoutedEventArgs());
        }

        if (_parsedCommands.Count == 0)
        {
            // nada que importar
            return;
        }

        var replace = ReplaceRadio.IsChecked == true;

        if (replace)
        {
            await _repo.SaveAsync(_parsedCommands);
        }
        else
        {
            // MERGE: token existente -> agrega sinónimos nuevos
            var existing = await _repo.LoadAsync();
            var merged = MergeByToken(existing, _parsedCommands);
            await _repo.SaveAsync(merged);
        }

        await _engine.ReloadAsync();
    }

    private static List<VoiceCommand> MergeByToken(List<VoiceCommand> existing, List<VoiceCommand> incoming)
    {
        var byToken = existing
            .Where(c => !string.IsNullOrWhiteSpace(c.Token))
            .ToDictionary(c => c.Token, c => c, StringComparer.OrdinalIgnoreCase);

        foreach (var inc in incoming)
        {
            if (string.IsNullOrWhiteSpace(inc.Token)) continue;

            if (!byToken.TryGetValue(inc.Token, out var cur))
            {
                byToken[inc.Token] = inc;
                continue;
            }

            cur.IsEnabled = true;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in (cur.Synonyms ?? new List<string>())) set.Add(s);
            foreach (var s in (inc.Synonyms ?? new List<string>())) set.Add(s);

            // asegúrate que el token también pueda ser sinónimo si quieres:
            // set.Add(inc.Token);

            cur.Synonyms = set.OrderBy(x => x).ToList();
        }

        return byToken.Values
            .OrderBy(c => c.Token)
            .ToList();
    }
}