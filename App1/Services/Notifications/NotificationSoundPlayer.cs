using NAudio.Wave;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Notifications
{
    public static class NotificationSoundPlayer
    {
        public const string LS_NotificationsMuted = "Notifications.Muted";
        public const string LS_NotificationsVolume = "Notifications.Volume";
        public const string LS_NotificationsAutoDismiss = "Notifications.AutoDismiss";

        public static event EventHandler<bool>? MuteChanged;

        public static bool IsMuted
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[LS_NotificationsMuted] as bool? ?? false;
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values[LS_NotificationsMuted] = value;
                    MuteChanged?.Invoke(null, value);
                }
                catch
                {
                }
            }
        }

        // Volumen en porcentaje (0 a 100). Predeterminado: 25% (para bajarle el 75-80% como pidió John).
        public static double VolumePercent
        {
            get
            {
                try
                {
                    var val = ApplicationData.Current.LocalSettings.Values[LS_NotificationsVolume];
                    if (val is double d) return Math.Clamp(d, 0.0, 100.0);
                    if (val is int i) return Math.Clamp((double)i, 0.0, 100.0);
                    if (val is float f) return Math.Clamp((double)f, 0.0, 100.0);
                    return 25.0;
                }
                catch
                {
                    return 25.0;
                }
            }
            set
            {
                try
                {
                    var clamped = Math.Clamp(value, 0.0, 100.0);
                    ApplicationData.Current.LocalSettings.Values[LS_NotificationsVolume] = clamped;
                }
                catch
                {
                }
            }
        }

        public static float VolumeNormalized => (float)(VolumePercent / 100.0);

        public static bool IsAutoDismissEnabled
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[LS_NotificationsAutoDismiss] as bool? ?? true;
                }
                catch
                {
                    return true;
                }
            }
            set
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values[LS_NotificationsAutoDismiss] = value;
                }
                catch
                {
                }
            }
        }

        public static void ToggleMute()
        {
            IsMuted = !IsMuted;
        }

        /// <summary>
        /// Reproduce un tono suave y agradable de notificación a bajo volumen.
        /// Si está muteado o el volumen es 0, no reproduce nada.
        /// Cero llamadas a Beep() de Win32.
        /// </summary>
        public static async Task PlayNotificationSoundAsync(bool isUrgent = false)
        {
            if (IsMuted)
                return;

            var volume = VolumeNormalized;
            if (volume <= 0.01f)
                return;

            await Task.Run(() =>
            {
                try
                {
                    // Genera un sonido armónico tipo chime suave con ataque y decaimiento exponencial
                    byte[] pcmData = GenerateChimePcm(isUrgent);
                    var waveFormat = new WaveFormat(44100, 16, 1);

                    using var ms = new MemoryStream(pcmData);
                    using var rawStream = new RawSourceWaveStream(ms, waveFormat);
                    using var waveOut = new WaveOutEvent();

                    // Ajusta el volumen específico del reproductor NAudio
                    waveOut.Volume = Math.Clamp(volume, 0.0f, 1.0f);
                    waveOut.Init(rawStream);
                    waveOut.Play();

                    while (waveOut.PlaybackState == PlaybackState.Playing)
                    {
                        Thread.Sleep(25);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NotificationSoundPlayer] Error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Sintetiza un chime suave en memoria (PCM 16-bit, 44.1 kHz, Mono).
        /// Para alerta normal: 2 notas armoniosas suaves (G5 784 Hz -> C6 1046 Hz).
        /// Para urgente: 3 notas distintivas pero atenuadas (E5 659 Hz -> G5 784 Hz -> C6 1046 Hz).
        /// Cada nota tiene envolvente ADSR suave para evitar chasquidos o estridencia.
        /// </summary>
        private static byte[] GenerateChimePcm(bool isUrgent)
        {
            int sampleRate = 44100;
            // Notas en Hz y duraciones en segundos
            double[] frequencies = isUrgent
                ? new double[] { 659.25, 783.99, 1046.50 } // E5 -> G5 -> C6
                : new double[] { 783.99, 1046.50 };        // G5 -> C6

            double noteDuration = isUrgent ? 0.16 : 0.22;
            double noteSpacing = isUrgent ? 0.10 : 0.13;
            double totalDuration = (frequencies.Length - 1) * noteSpacing + noteDuration + 0.15;
            int totalSamples = (int)(sampleRate * totalDuration);

            float[] floatBuffer = new float[totalSamples];

            for (int noteIdx = 0; noteIdx < frequencies.Length; noteIdx++)
            {
                double freq = frequencies[noteIdx];
                int startSample = (int)(noteIdx * noteSpacing * sampleRate);
                int noteSamples = (int)(noteDuration * sampleRate);

                for (int i = 0; i < noteSamples && (startSample + i) < totalSamples; i++)
                {
                    double t = (double)i / sampleRate;
                    // Envolvente suave: ataque rápido (10 ms) y decaimiento exponencial natural de campana
                    double attack = Math.Min(1.0, t / 0.012);
                    double decay = Math.Exp(-t * 8.5);
                    double envelope = attack * decay;

                    // Tono puro con armónico muy suave (octava a 0.25 para calidez)
                    double sample = (Math.Sin(2.0 * Math.PI * freq * t) +
                                    0.25 * Math.Sin(4.0 * Math.PI * freq * t)) * envelope * 0.45;

                    floatBuffer[startSample + i] += (float)sample;
                }
            }

            // Convertir float[-1.0, 1.0] a 16-bit PCM bytes
            byte[] bytes = new byte[totalSamples * 2];
            for (int i = 0; i < totalSamples; i++)
            {
                float val = Math.Clamp(floatBuffer[i], -1.0f, 1.0f);
                short sample16 = (short)(val * 32767f);
                bytes[i * 2] = (byte)(sample16 & 0xFF);
                bytes[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
            }

            return bytes;
        }
    }
}
