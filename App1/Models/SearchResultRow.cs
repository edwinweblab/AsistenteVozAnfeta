using System;

namespace Anfeta.UI.Models
{
    public class SearchResultRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string NodeId { get; set; } = "";

        public string Name { get; set; } = "";

        public string Target { get; set; } = ""; // ruta local o link dropbox
        public string PathLower { get; set; } = "";

        public SearchSource Source { get; set; }
        public string Type { get; set; } = "";         // file / folder
        public long Size { get; set; }                 // bytes
        public string MimeType { get; set; } = "";
        public string ServerModified { get; set; } = "";
        public string SharedLink { get; set; } = "";   // si viene en /search
        public string SizeText => Size > 0 ? $"{Size / 1024:N0} KB" : "—";
        public string ModifiedText => string.IsNullOrWhiteSpace(ServerModified) ? "—" : ServerModified;


        // Texto amigable para la UI
        public string SourceText =>
            Source == SearchSource.Local ? "Local" : "Dropbox";
    }
}
