using System;

namespace Anfeta.UI.Models
{
    public class SearchResultRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name { get; set; } = "";

        public string Target { get; set; } = ""; // ruta local o link dropbox

        public SearchSource Source { get; set; }

        // Texto amigable para la UI
        public string SourceText =>
            Source == SearchSource.Local ? "Local" : "Dropbox";
    }
}
