using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anfeta.UI.Services
{
    public sealed class GlobalHotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int MIC_HOTKEY_ID = 9001;
        private const int SEARCH_HOTKEY_ID = 9002;
        private readonly AppStateService _appState;
        private readonly SettingsService _settingsService;
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _registered;
        private WndProcDelegate? _wndProc;
        private GCHandle _wndProcHandle;
        private bool _searchRegistered;
        // Backup del último hotkey que funcionó
        private uint _lastWorkingModifiers;
        private uint _lastWorkingKey;

        public event EventHandler? HotkeyPressed;
        public event EventHandler<string>? RegistrationFailed;
        public event EventHandler? SearchHotkeyPressed;

        public GlobalHotkeyService(AppStateService appState, SettingsService settingsService)
        {
            _appState = appState;
            _settingsService = settingsService;

            // Guardar default como "último que funcionó"
            _lastWorkingModifiers = appState.HotkeyModifiers;
            _lastWorkingKey = appState.HotkeyKey;

            _appState.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppStateService.HotkeyModifiers) ||
                    e.PropertyName == nameof(AppStateService.HotkeyKey))
                {
                    UpdateHotkey();
                }
                if (e.PropertyName == nameof(AppStateService.SearchHotkeyModifiers) ||
                    e.PropertyName == nameof(AppStateService.SearchHotkeyKey))
                {
                    UpdateHotkey();
                }
            };
        }

        public void Start()
        {
            if (_hwnd != IntPtr.Zero) return;
            CreateMessageWindow();
            RegisterCurrentHotkey();
        }

        private void RegisterCurrentHotkey()
        {
            if (_hwnd == IntPtr.Zero) return;
            _registered = RegisterHotKey(_hwnd, MIC_HOTKEY_ID, _appState.HotkeyModifiers, _appState.HotkeyKey);
            Debug.WriteLine($"[HOTKEY] Intento registro: Mods={_appState.HotkeyModifiers} Key={_appState.HotkeyKey} => {_registered}");
            _searchRegistered = RegisterHotKey(
                _hwnd,
                SEARCH_HOTKEY_ID,
                _appState.SearchHotkeyModifiers,
                _appState.SearchHotkeyKey);
            Debug.WriteLine($"[HOTKEY] Search registro: Mods={_appState.SearchHotkeyModifiers} Key={_appState.SearchHotkeyKey} => {_searchRegistered}");
            if (!_registered)
            {
                var err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[HOTKEY] ERROR RegisterHotKey. Win32Error={err}");

                // Restaurar último hotkey que funcionó
                Debug.WriteLine($"[HOTKEY] Restaurando hotkey anterior: Mods={_lastWorkingModifiers} Key={_lastWorkingKey}");
                _appState.HotkeyModifiers = _lastWorkingModifiers;
                _appState.HotkeyKey = _lastWorkingKey;
                _settingsService.SaveHotkey(_lastWorkingModifiers, _lastWorkingKey);

                // Notificar fallo
                var keyName = ((System.Windows.Forms.Keys)_appState.HotkeyKey).ToString();
                var modsParts = new System.Collections.Generic.List<string>();
                if ((_appState.HotkeyModifiers & 0x0002) != 0) modsParts.Add("Ctrl");
                if ((_appState.HotkeyModifiers & 0x0001) != 0) modsParts.Add("Alt");
                if ((_appState.HotkeyModifiers & 0x0004) != 0) modsParts.Add("Shift");
                if ((_appState.HotkeyModifiers & 0x0008) != 0) modsParts.Add("Win");
                modsParts.Add(keyName);

                var hotkeyDisplay = string.Join(" + ", modsParts);
                RegistrationFailed?.Invoke(this, $"El atajo '{hotkeyDisplay}' ya está en uso por otra aplicación. Se restauró el atajo anterior.");
            }
            else
            {
                // Guardar como "último que funcionó"
                _lastWorkingModifiers = _appState.HotkeyModifiers;
                _lastWorkingKey = _appState.HotkeyKey;
                Debug.WriteLine("[HOTKEY] Registro exitoso");
            }
        }

        private void UpdateHotkey()
        {
            if (_registered)
            {
                UnregisterHotKey(_hwnd, MIC_HOTKEY_ID);
                _registered = false;
            }

            if (_searchRegistered)
            {
                UnregisterHotKey(_hwnd, SEARCH_HOTKEY_ID);
                _searchRegistered = false;
            }

            RegisterCurrentHotkey();
        }

        public void Stop()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            if (_registered)
            {
                UnregisterHotKey(_hwnd, MIC_HOTKEY_ID);
                _registered = false;
            }

            if (_searchRegistered)
            {
                UnregisterHotKey(_hwnd, SEARCH_HOTKEY_ID);
                _searchRegistered = false;
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
                Debug.WriteLine($"[HOTKEY] RegisterClassEx atom=0 err={err}");
            }

            IntPtr HWND_MESSAGE = new IntPtr(-3);

            _hwnd = CreateWindowEx(0, "AnfetaHotkeyMsgWindow", "AnfetaHotkeyMsgWindow",
                0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[HOTKEY] ERROR CreateWindowEx. Win32Error={err}");
                throw new InvalidOperationException("No se pudo crear ventana hotkey.");
            }

            Debug.WriteLine("[HOTKEY] Message-only window creada OK");
        }

        private void DestroyMessageWindow()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_wndProcHandle.IsAllocated)
                _wndProcHandle.Free();

            _wndProc = null;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY)
            {
                var id = wParam.ToInt32();

                if (id == MIC_HOTKEY_ID)
                {
                    Debug.WriteLine("[HOTKEY] Micrófono detectado");
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                }
                else if (id == SEARCH_HOTKEY_ID)
                {
                    Debug.WriteLine("[HOTKEY] Buscador detectado");
                    SearchHotkeyPressed?.Invoke(this, EventArgs.Empty);
                }
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        public void Pause()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            if (_registered)
            {
                UnregisterHotKey(_hwnd, MIC_HOTKEY_ID);
                _registered = false;
            }

            if (_searchRegistered)
            {
                UnregisterHotKey(_hwnd, SEARCH_HOTKEY_ID);
                _searchRegistered = false;
            }
        }

        public void Resume()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            if (_registered && _searchRegistered)
                return;

            if (_registered)
            {
                UnregisterHotKey(_hwnd, MIC_HOTKEY_ID);
                _registered = false;
            }

            if (_searchRegistered)
            {
                UnregisterHotKey(_hwnd, SEARCH_HOTKEY_ID);
                _searchRegistered = false;
            }

            RegisterCurrentHotkey();
            Debug.WriteLine("[HOTKEY] Reanudado");
        }

        public void Dispose() => Stop();

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