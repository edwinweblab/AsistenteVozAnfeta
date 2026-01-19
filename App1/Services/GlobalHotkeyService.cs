using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anfeta.UI.Services
{
    public sealed class GlobalHotkeyService : IDisposable
    {
        // Win32
        private const int WM_HOTKEY = 0x0312;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;

        private const int HOTKEY_ID = 9001;

        private IntPtr _hwnd = IntPtr.Zero;
        private bool _registered;
        private WndProcDelegate? _wndProc;
        private GCHandle _wndProcHandle;

        public event EventHandler? HotkeyPressed;

        public void Start()
        {
            if (_hwnd != IntPtr.Zero)
                return;

            CreateMessageWindow();

            // Ctrl + Alt + V
            // VK_V = 0x56
            _registered = RegisterHotKey(_hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, 0x56);
            Debug.WriteLine($"[HOTKEY] RegisterHotKey Ctrl+Alt+V => {_registered}");

            if (!_registered)
            {
                var err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[HOTKEY] ERROR RegisterHotKey. Win32Error={err}");
            }
        }

        public void Stop()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            if (_registered)
            {
                UnregisterHotKey(_hwnd, HOTKEY_ID);
                _registered = false;
                Debug.WriteLine("[HOTKEY] UnregisterHotKey OK");
            }

            DestroyMessageWindow();
        }

        private void CreateMessageWindow()
        {
            _wndProc = WndProc;
            _wndProcHandle = GCHandle.Alloc(_wndProc);

            var hInstance = GetModuleHandle(null);

            var cls = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = "AnfetaHotkeyMsgWindow"
            };

            ushort atom = RegisterClassEx(ref cls);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                // Si ya existe la clase, RegisterClassEx puede fallar. Igual podemos intentar CreateWindowEx.
                Debug.WriteLine($"[HOTKEY] RegisterClassEx atom=0 err={err} (puede ser OK si ya existe)");
            }

            // HWND_MESSAGE = -3 => ventana solo para mensajes (no visible)
            IntPtr HWND_MESSAGE = new IntPtr(-3);

            _hwnd = CreateWindowEx(
                0,
                "AnfetaHotkeyMsgWindow",
                "AnfetaHotkeyMsgWindow",
                0,
                0, 0, 0, 0,
                HWND_MESSAGE,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero
            );

            if (_hwnd == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[HOTKEY] ERROR CreateWindowEx. Win32Error={err}");
                throw new InvalidOperationException("No se pudo crear la ventana oculta para el hotkey.");
            }

            Debug.WriteLine("[HOTKEY] Message-only window creada OK");
        }

        private void DestroyMessageWindow()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                Debug.WriteLine("[HOTKEY] Message-only window destruida");
            }

            if (_wndProcHandle.IsAllocated)
                _wndProcHandle.Free();

            _wndProc = null;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID)
                {
                    Debug.WriteLine("[HOTKEY] Ctrl+Alt+V detectado");
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                }
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
        }

        // Delegado WndProc
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // P/Invoke
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

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
