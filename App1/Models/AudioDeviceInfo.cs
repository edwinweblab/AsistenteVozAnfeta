namespace Anfeta.UI.Models
{
    public class AudioDeviceInfo
    {
        public int NAudioId { get; set; }
        public string CoreAudioId { get; set; }
        public string UniqueId { get; set; }
        public string DeviceName { get; set; }
        public bool IsDefault { get; set; }

        public string DisplayName => IsDefault
            ? $"{DeviceName} ({UniqueId}) [Predeterminado]"
            : $"{DeviceName} ({UniqueId})";

        public AudioDeviceInfo(int naudioId, string coreAudioId, string uniqueId, string name, bool isDefault = false)
        {
            NAudioId = naudioId;
            CoreAudioId = coreAudioId;
            UniqueId = uniqueId;
            DeviceName = name;
            IsDefault = isDefault;
        }
    }
}