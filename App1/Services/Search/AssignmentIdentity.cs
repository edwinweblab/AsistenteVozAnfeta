using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Anfeta.UI.Services.Search;

public static class AssignmentIdentity
{
    public static string Normalize(string? value)
    {
        var clean = (value ?? "").Trim().ToLowerInvariant();
        if (Guid.TryParse(clean, out var id)) return id.ToString("N");
        // Alias del usuario de Notion observado en este espacio. No inferir
        // identidad por el prefijo de correos de cualquier dominio.
        if (clean == "nnetf@practicante.com") return "nneft";
        if (clean.Contains('@')) return "email:" + clean;
        var key = new string(clean.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
        return key switch
        {
            "nnetf" or "neftali" or "nneft" => "nneft",
            "iisai" or "iisaia" or "isaias" => "iisaia",
            "john" => "jjohn", "karla" => "kkarl", "brian" => "bbria",
            "genaro" => "ggena", "emmanuel" => "eemma", "andrade" => "aandr",
            "sotelo" => "eedua", "acalli" => "aacal",
            _ => key
        };
    }
}
