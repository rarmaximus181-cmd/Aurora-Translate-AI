using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace AuroraTranslate.Services;

/// <summary>Uses Windows speech and installed offline Piper voices; audio stays on this PC.</summary>
public sealed class VoiceService : IDisposable
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private readonly HashSet<CancellationTokenSource> _operations = [];
    private readonly VoiceLibraryService _voiceLibrary = new();
    private bool _disposed;

    public IReadOnlyList<VoiceOption> GetVoices()
    {
        ThrowIfDisposed();
        var local = _voiceLibrary.GetInstalledVoices().Select(v =>
            new VoiceOption("piper:" + v.Id, $"{v.Name} · Piper ({v.Language})", v.Language)).ToList();
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            local.AddRange(synthesizer.GetInstalledVoices().Where(v => v.Enabled)
                .Select(v => new VoiceOption(v.VoiceInfo.Id, v.VoiceInfo.Name, v.VoiceInfo.Culture.Name))
                .ToArray());
        }
        catch (Exception e) when (e is InvalidOperationException or COMException or PlatformNotSupportedException)
        {
            if (local.Count == 0)
                throw new InvalidOperationException("Speech voices are unavailable. Download or import a Piper voice in Aurora Settings, or add a Windows voice in Settings > Time & language > Speech.", e);
        }
        return local.OrderBy(v => v.Language).ThenBy(v => v.Name).ToArray();
    }

    public IReadOnlyList<string> GetRecognitionLanguages()
    {
        ThrowIfDisposed();
        try
        {
            return SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        }
        catch (Exception e) when (e is InvalidOperationException or COMException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException("Windows speech recognition is unavailable. Install speech recognition for your language in Windows Settings.", e);
        }
    }

    public Task SpeakAsync(string text, string language, string? voiceId = null, int rate = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        if (text.Length > 30_000) throw new ArgumentException("Please read aloud no more than 30,000 characters at a time.", nameof(text));
        var culture = ResolveCulture(language);
        return RunAudioAsync(async token =>
        {
            var voices = GetVoices();
            var chosen = string.IsNullOrWhiteSpace(voiceId) ? null
                : voices.FirstOrDefault(v => v.Id.Equals(voiceId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(voiceId) && chosen is null)
                throw new InvalidOperationException("Your selected voice is no longer available. Choose another voice or Automatic in Settings.");
            if (chosen is not null && !Matches(CultureInfo.GetCultureInfo(chosen.Language), culture))
                throw new InvalidOperationException($"Your selected voice speaks {CultureInfo.GetCultureInfo(chosen.Language).EnglishName}. Choose a {culture.EnglishName} voice or Automatic in Settings.");
            chosen ??= voices.Where(v => !v.Id.StartsWith("piper:", StringComparison.OrdinalIgnoreCase) && Matches(CultureInfo.GetCultureInfo(v.Language), culture))
                .OrderByDescending(v => v.Language.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
                ?? voices.Where(v => v.Id.StartsWith("piper:", StringComparison.OrdinalIgnoreCase) && Matches(CultureInfo.GetCultureInfo(v.Language), culture))
                    .OrderByDescending(v => v.Language.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (chosen is null)
                throw new InvalidOperationException($"No {culture.EnglishName} speaking voice is installed. Download or import one in Aurora Settings, or add a Windows speech voice.");
            if (chosen.Id.StartsWith("piper:", StringComparison.OrdinalIgnoreCase))
            {
                var local = _voiceLibrary.GetInstalledVoices().FirstOrDefault(v => ("piper:" + v.Id).Equals(chosen.Id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("This voice is no longer installed. Choose another voice in Settings.");
                await SpeakPiperAsync(text, local, rate, token).ConfigureAwait(false);
                return true;
            }
            using var synthesizer = new SpeechSynthesizer();
            var selected = synthesizer.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Id.Equals(chosen.Id, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                throw new InvalidOperationException("Your selected Windows voice is no longer available. Choose another voice in Settings.");

            synthesizer.SelectVoice(selected.VoiceInfo.Name);
            synthesizer.Rate = Math.Clamp(rate, -10, 10);
            synthesizer.SetOutputToDefaultAudioDevice();
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<SpeakCompletedEventArgs> handler = (_, e) =>
            {
                if (e.Cancelled || token.IsCancellationRequested) completed.TrySetCanceled(token);
                else if (e.Error is not null) completed.TrySetException(new InvalidOperationException("Windows could not play speech. Check your selected speaker or headset.", e.Error));
                else completed.TrySetResult(true);
            };
            synthesizer.SpeakCompleted += handler;
            using var registration = token.Register(() =>
            {
                // Complete the task even if an audio driver never delivers its completion event.
                completed.TrySetCanceled(token);
                try { synthesizer.SpeakAsyncCancelAll(); }
                catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or COMException) { }
            });
            try
            {
                token.ThrowIfCancellationRequested();
                synthesizer.SpeakAsync(text);
                await completed.Task.ConfigureAwait(false);
                return true;
            }
            finally
            {
                synthesizer.SpeakCompleted -= handler;
                if (token.IsCancellationRequested)
                {
                    try { synthesizer.SpeakAsyncCancelAll(); }
                    catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or COMException) { }
                }
            }
        }, cancellationToken);
    }

    private async Task SpeakPiperAsync(string text, LocalVoiceInfo voice, int rate, CancellationToken token)
    {
        var path = await _voiceLibrary.SynthesizeToFileAsync(text, voice, rate, token).ConfigureAwait(false);
        try
        {
            using var player = new SoundPlayer(path) { LoadTimeout = 10_000 };
            using var cancel = token.Register(() =>
            {
                try { player.Stop(); }
                catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { }
            });
            token.ThrowIfCancellationRequested();
            player.Load();
            token.ThrowIfCancellationRequested();
            // RunAudioAsync already schedules audio work off the UI thread. Stop cancels synchronous playback.
            player.PlaySync();
            token.ThrowIfCancellationRequested();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException)
        { throw new InvalidOperationException("Speech could not play. Check your selected speaker or headset.", e); }
        finally
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public Task<string> ListenAsync(string language, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var culture = ResolveCulture(language);
        var duration = TimeSpan.FromMilliseconds(Math.Clamp((timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds, 1_000, 20_000));
        return RunAudioAsync(async token =>
        {
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            var selected = recognizers.FirstOrDefault(r => r.Culture.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase))
                ?? recognizers.FirstOrDefault(r => Matches(r.Culture, culture));
            if (selected is null)
                throw new InvalidOperationException($"No {culture.EnglishName} speech recognizer is installed. Add that language's Speech feature in Windows Settings > Time & language > Language & region, then restart Aurora Translate.");

            using var recognizer = new SpeechRecognitionEngine(selected);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            var listenToken = deadline.Token;
            recognizer.InitialSilenceTimeout = duration;
            recognizer.BabbleTimeout = duration;
            recognizer.EndSilenceTimeout = TimeSpan.FromMilliseconds(650);
            recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(900);
            try
            {
                recognizer.LoadGrammar(new DictationGrammar());
                recognizer.SetInputToDefaultAudioDevice();
            }
            catch (Exception e) when (e is InvalidOperationException or COMException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Windows could not open the microphone. Connect a microphone, set it as the Windows default input, and allow desktop apps microphone access in Settings > Privacy & security > Microphone.", e);
            }

            var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<RecognizeCompletedEventArgs> handler = (_, e) =>
            {
                if (e.Cancelled || listenToken.IsCancellationRequested) completed.TrySetCanceled(listenToken);
                else if (e.Error is not null) completed.TrySetException(new InvalidOperationException("Speech recognition failed. Check your microphone and Windows speech language settings.", e.Error));
                else if (e.InitialSilenceTimeout || e.BabbleTimeout) completed.TrySetException(new TimeoutException("No clear speech was heard. Tap the microphone and try again."));
                else if (e.Result is null || e.Result.Confidence < 0.35f || string.IsNullOrWhiteSpace(e.Result.Text))
                    completed.TrySetException(new InvalidOperationException("I couldn't understand that clearly. Move closer to the microphone and try again."));
                else completed.TrySetResult(e.Result.Text.Trim());
            };
            recognizer.RecognizeCompleted += handler;
            using var registration = listenToken.Register(() =>
            {
                completed.TrySetCanceled(listenToken);
                try { recognizer.RecognizeAsyncCancel(); }
                catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or COMException) { }
            });
            try
            {
                token.ThrowIfCancellationRequested();
                deadline.CancelAfter(duration);
                recognizer.RecognizeAsync(RecognizeMode.Single);
                return await completed.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Listening timed out. Tap the microphone and try again.");
            }
            finally
            {
                recognizer.RecognizeCompleted -= handler;
                if (listenToken.IsCancellationRequested)
                {
                    try { recognizer.RecognizeAsyncCancel(); }
                    catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or COMException) { }
                }
            }
        }, cancellationToken);
    }

    public void Stop()
    {
        CancellationTokenSource[] pending;
        lock (_stateLock) pending = _operations.ToArray();
        foreach (var operation in pending)
        {
            try { operation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<T> RunAudioAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        CancellationTokenSource operation;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _operations.Add(operation);
        }
        var acquired = false;
        try
        {
            await _audioGate.WaitAsync(operation.Token).ConfigureAwait(false);
            acquired = true;
            // Construction, audio setup and disposal can call native drivers, so keep all of them off the UI thread.
            return await Task.Run(() => action(operation.Token), operation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) _audioGate.Release();
            lock (_stateLock)
            {
                _operations.Remove(operation);
                if (_disposed && _operations.Count == 0) _audioGate.Dispose();
            }
            operation.Dispose();
        }
    }

    private static CultureInfo ResolveCulture(string language)
    {
        var option = Languages.All.FirstOrDefault(x => x.Code.Equals(language, StringComparison.OrdinalIgnoreCase)
            || x.Culture.Equals(language, StringComparison.OrdinalIgnoreCase));
        try { return CultureInfo.GetCultureInfo(option?.Culture ?? language); }
        catch (CultureNotFoundException e) { throw new ArgumentException("Choose a supported speech language.", nameof(language), e); }
    }

    private static bool Matches(CultureInfo installed, CultureInfo requested) =>
        installed.TwoLetterISOLanguageName.Equals(requested.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase);

    private void ThrowIfDisposed()
    {
        lock (_stateLock) ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_operations.Count == 0) _audioGate.Dispose();
        }
        Stop();
        _voiceLibrary.Dispose();
    }
}
