using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Helpers
{
    public sealed record ProjectSuffixInfo(
        string Domain,
        string Suffix,
        string FullProjectName,
        string LocalFolderName,
        string CategoryLabel,
        bool IsStandardSuffix);

    public static class ProjectSuffixHelper
    {
        // Sufijos estándar pedidos por John para Weblab/ANFETA
        public const string SUFFIX_WEBS = "webs";
        public const string SUFFIX_ADS = "ads";
        public const string SUFFIX_CEO = "ceo"; // SEO
        public const string SUFFIX_AUDITORIA = "auditoria";
        public const string SUFFIX_COTIZACION = "cotizacion";
        public const string SUFFIX_PREPROYECTO = "preproyecto";
        public const string SUFFIX_SOFTWARE = "software";
        public const string SUFFIX_APLICACION = "aplicacion";

        public static readonly IReadOnlyDictionary<string, string> SuffixLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { SUFFIX_WEBS, "🌐 Desarrollo Web" },
                { SUFFIX_ADS, "📢 Campañas Ads" },
                { SUFFIX_CEO, "🔍 Posicionamiento SEO" },
                { "seo", "🔍 Posicionamiento SEO" },
                { SUFFIX_AUDITORIA, "📋 Auditoría" },
                { "auditoría", "📋 Auditoría" },
                { SUFFIX_COTIZACION, "💰 Cotización" },
                { "cotización", "💰 Cotización" },
                { SUFFIX_PREPROYECTO, "🚀 Pre-Proyecto / Onboarding" },
                { "pre-proyecto", "🚀 Pre-Proyecto / Onboarding" },
                { SUFFIX_SOFTWARE, "💻 Herramientas / Software" },
                { SUFFIX_APLICACION, "📱 Aplicación" },
                { "aplicación", "📱 Aplicación" }
            };

        // Regex para capturar {dominio.com}.{sufijo} o {dominio.com.mx}.{sufijo}
        private static readonly Regex ProjectSuffixRegex = new(
            @"(?<![\w@])(?<domain>[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|com|mx|org|net|io|co|app|dev))(?:\.(?<suffix>webs?|ads?|ceo|seo|auditor[ií]a|cotizaci[oó]n|preproyecto|pre-proyecto|software|aplicaci[oó]n|apli))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static ProjectSuffixInfo Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ProjectSuffixInfo(
                    Domain: "anfeta.com",
                    Suffix: string.Empty,
                    FullProjectName: "anfeta.com",
                    LocalFolderName: "anfeta.com.Carpeta",
                    CategoryLabel: "General",
                    IsStandardSuffix: false);
            }

            var match = ProjectSuffixRegex.Match(text);
            if (!match.Success)
            {
                var clean = text.Trim();
                return new ProjectSuffixInfo(
                    Domain: clean,
                    Suffix: string.Empty,
                    FullProjectName: clean,
                    LocalFolderName: $"{clean}.Carpeta",
                    CategoryLabel: "General",
                    IsStandardSuffix: false);
            }

            var domain = match.Groups["domain"].Value.ToLowerInvariant();
            var rawSuffix = match.Groups["suffix"].Value.ToLowerInvariant();
            var normalizedSuffix = NormalizeSuffix(rawSuffix);

            var category = SuffixLabels.TryGetValue(normalizedSuffix, out var label)
                ? label
                : (string.IsNullOrEmpty(normalizedSuffix) ? "General" : normalizedSuffix);

            var fullProject = string.IsNullOrEmpty(normalizedSuffix)
                ? domain
                : $"{domain}.{normalizedSuffix}";

            // Carpeta en Dropbox DRX: Si tiene sufijo estándar puede ser {dominio}.{sufijo} o dentro de {dominio}.Carpeta
            var folderName = string.IsNullOrEmpty(normalizedSuffix)
                ? $"{domain}.Carpeta"
                : $"{domain}.{normalizedSuffix}";

            return new ProjectSuffixInfo(
                Domain: domain,
                Suffix: normalizedSuffix,
                FullProjectName: fullProject,
                LocalFolderName: folderName,
                CategoryLabel: category,
                IsStandardSuffix: !string.IsNullOrEmpty(normalizedSuffix));
        }

        public static string NormalizeSuffix(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
                return string.Empty;

            var s = suffix.Trim().ToLowerInvariant();
            return s switch
            {
                "web" or "webs" => SUFFIX_WEBS,
                "ad" or "ads" => SUFFIX_ADS,
                "seo" or "ceo" => SUFFIX_CEO,
                "auditoria" or "auditoría" => SUFFIX_AUDITORIA,
                "cotizacion" or "cotización" => SUFFIX_COTIZACION,
                "preproyecto" or "pre-proyecto" => SUFFIX_PREPROYECTO,
                "software" or "prog" or "pprog" or "pro" => SUFFIX_SOFTWARE,
                "aplicacion" or "aplicación" or "apli" or "aapli" => SUFFIX_APLICACION,
                _ => s
            };
        }

        public static string FormatProjectName(string domain, string suffix)
        {
            var d = (domain ?? string.Empty).Trim().ToLowerInvariant();
            var s = NormalizeSuffix(suffix);
            return string.IsNullOrEmpty(s) ? d : $"{d}.{s}";
        }

        /// <summary>
        /// Resuelve la ruta física en Dropbox local para el dominio y sufijo especificado,
        /// buscando compatibilidad con DRX/{dominio}.{sufijo} o DRX/{dominio}.Carpeta/{sufijo}.
        /// </summary>
        public static string ResolveDropboxFolderPath(string drxRoot, string domain, string? suffix = null)
        {
            if (string.IsNullOrWhiteSpace(drxRoot) || !Directory.Exists(drxRoot))
                return string.Empty;

            var normSuffix = NormalizeSuffix(suffix);
            var domainPrefix = domain.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
                ? domain[..^4]
                : domain;

            // 1. Si hay sufijo, buscar carpeta exacta: DRX/{dominio}.{sufijo}
            if (!string.IsNullOrEmpty(normSuffix))
            {
                var candidateExact = Path.Combine(drxRoot, $"{domain}.{normSuffix}");
                if (Directory.Exists(candidateExact)) return candidateExact;

                var candidateExactPrefix = Path.Combine(drxRoot, $"{domainPrefix}.{normSuffix}");
                if (Directory.Exists(candidateExactPrefix)) return candidateExactPrefix;
            }

            // 2. Buscar en la carpeta general del dominio: DRX/{dominio}.Carpeta
            var expectedFolderName = $"{domain}.Carpeta";
            var existingDir = Directory.EnumerateDirectories(drxRoot)
                .FirstOrDefault(d =>
                {
                    var dirName = Path.GetFileName(d);
                    if (string.Equals(dirName, expectedFolderName, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (dirName.EndsWith(".Carpeta", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleanDirBase = dirName[..^".Carpeta".Length];
                        if (string.Equals(cleanDirBase, domain, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(cleanDirBase, domainPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    return false;
                });

            if (!string.IsNullOrEmpty(existingDir))
            {
                // Si hay sufijo y existe una subcarpeta interna con ese sufijo, retornarla
                if (!string.IsNullOrEmpty(normSuffix))
                {
                    var sub = Path.Combine(existingDir, normSuffix);
                    if (Directory.Exists(sub)) return sub;
                }
                return existingDir;
            }

            // 3. Fallback: la ruta predeterminada esperada
            return !string.IsNullOrEmpty(normSuffix)
                ? Path.Combine(drxRoot, $"{domain}.{normSuffix}")
                : Path.Combine(drxRoot, expectedFolderName);
        }
    }
}
