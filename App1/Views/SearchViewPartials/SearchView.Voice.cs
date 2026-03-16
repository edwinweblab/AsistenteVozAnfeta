using Anfeta.UI.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Comandos de Voz =====

        public Task ExecuteSearchTextFromExternalAsync(string text)
        {
            _allowProgrammaticSearch = true;
            SearchBox.Text = text ?? "";
            SearchBox.Focus(FocusState.Programmatic);
            TriggerSearchFromHelp(SearchBox.Text);
            _allowProgrammaticSearch = false;
            return Task.CompletedTask;
        }

        public Task ExecuteSearchTextAsync(string text)
            => ExecuteSearchTextFromExternalAsync(text);

        private async void VoiceMenu_Config_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VoiceCommandsDialog(_repo, _voiceEngine)
            {
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
            await _voiceEngine.ReloadAsync();
        }

        private void SetListeningUi(bool listening)
        {
            _isListening = listening;

            VoiceRing.IsActive = listening;
            VoiceRing.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
            VoiceSplit.IsEnabled = true;

            if (listening)
            {
                VoiceSplit.Background = _voiceActiveBg;
                VoiceSplit.Foreground = _voiceActiveFg;
                StatusText.Text = "Estado: 🎙 Escuchando…";
            }
            else
            {
                VoiceSplit.Background = _voiceSplitDefaultBg;
                VoiceSplit.Foreground = _voiceSplitDefaultFg;
                StatusText.Text = "Estado: Listo";
            }
        }

        private async void VoiceSplit_Click(SplitButton sender, SplitButtonClickEventArgs args)
        {
            if (_isListening)
                await CancelVoiceAsync();
            else
                await StartVoiceAsync();
        }

        private async void VoiceMenu_Listen_Click(object sender, RoutedEventArgs e)
        {
            if (_isListening)
                await CancelVoiceAsync();
            else
                await StartVoiceAsync();
        }

        private async Task StartVoiceAsync()
        {
            if (_isListening) return;

            _isListening = true;
            _voiceCts?.Dispose();
            _voiceCts = new CancellationTokenSource();

            SetListeningUi(true);
            VoiceDebugText.Text = "🎙️ Escuchando...";

            try
            {
                var res = await _voiceOrchestrator.ListenAndExecuteAsync(this, _voiceCts.Token);

                VoiceDebugText.Text = string.IsNullOrWhiteSpace(res?.Phrase)
                    ? "🎙️ Sin resultado"
                    : (res.Matched
                        ? $"✅ '{res.Phrase}' → {res.CommandName} ({res.Token})"
                        : $"❓ '{res.Phrase}' (sin match)");
            }
            catch (OperationCanceledException)
            {
                VoiceDebugText.Text = "🎙️ Cancelado";
            }
            finally
            {
                SetListeningUi(false);
                _isListening = false;
                _voiceCts?.Dispose();
                _voiceCts = null;
            }
        }

        private async Task CancelVoiceAsync()
        {
            if (!_isListening) return;

            try
            {
                _voiceCts?.Cancel();
                await _voicePost.StopAllAsync();
            }
            catch { }
            finally
            {
                _isListening = false;
                SetListeningUi(false);
                VoiceDebugText.Text = "🎙️ Cancelado";
                _voiceCts?.Dispose();
                _voiceCts = null;
            }
        }

        private void SetVoiceHeard(string? phrase)
        {
            VoiceDebugText.Text = string.IsNullOrWhiteSpace(phrase)
                ? "Voz: (no se entendió nada)"
                : $"Voz entendió: \"{phrase}\"";
        }

        #endregion

        #region ===== Dictado de Resultados =====

        private void Dictation_SetResults(IReadOnlyList<Anfeta.UI.Models.Weblab.SearchResultRow> rows)
        {
            _dictList = rows ?? Array.Empty<Anfeta.UI.Models.Weblab.SearchResultRow>();
            _dictIndex = 0;
            _dictPlaying = false;
            _dictCts?.Cancel();
            DispatcherQueue.TryEnqueue(() => UpdatePlayPauseIcon(false));
        }

        private async Task Dictation_SpeakCurrentAsync(CancellationToken ct)
        {
            if (_dictList.Count == 0) return;

            if (_dictIndex < 0) _dictIndex = 0;
            if (_dictIndex >= _dictList.Count) _dictIndex = _dictList.Count - 1;

            var row = _dictList[_dictIndex];
            var text = (row?.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text)) text = "Elemento sin nombre";

            ct.ThrowIfCancellationRequested();

            var stream = await _dictSynth.SynthesizeTextToStreamAsync(text);

            ct.ThrowIfCancellationRequested();

            _dictPlayer ??= new MediaPlayer();
            _dictPlayer.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _dictPlayer.Play();
        }

        private async Task WaitForMediaEndAsync(CancellationToken ct)
        {
            if (_dictPlayer == null) return;

            var tcs = new TaskCompletionSource<object?>();

            void OnEnded(MediaPlayer s, object a) => tcs.TrySetResult(null);
            void OnFailed(MediaPlayer s, MediaPlayerFailedEventArgs a) => tcs.TrySetResult(null);

            _dictPlayer.MediaEnded += OnEnded;
            _dictPlayer.MediaFailed += OnFailed;

            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

            try { await tcs.Task; }
            finally
            {
                _dictPlayer.MediaEnded -= OnEnded;
                _dictPlayer.MediaFailed -= OnFailed;
            }
        }

        private async Task Dictation_PlayAsync()
        {
            if (_dictList.Count == 0)
            {
                StatusText.Text = "Estado: No hay resultados para dictar";
                return;
            }

            _dictCts?.Cancel();
            _dictCts = new CancellationTokenSource();
            var ct = _dictCts.Token;

            _dictPlaying = true;

            try
            {
                try { await _voicePost.StopAllAsync(); } catch { }

                while (_dictPlaying && !ct.IsCancellationRequested)
                {
                    await Dictation_SpeakCurrentAsync(ct);

                    while (_dictPlayer != null &&
                           _dictPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(80, ct);
                    }

                    await Dictation_SpeakCurrentAsync(ct);
                    await WaitForMediaEndAsync(ct);

                    _dictIndex++;

                    if (_dictIndex >= _dictList.Count)
                    {
                        _dictPlaying = false;
                        _dictIndex = _dictList.Count - 1;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void Dictation_Pause()
        {
            _dictPlaying = false;
            _dictCts?.Cancel();
            try { _dictPlayer?.Pause(); } catch { }
            UpdatePlayPauseIcon(false);
        }

        private async void BtnDictPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_dictPlaying)
            {
                Dictation_Pause();
            }
            else
            {
                UpdatePlayPauseIcon(true);
                await Dictation_PlayAsync();
                UpdatePlayPauseIcon(false);
            }
        }

        // Alias para compatibilidad con handlers XAML que usen BtnDictPlayPause_Click
        private async void BtnDictPlayPause_Click(object sender, RoutedEventArgs e)
            => await Task.Run(() => BtnDictPlay_Click(sender, e));

        private void BtnDictPause_Click(object sender, RoutedEventArgs e)
            => Dictation_Pause();

        private async void BtnDictNext_Click(object sender, RoutedEventArgs e)
        {
            if (_dictList.Count == 0) return;
            Dictation_Pause();
            _dictIndex = Math.Min(_dictIndex + 1, _dictList.Count - 1);
            _dictCts?.Cancel();
            _dictCts = new CancellationTokenSource();
            await Dictation_SpeakCurrentAsync(_dictCts.Token);
        }

        private async void BtnDictPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_dictList.Count == 0) return;
            Dictation_Pause();
            _dictIndex = Math.Max(_dictIndex - 1, 0);
            _dictCts?.Cancel();
            _dictCts = new CancellationTokenSource();
            await Dictation_SpeakCurrentAsync(_dictCts.Token);
        }

        private void UpdatePlayPauseIcon(bool playing)
        {
            if (BtnSpeechPlay.Content is SymbolIcon icon)
                icon.Symbol = playing ? Symbol.Pause : Symbol.Play;

            BtnSpeechPlay.IsChecked = playing;
            ToolTipService.SetToolTip(BtnSpeechPlay, playing ? "Pausar" : "Reproducir");
        }

        #endregion
    }
}
