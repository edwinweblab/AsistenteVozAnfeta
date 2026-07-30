using Anfeta.UI.Models.Notion;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private const string MessagesCurrentUserKey =
            "Messaging.CurrentUserTag";

        private const string MessagesReadStateKey =
            "Messaging.ReadState.v1";

        private const string MessagesGroupModeKey =
            "Messaging.GroupMode";

        private const string MessagesAllRecipientsTag =
            "__all__";

        private readonly HashSet<string> _recentBroadcastFingerprints =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex MessagesReminderPattern = new(
            @"(?<!\d)(?<date>\d{4}-\d{2}-\d{2})[ T](?<hour>\d{2})[:\-](?<minute>\d{2})(?!\d)",
            RegexOptions.Compiled |
            RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> MessagesPeople =
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
                ["nneft"] = "Neftali"
            };

        private readonly ObservableCollection<MessageViewItem>
            _messageItems = new();

        private bool _messagesViewActive;
        private bool _messagesInitialized;
        private string _messagesFilter = "received";
        private string _messagesGroupMode = "none";
        private string _messagesSearchQuery = string.Empty;
        private readonly Dictionary<string, DateTimeOffset>
            _messagesReadState =
                new(StringComparer.OrdinalIgnoreCase);
        private CollectionViewSource?
            _messagesGroupedView;
        private DispatcherTimer? _messagesRefreshTimer;
        private readonly NotionMessageThreadService
            _messageThreadService = new();

        private static event Action<string>?
            ConversationOpenRequested;

        private static string
            _pendingConversationPageId = string.Empty;

        private bool
            _messagesNavigationBridgeAttached;

        private sealed class MessageGroup :
            ObservableCollection<MessageViewItem>
        {
            public string Name { get; }

            public MessageGroup(
                string name,
                IEnumerable<MessageViewItem> items)
                : base(items)
            {
                Name = name;
            }
        }

        private sealed class PendingMessageAttachment
        {
            public string Path { get; init; } = string.Empty;
            public string FileName { get; init; } = string.Empty;
        }

        private sealed class MessageViewItem
        {
            public SearchResultRow Row { get; init; } = null!;
            public string OriginalTitle { get; init; } = string.Empty;
            public string Message { get; init; } = string.Empty;
            public string RecipientTag { get; init; } = string.Empty;
            public string RecipientName { get; init; } = string.Empty;
            public string SenderTag { get; init; } = string.Empty;
            public string SenderName { get; init; } = string.Empty;
            public DateTimeOffset ScheduledAt { get; init; }
            public bool IsCompleted { get; init; }
            public bool IsReplyNotification { get; init; }
            public bool IsUnread { get; init; }

            public bool IsReviewAlert =>
                Message.StartsWith(
                    "Actividad lista para revisión",
                    StringComparison.OrdinalIgnoreCase);

            public bool IsTutorial { get; init; }

            public Visibility TutorialBadgeVisibility =>
                IsTutorial
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            public Visibility TutorialActionsVisibility =>
                IsTutorial && !IsCompleted
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            public string ReviewAlertKey =>
                IsReviewAlert
                    ? NormalizeReviewAlertKey(Message)
                    : string.Empty;

            public bool CanComplete =>
                !IsReviewAlert ||
                IsCurrentUserReviewApprover();

            public string CompleteButtonToolTip =>
                IsReviewAlert && !CanComplete
                    ? "Solo John o Genaro pueden marcar esta alerta como atendida."
                    : IsReviewAlert
                        ? "Cualquiera de los dos revisores puede cerrar la alerta."
                        : "Cambiar el estado del mensaje.";

            public Visibility ReviewOriginalButtonVisibility =>
                IsReviewAlert
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            public Visibility MessageEditActionsVisibility =>
                IsReviewAlert
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            public string DeleteButtonText =>
                IsReviewAlert
                    ? "Eliminar notificación"
                    : "Eliminar";

            public string DeleteButtonToolTip =>
                IsReviewAlert
                    ? "Elimina únicamente esta notificación. La actividad original no se modifica."
                    : "Mover este mensaje a la papelera de Notion.";

            public Visibility UnreadVisibility =>
                IsUnread
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            public string DirectionLabel =>
                $"De: {DisplayPerson(SenderName, SenderTag)} · Para: {DisplayPerson(RecipientName, RecipientTag)}";

            public string ScheduledLabel =>
                $"Programado: {ScheduledAt:dd/MM/yyyy HH:mm}";

            public bool IsOverdue =>
                !IsCompleted &&
                ScheduledAt < DateTimeOffset.Now;

            public string StatusLabel =>
                IsCompleted
                    ? "Estado: Terminado"
                    : IsOverdue
                        ? "Estado: Vencido"
                        : "Estado: Pendiente";

            public Brush CardBackground =>
                new SolidColorBrush(
                    IsCompleted
                        ? Color.FromArgb(72, 30, 110, 72)
                        : IsOverdue
                            ? Color.FromArgb(72, 145, 56, 56)
                            : Color.FromArgb(62, 154, 123, 35));

            public Brush CardBorderBrush =>
                new SolidColorBrush(
                    IsCompleted
                        ? Color.FromArgb(220, 52, 211, 153)
                        : IsOverdue
                            ? Color.FromArgb(220, 248, 113, 113)
                            : Color.FromArgb(220, 251, 191, 36));

            public string IdentifierLabel =>
                $"ID: {(string.IsNullOrWhiteSpace(Row.ExternalId) ? Row.NodeId : Row.ExternalId)}";

            public string CompleteButtonText =>
                IsReviewAlert
                    ? IsCompleted
                        ? "Reabrir alerta"
                        : "Marcar como atendida"
                    : IsCompleted
                        ? "Reabrir"
                        : "Terminar";

            public string LastReplyLabel =>
                IsReplyNotification
                    ? $"Última respuesta: {DisplayPerson(SenderName, SenderTag)} · {ScheduledAt:dd/MM HH:mm}"
                    : string.Empty;

            public Visibility LastReplyVisibility =>
                IsReplyNotification
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            public string WaitingForLabel =>
                IsCompleted
                    ? "Conversación cerrada"
                    : $"Esperando respuesta de: {DisplayPerson(RecipientName, RecipientTag)}";

            private static bool IsCurrentUserReviewApprover()
            {
                var currentUser =
                    (ApplicationData.Current.LocalSettings.Values[
                        MessagesCurrentUserKey] as string ??
                     string.Empty).Trim();

                return string.Equals(
                           currentUser,
                           "jjohn",
                           StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(
                           currentUser,
                           "ggena",
                           StringComparison.OrdinalIgnoreCase);
            }

            private static string NormalizeReviewAlertKey(
                string value)
            {
                return Regex.Replace(
                        (value ?? string.Empty)
                            .Trim()
                            .ToLowerInvariant(),
                        @"\s+",
                        " ")
                    .Trim();
            }

            public static string DisplayPerson(
                string name,
                string tag)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    !string.IsNullOrWhiteSpace(tag))
                {
                    return $"{name} ({tag})";
                }

                if (!string.IsNullOrWhiteSpace(name))
                    return name;

                if (!string.IsNullOrWhiteSpace(tag))
                    return tag;

                return "Sin identificar";
            }
        }

        public static void RequestOpenConversation(
            string pageId)
        {
            var clean =
                (pageId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return;

            _pendingConversationPageId = clean;
            ConversationOpenRequested?.Invoke(clean);
        }

        private void AttachMessagesNavigationBridge()
        {
            if (_messagesNavigationBridgeAttached)
                return;

            ConversationOpenRequested +=
                OnConversationOpenRequested;

            _messagesNavigationBridgeAttached = true;

            if (!string.IsNullOrWhiteSpace(
                    _pendingConversationPageId))
            {
                OnConversationOpenRequested(
                    _pendingConversationPageId);
            }
        }

        private void DetachMessagesNavigationBridge()
        {
            if (!_messagesNavigationBridgeAttached)
                return;

            ConversationOpenRequested -=
                OnConversationOpenRequested;

            _messagesNavigationBridgeAttached = false;
        }

        private void OnConversationOpenRequested(
            string pageId)
        {
            if (!IsLoaded ||
                Visibility != Visibility.Visible)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(
                async () =>
                {
                    await OpenConversationByPageIdAsync(
                        pageId);
                });
        }

        private async Task OpenConversationByPageIdAsync(
            string pageId)
        {
            var clean =
                (pageId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return;

            await ShowMessagesViewAsync();

            foreach (var item in
                     MessagesFilterCombo.Items
                         .OfType<ComboBoxItem>())
            {
                if (string.Equals(
                        item.Tag?.ToString(),
                        "conversations",
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessagesFilterCombo.SelectedItem = item;
                    break;
                }
            }

            _messagesFilter = "conversations";
            RefreshMessagesView();

            var message =
                _messageItems.FirstOrDefault(item =>
                    string.Equals(
                        item.Row.ExternalId,
                        clean,
                        StringComparison.OrdinalIgnoreCase));

            if (message == null)
            {
                StatusText.Text =
                    "Estado: La conversación todavía no está en el índice. Pulsa Actualizar Notion y vuelve a intentar.";
                return;
            }

            _pendingConversationPageId = string.Empty;

            await ShowMessageConversationAsync(
                message,
                focusReply: true);
        }

        private void InitializeMessagesView()
        {
            if (_messagesInitialized)
                return;

            _messagesInitialized = true;

            LoadMessagesReadState();

            _messagesGroupMode =
                ApplicationData.Current.LocalSettings.Values[
                    MessagesGroupModeKey] as string ??
                "none";

            MessagesList.ItemsSource = _messageItems;
            MessagesFilterCombo.SelectedIndex = 0;

            if (MessagesGroupCombo != null)
            {
                var selectedGroup =
                    MessagesGroupCombo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item =>
                            string.Equals(
                                item.Tag?.ToString(),
                                _messagesGroupMode,
                                StringComparison.OrdinalIgnoreCase));

                MessagesGroupCombo.SelectedItem =
                    selectedGroup ??
                    MessagesGroupCombo.Items[0];
            }

            _messagesRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(25)
            };

            _messagesRefreshTimer.Tick += (_, __) =>
            {
                if (_messagesViewActive)
                    RefreshMessagesView();
            };

            // Si se abre el calendario mientras Mensajes está visible,
            // se cierra Mensajes para que ambas vistas nunca se superpongan.
            ToggleCalendarView.Click += (_, __) =>
            {
                if (ToggleCalendarView.IsChecked == true &&
                    _messagesViewActive)
                {
                    CloseMessagesView();
                }
            };
        }

        private async void ToggleMessagesView_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (ToggleMessagesView.IsChecked == true)
            {
                if (_calendarViewActive)
                    CloseCalendarView();

                await ShowMessagesViewAsync();
            }
            else
            {
                CloseMessagesView();
            }
        }

        private Task ShowMessagesViewAsync()
        {
            InitializeMessagesView();

            _messagesViewActive = true;
            MessagesHost.Visibility = Visibility.Visible;
            ToggleMessagesView.IsChecked = true;

            RefreshMessagesView();
            _messagesRefreshTimer?.Stop();
            _messagesRefreshTimer?.Start();

            StatusText.Text =
                "Estado: Vista de mensajes abierta ✅";

            return Task.CompletedTask;
        }

        private void CloseMessagesView()
        {
            _messagesViewActive = false;
            _messagesRefreshTimer?.Stop();

            if (MessagesHost != null)
                MessagesHost.Visibility = Visibility.Collapsed;

            if (ToggleMessagesView != null)
                ToggleMessagesView.IsChecked = false;

            ModeText.Text =
                $"Modo: Buscar ({GetSourceScopeLabel()})";

            CountText.Text =
                $"{Results.Count} resultados";
        }

        private void MessagesClose_Click(
            object sender,
            RoutedEventArgs e)
        {
            CloseMessagesView();
        }

        private void MessagesRefresh_Click(
            object sender,
            RoutedEventArgs e)
        {
            RefreshMessagesView();

            StatusText.Text =
                "Estado: Mensajes actualizados ✅";
        }

        private void MessagesFilterCombo_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (MessagesFilterCombo.SelectedItem is ComboBoxItem item)
            {
                _messagesFilter =
                    item.Tag?.ToString() ?? "received";
            }

            if (_messagesViewActive)
                RefreshMessagesView();
        }

        private void MessagesGroupCombo_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (MessagesGroupCombo.SelectedItem is
                ComboBoxItem item)
            {
                _messagesGroupMode =
                    item.Tag?.ToString() ??
                    "none";

                ApplicationData.Current.LocalSettings.Values[
                    MessagesGroupModeKey] =
                    _messagesGroupMode;
            }

            if (_messagesViewActive)
                RefreshMessagesView();
        }

        private void MessagesSearchBox_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            _messagesSearchQuery =
                (MessagesSearchBox?.Text ?? string.Empty)
                    .Trim();

            if (_messagesViewActive)
                RefreshMessagesView();
        }

        private void RefreshMessagesView()
        {
            var currentUserTag =
                GetCurrentMessagesUserTag();

            var parsed = App.LocalIndex
                .GetAll()
                .Where(row =>
                    row.Source == SearchSource.Notion &&
                    string.Equals(
                        row.ExternalSourceName,
                        "Revisiones",
                        StringComparison.OrdinalIgnoreCase))
                .Select(TryCreateMessageViewItem)
                .Where(item => item != null)
                .Cast<MessageViewItem>();

            parsed = _messagesFilter switch
            {
                "received" =>
                    parsed.Where(item =>
                        !item.IsCompleted &&
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        string.Equals(
                            item.RecipientTag,
                            currentUserTag,
                            StringComparison.OrdinalIgnoreCase)),

                "sent" =>
                    parsed.Where(item =>
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        string.Equals(
                            item.SenderTag,
                            currentUserTag,
                            StringComparison.OrdinalIgnoreCase)),

                "conversations" =>
                    parsed.Where(item =>
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        (string.Equals(
                             item.SenderTag,
                             currentUserTag,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             item.RecipientTag,
                             currentUserTag,
                             StringComparison.OrdinalIgnoreCase))),

                "overdue" =>
                    parsed.Where(item =>
                        item.IsOverdue &&
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        string.Equals(
                            item.RecipientTag,
                            currentUserTag,
                            StringComparison.OrdinalIgnoreCase)),

                "completed" =>
                    parsed.Where(item =>
                        item.IsCompleted &&
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        (string.Equals(
                             item.SenderTag,
                             currentUserTag,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             item.RecipientTag,
                             currentUserTag,
                             StringComparison.OrdinalIgnoreCase))),

                _ =>
                    parsed.Where(item =>
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        (string.Equals(
                             item.SenderTag,
                             currentUserTag,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             item.RecipientTag,
                             currentUserTag,
                             StringComparison.OrdinalIgnoreCase)))
            };

            if (!string.IsNullOrWhiteSpace(
                    _messagesSearchQuery))
            {
                var query =
                    _messagesSearchQuery.Trim();

                parsed = parsed.Where(item =>
                    item.Message.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase) ||
                    item.SenderName.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase) ||
                    item.SenderTag.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase) ||
                    item.RecipientName.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase) ||
                    item.RecipientTag.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase));
            }

            var finalItems = parsed
                .OrderBy(item => item.IsCompleted)
                .ThenBy(item => item.ScheduledAt)
                .ToList();

            _messageItems.Clear();

            foreach (var item in finalItems)
                _messageItems.Add(item);

            ApplyMessagesGrouping(finalItems);
            UpdateMessagesUnreadBadge();

            var currentUserName =
                MessagesPeople.TryGetValue(
                    currentUserTag,
                    out var mappedCurrentName)
                    ? mappedCurrentName
                    : string.IsNullOrWhiteSpace(currentUserTag)
                        ? "sin usuario configurado"
                        : currentUserTag;

            var pendingCount =
                _messageItems.Count(item =>
                    !item.IsCompleted);

            var summaryLabel =
                _messagesFilter switch
                {
                    "received" =>
                        $"{_messageItems.Count} recibido(s)",
                    "sent" =>
                        $"{_messageItems.Count} enviado(s)",
                    "conversations" =>
                        $"{_messageItems.Count} conversación(es) activa(s)",
                    "overdue" =>
                        $"{_messageItems.Count} vencido(s)",
                    "completed" =>
                        $"{_messageItems.Count} terminado(s)",
                    _ =>
                        pendingCount == 0
                            ? $"{_messageItems.Count} mensaje(s)"
                            : $"{_messageItems.Count} mensaje(s) · {pendingCount} activo(s)"
                };

            MessagesSummaryText.Text =
                string.IsNullOrWhiteSpace(currentUserTag)
                    ? "Selecciona un usuario en Configuración para ver sus mensajes."
                    : $"{summaryLabel} · Usuario: {currentUserName}";

            MessagesEmptyState.Visibility =
                _messageItems.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            MessagesList.Visibility =
                _messageItems.Count == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            ModeText.Text =
                $"Modo: Mensajes ({GetMessagesFilterLabel()})";

            CountText.Text =
                $"{_messageItems.Count} mensajes";
        }

        private void ApplyMessagesGrouping(
            IReadOnlyList<MessageViewItem> items)
        {
            if (MessagesList == null)
                return;

            if (string.Equals(
                    _messagesGroupMode,
                    "none",
                    StringComparison.OrdinalIgnoreCase))
            {
                _messagesGroupedView = null;
                MessagesList.ItemsSource =
                    _messageItems;
                return;
            }

            var groups = items
                .GroupBy(GetMessageGroupName)
                .OrderBy(group => group.Key)
                .Select(group =>
                    new MessageGroup(
                        group.Key,
                        group))
                .ToList();

            _messagesGroupedView =
                new CollectionViewSource
                {
                    Source = groups,
                    IsSourceGrouped = true
                };

            MessagesList.ItemsSource =
                _messagesGroupedView.View;
        }

        private string GetMessageGroupName(
            MessageViewItem item)
        {
            return _messagesGroupMode switch
            {
                "person" =>
                    string.Equals(
                        item.RecipientTag,
                        GetCurrentMessagesUserTag(),
                        StringComparison.OrdinalIgnoreCase)
                        ? $"De {MessageViewItem.DisplayPerson(
                            item.SenderName,
                            item.SenderTag)}"
                        : $"Para {MessageViewItem.DisplayPerson(
                            item.RecipientName,
                            item.RecipientTag)}",

                "domainproject" =>
                    ExtractMessageDomainProject(item.Message),

                "domain" =>
                    ExtractMessageDomainProject(item.Message),

                "project" =>
                    ExtractMessageProject(item.Message),

                "month" =>
                    item.ScheduledAt
                        .ToString(
                            "MMMM yyyy",
                            new CultureInfo("es-MX")),

                _ => "Mensajes"
            };
        }

        private static string ExtractMessageDomain(
            string text)
        {
            var match = Regex.Match(
                text ?? string.Empty,
                @"(?<![\w@])(?:https?://)?(?:www\.)?" +
                @"(?<domain>(?:[a-z0-9-]+\.)+" +
                @"(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|" +
                @"com|mx|org|net|io|co|app|dev))",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);

            return match.Success
                ? match.Groups["domain"]
                    .Value
                    .ToLowerInvariant()
                : "Sin dominio";
        }

        private static string ExtractMessageDomainProject(
            string text)
        {
            var domain =
                ExtractMessageDomain(text);

            var project =
                ExtractMessageProject(text);

            var hasDomain =
                !string.Equals(
                    domain,
                    "Sin dominio",
                    StringComparison.OrdinalIgnoreCase);

            var hasProject =
                !string.Equals(
                    project,
                    "Sin tipo de proyecto",
                    StringComparison.OrdinalIgnoreCase);

            if (hasDomain && hasProject)
                return $"{domain} / {project}";

            if (hasDomain)
                return $"{domain} / Sin tipo de proyecto";

            if (hasProject)
                return $"Sin dominio / {project}";

            return "Sin dominio / Sin tipo de proyecto";
        }

        private static string ExtractMessageProject(
            string text)
        {
            var match = Regex.Match(
                text ?? string.Empty,
                @"(?<![\p{L}\p{Nd}_])(?<project>sseo|aapli|aads|wwebs)(?![\p{L}\p{Nd}_])",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);

            return match.Success
                ? match.Groups["project"]
                    .Value
                    .ToLowerInvariant()
                : "Sin tipo de proyecto";
        }

        private bool IsMessageUnread(
            string pageId,
            string recipientTag,
            DateTime localDate,
            bool isReplyNotification)
        {
            var currentUser =
                GetCurrentMessagesUserTag();

            if (!isReplyNotification ||
                string.IsNullOrWhiteSpace(pageId) ||
                !string.Equals(
                    recipientTag,
                    currentUser,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var messageDate =
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        localDate,
                        DateTimeKind.Local));

            return !_messagesReadState.TryGetValue(
                       pageId,
                       out var readAt) ||
                   messageDate > readAt;
        }

        private void MarkMessageAsRead(
            MessageViewItem message)
        {
            if (message == null ||
                string.IsNullOrWhiteSpace(
                    message.Row.ExternalId))
            {
                return;
            }

            _messagesReadState[
                message.Row.ExternalId] =
                DateTimeOffset.Now;

            SaveMessagesReadState();
            UpdateMessagesUnreadBadge();
        }

        private void LoadMessagesReadState()
        {
            _messagesReadState.Clear();

            try
            {
                var raw =
                    ApplicationData.Current.LocalSettings.Values[
                        MessagesReadStateKey] as string;

                if (string.IsNullOrWhiteSpace(raw))
                    return;

                var restored =
                    System.Text.Json.JsonSerializer.Deserialize<
                        Dictionary<string, DateTimeOffset>>(raw);

                if (restored == null)
                    return;

                foreach (var item in restored)
                    _messagesReadState[item.Key] = item.Value;
            }
            catch
            {
                _messagesReadState.Clear();
            }
        }

        private void SaveMessagesReadState()
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    MessagesReadStateKey] =
                    System.Text.Json.JsonSerializer.Serialize(
                        _messagesReadState);
            }
            catch
            {
            }
        }

        private void UpdateMessagesUnreadBadge()
        {
            if (MessagesUnreadBadge == null ||
                MessagesUnreadBadgeText == null)
            {
                return;
            }

            var count =
                App.LocalIndex
                    .GetAll()
                    .Where(row =>
                        row.Source ==
                            SearchSource.Notion &&
                        string.Equals(
                            row.ExternalSourceName,
                            "Revisiones",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(TryCreateMessageViewItem)
                    .Where(item =>
                        item != null &&
                        item.IsUnread)
                    .Count();

            MessagesUnreadBadge.Visibility =
                count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            MessagesUnreadBadgeText.Text =
                count > 99
                    ? "99+"
                    : count.ToString(
                        CultureInfo.InvariantCulture);
        }

        private string GetMessagesFilterLabel()
        {
            return MessagesFilterCombo.SelectedItem is ComboBoxItem item
                ? item.Content?.ToString() ?? "Mensajes"
                : "Mensajes";
        }

        private static string GetCurrentMessagesUserTag()
        {
            return (
                ApplicationData.Current.LocalSettings.Values[
                    MessagesCurrentUserKey] as string ??
                string.Empty).Trim();
        }

        private MessageViewItem?
            TryCreateMessageViewItem(
                SearchResultRow row)
        {
            var title =
                StripMessageSourcePrefix(
                    row.Name);

            var dateMatch =
                MessagesReminderPattern.Match(title);

            if (!dateMatch.Success)
                return null;

            var rawDate =
                $"{dateMatch.Groups["date"].Value} " +
                $"{dateMatch.Groups["hour"].Value}:" +
                $"{dateMatch.Groups["minute"].Value}";

            if (!DateTime.TryParseExact(
                    rawDate,
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var localDate))
            {
                return null;
            }

            var remainder = title
                .Remove(
                    dateMatch.Index,
                    dateMatch.Length)
                .Trim(
                    ' ',
                    '-',
                    '–',
                    '—',
                    ':',
                    '|');

            var tokens = remainder.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            var recipientTag =
                tokens.FirstOrDefault() ?? string.Empty;

            if (!MessagesPeople.TryGetValue(
                    recipientTag,
                    out var recipientName))
            {
                recipientTag = string.Empty;
                recipientName = string.Empty;
            }
            else
            {
                remainder = remainder
                    .Substring(tokens[0].Length)
                    .Trim();
            }

            var senderTag = string.Empty;
            var senderName = string.Empty;

            var senderMatch = Regex.Match(
                remainder,
                @"^(?:de:)(?<tag>[a-z0-9_-]+)(?:\s+|$)",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);

            if (senderMatch.Success)
            {
                senderTag =
                    senderMatch.Groups["tag"].Value;

                MessagesPeople.TryGetValue(
                    senderTag,
                    out senderName);

                remainder = remainder
                    .Substring(senderMatch.Length)
                    .Trim();
            }

            var completed = false;
            var isReplyNotification = false;
            var isTutorial = false;
            var markerFound = true;

            while (markerFound)
            {
                markerFound = false;

                if (remainder.StartsWith(
                        "[TERMINADO]",
                        StringComparison.OrdinalIgnoreCase))
                {
                    completed = true;
                    remainder = remainder
                        .Substring("[TERMINADO]".Length)
                        .Trim();
                    markerFound = true;
                }

                if (remainder.StartsWith(
                        "[RESPUESTA]",
                        StringComparison.OrdinalIgnoreCase))
                {
                    isReplyNotification = true;
                    remainder = remainder
                        .Substring("[RESPUESTA]".Length)
                        .Trim();
                    markerFound = true;
                }

                if (remainder.StartsWith(
                        "[TUTORIAL]",
                        StringComparison.OrdinalIgnoreCase))
                {
                    isTutorial = true;
                    remainder = remainder
                        .Substring("[TUTORIAL]".Length)
                        .Trim();
                    markerFound = true;
                }
            }

            if (string.IsNullOrWhiteSpace(remainder))
                remainder = "Mensaje sin texto";

            return new MessageViewItem
            {
                Row = row,
                OriginalTitle = title,
                Message = remainder,
                RecipientTag = recipientTag,
                RecipientName = recipientName ?? string.Empty,
                SenderTag = senderTag,
                SenderName = senderName ?? string.Empty,
                ScheduledAt = new DateTimeOffset(
                    DateTime.SpecifyKind(
                        localDate,
                        DateTimeKind.Local)),
                IsCompleted = completed,
                IsReplyNotification = isReplyNotification,
                IsTutorial = isTutorial,
                IsUnread =
                    IsMessageUnread(
                        row.ExternalId,
                        recipientTag,
                        localDate,
                        isReplyNotification)
            };
        }

        private static string StripMessageSourcePrefix(
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

        private async void MessagesNew_Click(
            object sender,
            RoutedEventArgs e)
        {
            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var recipientCombo =
                BuildMessagesPersonCombo(
                    string.Empty,
                    includeAll: true);

            recipientCombo.Header =
                "Destinatario";

            var messageTypeCombo =
                new ComboBox
                {
                    Header = "Tipo de mensaje",
                    MinWidth = 360,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch
                };

            messageTypeCombo.Items.Add(
                new ComboBoxItem
                {
                    Content = "Mensaje normal",
                    Tag = "normal"
                });

            messageTypeCombo.Items.Add(
                new ComboBoxItem
                {
                    Content = "Tutorial / aviso importante",
                    Tag = "tutorial"
                });

            messageTypeCombo.SelectedIndex = 0;

            var subjectBox =
                new TextBox
                {
                    Header = "Asunto",
                    PlaceholderText =
                        "Ejemplo: Revisar propuesta del cliente"
                };

            var messageBox =
                new TextBox
                {
                    Header = "Mensaje",
                    PlaceholderText =
                        "Escribe el contenido del mensaje...",
                    AcceptsReturn = true,
                    TextWrapping =
                        TextWrapping.Wrap,
                    MinHeight = 90
                };

            var datePicker =
                new DatePicker
                {
                    Header = "Fecha",
                    Date =
                        DateTimeOffset.Now
                            .AddMinutes(5)
                };

            var timePicker =
                new TimePicker
                {
                    Header = "Hora",
                    Time =
                        DateTimeOffset.Now
                            .AddMinutes(5)
                            .TimeOfDay
                };

            var pending =
                new ObservableCollection<
                    PendingMessageAttachment>();

            var filesPanel =
                new StackPanel
                {
                    Spacing = 4,
                    Visibility =
                        Visibility.Collapsed
                };

            void RefreshFiles()
            {
                filesPanel.Children.Clear();

                foreach (var item in pending)
                {
                    var row =
                        new Grid
                        {
                            ColumnSpacing = 8
                        };

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width =
                                new GridLength(
                                    1,
                                    GridUnitType.Star)
                        });
                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width =
                                GridLength.Auto
                        });

                    var name =
                        BuildPendingMessageAttachmentPreview(
                            item);

                    var remove =
                        new Button
                        {
                            Content = "Quitar",
                            Tag = item
                        };

                    remove.Click +=
                        (_, __) =>
                        {
                            if (remove.Tag is
                                PendingMessageAttachment selected)
                            {
                                pending.Remove(selected);
                                RefreshFiles();
                            }
                        };

                    Grid.SetColumn(name, 0);
                    row.Children.Add(name);
                    Grid.SetColumn(remove, 1);
                    row.Children.Add(remove);
                    filesPanel.Children.Add(row);
                }

                filesPanel.Visibility =
                    pending.Count > 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }

            var attach =
                new Button
                {
                    Content = "📎 Adjuntar archivos",
                    HorizontalAlignment =
                        HorizontalAlignment.Left
                };

            var uploadProgressBar =
                new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0,
                    Visibility = Visibility.Collapsed
                };

            var uploadProgressText =
                new TextBlock
                {
                    Opacity = 0.72,
                    Visibility = Visibility.Collapsed
                };

            var status =
                new TextBlock
                {
                    Opacity = 0.72,
                    TextWrapping =
                        TextWrapping.Wrap
                };

            attach.Click +=
                async (_, __) =>
                {
                    try
                    {
                        var picker =
                            new FileOpenPicker
                            {
                                SuggestedStartLocation =
                                    PickerLocationId.Downloads
                            };

                        picker.FileTypeFilter.Add("*");

                        var hwnd =
                            WindowNative.GetWindowHandle(
                                App.MainWindowInstance);

                        InitializeWithWindow.Initialize(
                            picker,
                            hwnd);

                        var selected =
                            await picker
                                .PickMultipleFilesAsync();

                        foreach (var file in selected)
                        {
                            if (!pending.Any(item =>
                                    string.Equals(
                                        item.Path,
                                        file.Path,
                                        StringComparison.OrdinalIgnoreCase)))
                            {
                                pending.Add(
                                    new PendingMessageAttachment
                                    {
                                        Path = file.Path,
                                        FileName = file.Name
                                    });
                            }
                        }

                        RefreshFiles();
                    }
                    catch (Exception ex)
                    {
                        status.Text =
                            $"No se pudieron seleccionar archivos → {ex.Message}";
                    }
                };

            var dateRow =
                new Grid
                {
                    ColumnSpacing = 8
                };

            dateRow.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });
            dateRow.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            Grid.SetColumn(datePicker, 0);
            dateRow.Children.Add(datePicker);
            Grid.SetColumn(timePicker, 1);
            dateRow.Children.Add(timePicker);

            var panel =
                new StackPanel
                {
                    Width = 510,
                    Spacing = 10
                };

            panel.Children.Add(recipientCombo);
            panel.Children.Add(messageTypeCombo);
            panel.Children.Add(subjectBox);
            panel.Children.Add(messageBox);
            panel.Children.Add(dateRow);
            panel.Children.Add(attach);
            panel.Children.Add(filesPanel);
            panel.Children.Add(uploadProgressBar);
            panel.Children.Add(uploadProgressText);
            panel.Children.Add(status);

            var dialog =
                new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Nuevo mensaje",
                    Content = panel,
                    PrimaryButtonText = "Crear y enviar",
                    CloseButtonText = "Cancelar",
                    DefaultButton =
                        ContentDialogButton.Primary
                };

            dialog.PrimaryButtonClick +=
                async (_, args) =>
                {
                    args.Cancel = true;

                    if (recipientCombo.SelectedItem is not
                        ComboBoxItem selectedRecipient)
                    {
                        status.Text =
                            "Selecciona un destinatario.";
                        return;
                    }

                    var subject =
                        (subjectBox.Text ??
                         string.Empty).Trim();

                    var body =
                        (messageBox.Text ??
                         string.Empty).Trim();

                    if (string.IsNullOrWhiteSpace(subject))
                    {
                        status.Text =
                            "Escribe un asunto.";
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(body) &&
                        pending.Count == 0)
                    {
                        status.Text =
                            "Escribe un mensaje o adjunta un archivo.";
                        return;
                    }

                    var selectedRecipientTag =
                        selectedRecipient.Tag?
                            .ToString() ??
                        string.Empty;

                    var isTutorial =
                        messageTypeCombo.SelectedItem is ComboBoxItem
                            selectedType &&
                        string.Equals(
                            selectedType.Tag?.ToString(),
                            "tutorial",
                            StringComparison.OrdinalIgnoreCase);

                    var isBroadcast =
                        string.Equals(
                            selectedRecipientTag,
                            MessagesAllRecipientsTag,
                            StringComparison.OrdinalIgnoreCase);

                    var recipientTags =
                        isBroadcast
                            ? MessagesPeople.Keys
                                .OrderBy(tag => MessagesPeople[tag])
                                .ToList()
                            : new List<string>
                            {
                                selectedRecipientTag
                            };

                    var authorTag =
                        GetCurrentMessagesUserTag();

                    var authorName =
                        MessagesPeople.TryGetValue(
                            authorTag,
                            out var mappedAuthor)
                            ? mappedAuthor
                            : authorTag;

                    var scheduled =
                        new DateTimeOffset(
                            datePicker.Date.Year,
                            datePicker.Date.Month,
                            datePicker.Date.Day,
                            timePicker.Time.Hours,
                            timePicker.Time.Minutes,
                            0,
                            DateTimeOffset.Now.Offset);

                    var broadcastFingerprint =
                        BuildBroadcastFingerprint(
                            authorTag,
                            isTutorial
                                ? $"[TUTORIAL] {subject}"
                                : subject,
                            body,
                            scheduled,
                            pending.Select(item => item.FileName));

                    if (isBroadcast &&
                        _recentBroadcastFingerprints.Contains(
                            broadcastFingerprint))
                    {
                        status.Text =
                            "Este aviso grupal ya se envió durante esta sesión. " +
                            "Cambia el asunto, texto o fecha para enviarlo otra vez.";
                        return;
                    }

                    try
                    {
                        dialog.IsPrimaryButtonEnabled =
                            false;

                        var progress =
                            new Progress<
                                NotionFileUploadProgress>(
                                report =>
                                {
                                    uploadProgressBar.Visibility =
                                        Visibility.Visible;
                                    uploadProgressText.Visibility =
                                        Visibility.Visible;
                                    uploadProgressBar.Value =
                                        report.Percentage;
                                    uploadProgressText.Text =
                                        $"{report.Percentage}% · " +
                                        $"{report.FileName} · " +
                                        $"{report.Completed}/{report.Total}";
                                    status.Text =
                                        $"Subiendo {report.FileName}...";
                                });

                        using var cts =
                            new CancellationTokenSource(
                                TimeSpan.FromMinutes(45));

                        var service =
                            new NotionFilePageService();

                        MessageViewItem? createdMessage =
                            null;

                        var completedRecipients = 0;

                        foreach (var recipientTag in recipientTags)
                        {
                            var tutorialToken =
                                isTutorial
                                    ? "[TUTORIAL] "
                                    : string.Empty;

                            var title =
                                $"{scheduled:yyyy-MM-dd HH:mm} " +
                                $"{recipientTag} de:{authorTag} " +
                                $"{tutorialToken}{subject}";

                            status.Text =
                                isBroadcast
                                    ? $"Enviando aviso {completedRecipients + 1} de {recipientTags.Count}..."
                                    : "Creando mensaje...";

                            var created =
                                await service
                                    .CreateRevisionMessageAsync(
                                        token,
                                        title,
                                        pending
                                            .Select(item =>
                                                item.Path)
                                            .ToList(),
                                        progress,
                                        cts.Token);

                            var recipientName =
                                MessagesPeople.TryGetValue(
                                    recipientTag,
                                    out var mappedRecipient)
                                    ? mappedRecipient
                                    : recipientTag;

                            var initialAttachments =
                                pending
                                    .Select(item =>
                                        new MessageThreadAttachment
                                        {
                                            FileName =
                                                item.FileName,
                                            BlockType =
                                                GetMessageAttachmentBlockType(
                                                    item.FileName)
                                        })
                                    .ToList();

                            var initialText =
                                string.IsNullOrWhiteSpace(body) &&
                                initialAttachments.Count > 0
                                    ? $"Adjuntó {initialAttachments.Count} archivo(s)."
                                    : body;

                            await _messageThreadService
                                .AppendEntryAsync(
                                    token,
                                    created.PageId,
                                    new MessageThreadEntry
                                    {
                                        Kind =
                                            MessageThreadKind.Message,
                                        AuthorTag = authorTag,
                                        AuthorName = authorName,
                                        RecipientTag = recipientTag,
                                        RecipientName = recipientName,
                                        CreatedAt =
                                            DateTimeOffset.Now,
                                        Text = initialText,
                                        Attachments =
                                            initialAttachments
                                    },
                                    cts.Token);

                            var row =
                                new SearchResultRow
                                {
                                    ExternalId =
                                        created.PageId,
                                    ExternalUrl =
                                        created.PageUrl,
                                    ExternalSourceName =
                                        "Revisiones",
                                    Source =
                                        SearchSource.Notion,
                                    Name = title,
                                    Target =
                                        created.PageUrl
                                };

                            createdMessage ??=
                                TryCreateMessageViewItem(
                                    row);

                            completedRecipients++;
                        }

                        if (isBroadcast)
                        {
                            _recentBroadcastFingerprints.Add(
                                broadcastFingerprint);
                        }

                        status.Text =
                            isBroadcast
                                ? $"Aviso enviado a {completedRecipients} usuario(s) ✅"
                                : "Mensaje creado ✅";

                        dialog.Hide();
                        RefreshMessagesView();

                        if (!isBroadcast &&
                            createdMessage != null)
                        {
                            await ShowMessageConversationAsync(
                                createdMessage,
                                focusReply: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        status.Text =
                            $"No se pudo crear → {ex.Message}";
                    }
                    finally
                    {
                        dialog.IsPrimaryButtonEnabled =
                            true;
                    }
                };

            await dialog.ShowAsync();
        }

        private async void MessageHistory_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            await ShowMessageConversationAsync(
                message,
                focusReply: false);
        }

        private async Task ShowMessageConversationAsync(
            MessageViewItem message,
            bool focusReply)
        {
            MarkMessageAsRead(message);
            if (string.IsNullOrWhiteSpace(
                    message.Row.ExternalId))
            {
                StatusText.Text =
                    "Estado: El mensaje no tiene identificador de Notion.";
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var historyPanel = new StackPanel
            {
                Spacing = 4,
                Padding = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var historyScroll = new ScrollViewer
            {
                MinHeight = 300,
                MaxHeight = 430,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(2, 4, 8, 4),
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Disabled,
                Content = historyPanel
            };

            var conversationState = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82,
                FontWeight =
                    Microsoft.UI.Text.FontWeights.SemiBold
            };

            var replyBox = new TextBox
            {
                Header = "Responder",
                PlaceholderText =
                    "Escribe una respuesta...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 54,
                MaxHeight = 84,
                IsEnabled = true
            };

            var pendingAttachments =
                new ObservableCollection<PendingMessageAttachment>();

            var attachmentsPanel =
                new StackPanel
                {
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Visibility = Visibility.Collapsed
                };

            var attachButton =
                new Button
                {
                    Content = "📎 Adjuntar",
                    HorizontalAlignment =
                        HorizontalAlignment.Left
                };

            async Task RefreshPendingAttachmentsAsync()
            {
                attachmentsPanel.Children.Clear();

                foreach (var attachment in pendingAttachments)
                {
                    var row =
                        new Grid
                        {
                            ColumnSpacing = 8
                        };

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width = new GridLength(
                                1,
                                GridUnitType.Star)
                        });

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width = GridLength.Auto
                        });

                    var nameText =
                        BuildPendingMessageAttachmentPreview(
                            attachment);

                    var removeButton =
                        new Button
                        {
                            Content = "Quitar",
                            Tag = attachment,
                            Padding = new Thickness(8, 3, 8, 3)
                        };

                    removeButton.Click +=
                        (_, __) =>
                        {
                            if (removeButton.Tag is
                                PendingMessageAttachment selected)
                            {
                                pendingAttachments.Remove(selected);
                                _ = RefreshPendingAttachmentsAsync();
                            }
                        };

                    Grid.SetColumn(nameText, 0);
                    row.Children.Add(nameText);

                    Grid.SetColumn(removeButton, 1);
                    row.Children.Add(removeButton);

                    attachmentsPanel.Children.Add(row);
                }

                attachmentsPanel.Visibility =
                    pendingAttachments.Count > 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                await Task.CompletedTask;
            }

            void AddPendingAttachment(
                string path)
            {
                var clean =
                    (path ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(clean) ||
                    !System.IO.File.Exists(clean) ||
                    pendingAttachments.Any(item =>
                        string.Equals(
                            item.Path,
                            clean,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                pendingAttachments.Add(
                    new PendingMessageAttachment
                    {
                        Path = clean,
                        FileName =
                            System.IO.Path.GetFileName(clean)
                    });

                _ = RefreshPendingAttachmentsAsync();
            }

            var replyUploadProgressBar =
                new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0,
                    Visibility = Visibility.Collapsed
                };

            var replyUploadProgressText =
                new TextBlock
                {
                    FontSize = 11,
                    Opacity = 0.72,
                    Visibility = Visibility.Collapsed
                };

            var status = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            };

            attachButton.Click +=
                async (_, __) =>
                {
                    try
                    {
                        var picker =
                            new FileOpenPicker
                            {
                                SuggestedStartLocation =
                                    PickerLocationId.Downloads
                            };

                        picker.FileTypeFilter.Add("*");

                        var hwnd =
                            WindowNative.GetWindowHandle(
                                App.MainWindowInstance);

                        InitializeWithWindow.Initialize(
                            picker,
                            hwnd);

                        var files =
                            await picker.PickMultipleFilesAsync();

                        foreach (var file in files)
                            AddPendingAttachment(file.Path);
                    }
                    catch (Exception ex)
                    {
                        status.Text =
                            $"No se pudieron seleccionar archivos → {ex.Message}";
                    }
                };

            var content = new Grid
            {
                Width = 500,
                MaxWidth = 500,
                MinHeight = 460,
                MaxHeight = 610,
                RowSpacing = 7
            };

            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = new GridLength(
                        1,
                        GridUnitType.Star)
                });
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            Grid.SetRow(historyScroll, 0);
            content.Children.Add(historyScroll);

            Grid.SetRow(conversationState, 1);
            content.Children.Add(conversationState);

            Grid.SetRow(replyBox, 2);
            content.Children.Add(replyBox);

            Grid.SetRow(attachButton, 3);
            content.Children.Add(attachButton);

            Grid.SetRow(attachmentsPanel, 4);
            content.Children.Add(attachmentsPanel);

            Grid.SetRow(replyUploadProgressBar, 5);
            content.Children.Add(replyUploadProgressBar);

            Grid.SetRow(replyUploadProgressText, 6);
            content.Children.Add(replyUploadProgressText);

            Grid.SetRow(status, 7);
            content.Children.Add(status);

            content.AllowDrop = true;

            content.DragOver +=
                (_, args) =>
                {
                    if (args.DataView.Contains(
                            StandardDataFormats.StorageItems))
                    {
                        args.AcceptedOperation =
                            DataPackageOperation.Copy;
                        args.DragUIOverride.Caption =
                            "Adjuntar a la conversación";
                        args.DragUIOverride.IsCaptionVisible = true;
                    }
                };

            content.Drop +=
                async (_, args) =>
                {
                    try
                    {
                        if (!args.DataView.Contains(
                                StandardDataFormats.StorageItems))
                        {
                            return;
                        }

                        var items =
                            await args.DataView.GetStorageItemsAsync();

                        foreach (var file in items.OfType<StorageFile>())
                            AddPendingAttachment(file.Path);
                    }
                    catch (Exception ex)
                    {
                        status.Text =
                            $"No se pudieron agregar los archivos → {ex.Message}";
                    }
                };

            replyBox.KeyDown +=
                async (_, args) =>
                {
                    if (args.Key !=
                        Windows.System.VirtualKey.V ||
                        !IsMessagesControlKeyDown())
                    {
                        return;
                    }

                    try
                    {
                        var clipboard =
                            Clipboard.GetContent();

                        if (!clipboard.Contains(
                                StandardDataFormats.Bitmap))
                        {
                            return;
                        }

                        var bitmapReference =
                            await clipboard.GetBitmapAsync();

                        using var input =
                            await bitmapReference.OpenReadAsync();

                        var tempFile =
                            await ApplicationData.Current.TemporaryFolder
                                .CreateFileAsync(
                                    $"captura-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                                    CreationCollisionOption.GenerateUniqueName);

                        using var output =
                            await tempFile.OpenAsync(
                                FileAccessMode.ReadWrite);

                        await RandomAccessStream.CopyAsync(
                            input,
                            output);

                        await output.FlushAsync();

                        AddPendingAttachment(tempFile.Path);
                        args.Handled = true;
                    }
                    catch (Exception ex)
                    {
                        status.Text =
                            $"No se pudo pegar la captura → {ex.Message}";
                    }
                };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Conversación · {message.Message}",
                Content = content,
                PrimaryButtonText = "Enviar",
                SecondaryButtonText = "Actualizar",
                CloseButtonText = "Cerrar",
                DefaultButton = ContentDialogButton.Primary
            };

            async Task ReloadThreadAsync()
            {
                historyPanel.Children.Clear();

                historyPanel.Children.Add(
                    BuildAdvancedMessageThreadCard(
                        new MessageThreadEntry
                        {
                            Kind = MessageThreadKind.Message,
                            AuthorTag = message.SenderTag,
                            AuthorName = string.IsNullOrWhiteSpace(
                                message.SenderName)
                                ? "Mensaje original"
                                : message.SenderName,
                            RecipientTag = message.RecipientTag,
                            RecipientName = message.RecipientName,
                            CreatedAt = message.ScheduledAt,
                            Text = message.Message
                        },
                        isOriginal: true,
                        pageId: message.Row.ExternalId,
                        token: token,
                        reloadThread: ReloadThreadAsync,
                        status: status));

                try
                {
                    using var cts =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(90));

                    var entries =
                        await _messageThreadService
                            .GetThreadAsync(
                                token,
                                message.Row.ExternalId,
                                cts.Token);

                    var receiptAdded =
                        await EnsureMessageReadReceiptAsync(
                            token,
                            message.Row.ExternalId,
                            entries);

                    if (receiptAdded)
                    {
                        entries =
                            await _messageThreadService
                                .GetThreadAsync(
                                    token,
                                    message.Row.ExternalId,
                                    cts.Token);
                    }

                    foreach (var entry in entries)
                    {
                        historyPanel.Children.Add(
                            BuildAdvancedMessageThreadCard(
                                entry,
                                isOriginal: false,
                                pageId: message.Row.ExternalId,
                                token: token,
                                reloadThread: ReloadThreadAsync,
                                status: status));
                    }

                    var latestMessage =
                        entries
                            .Where(entry =>
                                entry.Kind ==
                                MessageThreadKind.Message)
                            .OrderByDescending(entry =>
                                entry.CreatedAt)
                            .FirstOrDefault();

                    var waitingTag =
                        latestMessage != null &&
                        !string.IsNullOrWhiteSpace(
                            latestMessage.RecipientTag)
                            ? latestMessage.RecipientTag
                            : message.RecipientTag;

                    var waitingName =
                        MessagesPeople.TryGetValue(
                            waitingTag,
                            out var mappedWaitingName)
                            ? mappedWaitingName
                            : waitingTag;

                    conversationState.Text =
                        message.IsCompleted
                            ? "Conversación cerrada."
                            : string.IsNullOrWhiteSpace(waitingName)
                                ? "Conversación compartida."
                                : $"Esperando respuesta de {waitingName}.";

                    status.Text =
                        entries.Count == 0
                            ? "Sin respuestas todavía."
                            : $"{entries.Count} movimiento(s).";

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        historyScroll.ChangeView(
                            null,
                            historyScroll.ScrollableHeight,
                            null,
                            disableAnimation: true);

                        replyBox.IsEnabled = true;

                        if (focusReply)
                        {
                            replyBox.Focus(
                                FocusState.Programmatic);
                        }
                    });
                }
                catch (Exception ex)
                {
                    status.Text =
                        $"No se pudo cargar → {ex.Message}";
                }
            }

            dialog.PrimaryButtonClick +=
                async (_, args) =>
                {
                    args.Cancel = true;

                    var reply =
                        (replyBox.Text ?? string.Empty)
                            .Trim();

                    if (string.IsNullOrWhiteSpace(reply) &&
                        pendingAttachments.Count == 0)
                    {
                        status.Text =
                            "Escribe una respuesta o adjunta un archivo.";
                        return;
                    }

                    try
                    {
                        dialog.IsPrimaryButtonEnabled = false;

                        var authorTag =
                            GetCurrentMessagesUserTag();

                        var authorName =
                            MessagesPeople.TryGetValue(
                                authorTag,
                                out var mappedName)
                                ? mappedName
                                : authorTag;

                        using var cts =
                            new CancellationTokenSource(
                                TimeSpan.FromSeconds(60));

                        var recipientTag =
                            ResolveReplyRecipientTag(
                                message,
                                authorTag);

                        var recipientName =
                            MessagesPeople.TryGetValue(
                                recipientTag,
                                out var mappedRecipientName)
                                ? mappedRecipientName
                                : recipientTag;

                        var repliedAt =
                            DateTimeOffset.Now;

                        IReadOnlyList<MessageThreadAttachment>
                            uploadedAttachments =
                                Array.Empty<MessageThreadAttachment>();

                        if (pendingAttachments.Count > 0)
                        {
                            status.Text =
                                "Subiendo archivos a Notion...";

                            var uploadService =
                                new NotionFilePageService();

                            var uploaded =
                                await uploadService
                                    .AppendFilesToPageAsync(
                                        token,
                                        message.Row.ExternalId,
                                        pendingAttachments
                                            .Select(item => item.Path)
                                            .ToList(),
                                        progress:
                                            new Progress<
                                                NotionFileUploadProgress>(
                                                report =>
                                                {
                                                    replyUploadProgressBar.Visibility =
                                                        Visibility.Visible;
                                                    replyUploadProgressText.Visibility =
                                                        Visibility.Visible;
                                                    replyUploadProgressBar.Value =
                                                        report.Percentage;
                                                    replyUploadProgressText.Text =
                                                        $"{report.Percentage}% · {report.FileName}";
                                                }),
                                        cts.Token);

                            uploadedAttachments =
                                uploaded
                                    .Select(item =>
                                        new MessageThreadAttachment
                                        {
                                            FileName = item.FileName,
                                            FileUploadId = item.FileUploadId,
                                            BlockType = item.BlockType
                                        })
                                    .ToList();
                        }

                        await _messageThreadService
                            .AppendEntryAsync(
                                token,
                                message.Row.ExternalId,
                                new MessageThreadEntry
                                {
                                    Kind =
                                        MessageThreadKind.Message,
                                    AuthorTag = authorTag,
                                    AuthorName = authorName,
                                    RecipientTag = recipientTag,
                                    RecipientName = recipientName,
                                    CreatedAt = repliedAt,
                                    Text = reply,
                                    Attachments = uploadedAttachments
                                },
                                cts.Token);

                        await RouteReplyNotificationAsync(
                            token,
                            message,
                            authorTag,
                            recipientTag,
                            repliedAt,
                            string.IsNullOrWhiteSpace(reply)
                                ? $"Adjuntó {uploadedAttachments.Count} archivo(s)."
                                : reply,
                            cts.Token);

                        replyBox.Text = string.Empty;
                        pendingAttachments.Clear();
                        await RefreshPendingAttachmentsAsync();
                        await ReloadThreadAsync();
                        RefreshMessagesView();

                        status.Text =
                            string.IsNullOrWhiteSpace(recipientName)
                                ? "Respuesta enviada ✅"
                                : $"Enviada a {recipientName} ✅";
                    }
                    catch (Exception ex)
                    {
                        status.Text =
                            $"No se pudo enviar → {ex.Message}";
                    }
                    finally
                    {
                        dialog.IsPrimaryButtonEnabled = true;
                        replyBox.Focus(
                            FocusState.Programmatic);
                    }
                };

            dialog.SecondaryButtonClick +=
                async (_, args) =>
                {
                    args.Cancel = true;
                    await ReloadThreadAsync();
                };

            await ReloadThreadAsync();
            await dialog.ShowAsync();
        }

        private static bool IsMessagesControlKeyDown()
        {
            var left =
                Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.LeftControl);

            var right =
                Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.RightControl);

            const Windows.UI.Core.CoreVirtualKeyStates down =
                Windows.UI.Core.CoreVirtualKeyStates.Down;

            return (left & down) == down ||
                   (right & down) == down;
        }

        private Border BuildMessageThreadCard(
            MessageThreadEntry entry,
            bool isOriginal)
        {
            var isSystem =
                entry.Kind == MessageThreadKind.System;

            var recipientLabel =
                string.IsNullOrWhiteSpace(entry.RecipientTag) &&
                string.IsNullOrWhiteSpace(entry.RecipientName)
                    ? string.Empty
                    : $" → {DisplayMessageThreadRecipient(entry)}";

            if (isSystem)
            {
                return new Border
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(
                        Color.FromArgb(130, 100, 116, 139)),
                    Background = new SolidColorBrush(
                        Color.FromArgb(32, 100, 116, 139)),
                    Child = new TextBlock
                    {
                        Text =
                            $"Sistema · {entry.CreatedAt:dd/MM HH:mm} · {entry.Text}",
                        FontSize = 10.5,
                        Opacity = 0.80,
                        TextWrapping = TextWrapping.Wrap
                    }
                };
            }

            var colors =
                GetMessageParticipantColors(
                    entry.AuthorTag,
                    isOriginal);

            var header =
                $"{DisplayMessageThreadAuthor(entry)}" +
                $"{recipientLabel} · " +
                $"{entry.CreatedAt:dd/MM HH:mm}";

            var panel = new StackPanel
            {
                Spacing = 1
            };

            panel.Children.Add(
                new TextBlock
                {
                    Text = header,
                    FontSize = 10.5,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.84,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                });

            if (!string.IsNullOrWhiteSpace(entry.Text))
            {
                panel.Children.Add(
                    new TextBlock
                    {
                        Text = entry.Text,
                        FontSize = 11.5,
                        TextWrapping = TextWrapping.Wrap
                    });
            }

            if (entry.Attachments != null &&
                entry.Attachments.Count > 0)
            {
                foreach (var attachment in entry.Attachments)
                {
                    panel.Children.Add(
                        BuildMessageAttachmentView(
                            attachment));
                }
            }

            return new Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush =
                    new SolidColorBrush(colors.Border),
                Background =
                    new SolidColorBrush(colors.Background),
                Child = panel
            };
        }

        private static string
            GetMessageAttachmentBlockType(
                string fileName)
        {
            var extension =
                System.IO.Path
                    .GetExtension(
                        fileName ?? string.Empty)
                    .ToLowerInvariant();

            return extension switch
            {
                ".png" or
                ".jpg" or
                ".jpeg" or
                ".gif" or
                ".webp" or
                ".bmp" or
                ".svg" =>
                    "image",

                ".pdf" =>
                    "pdf",

                ".mp3" or
                ".wav" or
                ".m4a" or
                ".ogg" =>
                    "audio",

                ".mp4" or
                ".mov" or
                ".avi" or
                ".webm" or
                ".mkv" =>
                    "video",

                _ =>
                    "file"
            };
        }

        private FrameworkElement
            BuildMessageAttachmentView(
                MessageThreadAttachment attachment)
        {
            var container =
                new StackPanel
                {
                    Spacing = 5,
                    Margin = new Thickness(0, 3, 0, 0)
                };

            var canOpen =
                Uri.TryCreate(
                    attachment.Url,
                    UriKind.Absolute,
                    out var attachmentUri);

            FrameworkElement? previewAnchor = null;

            if (attachment.IsImage &&
                canOpen)
            {
                try
                {
                    var image =
                        new Image
                        {
                            Source =
                                new BitmapImage(
                                    attachmentUri),
                            MaxWidth = 260,
                            MaxHeight = 190,
                            Stretch =
                                Stretch.Uniform
                        };

                    var previewBorder =
                        new Border
                        {
                            MaxWidth = 260,
                            MaxHeight = 190,
                            HorizontalAlignment =
                                HorizontalAlignment.Left,
                            CornerRadius =
                                new CornerRadius(6),
                            BorderThickness =
                                new Thickness(1),
                            BorderBrush =
                                new SolidColorBrush(
                                    Color.FromArgb(
                                        90,
                                        255,
                                        255,
                                        255)),
                            Background =
                                new SolidColorBrush(
                                    Color.FromArgb(
                                        24,
                                        255,
                                        255,
                                        255)),
                            Child = image
                        };

                    previewBorder.PointerEntered +=
                        (_, __) =>
                        {
                            previewBorder.BorderBrush =
                                new SolidColorBrush(
                                    Color.FromArgb(
                                        220,
                                        96,
                                        165,
                                        250));
                        };

                    previewBorder.PointerExited +=
                        (_, __) =>
                        {
                            previewBorder.BorderBrush =
                                new SolidColorBrush(
                                    Color.FromArgb(
                                        90,
                                        255,
                                        255,
                                        255));
                        };

                    previewBorder.Tapped +=
                        (_, __) =>
                        {
                            ShowLargeMessageImagePreview(
                                previewBorder,
                                attachment);
                        };

                    ToolTipService.SetToolTip(
                        previewBorder,
                        "Clic para ver la imagen grande");

                    previewAnchor = previewBorder;
                    container.Children.Add(
                        previewBorder);
                }
                catch
                {
                    // Si la URL temporal expiró, se conserva la tarjeta.
                }
            }

            var name =
                new TextBlock
                {
                    Text = $"📎 {attachment.FileName}",
                    FontSize = 10.8,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.90,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                };

            container.Children.Add(name);

            var actions =
                new StackPanel
                {
                    Orientation =
                        Orientation.Horizontal,
                    Spacing = 6
                };

            if (attachment.IsImage)
            {
                var largeButton =
                    new Button
                    {
                        Content = "Ver grande",
                        IsEnabled = canOpen,
                        Padding =
                            new Thickness(9, 3, 9, 3),
                        FontSize = 10.5
                    };

                largeButton.Click +=
                    (_, __) =>
                    {
                        ShowLargeMessageImagePreview(
                            previewAnchor ??
                            largeButton,
                            attachment);
                    };

                ToolTipService.SetToolTip(
                    largeButton,
                    canOpen
                        ? "Mostrar la imagen en grande"
                        : "La URL temporal no está disponible. Pulsa Actualizar.");

                actions.Children.Add(
                    largeButton);
            }

            if (string.Equals(
                    attachment.BlockType,
                    "pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                var previewPdfButton =
                    new Button
                    {
                        Content = "Ver PDF",
                        IsEnabled = canOpen,
                        Padding =
                            new Thickness(9, 3, 9, 3),
                        FontSize = 10.5
                    };

                previewPdfButton.Click +=
                    async (_, __) =>
                    {
                        if (canOpen)
                            await Launcher.LaunchUriAsync(
                                attachmentUri);
                    };

                actions.Children.Add(
                    previewPdfButton);
            }

            var saveButton =
                new Button
                {
                    Content = "Guardar como",
                    IsEnabled = canOpen,
                    Padding =
                        new Thickness(9, 3, 9, 3),
                    FontSize = 10.5
                };

            saveButton.Click +=
                async (_, __) =>
                {
                    if (!canOpen)
                        return;

                    await SaveMessageAttachmentAsAsync(
                        attachment,
                        attachmentUri);
                };

            actions.Children.Add(
                saveButton);

            var openButton =
                new Button
                {
                    Content =
                        attachment.IsImage
                            ? "Abrir original"
                            : "Abrir archivo",
                    IsEnabled = canOpen,
                    Padding =
                        new Thickness(9, 3, 9, 3),
                    FontSize = 10.5,
                    Tag = attachment.Url
                };

            openButton.Click +=
                async (_, __) =>
                {
                    if (openButton.Tag is not string raw ||
                        !Uri.TryCreate(
                            raw,
                            UriKind.Absolute,
                            out var uri))
                    {
                        return;
                    }

                    await Launcher.LaunchUriAsync(uri);
                };

            ToolTipService.SetToolTip(
                openButton,
                canOpen
                    ? "Abrir el archivo con la aplicación predeterminada"
                    : "La URL temporal no está disponible. Pulsa Actualizar.");

            actions.Children.Add(
                openButton);

            container.Children.Add(
                actions);

            return container;
        }

        private async Task SaveMessageAttachmentAsAsync(
            MessageThreadAttachment attachment,
            Uri uri)
        {
            try
            {
                var picker =
                    new FileSavePicker
                    {
                        SuggestedStartLocation =
                            PickerLocationId.Downloads,
                        SuggestedFileName =
                            string.IsNullOrWhiteSpace(
                                attachment.FileName)
                                ? "archivo"
                                : System.IO.Path
                                    .GetFileNameWithoutExtension(
                                        attachment.FileName)
                    };

                var extension =
                    System.IO.Path.GetExtension(
                        attachment.FileName);

                if (string.IsNullOrWhiteSpace(extension))
                    extension = ".bin";

                picker.FileTypeChoices.Add(
                    "Archivo",
                    new List<string>
                    {
                        extension
                    });

                var hwnd =
                    WindowNative.GetWindowHandle(
                        App.MainWindowInstance);

                InitializeWithWindow.Initialize(
                    picker,
                    hwnd);

                var target =
                    await picker.PickSaveFileAsync();

                if (target == null)
                    return;

                using var http =
                    new HttpClient
                    {
                        Timeout =
                            TimeSpan.FromMinutes(10)
                    };

                var bytes =
                    await http.GetByteArrayAsync(uri);

                await FileIO.WriteBytesAsync(
                    target,
                    bytes);

                StatusText.Text =
                    $"Estado: Archivo guardado en {target.Path} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo guardar el archivo → {ex.Message}";
            }
        }

        private void ShowLargeMessageImagePreview(
            FrameworkElement anchor,
            MessageThreadAttachment attachment)
        {
            if (!Uri.TryCreate(
                    attachment.Url,
                    UriKind.Absolute,
                    out var uri))
            {
                return;
            }

            var image =
                new Image
                {
                    Source =
                        new BitmapImage(uri),
                    MaxWidth = 760,
                    MaxHeight = 560,
                    Stretch = Stretch.Uniform
                };

            var scroll =
                new ScrollViewer
                {
                    MaxWidth = 780,
                    MaxHeight = 580,
                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,
                    ZoomMode =
                        ZoomMode.Enabled,
                    MinZoomFactor = 0.5f,
                    MaxZoomFactor = 4.0f,
                    Content = image
                };

            var title =
                new TextBlock
                {
                    Text = attachment.FileName,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                };

            var openOriginalButton =
                new Button
                {
                    Content = "Abrir original",
                    HorizontalAlignment =
                        HorizontalAlignment.Left
                };

            openOriginalButton.Click +=
                async (_, __) =>
                {
                    await Launcher.LaunchUriAsync(uri);
                };

            var panel =
                new StackPanel
                {
                    Spacing = 8,
                    Padding =
                        new Thickness(10)
                };

            panel.Children.Add(title);
            panel.Children.Add(scroll);
            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        "Usa la rueda o el gesto de zoom para acercar.",
                    FontSize = 11,
                    Opacity = 0.68
                });
            panel.Children.Add(
                openOriginalButton);

            var flyout =
                new Flyout
                {
                    Placement =
                        FlyoutPlacementMode.Full,
                    Content = panel
                };

            flyout.ShowAt(anchor);
        }

        private static (
            Color Background,
            Color Border)
            GetMessageParticipantColors(
                string authorTag,
                bool isOriginal)
        {
            var clean =
                (authorTag ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

            var palette =
                new[]
                {
                    (
                        Color.FromArgb(56, 37, 99, 235),
                        Color.FromArgb(220, 96, 165, 250)
                    ),
                    (
                        Color.FromArgb(56, 5, 150, 105),
                        Color.FromArgb(220, 52, 211, 153)
                    ),
                    (
                        Color.FromArgb(56, 124, 58, 237),
                        Color.FromArgb(220, 167, 139, 250)
                    ),
                    (
                        Color.FromArgb(56, 194, 65, 12),
                        Color.FromArgb(220, 251, 146, 60)
                    ),
                    (
                        Color.FromArgb(56, 190, 24, 93),
                        Color.FromArgb(220, 244, 114, 182)
                    )
                };

            if (string.IsNullOrWhiteSpace(clean))
                return palette[0];

            var hash = 17;

            foreach (var character in clean)
                hash = unchecked(hash * 31 + character);

            var selected =
                palette[Math.Abs(hash % palette.Length)];

            if (!isOriginal)
                return selected;

            return (
                Color.FromArgb(
                    70,
                    selected.Item1.R,
                    selected.Item1.G,
                    selected.Item1.B),
                selected.Item2
            );
        }

        private static string DisplayMessageThreadAuthor(
            MessageThreadEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.AuthorName) &&
                !string.IsNullOrWhiteSpace(entry.AuthorTag))
            {
                return $"{entry.AuthorName} ({entry.AuthorTag})";
            }

            if (!string.IsNullOrWhiteSpace(entry.AuthorName))
                return entry.AuthorName;

            if (!string.IsNullOrWhiteSpace(entry.AuthorTag))
                return entry.AuthorTag;

            return "Usuario";
        }

        private static string DisplayMessageThreadRecipient(
            MessageThreadEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.RecipientName) &&
                !string.IsNullOrWhiteSpace(entry.RecipientTag))
            {
                return $"{entry.RecipientName} ({entry.RecipientTag})";
            }

            if (!string.IsNullOrWhiteSpace(entry.RecipientName))
                return entry.RecipientName;

            if (!string.IsNullOrWhiteSpace(entry.RecipientTag))
                return entry.RecipientTag;

            return "Sin destinatario";
        }

        private static string ResolveReplyRecipientTag(
            MessageViewItem message,
            string authorTag)
        {
            if (string.Equals(
                    authorTag,
                    message.RecipientTag,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(message.SenderTag))
            {
                return message.SenderTag;
            }

            if (string.Equals(
                    authorTag,
                    message.SenderTag,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(message.RecipientTag))
            {
                return message.RecipientTag;
            }

            if (!string.IsNullOrWhiteSpace(message.SenderTag) &&
                !string.Equals(
                    authorTag,
                    message.SenderTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                return message.SenderTag;
            }

            return message.RecipientTag;
        }

        private async Task RouteReplyNotificationAsync(
            string token,
            MessageViewItem message,
            string authorTag,
            string recipientTag,
            DateTimeOffset repliedAt,
            string reply,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(recipientTag))
                return;

            var senderToken =
                string.IsNullOrWhiteSpace(authorTag)
                    ? string.Empty
                    : $" de:{authorTag}";

            var newTitle =
                $"{repliedAt:yyyy-MM-dd HH:mm} " +
                $"{recipientTag}{senderToken} [RESPUESTA] {reply}";

            var service =
                new NotionPageActionsService();

            await service.RenamePageAsync(
                token,
                message.Row.ExternalId,
                NotionFilePageService.RevisionesDataSourceId,
                newTitle.Trim(),
                cancellationToken);

            await UpdateNotionRowTitleAsync(
                message.Row.ExternalId,
                "Revisiones",
                newTitle.Trim());
        }

        private async Task AppendMessageSystemHistoryAsync(
            MessageViewItem message,
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                    message.Row.ExternalId) ||
                string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
                return;

            var authorTag =
                GetCurrentMessagesUserTag();

            var authorName =
                MessagesPeople.TryGetValue(
                    authorTag,
                    out var mappedName)
                    ? mappedName
                    : authorTag;

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(45));

                await _messageThreadService.AppendEntryAsync(
                    token,
                    message.Row.ExternalId,
                    new MessageThreadEntry
                    {
                        Kind = MessageThreadKind.System,
                        AuthorTag = authorTag,
                        AuthorName = authorName,
                        RecipientTag = message.RecipientTag,
                        RecipientName = message.RecipientName,
                        CreatedAt = DateTimeOffset.Now,
                        Text = text
                    },
                    cts.Token);
            }
            catch
            {
                // El historial no debe impedir la acción principal.
            }
        }

        private void MessageCopyText_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            var package =
                new DataPackage();

            package.SetText(
                message.Message);

            Clipboard.SetContent(package);

            StatusText.Text =
                "Estado: Texto del mensaje copiado ✅";
        }

        private async void MessageOpen_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            // El contador rojo representa notificaciones no leídas.
            // Al abrir la notificación se marca como leída, aunque siga
            // pendiente de atención.
            MarkMessageAsRead(message);
            RefreshMessagesView();

            var target =
                !string.IsNullOrWhiteSpace(
                    message.Row.ExternalUrl)
                    ? message.Row.ExternalUrl
                    : message.Row.Target;

            if (!Uri.TryCreate(
                    target,
                    UriKind.Absolute,
                    out var uri))
            {
                StatusText.Text =
                    "Estado: El mensaje no tiene una URL válida.";
                return;
            }

            try
            {
                var desktop =
                    new Uri(
                        uri.AbsoluteUri.Replace(
                            "https://",
                            "notion://",
                            StringComparison.OrdinalIgnoreCase));

                var support =
                    await Launcher.QueryUriSupportAsync(
                        desktop,
                        LaunchQuerySupportType.Uri);

                var opened =
                    support ==
                    LaunchQuerySupportStatus.Available &&
                    await Launcher.LaunchUriAsync(desktop);

                if (!opened)
                    opened = await Launcher.LaunchUriAsync(uri);

                StatusText.Text = opened
                    ? "Estado: Mensaje abierto en Notion ✅"
                    : "Estado: No se pudo abrir el mensaje.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir → {ex.Message}";
            }
        }

        private async void MessageOpenOriginalActivity_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            if (!message.IsReviewAlert ||
                string.IsNullOrWhiteSpace(message.Row.ExternalId))
            {
                StatusText.Text =
                    "Estado: Esta notificación no está vinculada a una actividad.";
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            try
            {
                StatusText.Text =
                    "Estado: Buscando la actividad original...";

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(60));

                var source =
                    await _messageThreadService
                        .GetReviewAlertSourceAsync(
                            token,
                            message.Row.ExternalId,
                            cts.Token);

                if (source == null ||
                    string.IsNullOrWhiteSpace(source.PageUrl) ||
                    !Uri.TryCreate(
                        source.PageUrl,
                        UriKind.Absolute,
                        out var webUri))
                {
                    StatusText.Text =
                        "Estado: Esta alerta es anterior al vínculo automático o no contiene una URL válida.";
                    return;
                }

                MarkMessageAsRead(message);
                RefreshMessagesView();

                var desktopUri =
                    new Uri(
                        webUri.AbsoluteUri.Replace(
                            "https://",
                            "notion://",
                            StringComparison.OrdinalIgnoreCase));

                var support =
                    await Launcher.QueryUriSupportAsync(
                        desktopUri,
                        LaunchQuerySupportType.Uri);

                var opened =
                    support == LaunchQuerySupportStatus.Available &&
                    await Launcher.LaunchUriAsync(desktopUri);

                if (!opened)
                    opened = await Launcher.LaunchUriAsync(webUri);

                StatusText.Text = opened
                    ? $"Estado: Actividad original abierta ✅ {source.Title}"
                    : "Estado: No se pudo abrir la actividad original.";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: Notion tardó demasiado en localizar la actividad original.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir la actividad original → {ex.Message}";
            }
        }


        private async void MessageOpenTutorial_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message) ||
                !message.IsTutorial)
            {
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            try
            {
                StatusText.Text =
                    "Estado: Buscando el contenido del tutorial...";

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(90));

                var entries =
                    await _messageThreadService.GetThreadAsync(
                        token,
                        message.Row.ExternalId,
                        cts.Token);

                var attachment =
                    entries
                        .SelectMany(entry =>
                            entry.Attachments ??
                            Array.Empty<MessageThreadAttachment>())
                        .FirstOrDefault(item =>
                            !string.IsNullOrWhiteSpace(item.Url));

                Uri? targetUri = null;

                if (attachment != null)
                {
                    Uri.TryCreate(
                        attachment.Url,
                        UriKind.Absolute,
                        out targetUri);
                }

                if (targetUri == null)
                {
                    var urlMatch =
                        Regex.Match(
                            message.Message ?? string.Empty,
                            @"https?://[^\s<>""]+",
                            RegexOptions.IgnoreCase |
                            RegexOptions.CultureInvariant);

                    if (urlMatch.Success)
                    {
                        Uri.TryCreate(
                            urlMatch.Value.TrimEnd(
                                '.', ',', ';', ')', ']'),
                            UriKind.Absolute,
                            out targetUri);
                    }
                }

                if (targetUri == null)
                {
                    StatusText.Text =
                        "Estado: El tutorial no contiene un archivo o enlace disponible.";
                    return;
                }

                MarkMessageAsRead(message);
                RefreshMessagesView();

                var opened =
                    await Launcher.LaunchUriAsync(
                        targetUri);

                StatusText.Text =
                    opened
                        ? "Estado: Tutorial abierto ✅"
                        : "Estado: No se pudo abrir el tutorial.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir el tutorial → {ex.Message}";
            }
        }

        private async void MessageSnooze15_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (TryGetMessageFromSender(
                    sender,
                    out var message) &&
                message.IsTutorial)
            {
                await SnoozeTutorialAsync(
                    message,
                    DateTimeOffset.Now.AddMinutes(15),
                    "15 minutos");
            }
        }

        private async void MessageSnooze60_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (TryGetMessageFromSender(
                    sender,
                    out var message) &&
                message.IsTutorial)
            {
                await SnoozeTutorialAsync(
                    message,
                    DateTimeOffset.Now.AddHours(1),
                    "1 hora");
            }
        }

        private async void MessageBusy_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message) ||
                !message.IsTutorial)
            {
                return;
            }

            var datePicker =
                new DatePicker
                {
                    Header = "Recordarme el día",
                    Date = DateTimeOffset.Now.AddHours(2)
                };

            var timePicker =
                new TimePicker
                {
                    Header = "A la hora",
                    Time = DateTimeOffset.Now
                        .AddHours(2)
                        .TimeOfDay
                };

            var panel =
                new StackPanel
                {
                    Spacing = 10
                };

            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        "Selecciona cuándo debe volver a aparecer este tutorial.",
                    TextWrapping =
                        TextWrapping.Wrap
                });

            panel.Children.Add(datePicker);
            panel.Children.Add(timePicker);

            var dialog =
                new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Estoy ocupado",
                    Content = panel,
                    PrimaryButtonText = "Posponer",
                    CloseButtonText = "Cancelar",
                    DefaultButton =
                        ContentDialogButton.Primary
                };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            var selectedDate =
                datePicker.Date.Date;

            var target =
                new DateTimeOffset(
                    selectedDate.Year,
                    selectedDate.Month,
                    selectedDate.Day,
                    timePicker.Time.Hours,
                    timePicker.Time.Minutes,
                    0,
                    DateTimeOffset.Now.Offset);

            if (target <= DateTimeOffset.Now)
            {
                StatusText.Text =
                    "Estado: Selecciona una fecha futura.";
                return;
            }

            await SnoozeTutorialAsync(
                message,
                target,
                $"hasta {target:dd/MM/yyyy HH:mm}");
        }

        private async Task SnoozeTutorialAsync(
            MessageViewItem message,
            DateTimeOffset target,
            string label)
        {
            if (message == null ||
                !message.IsTutorial)
            {
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            try
            {
                ShowLoadingState(
                    "Estado: Posponiendo tutorial...",
                    message.Message);

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(60));

                var senderToken =
                    string.IsNullOrWhiteSpace(
                        message.SenderTag)
                        ? string.Empty
                        : $" de:{message.SenderTag}";

                var newTitle =
                    $"{target:yyyy-MM-dd HH:mm} " +
                    $"{message.RecipientTag}{senderToken} " +
                    $"[TUTORIAL] {message.Message}";

                var actions =
                    new NotionPageActionsService();

                await actions.RenamePageAsync(
                    token,
                    message.Row.ExternalId,
                    NotionFilePageService
                        .RevisionesDataSourceId,
                    newTitle,
                    cts.Token);

                await UpdateNotionRowTitleAsync(
                    message.Row.ExternalId,
                    "Revisiones",
                    newTitle);

                var currentUser =
                    GetCurrentMessagesUserTag();

                var currentName =
                    MessagesPeople.TryGetValue(
                        currentUser,
                        out var mappedName)
                        ? mappedName
                        : currentUser;

                await _messageThreadService.AppendEntryAsync(
                    token,
                    message.Row.ExternalId,
                    new MessageThreadEntry
                    {
                        Kind = MessageThreadKind.System,
                        AuthorTag = currentUser,
                        AuthorName = currentName,
                        RecipientTag = message.RecipientTag,
                        RecipientName = message.RecipientName,
                        CreatedAt = DateTimeOffset.Now,
                        Text =
                            $"Tutorial pospuesto {label} por " +
                            $"{MessageViewItem.DisplayPerson(currentName, currentUser)}."
                    },
                    cts.Token);

                MarkMessageAsRead(message);
                RefreshMessagesView();

                StatusText.Text =
                    $"Estado: Tutorial pospuesto {label} ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo posponer el tutorial → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async void MessageReassign_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            if (message.IsReviewAlert)
            {
                StatusText.Text =
                    "Estado: Las alertas de revisión no se pueden reasignar.";
                return;
            }

            var combo = BuildMessagesPersonCombo(
                message.RecipientTag);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Reasignar mensaje",
                Content = combo,
                PrimaryButtonText = "Reasignar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() !=
                    ContentDialogResult.Primary ||
                combo.SelectedItem is not ComboBoxItem selected)
            {
                return;
            }

            var recipientTag =
                selected.Tag?.ToString() ?? string.Empty;

            await ApplyMessageChangeAsync(
                message,
                recipientTag,
                message.ScheduledAt,
                message.IsCompleted);
        }

        private async void MessageReschedule_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            if (message.IsReviewAlert)
            {
                StatusText.Text =
                    "Estado: Las alertas de revisión no se pueden reprogramar.";
                return;
            }

            var datePicker = new DatePicker
            {
                Date = message.ScheduledAt,
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            var timePicker = new TimePicker
            {
                Time = message.ScheduledAt.TimeOfDay,
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            var panel = new StackPanel
            {
                Spacing = 10
            };

            panel.Children.Add(
                new TextBlock
                {
                    Text = "Nueva fecha y hora:",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            panel.Children.Add(datePicker);
            panel.Children.Add(timePicker);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Reprogramar mensaje",
                Content = panel,
                PrimaryButtonText = "Guardar fecha",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            var selectedDate =
                datePicker.Date.Date;

            var newDate =
                new DateTimeOffset(
                    selectedDate.Year,
                    selectedDate.Month,
                    selectedDate.Day,
                    timePicker.Time.Hours,
                    timePicker.Time.Minutes,
                    0,
                    DateTimeOffset.Now.Offset);

            await ApplyMessageChangeAsync(
                message,
                message.RecipientTag,
                newDate,
                message.IsCompleted);
        }

        private async void MessageComplete_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            if (message.IsReviewAlert)
            {
                await ApplyLinkedReviewAlertStateAsync(
                    message,
                    !message.IsCompleted);

                return;
            }

            await ApplyMessageChangeAsync(
                message,
                message.RecipientTag,
                message.ScheduledAt,
                !message.IsCompleted);
        }

        private async Task ApplyLinkedReviewAlertStateAsync(
            MessageViewItem selectedMessage,
            bool completed)
        {
            var currentUser =
                GetCurrentMessagesUserTag();

            var isAuthorized =
                string.Equals(
                    currentUser,
                    "jjohn",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    currentUser,
                    "ggena",
                    StringComparison.OrdinalIgnoreCase);

            if (!isAuthorized)
            {
                StatusText.Text =
                    "Estado: Solo John o Genaro pueden atender esta alerta.";
                return;
            }

            var token =
                GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var alertKey =
                selectedMessage.ReviewAlertKey;

            if (string.IsNullOrWhiteSpace(alertKey))
            {
                StatusText.Text =
                    "Estado: No se pudo identificar la alerta vinculada.";
                return;
            }

            var linkedAlerts =
                App.LocalIndex
                    .GetAll()
                    .Where(row =>
                        row.Source == SearchSource.Notion &&
                        string.Equals(
                            row.ExternalSourceName,
                            "Revisiones",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(TryCreateMessageViewItem)
                    .Where(item =>
                        item != null &&
                        item.IsReviewAlert &&
                        string.Equals(
                            item.ReviewAlertKey,
                            alertKey,
                            StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(
                             item.RecipientTag,
                             "jjohn",
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             item.RecipientTag,
                             "ggena",
                             StringComparison.OrdinalIgnoreCase)))
                    .Cast<MessageViewItem>()
                    .GroupBy(item =>
                        item.Row.ExternalId,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        group.First())
                    .ToList();

            if (linkedAlerts.Count == 0)
                linkedAlerts.Add(selectedMessage);

            var reviewerName =
                MessagesPeople.TryGetValue(
                    currentUser,
                    out var mappedReviewer)
                    ? mappedReviewer
                    : currentUser;

            var actionText =
                completed
                    ? $"Alerta atendida por {reviewerName} ({currentUser}) · " +
                      $"{DateTimeOffset.Now:dd/MM/yyyy HH:mm}."
                    : $"Alerta reabierta por {reviewerName} ({currentUser}) · " +
                      $"{DateTimeOffset.Now:dd/MM/yyyy HH:mm}.";

            try
            {
                ShowLoadingState(
                    completed
                        ? "Estado: Marcando alerta como atendida..."
                        : "Estado: Reabriendo alerta...",
                    selectedMessage.Message);

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                var pageActions =
                    new NotionPageActionsService();

                foreach (var alert in linkedAlerts)
                {
                    if (string.IsNullOrWhiteSpace(
                            alert.Row.ExternalId))
                    {
                        continue;
                    }

                    var senderTag =
                        string.IsNullOrWhiteSpace(
                            alert.SenderTag)
                            ? currentUser
                            : alert.SenderTag;

                    var senderToken =
                        string.IsNullOrWhiteSpace(senderTag)
                            ? string.Empty
                            : $" de:{senderTag}";

                    var statusToken =
                        completed
                            ? " [TERMINADO]"
                            : string.Empty;

                    var newTitle =
                        $"{alert.ScheduledAt:yyyy-MM-dd HH:mm} " +
                        $"{alert.RecipientTag}{senderToken}{statusToken} " +
                        $"{alert.Message}";

                    await pageActions.RenamePageAsync(
                        token,
                        alert.Row.ExternalId,
                        NotionFilePageService
                            .RevisionesDataSourceId,
                        newTitle.Trim(),
                        cts.Token);

                    await UpdateNotionRowTitleAsync(
                        alert.Row.ExternalId,
                        "Revisiones",
                        newTitle.Trim());

                    await _messageThreadService
                        .AppendEntryAsync(
                            token,
                            alert.Row.ExternalId,
                            new MessageThreadEntry
                            {
                                Kind =
                                    MessageThreadKind.System,
                                AuthorTag = currentUser,
                                AuthorName = reviewerName,
                                RecipientTag =
                                    alert.RecipientTag,
                                RecipientName =
                                    alert.RecipientName,
                                CreatedAt =
                                    DateTimeOffset.Now,
                                Text = actionText
                            },
                            cts.Token);

                    MarkMessageAsRead(alert);
                }

                RefreshMessagesView();

                StatusText.Text =
                    completed
                        ? $"Estado: Alerta atendida por {reviewerName} ✅"
                        : $"Estado: Alerta reabierta por {reviewerName} ✅";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: Notion tardó demasiado en responder.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo actualizar la alerta → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async void MessageDelete_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(
                    message.Row.ExternalId))
            {
                StatusText.Text =
                    "Estado: El mensaje no tiene identificador de Notion.";
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title =
                    message.IsReviewAlert
                        ? "Eliminar notificación"
                        : "Eliminar recordatorio",
                Content =
                    message.IsReviewAlert
                        ? $"Se eliminará únicamente esta notificación. " +
                          $"La actividad original del calendario no cambiará.\n\n" +
                          $"{message.Message}"
                        : $"¿Deseas mover este recordatorio a la papelera de Notion?\n\n" +
                          $"{message.Message}",
                PrimaryButtonText =
                    message.IsReviewAlert
                        ? "Eliminar notificación"
                        : "Eliminar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close
            };

            if (await confirm.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            try
            {
                ShowLoadingState(
                    message.IsReviewAlert
                        ? "Estado: Eliminando notificación..."
                        : "Estado: Eliminando recordatorio...",
                    message.Message);

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(45));

                var service =
                    new NotionPageActionsService();

                await service.MovePageToTrashAsync(
                    token,
                    message.Row.ExternalId,
                    cts.Token);

                await RemoveNotionRowsFromIndexAsync(
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        message.Row.ExternalId
                    });

                RefreshMessagesView();

                StatusText.Text =
                    message.IsReviewAlert
                        ? "Estado: Notificación eliminada. La actividad original permanece intacta ✅"
                        : "Estado: Recordatorio enviado a la papelera ✅";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: Notion tardó demasiado en responder.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    message.IsReviewAlert
                        ? $"Estado: No se pudo eliminar la notificación → {ex.Message}"
                        : $"Estado: No se pudo eliminar el recordatorio → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private async Task ApplyMessageChangeAsync(
            MessageViewItem message,
            string recipientTag,
            DateTimeOffset scheduledAt,
            bool completed)
        {
            if (string.IsNullOrWhiteSpace(
                    message.Row.ExternalId))
            {
                StatusText.Text =
                    "Estado: El mensaje no tiene identificador de Notion.";
                return;
            }

            if (string.IsNullOrWhiteSpace(recipientTag))
            {
                StatusText.Text =
                    "Estado: Selecciona un destinatario.";
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var senderTag =
                string.IsNullOrWhiteSpace(message.SenderTag)
                    ? GetCurrentMessagesUserTag()
                    : message.SenderTag;

            var senderToken =
                string.IsNullOrWhiteSpace(senderTag)
                    ? string.Empty
                    : $" de:{senderTag}";

            var statusToken =
                completed
                    ? " [TERMINADO]"
                    : string.Empty;

            var newTitle =
                $"{scheduledAt:yyyy-MM-dd HH:mm} " +
                $"{recipientTag}{senderToken}{statusToken} " +
                $"{message.Message}";

            newTitle = newTitle.Trim();

            try
            {
                ShowLoadingState(
                    "Estado: Actualizando mensaje...",
                    message.Message);

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(45));

                var service =
                    new NotionPageActionsService();

                await service.RenamePageAsync(
                    token,
                    message.Row.ExternalId,
                    NotionFilePageService.RevisionesDataSourceId,
                    newTitle,
                    cts.Token);

                await UpdateNotionRowTitleAsync(
                    message.Row.ExternalId,
                    "Revisiones",
                    newTitle);

                var historyEvents =
                    new List<string>();

                if (!string.Equals(
                        message.RecipientTag,
                        recipientTag,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var oldRecipient =
                        MessagesPeople.TryGetValue(
                            message.RecipientTag,
                            out var oldRecipientName)
                            ? oldRecipientName
                            : message.RecipientTag;

                    var newRecipient =
                        MessagesPeople.TryGetValue(
                            recipientTag,
                            out var newRecipientName)
                            ? newRecipientName
                            : recipientTag;

                    historyEvents.Add(
                        $"Mensaje reasignado de {oldRecipient} a {newRecipient}.");
                }

                if (message.ScheduledAt != scheduledAt)
                {
                    historyEvents.Add(
                        $"Mensaje reprogramado de " +
                        $"{message.ScheduledAt:dd/MM/yyyy HH:mm} a " +
                        $"{scheduledAt:dd/MM/yyyy HH:mm}.");
                }

                if (message.IsCompleted != completed)
                {
                    historyEvents.Add(
                        completed
                            ? "Estado cambiado a Terminado."
                            : "El mensaje fue reabierto.");
                }

                foreach (var historyEvent in historyEvents)
                {
                    await AppendMessageSystemHistoryAsync(
                        message,
                        historyEvent);
                }

                RefreshMessagesView();

                StatusText.Text =
                    "Estado: Mensaje actualizado ✅";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: Notion tardó demasiado en responder.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo actualizar el mensaje → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private static string BuildBroadcastFingerprint(
            string authorTag,
            string subject,
            string body,
            DateTimeOffset scheduled,
            IEnumerable<string> fileNames)
        {
            var normalizedFiles =
                string.Join(
                    "|",
                    (fileNames ??
                     Array.Empty<string>())
                    .Select(name =>
                        (name ?? string.Empty)
                            .Trim()
                            .ToLowerInvariant())
                    .OrderBy(name => name));

            var raw =
                string.Join(
                    "\n",
                    (authorTag ?? string.Empty)
                        .Trim()
                        .ToLowerInvariant(),
                    (subject ?? string.Empty)
                        .Trim()
                        .ToLowerInvariant(),
                    (body ?? string.Empty)
                        .Trim()
                        .ToLowerInvariant(),
                    scheduled.ToString(
                        "yyyy-MM-dd HH:mm",
                        CultureInfo.InvariantCulture),
                    normalizedFiles);

            using var algorithm =
                System.Security.Cryptography.SHA256.Create();

            return Convert.ToHexString(
                algorithm.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(raw)));
        }

        private static ComboBox BuildMessagesPersonCombo(
            string selectedTag,
            bool includeAll = false)
        {
            var combo = new ComboBox
            {
                Header = "Nuevo destinatario",
                MinWidth = 360,
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            if (includeAll)
            {
                combo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = "Todos los usuarios",
                        Tag = MessagesAllRecipientsTag
                    });
            }

            foreach (var person in MessagesPeople)
            {
                combo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = person.Value,
                        Tag = person.Key
                    });
            }

            combo.SelectedItem = combo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                    string.Equals(
                        item.Tag?.ToString(),
                        selectedTag,
                        StringComparison.OrdinalIgnoreCase));

            combo.SelectedIndex =
                combo.SelectedIndex >= 0
                    ? combo.SelectedIndex
                    : 0;

            return combo;
        }

        private static bool TryGetMessageFromSender(
            object sender,
            out MessageViewItem message)
        {
            message = null!;

            if (sender is not FrameworkElement element ||
                element.Tag is not MessageViewItem item)
            {
                return false;
            }

            var currentUser =
                GetCurrentMessagesUserTag();

            if (string.IsNullOrWhiteSpace(currentUser))
                return false;

            var belongsToCurrentUser =
                string.Equals(
                    item.SenderTag,
                    currentUser,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    item.RecipientTag,
                    currentUser,
                    StringComparison.OrdinalIgnoreCase);

            if (!belongsToCurrentUser)
                return false;

            message = item;
            return true;
        }
    }
}