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

        private string _name = "";
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value ?? "";
                OnPropertyChanged();
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