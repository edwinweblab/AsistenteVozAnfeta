using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Anfeta.UI.Models.Weblab;

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
        public static string CleanProjectDomain(string rawDomain, out string detectedType) =>
            SearchResultRow.CleanProjectDomain(rawDomain, out detectedType);

        public static string ExtractDomain(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var match = ProjectSuffixRegex.Match(text);
            return match.Success ? match.Groups["domain"].Value.ToLowerInvariant() : string.Empty;
        }

        // Sufijos y categorías estándar pedidos por John para Weblab/ANFETA
        public const string SUFFIX_WEBS = "webs";
        public const string SUFFIX_ADS = "ads";
        public const string SUFFIX_CEO = "ceo"; // SEO
        public const string SUFFIX_AUDITORIA = "auditoria";
        public const string SUFFIX_COTIZACION = "cotizacion";
        public const string SUFFIX_PREPROYECTO = "preproyecto";
        public const string SUFFIX_SOFTWARE = "software";
        public const string SUFFIX_APLICACION = "aplicacion";

        // Categorías preestablecidas para PROYECTO (John Shaw)
        public static readonly IReadOnlyList<string> ProjectCategories = new[]
        {
            "cotizacion",
            "disenos",
            "facebook",
            "instagram",
            "linkedin",
            "proyecto",
            "presentacion",
            "seo",
            "tiktok",
            "webs",
            "biblioteca"
        };

        // Categorías preestablecidas para SOFTWARE (John Shaw)
        public static readonly IReadOnlyList<string> SoftwareCategories = new[]
        {
            "instalador",
            "tutoriales",
            "cliente",
            "software"
        };

        public static readonly IReadOnlyDictionary<string, string> CategoryDisplayLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "cotizacion", "💰 Cotización" },
                { "disenos", "🎨 Diseños" },
                { "facebook", "📘 Facebook" },
                { "instagram", "📸 Instagram" },
                { "linkedin", "💼 LinkedIn" },
                { "proyecto", "📁 Proyecto General" },
                { "presentacion", "📊 Presentación" },
                { "seo", "🔍 SEO" },
                { "tiktok", "🎵 TikTok" },
                { "webs", "🌐 Webs" },
                { "biblioteca", "📚 Biblioteca" },
                { "instalador", "💿 Instalador / Instalación" },
                { "tutoriales", "🎓 Tutoriales" },
                { "cliente", "👤 Cliente" },
                { "software", "💻 Software" }
            };

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
                    LocalFolderName: "anfeta.com.proyecto",
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
                    LocalFolderName: $"{clean}.proyecto",
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

            // Carpeta en Dropbox DRX: Formato preferido .proyecto o .software
            var folderName = string.IsNullOrEmpty(normalizedSuffix)
                ? $"{domain}.proyecto"
                : (normalizedSuffix is "software" or "prog" ? $"{domain}.software" : $"{domain}.proyecto");

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
        /// Sugiere automáticamente si es Software o Proyecto según el nombre de archivo o palabras clave.
        /// </summary>
        public static bool GuessIsSoftware(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.ToLowerInvariant();
            return lower.Contains("instalador") ||
                   lower.Contains("instalacion") ||
                   lower.Contains("instalación") ||
                   lower.Contains("setup") ||
                   lower.Contains("msix") ||
                   lower.Contains(".exe") ||
                   lower.Contains("tutorial") ||
                   (lower.Contains("software") && !lower.Contains("proyecto"));
        }

        /// <summary>
        /// Sugiere la categoría de carpeta preestablecida según el nombre del archivo.
        /// </summary>
        public static string GuessCategory(string? fileName, bool isSoftware)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return isSoftware ? "software" : "proyecto";

            var lower = fileName.ToLowerInvariant();

            if (isSoftware)
            {
                if (lower.Contains("instalador") || lower.Contains("setup") || lower.Contains("msix") || lower.Contains("exe") || lower.Contains("instalacion") || lower.Contains("instalación"))
                    return "instalador";
                if (lower.Contains("tutorial") || lower.Contains("manual") || lower.Contains("guia") || lower.Contains("guía") || lower.Contains("video"))
                    return "tutoriales";
                if (lower.Contains("cliente"))
                    return "cliente";
                return "software";
            }

            if (lower.Contains("coti") || lower.Contains("presupuesto") || lower.Contains("precio"))
                return "cotizacion";
            if (lower.Contains("disen") || lower.Contains("diseñ") || lower.Contains("logo") || lower.Contains("flyer") || lower.Contains("banner") || lower.Contains(".png") || lower.Contains(".jpg") || lower.Contains(".jpeg") || lower.Contains(".svg"))
                return "disenos";
            if (lower.Contains("face") || lower.Contains("fb"))
                return "facebook";
            if (lower.Contains("insta") || lower.Contains("ig"))
                return "instagram";
            if (lower.Contains("link") || lower.Contains("linkedin"))
                return "linkedin";
            if (lower.Contains("present") || lower.Contains("pitch") || lower.Contains("slide") || lower.Contains("propuesta"))
                return "presentacion";
            if (lower.Contains("seo") || lower.Contains("posicion"))
                return "seo";
            if (lower.Contains("tiktok") || lower.Contains("tik"))
                return "tiktok";
            if (lower.Contains("web") || lower.Contains("sitio") || lower.Contains("wpress") || lower.Contains("wordpress"))
                return "webs";
            if (lower.Contains("biblio"))
                return "biblioteca";

            return "proyecto";
        }

        /// <summary>
        /// Resuelve y asegura (creando automáticamente si no existen) las carpetas de Proyecto/Software
        /// y subcarpetas preestablecidas en DRX, siguiendo el flujo solicitado por John.
        /// </summary>
        public static string ResolveAndEnsureSmartDropboxDestination(
            string drxRoot,
            string domain,
            bool isSoftware,
            string? category,
            out string relativeDisplay)
        {
            relativeDisplay = string.Empty;
            if (string.IsNullOrWhiteSpace(drxRoot))
                return string.Empty;

            var cleanDomain = (domain ?? "anfeta.com").Trim().Trim('.').ToLowerInvariant();
            var domainPrefix = cleanDomain.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
                ? cleanDomain[..^4]
                : cleanDomain;

            // 1. Encontrar o crear la carpeta principal del dominio en DRX
            string projectFolder = string.Empty;
            if (Directory.Exists(drxRoot))
            {
                var existingDirs = Directory.EnumerateDirectories(drxRoot).ToList();
                projectFolder = existingDirs.FirstOrDefault(d =>
                {
                    var name = Path.GetFileName(d);
                    return string.Equals(name, $"{cleanDomain}.proyecto", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, $"{cleanDomain}.software", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, $"{cleanDomain}.carpeta", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, $"{cleanDomain}.Carpeta", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, cleanDomain, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, $"{domainPrefix}.proyecto", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, $"{domainPrefix}.software", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, $"{domainPrefix}.carpeta", StringComparison.OrdinalIgnoreCase);
                }) ?? string.Empty;
            }

            if (string.IsNullOrEmpty(projectFolder))
            {
                var folderSuffix = isSoftware ? "software" : "proyecto";
                projectFolder = Path.Combine(drxRoot, $"{cleanDomain}.{folderSuffix}");
                try { Directory.CreateDirectory(projectFolder); } catch { }
            }

            // 2. Si no se especificó subcarpeta o es raíz, retornar la carpeta principal
            var cat = (category ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(cat) || cat is "raiz" or "raíz" or "general")
            {
                relativeDisplay = Path.GetFileName(projectFolder);
                return projectFolder;
            }

            // 3. Encontrar o crear la subcarpeta de categoría dentro de la carpeta principal
            string targetSubfolder = string.Empty;
            if (Directory.Exists(projectFolder))
            {
                var subDirs = Directory.EnumerateDirectories(projectFolder).ToList();
                targetSubfolder = subDirs.FirstOrDefault(s =>
                {
                    var name = Path.GetFileName(s);
                    return string.Equals(name, $"{cat}.{cleanDomain}", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, $"{cleanDomain}.{cat}", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, cat, StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith($"{cat}.", StringComparison.OrdinalIgnoreCase) ||
                           name.EndsWith($".{cat}", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains(cat, StringComparison.OrdinalIgnoreCase);
                }) ?? string.Empty;
            }

            if (string.IsNullOrEmpty(targetSubfolder))
            {
                // Convención de nombrado para subcarpetas creadas automáticamente
                // Proyecto: webs.agapetoursconcordia.com / agapetoursconcordia.com.biblioteca
                // Software: anfeta.com.tutoriales / anfeta.com.instalador
                string subName = isSoftware
                    ? $"{cleanDomain}.{cat}"
                    : (cat is "webs" ? $"{cat}.{cleanDomain}" : $"{cleanDomain}.{cat}");

                targetSubfolder = Path.Combine(projectFolder, subName);
                try { Directory.CreateDirectory(targetSubfolder); } catch { }
            }

            var projName = Path.GetFileName(projectFolder);
            var subNameDisplay = Path.GetFileName(targetSubfolder);
            relativeDisplay = $"{projName}\\{subNameDisplay}";

            return targetSubfolder;
        }

        /// <summary>
        /// Resuelve la ruta física en Dropbox local para el dominio y sufijo especificado,
        /// buscando compatibilidad con DRX/{dominio}.proyecto, DRX/{dominio}.Carpeta o {dominio}.{sufijo}.
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

            // 2. Buscar en la carpeta general del dominio: DRX/{dominio}.proyecto o DRX/{dominio}.Carpeta
            var existingDir = Directory.EnumerateDirectories(drxRoot)
                .FirstOrDefault(d =>
                {
                    var dirName = Path.GetFileName(d);
                    if (string.Equals(dirName, $"{domain}.proyecto", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dirName, $"{domain}.Carpeta", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dirName, $"{domain}.software", StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (dirName.EndsWith(".proyecto", StringComparison.OrdinalIgnoreCase) ||
                        dirName.EndsWith(".Carpeta", StringComparison.OrdinalIgnoreCase) ||
                        dirName.EndsWith(".software", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleanDirBase = dirName.Split('.')[0];
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
                : Path.Combine(drxRoot, $"{domain}.proyecto");
        }
    }
}
