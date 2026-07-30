using Anfeta.UI.Models.Notion;
using Anfeta.UI.Models.Weblab;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
using Windows.UI.Text;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        private void ResetPreviewPanel()
        {
            try
            {
                _notionPreviewCts?.Cancel();
                _notionPreviewCts?.Dispose();
            }
            catch
            {
                // La cancelación es opcional.
            }

            _notionPreviewCts = null;
            _activePreviewPageId = string.Empty;
            _activePreviewBlocks = Array.Empty<NotionPreviewBlock>();
            _activePreviewRow = null;
            StopNotionPreviewSpeech();

            if (NotionPreviewProgress != null)
            {
                NotionPreviewProgress.IsActive = false;
                NotionPreviewProgress.Visibility = Visibility.Collapsed;
            }

            if (NotionPreviewStatus != null)
            {
                NotionPreviewStatus.Text =
                    "La vista previa está disponible para páginas de Notion.";
            }

            if (NotionPreviewContent != null)
                NotionPreviewContent.Children.Clear();

            if (NotionPreviewCard != null)
                NotionPreviewCard.Visibility = Visibility.Collapsed;

            ResetLocalImagePreview();
        }

        private async Task LoadNotionPreviewForSelectionAsync(
            SearchResultRow row)
        {
            if (row == null || !IsNotionRow(row))
            {
                ResetPreviewPanel();
                return;
            }

            var pageId =
                (row.ExternalId ?? string.Empty).Trim();

            NotionPreviewCard.Visibility = Visibility.Visible;
            _activePreviewRow = row;
            NotionPreviewContent.Children.Clear();

            AddDescriptionPreview(row.Description);

            if (string.IsNullOrWhiteSpace(pageId))
            {
                NotionPreviewStatus.Text =
                    "No se encontró el identificador de esta página. " +
                    "Se muestra únicamente la descripción indexada.";
                return;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                NotionPreviewStatus.Text =
                    "Configura el token de Notion para cargar el contenido completo.";
                return;
            }

            try
            {
                _notionPreviewCts?.Cancel();
                _notionPreviewCts?.Dispose();
            }
            catch
            {
                // No bloquea la nueva selección.
            }

            _notionPreviewCts =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(90));

            var localCts = _notionPreviewCts;
            _activePreviewPageId = pageId;

            NotionPreviewProgress.IsActive = true;
            NotionPreviewProgress.Visibility = Visibility.Visible;
            NotionPreviewStatus.Text =
                "Cargando contenido de Notion...";

            try
            {
                var blocks =
                    await _notionPreviewService.GetPagePreviewAsync(
                        token,
                        pageId,
                        localCts.Token);

                if (localCts.IsCancellationRequested ||
                    !string.Equals(
                        _activePreviewPageId,
                        pageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _activePreviewBlocks = blocks;
                RenderNotionPreviewBlocks(
                    row,
                    blocks);

                NotionPreviewStatus.Text =
                    blocks.Count == 0
                        ? "La página no tiene bloques visibles. " +
                          "Se muestra la información disponible."
                        : $"{blocks.Count} bloque(s) cargados · desplázate para ver el contenido.";
            }
            catch (OperationCanceledException)
            {
                if (string.Equals(
                        _activePreviewPageId,
                        pageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    NotionPreviewStatus.Text =
                        "Vista previa cancelada.";
                }
            }
            catch (Exception ex)
            {
                if (!string.Equals(
                        _activePreviewPageId,
                        pageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                NotionPreviewStatus.Text =
                    $"No se pudo cargar el contenido completo: {ex.Message}";

                if (NotionPreviewContent.Children.Count == 0)
                    AddDescriptionPreview(row.Description);
            }
            finally
            {
                if (string.Equals(
                        _activePreviewPageId,
                        pageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    NotionPreviewProgress.IsActive = false;
                    NotionPreviewProgress.Visibility =
                        Visibility.Collapsed;
                }
            }
        }

        private void AddDescriptionPreview(string? description)
        {
            var clean =
                (description ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return;

            NotionPreviewContent.Children.Add(
                CreatePreviewText(
                    clean,
                    fontSize: 11,
                    fontWeight: FontWeights.Normal,
                    opacity: 0.88,
                    leftMargin: 0));
        }

        private void RenderNotionPreviewBlocks(
            SearchResultRow row,
            IReadOnlyList<NotionPreviewBlock> blocks)
        {
            NotionPreviewContent.Children.Clear();

            var cleanDescription =
                (row.Description ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(cleanDescription))
            {
                NotionPreviewContent.Children.Add(
                    CreateSectionLabel(
                        "DESCRIPCIÓN"));

                AddDescriptionPreview(
                    cleanDescription);
            }

            var visibleBlocks = blocks
                .Where(block =>
                    block.Kind == NotionPreviewBlockKind.Divider ||
                    !string.IsNullOrWhiteSpace(block.Text) ||
                    !string.IsNullOrWhiteSpace(block.Url))
                .ToList();

            if (visibleBlocks.Count > 0)
            {
                NotionPreviewContent.Children.Add(
                    CreateSectionLabel(
                        "CONTENIDO DE LA PÁGINA"));
            }

            var number = 0;

            foreach (var block in visibleBlocks)
            {
                if (block.Kind ==
                    NotionPreviewBlockKind.NumberedListItem)
                {
                    number++;
                }
                else
                {
                    number = 0;
                }

                var element =
                    CreateBlockElement(
                        block,
                        number);

                if (element != null)
                    NotionPreviewContent.Children.Add(element);
            }

            if (NotionPreviewContent.Children.Count == 0)
            {
                NotionPreviewContent.Children.Add(
                    CreatePreviewText(
                        "Esta página no contiene una descripción o bloques visibles.",
                        11,
                        FontWeights.Normal,
                        0.65,
                        0));
            }
        }

        private UIElement? CreateBlockElement(
            NotionPreviewBlock block,
            int number)
        {
            var indent =
                Math.Min(36, block.Depth * 12);

            return block.Kind switch
            {
                NotionPreviewBlockKind.Heading1 =>
                    CreatePreviewText(
                        block.Text,
                        15,
                        FontWeights.Bold,
                        1.0,
                        indent),

                NotionPreviewBlockKind.Heading2 =>
                    CreatePreviewText(
                        block.Text,
                        13,
                        FontWeights.SemiBold,
                        1.0,
                        indent),

                NotionPreviewBlockKind.Heading3 =>
                    CreatePreviewText(
                        block.Text,
                        12,
                        FontWeights.SemiBold,
                        0.95,
                        indent),

                NotionPreviewBlockKind.BulletedListItem =>
                    CreatePreviewText(
                        $"• {block.Text}",
                        11,
                        FontWeights.Normal,
                        0.90,
                        indent),

                NotionPreviewBlockKind.NumberedListItem =>
                    CreatePreviewText(
                        $"{number}. {block.Text}",
                        11,
                        FontWeights.Normal,
                        0.90,
                        indent),

                NotionPreviewBlockKind.ToDo =>
                    CreateToDoElement(
                        block,
                        indent),

                NotionPreviewBlockKind.Quote =>
                    CreateQuoteElement(
                        block.Text,
                        indent),

                NotionPreviewBlockKind.Callout =>
                    CreateCalloutElement(
                        block.Text,
                        indent),

                NotionPreviewBlockKind.Divider =>
                    new Border
                    {
                        Height = 1,
                        Margin = new Thickness(
                            indent,
                            4,
                            0,
                            4),
                        Background =
                            Application.Current.Resources[
                                "DividerStrokeColorDefaultBrush"]
                            as Brush
                    },

                NotionPreviewBlockKind.Code =>
                    CreateCodeElement(
                        block,
                        indent),

                NotionPreviewBlockKind.Image or
                NotionPreviewBlockKind.Pdf or
                NotionPreviewBlockKind.File or
                NotionPreviewBlockKind.Audio or
                NotionPreviewBlockKind.Video or
                NotionPreviewBlockKind.Bookmark or
                NotionPreviewBlockKind.LinkPreview =>
                    CreateResourceElement(
                        block,
                        indent),

                NotionPreviewBlockKind.ChildPage =>
                    CreatePreviewText(
                        $"📄 {block.Text}",
                        11,
                        FontWeights.SemiBold,
                        0.90,
                        indent),

                NotionPreviewBlockKind.ChildDatabase =>
                    CreatePreviewText(
                        $"🗃 {block.Text}",
                        11,
                        FontWeights.SemiBold,
                        0.90,
                        indent),

                NotionPreviewBlockKind.Toggle =>
                    CreateToggleElement(
                        block,
                        indent),

                NotionPreviewBlockKind.Equation =>
                    CreateEquationElement(
                        block.Text,
                        indent),

                NotionPreviewBlockKind.TableRow =>
                    CreateTableRowElement(
                        block.Text,
                        indent),

                NotionPreviewBlockKind.Embed =>
                    CreateResourceElement(
                        block,
                        indent),

                NotionPreviewBlockKind.Unsupported =>
                    string.IsNullOrWhiteSpace(block.Text)
                        ? null
                        : CreatePreviewText(
                            block.Text,
                            10,
                            FontWeights.Normal,
                            0.55,
                            indent),

                _ =>
                    CreatePreviewText(
                        block.Text,
                        11,
                        FontWeights.Normal,
                        0.88,
                        indent)
            };
        }

        private static TextBlock CreatePreviewText(
            string text,
            double fontSize,
            FontWeight fontWeight,
            double opacity,
            double leftMargin)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = fontWeight,
                Opacity = opacity,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(
                    leftMargin,
                    0,
                    0,
                    0)
            };
        }

        private static TextBlock CreateSectionLabel(
            string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.55,
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        private static UIElement CreateToDoElement(
            NotionPreviewBlock block,
            double indent)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(
                    indent,
                    0,
                    0,
                    0)
            };

            panel.Children.Add(
                new CheckBox
                {
                    IsChecked = block.IsChecked,
                    IsEnabled = false,
                    VerticalAlignment =
                        VerticalAlignment.Top
                });

            panel.Children.Add(
                new TextBlock
                {
                    Text = block.Text,
                    FontSize = 11,
                    Opacity =
                        block.IsChecked ? 0.55 : 0.90,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                });

            return panel;
        }

        private static UIElement CreateQuoteElement(
            string text,
            double indent)
        {
            return new Border
            {
                Margin = new Thickness(
                    indent,
                    0,
                    0,
                    0),
                Padding = new Thickness(
                    8,
                    5,
                    6,
                    5),
                BorderThickness =
                    new Thickness(2, 0, 0, 0),
                BorderBrush =
                    Application.Current.Resources[
                        "SystemControlHighlightAccentBrush"]
                    as Brush,
                Child = CreatePreviewText(
                    text,
                    11,
                    FontWeights.Normal,
                    0.88,
                    0)
            };
        }

        private static UIElement CreateCalloutElement(
            string text,
            double indent)
        {
            return new Border
            {
                Margin = new Thickness(
                    indent,
                    0,
                    0,
                    0),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(5),
                Background =
                    Application.Current.Resources[
                        "ControlAltFillColorSecondaryBrush"]
                    as Brush,
                Child = CreatePreviewText(
                    $"💡 {text}",
                    11,
                    FontWeights.Normal,
                    0.90,
                    0)
            };
        }

        private static UIElement CreateCodeElement(
            NotionPreviewBlock block,
            double indent)
        {
            var panel = new StackPanel
            {
                Spacing = 4
            };

            if (!string.IsNullOrWhiteSpace(
                    block.Language))
            {
                panel.Children.Add(
                    CreatePreviewText(
                        block.Language.ToUpperInvariant(),
                        9,
                        FontWeights.SemiBold,
                        0.50,
                        0));
            }

            panel.Children.Add(
                new TextBlock
                {
                    Text = block.Text,
                    FontFamily =
                        new FontFamily("Consolas"),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.90
                });

            return new Border
            {
                Margin = new Thickness(
                    indent,
                    0,
                    0,
                    0),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(5),
                Background =
                    Application.Current.Resources[
                        "ControlAltFillColorSecondaryBrush"]
                    as Brush,
                Child = panel
            };
        }

        private static UIElement CreateToggleElement(
            NotionPreviewBlock block,
            double indent)
        {
            var expander = new Expander
            {
                Header = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(block.Text)
                        ? "Sección"
                        : block.Text,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                IsExpanded = true,
                Margin = new Thickness(indent, 0, 0, 0)
            };

            return expander;
        }

        private static UIElement CreateEquationElement(
            string text,
            double indent)
        {
            return new Border
            {
                Margin = new Thickness(indent, 0, 0, 0),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(5),
                Background =
                    Application.Current.Resources[
                        "ControlAltFillColorSecondaryBrush"] as Brush,
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Cambria Math"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private static UIElement CreateTableRowElement(
            string text,
            double indent)
        {
            return new Border
            {
                Margin = new Thickness(indent, 0, 0, 0),
                Padding = new Thickness(8, 5, 8, 5),
                BorderBrush =
                    Application.Current.Resources[
                        "DividerStrokeColorDefaultBrush"] as Brush,
                BorderThickness = new Thickness(1),
                Child = CreatePreviewText(
                    text,
                    10,
                    FontWeights.Normal,
                    0.90,
                    0)
            };
        }

        private UIElement CreateResourceElement(
            NotionPreviewBlock block,
            double indent)
        {
            var kindLabel = block.Kind switch
            {
                NotionPreviewBlockKind.Image => "Imagen",
                NotionPreviewBlockKind.Pdf => "PDF",
                NotionPreviewBlockKind.Audio => "Audio",
                NotionPreviewBlockKind.Video => "Video",
                NotionPreviewBlockKind.Bookmark => "Enlace",
                NotionPreviewBlockKind.LinkPreview => "Vista de enlace",
                NotionPreviewBlockKind.Embed => "Contenido insertado",
                _ => "Archivo"
            };

            var panel = new StackPanel
            {
                Spacing = 4,
                Margin = new Thickness(
                    indent,
                    0,
                    0,
                    0)
            };

            panel.Children.Add(
                CreatePreviewText(
                    $"📎 {kindLabel}",
                    11,
                    FontWeights.SemiBold,
                    0.90,
                    0));

            var label =
                !string.IsNullOrWhiteSpace(block.Caption)
                    ? block.Caption
                    : !string.IsNullOrWhiteSpace(block.Text)
                        ? block.Text
                        : "Abrir recurso";

            if (!string.IsNullOrWhiteSpace(block.Url) &&
                Uri.TryCreate(
                    block.Url,
                    UriKind.Absolute,
                    out var uri))
            {
                var button = new HyperlinkButton
                {
                    Content = label,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    HorizontalAlignment =
                        HorizontalAlignment.Left
                };

                button.Click += async (_, __) =>
                {
                    try
                    {
                        await Launcher.LaunchUriAsync(uri);
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text =
                            $"Estado: No se pudo abrir el recurso → {ex.Message}";
                    }
                };

                panel.Children.Add(button);
            }
            else
            {
                panel.Children.Add(
                    CreatePreviewText(
                        label,
                        10,
                        FontWeights.Normal,
                        0.65,
                        0));
            }

            return panel;
        }

        private static bool IsLocalImagePreviewSupported(string? path)
        {
            var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
        }

        private void ResetLocalImagePreview()
        {
            try
            {
                _localImagePreviewCts?.Cancel();
                _localImagePreviewCts?.Dispose();
            }
            catch
            {
            }

            _localImagePreviewCts = null;
            _activeLocalImagePath = string.Empty;
            _localImageZoom = 1.0;
            _localImagePixelWidth = 0;
            _localImagePixelHeight = 0;
            _localImageFitMode = false;

            if (LocalImagePreview != null)
            {
                LocalImagePreview.Source = null;
                LocalImagePreview.Width = double.NaN;
                LocalImagePreview.Height = double.NaN;
            }

            if (LocalImageCanvas != null)
            {
                LocalImageCanvas.Width = double.NaN;
                LocalImageCanvas.Height = double.NaN;
            }

            if (LocalImagePreviewProgress != null)
            {
                LocalImagePreviewProgress.IsActive = false;
                LocalImagePreviewProgress.Visibility = Visibility.Collapsed;
            }

            if (LocalImagePreviewCard != null)
                LocalImagePreviewCard.Visibility = Visibility.Collapsed;
        }

        private async Task LoadLocalPreviewForSelectionAsync(SearchResultRow row)
        {
            if (row == null ||
                IsNotionRow(row) ||
                row.IsFolder ||
                !IsLocalImagePreviewSupported(row.Target))
            {
                ResetLocalImagePreview();
                return;
            }

            var path = (row.Target ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ResetLocalImagePreview();
                return;
            }

            ResetLocalImagePreview();

            _activeLocalImagePath = path;
            _localImagePreviewCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var localCts = _localImagePreviewCts;

            LocalImagePreviewCard.Visibility = Visibility.Visible;

            EnsureLocalImageWheelHandler();

            LocalImagePreviewProgress.IsActive = true;
            LocalImagePreviewProgress.Visibility = Visibility.Visible;
            LocalImagePreviewStatus.Text = "Cargando vista previa de imagen...";

            try
            {
                if (NeedsHydration(path))
                {
                    LocalImagePreviewStatus.Text = "Descargando imagen desde Dropbox...";

                    var hydrated = await EnsureHydratedAsync(path, localCts.Token);
                    if (!hydrated)
                    {
                        LocalImagePreviewStatus.Text = "No se pudo descargar la imagen.";
                        return;
                    }
                }

                localCts.Token.ThrowIfCancellationRequested();

                var storageFile = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await storageFile.OpenReadAsync();

                var bitmap = new BitmapImage
                {
                    CreateOptions = BitmapCreateOptions.IgnoreImageCache
                };

                await bitmap.SetSourceAsync(stream);

                if (localCts.IsCancellationRequested ||
                    !string.Equals(_activeLocalImagePath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                LocalImagePreview.Source = bitmap;
                _localImageFitMode = true;
                LocalImagePreviewStatus.Text = "Ctrl + rueda para zoom · Ctrl + 0 para 100%.";
            }
            catch (OperationCanceledException)
            {
                LocalImagePreviewStatus.Text = "Vista previa cancelada.";
            }
            catch (Exception ex)
            {
                LocalImagePreviewStatus.Text = $"No se pudo mostrar la imagen: {ex.Message}";
            }
            finally
            {
                if (string.Equals(_activeLocalImagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    LocalImagePreviewProgress.IsActive = false;
                    LocalImagePreviewProgress.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void LocalImagePreview_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (LocalImagePreview.Source is not BitmapImage bitmap)
                return;

            _localImagePixelWidth = Math.Max(1, bitmap.PixelWidth);
            _localImagePixelHeight = Math.Max(1, bitmap.PixelHeight);

            if (_localImageFitMode)
                ApplyLocalImageFit();
            else
                ApplyLocalImageZoom();
        }

        private void LocalImagePreview_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            LocalImagePreviewProgress.IsActive = false;
            LocalImagePreviewProgress.Visibility = Visibility.Collapsed;
            LocalImagePreviewStatus.Text = $"No se pudo decodificar la imagen: {e.ErrorMessage}";
        }

        private void EnsureLocalImageWheelHandler()
        {
            if (_localImageWheelHandlerHooked ||
                LocalImageScrollViewer == null)
            {
                return;
            }

            LocalImageScrollViewer.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(
                    LocalImageScrollViewer_PointerWheelChanged),
                handledEventsToo: true);

            LocalImagePreviewCard.AddHandler(
                UIElement.KeyDownEvent,
                new KeyEventHandler(
                    LocalImagePreviewCard_KeyDown),
                handledEventsToo: true);

            _localImageWheelHandlerHooked = true;
        }

        private void LocalImageScrollViewer_PointerPressed(
            object sender,
            PointerRoutedEventArgs e)
        {
            LocalImageScrollViewer.Focus(
                FocusState.Pointer);
        }

        private void LocalImageScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!IsControlKeyDown())
                return;

            var delta = e.GetCurrentPoint(LocalImageScrollViewer)
                .Properties.MouseWheelDelta;

            var previousHorizontal =
                LocalImageScrollViewer.HorizontalOffset;

            var previousVertical =
                LocalImageScrollViewer.VerticalOffset;

            var previousZoom = _localImageZoom;

            SetLocalImageZoom(
                _localImageZoom +
                (delta > 0 ? 0.15 : -0.15));

            if (previousZoom > 0)
            {
                var ratio =
                    _localImageZoom / previousZoom;

                LocalImageScrollViewer.ChangeView(
                    previousHorizontal * ratio,
                    previousVertical * ratio,
                    null,
                    disableAnimation: true);
            }

            e.Handled = true;
        }

        private void LocalImagePreviewCard_KeyDown(
            object sender,
            KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Number0 &&
                IsControlKeyDown())
            {
                SetLocalImageZoom(1.0);
                e.Handled = true;
            }
        }

        private static bool IsControlKeyDown()
        {
            var leftState =
                Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.LeftControl);

            var rightState =
                Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.RightControl);

            const Windows.UI.Core.CoreVirtualKeyStates down =
                Windows.UI.Core.CoreVirtualKeyStates.Down;

            return (leftState & down) == down ||
                   (rightState & down) == down;
        }

        private void LocalImageZoomIn_Click(object sender, RoutedEventArgs e)
            => SetLocalImageZoom(_localImageZoom + 0.15);

        private void LocalImageZoomOut_Click(object sender, RoutedEventArgs e)
            => SetLocalImageZoom(_localImageZoom - 0.15);

        private void LocalImageResetZoom_Click(object sender, RoutedEventArgs e)
            => SetLocalImageZoom(1.0);

        private void LocalImageFit_Click(object sender, RoutedEventArgs e)
            => ApplyLocalImageFit();

        private void SetLocalImageZoom(double zoom)
        {
            if (_localImagePixelWidth <= 0 || _localImagePixelHeight <= 0)
                return;

            _localImageFitMode = false;
            _localImageZoom = Math.Clamp(zoom, 0.25, 5.0);
            ApplyLocalImageZoom();
        }

        private void ApplyLocalImageZoom()
        {
            if (_localImagePixelWidth <= 0 || _localImagePixelHeight <= 0)
                return;

            var width = Math.Max(1, _localImagePixelWidth * _localImageZoom);
            var height = Math.Max(1, _localImagePixelHeight * _localImageZoom);

            LocalImagePreview.Width = width;
            LocalImagePreview.Height = height;
            LocalImageCanvas.Width = width;
            LocalImageCanvas.Height = height;

            LocalImagePreviewStatus.Text =
                $"Zoom: {_localImageZoom * 100:0}% · Ctrl + rueda para ajustar.";
        }

        private void ApplyLocalImageFit()
        {
            if (_localImagePixelWidth <= 0 || _localImagePixelHeight <= 0)
                return;

            var viewportWidth = Math.Max(1, LocalImageScrollViewer.ViewportWidth - 12);
            var viewportHeight = Math.Max(1, LocalImageScrollViewer.ViewportHeight - 12);

            var widthScale = viewportWidth / _localImagePixelWidth;
            var heightScale = viewportHeight / _localImagePixelHeight;

            _localImageZoom = Math.Clamp(Math.Min(widthScale, heightScale), 0.25, 5.0);
            _localImageFitMode = true;
            ApplyLocalImageZoom();

            LocalImagePreviewStatus.Text =
                $"Ajustada a la ventana · {_localImageZoom * 100:0}%";
        }

        private async void BtnReadNotionPreview_Click(object sender, RoutedEventArgs e)
        {
            var speechText = BuildNotionPreviewSpeechText(_activePreviewRow, _activePreviewBlocks);
            if (string.IsNullOrWhiteSpace(speechText))
            {
                NotionPreviewStatus.Text = "No hay contenido pendiente para leer.";
                return;
            }

            try
            {
                StopNotionPreviewSpeech();
                var stream = await _previewSpeechSynth.SynthesizeTextToStreamAsync(speechText);
                _previewSpeechPlayer = new MediaPlayer();
                _previewSpeechPlayer.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
                _previewSpeechPlayer.MediaEnded += (_, __) => DispatcherQueue.TryEnqueue(StopNotionPreviewSpeech);
                _previewSpeechPlayer.MediaFailed += (_, __) => DispatcherQueue.TryEnqueue(StopNotionPreviewSpeech);
                _previewSpeechPlaying = true;
                BtnReadNotionPreview.Content = "🔊 Leyendo...";
                BtnReadNotionPreview.IsEnabled = false;
                BtnStopNotionPreviewSpeech.IsEnabled = true;
                NotionPreviewStatus.Text = "Leyendo contenido pendiente de la página...";
                _previewSpeechPlayer.Play();
            }
            catch (Exception ex)
            {
                StopNotionPreviewSpeech();
                NotionPreviewStatus.Text = $"No se pudo iniciar la lectura: {ex.Message}";
            }
        }

        private void BtnStopNotionPreviewSpeech_Click(object sender, RoutedEventArgs e)
            => StopNotionPreviewSpeech();

        private void StopNotionPreviewSpeech()
        {
            try { _previewSpeechPlayer?.Pause(); } catch { }
            try { _previewSpeechPlayer?.Dispose(); } catch { }
            _previewSpeechPlayer = null;
            _previewSpeechPlaying = false;
            if (BtnReadNotionPreview != null)
            {
                BtnReadNotionPreview.Content = "▶ Leer";
                BtnReadNotionPreview.IsEnabled = true;
            }
            if (BtnStopNotionPreviewSpeech != null)
                BtnStopNotionPreviewSpeech.IsEnabled = false;
        }

        private static string BuildNotionPreviewSpeechText(
            SearchResultRow? row,
            IReadOnlyList<NotionPreviewBlock> blocks)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(row?.Description))
                parts.Add(CleanSpeechText(row.Description));

            foreach (var block in blocks ?? Array.Empty<NotionPreviewBlock>())
            {
                if (block.IsStrikethrough ||
                    (block.Kind == NotionPreviewBlockKind.ToDo && block.IsChecked) ||
                    block.Kind is NotionPreviewBlockKind.Divider or
                        NotionPreviewBlockKind.Image or NotionPreviewBlockKind.Pdf or
                        NotionPreviewBlockKind.File or NotionPreviewBlockKind.Audio or
                        NotionPreviewBlockKind.Video or NotionPreviewBlockKind.Embed)
                {
                    continue;
                }

                var clean = CleanSpeechText(block.Text);
                if (!string.IsNullOrWhiteSpace(clean))
                    parts.Add(clean);
            }

            return string.Join(". ", parts
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string CleanSpeechText(string? value)
        {
            var text = value ?? string.Empty;
            text = Regex.Replace(text,
                @"(?<![\p{L}\p{Nd}_])(?:prtuzREVISION|rtuzREVISION|zREVISION|sprtuzREVISION)(?![\p{L}\p{Nd}_])",
                " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"\bRevisiones\b", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '–', '—', ':', '|', '/');
            return text;
        }
    }
}
