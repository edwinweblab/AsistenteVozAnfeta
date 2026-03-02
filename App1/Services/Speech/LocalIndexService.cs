using System;
using System.Collections.Generic;
using System.Linq;
using Anfeta.UI.Models.Weblab;

namespace Anfeta.UI.Services.Speech
{
    public sealed class LocalIndexService
    {
        private readonly object _lock = new();
        private List<SearchResultRow>? _items;

        public bool HasData
        {
            get { lock (_lock) return _items is { Count: > 0 }; }
        }

        public int Count
        {
            get { lock (_lock) return _items?.Count ?? 0; }
        }

        public void Set(IEnumerable<SearchResultRow> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            lock (_lock) _items = items.ToList();
        }

        public List<SearchResultRow> GetAll()
        {
            lock (_lock) return _items?.ToList() ?? new List<SearchResultRow>();
        }

        public void Clear()
        {
            lock (_lock) _items = null;
        }
    }
}
