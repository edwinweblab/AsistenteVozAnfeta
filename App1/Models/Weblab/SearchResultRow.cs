using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Anfeta.UI.Models.Weblab
{
    public class SearchResultRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private double _thumbnailTileWidth = 150;
        [JsonIgnore]
        public double ThumbnailTileWidth
        {
            get => _thumbnailTileWidth;
            set
            {
                if (Math.Abs(_thumbnailTileWidth - value) < 0.1) return;
                _thumbnailTileWidth = value;
                OnPropertyChanged();
            }
        }

        private double _thumbnailTileHeight = 176;
        [JsonIgnore]
        public double ThumbnailTileHeight
        {
            get => _thumbnailTileHeight;
            set
            {
                if (Math.Abs(_thumbnailTileHeight - value) < 0.1) return;
                _thumbnailTileHeight = value;
                OnPropertyChanged();
            }
        }

        private double _thumbnailImageHeight = 128;
        [JsonIgnore]
        public double ThumbnailImageHeight
        {
            get => _thumbnailImageHeight;
            set
            {
                if (Math.Abs(_thumbnailImageHeight - value) < 0.1) return;
                _thumbnailImageHeight = value;
                OnPropertyChanged();
            }
        }

        private ImageSource? _thumbnail;
        [JsonIgnore]
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value)) return;
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        private IconSource? _icon;
        [JsonIgnore]
        public IconSource? Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                OnPropertyChanged();
            }
        }

        public string NodeId { get; set; } = "";
        public string ExternalId { get; set; } = "";
        public string ExternalUrl { get; set; } = "";
        public string SearchText { get; set; } = "";
        public string Description { get; set; } = "";

        private string _projectUpdateStatus = "";
        public string ProjectUpdateStatus
        {
            get => _projectUpdateStatus;
            set
            {
                var clean = value ?? "";
                if (_projectUpdateStatus == clean) return;

                _projectUpdateStatus = clean;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WorkflowChipText));
                OnPropertyChanged(nameof(WorkflowChipVisibility));
                OnPropertyChanged(nameof(WorkflowChipAccentBrush));
                OnPropertyChanged(nameof(WorkflowFallbackText));
                OnPropertyChanged(nameof(ResultNameBrush));
            }
        }

        public string ScheduledDate { get; set; } = "";
        public int AssignmentDataVersion { get; set; }
        public string[] AssignmentKeys { get; set; } = Array.Empty<string>();
        public DateTimeOffset? NotionEditedUtc { get; set; }
        public string ExternalSourceName { get; set; } = "";

        private string _name = "";
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(VisualTitle));
                OnPropertyChanged(nameof(WorkflowChipText));
                OnPropertyChanged(nameof(WorkflowChipVisibility));
                OnPropertyChanged(nameof(WorkflowChipAccentBrush));
                OnPropertyChanged(nameof(AreaChipText));
                OnPropertyChanged(nameof(AreaChipVisibility));
                OnPropertyChanged(nameof(ExtraTagsText));
                OnPropertyChanged(nameof(ExtraTagsVisibility));
                OnPropertyChanged(nameof(DomainChipText));
                OnPropertyChanged(nameof(DomainChipVisibility));
                OnPropertyChanged(nameof(AreaGroupName));
                OnPropertyChanged(nameof(ResultSummary));
                OnPropertyChanged(nameof(ResultNameBrush));
            }
        }

        private string _target = "";
        /// <summary>Ruta completa local (archivo/carpeta). Tu código la usa como Target.</summary>
        public string Target
        {
            get => _target;
            set
            {
                if (_target == value) return;
                _target = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(FullPath));
                OnPropertyChanged(nameof(TargetNorm));
                OnPropertyChanged(nameof(ResultSummary));
                OnPropertyChanged(nameof(DisplayLocation));
                OnPropertyChanged(nameof(ResultSymbol));
                OnPropertyChanged(nameof(ResultKindBadgeText));
                OnPropertyChanged(nameof(ResultKindBadgeVisibility));
                OnPropertyChanged(nameof(ResultAccentBrush));
                OnPropertyChanged(nameof(ResultIconSize));
                OnPropertyChanged(nameof(UsesClassicWebIcon));
                OnPropertyChanged(nameof(ClassicWebIconVisibility));
                OnPropertyChanged(nameof(NativeResultIconVisibility));
            }
        }
        public string FullPath
        {
            get => Target;
            set => Target = value ?? "";
        }
        private string _type = ""; // FILE/FOLDER
        public string Type
        {
            get => _type;
            set
            {
                var normalized = NormalizeType(value);
                if (_type == normalized) return;
                _type = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFolder));
                OnPropertyChanged(nameof(ResultGlyph));
                OnPropertyChanged(nameof(FolderBadgeText));
                OnPropertyChanged(nameof(FolderBadgeVisibility));
                OnPropertyChanged(nameof(ResultSymbol));
                OnPropertyChanged(nameof(ResultKindBadgeText));
                OnPropertyChanged(nameof(ResultKindBadgeVisibility));
                OnPropertyChanged(nameof(ResultAccentBrush));
                OnPropertyChanged(nameof(ResultIconSize));
                OnPropertyChanged(nameof(UsesClassicWebIcon));
                OnPropertyChanged(nameof(ClassicWebIconVisibility));
                OnPropertyChanged(nameof(NativeResultIconVisibility));
            }
        }

        public long Size { get; set; }
        public string ServerModified { get; set; } = "";
        public SearchSource Source { get; set; }

        // ----------------------------
        // Helpers visuales para resultados
        // ----------------------------

        [JsonIgnore]
        public string DisplayName => BuildDisplayName();

        // Proyección exclusivamente visual. Name y SearchText permanecen intactos
        // para búsqueda, filtros, apertura, menús y acciones.
        [JsonIgnore]
        public string VisualTitle => GetVisualParts().Title;

        [JsonIgnore]
        public string WorkflowChipText =>
            ResolveStatusWorkflowChip(ProjectUpdateStatus) is { Length: > 0 } statusChip
                ? statusChip
                : GetVisualParts().Workflow;

        [JsonIgnore]
        public string WorkflowFallbackText =>
            string.IsNullOrWhiteSpace(WorkflowChipText)
                ? ProjectUpdateStatus ?? string.Empty
                : string.Empty;

        [JsonIgnore]
        public Visibility WorkflowChipVisibility =>
            string.IsNullOrWhiteSpace(WorkflowChipText) ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public Brush WorkflowChipAccentBrush =>
            WorkflowChipText switch
            {
                "PENDIENTE" => BuildResultBrush(251, 191, 36),
                "EN REVISIÓN" => BuildResultBrush(248, 80, 80),
                "SUSPENDIDA" => BuildResultBrush(250, 204, 21),
                "POR HACER" => BuildResultBrush(192, 132, 252),
                "TERMINADA" => BuildResultBrush(148, 190, 220),
                _ => BuildResultBrush(148, 163, 184)
            };

        [JsonIgnore]
        public string AreaChipText
        {
            get
            {
                var area = GetVisualParts().Area;
                return Source == SearchSource.Notion && string.IsNullOrWhiteSpace(area)
                    ? "S/T"
                    : area;
            }
        }

        [JsonIgnore]
        public Visibility AreaChipVisibility =>
            string.IsNullOrWhiteSpace(AreaChipText) ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public string ExtraTagsText => GetVisualParts().ExtraTags;

        [JsonIgnore]
        public Visibility ExtraTagsVisibility =>
            string.IsNullOrWhiteSpace(ExtraTagsText) ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public string DomainChipText => GetVisualParts().Domain;

        [JsonIgnore]
        public Visibility DomainChipVisibility =>
            string.IsNullOrWhiteSpace(DomainChipText) ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public string AreaGroupName =>
            string.IsNullOrWhiteSpace(GetVisualParts().Area) ? "Otros" : GetVisualParts().Area;

        [JsonIgnore]
        public string ResultSummary => BuildResultSummary();

        /// <summary>
        /// Color del nombre según el estado de revisión.
        /// rtuzREVISION = rojo fuerte; zREVISION = tenue; resto = normal.
        /// La detección usa límites para no confundir prtuzREVISION con rtuzREVISION.
        /// </summary>
        [JsonIgnore]
        public Brush ResultNameBrush
        {
            get
            {
                var workflowText = string.Join(
                    " ",
                    ProjectUpdateStatus ?? string.Empty,
                    SearchText ?? string.Empty,
                    Description ?? string.Empty,
                    Name ?? string.Empty);

                if (HasWorkflowToken(workflowText, "rtuzREVISION"))
                    return BuildResultBrush(248, 113, 113);

                if (HasWorkflowToken(workflowText, "zREVISION"))
                    return BuildResultBrush(148, 163, 184);

                return BuildResultBrush(242, 242, 242);
            }
        }

        // Ruta visual relativa a la raíz de Dropbox.
        // Target conserva SIEMPRE la ruta local absoluta para operaciones reales.
        private string _dropboxRelativePath = string.Empty;
        [JsonIgnore]
        public string DropboxRelativePath
        {
            get => _dropboxRelativePath;
            set
            {
                var clean = value ?? string.Empty;
                if (string.Equals(_dropboxRelativePath, clean, StringComparison.Ordinal))
                    return;

                _dropboxRelativePath = clean;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResultSummary));
                OnPropertyChanged(nameof(DisplayLocation));
            }
        }

        private string _dropboxPathColumn = string.Empty;
        [JsonIgnore]
        public string DropboxPathColumn
        {
            get => _dropboxPathColumn;
            set
            {
                var clean = value ?? string.Empty;
                if (string.Equals(_dropboxPathColumn, clean, StringComparison.Ordinal))
                    return;

                _dropboxPathColumn = clean;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PathColumn));
            }
        }

        [JsonIgnore]
        public string DisplayLocation
        {
            get
            {
                if (Source == SearchSource.Notion)
                {
                    if (!string.IsNullOrWhiteSpace(ExternalUrl))
                        return ExternalUrl.Trim();

                    return (Target ?? string.Empty).Trim();
                }

                if (!string.IsNullOrWhiteSpace(DropboxRelativePath))
                    return DropboxRelativePath.Trim();

                return (Target ?? string.Empty).Trim();
            }
        }

        // Compatibilidad:
        // Evitamos depender de codepoints crudos de Segoe MDL2 Assets.
        // SymbolIcon usa símbolos nativos de WinUI y no aparece como "□"
        // cuando cambia la versión de la fuente instalada.
        [JsonIgnore]
        public bool UsesClassicWebIcon
        {
            get
            {
                if (Source == SearchSource.Notion)
                    return true;

                var extension = GetResultExtension();

                return extension is "url" or "website" or "webloc";
            }
        }

        [JsonIgnore]
        public Visibility ClassicWebIconVisibility =>
            UsesClassicWebIcon
                ? Visibility.Visible
                : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility NativeResultIconVisibility =>
            UsesClassicWebIcon
                ? Visibility.Collapsed
                : Visibility.Visible;

        [JsonIgnore]
        public Symbol ResultSymbol
        {
            get
            {
                if (IsFolder)
                    return Symbol.Folder;

                // Las páginas de Notion son recursos web.
                // Usamos Link para evitar codepoints/fuentes no compatibles.
                if (Source == SearchSource.Notion)
                    return Symbol.Globe;

                var extension = GetResultExtension();

                if (extension is "url" or "website" or "webloc")
                    return Symbol.Globe;

                if (IsImageExtension(extension))
                    return Symbol.Pictures;

                if (IsVideoExtension(extension))
                    return Symbol.Video;

                return Symbol.Document;
            }
        }

        [JsonIgnore]
        public double ResultIconSize
        {
            get
            {
                // El globo se percibe visualmente más grande que los demás
                // SymbolIcon aunque tenga el mismo Width/Height.
                // Lo reducimos SOLO para Notion y accesos web.
                if (Source == SearchSource.Notion)
                    return 11;

                var extension = GetResultExtension();

                if (extension is "url" or "website" or "webloc")
                    return 11;

                return 16;
            }
        }

        [JsonIgnore]
        public string ResultKindBadgeText
        {
            get
            {
                if (IsFolder)
                    return "CARPETA";

                // En Notion dejamos solo el icono web, sin marcador/pastilla.
                if (Source == SearchSource.Notion)
                    return string.Empty;

                var extension = GetResultExtension();

                if (string.IsNullOrWhiteSpace(extension))
                    return "FILE";

                if (extension is "url" or "website" or "webloc")
                    return "WEB";

                if (extension == "pdf")
                    return "PDF";

                if (extension is "doc" or "docx" or "odt")
                    return "DOC";

                if (extension is "xls" or "xlsx" or "xlsm" or "csv" or "ods")
                    return "XLS";

                if (extension is "ppt" or "pptx" or "odp")
                    return "PPT";

                if (IsImageExtension(extension))
                    return "IMG";

                if (IsVideoExtension(extension))
                    return "VID";

                if (extension is "mp3" or "wav" or "m4a" or "aac" or "flac" or "ogg")
                    return "AUD";

                if (extension is "zip" or "rar" or "7z" or "tar" or "gz")
                    return "ZIP";

                if (extension is "txt" or "md" or "rtf" or "log")
                    return "TXT";

                if (extension is "cs" or "xaml" or "json" or "xml" or
                    "js" or "ts" or "tsx" or "jsx" or "html" or "css" or
                    "php" or "py" or "sql")
                {
                    return "CODE";
                }

                // Para otras extensiones cortas, la propia extensión es más
                // útil que un icono genérico.
                return extension.Length <= 5
                    ? extension.ToUpperInvariant()
                    : "FILE";
            }
        }

        [JsonIgnore]
        public Visibility ResultKindBadgeVisibility =>
            string.IsNullOrWhiteSpace(ResultKindBadgeText)
                ? Visibility.Collapsed
                : Visibility.Visible;

        [JsonIgnore]
        public Brush ResultAccentBrush
        {
            get
            {
                var extension = GetResultExtension();

                if (IsFolder)
                    return BuildResultBrush(245, 194, 66);   // carpeta

                if (Source == SearchSource.Notion)
                    return BuildResultBrush(226, 232, 240); // Notion

                if (extension is "url" or "website" or "webloc")
                    return BuildResultBrush(56, 189, 248);  // web / enlace

                if (extension == "pdf")
                    return BuildResultBrush(248, 113, 113); // PDF

                if (extension is "doc" or "docx" or "odt")
                    return BuildResultBrush(96, 165, 250);  // Word

                if (extension is "xls" or "xlsx" or "xlsm" or "csv" or "ods")
                    return BuildResultBrush(52, 211, 153);  // Excel

                if (extension is "ppt" or "pptx" or "odp")
                    return BuildResultBrush(251, 146, 60);  // PowerPoint

                if (IsImageExtension(extension))
                    return BuildResultBrush(192, 132, 252); // imagen

                if (IsVideoExtension(extension))
                    return BuildResultBrush(244, 114, 182); // video

                if (extension is "mp3" or "wav" or "m4a" or "aac" or "flac" or "ogg")
                    return BuildResultBrush(167, 139, 250); // audio

                if (extension is "zip" or "rar" or "7z" or "tar" or "gz")
                    return BuildResultBrush(251, 191, 36);  // comprimido

                if (extension is "cs" or "xaml" or "json" or "xml" or
                    "js" or "ts" or "tsx" or "jsx" or "html" or "css" or
                    "php" or "py" or "sql")
                {
                    return BuildResultBrush(103, 232, 249); // código
                }

                return BuildResultBrush(184, 184, 184);
            }
        }

        // Se mantiene por compatibilidad con cualquier vista vieja que todavía
        // lo enlace, pero las vistas nuevas usan ResultSymbol.
        [JsonIgnore]
        public string ResultGlyph => IsFolder ? "\uE8B7" : "\uE8A5";

        private string GetResultExtension()
        {
            var candidate =
                !string.IsNullOrWhiteSpace(Target)
                    ? Target
                    : Name;

            return System.IO.Path
                .GetExtension(candidate ?? string.Empty)
                .TrimStart('.')
                .Trim()
                .ToLowerInvariant();
        }

        private static bool IsImageExtension(string extension)
            => extension is "png" or "jpg" or "jpeg" or "webp" or
                "gif" or "bmp" or "tif" or "tiff" or "svg" or "ico";

        private static bool IsVideoExtension(string extension)
            => extension is "mp4" or "mov" or "avi" or "mkv" or
                "webm" or "wmv" or "m4v" or "mpeg" or "mpg";

        private static bool HasWorkflowToken(string? value, string token)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            return Regex.IsMatch(
                value,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static Brush BuildResultBrush(
            byte red,
            byte green,
            byte blue)
            => new SolidColorBrush(
                Windows.UI.Color.FromArgb(
                    255,
                    red,
                    green,
                    blue));

        [JsonIgnore]
        public string FolderBadgeText => IsFolder ? "CARPETA" : string.Empty;

        [JsonIgnore]
        public Visibility FolderBadgeVisibility =>
            IsFolder ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public string ModifiedLabel
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ServerModified))
                    return "";

                return $"Modificado: {ServerModified}";
            }
        }


        [JsonIgnore]
        public string PathColumn
        {
            get
            {
                if (Source == SearchSource.Notion)
                {
                    return ExternalSourceName switch
                    {
                        "Cobrar y pagar" => "zPAGAR - zCOBRAR",
                        "Dominios" => "zDOMINIOS",
                        "Clientes" => "zCLIENTES",
                        "Programas y proyectos" => "zPROYECTOS",
                        "Correos Contraseñas" => "zCORREOS",
                        "Revisiones" => "Revisiones",
                        _ => string.IsNullOrWhiteSpace(ExternalSourceName) ? "Notion" : ExternalSourceName
                    };
                }

                if (!string.IsNullOrWhiteSpace(DropboxPathColumn))
                    return DropboxPathColumn;

                return Source == SearchSource.Dropbox
                    ? "Dropbox"
                    : "Local";
            }
        }

        [JsonIgnore]
        public string FechaPorHacerColumn
        {
            get
            {
                if (Source != SearchSource.Notion ||
                    string.IsNullOrWhiteSpace(ScheduledDate))
                {
                    return "-";
                }

                var raw = ScheduledDate.Trim();
                var separatorIndex = raw.IndexOf(" - ", StringComparison.Ordinal);

                if (separatorIndex > 0)
                {
                    var startRaw = raw.Substring(0, separatorIndex).Trim();
                    var endRaw = raw.Substring(separatorIndex + 3).Trim();

                    if (DateTimeOffset.TryParse(startRaw, out var start) &&
                        DateTimeOffset.TryParse(endRaw, out var end))
                    {
                        var localStart = start.LocalDateTime;
                        var localEnd = end.LocalDateTime;

                        return localStart.Date == localEnd.Date
                            ? $"{FormatRelativeScheduledDate(localStart)} {localStart:HH:mm}–{localEnd:HH:mm}"
                            : $"{FormatRelativeScheduledDate(localStart)} {localStart:HH:mm} – {FormatRelativeScheduledDate(localEnd)} {localEnd:HH:mm}";
                    }
                }

                if (DateTimeOffset.TryParse(raw, out var offset))
                {
                    var local = offset.LocalDateTime;
                    return $"{FormatRelativeScheduledDate(local)} {local:HH:mm}";
                }

                if (DateTime.TryParse(raw, out var date))
                    return $"{FormatRelativeScheduledDate(date)} {date:HH:mm}";

                return raw;
            }
        }

        private static string FormatRelativeScheduledDate(
            DateTime value)
        {
            var difference =
                (value.Date - DateTime.Today).Days;

            return difference switch
            {
                -1 => "Ayer",
                0 => "Hoy",
                1 => "Mañana",
                _ => value.ToString("dd/MM/yyyy")
            };
        }

        [JsonIgnore]
        public string DateModifiedColumn
        {
            get
            {
                if (DateTime.TryParse(ServerModified, out var dt))
                    return dt.ToString("dd/MM/yyyy");

                return "-";
            }
        }

        [JsonIgnore]
        public string SizeColumn
        {
            get
            {
                if (Source == SearchSource.Notion)
                    return "-";

                return FormatSize(Size);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "";

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;

            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0
                ? $"{bytes} B"
                : $"{size:0.#} {units[unit]}";
        }

        private string BuildDisplayName()
        {
            var clean = (Name ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return "Sin título";

            clean = StripSourcePrefix(clean, ExternalSourceName);

            if (Source == SearchSource.Notion &&
                string.Equals(
                    ExternalSourceName,
                    "Revisiones",
                    StringComparison.OrdinalIgnoreCase))
            {
                clean = StripReminderMetadata(clean);
            }

            return clean.Trim();
        }

        private sealed record VisualParts(
            string Title,
            string Workflow,
            string Area,
            string ExtraTags,
            string Domain);

        private VisualParts GetVisualParts()
        {
            var display = BuildDisplayName();
            if (Source != SearchSource.Notion || string.IsNullOrWhiteSpace(display))
                return new VisualParts(display, string.Empty, string.Empty, string.Empty, string.Empty);

            var workflowMatch = Regex.Match(
                display,
                @"(?<![\p{L}\p{Nd}_])(?<value>sprtuzREVISION|aprtuzREVISION|prtuzREVISION|rtuzREVISION|zREVISION)(?![\p{L}\p{Nd}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var workflow = workflowMatch.Success
                ? NormalizeWorkflowChip(workflowMatch.Groups["value"].Value)
                : string.Empty;

            var withoutWorkflow = workflowMatch.Success
                ? display.Remove(workflowMatch.Index, workflowMatch.Length)
                : display;

            var encodedAreaMatch = Regex.Match(
                withoutWorkflow,
                @"(?<![\p{L}\p{Nd}_])(?<area>sseo|wwebs|aads|aapli|pprog|ddise|rrede|mmaps)(?![\p{L}\p{Nd}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var encodedArea = encodedAreaMatch.Success
                ? NormalizeEncodedArea(encodedAreaMatch.Groups["area"].Value)
                : string.Empty;
            if (encodedAreaMatch.Success)
                withoutWorkflow = withoutWorkflow.Remove(encodedAreaMatch.Index, encodedAreaMatch.Length);

            var domainMatch = Regex.Match(
                withoutWorkflow,
                @"(?<![\w.-])(?:https?://)?(?:www\.)?(?<domain>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|com|mx|org|net|io|co|app|dev))(?=$|[/:?#\s)\]}>.,;!])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var domain = domainMatch.Success
                ? domainMatch.Groups["domain"].Value.ToLowerInvariant()
                : string.Empty;
            var titleSource = domainMatch.Success
                ? withoutWorkflow.Remove(domainMatch.Index, domainMatch.Length)
                : withoutWorkflow;

            // Solo extraemos tags iniciales inequívocos. No quitamos palabras
            // semánticas que aparezcan dentro del título (p. ej. "Auditoría SEO").
            var tagMatch = Regex.Match(
                titleSource,
                @"^\s*(?:(?:\([^)]*\)|\d+(?:\.\d+)?)\s+)*(?<tags>(?:(?:APLICACI[ÓO]N|PROGRAMAS?|CLIENTE|ADS|REDES|WEBS?|SEO|MAPS|COTI|BIBLIA|COBROS?|PAGOS?)\s+){1,4})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var tags = tagMatch.Success
                ? Regex.Matches(tagMatch.Groups["tags"].Value, @"[\p{L}]+", RegexOptions.CultureInvariant)
                    .Select(match => NormalizeArea(match.Value))
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToList()
                : new System.Collections.Generic.List<string>();

            var area = encodedArea.Length > 0
                ? encodedArea
                : tags.FirstOrDefault() ?? FindAreaPrefix(titleSource);
            var extras = string.Join(" · ", tags.Skip(1).Take(3));
            var title = tagMatch.Success
                ? titleSource.Remove(tagMatch.Groups["tags"].Index, tagMatch.Groups["tags"].Length)
                : titleSource;
            title = Regex.Replace(title, @"\s{2,}", " ").Trim(' ', '-', '–', '—', '|', ':');
            if (string.IsNullOrWhiteSpace(title)) title = display;

            return new VisualParts(title, workflow, area, extras, domain);
        }

        private static string NormalizeWorkflowChip(string value)
        {
            if (value.Equals("prtuzREVISION", StringComparison.OrdinalIgnoreCase)) return "PENDIENTE";
            if (value.Equals("rtuzREVISION", StringComparison.OrdinalIgnoreCase)) return "EN REVISIÓN";
            if (value.Equals("zREVISION", StringComparison.OrdinalIgnoreCase)) return "TERMINADA";
            if (value.Equals("sprtuzREVISION", StringComparison.OrdinalIgnoreCase)) return "SUSPENDIDA";
            if (value.Equals("aprtuzREVISION", StringComparison.OrdinalIgnoreCase)) return "POR HACER";
            return value;
        }

        private static string ResolveStatusWorkflowChip(string? value)
        {
            var status = (value ?? string.Empty).Trim();
            if (status.Contains("cobrado terminado", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("pendiente cobrar", StringComparison.OrdinalIgnoreCase))
                return "TERMINADA";
            if (status.Contains("revisar revisiones", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("terminado rev cobro", StringComparison.OrdinalIgnoreCase))
                return "EN REVISIÓN";
            if (status.Contains("suspex", StringComparison.OrdinalIgnoreCase))
                return "SUSPENDIDA";
            if (status.Contains("arrancar asignar", StringComparison.OrdinalIgnoreCase))
                return "POR HACER";
            if (status.Contains("prtuz por hacer", StringComparison.OrdinalIgnoreCase))
                return "PENDIENTE";
            return string.Empty;
        }

        private static string FindAreaPrefix(string value)
        {
            var match = Regex.Match(value ?? string.Empty,
                @"^\s*(?:(?:\([^)]*\)|\d+(?:\.\d+)?)\s+)*(?<area>APLICACI[ÓO]N|PROGRAMAS?|CLIENTE|ADS|REDES|WEBS?|SEO|MAPS|COTI|BIBLIA|COBROS?|PAGOS?)(?![\p{L}\p{Nd}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? NormalizeArea(match.Groups["area"].Value) : string.Empty;
        }

        private static string NormalizeArea(string value)
        {
            var upper = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (upper is "WEB" or "WEBS") return "WEB";
            if (upper is "COBRO" or "COBROS") return "COBROS";
            if (upper is "PAGO" or "PAGOS") return "PAGOS";
            if (upper is "PROGRAMA" or "PROGRAMAS") return "PROGRAMAS";
            return upper;
        }

        private static string NormalizeEncodedArea(string value) =>
            (value ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "sseo" => "SEO",
                "wwebs" => "WEB",
                "aads" => "ADS",
                "aapli" => "APLICACIÓN",
                "pprog" => "PROGRAMACIÓN",
                "ddise" => "DISEÑO",
                "rrede" => "REDES",
                "mmaps" => "MAPS",
                _ => string.Empty
            };

        private static string StripReminderMetadata(string value)
        {
            var text = (value ?? string.Empty).Trim();

            var match = Regex.Match(
                text,
                @"^(?<date>\d{4}-\d{2}-\d{2})[ T](?<hour>\d{2})[:\-](?<minute>\d{2})\s+" +
                @"(?<recipient>[a-z0-9_-]+)(?:\s+de:[a-z0-9_-]+)?(?:\s+\[TERMINADO\])?\s*",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);

            if (!match.Success)
                return text;

            var clean = text
                .Substring(match.Length)
                .Trim(' ', '-', '–', '—', ':', '|');

            return string.IsNullOrWhiteSpace(clean)
                ? text
                : clean;
        }

        private string BuildResultSummary()
        {
            if (Source == SearchSource.Notion)
            {
                var desc = StripSourcePrefix(Description, ExternalSourceName);
                if (!string.IsNullOrWhiteSpace(desc))
                    return desc.Trim();

                if (!string.IsNullOrWhiteSpace(ExternalUrl))
                    return ExternalUrl.Trim();

                return "Página de Notion";
            }

            if (!string.IsNullOrWhiteSpace(DropboxRelativePath))
                return DropboxRelativePath.Trim();

            if (!string.IsNullOrWhiteSpace(Target))
                return Target.Trim();

            return Type ?? string.Empty;
        }

        private static string StripSourcePrefix(string? value, string? sourceName)
        {
            var text = (value ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                var prefix = $"[{sourceName.Trim()}]";
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    text = text.Substring(prefix.Length).Trim();
            }

            // Fallback por si viene [Clientes], [Revisiones], etc.
            if (text.StartsWith("["))
            {
                var close = text.IndexOf(']');
                if (close > 0 && close < 45)
                    text = text.Substring(close + 1).Trim();
            }

            return text;
        }

        // ----------------------------
        // Helpers para Rename/Delete
        // ----------------------------

        /// <summary>Alias claro para Target.</summary>


        /// <summary>True si representa carpeta.</summary>
        public bool IsFolder => string.Equals(Type, "FOLDER", StringComparison.OrdinalIgnoreCase);

        /// <summary>Path normalizado para comparaciones.</summary>
        public string TargetNorm => NormalizePath(Target);

        public static string NormalizePath(string p)
            => (p ?? "").Trim().Replace('/', '\\');

        private static string NormalizeType(string? t)
        {
            var s = (t ?? "").Trim();
            if (s.Length == 0) return "";

            // Acepta variantes: "folder", "dir", "directory"
            if (s.Equals("folder", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("dir", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("directory", StringComparison.OrdinalIgnoreCase))
                return "FOLDER";

            if (s.Equals("file", StringComparison.OrdinalIgnoreCase))
                return "FILE";

            // Si ya viene FILE/FOLDER u otro, respeta pero en mayúsculas
            return s.ToUpperInvariant();
        }

        // ----------------------------
        // Bookmarks / estrella (igual)
        // ----------------------------

        private bool _isBookmarked;
        public bool IsBookmarked
        {
            get => _isBookmarked;
            set
            {
                if (_isBookmarked == value) return;
                _isBookmarked = value;
                StarGlyph = value ? "★" : "☆";
                OnPropertyChanged();
            }
        }

        private string _starGlyph = "☆";
        public string StarGlyph
        {
            get => _starGlyph;
            set
            {
                if (_starGlyph == value) return;
                _starGlyph = value;
                OnPropertyChanged();
            }
        }
    }
}
