namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}
namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    public class SolidColorBrush : Brush
    {
        public SolidColorBrush(Windows.UI.Color color) { }
    }
    public class ImageSource { }
}
namespace Microsoft.UI.Xaml.Controls
{
    public class IconSource { }
    public enum Symbol
    {
        Accept,
        Folder,
        Globe,
        Pictures,
        Video,
        Document
    }
}
namespace Windows.UI
{
    public struct Color
    {
        public static Color FromArgb(byte a, byte r, byte g, byte b) => default;
    }
}
namespace Anfeta.UI.Services.Notion
{
    public static class NotionCalendarService
    {
        public static bool TryGetStoredChecklistStats(string id, out NotionStats stats)
        {
            stats = default;
            return false;
        }
    }
    public struct NotionStats { public int Total; public int Completed; }
}
