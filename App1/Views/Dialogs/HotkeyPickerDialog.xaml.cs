using Anfeta.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Anfeta.UI.Dialogs
{
    public sealed partial class HotkeyPickerDialog : ContentDialog
    {
        private readonly AppStateService _appState;
        private uint _capturedModifiers;
        private uint _capturedKey;

        public HotkeyPickerDialog()
        {
            this.InitializeComponent();
        }

        public HotkeyPickerDialog(AppStateService appState) : this()
        {
            _appState = appState;
            TxtPreview.Text = _appState.GetHotkeyDisplayString();
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            e.Handled = true;

            var coreWindow = CoreWindow.GetForCurrentThread();
            var ctrl = (coreWindow.GetKeyState(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            var alt = (coreWindow.GetKeyState(VirtualKey.Menu) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            var shift = (coreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            var win = (coreWindow.GetKeyState(VirtualKey.LeftWindows) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down ||
                      (coreWindow.GetKeyState(VirtualKey.RightWindows) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            VirtualKeyModifiers modifiers = VirtualKeyModifiers.None;
            if (ctrl) modifiers |= VirtualKeyModifiers.Control;
            if (alt) modifiers |= VirtualKeyModifiers.Menu;
            if (shift) modifiers |= VirtualKeyModifiers.Shift;
            if (win) modifiers |= VirtualKeyModifiers.Windows;

            if (modifiers == VirtualKeyModifiers.None)
            {
                TxtWarning.Visibility = Visibility.Visible;
                return;
            }

            TxtWarning.Visibility = Visibility.Collapsed;
            _capturedModifiers = (uint)modifiers;
            _capturedKey = (uint)e.Key;

            TxtPreview.Text = $"{modifiers} + {e.Key}";
        }
    }
}