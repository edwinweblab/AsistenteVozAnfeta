namespace Anfeta.UI.Models
{
    public sealed class DropboxFileInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Type { get; set; } = "";      // FILE / FOLDER
        public long Size { get; set; }
        public string Modified { get; set; } = "";
    }
}
