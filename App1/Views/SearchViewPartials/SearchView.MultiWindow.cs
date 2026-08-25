using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        // Evita que una ventana secundaria intente inicializar dos veces
        // el mismo SearchView si WinUI dispara Loaded más de una vez.
        private bool _standaloneCalendarInitialized;

        // Debounce exclusivo de sincronización multi-monitor.
        // Evita repintar el Calendario secundario por cada tecla.
        private DispatcherTimer? _calendarWindowFilterSyncTimer;
        private string _pendingCalendarWindowFilter =
            string.Empty;

        /// <summary>
        /// Programa la sincronización del texto del Buscador hacia una ventana
        /// de Calendario YA abierta. No abre ventanas automáticamente.
        /// </summary>
        private void QueueCalendarWindowFilterSync(
            string? filter)
        {
            _pendingCalendarWindowFilter =
                (filter ?? string.Empty)
                    .Trim();

            if (_calendarWindowFilterSyncTimer == null)
            {
                _calendarWindowFilterSyncTimer =
                    new DispatcherTimer
                    {
                        Interval =
                            TimeSpan.FromMilliseconds(300)
                    };

                _calendarWindowFilterSyncTimer.Tick +=
                    (_, __) =>
                    {
                        _calendarWindowFilterSyncTimer.Stop();

                        if (Application.Current is App app)
                        {
                            app.SyncOpenCalendarWindowFilter(
                                _pendingCalendarWindowFilter);
                        }
                    };
            }

            _calendarWindowFilterSyncTimer.Stop();
            _calendarWindowFilterSyncTimer.Start();
        }

        /// <summary>
        /// Inicializa este SearchView como Calendario independiente.
        /// Reutiliza exactamente el mismo calendario, caché, checklist,
        /// filtros y acciones de la vista normal.
        /// </summary>
        public async Task OpenAsStandaloneCalendarAsync(
            DateTime? date = null,
            string? calendarFilter = null)
        {
            if (_standaloneCalendarInitialized)
            {
                if (!string.IsNullOrWhiteSpace(calendarFilter))
                {
                    ApplyCalendarSearchFilter(
                        calendarFilter);
                }

                return;
            }

            _standaloneCalendarInitialized = true;

            try
            {
                // CalendarWindow crea SearchView en modo diferido para que
                // primero aparezca la ventana y luego se materialice la UI.
                if (DeferInitialIndexPaint)
                {
                    await ActivateDeferredTabAsync();
                }
                else
                {
                    await EnsureSearchViewRuntimeInitializedAsync();

                    if (!_bootstrappedOnce)
                    {
                        await EnsureIndexBootstrappedAsync();
                    }
                }

                await ShowCalendarAsync(
                    (date ?? DateTime.Today).Date);

                if (!string.IsNullOrWhiteSpace(calendarFilter))
                {
                    ApplyCalendarSearchFilter(
                        calendarFilter);
                }

                StatusText.Text =
                    string.IsNullOrWhiteSpace(calendarFilter)
                        ? "Estado: Calendario independiente listo ✅"
                        : $"Estado: Calendario independiente · filtro “{calendarFilter}” ✅";
            }
            catch
            {
                // Permite reintentar si la primera inicialización falla.
                _standaloneCalendarInitialized = false;
                throw;
            }
        }

        /// <summary>
        /// Actualiza el filtro de una ventana de Calendario que ya está abierta.
        /// No vuelve a cargar Notion ni reconstruye el índice; aplica únicamente
        /// el filtro visual del calendario ya cargado.
        /// </summary>
        public async Task ApplyStandaloneCalendarFilterAsync(
            string? calendarFilter)
        {
            var filter =
                (calendarFilter ?? string.Empty)
                    .Trim();

            if (!_standaloneCalendarInitialized)
            {
                await OpenAsStandaloneCalendarAsync(
                    DateTime.Today,
                    filter);

                return;
            }

            ApplyCalendarSearchFilter(
                filter);

            StatusText.Text =
                string.IsNullOrWhiteSpace(filter)
                    ? "Estado: Calendario independiente · sin filtro ✅"
                    : $"Estado: Calendario independiente · filtro “{filter}” ✅";
        }

        /// <summary>
        /// Detiene el trabajo exclusivo de una ventana secundaria antes de
        /// liberar su árbol visual. Evita consultas, repintados y debounces
        /// sobrevivientes después de cerrar el monitor adicional.
        /// </summary>
        public void ShutdownStandaloneCalendar()
        {
            _calendarWindowFilterSyncTimer?.Stop();
            _calendarWindowFilterSyncTimer = null;

            if (_calendarViewActive)
                CloseCalendarView();

            SuspendAsBackgroundTab();

            try
            {
                _calendarCts?.Cancel();
                _calendarChecklistHydrationCts?.Cancel();
                _calendarIncrementalChecklistCts?.Cancel();
                _calendarReviewFlowBackgroundCts?.Cancel();
                _calendarProjectWarmupCts?.Cancel();
            }
            catch
            {
                // Cerrar la ventana siempre debe completar aunque una carga
                // ya haya finalizado o esté liberando su CTS.
            }
        }

        /// <summary>
        /// Botón ▣ de la barra superior.
        /// La ventana principal NO cambia de modo ni pierde su búsqueda.
        /// </summary>
        private void OpenCalendarWindow_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (Application.Current is not App app)
            {
                StatusText.Text =
                    "Estado: No se pudo abrir la segunda ventana.";
                return;
            }

            var currentFilter =
                (SearchBox?.Text ?? string.Empty)
                    .Trim();

            app.OpenCalendarWindow(
                currentFilter);

            StatusText.Text =
                string.IsNullOrWhiteSpace(currentFilter)
                    ? "Estado: Calendario abierto en ventana independiente ✅"
                    : $"Estado: Calendario abierto con filtro “{currentFilter}” ✅";
        }
    }
}
