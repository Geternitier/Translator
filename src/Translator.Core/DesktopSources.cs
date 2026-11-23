namespace Translator.Core;

/// <summary>A rectangle in physical pixels relative to the selected display's origin.</summary>
public readonly record struct ScreenRegion(string DisplayId, int X, int Y, int Width, int Height);

public sealed record SelectedText(string Text, ScreenRegion? Region);

public interface ITextSelectionSource
{
    IAsyncEnumerable<SelectedText?> WatchAsync(CancellationToken cancellationToken);
}

/// <summary>Top-down BGRA32 pixels. Stride is measured in bytes.</summary>
public sealed record ScreenFrame(ScreenRegion Region, int Stride, ReadOnlyMemory<byte> Pixels);

public interface IScreenCapture
{
    ValueTask<ScreenFrame> CaptureAsync(ScreenRegion region, CancellationToken cancellationToken);
    /// <summary>Watches a region. The sampling interval is the cadence for polling backends.</summary>
    IAsyncEnumerable<ScreenFrame> WatchAsync(
        ScreenRegion region, TimeSpan samplingInterval, CancellationToken cancellationToken);
}

public interface IOpticalCharacterRecognizer
{
    ValueTask<string> RecognizeAsync(
        ScreenFrame frame, CancellationToken cancellationToken);
}

public sealed record AudioSource(string Id, string DisplayName, bool IsActive = false);

public interface IAudioSourceCatalog
{
    IReadOnlyList<AudioSource> ListSources();
}

/// <summary>16 kHz mono PCM samples. Offset is relative to the start of the capture session.</summary>
public sealed record AudioFrame(TimeSpan Offset, ReadOnlyMemory<short> Samples)
{
    public const int SampleRate = 16_000;
}

public interface IApplicationAudioCapture
{
    IAsyncEnumerable<AudioFrame> CaptureAsync(AudioSource source, CancellationToken cancellationToken);
}

public sealed record SpeechSegment(TimeSpan Start, TimeSpan End, string Text, string? DetectedLanguage = null);

public interface ISpeechTranscriber
{
    IAsyncEnumerable<SpeechSegment> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio, SourceLanguage language, CancellationToken cancellationToken);
}
