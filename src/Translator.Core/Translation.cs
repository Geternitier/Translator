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

public interface IStreamingSubtitleTranslator
{
    // Each update contains all the text generated so far for this request.
    IAsyncEnumerable<string> StreamSubtitleAsync(TranslationRequest request, CancellationToken cancellationToken);
}

public sealed record SubtitleLine(long Id, SpeechSegment Speech, string Translation, bool IsRevised = false);

public interface ISubtitleParagraphTranslator
{
    Task<IReadOnlyDictionary<long, string>> ReviseAsync(IReadOnlyList<SubtitleLine> paragraph,
        SourceLanguage language, CancellationToken cancellationToken);
}
