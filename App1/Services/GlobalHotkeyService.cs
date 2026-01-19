using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Anfeta.UI.Services
{
    [Flags]
    public enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Win = 8
    }

    public sealed class GlobalHotkeyService : IDisposable
    {
        private readonly IntPtr _hWnd;
        private readonly int _hotkeyId;
        private readonly DispatcherQueue _dispatcher;

        private const int WM_HOTKEY = 0x0312;
        private const int GWL_WNDPROC = -4;

        // Mantener delegate vivo SIEMPRE
        private readonly WndProcDelegate _newWndProc;
        private readonly IntPtr _newWndProcPtr;
        private IntPtr _oldWndProcPtr;

        public event EventHandler? HotkeyPressed;

        public GlobalHotkeyService(Window window, int hotkeyId = 9001)
        {
            _hotkeyId = hotkeyId;

            // Dispatcher del hilo UI donde creaste MainWindow
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _hWnd = WindowNative.GetWindowHandle(window);

            Debug.WriteLine($"[HOTKEY] ctor. hWnd=0x{_hWnd.ToInt64():X} id={_hotkeyId}");

            _newWndProc = WndProc;
            _newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_newWndProc);

            // Hook correcto: SetWindowLongPtr con IntPtr
            _oldWndProcPtr = SetWindowLongPtr(_hWnd, GWL_WNDPROC, _newWndProcPtr);

            Debug.WriteLine($"[HOTKEY] WndProc hook ok. oldWndProc=0x{_oldWndProcPtr.ToInt64():X}");
        }

        public void Register(HotkeyModifiers modifiers, uint virtualKey)
        {
            Debug.WriteLine($"[HOTKEY] Register -> mods={(uint)modifiers} vk=0x{virtualKey:X} id={_hotkeyId}");

            if (!RegisterHotKey(_hWnd, _hotkeyId, (uint)modifiers, virtualKey))
            {
                var err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[HOTKEY] RegisterHotKey FAIL. err={err}");
                throw new InvalidOperationException($"RegisterHotKey falló. Win32Error={err}");
            }

            Debug.WriteLine("[HOTKEY] RegisterHotKey OK");
        }

        public void Unregister()
        {
            Debug.WriteLine("[HOTKEY] Unregister");
            UnregisterHotKey(_hWnd, _hotkeyId);
        }

        public void Dispose()
        {
            Debug.WriteLine("[HOTKEY] Dispose");
            try { Unregister(); } catch (Exception ex) { Debug.WriteLine("[HOTKEY] Unregister error: " + ex); }

            // Restaurar WndProc original
            if (_oldWndProcPtr != IntPtr.Zero)
            {
                SetWindowLongPtr(_hWnd, GWL_WNDPROC, _oldWndProcPtr);
                Debug.WriteLine($"[HOTKEY] WndProc restored. oldWndProc=0x{_oldWndProcPtr.ToInt64():X}");
                _oldWndProcPtr = IntPtr.Zero;
            }

            // No liberamos el delegate: queda para vida de este objeto y ya se va al GC al destruirse
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY)
            {
                Debug.WriteLine($"[HOTKEY] WM_HOTKEY recibido. wParam={wParam} lParam=0x{lParam.ToInt64():X}");
            }

            if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
            {
                Debug.WriteLine("[HOTKEY] WM_HOTKEY coincide con id -> disparando evento (UI thread)");
                _dispatcher.TryEnqueue(() => HotkeyPressed?.Invoke(this, EventArgs.Empty));
                return IntPtr.Zero;
            }

            return CallWindowProc(_oldWndProcPtr, hWnd, msg, wParam, lParam);
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Firma correcta x64/x86 (IntPtr)
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
