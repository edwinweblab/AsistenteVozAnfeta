using Anfeta.UI.Services.Notion;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Linq;
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
            guideStack.Children.Add(new TextBlock
            {
                Text = "💡 Convención recomendada de título:",
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 130, 215, 255))
            });
            guideStack.Children.Add(new TextBlock
            {
                Text = "[dominio.com] → [Tipo: sseo | aapli | aads | wwebs] → [Persona/Mes: jjuli | jjohn] → [Descripción]",
                FontSize = 10.5,
                Opacity = 0.88,
                TextWrapping = TextWrapping.Wrap
            });
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

            string GetActiveVariantSuffix()
            {
                if (variant00Radio.IsChecked == true) return "00";
                if (variant001Radio.IsChecked == true) return "001";
                if (variant002Radio.IsChecked == true) return "002";
                return string.Empty;
            }

            string StripVariantSuffix(string tag)
            {
                var clean = (tag ?? string.Empty).Trim();
                if (clean.EndsWith("001", StringComparison.OrdinalIgnoreCase)) return clean[..^3];
                if (clean.EndsWith("002", StringComparison.OrdinalIgnoreCase)) return clean[..^3];
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

            var tagsStack = new StackPanel { Spacing = 7 };
            tagsStack.Children.Add(new TextBlock
            {
                Text = "🏷️ Etiquetas y Estados (Tags):",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12
            });

            // Selector de variantes (00 Urgente, 001 Importante, 002 Secundaria)
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

            var tagsCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 20, 29)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 20, 75, 115)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14)
            };
            tagsCard.Child = tagsStack;

            var content = new StackPanel
            {
                Width = 680,
                Spacing = 12
            };

            content.Children.Add(guideCard);
            content.Children.Add(titleBox);
            content.Children.Add(tagsCard);
            content.Children.Add(bodyBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Content = content,
                PrimaryButtonText = "Crear actividad",
                CloseButtonText = "Cancelar",
                DefaultButton =
                    ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                HorizontalContentAlignment =
                    HorizontalAlignment.Stretch
            };

            dialog.Resources[
                "ContentDialogMaxWidth"] = 760d;

            dialog.Resources[
                "ContentDialogMinWidth"] = 760d;

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

            void RefreshCreateState()
            {
                dialog.IsPrimaryButtonEnabled =
                    !string.IsNullOrWhiteSpace(titleBox.Text) &&
                    !string.IsNullOrWhiteSpace(bodyBox.Text);
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

            var token =
                ApplicationData.Current.LocalSettings.Values[
                    "Notion.Token"] as string;

            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText.Text =
                    "Estado: Configura y guarda primero el token de Notion en Configuración.";
                return;
            }

            var title =
                (titleBox.Text ?? string.Empty).Trim();

            var body =
                bodyBox.Text ?? string.Empty;

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
    }
}
