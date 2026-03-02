using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Anfeta.UI.Models.Weblab
{
    public class SearchResultRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public IconSource? Icon { get; set; }  // para IconSourceElement
        public string NodeId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Target { get; set; } = "";
        public string Type { get; set; } = ""; // FILE/FOLDER
        public long Size { get; set; }
        public string ServerModified { get; set; } = "";
        public SearchSource Source { get; set; }

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
