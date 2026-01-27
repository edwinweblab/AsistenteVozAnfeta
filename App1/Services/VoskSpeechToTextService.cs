using Anfeta.UI.Models;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vosk;

namespace Anfeta.UI.Services
{
    /// <summary>Servicio de reconocimiento de voz usando Vosk (offline, rápido)</summary>
    public sealed class VoskSpeechToTextService : ISpeechToTextService, IDisposable
    {
        private Model? _model;
        private VoskRecognizer? _recognizer;
        private WaveInEvent? _waveIn;
        private bool _isRecording;
        private TaskCompletionSource<string>? _resultTcs;
        private string _currentLanguage = "es-MX";
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _isInitialized;

        /// <summary>Inicializar modelo Vosk</summary>
        public async Task InitializeAsync(string languageTag = "es-MX")
        {
            await _initLock.WaitAsync();
            try
            {
                if (_isInitialized)
                {
                    System.Diagnostics.Debug.WriteLine("[Vosk] Ya inicializado");
                    return;
                }

                await Task.Run(() =>
                {
                    var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "vosk-model-es");

                    if (!Directory.Exists(modelPath))
                    {
                        throw new FileNotFoundException(
                            $"Modelo Vosk no encontrado en: {modelPath}\n" +
                            "Descarga 'vosk-model-small-es-0.42.zip' de https://alphacephei.com/vosk/models\n" +
                            "Extrae la carpeta 'vosk-model-small-es-0.42' y renómbrala a 'vosk-model-es'\n" +
                            "Cópiala a la carpeta Assets del proyecto"
                        );
                    }

                    Vosk.Vosk.SetLogLevel(-1);
                    _model = new Model(modelPath);
                    _recognizer = new VoskRecognizer(_model, 16000.0f);
                    _recognizer.SetMaxAlternatives(0);
                    _recognizer.SetWords(false);

                    _currentLanguage = languageTag;
                    _isInitialized = true;

                    System.Diagnostics.Debug.WriteLine($"[Vosk] Inicializado OK: {modelPath}");
                });
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>Reconocer comando de voz una vez</summary>
        public Task<string?> RecognizeOnceAsync(CancellationToken ct = default)
        {
            if (!_isInitialized || _model == null || _recognizer == null)
                throw new InvalidOperationException("Vosk no inicializado. Llama a InitializeAsync primero.");

            _resultTcs = new TaskCompletionSource<string>();
            _isRecording = true;

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 1),
                BufferMilliseconds = 100
            };

            var silenceThreshold = 0.01f;
            var silenceCounter = 0;
            var maxSilenceBuffers = 10;
            var hasSpoken = false;

            _waveIn.DataAvailable += (s, e) =>
            {
                if (!_isRecording) return;

                try
                {
                    var samples = new float[e.BytesRecorded / 2];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                    }
                    var rms = Math.Sqrt(samples.Average(s => s * s));

                    if (rms > silenceThreshold)
                    {
                        hasSpoken = true;
                        silenceCounter = 0;
                    }
                    else if (hasSpoken)
                    {
                        silenceCounter++;
                    }

                    if (_recognizer!.AcceptWaveform(e.Buffer, e.BytesRecorded))
                    {
                        var result = _recognizer.Result();
                        var text = ExtractTextFromJson(result);

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Vosk] Reconocido: {text}");
                            _isRecording = false;
                            _waveIn?.StopRecording();
                            _resultTcs?.TrySetResult(text);
                        }
                    }

                    if (hasSpoken && silenceCounter >= maxSilenceBuffers)
                    {
                        System.Diagnostics.Debug.WriteLine("[Vosk] Silencio detectado -> finalizando");
                        _isRecording = false;
                        _waveIn?.StopRecording();

                        var finalResult = _recognizer!.FinalResult();
                        var finalText = ExtractTextFromJson(finalResult);
                        _resultTcs?.TrySetResult(finalText);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Vosk] Error procesando audio: {ex.Message}");
                    _isRecording = false;
                    _waveIn?.StopRecording();
                    _resultTcs?.TrySetException(ex);
                }
            };

            _waveIn.RecordingStopped += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[Vosk] Grabación detenida");

                if (_isRecording && _resultTcs != null && !_resultTcs.Task.IsCompleted)
                {
                    var finalResult = _recognizer!.FinalResult();
                    var finalText = ExtractTextFromJson(finalResult);
                    _resultTcs.TrySetResult(finalText);
                }
            };

            ct.Register(() =>
            {
                System.Diagnostics.Debug.WriteLine("[Vosk] Cancelación solicitada");
                _isRecording = false;
                _waveIn?.StopRecording();
                _resultTcs?.TrySetCanceled();
            });

            try
            {
                _waveIn.StartRecording();
                System.Diagnostics.Debug.WriteLine("[Vosk] Grabación iniciada");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vosk] Error iniciando grabación: {ex.Message}");
                _resultTcs.TrySetException(ex);
            }

            return _resultTcs.Task;
        }

        /// <summary>Cancelar reconocimiento en curso</summary>
        public Task CancelAsync()
        {
            System.Diagnostics.Debug.WriteLine("[Vosk] CancelAsync llamado");
            _isRecording = false;
            _waveIn?.StopRecording();
            _resultTcs?.TrySetCanceled();
            return Task.CompletedTask;
        }

        /// <summary>Resetear servicio</summary>
        public async Task ResetAsync(string languageTag = "es-MX")
        {
            await _initLock.WaitAsync();
            try
            {
                System.Diagnostics.Debug.WriteLine("[Vosk] ResetAsync llamado");

                _isRecording = false;
                _isInitialized = false;

                try { _waveIn?.StopRecording(); } catch { }
                try { _waveIn?.Dispose(); } catch { }
                _waveIn = null;

                try { _recognizer?.Dispose(); } catch { }
                _recognizer = null;

                try { _model?.Dispose(); } catch { }
                _model = null;

                _currentLanguage = string.IsNullOrWhiteSpace(languageTag) ? "es-MX" : languageTag;
            }
            finally
            {
                _initLock.Release();
            }

            await InitializeAsync(_currentLanguage);
        }

        /// <summary>Obtener idioma actual</summary>
        public string GetCurrentLanguage()
        {
            return _currentLanguage;
        }

        /// <summary>Obtener idiomas disponibles</summary>
        public List<LanguageInfo> GetAvailableLanguages()
        {
            return new List<LanguageInfo>
            {
                new LanguageInfo
                {
                    Tag = "es-MX",
                    DisplayName = "Español (México) - Vosk",
                    NativeName = "Español"
                }
            };
        }

        /// <summary>Extraer texto del JSON de respuesta de Vosk</summary>
        private static string ExtractTextFromJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("text", out var textProp))
                {
                    return textProp.GetString()?.Trim() ?? "";
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Limpiar recursos</summary>
        public void Dispose()
        {
            _isRecording = false;

            try { _waveIn?.StopRecording(); } catch { }
            try { _waveIn?.Dispose(); } catch { }

            try { _recognizer?.Dispose(); } catch { }
            try { _model?.Dispose(); } catch { }

            _initLock?.Dispose();
        }
    }
}