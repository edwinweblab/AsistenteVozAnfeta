using Anfeta.UI.Services.Notion;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
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
                Header = "Título",
                PlaceholderText =
                    "Escribe el título de la nueva actividad…",
                Text = string.Empty,
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            var bodyBox = new TextBox
            {
                Header = "Descripción / BODY",
                Text = clipboardText ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 240,
                MaxHeight = 430,
                HorizontalAlignment =
                    HorizontalAlignment.Stretch
            };

            ScrollViewer.SetVerticalScrollBarVisibility(
                bodyBox,
                ScrollBarVisibility.Auto);

            var info = new TextBlock
            {
                Text =
                    "Ctrl+V detectó texto. El contenido se guardará dentro del BODY de la página; " +
                    "el título inicia vacío para que escribas únicamente lo que corresponda.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78,
                FontSize = 11
            };

            var content = new StackPanel
            {
                Width = 680,
                Spacing = 10
            };

            content.Children.Add(info);
            content.Children.Add(titleBox);
            content.Children.Add(bodyBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Pegar texto en Notion → Revisiones",
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
