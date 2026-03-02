using Anfeta.UI.Models.Weblab;
using System;

public class BookmarkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string LocalPath { get; set; } = "";   // ✅ llave única
    public SearchSource Source { get; set; } = SearchSource.Local;
    public string Type { get; set; } = ""; // FILE/FOLDER
    public long Size { get; set; }
    public string Modified { get; set; } = "";
    public string Folder { get; set; } = "General";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
