using System;
using System.Text;
using System.Text.Json;

namespace Anfeta.UI.Helpers
{
    /// <summary>
    /// Decodifica claims del payload de un JWT local.
    /// NO valida firma — solo lectura de datos ya autenticados y guardados localmente.
    /// </summary>
    public static class JwtHelper
    {
        /// Extrae el email del payload JWT.
        /// Entrada: token JWT string. Salida: email o null si no se puede leer.
        public static string? GetEmail(string? token)
        {
            var payload = DecodePayload(token);
            if (payload == null) return null;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                return doc.RootElement.TryGetProperty("email", out var el)
                    ? el.GetString()
                    : null;
            }
            catch { return null; }
        }

        /// Extrae el uid del payload JWT.
        /// Entrada: token JWT string. Salida: uid o null si no se puede leer.
        public static string? GetUid(string? token)
        {
            var payload = DecodePayload(token);
            if (payload == null) return null;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                return doc.RootElement.TryGetProperty("uid", out var el)
                    ? el.GetString()
                    : null;
            }
            catch { return null; }
        }

        /// Decodifica el segmento del payload (base64url → UTF-8 JSON).
        private static string? DecodePayload(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) return null;

                var b64 = parts[1]
                    .Replace('-', '+')
                    .Replace('_', '/');

                switch (b64.Length % 4)
                {
                    case 2: b64 += "=="; break;
                    case 3: b64 += "="; break;
                }

                var bytes = Convert.FromBase64String(b64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }
}