using Anfeta.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.InteropServices;
using Windows.System;

namespace Anfeta.UI.Dialogs
{
    public sealed partial class HotkeyPickerDialog : ContentDialog
    {
        private readonly AppStateService _appState;
        private readonly SettingsService _settingsService;
        private readonly GlobalHotkeyService _hotkeyService;
        private uint _capturedModifiers;
        private uint _capturedKey;

        public HotkeyPickerDialog()
        {
            this.InitializeComponent();
        }

        public HotkeyPickerDialog(AppStateService appState, SettingsService settingsService) : this()
        {
            _appState = appState;
            _settingsService = settingsService;
            _hotkeyService = App.AppHost.Services.GetRequiredService<GlobalHotkeyService>();

            TxtPreview.Text = _appState.GetHotkeyDisplayString();
            _capturedModifiers = _appState.HotkeyModifiers;
            _capturedKey = _appState.HotkeyKey;

            PrimaryButtonClick += OnSave;
            Closed += OnClosed;

            // AGREGAR ESTO:
            Loaded += (s, e) =>
            {
                var stackPanel = Content as StackPanel;
                stackPanel?.Focus(FocusState.Programmatic);
            };

            _hotkeyService.Pause();
        }

        private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            // Reanudar hotkey global
            _hotkeyService.Resume();
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            e.Handled = true;

            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var win = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
                      Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            uint mods = 0;
            if (ctrl) mods |= 0x0002;
            if (alt) mods |= 0x0001;
            if (shift) mods |= 0x0004;
            if (win) mods |= 0x0008;

            if (mods == 0)
            {
                TxtWarning.Visibility = Visibility.Visible;
                TxtWarning.Text = "Debe incluir modificadores (Ctrl, Alt, Shift o Win).";
                IsPrimaryButtonEnabled = false;
                return;
            }

            var key = e.Key;
            if (key == VirtualKey.Control || key == VirtualKey.Menu ||
                key == VirtualKey.Shift || key == VirtualKey.LeftWindows ||
                key == VirtualKey.RightWindows)
            {
                TxtWarning.Visibility = Visibility.Visible;
                TxtWarning.Text = "Presiona una tecla adicional (A-Z, 0-9, F1-F12).";
                IsPrimaryButtonEnabled = false;
                return;
            }

            _capturedModifiers = mods;
            _capturedKey = (uint)key;

            // Detectar si es la misma combinación actual
            if (_capturedModifiers == _appState.HotkeyModifiers && _capturedKey == _appState.HotkeyKey)
            {
                TxtWarning.Visibility = Visibility.Visible;
                TxtWarning.Text = "Esta es la combinación actual. Elige una diferente para cambiarla.";
                IsPrimaryButtonEnabled = false;

                var parts = new System.Collections.Generic.List<string>();
                if (ctrl) parts.Add("Ctrl");
                if (alt) parts.Add("Alt");
                if (shift) parts.Add("Shift");
                if (win) parts.Add("Win");
                parts.Add(key.ToString());
                TxtPreview.Text = string.Join(" + ", parts) + " (Actual)";
                return;
            }

            TxtWarning.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = true;

            var keyParts = new System.Collections.Generic.List<string>();
            if (ctrl) keyParts.Add("Ctrl");
            if (alt) keyParts.Add("Alt");
            if (shift) keyParts.Add("Shift");
            if (win) keyParts.Add("Win");
            keyParts.Add(key.ToString());

            TxtPreview.Text = string.Join(" + ", keyParts);
        }

        private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_capturedModifiers == 0 || _capturedKey == 0)
            {
                args.Cancel = true;
                TxtWarning.Visibility = Visibility.Visible;
                TxtWarning.Text = "Presiona una combinación válida antes de guardar.";
                return;
            }

            // Detectar si ya está en uso por el sistema
            if (!IsHotkeyAvailable(_capturedModifiers, _capturedKey))
            {
                args.Cancel = true;
                TxtWarning.Visibility = Visibility.Visible;
                TxtWarning.Text = "Esta combinación ya está en uso por el sistema u otra aplicación. Elige otra.";
                return;
            }

            _settingsService.SaveHotkey(_capturedModifiers, _capturedKey);
        }

        // Verifica si el hotkey está disponible intentando registrarlo temporalmente
        private bool IsHotkeyAvailable(uint modifiers, uint key)
        {
            IntPtr dummyHwnd = IntPtr.Zero;
            const int TEST_ID = 9999;

            try
            {
                // Crear ventana temporal para probar
                var hInstance = GetModuleHandle(null);
                var wndClass = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(new WndProcDelegate(DefWindowProc)),
                    hInstance = hInstance,
                    lpszClassName = "TempHotkeyTest"
                };

                RegisterClassEx(ref wndClass);
                IntPtr HWND_MESSAGE = new IntPtr(-3);
                dummyHwnd = CreateWindowEx(0, "TempHotkeyTest", "", 0, 0, 0, 0, 0,
                    HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (dummyHwnd == IntPtr.Zero) return false;

                // Intentar registrar
                bool registered = RegisterHotKey(dummyHwnd, TEST_ID, modifiers, key);

                if (registered)
                {
                    UnregisterHotKey(dummyHwnd, TEST_ID);
                    return true;
                }

                return false;
            }
            finally
            {
                if (dummyHwnd != IntPtr.Zero)
                    DestroyWindow(dummyHwnd);
            }
        }

        // P/Invoke
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }
    }
}