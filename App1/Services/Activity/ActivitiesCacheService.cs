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
        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            text = text.ToLowerInvariant().Trim();

            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var chars = normalized
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                            != System.Globalization.UnicodeCategory.NonSpacingMark)
                .ToArray();

            return new string(chars).Normalize(System.Text.NormalizationForm.FormC);
        }
        public List<CachedActivityItem> SearchByTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<CachedActivityItem>();

            var query = Normalize(text);

            return _activities
                .Where(a => Normalize(a.Title).Contains(query))
                .OrderBy(a => a.Title.Length)
                .ToList();
        }

        public void Clear()
        {
            _activities.Clear();
            _lastUpdate = null;
        }
    }
}