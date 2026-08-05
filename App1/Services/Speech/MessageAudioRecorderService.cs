using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Anfeta.UI.Services.Speech
{
    public sealed class MessageAudioRecordingResult
    {
        public string Path { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Grabador dedicado para mensajes de ANFETA.
    /// Usa el micrófono predeterminado de Windows y guarda temporalmente en M4A.
    /// No comparte estado con el reconocimiento de voz ni con HomeView.
    /// </summary>
    public sealed class MessageAudioRecorderService : IAsyncDisposable
    {
        private MediaCapture? _capture;
        private LowLagMediaRecording? _recording;
        private StorageFile? _temporaryFile;
        private DateTimeOffset _startedAt;
        private bool _isRecording;
        private bool _isStopping;

        public bool IsRecording => _isRecording;

        public TimeSpan Elapsed =>
            _isRecording && _startedAt != default
                ? DateTimeOffset.Now - _startedAt
                : TimeSpan.Zero;

        public async Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            if (_isRecording)
                return;

            await ResetCaptureAsync(deleteTemporaryFile: true);
            cancellationToken.ThrowIfCancellationRequested();

            var capture = new MediaCapture();
            StorageFile? file = null;
            LowLagMediaRecording? recording = null;

            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio,
                    MediaCategory = MediaCategory.Speech,
                    AudioProcessing = AudioProcessing.Default
                };

                await capture.InitializeAsync(settings);
                cancellationToken.ThrowIfCancellationRequested();

                file =
                    await ApplicationData.Current.TemporaryFolder
                        .CreateFileAsync(
                            $"audio-ANFETA-{DateTime.Now:yyyyMMdd-HHmmss}.m4a",
                            CreationCollisionOption.GenerateUniqueName);

                var profile =
                    MediaEncodingProfile.CreateM4a(
                        AudioEncodingQuality.Auto);

                recording =
                    await capture.PrepareLowLagRecordToStorageFileAsync(
                        profile,
                        file);

                cancellationToken.ThrowIfCancellationRequested();
                await recording.StartAsync();

                _capture = capture;
                _recording = recording;
                _temporaryFile = file;
                _startedAt = DateTimeOffset.Now;
                _isRecording = true;
            }
            catch
            {
                if (recording != null)
                {
                    try
                    {
                        await recording.FinishAsync();
                    }
                    catch
                    {
                    }
                }

                capture.Dispose();

                if (file != null)
                {
                    try
                    {
                        await file.DeleteAsync(
                            StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                    }
                }

                throw;
            }
        }

        public async Task<MessageAudioRecordingResult?> StopAsync()
        {
            if (!_isRecording ||
                _recording == null ||
                _temporaryFile == null ||
                _isStopping)
            {
                return null;
            }

            _isStopping = true;

            var recording = _recording;
            var capture = _capture;
            var file = _temporaryFile;
            var duration = DateTimeOffset.Now - _startedAt;

            try
            {
                await recording.StopAsync();
                await recording.FinishAsync();

                _recording = null;
                _capture = null;
                _temporaryFile = null;
                _startedAt = default;
                _isRecording = false;

                capture?.Dispose();

                return new MessageAudioRecordingResult
                {
                    Path = file.Path,
                    FileName = file.Name,
                    Duration = duration < TimeSpan.Zero
                        ? TimeSpan.Zero
                        : duration
                };
            }
            catch
            {
                await ResetCaptureAsync(deleteTemporaryFile: true);
                throw;
            }
            finally
            {
                _isStopping = false;
            }
        }

        public Task CancelAsync()
            => ResetCaptureAsync(deleteTemporaryFile: true);

        private async Task ResetCaptureAsync(
            bool deleteTemporaryFile)
        {
            var recording = _recording;
            var capture = _capture;
            var file = _temporaryFile;
            var wasRecording = _isRecording;

            _recording = null;
            _capture = null;
            _temporaryFile = null;
            _startedAt = default;
            _isRecording = false;

            if (recording != null)
            {
                try
                {
                    if (wasRecording)
                        await recording.StopAsync();
                }
                catch
                {
                }

                try
                {
                    await recording.FinishAsync();
                }
                catch
                {
                }
            }

            try
            {
                capture?.Dispose();
            }
            catch
            {
            }

            if (deleteTemporaryFile && file != null)
            {
                try
                {
                    await file.DeleteAsync(
                        StorageDeleteOption.PermanentDelete);
                }
                catch
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ResetCaptureAsync(deleteTemporaryFile: true);
        }
    }
}
