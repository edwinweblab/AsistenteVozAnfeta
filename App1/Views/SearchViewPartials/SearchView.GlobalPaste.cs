using Anfeta.UI.Services.Notion;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        // BLOQUE 11 · evita abrir dos flujos de pegado al mismo tiempo.
        private bool _globalPasteBusy;

        private async void RootLayout_GlobalPasteKeyDown(
            object sender,
            KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.V ||
                !IsGlobalPasteControlDown() ||
                _globalPasteBusy ||
                IsGlobalPasteEditableTarget())
            {
                return;
            }

            DataPackageView clipboard;

            try
            {
                clipboard = Clipboard.GetContent();
            }
            catch
            {
                return;
            }

            var hasBitmap =
                clipboard.Contains(StandardDataFormats.Bitmap);

            var hasText =
                clipboard.Contains(StandardDataFormats.Text);

            if (!hasBitmap && !hasText)
                return;

            // A partir de aquí el Ctrl+V pertenece a ANFETA. La imagen tiene
            // prioridad cuando Windows publica imagen + representación textual.
            e.Handled = true;
            _globalPasteBusy = true;

            try
            {
                if (hasBitmap)
                {
                    await HandleGlobalPasteBitmapAsync(
                        clipboard);
                    return;
                }

                var text =
                    await clipboard.GetTextAsync();

                if (string.IsNullOrWhiteSpace(text))
                {
                    StatusText.Text =
                        "Estado: El texto del portapapeles está vacío.";
                    return;
                }

                await ShowGlobalPasteTextDialogAsync(text);
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo pegar desde el portapapeles → {ex.Message}";
            }
            finally
            {
                _globalPasteBusy = false;
            }
        }

        private static bool IsGlobalPasteControlDown()
        {
            const CoreVirtualKeyStates down =
                CoreVirtualKeyStates.Down;

            var left =
                InputKeyboardSource
                    .GetKeyStateForCurrentThread(
                        VirtualKey.LeftControl);

            var right =
                InputKeyboardSource
                    .GetKeyStateForCurrentThread(
                        VirtualKey.RightControl);

            return (left & down) == down ||
                   (right & down) == down;
        }

        private bool IsGlobalPasteEditableTarget()
        {
            DependencyObject? current = null;

            try
            {
                current =
                    FocusManager.GetFocusedElement(
                        XamlRoot) as DependencyObject;
            }
            catch
            {
            }

            while (current != null)
            {
                // No interceptar Ctrl+V normal en ningún editor conocido.
                // AutoSuggestBox cubre el buscador y su TextBox interno también
                // queda cubierto por la primera condición.
                if (current is TextBox ||
                    current is RichEditBox ||
                    current is PasswordBox ||
                    current is AutoSuggestBox ||
                    current is NumberBox)
                {
                    return true;
                }

                current =
                    VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private async Task HandleGlobalPasteBitmapAsync(
            DataPackageView clipboard)
        {
            StorageFile? tempFile = null;

            try
            {
                var reference =
                    await clipboard.GetBitmapAsync();

                tempFile =
                    await SaveClipboardBitmapAsTemporaryPngAsync(
                        reference);

                // Reutiliza exactamente el flujo de archivos que ya usa
                // Arrastrar/selector en SearchView.Actions. El override vacío
                // hace que el modal abra con TÍTULO VACÍO, como pide el bloque.
                await UploadFilesToNotionRevisionsAsync(
                    new[] { tempFile },
                    "Ctrl+V · imagen",
                    suggestedTitleOverride: string.Empty);
            }
            finally
            {
                if (tempFile != null)
                {
                    try
                    {
                        await tempFile.DeleteAsync(
                            StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                        // TemporaryFolder también limpia estos archivos; no se
                        // debe convertir una limpieza fallida en error de pegado.
                    }
                }
            }
        }

        private static async Task<StorageFile>
            SaveClipboardBitmapAsTemporaryPngAsync(
                RandomAccessStreamReference reference)
        {
            if (reference == null)
                throw new InvalidOperationException(
                    "Windows no devolvió la imagen del portapapeles.");

            using var source =
                await reference.OpenReadAsync();

            var decoder =
                await BitmapDecoder.CreateAsync(source);

            var pixels =
                await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

            var file =
                await ApplicationData.Current.TemporaryFolder
                    .CreateFileAsync(
                        $"ANFETA CtrlV {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png",
                        CreationCollisionOption.GenerateUniqueName);

            using var destination =
                await file.OpenAsync(
                    FileAccessMode.ReadWrite);

            var encoder =
                await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId,
                    destination);

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                decoder.PixelWidth,
                decoder.PixelHeight,
                decoder.DpiX,
                decoder.DpiY,
                pixels.DetachPixelData());

            await encoder.FlushAsync();
            await destination.FlushAsync();

            return file;
        }

        private async Task ShowGlobalPasteTextDialogAsync(
            string clipboardText)
        {
            var titleBox = new TextBox
            {
                Header = "Título de la nueva página en Notion",
                PlaceholderText =
                    "Ej: dominio.com sseo jjuli Descripción de la actividad…",
                Text = string.Empty,
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            var bodyBox = new TextBox
            {
                Header = "Contenido / BODY (Texto pegado del portapapeles)",
                Text = clipboardText ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 200,
                MaxHeight = 360,
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            ScrollViewer.SetVerticalScrollBarVisibility(
                bodyBox,
                ScrollBarVisibility.Auto);

            var guideCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 14, 116, 144)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 56, 189, 248)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9)
            };

            var guideStack = new StackPanel { Spacing = 3 };
            var guideTitleBlock = new TextBlock
            {
                Text = "💡 Convención recomendada de título:",
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 130, 215, 255))
            };
            guideStack.Children.Add(guideTitleBlock);
            var guideDescBlock = new TextBlock
            {
                Text = "[dominio.com] → [Tipo: sseo | aapli | aads | wwebs] → [Persona/Mes: jjuli | jjohn] → [Descripción]",
                FontSize = 10.5,
                Opacity = 0.88,
                TextWrapping = TextWrapping.Wrap
            };
            guideStack.Children.Add(guideDescBlock);
            guideCard.Child = guideStack;

            var variantNormalRadio = new RadioButton
            {
                Content = "Normal",
                GroupName = "GlobalPasteVariantGroup",
                IsChecked = true,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var variant00Radio = new RadioButton
            {
                Content = "00 (Urgente)",
                GroupName = "GlobalPasteVariantGroup",
                IsChecked = false,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 120, 120)),
                Margin = new Thickness(0, 0, 8, 0)
            };

            var variant001Radio = new RadioButton
            {
                Content = "001 (Importante)",
                GroupName = "GlobalPasteVariantGroup",
                IsChecked = false,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 215, 120)),
                Margin = new Thickness(0, 0, 8, 0)
            };

            var variant002Radio = new RadioButton
            {
                Content = "002 (Secundaria)",
                GroupName = "GlobalPasteVariantGroup",
                IsChecked = false,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 200, 255)),
                Margin = new Thickness(0, 0, 8, 0)
            };

            var variant003Radio = new RadioButton
            {
                Content = "003 (Recordar-usar)",
                GroupName = "GlobalPasteVariantGroup",
                IsChecked = false,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 192, 132, 252)),
                Margin = new Thickness(0, 0, 8, 0)
            };

            string GetActiveVariantSuffix()
            {
                if (variant00Radio.IsChecked == true) return "00";
                if (variant001Radio.IsChecked == true) return "001";
                if (variant002Radio.IsChecked == true) return "002";
                if (variant003Radio.IsChecked == true) return "003";
                return string.Empty;
            }

            string StripVariantSuffix(string tag)
            {
                var clean = (tag ?? string.Empty).Trim();
                if (clean.EndsWith("001", StringComparison.OrdinalIgnoreCase)) return clean[..^3];
                if (clean.EndsWith("002", StringComparison.OrdinalIgnoreCase)) return clean[..^3];
                if (clean.EndsWith("003", StringComparison.OrdinalIgnoreCase)) return clean[..^3];
                if (clean.EndsWith("00", StringComparison.OrdinalIgnoreCase)) return clean[..^2];
                return clean;
            }

            void AppendTagToTitle(string tag)
            {
                var cleanTag = (tag ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(cleanTag)) return;

                var activeVariant = GetActiveVariantSuffix();
                if (!string.IsNullOrEmpty(activeVariant) &&
                    !cleanTag.EndsWith("001", StringComparison.OrdinalIgnoreCase) &&
                    !cleanTag.EndsWith("002", StringComparison.OrdinalIgnoreCase) &&
                    !cleanTag.EndsWith("003", StringComparison.OrdinalIgnoreCase) &&
                    !cleanTag.EndsWith("00", StringComparison.OrdinalIgnoreCase))
                {
                    cleanTag += activeVariant;
                }

                var current = (titleBox.Text ?? string.Empty).Trim();
                var tokens = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (tokens.Any(x => string.Equals(x, cleanTag, StringComparison.OrdinalIgnoreCase)))
                    return;

                titleBox.Text = string.IsNullOrWhiteSpace(current) ? cleanTag : $"{cleanTag} {current}";
                titleBox.SelectionStart = titleBox.Text.Length;
            }

            void ApplyVariantToTitle(string targetVariant)
            {
                var text = (titleBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text)) return;

                var allTags = NotionUploadQuickTags.Concat(NotionUploadPersonTags).ToArray();
                var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                bool modified = false;

                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];
                    foreach (var baseTag in allTags)
                    {
                        var rawTokenBase = StripVariantSuffix(token);
                        if (string.Equals(rawTokenBase, baseTag, StringComparison.OrdinalIgnoreCase))
                        {
                            tokens[i] = string.IsNullOrEmpty(targetVariant) ? baseTag : baseTag + targetVariant;
                            modified = true;
                            break;
                        }
                    }
                }

                if (modified)
                {
                    titleBox.Text = string.Join(" ", tokens);
                    titleBox.SelectionStart = titleBox.Text.Length;
                }
            }

            void OnVariantSelectionChanged()
            {
                ApplyVariantToTitle(GetActiveVariantSuffix());
            }

            variantNormalRadio.Checked += (_, __) => OnVariantSelectionChanged();
            variant00Radio.Checked += (_, __) => OnVariantSelectionChanged();
            variant001Radio.Checked += (_, __) => OnVariantSelectionChanged();
            variant002Radio.Checked += (_, __) => OnVariantSelectionChanged();
            variant003Radio.Checked += (_, __) => OnVariantSelectionChanged();

            var tagsStack = new StackPanel { Spacing = 7 };
            var tagsHeaderBlock = new TextBlock
            {
                Text = "🏷️ Etiquetas y Estados (Tags):",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12
            };
            tagsStack.Children.Add(tagsHeaderBlock);

            // Selector de variantes (00 Urgente, 001 Importante, 002 Secundaria, 003 Recordar-usar)
            var variantsHeader = new TextBlock
            {
                Text = "Variante de prioridad / asignación:",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.85
            };
            tagsStack.Children.Add(variantsHeader);

            var variantsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 0, 0, 2)
            };
            variantsRow.Children.Add(variantNormalRadio);
            variantsRow.Children.Add(variant00Radio);
            variantsRow.Children.Add(variant001Radio);
            variantsRow.Children.Add(variant002Radio);
            variantsRow.Children.Add(variant003Radio);
            tagsStack.Children.Add(variantsRow);

            // Botón Asignar a Todos (002 Secundario)
            var assignAll002Button = new Button
            {
                Content = "👥 Asignar a Todos (002 Secundario)",
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 56, 189, 248)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 56, 189, 248)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ToolTipService.SetToolTip(assignAll002Button, "Inserta los tags 002 secundarios de todos los integrantes del equipo. Puedes borrar individualmente a quien no aplique.");

            assignAll002Button.Click += (_, __) =>
            {
                foreach (var personTag in NotionUploadPersonTags)
                {
                    AppendTagToTitle(personTag + "002");
                }
            };
            tagsStack.Children.Add(assignAll002Button);

            // Tags principales
            var mainTagsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            mainTagsRow.Children.Add(new TextBlock
            {
                Text = "Principales:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.8
            });
            foreach (var tag in NotionUploadQuickTags)
            {
                var btn = new Button
                {
                    Content = tag,
                    Padding = new Thickness(8, 3, 8, 3),
                    CornerRadius = new CornerRadius(5)
                };
                btn.Click += (_, __) => AppendTagToTitle(tag);
                mainTagsRow.Children.Add(btn);
            }
            tagsStack.Children.Add(mainTagsRow);

            // Personas
            var personCombo = new ComboBox
            {
                PlaceholderText = "TAGS de persona (ej. jjohn, nneft...)",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var tag in NotionUploadPersonTags)
            {
                personCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{GetNotionPersonDisplayName(tag)} ({tag})",
                    Tag = tag
                });
            }
            personCombo.SelectionChanged += (_, __) =>
            {
                if (personCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                {
                    AppendTagToTitle(tag);
                    personCombo.SelectedItem = null;
                }
            };
            tagsStack.Children.Add(personCombo);

            var activePeoplePanel = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 3,
                ItemWidth = 100,
                ItemHeight = 36
            };
            foreach (var pTag in NotionUploadPersonTags)
            {
                var btn = new Button
                {
                    Content = pTag,
                    Padding = new Thickness(8, 3, 8, 3),
                    CornerRadius = new CornerRadius(5)
                };
                ToolTipService.SetToolTip(btn, $"{GetNotionPersonDisplayName(pTag)} ({pTag})");
                btn.Click += (_, __) => AppendTagToTitle(pTag);
                activePeoplePanel.Children.Add(btn);
            }
            tagsStack.Children.Add(activePeoplePanel);

            var tagsCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 20, 29)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 20, 75, 115)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14)
            };
            tagsCard.Child = tagsStack;

            double desiredGlobalPasteWidth = Math.Clamp(
                (XamlRoot?.Size.Width ?? 1200) * 0.85,
                900d,
                1060d);

            // Selector de destino: Notion vs Dropbox
            var destNotionRadio = new RadioButton
            {
                Content = "🌐 Notion · Revisiones",
                GroupName = "GlobalPasteDestGroup",
                IsChecked = true,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 16, 0)
            };

            var destDropboxRadio = new RadioButton
            {
                Content = "📦 Dropbox · DRX/{dominio}.Carpeta",
                GroupName = "GlobalPasteDestGroup",
                IsChecked = false,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };

            var destHintText = new TextBlock
            {
                Text = "Destino actual: Se creará una nueva actividad en Notion (Revisiones).",
                FontSize = 11,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap
            };

            var destCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 14, 116, 144)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 56, 189, 248)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9)
            };

            var destStack = new StackPanel { Spacing = 6 };
            destStack.Children.Add(new TextBlock
            {
                Text = "🎯 Destino del pegado:",
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 130, 215, 255))
            });

            var dropboxDisclaimerCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 245, 158, 11)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 245, 158, 11)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Visibility = Visibility.Collapsed
            };

            var disclaimerStack = new StackPanel { Spacing = 3 };
            disclaimerStack.Children.Add(new TextBlock
            {
                Text = "📁 Aviso de Dropbox · Carpeta DRX:",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 253, 224, 71))
            });
            disclaimerStack.Children.Add(new TextBlock
            {
                Text = "ANFETA guardará el archivo (.url o .txt) dentro de 'DRX/{dominio}.Carpeta' en tu Dropbox local. Si la carpeta ya existe la reutilizará, y si no existe la creará automáticamente para sincronizarse en la nube e indexarse al instante.",
                FontSize = 10.5,
                Opacity = 0.9,
                TextWrapping = TextWrapping.Wrap
            });
            dropboxDisclaimerCard.Child = disclaimerStack;

            var destRadios = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            destRadios.Children.Add(destNotionRadio);
            destRadios.Children.Add(destDropboxRadio);
            destStack.Children.Add(destRadios);
            destStack.Children.Add(destHintText);
            destStack.Children.Add(dropboxDisclaimerCard);
            destCard.Child = destStack;

            var content = new StackPanel
            {
                Width = desiredGlobalPasteWidth - 40,
                Spacing = 12
            };

            content.Children.Add(destCard);
            content.Children.Add(guideCard);
            content.Children.Add(titleBox);
            content.Children.Add(tagsCard);
            content.Children.Add(bodyBox);

            var contentScrollViewer = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Clamp((XamlRoot?.Size.Height ?? 900) * 0.78, 480, 720)
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Content = contentScrollViewer,
                PrimaryButtonText = "Crear actividad",
                CloseButtonText = "Cancelar",
                DefaultButton =
                    ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                HorizontalContentAlignment =
                    HorizontalAlignment.Stretch
            };

            dialog.Resources[
                "ContentDialogMaxWidth"] = desiredGlobalPasteWidth;

            dialog.Resources[
                "ContentDialogMinWidth"] = Math.Min(desiredGlobalPasteWidth, 880d);

            dialog.Resources["ContentDialogBackground"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 9, 16, 23));
            dialog.Resources["ContentDialogBorderBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0, 168, 255));
            dialog.Resources["ContentDialogBorderThickness"] =
                new Thickness(1.5);
            dialog.Resources["ContentDialogCornerRadius"] =
                new CornerRadius(14);
            dialog.Resources["ContentDialogForeground"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 245, 255));
            dialog.Resources["AccentButtonBackground"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 140, 230));
            dialog.Resources["AccentButtonBackgroundPointerOver"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 168, 255));
            dialog.Resources["AccentButtonForeground"] =
                new SolidColorBrush(Microsoft.UI.Colors.White);
            dialog.Resources["TextControlBackground"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 14, 25, 36));
            dialog.Resources["TextControlBackgroundPointerOver"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 32, 46));
            dialog.Resources["TextControlBackgroundFocused"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 22, 32));
            dialog.Resources["TextControlBorderBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 168, 255));
            dialog.Resources["TextControlBorderBrushFocused"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 168, 255));
            dialog.Resources["ComboBoxBackground"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 14, 25, 36));
            dialog.Resources["ComboBoxBackgroundPointerOver"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 32, 46));
            dialog.Resources["ComboBoxBorderBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 168, 255));

            MakeCalendarContentDialogMovable(
                dialog,
                "📋 Pegar texto en Notion · Revisiones");

            var dialogTitleBlock = (dialog.Title as Border)?.Child as TextBlock;

            void UpdateDestUi()
            {
                var isDropbox = destDropboxRadio.IsChecked == true;
                var currentTitle = (titleBox.Text ?? string.Empty).Trim();
                var domain = ExtractClientDomainFromPaste(currentTitle, clipboardText);
                var trimmed = (clipboardText ?? string.Empty).Trim();
                var isUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var uriRes) &&
                            (uriRes.Scheme == Uri.UriSchemeHttp || uriRes.Scheme == Uri.UriSchemeHttps);

                if (isDropbox)
                {
                    dropboxDisclaimerCard.Visibility = Visibility.Visible;
                    destHintText.Text = $"📦 Se guardará en Dropbox: DRX/{domain}.Carpeta/ como {(isUrl ? "acceso directo (.url)" : "archivo de texto (.txt)")}";
                    dialog.PrimaryButtonText = isUrl ? "Guardar enlace .url en Dropbox" : "Guardar archivo .txt en Dropbox";
                    titleBox.Header = isUrl ? "Nombre del enlace en Dropbox (.url):" : "Nombre del archivo de texto en Dropbox (.txt):";
                    titleBox.PlaceholderText = "Ej: notas-reunion o [dominio.com] [tipo] [persona] [descripcion]...";
                    bodyBox.Header = isUrl ? "URL del acceso directo (.url):" : "Contenido del archivo .txt (Texto pegado):";
                    guideTitleBlock.Text = "💡 Convención recomendada para archivo en Dropbox:";
                    guideDescBlock.Text = "[dominio.com] → [Tipo opcional] → [Persona/Mes opcional] → [Nombre descriptivo del archivo]";
                    tagsHeaderBlock.Text = "🏷️ Etiquetas y Sufijos para nombre del archivo (opcional):";

                    if (dialogTitleBlock != null)
                    {
                        dialogTitleBlock.Text = isUrl ? $"🔗 Guardar enlace en Dropbox · DRX/{domain}.Carpeta" : $"📄 Guardar texto en Dropbox · DRX/{domain}.Carpeta";
                    }
                }
                else
                {
                    dropboxDisclaimerCard.Visibility = Visibility.Collapsed;
                    destHintText.Text = "🌐 Se creará una página de actividad en Notion · Revisiones.";
                    dialog.PrimaryButtonText = "Crear actividad";
                    titleBox.Header = "Título de la nueva página en Notion:";
                    titleBox.PlaceholderText = "Ej: dominio.com sseo jjuli Descripción de la actividad…";
                    bodyBox.Header = "Contenido / BODY (Texto de la nueva página en Notion):";
                    guideTitleBlock.Text = "💡 Convención recomendada de título:";
                    guideDescBlock.Text = "[dominio.com] → [Tipo: sseo | aapli | aads | wwebs] → [Persona/Mes: jjuli | jjohn] → [Descripción]";
                    tagsHeaderBlock.Text = "🏷️ Etiquetas y Estados (Tags para Notion):";

                    if (dialogTitleBlock != null)
                    {
                        dialogTitleBlock.Text = "📋 Pegar texto en Notion · Revisiones";
                    }
                }
            }

            destNotionRadio.Checked += (_, __) => UpdateDestUi();
            destDropboxRadio.Checked += (_, __) => UpdateDestUi();
            UpdateDestUi();

            void RefreshCreateState()
            {
                dialog.IsPrimaryButtonEnabled =
                    !string.IsNullOrWhiteSpace(titleBox.Text) &&
                    !string.IsNullOrWhiteSpace(bodyBox.Text);
                UpdateDestUi();
            }

            titleBox.TextChanged +=
                (_, __) => RefreshCreateState();

            bodyBox.TextChanged +=
                (_, __) => RefreshCreateState();

            dialog.Opened += (_, __) =>
            {
                titleBox.Focus(FocusState.Programmatic);
                RefreshCreateState();
            };

            if (await dialog.ShowAsync() !=
                ContentDialogResult.Primary)
            {
                return;
            }

            var title =
                (titleBox.Text ?? string.Empty).Trim();

            var body =
                bodyBox.Text ?? string.Empty;

            if (destDropboxRadio.IsChecked == true)
            {
                await SaveGlobalPasteToDropboxAsync(title, body);
                return;
            }

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura y guarda primero el token de Notion en Configuración.";
                return;
            }

            // title y body ya definidos para Notion

            try
            {
                ShowLoadingState(
                    "Estado: Creando actividad desde Ctrl+V…",
                    "Guardando el texto pegado dentro del BODY de Notion.");

                using var cts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(3));

                var service =
                    new NotionFilePageService();

                var created =
                    await service.CreateRevisionFromTextAsync(
                        token,
                        title,
                        body,
                        cts.Token);

                await AddCreatedNotionPageToIndexAsync(
                    created.PageId,
                    created.PageUrl,
                    created.Title);

                var matchedPerson = NotionUploadPersonTags.FirstOrDefault(p =>
                    created.Title.Contains(p, StringComparison.OrdinalIgnoreCase));
                string detectedVariant = "";
                if (created.Title.Contains("001", StringComparison.OrdinalIgnoreCase)) detectedVariant = "001";
                else if (created.Title.Contains("002", StringComparison.OrdinalIgnoreCase)) detectedVariant = "002";
                else if (created.Title.Contains("003", StringComparison.OrdinalIgnoreCase)) detectedVariant = "003";
                else if (created.Title.Contains("00", StringComparison.OrdinalIgnoreCase)) detectedVariant = "00";
                ShowDiscreteActivityToast(created.Title, matchedPerson ?? "", detectedVariant, created.PageUrl);

                StatusText.Text =
                    $"Estado: Actividad creada desde Ctrl+V ✅ ({created.Title})";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text =
                    "Estado: Notion tardó demasiado al crear la actividad desde Ctrl+V.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    $"Estado: No se pudo crear la actividad desde Ctrl+V → {ex.Message}";
            }
            finally
            {
                HideLoadingState();
            }
        }

        private static readonly Regex GlobalPasteDomainRegex = new(
            @"(?:https?://)?(?:www\.)?([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.(?:com\.mx|org\.mx|gob\.mx|edu\.mx|net\.mx|com|mx|org|net|io|co|app|dev))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static string ExtractClientDomainFromPaste(string title, string body)
        {
            var combined = $"{(title ?? string.Empty)} {(body ?? string.Empty)}";
            var match = GlobalPasteDomainRegex.Match(combined);
            if (match.Success)
            {
                var candidate = match.Groups[1].Value.ToLowerInvariant().Trim();
                if (!candidate.Contains("notion.so") && !candidate.Contains("google.com") && !candidate.Contains("notion.site"))
                {
                    return candidate;
                }
            }

            if (combined.Contains(".apli", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains(".pro", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("pprog", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("anfeta", StringComparison.OrdinalIgnoreCase))
            {
                return "anfeta.com";
            }

            return match.Success ? match.Groups[1].Value.ToLowerInvariant().Trim() : "anfeta.com";
        }

        private async Task SaveGlobalPasteToDropboxAsync(string title, string body)
        {
            var dropboxRoot = DROPBOX_ROOT;
            if (string.IsNullOrWhiteSpace(dropboxRoot) || !Directory.Exists(dropboxRoot))
            {
                dropboxRoot = ApplicationData.Current.LocalSettings.Values[Anfeta.UI.Helpers.AppSettingsKeys.LS_DropboxRoot] as string;
            }

            if (string.IsNullOrWhiteSpace(dropboxRoot) || !Directory.Exists(dropboxRoot))
            {
                StatusText.Text = "Estado: No se encontró la ruta de Dropbox configurada en Ajustes.";
                return;
            }

            var domain = ExtractClientDomainFromPaste(title, body);

            // 1. Resolver la carpeta DRX dentro de la raíz de Dropbox
            string drxRoot;
            if (string.Equals(Path.GetFileName(dropboxRoot.TrimEnd('\\', '/')), "DRX", StringComparison.OrdinalIgnoreCase))
            {
                drxRoot = dropboxRoot;
            }
            else
            {
                drxRoot = Path.Combine(dropboxRoot, "DRX");
            }

            try
            {
                if (!Directory.Exists(drxRoot))
                {
                    Directory.CreateDirectory(drxRoot);
                }

                // 2. Si ya existe una carpeta con ese dominio (ej: {dominio}.Carpeta), reutilizarla; si no, crearla
                var expectedFolderName = $"{domain}.Carpeta";
                var domainPrefix = domain.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
                    ? domain.Substring(0, domain.Length - 4)
                    : domain;

                string targetFolder;
                var existingDir = Directory.EnumerateDirectories(drxRoot)
                    .FirstOrDefault(d =>
                    {
                        var dirName = Path.GetFileName(d);
                        if (string.Equals(dirName, expectedFolderName, StringComparison.OrdinalIgnoreCase))
                            return true;

                        if (dirName.EndsWith(".Carpeta", StringComparison.OrdinalIgnoreCase))
                        {
                            var cleanDirBase = dirName.Substring(0, dirName.Length - ".Carpeta".Length);
                            if (string.Equals(cleanDirBase, domain, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(cleanDirBase, domainPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                        return false;
                    });

                if (!string.IsNullOrEmpty(existingDir))
                {
                    targetFolder = existingDir;
                }
                else
                {
                    targetFolder = Path.Combine(drxRoot, expectedFolderName);
                    Directory.CreateDirectory(targetFolder);
                }

                var trimmedBody = (body ?? string.Empty).Trim();
                var isUrl = Uri.TryCreate(trimmedBody, UriKind.Absolute, out var uriResult) &&
                            (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

                var rawTitle = string.IsNullOrWhiteSpace(title)
                    ? (isUrl ? uriResult!.Host : domain)
                    : title.Trim();

                var invalidChars = Path.GetInvalidFileNameChars();
                var cleanTitle = string.Join("_", rawTitle.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries)).Trim();
                if (string.IsNullOrWhiteSpace(cleanTitle))
                {
                    cleanTitle = $"{domain}_{DateTime.Now:yyyyMMdd_HHmmss}";
                }

                string filePath;
                if (isUrl)
                {
                    if (!cleanTitle.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                        cleanTitle += ".url";

                    filePath = Path.Combine(targetFolder, cleanTitle);
                    var shortcutContent = $"[InternetShortcut]\r\nURL={trimmedBody}\r\n";
                    await File.WriteAllTextAsync(filePath, shortcutContent, System.Text.Encoding.UTF8);
                }
                else
                {
                    if (!cleanTitle.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        cleanTitle += ".txt";

                    filePath = Path.Combine(targetFolder, cleanTitle);
                    await File.WriteAllTextAsync(filePath, body ?? string.Empty, System.Text.Encoding.UTF8);
                }

                var fileInfo = new FileInfo(filePath);
                await AddUploadedFileToIndexAsync(filePath, fileInfo.Name, fileInfo.Length, DateTime.UtcNow);

                var matchedPerson = NotionUploadPersonTags.FirstOrDefault(p =>
                    title.Contains(p, StringComparison.OrdinalIgnoreCase));
                string detectedVariant = "";
                if (title.Contains("001", StringComparison.OrdinalIgnoreCase)) detectedVariant = "001";
                else if (title.Contains("002", StringComparison.OrdinalIgnoreCase)) detectedVariant = "002";
                else if (title.Contains("003", StringComparison.OrdinalIgnoreCase)) detectedVariant = "003";
                else if (title.Contains("00", StringComparison.OrdinalIgnoreCase)) detectedVariant = "00";

                ShowDiscreteActivityToast(cleanTitle, matchedPerson ?? "", detectedVariant, filePath);

                var savedFolderName = Path.GetFileName(targetFolder);
                StatusText.Text = $"Estado: Guardado en Dropbox ✅ ({cleanTitle} → DRX/{savedFolderName})";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Estado: No se pudo guardar en Dropbox → {ex.Message}";
            }
        }
    }
}
