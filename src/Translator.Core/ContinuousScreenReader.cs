using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Translator.Core;

public sealed class ContinuousScreenReader(IScreenCapture screen, IOpticalCharacterRecognizer ocr, OcrTuning tuning)
{

    public async IAsyncEnumerable<string> ReadChangesAsync(ScreenRegion region,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = session.Token;
        var frames = Channel.CreateBounded<ScreenFrame>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        Task capture = CaptureAsync();
        var clock = Stopwatch.StartNew();
        ScreenFrame? previousFrame = null;
        string? previousText = null;
        bool confirmEmpty = false;
        try
        {
            while (await NextFrameAsync(frames.Reader, previousFrame, confirmEmpty, clock, token).ConfigureAwait(false)
                is { } frame)
            {
                previousFrame = frame;
                string recognized = await ocr.RecognizeAsync(frame, token).ConfigureAwait(false);
                string text = string.Join('\n', recognized.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                if (text.Length == 0 && !string.IsNullOrEmpty(previousText) && !confirmEmpty)
                {
                    confirmEmpty = true;
                    continue;
                }

                confirmEmpty = false;
                if (text == previousText) continue;
                previousText = text;
                yield return text;
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await capture.ConfigureAwait(false);
        }

        async Task CaptureAsync()
        {
            try
            {
                await foreach (ScreenFrame frame in screen.WatchAsync(region, TimeSpan.FromMilliseconds(tuning.FallbackIntervalMs), token)
                    .WithCancellation(token).ConfigureAwait(false))
                    frames.Writer.TryWrite(frame);
                frames.Writer.TryComplete();
            }
            catch (Exception error) { frames.Writer.TryComplete(error); }
        }
    }

    private async Task<ScreenFrame?> NextFrameAsync(ChannelReader<ScreenFrame> frames,
        ScreenFrame? previous, bool confirmEmpty, Stopwatch clock, CancellationToken token)
    {
        ScreenFrame? candidate = null;
        long firstChange = 0;
        long latestChange = 0;
        long? confirmAt = confirmEmpty ? clock.ElapsedMilliseconds + tuning.BlankConfirmationMs : null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            long now = clock.ElapsedMilliseconds;
            if (frames.TryRead(out ScreenFrame? frame))
            {
                if (previous is null) return frame;
                if (SamePixels(previous, frame)) candidate = null;
                else if (candidate is null || !SamePixels(candidate, frame))
                {
                    if (candidate is null) firstChange = now;
                    candidate = frame;
                    latestChange = now;
                }
            }

            long? deadline = candidate is not null
                ? Math.Min(firstChange + tuning.MaxWaitMs, latestChange + tuning.QuietMs) : confirmAt;
            if (deadline is { } due && now >= due) return candidate ?? previous;

            if (deadline is null)
            {
                if (!await frames.WaitToReadAsync(token).ConfigureAwait(false)) return null;
                continue;
            }

            int remaining = (int)Math.Max(1, deadline.Value - clock.ElapsedMilliseconds);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
            timer.CancelAfter(remaining);
            try
            {
                if (!await frames.WaitToReadAsync(timer.Token).ConfigureAwait(false))
                {
                    if (candidate is not null) return candidate;
                    await Task.Delay(remaining, token).ConfigureAwait(false);
                    return previous;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
    }

    private static bool SamePixels(ScreenFrame first, ScreenFrame second) =>
        first.Region == second.Region && first.Stride == second.Stride && first.Pixels.Span.SequenceEqual(second.Pixels.Span);
}
