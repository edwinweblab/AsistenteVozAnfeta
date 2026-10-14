using System;
using System.Runtime.InteropServices;
using Windows.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using Anfeta.UI.Models.Search;
using Anfeta.UI.Services.Search;

namespace Anfeta.UI.Views.Dialogs
{
    public sealed partial class QuickPendingWindow : Window
    {
        private AppWindow? _appWindow;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        public QuickPendingWindow()
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            InitializeWindow();
        }

        private void InitializeWindow()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

            if (_appWindow != null)
            {
                var presenter = OverlappedPresenter.Create();
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                _appWindow.SetPresenter(presenter);

                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                var workArea = displayArea.WorkArea;

                int width = 580;
                int height = 155;

                _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    workArea.X + (workArea.Width - width) / 2,
                    workArea.Y + (workArea.Height / 4),
                    width,
                    height
                ));
            }
        }

        public void ShowAndFocus()
        {
            Activate();
            var hwnd = WindowNative.GetWindowHandle(this);
            SetForegroundWindow(hwnd);
            TaskInputBox.Text = "";
            TaskInputBox.Focus(FocusState.Programmatic);
            UpdateLivePreview();
        }

        private void TaskInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                SaveAndClose();
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void TaskInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateLivePreview();
        }

        private void UpdateLivePreview()
        {
            var text = (TaskInputBox.Text ?? "").Trim();
            SavedSearch parsed;
            try { parsed = QuickTaskParser.Parse(text); }
            catch (Exception ex) { DatePreviewText.Text = "⚠ " + ex.Message; DatePreviewChip.Visibility = Visibility.Visible; return; }

            // Fecha
            DatePreviewText.Text = $"📅 {parsed.ScheduledDate}";
            DatePreviewChip.Visibility = Visibility.Visible;

            // Prioridad
            if (!string.IsNullOrWhiteSpace(parsed.Priority))
            {
                PriorityPreviewText.Text = $"🔥 {parsed.Priority.ToUpperInvariant()}";
                PriorityPreviewChip.Visibility = Visibility.Visible;
            }
            else
            {
                PriorityPreviewChip.Visibility = Visibility.Collapsed;
            }

            // Tag / Persona
            if (!string.IsNullOrWhiteSpace(parsed.Tag))
            {
                TagPreviewText.Text = $"👤 {parsed.Tag}";
                TagPreviewChip.Visibility = Visibility.Visible;
            }
            else
            {
                TagPreviewChip.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnSaveTask_Click(object sender, RoutedEventArgs e)
        {
            SaveAndClose();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SaveAndClose()
        {
            var raw = (TaskInputBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                Close();
                return;
            }

            try
            {
                var parsed = QuickTaskParser.Parse(raw);
                if (string.IsNullOrWhiteSpace(parsed.Title)) throw new FormatException("Agrega el texto del pendiente además de su fecha o etiquetas.");
                PendingTasksService.AddTask(parsed);
                Close();
            }
            catch (Exception ex)
            {
                DatePreviewText.Text = "⚠ " + ex.Message;
                DatePreviewChip.Visibility = Visibility.Visible;
                TaskInputBox.Focus(FocusState.Programmatic);
            }
        }
    }
}
