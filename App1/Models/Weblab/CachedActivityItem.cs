using System;

namespace Anfeta.UI.Models.Weblab
{
    public sealed class CachedActivityItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public string Priority { get; set; } = "";
        public DateTimeOffset? DueStart { get; set; }
        public DateTimeOffset? DueEnd { get; set; }
    }
}