using Anfeta.UI.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using WinRT.Interop;

namespace Anfeta.UI
{
    /// <summary>
    /// Ventana secundaria real para el Calendario.
    /// Se puede redimensionar, maximizar y mover libremente a otro monitor.
    /// Cerrar esta ventana NO cierra la ventana principal de ANFETA.
    /// </summary>
    public sealed class CalendarWindow : Window
    {
        private readonly SearchView _calendarView;
        private bool _initialized;

        public CalendarWindow(
            string? initialFilter = null)
        {
            Title =
                "ANFETA · Calendario";

            _calendarView =
                new SearchView
                {
                    DeferInitialIndexPaint = true
                };

            var root =
                new Grid();

            root.Children.Add(
                _calendarView);

            Content =
                root;

            TryApplyInitialWindowSize();

            _calendarView.Loaded +=
                async (_, __) =>
                {
                    if (_initialized)
                        return;

                    _initialized = true;

                    try
                    {
                        await _calendarView
                            .OpenAsStandaloneCalendarAsync(
                                DateTime.Today,
                                initialFilter);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[CALENDAR_WINDOW_INIT] {ex}");

                        _initialized = false;
                    }
                };
        }

        public SearchView CalendarView =>
            _calendarView;

        public void BringToFront()
        {
            try
            {
                Activate();

                var hwnd =
                    WindowNative.GetWindowHandle(this);

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
                    WindowNative.GetWindowHandle(this);

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
                        1500,
                        900));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[CALENDAR_WINDOW_SIZE] {ex.Message}");
            }
        }
    }
}
