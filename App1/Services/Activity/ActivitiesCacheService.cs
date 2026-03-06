using System;
using System.Collections.Generic;
using System.Linq;
using Anfeta.UI.Models.Weblab;

namespace Anfeta.UI.Services.Activity
{
    public sealed class ActivitiesCacheService
    {
        private List<CachedActivityItem> _activities = new();
        private DateTimeOffset? _lastUpdate;

        public void SetActivities(List<CachedActivityItem> activities)
        {
            _activities = activities ?? new List<CachedActivityItem>();
            _lastUpdate = DateTimeOffset.Now;
        }

        public List<CachedActivityItem> GetAll()
        {
            return _activities;
        }

        public bool HasData()
        {
            return _activities.Count > 0;
        }

        public List<CachedActivityItem> SearchByTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<CachedActivityItem>();

            return _activities
                .Where(a => a.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public void Clear()
        {
            _activities.Clear();
            _lastUpdate = null;
        }
    }
}