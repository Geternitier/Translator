using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Translator.Core;

public sealed class AudioSubtitleSession(ISpeechTranscriber transcriber, IStreamingTranslator translator,
    ISubtitleParagraphTranslator? paragraphTranslator, AudioTuning tuning, ISpeakerDiarizer? speakerDiarizer = null)
{
    public async IAsyncEnumerable<SubtitleUpdate> RunAsync(IAsyncEnumerable<AudioFrame> audio, SourceLanguage language,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = session.Token;
        var pending = Channel.CreateBounded<SpeechSegment>(new BoundedChannelOptions(tuning.PendingSegments)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        // Wake the translation worker when recognition changes. Only the latest preview is needed.
        var activity = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite
        });
        var output = Channel.CreateBounded<SubtitleUpdate>(new BoundedChannelOptions(16) { SingleReader = true });
        // Speaker processing must never apply backpressure to transcription.
        var speakerAudio = speakerDiarizer is null ? null : Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(128)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        var prefill = tuning.PrefillBudgetMs > 0 ? translator as ISubtitlePrefill : null;
        TimeSpan refresh = TimeSpan.FromMilliseconds(tuning.RefreshMs);
        TimeSpan shortBudget = TimeSpan.FromMilliseconds(tuning.ShortBudgetMs);
        TimeSpan paragraphDelay = TimeSpan.FromMilliseconds(tuning.ParagraphDelayMs);
        TimeSpan paragraphGap = TimeSpan.FromMilliseconds(tuning.ParagraphGapMs);
        var paragraph = new List<SubtitleLine>(tuning.ParagraphLines);
        var context = new Queue<string>(tuning.HistoryLines);
        LiveUtterance? live = null;
        AudioClock? clock = null;
        long lastPrefill = 0;
        long lastLine = 0;
        long lastTranslationAt = 0;

        Task speakers = speakerDiarizer is null ? Task.CompletedTask : Task.Run(TrackSpeakersAsync);
        Task recognition = Task.Run(RecognizeAsync);
        Task translation = Task.Run(TranslateAsync);
        try
        {
            await foreach (SubtitleUpdate update in output.Reader.ReadAllAsync(token).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(recognition, translation, speakers).ConfigureAwait(false);
        }

        async Task RecognizeAsync()
        {
            long utteranceId = 1;
            string displayed = "";
            long displayedAt = 0;
            try
            {
                await foreach (SpeechRecognitionUpdate update in transcriber.TranscribeAsync(ReadAudioAsync(token), language, token)
                    .WithCancellation(token).ConfigureAwait(false))
                {
                    string text = update.Speech.Text;
                    if (update.IsFinal)
                    {
                        Volatile.Write(ref live, null);
                        pending.Writer.TryWrite(update.Speech);
                        activity.Writer.TryWrite(true);
                        utteranceId++;
                        await ClearPreviewAsync().ConfigureAwait(false);
                        continue;
                    }

                    Volatile.Write(ref live, string.IsNullOrWhiteSpace(text) ? null : new LiveUtterance(utteranceId, update));
                    activity.Writer.TryWrite(true);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await ClearPreviewAsync().ConfigureAwait(false);
                        continue;
                    }
                    if (text != displayed && (displayedAt == 0 || Stopwatch.GetElapsedTime(displayedAt) >= refresh)
                        && output.Writer.TryWrite(new SubtitleUpdate.Preview(text)))
                    {
                        displayed = text;
                        displayedAt = Stopwatch.GetTimestamp();
                    }
                }
                Volatile.Write(ref live, null);
                await ClearPreviewAsync().ConfigureAwait(false);
                pending.Writer.TryComplete();
            }
            catch (Exception error) { pending.Writer.TryComplete(error); }
            finally
            {
                speakerAudio?.Writer.TryComplete();
                activity.Writer.TryWrite(true);
            }

            async ValueTask ClearPreviewAsync()
            {
                if (displayed.Length == 0) return;
                await output.Writer.WriteAsync(new SubtitleUpdate.Preview(""), token).ConfigureAwait(false);
                displayed = "";
                displayedAt = 0;
            }
        }

        async IAsyncEnumerable<AudioFrame> ReadAudioAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (AudioFrame frame in audio.WithCancellation(ct).ConfigureAwait(false))
            {
                if (clock is null)
                {
                    TimeSpan end = frame.Offset + TimeSpan.FromSeconds(frame.Samples.Length / (double)AudioFrame.SampleRate);
                    Volatile.Write(ref clock, new AudioClock(Stopwatch.GetTimestamp(), end));
                }
                speakerAudio?.Writer.TryWrite(frame);
                yield return frame;
            }
        }

        async Task TrackSpeakersAsync()
        {
            try
            {
                await foreach (SpeakerSnapshot snapshot in speakerDiarizer!.TrackAsync(speakerAudio!.Reader.ReadAllAsync(token), token)
                    .WithCancellation(token).ConfigureAwait(false))
                    await output.Writer.WriteAsync(new SubtitleUpdate.Speakers(snapshot), token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                    output.Writer.TryWrite(new SubtitleUpdate.SpeakersUnavailable(error.Message));
            }
        }

        async Task TranslateAsync()
        {
            try
            {
                await TranslatePendingAsync().ConfigureAwait(false);
                await speakers.ConfigureAwait(false);
                output.Writer.TryComplete();
            }
            catch (Exception error) { output.Writer.TryComplete(error); }
        }

        async Task TranslatePendingAsync()
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                activity.Reader.TryRead(out _);
                if (pending.Reader.TryRead(out SpeechSegment? speech))
                {
                    await TranslateLineAsync(speech).ConfigureAwait(false);
                    continue;
                }

                if (pending.Reader.Completion.IsCompleted)
                {
                    await pending.Reader.Completion.ConfigureAwait(false);
                    if (paragraph.Count >= 2) await ReviseAsync(static () => false).ConfigureAwait(false);
                    return;
                }

                LiveUtterance? current = Volatile.Read(ref live);
                if (current is not null && prefill is not null && current.Id != lastPrefill)
                {
                    SpeechRecognitionUpdate update = current.Update;
                    int prefixLength = Math.Clamp(update.StablePrefixLength, 0, update.Speech.Text.Length);
                    string prefix = update.Speech.Text[..prefixLength];
                    if (TranslationText.NeedsTranslation(prefix, update.Speech.DetectedLanguage))
                    {
                        lastPrefill = current.Id;
                        var request = CreateRequest(update.Speech with { Text = prefix });
                        await RunOptionalAsync(ct => prefill.TryPrefillAsync(request, ct),
                            () => pending.Reader.TryPeek(out _) || Volatile.Read(ref live)?.Id != current.Id,
                            activity.Reader, TimeSpan.FromMilliseconds(tuning.PrefillBudgetMs), token).ConfigureAwait(false);
                        continue;
                    }
                }

                if (current is not null || paragraph.Count < 2)
                {
                    await activity.Reader.ReadAsync(token).ConfigureAwait(false);
                    continue;
                }

                TimeSpan remainingDelay = paragraphDelay - Stopwatch.GetElapsedTime(lastTranslationAt);
                if (paragraph.Count < tuning.ParagraphLines && remainingDelay > TimeSpan.Zero)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                    wait.CancelAfter(remainingDelay);
                    try { await activity.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    continue;
                }

                await ReviseAsync(() => pending.Reader.TryPeek(out _) || Volatile.Read(ref live) is not null
                    || pending.Reader.Completion.IsFaulted).ConfigureAwait(false);
            }
        }

        TranslationRequest CreateRequest(SpeechSegment speech) =>
            new(speech.Text, language, TranslationSource.AudioSubtitle, context.ToArray(), speech.DetectedLanguage);

        async Task TranslateLineAsync(SpeechSegment speech)
        {
            AudioClock? start = Volatile.Read(ref clock);
            TimeSpan age = start is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(start.Timestamp) + start.AudioEnd - speech.End;
            TimeSpan remaining = age > TimeSpan.Zero ? shortBudget - age : shortBudget;
            if (remaining <= TimeSpan.Zero || !TranslationText.NeedsTranslation(speech.Text, speech.DetectedLanguage)) return;

            using var shortLine = CancellationTokenSource.CreateLinkedTokenSource(token);
            shortLine.CancelAfter(remaining);
            string translation = "";
            string displayed = "";
            long displayedAt = 0;
            long id = lastLine + 1;
            TranslationRequest request = CreateRequest(speech);
            try
            {
                await foreach (string update in translator.StreamTranslationAsync(request, shortLine.Token)
                    .WithCancellation(shortLine.Token).ConfigureAwait(false))
                {
                    translation = update;
                    if (update == displayed || string.IsNullOrWhiteSpace(update)) continue;
                    if (displayedAt != 0 && Stopwatch.GetElapsedTime(displayedAt) < refresh) continue;
                    displayed = update;
                    displayedAt = Stopwatch.GetTimestamp();
                    await PublishAsync([new SubtitleLine(id, speech, update)]).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            if (string.IsNullOrWhiteSpace(translation)) return;

            if (context.Count == tuning.HistoryLines) context.Dequeue();
            context.Enqueue(speech.Text);
            lastLine = id;
            var line = new SubtitleLine(id, speech, translation);
            if (paragraphTranslator is not null)
            {
                if (paragraph.Count == tuning.ParagraphLines) paragraph.RemoveAt(0);
                if (paragraph.Count != 0 && speech.Start - paragraph[^1].Speech.End > paragraphGap) paragraph.Clear();
                paragraph.Add(line);
                lastTranslationAt = Stopwatch.GetTimestamp();
            }
            if (translation != displayed) await PublishAsync([line]).ConfigureAwait(false);
        }

        async Task ReviseAsync(Func<bool> hasPriorityWork)
        {
            if (paragraphTranslator is null) return;
            var lines = paragraph.ToArray();
            IReadOnlyDictionary<long, string>? revised = null;
            bool keptPriority = await RunOptionalAsync(async ct =>
            {
                revised = await paragraphTranslator.ReviseAsync(lines, language, ct).ConfigureAwait(false);
            }, hasPriorityWork, activity.Reader, TimeSpan.FromMilliseconds(tuning.ParagraphBudgetMs), token).ConfigureAwait(false);
            if (!keptPriority) return;

            paragraph.Clear();
            if (revised is null) return;
            SubtitleLine[] updates = lines.Where(line => revised.TryGetValue(line.Id, out string? text)
                && !string.IsNullOrWhiteSpace(text))
                .Select(line => line with { Translation = revised[line.Id], IsRevised = true }).ToArray();
            if (updates.Length != 0) await PublishAsync(updates).ConfigureAwait(false);
        }

        ValueTask PublishAsync(IReadOnlyList<SubtitleLine> lines) =>
            output.Writer.WriteAsync(new SubtitleUpdate.Lines(lines), token);
    }

    private static async Task<bool> RunOptionalAsync(Func<CancellationToken, Task> work, Func<bool> hasPriorityWork,
        ChannelReader<bool> activity, TimeSpan budget, CancellationToken token)
    {
        if (hasPriorityWork()) return false;

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(token);
        operation.CancelAfter(budget);
        Task watcher = WatchAsync();
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            await work(operation.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
        finally
        {
            await watch.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        return !hasPriorityWork();

        async Task WatchAsync()
        {
            try
            {
                while (!hasPriorityWork())
                    await activity.ReadAsync(watch.Token).ConfigureAwait(false);
                await operation.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (watch.IsCancellationRequested) { }
        }
    }

    private sealed record LiveUtterance(long Id, SpeechRecognitionUpdate Update);
    private sealed record AudioClock(long Timestamp, TimeSpan AudioEnd);
}
