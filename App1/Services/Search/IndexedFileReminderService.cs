using Anfeta.UI.Models.Weblab;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace Anfeta.UI.Services.Search
{
    public sealed record IndexedFileReminder(
        string Identity,
        string Title,
        string Message,
        string Target,
        DateTimeOffset ReminderAt,
        SearchSource Source);

    public sealed class IndexedFileReminderService : IDisposable
    {
        private const string LS_FiredReminders =
            "Search.IndexedReminders.Fired.v1";

        private static readonly Regex ReminderPattern = new(
            @"(?<!\d)(?<date>\d{4}-\d{2}-\d{2})[ T](?<hour>\d{2})[:\-](?<minute>\d{2})(?!\d)",
            RegexOptions.Compiled |
            RegexOptions.CultureInvariant);

        private readonly Dictionary<string, DateTimeOffset> _fired =
            new(StringComparer.OrdinalIgnoreCase);

        private DispatcherQueueTimer? _scanTimer;
        private bool _started;
        private bool _disposed;

        public event EventHandler<IndexedFileReminder>? ReminderDue;

        public void Start(DispatcherQueue dispatcherQueue)
        {
            if (_started || _disposed)
                return;

            _started = true;

            LoadFiredReminders();
            PruneFiredReminders();

            _scanTimer = dispatcherQueue.CreateTimer();
            _scanTimer.Interval = TimeSpan.FromSeconds(30);
            _scanTimer.Tick += ScanTimer_Tick;
            _scanTimer.Start();

            ScanForDueReminders();
        }

        public void Stop()
        {
            if (!_started)
                return;

            _started = false;

            if (_scanTimer != null)
            {
                _scanTimer.Stop();
                _scanTimer.Tick -= ScanTimer_Tick;
                _scanTimer = null;
            }
        }

        private void ScanTimer_Tick(
            DispatcherQueueTimer sender,
            object args)
        {
            ScanForDueReminders();
        }

        private void ScanForDueReminders()
        {
            if (!_started || _disposed)
                return;

            var rows = App.LocalIndex
                .GetAll()
                .Where(row =>
                    row != null &&
                    !string.IsNullOrWhiteSpace(
                        row.DisplayName ?? row.Name))
                .ToList();

            if (rows.Count == 0)
                return;

            var now = DateTimeOffset.Now;

            // Solo recupera avisos recientes si ANFETA estaba cerrada
            // o si el índice terminó de cargar unos minutos después.
            var oldestAllowed =
                now.Subtract(TimeSpan.FromMinutes(15));

            foreach (var row in rows)
            {
                var visibleTitle =
                    (row.DisplayName ??
                     row.Name ??
                     string.Empty).Trim();

                if (!TryParseReminder(
                        visibleTitle,
                        out var reminderAt,
                        out var reminderMessage))
                {
                    continue;
                }

                if (reminderAt > now ||
                    reminderAt < oldestAllowed)
                {
                    continue;
                }

                var identity =
                    BuildReminderIdentity(
                        row,
                        reminderAt);

                if (_fired.ContainsKey(identity))
                    continue;

                _fired[identity] = now;
                SaveFiredReminders();

                ReminderDue?.Invoke(
                    this,
                    new IndexedFileReminder(
                        identity,
                        visibleTitle,
                        reminderMessage,
                        ResolveTarget(row),
                        reminderAt,
                        row.Source));
            }
        }

        private static bool TryParseReminder(
            string title,
            out DateTimeOffset reminderAt,
            out string message)
        {
            reminderAt = default;
            message = string.Empty;

            if (string.IsNullOrWhiteSpace(title))
                return false;

            var match =
                ReminderPattern.Match(title);

            if (!match.Success)
                return false;

            var raw =
                $"{match.Groups["date"].Value} " +
                $"{match.Groups["hour"].Value}:" +
                $"{match.Groups["minute"].Value}";

            if (!DateTime.TryParseExact(
                    raw,
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var localDateTime))
            {
                return false;
            }

            reminderAt =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        localDateTime,
                        DateTimeKind.Local));

            message =
                title.Remove(
                        match.Index,
                        match.Length)
                    .Trim(
                        ' ',
                        '-',
                        '–',
                        '—',
                        ':',
                        '|');

            if (string.IsNullOrWhiteSpace(message))
                message = title.Trim();

            return true;
        }

        private static string BuildReminderIdentity(
            SearchResultRow row,
            DateTimeOffset reminderAt)
        {
            var rowId =
                !string.IsNullOrWhiteSpace(row.ExternalId)
                    ? row.ExternalId.Trim()
                    : !string.IsNullOrWhiteSpace(row.NodeId)
                        ? row.NodeId.Trim()
                        : !string.IsNullOrWhiteSpace(row.Target)
                            ? row.Target.Trim()
                            : row.Name?.Trim() ?? "resultado";

            return $"{row.Source}|{rowId}|{reminderAt:O}";
        }

        private static string ResolveTarget(
            SearchResultRow row)
        {
            if (row.Source == SearchSource.Notion &&
                !string.IsNullOrWhiteSpace(row.ExternalUrl))
            {
                return row.ExternalUrl;
            }

            return row.Target ?? string.Empty;
        }

        private void LoadFiredReminders()
        {
            _fired.Clear();

            try
            {
                var raw =
                    ApplicationData.Current.LocalSettings.Values[
                        LS_FiredReminders] as string;

                if (string.IsNullOrWhiteSpace(raw))
                    return;

                var stored =
                    JsonSerializer.Deserialize<
                        Dictionary<string, DateTimeOffset>>(raw);

                if (stored == null)
                    return;

                foreach (var item in stored)
                    _fired[item.Key] = item.Value;
            }
            catch
            {
                _fired.Clear();
            }
        }

        private void SaveFiredReminders()
        {
            try
            {
                PruneFiredReminders();

                ApplicationData.Current.LocalSettings.Values[
                    LS_FiredReminders] =
                    JsonSerializer.Serialize(_fired);
            }
            catch
            {
                // La persistencia no debe bloquear ANFETA.
            }
        }

        private void PruneFiredReminders()
        {
            var cutoff =
                DateTimeOffset.Now.Subtract(
                    TimeSpan.FromDays(30));

            foreach (var key in _fired
                .Where(item => item.Value < cutoff)
                .Select(item => item.Key)
                .ToList())
            {
                _fired.Remove(key);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Stop();
        }
    }
}
