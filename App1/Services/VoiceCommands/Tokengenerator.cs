using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Anfeta.UI.Services.VoiceCommands
{
    /// <summary>
    /// Genera token estilo "tartamudo":
    /// Primera letra duplicada + 4 siguientes letras limpias.
    /// Ej: Abrir → AAbri
    /// </summary>
    public sealed class TokenGenerator
    {
        public string Generate(string? name, IEnumerable<string>? existingTokens = null)
        {
            var baseToken = MakeBase(name); // AAbri

            if (existingTokens == null) return baseToken;

            var set = new HashSet<string>(existingTokens.Where(t => !string.IsNullOrWhiteSpace(t)),
                                          StringComparer.OrdinalIgnoreCase);

            if (!set.Contains(baseToken)) return baseToken;

            // Si choca, agrega número AL FINAL (AAbri2, AAbri3...)
            for (int i = 2; i < 1000; i++)
            {
                var candidate = baseToken + i.ToString();
                if (!set.Contains(candidate)) return candidate;
            }

            return baseToken; // fallback
        }

        private static string MakeBase(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "TToke";

            var clean = new string(name.Trim().Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(clean)) clean = "Token";

            clean = clean.Length >= 5 ? clean.Substring(0, 5) : clean;

            var first = char.ToUpperInvariant(clean[0]);
            var rest = clean.Length > 1 ? clean.Substring(1) : "";

            // primera letra duplicada + resto, y recorte total a 5
            var token = (new string(first, 2) + rest);
            return token.Length > 5 ? token.Substring(0, 5) : token;
        }
    }
}