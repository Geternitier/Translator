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

/// <summary>A snapshot of the current utterance, with timestamps relative to the PCM stream.</summary>
/// <param name="StablePrefixLength">Length in UTF-16 code units of the prefix shared by subsequent
/// snapshots of this utterance. Use zero when the recognizer cannot guarantee a stable prefix.</param>
public sealed record SpeechRecognitionUpdate(SpeechSegment Speech, bool IsFinal, int StablePrefixLength = 0);

public interface ISpeechTranscriber
{
    /// <summary>Reports utterances in order. A final update closes the current utterance.
    /// Recognizers that only produce final results can omit intermediate updates.</summary>
    IAsyncEnumerable<SpeechRecognitionUpdate> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio, SourceLanguage language, CancellationToken cancellationToken);
}
