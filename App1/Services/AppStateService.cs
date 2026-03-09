using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Anfeta.UI.Services
{
    public sealed class AppStateService : INotifyPropertyChanged
    {
        private int? _inputDeviceId;
        private int? _outputDeviceId;
        private string _inputDeviceName = "No configurado";
        private string _outputDeviceName = "No configurado";
        private uint _hotkeyModifiers = 0x0003; // Ctrl+Alt
        private uint _hotkeyKey = 0x56;   // V

        // Email del usuario autenticado. Poblado desde el JWT al arranque.
        private string? _currentUserEmail;
        public string? CurrentUserEmail
        {
            get => _currentUserEmail;
            set => SetField(ref _currentUserEmail, value);
        }

        // Nombre visible del usuario autenticado (firstName + lastName).
        // Poblado desde /api/users/search al arranque, junto a CollaboratorId.
        // Usado por WeblabReportesClient para evitar llamar a /api/auth/me en cada comando.
        private string? _currentUserName;
        public string? CurrentUserName
        {
            get => _currentUserName;
            set => SetField(ref _currentUserName, value);
        }

        // ID de colaborador Notion. Corresponde a collaboratorId en /api/users/search
        // y a idAsignee / assignees[].id en /api/reportes/revisiones-por-fecha.
        // Poblado desde /api/users/search al arranque, junto a CurrentUserName.
        private string? _collaboratorId;
        public string? CollaboratorId
        {
            get => _collaboratorId;
            set => SetField(ref _collaboratorId, value);
        }

        public int? InputDeviceId
        {
            get => _inputDeviceId;
            set => SetField(ref _inputDeviceId, value);
        }

        public int? OutputDeviceId
        {
            get => _outputDeviceId;
            set => SetField(ref _outputDeviceId, value);
        }

        public string InputDeviceName
        {
            get => _inputDeviceName;
            set => SetField(ref _inputDeviceName, value);
        }

        public string OutputDeviceName
        {
            get => _outputDeviceName;
            set => SetField(ref _outputDeviceName, value);
        }

        public uint HotkeyModifiers
        {
            get => _hotkeyModifiers;
            set => SetField(ref _hotkeyModifiers, value);
        }

        public uint HotkeyKey
        {
            get => _hotkeyKey;
            set => SetField(ref _hotkeyKey, value);
        }

        public string GetHotkeyDisplayString()
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((_hotkeyModifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((_hotkeyModifiers & 0x0001) != 0) parts.Add("Alt");
            if ((_hotkeyModifiers & 0x0004) != 0) parts.Add("Shift");
            if ((_hotkeyModifiers & 0x0008) != 0) parts.Add("Win");
            var keyName = ((System.Windows.Forms.Keys)_hotkeyKey).ToString();
            parts.Add(keyName);
            return string.Join(" + ", parts);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}