// Services/Activity/StringSimilarity.cs
using System;

namespace Anfeta.UI.Services.Activity
{
    /// <summary>
    /// Calcula similitud entre strings usando Levenshtein Distance
    /// </summary>
    public static class StringSimilarity
    {
        /// <summary>
        /// Calcula distancia de Levenshtein entre dos strings
        /// Retorna: número de cambios necesarios para transformar s1 en s2
        /// </summary>
        public static int LevenshteinDistance(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
            if (string.IsNullOrEmpty(s2)) return s1.Length;

            var len1 = s1.Length;
            var len2 = s2.Length;
            var d = new int[len1 + 1, len2 + 1];

            for (var i = 0; i <= len1; i++)
                d[i, 0] = i;

            for (var j = 0; j <= len2; j++)
                d[0, j] = j;

            for (var i = 1; i <= len1; i++)
            {
                for (var j = 1; j <= len2; j++)
                {
                    var cost = (s2[j - 1] == s1[i - 1]) ? 0 : 1;

                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost
                    );
                }
            }

            return d[len1, len2];
        }

        /// <summary>
        /// Calcula similitud entre 0.0 (completamente diferente) y 1.0 (idéntico)
        /// </summary>
        public static double Similarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                return 1.0;

            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;

            var distance = LevenshteinDistance(s1.ToLowerInvariant(), s2.ToLowerInvariant());
            var maxLen = Math.Max(s1.Length, s2.Length);

            return 1.0 - ((double)distance / maxLen);
        }

        /// <summary>
        /// Verifica si dos nombres son "suficientemente similares"
        /// Umbral: 0.8 (80% de similitud)
        /// </summary>
        public static bool AreSimilar(string name1, string name2, double threshold = 0.8)
        {
            return Similarity(name1, name2) >= threshold;
        }
    }
}