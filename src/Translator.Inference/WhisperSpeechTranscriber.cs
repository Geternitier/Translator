using System.Runtime.CompilerServices;
using Translator.Core;
using Whisper.net;

namespace Translator.Inference;

public sealed class WhisperSpeechTranscriber(string modelPath, WhisperTuning tuning) : ISpeechTranscriber
{
    private const int SampleRate = AudioFrame.SampleRate;

    public async IAsyncEnumerable<SpeechRecognitionUpdate> TranscribeAsync(IAsyncEnumerable<AudioFrame> audio, SourceLanguage language,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("缺少 Whisper 模型，请运行 scripts/setup-runtime.ps1。", modelPath);
        using var factory = WhisperFactory.FromPath(modelPath);
        string requestedLanguage = language switch
        {
            SourceLanguage.English => "en",
            SourceLanguage.Japanese => "ja",
            _ => "auto"
        };
        await using var processor = factory.CreateBuilder().WithLanguage(requestedLanguage)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, tuning.MaxThreads)).Build();

        var segments = new SpeechSegmentation(tuning.PreRollMs, tuning.MinSpeechMs, tuning.TrailingSilenceMs,
            tuning.MaxSegmentMs, tuning.DiscontinuityMs, tuning.VoiceThreshold);
        await foreach (SpeechAudioSegment segment in segments.ReadAsync(audio, cancellationToken, SampleRate).ConfigureAwait(false))
        {
            await foreach (SegmentData result in processor.ProcessAsync(segment.Samples, cancellationToken).ConfigureAwait(false))
            {
                string text = result.Text.Trim();
                if (text.Length != 0)
                    yield return new SpeechRecognitionUpdate(
                        new SpeechSegment(segment.Start + result.Start, segment.Start + result.End, text,
                            language == SourceLanguage.Automatic ? result.Language : requestedLanguage), IsFinal: true);
            }
        }
    }
}
