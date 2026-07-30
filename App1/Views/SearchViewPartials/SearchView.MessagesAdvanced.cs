using Anfeta.UI.Models.Notion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private FrameworkElement BuildPendingMessageAttachmentPreview(
            PendingMessageAttachment attachment)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (IsMessageImageFile(attachment.FileName) &&
                File.Exists(attachment.Path))
            {
                try
                {
                    panel.Children.Add(
                        new Border
                        {
                            Width = 48,
                            Height = 48,
                            CornerRadius = new CornerRadius(6),
                            BorderThickness = new Thickness(1),
                            BorderBrush = new SolidColorBrush(
                                Color.FromArgb(90, 255, 255, 255)),
                            Child = new Image
                            {
                                Source = new BitmapImage(
                                    new Uri(attachment.Path)),
                                Stretch = Stretch.UniformToFill
                            }
                        });
                }
                catch
                {
                }
            }

            panel.Children.Add(
                new TextBlock
                {
                    Text = $"📎 {attachment.FileName}",
                    MaxWidth = 350,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });

            return panel;
        }

        private static bool IsMessageImageFile(string fileName)
        {
            var extension = Path.GetExtension(
                fileName ?? string.Empty).ToLowerInvariant();

            return extension is
                ".png" or ".jpg" or ".jpeg" or ".gif" or
                ".webp" or ".bmp";
        }

        private async Task<bool> EnsureMessageReadReceiptAsync(
            string token,
            string pageId,
            IReadOnlyList<MessageThreadEntry> entries)
        {
            var currentTag = GetCurrentMessagesUserTag();

            if (string.IsNullOrWhiteSpace(currentTag))
                return false;

            var latest = entries
                .Where(entry =>
                    entry.Kind == MessageThreadKind.Message &&
                    !string.IsNullOrWhiteSpace(entry.Id) &&
                    string.Equals(
                        entry.RecipientTag,
                        currentTag,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.CreatedAt)
                .FirstOrDefault();

            if (latest == null)
                return false;

            var alreadyRead = entries.Any(entry =>
                entry.Kind == MessageThreadKind.ReadReceipt &&
                string.Equals(
                    entry.ReferenceEntryId,
                    latest.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    entry.AuthorTag,
                    currentTag,
                    StringComparison.OrdinalIgnoreCase));

            if (alreadyRead)
                return false;

            var currentName =
                MessagesPeople.TryGetValue(
                    currentTag,
                    out var mapped)
                    ? mapped
                    : currentTag;

            await _messageThreadService.AppendEntryAsync(
                token,
                pageId,
                new MessageThreadEntry
                {
                    Kind = MessageThreadKind.ReadReceipt,
                    AuthorTag = currentTag,
                    AuthorName = currentName,
                    CreatedAt = DateTimeOffset.Now,
                    ReferenceEntryId = latest.Id,
                    Text =
                        $"✓✓ Leído por {currentName} " +
                        $"el {DateTimeOffset.Now:dd/MM/yyyy HH:mm}"
                });

            return true;
        }

        private Border BuildAdvancedMessageThreadCard(
            MessageThreadEntry entry,
            bool isOriginal,
            string pageId,
            string token,
            Func<Task> reloadThread,
            TextBlock status)
        {
            if (entry.Kind == MessageThreadKind.ReadReceipt)
            {
                return new Border
                {
                    MaxWidth = 360,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(36, 1, 2, 1),
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(
                        Color.FromArgb(140, 52, 211, 153)),
                    Background = new SolidColorBrush(
                        Color.FromArgb(28, 52, 211, 153)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = new TextBlock
                    {
                        Text = entry.Text,
                        MaxWidth = 340,
                        FontSize = 10.5,
                        Opacity = 0.86,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.None
                    }
                };
            }

            var card = BuildMessageThreadCard(
                entry,
                isOriginal);

            card.MaxWidth = 480;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;

            var currentTag = GetCurrentMessagesUserTag();
            var canModify =
                !isOriginal &&
                entry.Kind == MessageThreadKind.Message &&
                !string.IsNullOrWhiteSpace(entry.Id) &&
                !string.IsNullOrWhiteSpace(currentTag) &&
                string.Equals(
                    entry.AuthorTag,
                    currentTag,
                    StringComparison.OrdinalIgnoreCase);

            if (!canModify ||
                card.Child is not StackPanel panel)
            {
                return card;
            }

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var editButton = new Button
            {
                Content = "Editar",
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 10.5
            };

            editButton.Click += async (_, __) =>
            {
                var editor = new TextBox
                {
                    Text = entry.Text,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 420,
                    MinHeight = 100
                };

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Editar respuesta",
                    Content = editor,
                    PrimaryButtonText = "Guardar",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Primary
                };

                if (await dialog.ShowAsync() !=
                    ContentDialogResult.Primary)
                {
                    return;
                }

                var newText = (editor.Text ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(newText))
                {
                    status.Text = "La respuesta no puede quedar vacía.";
                    return;
                }

                try
                {
                    using var cts =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(60));

                    await _messageThreadService.UpdateEntryAsync(
                        token,
                        entry.Id,
                        new MessageThreadEntry
                        {
                            Id = entry.Id,
                            Kind = entry.Kind,
                            AuthorTag = entry.AuthorTag,
                            AuthorName = entry.AuthorName,
                            RecipientTag = entry.RecipientTag,
                            RecipientName = entry.RecipientName,
                            CreatedAt = entry.CreatedAt,
                            ReferenceEntryId =
                                entry.ReferenceEntryId,
                            Text = newText,
                            Attachments = entry.Attachments
                        },
                        cts.Token);

                    status.Text = "Respuesta editada ✅";
                    await reloadThread();
                }
                catch (Exception ex)
                {
                    status.Text =
                        $"No se pudo editar → {ex.Message}";
                }
            };

            var deleteButton = new Button
            {
                Content = "Eliminar respuesta",
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 10.5
            };

            deleteButton.Click += async (_, __) =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Eliminar respuesta",
                    Content =
                        "Se eliminará únicamente esta respuesta. " +
                        "La conversación y la actividad original permanecerán intactas.",
                    PrimaryButtonText = "Eliminar",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close
                };

                if (await dialog.ShowAsync() !=
                    ContentDialogResult.Primary)
                {
                    return;
                }

                try
                {
                    using var cts =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(60));

                    await _messageThreadService.DeleteEntryAsync(
                        token,
                        entry.Id,
                        cts.Token);

                    status.Text = "Respuesta eliminada ✅";
                    await reloadThread();
                }
                catch (Exception ex)
                {
                    status.Text =
                        $"No se pudo eliminar → {ex.Message}";
                }
            };

            actions.Children.Add(editButton);
            actions.Children.Add(deleteButton);
            panel.Children.Add(actions);

            return card;
        }
    }
}
