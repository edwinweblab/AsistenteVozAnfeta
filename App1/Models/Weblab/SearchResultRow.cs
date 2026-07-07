using Microsoft.UI.Xaml.Controls;
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