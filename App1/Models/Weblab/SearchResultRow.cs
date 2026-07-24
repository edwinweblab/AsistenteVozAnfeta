using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
        public string ProjectUpdateStatus { get; set; } = "";
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
                OnPropertyChanged(nameof(ResultSummary));
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

        [JsonIgnore]
        public string ResultSummary => BuildResultSummary();

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

                return "Local";
            }
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

            return clean.Trim();
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