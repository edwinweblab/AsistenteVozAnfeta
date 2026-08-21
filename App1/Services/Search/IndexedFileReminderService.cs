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
        SearchSource Source,
        string RecipientTag,
        string RecipientName,
        string SenderTag,
        string SenderName,
        string PageId);

    public sealed class IndexedFileReminderService : IDisposable
    {
        private const string LS_FiredReminders =
            "Search.IndexedReminders.Fired.v1";

        private const string LS_CurrentUserTag =
            "Messaging.CurrentUserTag";

        private const string LS_SnoozedReminders =
            "Search.IndexedReminders.Snoozed.v1";

        private const string LS_MessagesReadState =
            "Messaging.ReadState.v1";

        private static readonly Regex ReminderPattern = new(
            @"(?<!\d)(?<date>\d{4}-\d{2}-\d{2})[ T](?<hour>\d{2})[:\-](?<minute>\d{2})(?!\d)",
            RegexOptions.Compiled |
            RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> RecipientNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["jjohn"] = "John",
                ["kkarl"] = "Karla",
                ["iisaia"] = "Isaias",
                ["eedua"] = "Sotelo",
                ["aacal"] = "Acalli",
                ["aandr"] = "Andrade",
                ["eemma"] = "Emmanuel",
                ["bbria"] = "Brian",
                ["ggena"] = "Genaro",
                ["nneft"] = "Neftali",
                ["__all__"] = "Todos los usuarios"
            };

        private readonly Dictionary<string, DateTimeOffset> _fired =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, SnoozedReminder> _snoozed =
            new(StringComparer.OrdinalIgnoreCase);

        public sealed record SnoozedReminder(
            IndexedFileReminder Reminder,
            DateTimeOffset DueAt);

        // Ventana normal para avisos recién vencidos. Además, Notion tiene
        // una recuperación controlada de 48 h para que un equipo apagado o una
        // alerta que llegó tarde no desaparezca para siempre. Se emite solo una
        // recuperación vieja por scan para no inundar de popups al iniciar.
        private static readonly TimeSpan FreshReminderWindow =
            TimeSpan.FromMinutes(15);

        private static readonly TimeSpan NotionCatchUpWindow =
            TimeSpan.FromHours(48);

        private const int MaxCatchUpRemindersPerScan = 1;

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
            LoadSnoozedReminders();
            PruneFiredReminders();

            _scanTimer = dispatcherQueue.CreateTimer();
            _scanTimer.Interval = TimeSpan.FromSeconds(30);
            _scanTimer.Tick += ScanTimer_Tick;
            _scanTimer.Start();

            ScanForDueReminders();
        }

        public void ScanNow()
        {
            if (!_started || _disposed)
                return;

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
                        row.Name))
                .ToList();

            if (rows.Count == 0)
                return;

            PruneInactiveSnoozedReminders();

            var now = DateTimeOffset.Now;

            FireDueSnoozedReminders(now);

            var freshCutoff =
                now.Subtract(FreshReminderWindow);

            var notionCatchUpCutoff =
                now.Subtract(NotionCatchUpWindow);

            var candidates =
                new List<(
                    SearchResultRow Row,
                    string VisibleTitle,
                    DateTimeOffset ReminderAt,
                    string Message,
                    string RecipientTag,
                    string RecipientName,
                    string SenderTag,
                    string SenderName,
                    bool IsCatchUp)>();

            foreach (var row in rows)
            {
                var visibleTitle =
                    StripReminderSourcePrefix(
                        row.Name);

                if (!TryParseReminder(
                        visibleTitle,
                        out var reminderAt,
                        out var reminderMessage,
                        out var recipientTag,
                        out var recipientName,
                        out var senderTag,
                        out var senderName))
                {
                    continue;
                }

                if (!ShouldShowForCurrentUser(recipientTag))
                    continue;

                if (row.Source == SearchSource.Notion &&
                    IsReminderMarkedAsRead(
                        row.ExternalId,
                        reminderAt))
                {
                    continue;
                }

                if (reminderAt > now)
                    continue;

                var isFresh =
                    reminderAt >= freshCutoff;

                var isCatchUp =
                    !isFresh &&
                    row.Source == SearchSource.Notion &&
                    reminderAt >= notionCatchUpCutoff;

                if (!isFresh && !isCatchUp)
                    continue;

                var identity =
                    BuildReminderIdentity(
                        row,
                        reminderAt);

                if (_fired.ContainsKey(identity))
                    continue;

                candidates.Add((
                    row,
                    visibleTitle,
                    reminderAt,
                    reminderMessage,
                    recipientTag,
                    recipientName,
                    senderTag,
                    senderName,
                    isCatchUp));
            }

            // Primero avisos frescos; después el pendiente antiguo más reciente.
            // Los demás pendientes viejos quedan sin marcar y saldrán de uno en
            // uno en scans posteriores (cada 30 s).
            var catchUpFired = 0;

            foreach (var candidate in candidates
                         .OrderBy(item => item.IsCatchUp)
                         .ThenByDescending(item => item.ReminderAt))
            {
                if (candidate.IsCatchUp &&
                    catchUpFired >= MaxCatchUpRemindersPerScan)
                {
                    continue;
                }

                var identity =
                    BuildReminderIdentity(
                        candidate.Row,
                        candidate.ReminderAt);

                if (_fired.ContainsKey(identity))
                    continue;

                _fired[identity] = now;
                SaveFiredReminders();

                if (candidate.IsCatchUp)
                    catchUpFired++;

                ReminderDue?.Invoke(
                    this,
                    new IndexedFileReminder(
                        identity,
                        candidate.VisibleTitle,
                        candidate.Message,
                        ResolveTarget(candidate.Row),
                        candidate.ReminderAt,
                        candidate.Row.Source,
                        candidate.RecipientTag,
                        candidate.RecipientName,
                        candidate.SenderTag,
                        candidate.SenderName,
                        candidate.Row.ExternalId ?? string.Empty));
            }
        }

        private static string StripReminderSourcePrefix(
            string? value)
        {
            var text =
                (value ?? string.Empty).Trim();

            if (!text.StartsWith(
                    "[",
                    StringComparison.Ordinal))
            {
                return text;
            }

            var close =
                text.IndexOf(']');

            if (close > 0 &&
                close < 60)
            {
                text = text
                    .Substring(close + 1)
                    .Trim();
            }

            return text;
        }

        private static bool ContainsCompletedMarker(
            string? value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Contains(
                       "[TERMINADO]",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReminderStillActive(
            IndexedFileReminder reminder)
        {
            if (reminder == null ||
                ContainsCompletedMarker(
                    reminder.Title))
            {
                return false;
            }

            if (reminder.Source != SearchSource.Notion ||
                string.IsNullOrWhiteSpace(
                    reminder.PageId))
            {
                return true;
            }

            if (IsReminderMarkedAsRead(
                    reminder.PageId,
                    reminder.ReminderAt))
            {
                return false;
            }

            var row =
                App.LocalIndex
                    .GetAll()
                    .FirstOrDefault(item =>
                        item != null &&
                        item.Source == SearchSource.Notion &&
                        string.Equals(
                            item.ExternalId,
                            reminder.PageId,
                            StringComparison.OrdinalIgnoreCase));

            if (row == null)
                return false;

            var currentTitle =
                StripReminderSourcePrefix(
                    row.Name);

            return !ContainsCompletedMarker(
                currentTitle);
        }

        private static bool IsReminderMarkedAsRead(
            string? pageId,
            DateTimeOffset reminderAt)
        {
            if (string.IsNullOrWhiteSpace(pageId))
                return false;

            try
            {
                var raw =
                    ApplicationData.Current.LocalSettings.Values[
                        LS_MessagesReadState] as string;

                if (string.IsNullOrWhiteSpace(raw))
                    return false;

                var readState = JsonSerializer.Deserialize<
                    Dictionary<string, DateTimeOffset>>(raw);

                return readState != null &&
                       readState.TryGetValue(
                           pageId.Trim(),
                           out var readAt) &&
                       readAt >= reminderAt;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseReminder(
            string title,
            out DateTimeOffset reminderAt,
            out string message,
            out string recipientTag,
            out string recipientName,
            out string senderTag,
            out string senderName)
        {
            reminderAt = default;
            message = string.Empty;
            recipientTag = string.Empty;
            recipientName = string.Empty;
            senderTag = string.Empty;
            senderName = string.Empty;

            if (string.IsNullOrWhiteSpace(title) ||
                ContainsCompletedMarker(title))
            {
                return false;
            }

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

            var firstToken = message
                .Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;

            if (RecipientNames.TryGetValue(
                    firstToken,
                    out var mappedName))
            {
                recipientTag = firstToken;
                recipientName = mappedName;

                message = message
                    .Substring(firstToken.Length)
                    .Trim(
                        ' ',
                        '-',
                        '–',
                        '—',
                        ':',
                        '|');
            }

            var senderMatch = Regex.Match(
                message,
                @"^(?:de:)(?<tag>[a-z0-9_-]+)(?:\s+|$)",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);

            if (senderMatch.Success)
            {
                var parsedSenderTag =
                    senderMatch.Groups["tag"].Value;

                if (RecipientNames.TryGetValue(
                        parsedSenderTag,
                        out var mappedSenderName))
                {
                    senderTag = parsedSenderTag;
                    senderName = mappedSenderName;
                }

                message = message
                    .Substring(senderMatch.Length)
                    .Trim(
                        ' ',
                        '-',
                        '–',
                        '—',
                        ':',
                        '|');
            }

            var markerFound = true;

            while (markerFound)
            {
                markerFound = false;

                if (message.StartsWith(
                        "[TERMINADO]",
                        StringComparison.OrdinalIgnoreCase))
                {
                    message = message
                        .Substring("[TERMINADO]".Length)
                        .Trim();
                    markerFound = true;
                }

                if (message.StartsWith(
                        "[RESPUESTA]",
                        StringComparison.OrdinalIgnoreCase))
                {
                    message = message
                        .Substring("[RESPUESTA]".Length)
                        .Trim();
                    markerFound = true;
                }
            }

            if (string.IsNullOrWhiteSpace(message))
                message = title.Trim();

            return true;
        }

        private static bool ShouldShowForCurrentUser(
            string recipientTag)
        {
            var currentUserTag =
                (ApplicationData.Current.LocalSettings.Values[
                    LS_CurrentUserTag] as string ?? string.Empty)
                .Trim();

            if (string.IsNullOrWhiteSpace(currentUserTag))
                return true;

            if (string.IsNullOrWhiteSpace(recipientTag) ||
                string.Equals(
                    recipientTag,
                    "__all__",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(
                currentUserTag,
                recipientTag,
                StringComparison.OrdinalIgnoreCase);
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


        public void Acknowledge(
            IndexedFileReminder reminder)
        {
            if (reminder == null)
                return;

            var pageId =
                (reminder.PageId ?? string.Empty)
                    .Trim();

            if (!string.IsNullOrWhiteSpace(pageId))
            {
                try
                {
                    var values =
                        ApplicationData.Current.LocalSettings.Values;

                    var raw =
                        values[LS_MessagesReadState] as string;

                    Dictionary<string, DateTimeOffset> readState;

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        readState =
                            new Dictionary<string, DateTimeOffset>(
                                StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        var restored =
                            JsonSerializer.Deserialize<
                                Dictionary<string, DateTimeOffset>>(raw);

                        readState =
                            restored == null
                                ? new Dictionary<string, DateTimeOffset>(
                                    StringComparer.OrdinalIgnoreCase)
                                : new Dictionary<string, DateTimeOffset>(
                                    restored,
                                    StringComparer.OrdinalIgnoreCase);
                    }

                    readState[pageId] =
                        DateTimeOffset.Now;

                    values[LS_MessagesReadState] =
                        JsonSerializer.Serialize(
                            readState);
                }
                catch
                {
                    // El cierre visual del aviso no debe fallar si Windows
                    // no permite persistir temporalmente el estado.
                }

                DismissPage(pageId);
            }

            var removed = false;

            foreach (var key in _snoozed
                .Where(item =>
                    string.Equals(
                        item.Value.Reminder.Identity,
                        reminder.Identity,
                        StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Key)
                .ToList())
            {
                removed |= _snoozed.Remove(key);
            }

            if (removed)
                SaveSnoozedReminders();
        }

        public void DismissPage(
            string? pageId)
        {
            var cleanPageId =
                (pageId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(cleanPageId))
                return;

            var removed = false;

            foreach (var key in _snoozed
                .Where(item =>
                    string.Equals(
                        item.Value.Reminder.PageId,
                        cleanPageId,
                        StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Key)
                .ToList())
            {
                removed |= _snoozed.Remove(key);
            }

            if (removed)
                SaveSnoozedReminders();
        }

        public void Snooze(
            IndexedFileReminder reminder,
            TimeSpan delay)
        {
            if (reminder == null || delay <= TimeSpan.Zero)
                return;

            var dueAt = DateTimeOffset.Now.Add(delay);
            var key = $"{reminder.Identity}|snooze";

            _snoozed[key] = new SnoozedReminder(
                reminder with
                {
                    ReminderAt = dueAt
                },
                dueAt);

            SaveSnoozedReminders();
        }

        private void PruneInactiveSnoozedReminders()
        {
            var inactiveKeys =
                _snoozed
                    .Where(item =>
                        !IsReminderStillActive(
                            item.Value.Reminder))
                    .Select(item => item.Key)
                    .ToList();

            if (inactiveKeys.Count == 0)
                return;

            foreach (var key in inactiveKeys)
                _snoozed.Remove(key);

            SaveSnoozedReminders();
        }

        private void FireDueSnoozedReminders(
            DateTimeOffset now)
        {
            var due = _snoozed
                .Where(item => item.Value.DueAt <= now)
                .ToList();

            foreach (var item in due)
            {
                _snoozed.Remove(item.Key);

                if (!IsReminderStillActive(
                        item.Value.Reminder))
                {
                    continue;
                }

                ReminderDue?.Invoke(
                    this,
                    item.Value.Reminder);
            }

            if (due.Count > 0)
                SaveSnoozedReminders();
        }

        private void LoadSnoozedReminders()
        {
            _snoozed.Clear();

            try
            {
                var raw =
                    ApplicationData.Current.LocalSettings.Values[
                        LS_SnoozedReminders] as string;

                if (string.IsNullOrWhiteSpace(raw))
                    return;

                var stored = JsonSerializer.Deserialize<
                    Dictionary<string, SnoozedReminder>>(raw);

                if (stored == null)
                    return;

                foreach (var item in stored)
                    _snoozed[item.Key] = item.Value;
            }
            catch
            {
                _snoozed.Clear();
            }
        }

        private void SaveSnoozedReminders()
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    LS_SnoozedReminders] =
                    JsonSerializer.Serialize(_snoozed);
            }
            catch
            {
            }
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