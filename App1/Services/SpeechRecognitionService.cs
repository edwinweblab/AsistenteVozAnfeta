using NAudio.Wave;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;
using Windows.Storage.Streams;

namespace Anfeta.UI.Services
{
    public class SpeechRecognitionService
    {
        private WaveInEvent _waveIn;
        private readonly SettingsService _settingsService;
        private SpeechRecognizer _recognizer;
        private MemoryStream _audioBuffer;

        public event Action<string> OnTextRecognized;

        public SpeechRecognitionService()
        {
            _settingsService = new SettingsService();
            _audioBuffer = new MemoryStream();
        }

        public async Task InitializeAsync()
        {
            _recognizer = new SpeechRecognizer(new Windows.Globalization.Language("es-MX"));

            // Constraints básicos para comandos
            var constraint = new SpeechRecognitionListConstraint(new[]
            {
                "crear recordatorio",
                "abrir chrome",
                "qué hora es",
                "cerrar aplicación"
            });

            _recognizer.Constraints.Add(constraint);
            await _recognizer.CompileConstraintsAsync();
        }

        public void StartListening()
        {
            StopListening();

            int deviceId = _settingsService.InputDeviceId ?? 0;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceId,
                WaveFormat = new WaveFormat(16000, 1)
            };

            _waveIn.DataAvailable += OnAudioData;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
        }

        private void OnAudioData(object sender, WaveInEventArgs e)
        {
            _audioBuffer.Write(e.Buffer, 0, e.BytesRecorded);
        }

        private async void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (_audioBuffer.Length == 0) return;

            try
            {
                _audioBuffer.Position = 0;

                var result = await _recognizer.RecognizeAsync();

                if (result.Status == SpeechRecognitionResultStatus.Success)
                {
                    OnTextRecognized?.Invoke(result.Text);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error STT: {ex.Message}");
            }
            finally
            {
                _audioBuffer = new MemoryStream();
            }
        }

        public void StopListening()
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
        }
    }
}