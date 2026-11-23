namespace Translator.Core;

public enum SourceLanguage
{
    Automatic,
    English,
    Japanese
}

public enum TranslationSource
{
    Selection,
    ContinuousOcr,
    AudioSubtitle
}

public sealed record TranslationRequest(
    string Text,
    SourceLanguage SourceLanguage,
    TranslationSource Source,
    IReadOnlyList<string> Context,
    string? DetectedLanguage = null);

public interface IStreamingTranslator
{
    // Each update contains all the text generated so far for this request.
    IAsyncEnumerable<string> StreamTranslationAsync(TranslationRequest request, CancellationToken cancellationToken);
}

public interface ISubtitlePrefill
{
    /// <summary>Opportunistically prepares the stable input in request.Text without publishing a
    /// translation. Implementations may skip this work when busy or unable to reuse it and should
    /// respect cancellation so that finalized subtitles can take priority.</summary>
    Task TryPrefillAsync(TranslationRequest request, CancellationToken cancellationToken);
}

public interface ITranslationService : IStreamingTranslator, IAsyncDisposable
{
    string Status { get; }
    Task PrepareAsync(CancellationToken cancellationToken);
    Task ReleaseAsync();
    Task ReleaseObsoleteModelAsync();
}

public sealed record SubtitleLine(long Id, SpeechSegment Speech, string Translation, bool IsRevised = false);

public abstract record SubtitleUpdate
{
    /// <summary>The provisional source text. An empty string clears the preview.</summary>
    public sealed record Preview(string Text) : SubtitleUpdate;
    public sealed record Lines(IReadOnlyList<SubtitleLine> Items) : SubtitleUpdate;
    public sealed record Speakers(SpeakerSnapshot Snapshot) : SubtitleUpdate;
    public sealed record SpeakersUnavailable(string Message) : SubtitleUpdate;
}

public interface ISubtitleParagraphTranslator
{
    Task<IReadOnlyDictionary<long, string>> ReviseAsync(IReadOnlyList<SubtitleLine> paragraph,
        SourceLanguage language, CancellationToken cancellationToken);
}
