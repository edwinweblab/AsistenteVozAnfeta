using System;

namespace Anfeta.UI.Models
{
    public class BookmarkItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Title { get; set; } = "";

        public string Target { get; set; } = ""; // ruta local o link dropbox

        public SearchSource Source { get; set; }

        public string Folder { get; set; } = "General";

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    }
}
