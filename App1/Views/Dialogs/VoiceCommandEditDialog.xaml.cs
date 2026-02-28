using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;
using Anfeta.UI.Services.VoiceCommands;
using Anfeta.UI.Views.Dialogs;

namespace Anfeta.UI.Views.Dialogs;

public sealed partial class VoiceCommandEditDialog : ContentDialog
{
    private readonly TokenGenerator _tokenGen = new(); // simple por ahora
    private readonly string[] _existingTokens;

    public string NameValue => NameBox.Text?.Trim() ?? "";
    public string TokenValue => TokenBox.Text?.Trim() ?? "";
    public string SynonymsValue => SynonymsBox.Text ?? "";

    public VoiceCommandEditDialog(string name, string token, string synonyms, string[] existingTokens)
    {
        InitializeComponent();
        _existingTokens = existingTokens ?? Array.Empty<string>();

        NameBox.Text = name ?? "";
        SynonymsBox.Text = synonyms ?? "";

        // Si ya tenía token, respétalo; si no, genera
        TokenBox.Text = string.IsNullOrWhiteSpace(token)
            ? _tokenGen.Generate(NameBox.Text, _existingTokens)
            : token;

        NameBox.TextChanged += (_, __) =>
        {
            // Regenerar token solo si estaba vacío o si quieres que siempre regenere:
            TokenBox.Text = _tokenGen.Generate(NameBox.Text, _existingTokens);
        };
    }
}