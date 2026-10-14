using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Anfeta.UI.Models.Search
{
    public class SavedSearch : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _title = "";
        private string _description = "";
        private string _query = "";
        private string _scheduledDate = "";
        private bool _isPendingTask;
        private bool _isCompleted;
        private string _priority = "";
        private string _tag = "";
        private SearchCriteriaState? _criteria;

        public string Id
        {
            get => _id;
            set { if (_id != value) { _id = value; OnPropertyChanged(); } }
        }

        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(OpenTarget)); OnPropertyChanged(nameof(CanOpen)); } }
        }

        public string Description
        {
            get => _description;
            set { if (_description != value) { _description = value; OnPropertyChanged(); } }
        }

        public string Query
        {
            get => _query;
            set { if (_query != value) { _query = value; OnPropertyChanged(); OnPropertyChanged(nameof(SubtitleText)); OnPropertyChanged(nameof(OpenTarget)); OnPropertyChanged(nameof(CanOpen)); } }
        }

        public string ScheduledDate
        {
            get => _scheduledDate;
            set { if (_scheduledDate != value) { _scheduledDate = value; OnPropertyChanged(); } }
        }

        public bool IsPendingTask
        {
            get => _isPendingTask;
            set { if (_isPendingTask != value) { _isPendingTask = value; OnPropertyChanged(); } }
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                if (_isCompleted != value)
                {
                    _isCompleted = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TitleDecorations));
                    OnPropertyChanged(nameof(CompletedOpacity));
                    OnPropertyChanged(nameof(CheckGlyph));
                    OnPropertyChanged(nameof(CheckBackgroundBrush));
                    OnPropertyChanged(nameof(CheckBorderBrush));
                    OnPropertyChanged(nameof(CheckGlyphForeground));
                    OnPropertyChanged(nameof(TitleForegroundBrush));
                }
            }
        }

        public string Priority
        {
            get => _priority;
            set
            {
                if (_priority != value)
                {
                    _priority = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PriorityLabel));
                    OnPropertyChanged(nameof(PriorityBadgeVisibility));
                    OnPropertyChanged(nameof(PriorityBackgroundBrush));
                    OnPropertyChanged(nameof(PriorityBorderBrush));
                    OnPropertyChanged(nameof(PriorityForegroundBrush));
                }
            }
        }

        public string Tag
        {
            get => _tag;
            set
            {
                if (_tag != value)
                {
                    _tag = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SubtitleText));
                }
            }
        }

        public SearchCriteriaState? Criteria
        {
            get => _criteria;
            set { if (_criteria != value) { _criteria = value; OnPropertyChanged(); } }
        }

        // ==========================================
        // Visual Helpers (Ignored by JSON serializer)
        // ==========================================

        [JsonIgnore]
        public Windows.UI.Text.TextDecorations TitleDecorations =>
            IsCompleted ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;

        [JsonIgnore]
        public double CompletedOpacity => IsCompleted ? 0.55 : 1.0;
        [JsonIgnore]
        public Brush TitleForegroundBrush => new SolidColorBrush(IsCompleted ? Color.FromArgb(255,16,185,129) : Color.FromArgb(255,226,232,240));
        public string Url { get; set; } = "";
        public string NotionPageId { get; set; } = "";
        [JsonIgnore]
        public string OpenTarget
        {
            get
            {
                if (Guid.TryParse(NotionPageId, out var id)) return "https://www.notion.so/" + id.ToString("N");
                var match = Regex.Match(string.Join(" ", Url, Query, Title), @"https?://[^\s<>""']+", RegexOptions.IgnoreCase);
                return match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? uri.AbsoluteUri : "";
            }
        }
        [JsonIgnore] public bool CanOpen => !string.IsNullOrWhiteSpace(OpenTarget);

        [JsonIgnore]
        public string CheckGlyph => IsCompleted ? "\uE73E" : "";

        [JsonIgnore]
        public Brush CheckBackgroundBrush =>
            IsCompleted
                ? new SolidColorBrush(Color.FromArgb(0x33, 0x10, 0xB9, 0x81)) // Emerald green tint
                : new SolidColorBrush(Colors.Transparent);

        [JsonIgnore]
        public Brush CheckBorderBrush =>
            IsCompleted
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0xB9, 0x81)) // #10B981 emerald
                : new SolidColorBrush(Color.FromArgb(0x80, 0x64, 0x74, 0x8B)); // #64748B slate

        [JsonIgnore]
        public Brush CheckGlyphForeground =>
            new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0xB9, 0x81));

        [JsonIgnore]
        public string SubtitleText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Tag) && !string.IsNullOrWhiteSpace(Query))
                    return $"{Tag} · {Query}";
                if (!string.IsNullOrWhiteSpace(Tag))
                    return Tag;
                return Query;
            }
        }

        [JsonIgnore]
        public Visibility PriorityBadgeVisibility =>
            string.IsNullOrWhiteSpace(Priority) ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public string PriorityLabel =>
            Priority?.ToUpperInvariant() switch
            {
                "P1" => "P1",
                "P2" => "P2",
                "P3" => "P3",
                _ => Priority ?? ""
            };

        [JsonIgnore]
        public Brush PriorityBackgroundBrush =>
            Priority?.ToUpperInvariant() switch
            {
                "P1" => new SolidColorBrush(Color.FromArgb(0x33, 0xEF, 0x44, 0x44)), // Red
                "P2" => new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B)), // Amber
                "P3" => new SolidColorBrush(Color.FromArgb(0x33, 0x3B, 0x82, 0xF6)), // Blue
                _ => new SolidColorBrush(Color.FromArgb(0x20, 0x94, 0xA3, 0xB8))
            };

        [JsonIgnore]
        public Brush PriorityBorderBrush =>
            Priority?.ToUpperInvariant() switch
            {
                "P1" => new SolidColorBrush(Color.FromArgb(0x80, 0xEF, 0x44, 0x44)),
                "P2" => new SolidColorBrush(Color.FromArgb(0x80, 0xF5, 0x9E, 0x0B)),
                "P3" => new SolidColorBrush(Color.FromArgb(0x80, 0x3B, 0x82, 0xF6)),
                _ => new SolidColorBrush(Color.FromArgb(0x50, 0x94, 0xA3, 0xB8))
            };

        [JsonIgnore]
        public Brush PriorityForegroundBrush =>
            Priority?.ToUpperInvariant() switch
            {
                "P1" => new SolidColorBrush(Color.FromArgb(0xFF, 0xFC, 0xA5, 0xA5)),
                "P2" => new SolidColorBrush(Color.FromArgb(0xFF, 0xFD, 0xE6, 0x8A)),
                "P3" => new SolidColorBrush(Color.FromArgb(0xFF, 0x93, 0xC5, 0xFD)),
                _ => new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1))
            };

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
