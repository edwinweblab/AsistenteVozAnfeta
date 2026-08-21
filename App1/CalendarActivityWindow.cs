using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using Windows.UI;
using WinRT.Interop;

namespace Anfeta.UI
{
    /// <summary>
    /// Ventana real de Windows para una actividad fijada del Calendario.
    ///
    /// A diferencia del Popup interno de SearchView:
    /// - puede salir físicamente de ANFETA;
    /// - puede arrastrarse a otro monitor;
    /// - puede redimensionarse/maximizarse;
    /// - cerrarla NO cierra ANFETA.
    ///
    /// El contenido sigue siendo construido por el SearchView original,
    /// por lo que conserva sus handlers y lógica existente.
    /// </summary>
    public sealed class CalendarActivityWindow : Window
    {
        private readonly ContentControl _contentHost;
        private readonly ScrollViewer _scrollViewer;

        public CalendarActivityWindow(
            FrameworkElement content,
            string? title = null)
        {
            Title =
                string.IsNullOrWhiteSpace(title)
                    ? "ANFETA · Actividad"
                    : title;

            _contentHost =
                new ContentControl
                {
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    HorizontalContentAlignment =
                        HorizontalAlignment.Stretch,
                    VerticalAlignment =
                        VerticalAlignment.Top,
                    VerticalContentAlignment =
                        VerticalAlignment.Top
                };

            _scrollViewer =
                new ScrollViewer
                {
                    Content =
                        _contentHost,
                    HorizontalScrollMode =
                        ScrollMode.Disabled,
                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled,
                    VerticalScrollMode =
                        ScrollMode.Auto,
                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,
                    HorizontalContentAlignment =
                        HorizontalAlignment.Stretch
                };

            var root =
                new Border
                {
                    Padding =
                        new Thickness(10),
                    Background =
                        new SolidColorBrush(
                            Color.FromArgb(
                                255,
                                18,
                                24,
                                30)),
                    BorderBrush =
                        new SolidColorBrush(
                            Color.FromArgb(
                                110,
                                56,
                                189,
                                248)),
                    BorderThickness =
                        new Thickness(1),
                    Child =
                        _scrollViewer
                };

            Content =
                root;

            SetContent(
                content,
                title);

            TryApplyInitialWindowSize();
        }

        public void SetContent(
            FrameworkElement content,
            string? title = null)
        {
            if (content == null)
                return;

            if (!string.IsNullOrWhiteSpace(title))
            {
                Title =
                    title;
            }

            _contentHost.Content =
                content;

            _scrollViewer.ChangeView(
                horizontalOffset: null,
                verticalOffset: 0,
                zoomFactor: null,
                disableAnimation: true);
        }

        public void BringToFront()
        {
            try
            {
                Activate();

                var hwnd =
                    WindowNative.GetWindowHandle(
                        this);

                if (hwnd == IntPtr.Zero)
                    return;

                var windowId =
                    Win32Interop.GetWindowIdFromWindow(
                        hwnd);

                var appWindow =
                    AppWindow.GetFromWindowId(
                        windowId);

                appWindow?.Show(true);
            }
            catch
            {
                Activate();
            }
        }

        private void TryApplyInitialWindowSize()
        {
            try
            {
                var hwnd =
                    WindowNative.GetWindowHandle(
                        this);

                if (hwnd == IntPtr.Zero)
                    return;

                var windowId =
                    Win32Interop.GetWindowIdFromWindow(
                        hwnd);

                var appWindow =
                    AppWindow.GetFromWindowId(
                        windowId);

                appWindow?.Resize(
                    new Windows.Graphics.SizeInt32(
                        900,
                        900));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[CALENDAR_ACTIVITY_WINDOW_SIZE] {ex.Message}");
            }
        }
    }
}
