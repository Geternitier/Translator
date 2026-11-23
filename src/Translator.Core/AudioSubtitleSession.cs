using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Translator.Core;

public sealed class AudioSubtitleSession(IApplicationAudioCapture capture, ISpeechTranscriber transcriber,
    IStreamingSubtitleTranslator translator, ISubtitleParagraphTranslator paragraphTranslator, AudioTuning tuning)
{

    public async IAsyncEnumerable<IReadOnlyList<SubtitleLine>> RunAsync(AudioSource source, SourceLanguage language,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = session.Token;
        var pending = Channel.CreateBounded<SpeechSegment>(new BoundedChannelOptions(tuning.PendingSegments)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        long firstFrameAt = 0;
        TimeSpan firstFrameEnd = TimeSpan.Zero;
        Task producer = Task.Run(ProduceAsync);
        var paragraph = new List<SubtitleLine>(tuning.ParagraphLines);
        var context = new Queue<string>(tuning.HistoryLines);
        TimeSpan shortBudget = TimeSpan.FromMilliseconds(tuning.ShortBudgetMs);
        TimeSpan refresh = TimeSpan.FromMilliseconds(tuning.RefreshMs);
        TimeSpan paragraphGap = TimeSpan.FromMilliseconds(tuning.ParagraphGapMs);
        long nextId = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (pending.Reader.TryRead(out SpeechSegment? speech))
                {
                    TimeSpan age = firstFrameAt == 0 ? TimeSpan.Zero
                        : Stopwatch.GetElapsedTime(firstFrameAt) + firstFrameEnd - speech.End;
                    TimeSpan remaining = age > TimeSpan.Zero ? shortBudget - age : shortBudget;
                    if (remaining <= TimeSpan.Zero) continue;
                    if (!TranslationText.NeedsTranslation(speech.Text, speech.DetectedLanguage)) continue;

                    using var shortLine = CancellationTokenSource.CreateLinkedTokenSource(token);
                    shortLine.CancelAfter(remaining);
                    string translation = "";
                    string displayed = "";
                    long lastDisplay = 0;
                    long id = nextId + 1;
                    var request = new TranslationRequest(speech.Text, language,
                        TranslationSource.AudioSubtitle, context.ToArray(), speech.DetectedLanguage);
                    await using (var updates = translator.StreamSubtitleAsync(request, shortLine.Token).GetAsyncEnumerator(shortLine.Token))
                    {
                        while (true)
                        {
                            try { if (!await updates.MoveNextAsync().ConfigureAwait(false)) break; }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
                            translation = updates.Current;
                            if (translation == displayed || string.IsNullOrWhiteSpace(translation)) continue;
                            if (lastDisplay != 0 && Stopwatch.GetElapsedTime(lastDisplay) < refresh)
                                continue;
                            displayed = translation;
                            lastDisplay = Stopwatch.GetTimestamp();
                            yield return new[] { new SubtitleLine(id, speech, displayed) };
                        }
                    }
                    if (string.IsNullOrWhiteSpace(translation)) continue;

                    if (context.Count == tuning.HistoryLines) context.Dequeue();
                    context.Enqueue(speech.Text);
                    nextId = id;
                    var line = new SubtitleLine(id, speech, translation);
                    if (paragraph.Count == tuning.ParagraphLines) paragraph.RemoveAt(0);
                    if (paragraph.Count != 0 && speech.Start - paragraph[^1].Speech.End > paragraphGap)
                        paragraph.Clear();
                    paragraph.Add(line);
                    if (translation != displayed) yield return new[] { line };
                    continue;
                }

                if (paragraph.Count < 2)
                {
                    if (!await pending.Reader.WaitToReadAsync(token).ConfigureAwait(false)) break;
                    continue;
                }

                if (paragraph.Count < tuning.ParagraphLines
                    && await HasIncomingSpeechAsync(pending.Reader, TimeSpan.FromMilliseconds(tuning.ParagraphDelayMs), token).ConfigureAwait(false))
                    continue;

                bool inputCompleted = pending.Reader.Completion.IsCompletedSuccessfully;
                IReadOnlyList<SubtitleLine>? revision = await ReviseWithoutBlockingAsync(paragraph.ToArray(),
                    language, pending.Reader, inputCompleted, token).ConfigureAwait(false);
                if (revision is null) continue; // A short line arrived during revision.
                paragraph.Clear();
                if (revision.Count != 0) yield return revision;
                if (inputCompleted) break;
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }

        async Task ProduceAsync()
        {
            try
            {
                await foreach (SpeechSegment segment in transcriber.TranscribeAsync(ReadAudioAsync(token), language, token)
                    .WithCancellation(token).ConfigureAwait(false))
                    pending.Writer.TryWrite(segment);
                pending.Writer.TryComplete();
            }
            catch (Exception exception) { pending.Writer.TryComplete(exception); }
        }

        async IAsyncEnumerable<AudioFrame> ReadAudioAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (AudioFrame frame in capture.CaptureAsync(source, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (firstFrameAt == 0)
                {
                    firstFrameEnd = frame.Offset + TimeSpan.FromSeconds(frame.Samples.Length / (double)AudioFrame.SampleRate);
                    firstFrameAt = Stopwatch.GetTimestamp();
                }
                yield return frame;
            }
        }
    }

    private static async Task<bool> HasIncomingSpeechAsync(ChannelReader<SpeechSegment> reader, TimeSpan delay,
        CancellationToken token)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(delay);
        try { return await reader.WaitToReadAsync(window.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private async Task<IReadOnlyList<SubtitleLine>?> ReviseWithoutBlockingAsync(SubtitleLine[] paragraph,
        SourceLanguage language, ChannelReader<SpeechSegment> reader, bool inputCompleted, CancellationToken token)
    {
        using var revision = CancellationTokenSource.CreateLinkedTokenSource(token);
        revision.CancelAfter(TimeSpan.FromMilliseconds(tuning.ParagraphBudgetMs));
        Task<IReadOnlyDictionary<long, string>> request = paragraphTranslator.ReviseAsync(paragraph, language, revision.Token);
        bool preempted = false;
        try
        {
            if (!inputCompleted)
            {
                using var arrival = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task<bool> next = reader.WaitToReadAsync(arrival.Token).AsTask();
                try
                {
                    preempted = await Task.WhenAny(request, next).ConfigureAwait(false) == next
                        && await next.ConfigureAwait(false);
                }
                finally
                {
                    await arrival.CancelAsync().ConfigureAwait(false);
                    if (!request.IsCompleted && (preempted || next.IsFaulted))
                        await revision.CancelAsync().ConfigureAwait(false);
                }
            }

            IReadOnlyDictionary<long, string> revised = await request.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (preempted) return null;
            return paragraph.Where(line => revised.TryGetValue(line.Id, out string? text) && !string.IsNullOrWhiteSpace(text))
                .Select(line => line with { Translation = revised[line.Id], IsRevised = true }).ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return preempted ? null : [];
        }
        catch (Exception) when (!token.IsCancellationRequested && !reader.Completion.IsFaulted)
        {
            return preempted ? null : []; // Keep the displayed short translations if optional refinement fails.
        }
        finally
        {
            if (!request.IsCompleted) await revision.CancelAsync().ConfigureAwait(false);
        }
    }
}
