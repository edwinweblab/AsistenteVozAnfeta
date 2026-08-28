using Microsoft.UI.Xaml;
using System;

namespace Anfeta.UI
{
    public partial class App
    {
        private CalendarWindow? _calendarWindow;
        private CalendarActivityWindow? _calendarActivityWindow;
        private Action? _calendarActivityWindowClosedCallback;


        public bool IsCalendarActivityWindowOpen =>
            _calendarActivityWindow != null;

        /// <summary>
        /// Activa la actividad fijada existente sin crear otra ventana.
        /// Devuelve true cuando había una ventana que debía conservar el foco.
        /// </summary>
        public bool TryBringCalendarActivityWindowToFront()
        {
            var window = _calendarActivityWindow;

            if (window == null)
                return false;

            try
            {
                window.BringToFront();
            }
            catch
            {
                // La referencia sigue siendo suficiente para bloquear la
                // apertura de otro modal mientras termina el cierre real.
            }

            return true;
        }


        /// <summary>
        /// Sincroniza el filtro SOLO si la ventana secundaria ya existe.
        /// Es intencionalmente distinto de OpenCalendarWindow: escribir en
        /// el Buscador nunca debe abrir otra ventana por sí solo.
        /// </summary>
        public void SyncOpenCalendarWindowFilter(
            string? filter)
        {
            if (_calendarWindow == null)
                return;

            _ = _calendarWindow
                .CalendarView
                .ApplyStandaloneCalendarFilterAsync(
                    filter);
        }

        /// <summary>
        /// Abre/reutiliza la ventana independiente del Calendario.
        /// La referencia se conserva para que el GC no destruya la Window.
        /// </summary>
        public void OpenCalendarWindow(
            string? initialFilter = null)
        {
            if (_calendarWindow != null)
            {
                _calendarWindow.BringToFront();

                _ = _calendarWindow
                    .CalendarView
                    .ApplyStandaloneCalendarFilterAsync(
                        initialFilter);

                return;
            }

            var window =
                new CalendarWindow(
                    initialFilter);

            _calendarWindow =
                window;

            // App ya posee esta colección para mantener vivas ventanas
            // adicionales. La reutilizamos en vez de crear otro registro.
            _openWindows.Add(
                window);

            window.Closed +=
                (_, __) =>
                {
                    _openWindows.Remove(
                        window);

                    if (ReferenceEquals(
                            _calendarWindow,
                            window))
                    {
                        _calendarWindow =
                            null;
                    }
                };

            window.Activate();
        }
        /// <summary>
        /// Abre/reutiliza la ventana REAL de una actividad fijada.
        /// Esta Window puede salir de los límites de ANFETA y moverse
        /// libremente entre monitores.
        /// </summary>
        public void OpenCalendarActivityWindow(
            FrameworkElement content,
            string? title = null,
            Action? onClosed = null,
            Windows.UI.Color? themeColor = null)
        {
            if (content == null)
                return;

            _calendarActivityWindowClosedCallback =
                onClosed;

            if (_calendarActivityWindow != null)
            {
                _calendarActivityWindow.SetContent(
                    content,
                    title);

                if (themeColor.HasValue)
                    _calendarActivityWindow.ApplyTheme(themeColor.Value);

                _calendarActivityWindow.BringToFront();
                return;
            }

            var window =
                new CalendarActivityWindow(
                    content,
                    title,
                    themeColor);

            _calendarActivityWindow =
                window;

            _openWindows.Add(
                window);

            window.Closed +=
                (_, __) =>
                {
                    _openWindows.Remove(
                        window);

                    if (ReferenceEquals(
                            _calendarActivityWindow,
                            window))
                    {
                        _calendarActivityWindow =
                            null;
                    }

                    var callback =
                        _calendarActivityWindowClosedCallback;

                    _calendarActivityWindowClosedCallback =
                        null;

                    callback?.Invoke();
                };

            window.Activate();
        }

        public void UpdateCalendarActivityWindowContent(
            FrameworkElement content,
            string? title = null)
        {
            if (_calendarActivityWindow == null ||
                content == null)
            {
                return;
            }

            _calendarActivityWindow.SetContent(
                content,
                title);
        }

        public void UpdateCalendarActivityWindowTheme(
            Windows.UI.Color color)
        {
            _calendarActivityWindow?.ApplyTheme(color);
        }

        public void CloseCalendarActivityWindow()
        {
            if (_calendarActivityWindow == null)
                return;

            try
            {
                _calendarActivityWindow.Close();
            }
            catch
            {
            }
        }

    }
}
