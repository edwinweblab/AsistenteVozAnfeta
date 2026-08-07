using Anfeta.UI.Models.Notion;
using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
using Anfeta.UI.Services.Speech;
using Anfeta.UI.Services.Search;
using Microsoft.Extensions.DependencyInjection;
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
using Windows.Media.Core;
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

        private const string MessagesAllRecipientsName =
            "Todos los usuarios";

        private static bool IsGroupMessageRecipient(
            string? recipientTag)
        {
            return string.Equals(
                recipientTag,
                MessagesAllRecipientsTag,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool MessageBelongsToCurrentUser(
            string senderTag,
            string recipientTag,
            string currentUserTag)
        {
            return !string.IsNullOrWhiteSpace(currentUserTag) &&
                   (AreSameMessagesPersonTag(
                        senderTag,
                        currentUserTag) ||
                    AreSameMessagesPersonTag(
                        recipientTag,
                        currentUserTag) ||
                    IsGroupMessageRecipient(recipientTag));
        }

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
                // Ajustes guarda el tag de Isaías como iisaia.
                // Los títulos antiguos con iisai se normalizan automáticamente.
                ["iisaia"] = "Isaias",
                ["eedua"] = "Sotelo",
                ["aacal"] = "Acalli",
                ["aandr"] = "Andrade",
                ["eemma"] = "Emmanuel",
                ["bbria"] = "Brian",
                ["ggena"] = "Genaro",
                ["nneft"] = "Neftali"
            };

        private static string NormalizeMessagesPersonTag(
            string? value)
        {
            var clean =
                (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            return clean switch
            {
                // Compatibilidad con títulos y versiones anteriores.
                "iisai" or "iisiaia" or "isaias" => "iisaia",
                _ => clean
            };
        }

        private static bool AreSameMessagesPersonTag(
            string? first,
            string? second)
        {
            return string.Equals(
                NormalizeMessagesPersonTag(first),
                NormalizeMessagesPersonTag(second),
                StringComparison.OrdinalIgnoreCase);
        }

        private readonly ObservableCollection<MessageViewItem>
            _messageItems = new();

        private bool _messagesViewActive;
        private bool _messagesInitialized;
        private string _messagesFilter = "received";
        private string _messagesTypeFilter = "all";
        private string _messagesGroupMode = "none";
        private string _messagesSearchQuery = string.Empty;
        private string _messagesSelectedPageId = string.Empty;
        private MessageViewItem? _messagesSelectedItem;
        private CancellationTokenSource? _messagesConversationCts;
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

        private static event Action<string, string>?
            ReminderQuickActionRequested;

        private static string
            _pendingConversationPageId = string.Empty;

        private static string
            _pendingReminderQuickActionPageId = string.Empty;

        private static string
            _pendingReminderQuickAction = string.Empty;

        private bool
            _messagesNavigationBridgeAttached;

        private sealed class MessageGroup :
            ObservableCollection<MessageViewItem>
        {
            public string Name { get; }

            public string Header =>
                $"{Name} · {Count} " +
                (Count == 1
                    ? "conversación"
                    : "conversaciones");

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
            public bool IsTemporaryRecording { get; init; }
            public TimeSpan? Duration { get; init; }
        }

        private sealed class MessageAudioComposerSession : IAsyncDisposable
        {
            private const int MaxRecordingSeconds = 600;

            private readonly ObservableCollection<
                PendingMessageAttachment> _pending;

            private readonly Action _refreshAttachments;
            private readonly TextBlock _status;
            private readonly MessageAudioRecorderService _recorder = new();
            private readonly DispatcherTimer _durationTimer;
            private readonly Button _recordButton;
            private readonly Button _stopButton;
            private readonly Button _deleteButton;
            private readonly TextBlock _durationText;
            private readonly TextBlock _stateText;
            private readonly MediaPlayerElement _previewPlayer;
            private PendingMessageAttachment? _currentAttachment;
            private bool _stopping;

            public Border View { get; }

            public bool IsRecording => _recorder.IsRecording;

            public MessageAudioComposerSession(
                ObservableCollection<PendingMessageAttachment> pending,
                Action refreshAttachments,
                TextBlock status)
            {
                _pending = pending;
                _refreshAttachments = refreshAttachments;
                _status = status;

                _recordButton = new Button
                {
                    Content = "🎙 Grabar audio",
                    Padding = new Thickness(11, 6, 11, 6)
                };

                _stopButton = new Button
                {
                    Content = "⏹ Detener",
                    IsEnabled = false,
                    Padding = new Thickness(11, 6, 11, 6)
                };

                _deleteButton = new Button
                {
                    Content = "Borrar audio",
                    Visibility = Visibility.Collapsed,
                    Padding = new Thickness(11, 6, 11, 6)
                };

                _durationText = new TextBlock
                {
                    Text = "00:00",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                };

                _stateText = new TextBlock
                {
                    Text = "Puedes adjuntar un audio al mismo mensaje.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72,
                    FontSize = 11
                };

                _previewPlayer = new MediaPlayerElement
                {
                    AreTransportControlsEnabled = true,
                    AutoPlay = false,
                    MinWidth = 300,
                    Height = 54,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Visibility = Visibility.Collapsed
                };

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };

                actions.Children.Add(_recordButton);
                actions.Children.Add(_stopButton);
                actions.Children.Add(_deleteButton);
                actions.Children.Add(_durationText);

                var body = new StackPanel
                {
                    Spacing = 7
                };

                body.Children.Add(actions);
                body.Children.Add(_stateText);
                body.Children.Add(_previewPlayer);

                View = new Border
                {
                    Padding = new Thickness(10),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(
                        Color.FromArgb(90, 255, 255, 255)),
                    Background = new SolidColorBrush(
                        Color.FromArgb(20, 255, 255, 255)),
                    Child = body
                };

                _durationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };

                _durationTimer.Tick += DurationTimer_Tick;
                _recordButton.Click += RecordButton_Click;
                _stopButton.Click += StopButton_Click;
                _deleteButton.Click += DeleteButton_Click;
            }

            private async void RecordButton_Click(
                object sender,
                RoutedEventArgs e)
            {
                if (_recorder.IsRecording)
                    return;

                try
                {
                    if (_currentAttachment != null)
                    {
                        await RemoveAttachmentAsync(
                            _currentAttachment);
                    }

                    _recordButton.IsEnabled = false;
                    _stopButton.IsEnabled = false;
                    _deleteButton.Visibility = Visibility.Collapsed;
                    _previewPlayer.Source = null;
                    _previewPlayer.Visibility = Visibility.Collapsed;
                    _durationText.Text = "00:00";
                    _stateText.Text = "Solicitando micrófono...";
                    _status.Text = "Preparando grabación de audio...";

                    using var cts =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(30));

                    await _recorder.StartAsync(cts.Token);

                    _recordButton.Content = "🔴 Grabando";
                    _stopButton.IsEnabled = true;
                    _stateText.Text =
                        "Grabando audio. Máximo 10 minutos.";
                    _status.Text = "Grabando audio...";
                    _durationTimer.Start();
                }
                catch (Exception ex)
                {
                    _recordButton.IsEnabled = true;
                    _stopButton.IsEnabled = false;
                    _recordButton.Content = "🎙 Grabar audio";
                    _stateText.Text =
                        "No se pudo iniciar la grabación.";
                    _status.Text =
                        $"No se pudo usar el micrófono → {ex.Message}";
                }
            }

            private async void StopButton_Click(
                object sender,
                RoutedEventArgs e)
            {
                await StopRecordingAsync();
            }

            private async void DeleteButton_Click(
                object sender,
                RoutedEventArgs e)
            {
                if (_currentAttachment != null)
                {
                    await RemoveAttachmentAsync(
                        _currentAttachment);
                }
            }

            private async void DurationTimer_Tick(
                object? sender,
                object e)
            {
                if (!_recorder.IsRecording)
                    return;

                var elapsed = _recorder.Elapsed;
                _durationText.Text = FormatMessageAudioDuration(elapsed);

                if (elapsed.TotalSeconds >= MaxRecordingSeconds)
                    await StopRecordingAsync();
            }

            private async Task StopRecordingAsync()
            {
                if (!_recorder.IsRecording || _stopping)
                    return;

                _stopping = true;
                _durationTimer.Stop();
                _stopButton.IsEnabled = false;
                _stateText.Text = "Preparando audio...";

                try
                {
                    var result = await _recorder.StopAsync();

                    if (result == null ||
                        string.IsNullOrWhiteSpace(result.Path))
                    {
                        throw new InvalidOperationException(
                            "No se generó el archivo de audio.");
                    }

                    var attachment = new PendingMessageAttachment
                    {
                        Path = result.Path,
                        FileName = result.FileName,
                        IsTemporaryRecording = true,
                        Duration = result.Duration
                    };

                    _currentAttachment = attachment;
                    _pending.Add(attachment);
                    _refreshAttachments();

                    var file =
                        await StorageFile.GetFileFromPathAsync(
                            result.Path);

                    _previewPlayer.Source =
                        MediaSource.CreateFromStorageFile(file);
                    _previewPlayer.Visibility = Visibility.Visible;

                    _durationText.Text =
                        FormatMessageAudioDuration(result.Duration);
                    _stateText.Text =
                        "Audio listo. Escúchalo antes de enviar.";
                    _status.Text = "Audio listo para enviar ✅";
                    _deleteButton.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    _stateText.Text =
                        "No se pudo terminar la grabación.";
                    _status.Text =
                        $"No se pudo guardar el audio → {ex.Message}";
                }
                finally
                {
                    _recordButton.Content = "🎙 Volver a grabar";
                    _recordButton.IsEnabled = true;
                    _stopping = false;
                }
            }

            public async Task RemoveAttachmentAsync(
                PendingMessageAttachment attachment)
            {
                if (attachment == null)
                    return;

                _pending.Remove(attachment);
                await DeletePendingAttachmentFileAsync(attachment);

                if (_currentAttachment != null &&
                    string.Equals(
                        _currentAttachment.Path,
                        attachment.Path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _currentAttachment = null;
                    _previewPlayer.Source = null;
                    _previewPlayer.Visibility = Visibility.Collapsed;
                    _deleteButton.Visibility = Visibility.Collapsed;
                    _durationText.Text = "00:00";
                    _recordButton.Content = "🎙 Grabar audio";
                    _stateText.Text =
                        "Puedes adjuntar un audio al mismo mensaje.";
                    _status.Text = "Audio eliminado.";
                }

                _refreshAttachments();
            }

            public async ValueTask DisposeAsync()
            {
                _durationTimer.Stop();
                _previewPlayer.Source = null;
                await _recorder.DisposeAsync();
            }
        }

        private static string FormatMessageAudioDuration(
            TimeSpan duration)
        {
            var safe = duration < TimeSpan.Zero
                ? TimeSpan.Zero
                : duration;

            return safe.TotalHours >= 1
                ? safe.ToString(@"hh\:mm\:ss")
                : safe.ToString(@"mm\:ss");
        }

        private static async Task DeletePendingAttachmentFileAsync(
            PendingMessageAttachment attachment)
        {
            if (attachment == null ||
                !attachment.IsTemporaryRecording ||
                string.IsNullOrWhiteSpace(attachment.Path))
            {
                return;
            }

            try
            {
                var file =
                    await StorageFile.GetFileFromPathAsync(
                        attachment.Path);

                await file.DeleteAsync(
                    StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private static async Task DeleteTemporaryMessageAttachmentsAsync(
            IEnumerable<PendingMessageAttachment> attachments)
        {
            foreach (var attachment in
                     (attachments ??
                      Array.Empty<PendingMessageAttachment>())
                     .Where(item => item.IsTemporaryRecording)
                     .ToList())
            {
                await DeletePendingAttachmentFileAsync(attachment);
            }
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

            public string ConversationTitle
            {
                get
                {
                    var structured =
                        ExtractMessageDomainProject(Message);

                    if (!string.Equals(
                            structured,
                            "Sin dominio / Sin tipo de proyecto",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return structured;
                    }

                    var firstLine =
                        (Message ?? string.Empty)
                            .Split(
                                new[] { "\r\n", "\n", "\r" },
                                StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault()
                            ?.Trim();

                    return string.IsNullOrWhiteSpace(firstLine)
                        ? "Conversación"
                        : firstLine;
                }
            }

            public string ConversationPreview
            {
                get
                {
                    var value =
                        Regex.Replace(
                            Message ?? string.Empty,
                            @"\s+",
                            " ")
                        .Trim();

                    return string.IsNullOrWhiteSpace(value)
                        ? "Sin mensajes todavía"
                        : value;
                }
            }

            public string ConversationContactLabel
            {
                get
                {
                    var current =
                        GetCurrentMessagesUserTag();

                    return string.Equals(
                            RecipientTag,
                            current,
                            StringComparison.OrdinalIgnoreCase)
                        ? $"De {DisplayPerson(SenderName, SenderTag)}"
                        : $"Para {DisplayPerson(RecipientName, RecipientTag)}";
                }
            }

            public string ConversationTimeLabel =>
                ScheduledAt.Date == DateTimeOffset.Now.Date
                    ? ScheduledAt.ToString("HH:mm", CultureInfo.InvariantCulture)
                    : ScheduledAt.ToString("dd/MM", CultureInfo.InvariantCulture);

            public string AvatarText
            {
                get
                {
                    var project =
                        ExtractMessageProject(Message);

                    return project.ToLowerInvariant() switch
                    {
                        "sseo" => "SEO",
                        "aads" => "ADS",
                        "wwebs" => "WEB",
                        "aapli" => "APP",
                        "pprog" => "PRO",
                        _ => IsReviewAlert
                            ? "REV"
                            : MessageTypeLabel
                                .Substring(0, Math.Min(3, MessageTypeLabel.Length))
                                .ToUpperInvariant()
                    };
                }
            }

            public Brush AvatarBrush
            {
                get
                {
                    var project =
                        ExtractMessageProject(Message)
                            .ToLowerInvariant();

                    var color = project switch
                    {
                        "sseo" => Color.FromArgb(255, 14, 116, 144),
                        "aads" => Color.FromArgb(255, 194, 65, 12),
                        "wwebs" => Color.FromArgb(255, 37, 99, 235),
                        "aapli" => Color.FromArgb(255, 124, 58, 237),
                        "pprog" => Color.FromArgb(255, 5, 150, 105),
                        _ => IsReviewAlert
                            ? Color.FromArgb(255, 190, 24, 93)
                            : Color.FromArgb(255, 71, 85, 105)
                    };

                    return new SolidColorBrush(color);
                }
            }

            public bool IsReviewAlert =>
                Message.StartsWith(
                    "Actividad lista para revisión",
                    StringComparison.OrdinalIgnoreCase) ||
                Message.StartsWith(
                    "Correcciones solicitadas",
                    StringComparison.OrdinalIgnoreCase) ||
                Message.StartsWith(
                    "Revisión aprobada",
                    StringComparison.OrdinalIgnoreCase);

            public bool IsProjectMessage =>
                IsReviewAlert ||
                LooksLikeProjectMessage(Message);

            public string MessageTypeKey =>
                IsReviewAlert
                    ? "reviews"
                    : IsProjectMessage
                        ? "projects"
                        : "reminders";

            public string MessageTypeLabel =>
                IsReviewAlert
                    ? "Revisión"
                    : IsProjectMessage
                        ? "Proyecto"
                        : "Recordatorio";

            public Visibility OriginalActivityButtonVisibility =>
                IsProjectMessage
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            // Alias conservado por compatibilidad con plantillas anteriores.
            public Visibility ReviewOriginalButtonVisibility =>
                OriginalActivityButtonVisibility;

            public Visibility NormalReminderOpenButtonVisibility =>
                IsProjectMessage
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            public Visibility ProjectNotificationOpenButtonVisibility =>
                IsProjectMessage
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            private static bool LooksLikeProjectMessage(
                string? value)
            {
                var text =
                    (value ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(text))
                    return false;

                var hasProjectToken =
                    Regex.IsMatch(
                        text,
                        @"(?<![a-z0-9_])(?:sseo|aapli|aads|wwebs|pprog|sprtuzrevision|prtuzrevision|rtuzrevision|zrevision)(?![a-z0-9_])",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);

                return hasProjectToken &&
                       (text.Contains('/') ||
                        text.Contains("revisión", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("revision", StringComparison.OrdinalIgnoreCase));
            }

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

            public Visibility MarkReadButtonVisibility =>
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
                if (IsGroupMessageRecipient(tag))
                    return MessagesAllRecipientsName;

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

        public static void RequestReminderQuickAction(
            string pageId,
            string action)
        {
            var cleanPageId =
                (pageId ?? string.Empty).Trim();

            var cleanAction =
                (action ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(cleanPageId) ||
                string.IsNullOrWhiteSpace(cleanAction))
            {
                return;
            }

            _pendingReminderQuickActionPageId =
                cleanPageId;

            _pendingReminderQuickAction =
                cleanAction;

            ReminderQuickActionRequested?.Invoke(
                cleanPageId,
                cleanAction);
        }

        private void AttachMessagesNavigationBridge()
        {
            if (_messagesNavigationBridgeAttached)
                return;

            ConversationOpenRequested +=
                OnConversationOpenRequested;

            ReminderQuickActionRequested +=
                OnReminderQuickActionRequested;

            _messagesNavigationBridgeAttached = true;

            if (!string.IsNullOrWhiteSpace(
                    _pendingConversationPageId))
            {
                OnConversationOpenRequested(
                    _pendingConversationPageId);
            }

            if (!string.IsNullOrWhiteSpace(
                    _pendingReminderQuickActionPageId) &&
                !string.IsNullOrWhiteSpace(
                    _pendingReminderQuickAction))
            {
                OnReminderQuickActionRequested(
                    _pendingReminderQuickActionPageId,
                    _pendingReminderQuickAction);
            }
        }

        private void DetachMessagesNavigationBridge()
        {
            if (!_messagesNavigationBridgeAttached)
                return;

            ConversationOpenRequested -=
                OnConversationOpenRequested;

            ReminderQuickActionRequested -=
                OnReminderQuickActionRequested;

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

        private void OnReminderQuickActionRequested(
            string pageId,
            string action)
        {
            if (!IsLoaded ||
                Visibility != Visibility.Visible)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(
                async () =>
                {
                    await ExecuteReminderQuickActionByPageIdAsync(
                        pageId,
                        action);
                });
        }

        private async Task ExecuteReminderQuickActionByPageIdAsync(
            string pageId,
            string action)
        {
            var cleanPageId =
                (pageId ?? string.Empty).Trim();

            var cleanAction =
                (action ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(cleanPageId) ||
                string.IsNullOrWhiteSpace(cleanAction))
            {
                return;
            }

            _pendingReminderQuickActionPageId =
                string.Empty;

            _pendingReminderQuickAction =
                string.Empty;

            if (string.Equals(
                    cleanAction,
                    "mark-read",
                    StringComparison.OrdinalIgnoreCase))
            {
                InitializeMessagesView();
            }
            else
            {
                await ShowMessagesViewAsync();
            }

            var row =
                App.LocalIndex
                    .GetAll()
                    .FirstOrDefault(item =>
                        item.Source == SearchSource.Notion &&
                        string.Equals(
                            item.ExternalSourceName,
                            "Revisiones",
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            item.ExternalId,
                            cleanPageId,
                            StringComparison.OrdinalIgnoreCase));

            var message =
                row == null
                    ? null
                    : TryCreateMessageViewItem(row);

            if (message == null)
            {
                StatusText.Text =
                    "Estado: El recordatorio todavía no está disponible en el índice. Pulsa Actualizar Notion y vuelve a intentar.";
                return;
            }

            var currentUser =
                GetCurrentMessagesUserTag();

            if (!MessageBelongsToCurrentUser(
                    message.SenderTag,
                    message.RecipientTag,
                    currentUser))
            {
                StatusText.Text =
                    "Estado: Este recordatorio no pertenece al usuario configurado.";
                return;
            }

            switch (cleanAction)
            {
                case "conversation":
                    await SelectMessagesConversationAsync(
                        message,
                        focusReply: true);
                    break;

                case "history":
                    await SelectMessagesConversationAsync(
                        message,
                        focusReply: false);
                    break;

                case "open":
                    await OpenMessageInNotionAsync(
                        message);
                    break;

                case "open-original":
                    await OpenOriginalActivityAsync(
                        message);
                    break;

                case "copy":
                    CopyMessageText(message);
                    break;

                case "mark-read":
                    MarkMessageAsRead(message);
                    RefreshMessagesView();
                    StatusText.Text =
                        "Estado: Mensaje marcado como visto ✅";
                    break;

                case "reassign":
                    await ReassignMessageAsync(message);
                    break;

                case "reschedule":
                    await RescheduleMessageAsync(message);
                    break;

                case "complete":
                    await CompleteMessageAsync(message);
                    break;

                case "delete":
                    await DeleteMessageAsync(message);
                    break;

                default:
                    StatusText.Text =
                        "Estado: La acción rápida solicitada no está disponible.";
                    break;
            }
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

            await SelectMessagesConversationAsync(
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
                "domainproject";

            MessagesList.ItemsSource = _messageItems;
            MessagesFilterCombo.SelectedIndex = 0;

            if (MessagesTypeCombo != null)
                MessagesTypeCombo.SelectedIndex = 0;

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
                    MessagesGroupCombo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item =>
                            string.Equals(
                                item.Tag?.ToString(),
                                "domainproject",
                                StringComparison.OrdinalIgnoreCase)) ??
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
            ClearSearchForModuleSwitch();

            if (_remindersCalendarViewActive)
                CloseRemindersCalendarView();

            if (_calendarViewActive)
                CloseCalendarView();

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
            ClearSearchForModuleSwitch();
            _messagesRefreshTimer?.Stop();

            try
            {
                _messagesConversationCts?.Cancel();
            }
            catch
            {
            }

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

        private void MessagesTypeCombo_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (MessagesTypeCombo.SelectedItem is ComboBoxItem item)
            {
                _messagesTypeFilter =
                    item.Tag?.ToString() ?? "all";
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

        private async void MessagesList_ItemClick(
            object sender,
            ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MessageViewItem message)
                return;

            await SelectMessagesConversationAsync(
                message,
                focusReply: false);
        }

        private async Task SelectMessagesConversationAsync(
            MessageViewItem message,
            bool focusReply)
        {
            if (message == null ||
                string.IsNullOrWhiteSpace(message.Row.ExternalId))
            {
                StatusText.Text =
                    "Estado: La conversación no tiene identificador de Notion.";
                return;
            }

            _messagesSelectedPageId =
                message.Row.ExternalId;

            _messagesSelectedItem = message;

            MessagesConversationPanel.DataContext = message;
            MessagesConversationPanel.Visibility = Visibility.Visible;
            MessagesConversationEmptyState.Visibility = Visibility.Collapsed;
            MessagesChatReplyBox.IsEnabled = true;
            MessagesChatSendButton.IsEnabled = true;
            MessagesChatStatusText.Text = "Cargando conversación...";

            MarkMessageAsRead(message);
            RefreshMessagesView();

            var selected =
                _messagesSelectedItem ?? message;

            await LoadSelectedMessagesConversationAsync(
                selected,
                focusReply);
        }

        private void ClearMessagesConversationSelection()
        {
            try
            {
                _messagesConversationCts?.Cancel();
                _messagesConversationCts?.Dispose();
            }
            catch
            {
            }

            _messagesConversationCts = null;
            _messagesSelectedPageId = string.Empty;
            _messagesSelectedItem = null;

            if (MessagesConversationPanel != null)
            {
                MessagesConversationPanel.DataContext = null;
                MessagesConversationPanel.Visibility = Visibility.Collapsed;
            }

            if (MessagesConversationEmptyState != null)
            {
                MessagesConversationEmptyState.Visibility = Visibility.Visible;
            }

            MessagesChatHistoryPanel?.Children.Clear();

            if (MessagesChatReplyBox != null)
                MessagesChatReplyBox.Text = string.Empty;

            if (MessagesChatStatusText != null)
            {
                MessagesChatStatusText.Text =
                    "Selecciona una conversación para responder.";
            }
        }

        private async Task LoadSelectedMessagesConversationAsync(
            MessageViewItem message,
            bool focusReply)
        {
            if (message == null ||
                string.IsNullOrWhiteSpace(message.Row.ExternalId))
            {
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                MessagesChatStatusText.Text =
                    "Configura primero el token de Notion.";
                return;
            }

            try
            {
                _messagesConversationCts?.Cancel();
                _messagesConversationCts?.Dispose();
            }
            catch
            {
            }

            _messagesConversationCts =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(90));

            var cancellationToken =
                _messagesConversationCts.Token;

            var requestedPageId =
                message.Row.ExternalId;

            MessagesChatHistoryPanel.Children.Clear();
            MessagesChatHistoryPanel.Children.Add(
                new TextBlock
                {
                    Text = "Cargando historial...",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 0),
                    Opacity = 0.68
                });

            MessagesChatStatusText.Text =
                "Consultando historial en Notion...";

            try
            {
                var entries =
                    await _messageThreadService.GetThreadAsync(
                        token,
                        requestedPageId,
                        cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                var receiptAdded =
                    await EnsureMessageReadReceiptAsync(
                        token,
                        requestedPageId,
                        entries);

                if (receiptAdded)
                {
                    entries =
                        await _messageThreadService.GetThreadAsync(
                            token,
                            requestedPageId,
                            cancellationToken);
                }

                if (!string.Equals(
                        _messagesSelectedPageId,
                        requestedPageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                MessagesChatHistoryPanel.Children.Clear();

                // Los mensajes normales antiguos pueden no tener todavía
                // una entrada inicial codificada. Se conserva su tarjeta base.
                if (!message.IsReviewAlert)
                {
                    MessagesChatHistoryPanel.Children.Add(
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
                            pageId: requestedPageId,
                            token: token,
                            reloadThread: () =>
                                LoadSelectedMessagesConversationAsync(
                                    _messagesSelectedItem ?? message,
                                    focusReply: false),
                            status: MessagesChatStatusText));
                }

                foreach (var entry in entries)
                {
                    MessagesChatHistoryPanel.Children.Add(
                        BuildAdvancedMessageThreadCard(
                            entry,
                            isOriginal: false,
                            pageId: requestedPageId,
                            token: token,
                            reloadThread: () =>
                                LoadSelectedMessagesConversationAsync(
                                    _messagesSelectedItem ?? message,
                                    focusReply: false),
                            status: MessagesChatStatusText));
                }

                if (MessagesChatHistoryPanel.Children.Count == 0)
                {
                    MessagesChatHistoryPanel.Children.Add(
                        new TextBlock
                        {
                            Text = "Esta conversación todavía no tiene movimientos.",
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 24, 0, 0),
                            Opacity = 0.66
                        });
                }

                var latestMessage =
                    entries
                        .Where(entry =>
                            entry.Kind == MessageThreadKind.Message)
                        .OrderByDescending(entry => entry.CreatedAt)
                        .FirstOrDefault();

                var waitingTag =
                    latestMessage != null &&
                    !string.IsNullOrWhiteSpace(
                        latestMessage.RecipientTag)
                        ? latestMessage.RecipientTag
                        : message.RecipientTag;

                var waitingName =
                    IsGroupMessageRecipient(waitingTag)
                        ? MessagesAllRecipientsName
                        : MessagesPeople.TryGetValue(
                            waitingTag,
                            out var mappedWaitingName)
                            ? mappedWaitingName
                            : waitingTag;

                MessagesChatConversationStateText.Text =
                    message.IsCompleted
                        ? "Conversación cerrada"
                        : IsGroupMessageRecipient(waitingTag)
                            ? "Compartida con todo el equipo"
                            : string.IsNullOrWhiteSpace(waitingName)
                                ? "Conversación activa"
                                : $"Esperando respuesta de {waitingName}";

                MessagesChatStatusText.Text =
                    entries.Count == 0
                        ? "Sin respuestas todavía."
                        : $"{entries.Count} movimiento(s) en el historial.";

                DispatcherQueue.TryEnqueue(() =>
                {
                    MessagesChatHistoryScroll.ChangeView(
                        null,
                        MessagesChatHistoryScroll.ScrollableHeight,
                        null,
                        disableAnimation: true);

                    if (focusReply)
                    {
                        MessagesChatReplyBox.Focus(
                            FocusState.Programmatic);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                if (string.Equals(
                        _messagesSelectedPageId,
                        requestedPageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessagesChatStatusText.Text =
                        "La carga del historial fue cancelada.";
                }
            }
            catch (Exception ex)
            {
                if (!string.Equals(
                        _messagesSelectedPageId,
                        requestedPageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                MessagesChatHistoryPanel.Children.Clear();
                MessagesChatHistoryPanel.Children.Add(
                    new TextBlock
                    {
                        Text = $"No se pudo cargar el historial.\n{ex.Message}",
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(20),
                        Opacity = 0.78
                    });

                MessagesChatStatusText.Text =
                    $"No se pudo cargar → {ex.Message}";
            }
        }

        private async void MessagesChatRefresh_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_messagesSelectedItem == null)
                return;

            await LoadSelectedMessagesConversationAsync(
                _messagesSelectedItem,
                focusReply: false);
        }

        private async void MessagesChatOpenFull_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_messagesSelectedItem == null)
            {
                MessagesChatStatusText.Text =
                    "Selecciona primero una conversación.";
                return;
            }

            await ShowMessageConversationAsync(
                _messagesSelectedItem,
                focusReply: true);
        }

        private async void MessagesChatSend_Click(
            object sender,
            RoutedEventArgs e)
        {
            await SendSelectedMessagesChatReplyAsync();
        }

        private async void MessagesChatReplyBox_KeyDown(
            object sender,
            KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter ||
                !IsMessagesControlKeyDown())
            {
                return;
            }

            e.Handled = true;
            await SendSelectedMessagesChatReplyAsync();
        }

        private async Task SendSelectedMessagesChatReplyAsync()
        {
            var message = _messagesSelectedItem;

            if (message == null)
            {
                MessagesChatStatusText.Text =
                    "Selecciona primero una conversación.";
                return;
            }

            var reply =
                (MessagesChatReplyBox.Text ?? string.Empty)
                    .Trim();

            if (string.IsNullOrWhiteSpace(reply))
            {
                MessagesChatStatusText.Text =
                    "Escribe una respuesta antes de enviar.";
                return;
            }

            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                MessagesChatStatusText.Text =
                    "Configura primero el token de Notion.";
                return;
            }

            MessagesChatSendButton.IsEnabled = false;
            MessagesChatReplyBox.IsEnabled = false;
            MessagesChatStatusText.Text = "Enviando respuesta...";

            try
            {
                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(3));

                var authorTag =
                    GetCurrentMessagesUserTag();

                var authorName =
                    MessagesPeople.TryGetValue(
                        authorTag,
                        out var mappedName)
                        ? mappedName
                        : authorTag;

                var recipientTag =
                    ResolveReplyRecipientTag(
                        message,
                        authorTag);

                var recipientName =
                    IsGroupMessageRecipient(recipientTag)
                        ? MessagesAllRecipientsName
                        : MessagesPeople.TryGetValue(
                            recipientTag,
                            out var mappedRecipientName)
                            ? mappedRecipientName
                            : recipientTag;

                var repliedAt = DateTimeOffset.Now;

                await _messageThreadService.AppendEntryAsync(
                    token,
                    message.Row.ExternalId,
                    new MessageThreadEntry
                    {
                        Kind = MessageThreadKind.Message,
                        AuthorTag = authorTag,
                        AuthorName = authorName,
                        RecipientTag = recipientTag,
                        RecipientName = recipientName,
                        CreatedAt = repliedAt,
                        Text = reply
                    },
                    cts.Token);

                await RouteReplyNotificationAsync(
                    token,
                    message,
                    authorTag,
                    recipientTag,
                    repliedAt,
                    reply,
                    cts.Token);

                MessagesChatReplyBox.Text = string.Empty;
                RefreshMessagesView();

                var refreshed =
                    _messagesSelectedItem ?? message;

                await LoadSelectedMessagesConversationAsync(
                    refreshed,
                    focusReply: true);

                MessagesChatStatusText.Text =
                    string.IsNullOrWhiteSpace(recipientName)
                        ? "Respuesta enviada ✅"
                        : $"Respuesta enviada a {recipientName} ✅";
            }
            catch (OperationCanceledException)
            {
                MessagesChatStatusText.Text =
                    "Notion tardó demasiado en enviar la respuesta.";
            }
            catch (Exception ex)
            {
                MessagesChatStatusText.Text =
                    $"No se pudo enviar → {ex.Message}";
            }
            finally
            {
                MessagesChatSendButton.IsEnabled = true;
                MessagesChatReplyBox.IsEnabled = true;
            }
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
                        (AreSameMessagesPersonTag(
                             item.RecipientTag,
                             currentUserTag) ||
                         IsGroupMessageRecipient(
                             item.RecipientTag))),

                "sent" =>
                    parsed.Where(item =>
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        AreSameMessagesPersonTag(
                            item.SenderTag,
                            currentUserTag)),

                "conversations" =>
                    parsed.Where(item =>
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        MessageBelongsToCurrentUser(
                            item.SenderTag,
                            item.RecipientTag,
                            currentUserTag)),

                "overdue" =>
                    parsed.Where(item =>
                        item.IsOverdue &&
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        (AreSameMessagesPersonTag(
                             item.RecipientTag,
                             currentUserTag) ||
                         IsGroupMessageRecipient(
                             item.RecipientTag))),

                "completed" =>
                    parsed.Where(item =>
                        item.IsCompleted &&
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        MessageBelongsToCurrentUser(
                            item.SenderTag,
                            item.RecipientTag,
                            currentUserTag)),

                _ =>
                    parsed.Where(item =>
                        !string.IsNullOrWhiteSpace(currentUserTag) &&
                        MessageBelongsToCurrentUser(
                            item.SenderTag,
                            item.RecipientTag,
                            currentUserTag))
            };

            parsed = _messagesTypeFilter switch
            {
                "reminders" =>
                    parsed.Where(item =>
                        !item.IsProjectMessage),

                "projects" =>
                    parsed.Where(item =>
                        item.IsProjectMessage &&
                        !item.IsReviewAlert),

                "reviews" =>
                    parsed.Where(item =>
                        item.IsReviewAlert),

                _ => parsed
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

            var typeLabel =
                MessagesTypeCombo?.SelectedItem is ComboBoxItem typeItem
                    ? typeItem.Content?.ToString() ?? "Todos los tipos"
                    : "Todos los tipos";

            MessagesSummaryText.Text =
                string.IsNullOrWhiteSpace(currentUserTag)
                    ? $"{typeLabel} · Selecciona un usuario en Configuración para ver sus mensajes."
                    : $"{summaryLabel} · {typeLabel} · Usuario: {currentUserName}";

            MessagesEmptyState.Visibility =
                _messageItems.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            MessagesList.Visibility =
                _messageItems.Count == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(
                    _messagesSelectedPageId))
            {
                var refreshedSelection =
                    finalItems.FirstOrDefault(item =>
                        string.Equals(
                            item.Row.ExternalId,
                            _messagesSelectedPageId,
                            StringComparison.OrdinalIgnoreCase));

                if (refreshedSelection != null)
                {
                    _messagesSelectedItem = refreshedSelection;
                    MessagesConversationPanel.DataContext =
                        refreshedSelection;
                    MessagesConversationPanel.Visibility =
                        Visibility.Visible;
                    MessagesConversationEmptyState.Visibility =
                        Visibility.Collapsed;
                    MessagesList.SelectedItem = refreshedSelection;
                }
                else
                {
                    ClearMessagesConversationSelection();
                }
            }

            ModeText.Text =
                $"Modo: Mensajes ({GetMessagesFilterLabel()})";

            CountText.Text =
                $"{_messageItems.Count} mensajes";

            // Mantiene sincronizadas las dos representaciones de recordatorios:
            // la vista exclusiva y las tarjetas especiales del calendario normal.
            RefreshReminderCalendarViewsFromIndex();
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
                    AreSameMessagesPersonTag(
                        item.RecipientTag,
                        GetCurrentMessagesUserTag())
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
                @"(?<![\p{L}\p{Nd}_])(?<project>sseo|aapli|aads|wwebs|pprog)(?![\p{L}\p{Nd}_])",
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
                (!AreSameMessagesPersonTag(
                     recipientTag,
                     currentUser) &&
                 !IsGroupMessageRecipient(
                     recipientTag)))
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

            var acknowledgedAt =
                message.ScheduledAt >
                    DateTimeOffset.Now
                    ? message.ScheduledAt
                    : DateTimeOffset.Now;

            _messagesReadState[
                message.Row.ExternalId] =
                acknowledgedAt;

            SaveMessagesReadState();

            try
            {
                App.AppHost.Services
                    .GetService<IndexedFileReminderService>()
                    ?.DismissPage(message.Row.ExternalId);
            }
            catch
            {
                // El estado leído ya quedó guardado. El servicio normal
                // volverá a validar el recordatorio en el siguiente escaneo.
            }

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
            var values =
                ApplicationData.Current.LocalSettings.Values;

            var raw =
                values[MessagesCurrentUserKey] as string ??
                string.Empty;

            var normalized =
                NormalizeMessagesPersonTag(raw);

            // Migra aliases anteriores sin obligar a seleccionar de nuevo
            // el usuario en Ajustes.
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !string.Equals(
                    raw.Trim(),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                values[MessagesCurrentUserKey] = normalized;
            }

            return normalized;
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
                NormalizeMessagesPersonTag(
                    tokens.FirstOrDefault());

            string recipientName;

            if (IsGroupMessageRecipient(recipientTag))
            {
                recipientName =
                    MessagesAllRecipientsName;

                remainder = remainder
                    .Substring(tokens[0].Length)
                    .Trim();
            }
            else if (!MessagesPeople.TryGetValue(
                         recipientTag,
                         out recipientName))
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
                    NormalizeMessagesPersonTag(
                        senderMatch.Groups["tag"].Value);

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

        private static DateTimeOffset GetSuggestedMessageSchedule()
        {
            var now = DateTimeOffset.Now;

            // El editor propone siempre el día, la hora y el minuto actuales.
            // Se eliminan los segundos para que el TimePicker represente el
            // valor de forma exacta sin adelantarlo ni redondearlo.
            return new DateTimeOffset(
                now.Year,
                now.Month,
                now.Day,
                now.Hour,
                now.Minute,
                0,
                now.Offset);
        }

        private sealed class NewMessageComposerContext
        {
            public string RecipientTag { get; init; } = string.Empty;
            public string RecipientName { get; init; } = string.Empty;
            public string Domain { get; init; } = string.Empty;
            public string ProjectType { get; init; } = string.Empty;
            public string ActivityTitle { get; init; } = string.Empty;
            public string ActivityUrl { get; init; } = string.Empty;
            public DateTimeOffset SuggestedAt { get; init; } =
                GetSuggestedMessageSchedule();
        }

        private async void MessagesNew_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ShowNewMessageDialogAsync(
                context: null);
        }

        private Task ShowCalendarMessageComposerAsync(
            NotionCalendarActivity activity,
            string? recipientPerson = null)
        {
            if (activity == null)
                return Task.CompletedTask;

            var person =
                NormalizeCalendarPerson(
                    recipientPerson ?? string.Empty);

            if (string.IsNullOrWhiteSpace(person) ||
                string.Equals(
                    person,
                    "Sin asignar",
                    StringComparison.OrdinalIgnoreCase))
            {
                person = SplitPersons(activity.Person)
                    .Select(NormalizeCalendarPerson)
                    .FirstOrDefault(candidate =>
                        !string.IsNullOrWhiteSpace(
                            GetCalendarMessageRecipientTag(candidate)))
                    ?? string.Empty;
            }

            var recipientTag =
                GetCalendarMessageRecipientTag(person);

            var searchable = string.Join(
                " ",
                new[]
                {
                    activity.Title,
                    activity.Project,
                    activity.Status,
                    activity.UpdateText,
                    activity.Description
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));

            var domain = ExtractMessageDomain(searchable);

            if (string.Equals(
                    domain,
                    "Sin dominio",
                    StringComparison.OrdinalIgnoreCase))
            {
                domain = string.Empty;
            }

            var project =
                DetectCalendarMessageProjectType(searchable);

            return ShowNewMessageDialogAsync(
                new NewMessageComposerContext
                {
                    RecipientTag = recipientTag,
                    RecipientName = person,
                    Domain = domain,
                    ProjectType = project,
                    ActivityTitle =
                        (activity.Title ?? string.Empty).Trim(),
                    ActivityUrl =
                        (activity.PageUrl ?? string.Empty).Trim(),
                    SuggestedAt =
                        GetSuggestedMessageSchedule()
                });
        }

        private static string DetectCalendarMessageProjectType(
            string searchable)
        {
            var project =
                ExtractMessageProject(searchable);

            if (!string.Equals(
                    project,
                    "Sin tipo de proyecto",
                    StringComparison.OrdinalIgnoreCase))
            {
                return project;
            }

            var normalized =
                NormalizeCalendarSearchText(searchable);

            if (normalized.Contains(
                    "aplicacion",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "app",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "aapli";
            }

            if (normalized.Contains(
                    "sitio web",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "pagina web",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "web",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "wwebs";
            }

            if (normalized.Contains(
                    "seo",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "sseo";
            }

            if (normalized.Contains(
                    "ads",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "anuncios",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "aads";
            }

            if (normalized.Contains(
                    "programa",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "pprog";
            }

            return string.Empty;
        }

        private static string BuildStructuredCalendarMessageSubject(
            string domain,
            string project,
            string subject)
        {
            return string.Join(
                " / ",
                new[]
                {
                    (domain ?? string.Empty).Trim(),
                    (project ?? string.Empty).Trim().ToLowerInvariant(),
                    (subject ?? string.Empty).Trim()
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
        }

        private static string BuildCalendarMessageReferenceText(
            NewMessageComposerContext context)
        {
            return string.Join(
                "\n",
                new[]
                {
                    $"Actividad: {context.ActivityTitle}",
                    string.IsNullOrWhiteSpace(context.ActivityUrl)
                        ? string.Empty
                        : $"Notion: {context.ActivityUrl}"
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
        }

        private static string BuildCalendarMessageBody(
            string body,
            NewMessageComposerContext? context)
        {
            var cleanBody =
                (body ?? string.Empty).Trim();

            if (context == null)
                return cleanBody;

            var reference =
                BuildCalendarMessageReferenceText(context);

            return string.Join(
                "\n\n",
                new[]
                {
                    cleanBody,
                    reference
                }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
        }

        private async Task ShowNewMessageDialogAsync(
            NewMessageComposerContext? context)
        {
            var token = GetSavedNotionToken();

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura primero el token de Notion.";
                return;
            }

            var rootWidth =
                XamlRoot == null
                    ? 1200d
                    : XamlRoot.Size.Width;

            var rootHeight =
                XamlRoot == null
                    ? 900d
                    : XamlRoot.Size.Height;

            var composerDialogWidth =
                context == null
                    ? 620d
                    : Math.Clamp(
                        rootWidth - 140d,
                        680d,
                        860d);

            var composerContentWidth =
                Math.Max(
                    560d,
                    composerDialogWidth - 56d);

            var composerContentHeight =
                Math.Clamp(
                    rootHeight - 190d,
                    480d,
                    720d);

            var recipientCombo =
                BuildMessagesPersonCombo(
                    context?.RecipientTag ?? string.Empty,
                    includeAll: true);

            recipientCombo.Header =
                "Destinatario";

            if (context != null &&
                string.IsNullOrWhiteSpace(
                    context.RecipientTag))
            {
                recipientCombo.SelectedIndex = -1;
            }

            var directionPreview =
                new TextBlock
                {
                    FontSize = 11,
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.82,
                    TextWrapping = TextWrapping.Wrap
                };

            void RefreshDirectionPreview()
            {
                var currentTag =
                    GetCurrentMessagesUserTag();

                var currentName =
                    MessagesPeople.TryGetValue(
                        currentTag,
                        out var mappedCurrent)
                        ? mappedCurrent
                        : string.IsNullOrWhiteSpace(currentTag)
                            ? "Sin usuario configurado"
                            : currentTag;

                var recipientTag =
                    recipientCombo.SelectedItem is ComboBoxItem recipientItem
                        ? recipientItem.Tag?.ToString() ?? string.Empty
                        : string.Empty;

                var recipientName =
                    IsGroupMessageRecipient(recipientTag)
                        ? MessagesAllRecipientsName
                        : MessagesPeople.TryGetValue(
                            recipientTag,
                            out var mappedRecipient)
                            ? mappedRecipient
                            : string.IsNullOrWhiteSpace(recipientTag)
                                ? "Sin destinatario"
                                : recipientTag;

                directionPreview.Text =
                    $"De: {currentName} ({currentTag}) · " +
                    $"Para: {recipientName} ({recipientTag})";
            }

            recipientCombo.SelectionChanged +=
                (_, __) => RefreshDirectionPreview();

            RefreshDirectionPreview();

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
                    Header = context == null
                        ? "Asunto"
                        : "Título / asunto",
                    PlaceholderText =
                        "Ejemplo: Revisar propuesta del cliente",
                    Text =
                        context?.ActivityTitle ??
                        string.Empty,
                    TextWrapping =
                        TextWrapping.Wrap,
                    AcceptsReturn = false,
                    MinHeight =
                        context == null
                            ? 64
                            : 72,
                    MaxHeight =
                        context == null
                            ? 92
                            : 110,
                    Padding =
                        new Thickness(10, 8, 10, 8),
                    VerticalContentAlignment =
                        VerticalAlignment.Top,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch
                };

            var messageBox =
                new TextBox
                {
                    Header = "Mensaje",
                    PlaceholderText =
                        context == null
                            ? "Escribe el contenido del mensaje..."
                            : "Escribe qué necesita revisar o realizar la persona...",
                    AcceptsReturn = true,
                    TextWrapping =
                        TextWrapping.Wrap,
                    MinHeight = 105,
                    MaxHeight = 150,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch
                };

            var datePicker =
                new DatePicker
                {
                    Header = "Fecha",
                    Date =
                        context?.SuggestedAt ??
                        GetSuggestedMessageSchedule()
                };

            var timePicker =
                new TimePicker
                {
                    Header = "Hora",
                    Time =
                        (context?.SuggestedAt ??
                         GetSuggestedMessageSchedule())
                            .TimeOfDay,
                    MinuteIncrement = 1
                };

            var domainBox =
                new TextBox
                {
                    Header = "Dominio",
                    PlaceholderText =
                        "Ejemplo: anfeta.com",
                    Text = context?.Domain ?? string.Empty
                };

            var projectCombo =
                new ComboBox
                {
                    Header = "Tipo de proyecto",
                    IsEditable = true,
                    PlaceholderText =
                        "Ejemplo: aapli, wwebs, sseo...",
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    Text = context?.ProjectType ?? string.Empty
                };

            foreach (var project in new[]
                     {
                         "aapli",
                         "wwebs",
                         "sseo",
                         "aads",
                         "pprog"
                     })
            {
                projectCombo.Items.Add(project);
            }

            var contextGrid = new Grid
            {
                ColumnSpacing = 8,
                Visibility = context == null
                    ? Visibility.Collapsed
                    : Visibility.Visible
            };

            contextGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            contextGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            Grid.SetColumn(domainBox, 0);
            contextGrid.Children.Add(domainBox);

            Grid.SetColumn(projectCombo, 1);
            contextGrid.Children.Add(projectCombo);

            var activityReferenceText =
                new TextBlock
                {
                    Text =
                        context == null
                            ? string.Empty
                            : string.Join(
                                "\n",
                                new[]
                                {
                                    "Actividad relacionada:",
                                    context.ActivityTitle,
                                    string.IsNullOrWhiteSpace(
                                        context.ActivityUrl)
                                        ? string.Empty
                                        : "El enlace de Notion se incluirá automáticamente al enviar."
                                }
                                .Where(value =>
                                    !string.IsNullOrWhiteSpace(value))),
                    TextWrapping =
                        TextWrapping.Wrap,
                    FontSize = 11,
                    Opacity = 0.90,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch
                };

            var activityReference =
                new Border
                {
                    Visibility =
                        context == null
                            ? Visibility.Collapsed
                            : Visibility.Visible,
                    Padding =
                        new Thickness(
                            12,
                            10,
                            12,
                            10),
                    CornerRadius =
                        new CornerRadius(7),
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(
                                38,
                                96,
                                165,
                                250)),
                    BorderBrush =
                        new SolidColorBrush(
                            Color.FromArgb(
                                120,
                                96,
                                165,
                                250)),
                    BorderThickness =
                        new Thickness(1),
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    Child =
                        activityReferenceText
                };

            var pending =
                new ObservableCollection<
                    PendingMessageAttachment>();

            MessageAudioComposerSession? audioComposer = null;

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
                        async (_, __) =>
                        {
                            if (remove.Tag is not
                                PendingMessageAttachment selected)
                            {
                                return;
                            }

                            if (selected.IsTemporaryRecording &&
                                audioComposer != null)
                            {
                                await audioComposer
                                    .RemoveAttachmentAsync(selected);
                            }
                            else
                            {
                                pending.Remove(selected);
                                await DeletePendingAttachmentFileAsync(
                                    selected);
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

            audioComposer =
                new MessageAudioComposerSession(
                    pending,
                    RefreshFiles,
                    status);

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
                    Width =
                        context == null
                            ? 560
                            : composerContentWidth,
                    Spacing = 10,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch
                };

            panel.Children.Add(recipientCombo);
            panel.Children.Add(directionPreview);
            panel.Children.Add(messageTypeCombo);
            panel.Children.Add(contextGrid);
            panel.Children.Add(subjectBox);
            panel.Children.Add(activityReference);
            panel.Children.Add(messageBox);
            panel.Children.Add(audioComposer.View);
            panel.Children.Add(dateRow);
            panel.Children.Add(attach);
            panel.Children.Add(filesPanel);
            panel.Children.Add(uploadProgressBar);
            panel.Children.Add(uploadProgressText);
            panel.Children.Add(status);

            var dialogScroll =
                new ScrollViewer
                {
                    MaxHeight =
                        composerContentHeight,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled,
                    HorizontalScrollMode =
                        ScrollMode.Disabled,
                    VerticalScrollMode =
                        ScrollMode.Auto,
                    Content = panel
                };

            var dialog =
                new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = context == null
                        ? "Nuevo mensaje"
                        : "Enviar mensaje desde calendario",
                    Content = dialogScroll,
                    PrimaryButtonText = context == null
                        ? "Crear y enviar"
                        : "Enviar mensaje",
                    CloseButtonText = "Cancelar",
                    DefaultButton =
                        ContentDialogButton.Primary,
                    MinWidth =
                        composerDialogWidth,
                    MaxWidth =
                        composerDialogWidth
                };

            dialog.Resources[
                "ContentDialogMinWidth"] =
                composerDialogWidth;

            dialog.Resources[
                "ContentDialogMaxWidth"] =
                composerDialogWidth;

            dialog.PrimaryButtonClick +=
                async (_, args) =>
                {
                    args.Cancel = true;

                    if (audioComposer.IsRecording)
                    {
                        status.Text =
                            "Detén la grabación antes de enviar.";
                        return;
                    }

                    if (recipientCombo.SelectedItem is not
                        ComboBoxItem selectedRecipient)
                    {
                        status.Text =
                            "Selecciona un destinatario.";
                        return;
                    }

                    var rawSubject =
                        (subjectBox.Text ??
                         string.Empty).Trim();

                    var domain =
                        (domainBox.Text ??
                         string.Empty).Trim();

                    var project =
                        (projectCombo.Text ??
                         string.Empty).Trim();

                    var subject =
                        context == null
                            ? rawSubject
                            : BuildStructuredCalendarMessageSubject(
                                domain,
                                project,
                                rawSubject);

                    var body =
                        (messageBox.Text ??
                         string.Empty).Trim();

                    if (string.IsNullOrWhiteSpace(rawSubject))
                    {
                        status.Text =
                            "Escribe un título o asunto.";
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

                    var authorTag =
                        GetCurrentMessagesUserTag();

                    if (string.IsNullOrWhiteSpace(authorTag) ||
                        !MessagesPeople.ContainsKey(authorTag))
                    {
                        status.Text =
                            "Configura correctamente tu usuario en Ajustes antes de enviar.";
                        return;
                    }

                    if (!IsGroupMessageRecipient(selectedRecipientTag) &&
                        !MessagesPeople.ContainsKey(selectedRecipientTag))
                    {
                        status.Text =
                            "El destinatario seleccionado no es válido.";
                        return;
                    }

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
                        new List<string>
                        {
                            isBroadcast
                                ? MessagesAllRecipientsTag
                                : selectedRecipientTag
                        };

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
                            BuildCalendarMessageBody(
                                body,
                                context),
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
                                    ? "Creando mensaje grupal único..."
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
                                IsGroupMessageRecipient(
                                    recipientTag)
                                    ? MessagesAllRecipientsName
                                    : MessagesPeople.TryGetValue(
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

                            var initialBody =
                                string.IsNullOrWhiteSpace(body) &&
                                initialAttachments.Count > 0
                                    ? $"Adjuntó {initialAttachments.Count} archivo(s)."
                                    : body;

                            var initialText =
                                BuildCalendarMessageBody(
                                    initialBody,
                                    context);

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
                                    NodeId =
                                        created.PageId,
                                    ExternalUrl =
                                        created.PageUrl,
                                    ExternalSourceName =
                                        "Revisiones",
                                    Source =
                                        SearchSource.Notion,
                                    Name = title,
                                    Target =
                                        created.PageUrl,
                                    Type = "NOTION_PAGE",
                                    SearchText =
                                        $"Revisiones {title}",
                                    ServerModified =
                                        DateTime.Now.ToString(
                                            "yyyy-MM-dd HH:mm",
                                            CultureInfo.InvariantCulture)
                                };

                            await UpsertCreatedMessageInLocalIndexAsync(
                                row);

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
                                ? "Mensaje grupal creado una sola vez para todo el equipo ✅"
                                : "Mensaje creado ✅";

                        dialog.Hide();

                        if (context == null &&
                            createdMessage != null)
                        {
                            // Un mensaje nuevo debe aparecer inmediatamente en
                            // la bandeja del emisor, sin esperar una sincronización
                            // completa de Notion. Se abre en la vista tipo chat.
                            foreach (var item in
                                     MessagesFilterCombo.Items
                                         .OfType<ComboBoxItem>())
                            {
                                if (!string.Equals(
                                        item.Tag?.ToString(),
                                        "conversations",
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                MessagesFilterCombo.SelectedItem = item;
                                break;
                            }

                            _messagesFilter = "conversations";
                            RefreshMessagesView();

                            var indexedMessage =
                                _messageItems.FirstOrDefault(item =>
                                    string.Equals(
                                        item.Row.ExternalId,
                                        createdMessage.Row.ExternalId,
                                        StringComparison.OrdinalIgnoreCase))
                                ?? createdMessage;

                            await SelectMessagesConversationAsync(
                                indexedMessage,
                                focusReply: true);
                        }
                        else
                        {
                            RefreshMessagesView();
                        }

                        if (context != null)
                        {
                            StatusText.Text =
                                $"Estado: Mensaje enviado desde el calendario" +
                                (string.IsNullOrWhiteSpace(
                                     context.RecipientName)
                                    ? " ✅"
                                    : $" a {context.RecipientName} ✅");
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

        private async Task UpsertCreatedMessageInLocalIndexAsync(
            SearchResultRow createdRow)
        {
            if (createdRow == null ||
                string.IsNullOrWhiteSpace(
                    createdRow.ExternalId))
            {
                return;
            }

            var snapshot =
                App.LocalIndex.GetAll();

            var existing =
                snapshot.FirstOrDefault(item =>
                    item.Source == SearchSource.Notion &&
                    string.Equals(
                        item.ExternalId,
                        createdRow.ExternalId,
                        StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                snapshot.Add(createdRow);
            }
            else
            {
                existing.NodeId = createdRow.NodeId;
                existing.Name = createdRow.Name;
                existing.Target = createdRow.Target;
                existing.Type = createdRow.Type;
                existing.SearchText = createdRow.SearchText;
                existing.ServerModified = createdRow.ServerModified;
                existing.ExternalUrl = createdRow.ExternalUrl;
                existing.ExternalSourceName =
                    createdRow.ExternalSourceName;
                existing.Source = createdRow.Source;
            }

            App.LocalIndex.Set(snapshot);

            await PersistCombinedIndexIfPossibleAsync(
                snapshot);

            try
            {
                App.AppHost.Services
                    .GetService<IndexedFileReminderService>()
                    ?.ScanNow();
            }
            catch
            {
                // El mensaje ya quedó visible en el índice local.
            }
        }

        private void MessageMarkRead_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!TryGetMessageFromSender(
                    sender,
                    out var message))
            {
                return;
            }

            MarkMessageAsRead(message);
            RefreshMessagesView();

            StatusText.Text =
                "Estado: Mensaje marcado como visto ✅";
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

            await SelectMessagesConversationAsync(
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

            MessageAudioComposerSession? replyAudioComposer = null;

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
                        async (_, __) =>
                        {
                            if (removeButton.Tag is not
                                PendingMessageAttachment selected)
                            {
                                return;
                            }

                            if (selected.IsTemporaryRecording &&
                                replyAudioComposer != null)
                            {
                                await replyAudioComposer
                                    .RemoveAttachmentAsync(selected);
                            }
                            else
                            {
                                pendingAttachments.Remove(selected);
                                await DeletePendingAttachmentFileAsync(
                                    selected);
                                await RefreshPendingAttachmentsAsync();
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

            replyAudioComposer =
                new MessageAudioComposerSession(
                    pendingAttachments,
                    () => _ = RefreshPendingAttachmentsAsync(),
                    status);

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

            Grid.SetRow(replyAudioComposer.View, 3);
            content.Children.Add(replyAudioComposer.View);

            Grid.SetRow(attachButton, 4);
            content.Children.Add(attachButton);

            Grid.SetRow(attachmentsPanel, 5);
            content.Children.Add(attachmentsPanel);

            Grid.SetRow(replyUploadProgressBar, 6);
            content.Children.Add(replyUploadProgressBar);

            Grid.SetRow(replyUploadProgressText, 7);
            content.Children.Add(replyUploadProgressText);

            Grid.SetRow(status, 8);
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

                // Las alertas de revisión ya guardan el envío inicial
                // dentro del hilo técnico. No se agrega otra tarjeta sintética
                // porque se veía como un segundo mensaje duplicado.
                if (!message.IsReviewAlert)
                {
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
                }

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
                        IsGroupMessageRecipient(
                            waitingTag)
                            ? MessagesAllRecipientsName
                            : MessagesPeople.TryGetValue(
                                  waitingTag,
                                  out var mappedWaitingName)
                                ? mappedWaitingName
                                : waitingTag;

                    conversationState.Text =
                        message.IsCompleted
                            ? "Conversación cerrada."
                            : IsGroupMessageRecipient(
                                  waitingTag)
                                ? "Mensaje compartido con todo el equipo."
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

                    if (replyAudioComposer.IsRecording)
                    {
                        status.Text =
                            "Detén la grabación antes de enviar.";
                        return;
                    }

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
                                TimeSpan.FromMinutes(15));

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
                        await DeleteTemporaryMessageAttachmentsAsync(
                            pendingAttachments.ToList());
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

            dialog.Closed +=
                async (_, __) =>
                {
                    await replyAudioComposer.DisposeAsync();
                    await DeleteTemporaryMessageAttachmentsAsync(
                        pendingAttachments.ToList());
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

            if (string.Equals(
                    attachment.BlockType,
                    "audio",
                    StringComparison.OrdinalIgnoreCase) &&
                canOpen)
            {
                var audioPlayer =
                    new MediaPlayerElement
                    {
                        Source =
                            MediaSource.CreateFromUri(
                                attachmentUri),
                        AreTransportControlsEnabled = true,
                        AutoPlay = false,
                        MinWidth = 280,
                        Height = 54,
                        HorizontalAlignment =
                            HorizontalAlignment.Stretch
                    };

                container.Children.Add(audioPlayer);
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
            if (IsGroupMessageRecipient(
                    message.RecipientTag))
            {
                return MessagesAllRecipientsTag;
            }

            if (AreSameMessagesPersonTag(
                    authorTag,
                    message.RecipientTag) &&
                !string.IsNullOrWhiteSpace(message.SenderTag))
            {
                return message.SenderTag;
            }

            if (AreSameMessagesPersonTag(
                    authorTag,
                    message.SenderTag) &&
                !string.IsNullOrWhiteSpace(message.RecipientTag))
            {
                return message.RecipientTag;
            }

            if (!string.IsNullOrWhiteSpace(message.SenderTag) &&
                !AreSameMessagesPersonTag(
                    authorTag,
                    message.SenderTag))
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

            CopyMessageText(message);
        }

        private void CopyMessageText(
            MessageViewItem message)
        {
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

            await OpenMessageInNotionAsync(message);
        }

        private async Task OpenMessageInNotionAsync(
            MessageViewItem message)
        {
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

            await OpenNotionPageWithFallbackAsync(
                target,
                desktopSuccessStatus:
                    "Mensaje abierto en Notion Desktop",
                browserSuccessStatus:
                    "Mensaje abierto en el navegador",
                failureStatus:
                    "No se pudo abrir el mensaje",
                invalidUrlStatus:
                    "El mensaje no tiene una URL válida de Notion");
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

            await OpenOriginalActivityAsync(message);
        }

        private async Task OpenOriginalActivityAsync(
            MessageViewItem message)
        {
            if (message == null ||
                string.IsNullOrWhiteSpace(message.Row.ExternalId))
            {
                StatusText.Text =
                    "Estado: Este mensaje no está vinculado a una actividad.";
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
                    "Estado: Buscando la actividad real...";

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
                        "Estado: Este recordatorio no contiene una actividad real vinculada.";
                    return;
                }

                MarkMessageAsRead(message);
                RefreshMessagesView();

                var title =
                    string.IsNullOrWhiteSpace(source.Title)
                        ? "actividad"
                        : source.Title;

                await OpenNotionPageWithFallbackAsync(
                    webUri.AbsoluteUri,
                    desktopSuccessStatus:
                        $"Actividad real abierta en Notion Desktop · {title}",
                    browserSuccessStatus:
                        $"Actividad real abierta en el navegador · {title}",
                    failureStatus:
                        "No se pudo abrir la actividad real",
                    invalidUrlStatus:
                        "La actividad real no tiene una URL válida de Notion");
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: Notion tardó demasiado en localizar la actividad real.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo abrir la actividad real → {ex.Message}";
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

            await ReassignMessageAsync(message);
        }

        private async Task ReassignMessageAsync(
            MessageViewItem message)
        {
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

            await RescheduleMessageAsync(message);
        }

        private async Task RescheduleMessageAsync(
            MessageViewItem message)
        {
            if (message.IsReviewAlert)
            {
                StatusText.Text =
                    "Estado: Las alertas de revisión no se pueden reprogramar.";
                return;
            }

            var suggestedSchedule =
                GetSuggestedMessageSchedule();

            var datePicker = new DatePicker
            {
                Date = suggestedSchedule,
                MinYear = new DateTimeOffset(
                    DateTime.Today.Year,
                    1,
                    1,
                    0,
                    0,
                    0,
                    DateTimeOffset.Now.Offset),
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            var timePicker = new TimePicker
            {
                Time = suggestedSchedule.TimeOfDay,
                MinuteIncrement = 1,
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
                    Text =
                        "Nueva fecha y hora · se propone exactamente " +
                        "el día y la hora actuales:",
                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
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

            await CompleteMessageAsync(message);
        }

        private async Task CompleteMessageAsync(
            MessageViewItem message)
        {
            if (message.IsReviewAlert)
            {
                await ApplyLinkedReviewAlertStateAsync(
                    message,
                    !message.IsCompleted);

                return;
            }

            if (!message.IsCompleted)
            {
                await CompleteMessageAndMoveToTrashAsync(message);
                return;
            }

            await ApplyMessageChangeAsync(
                message,
                message.RecipientTag,
                message.ScheduledAt,
                completed: false);
        }

        private async Task CompleteMessageAndMoveToTrashAsync(
            MessageViewItem message)
        {
            if (string.IsNullOrWhiteSpace(message.Row.ExternalId))
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

            try
            {
                ShowLoadingState(
                    "Estado: Terminando y limpiando recordatorio…",
                    message.Message);

                using var cts = new CancellationTokenSource(
                    TimeSpan.FromMinutes(2));

                var currentUser = GetCurrentMessagesUserTag();
                var currentName = MessagesPeople.TryGetValue(
                    currentUser,
                    out var mapped)
                        ? mapped
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
                            $"Recordatorio terminado por {currentName} " +
                            $"el {DateTimeOffset.Now:dd/MM/yyyy HH:mm}. " +
                            "La página fue enviada a la papelera."
                    },
                    cts.Token);

                var pageActions = new NotionPageActionsService();

                await pageActions.MovePageToTrashAsync(
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
                    "Estado: Recordatorio terminado y enviado a la papelera ✅";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo terminar el recordatorio → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
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

                var removedIds = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

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

                    if (completed)
                    {
                        // Una alerta atendida se elimina para no acumular
                        // notificaciones. Si la actividad vuelve a revisión,
                        // el calendario detectará que este PageId ya no está
                        // activo y creará una notificación nueva.
                        await pageActions.MovePageToTrashAsync(
                            token,
                            alert.Row.ExternalId,
                            cts.Token);

                        removedIds.Add(alert.Row.ExternalId);
                    }
                }

                if (removedIds.Count > 0)
                {
                    await RemoveNotionRowsFromIndexAsync(
                        removedIds);
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

            await DeleteMessageAsync(message);
        }

        private async Task DeleteMessageAsync(
            MessageViewItem message)
        {
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

                if (!AreSameMessagesPersonTag(
                        message.RecipientTag,
                        recipientTag))
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
                    AreSameMessagesPersonTag(
                        item.Tag?.ToString(),
                        selectedTag));

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
                MessageBelongsToCurrentUser(
                    item.SenderTag,
                    item.RecipientTag,
                    currentUser);

            if (!belongsToCurrentUser)
                return false;

            message = item;
            return true;
        }
    }
}