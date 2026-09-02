using Microsoft.UI.Xaml;
using System;

namespace Anfeta.UI
{
    public partial class App
    {
        private CalendarWindow? _calendarWindow;
        private WeakReference<Views.SearchView>? _calendarSearchOwner;

        public bool IsCalendarSearchOwner(Views.SearchView view) =>
            _calendarSearchOwner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, view);

        public void RegisterCalendarSearchOwner(Views.SearchView view)
        {
            if (_calendarSearchOwner?.TryGetTarget(out _) == true) return;
            _calendarSearchOwner = new WeakReference<Views.SearchView>(view);
            view.SetCalendarSearchOwner(true);
        }

        public void ReleaseCalendarSearchOwner(Views.SearchView view)
        {
            if (!IsCalendarSearchOwner(view)) return;
            _calendarSearchOwner = null;
            view.SetCalendarSearchOwner(false);
            // Calendar stays open with its last filter. Another view must link
            // explicitly via the calendar-window button, not steal ownership.
        }
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
            string? filter, Views.SearchView sender)
        {
            if (_calendarWindow == null || !IsCalendarSearchOwner(sender))
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
            string? initialFilter = null, Views.SearchView? sender = null)
        {
            if (sender != null) RegisterCalendarSearchOwner(sender);
            var mayFilter = sender != null && IsCalendarSearchOwner(sender);
            if (!mayFilter && _calendarSearchOwner?.TryGetTarget(out var owner) == true)
                initialFilter = owner.GetTabState().Query;
            if (_calendarWindow != null)
            {
                _calendarWindow.BringToFront();

                if (mayFilter) _ = _calendarWindow
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
