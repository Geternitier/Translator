using System.Runtime.CompilerServices;
using Translator.Core;
using Whisper.net;

namespace Translator.Inference;

public sealed class WhisperSpeechTranscriber(string modelPath, WhisperTuning tuning) : ISpeechTranscriber
{
    private const int SampleRate = AudioFrame.SampleRate;

    public async IAsyncEnumerable<SpeechSegment> TranscribeAsync(IAsyncEnumerable<AudioFrame> audio, SourceLanguage language,
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

        await foreach ((TimeSpan offset, float[] samples) in Segment(audio, cancellationToken).ConfigureAwait(false))
        {
            await foreach (SegmentData result in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                string text = result.Text.Trim();
                if (text.Length != 0)
                    yield return new SpeechSegment(offset + result.Start, offset + result.End, text,
                        language == SourceLanguage.Automatic ? result.Language : requestedLanguage);
            }
        }
    }

    private async IAsyncEnumerable<(TimeSpan Offset, float[] Samples)> Segment(
        IAsyncEnumerable<AudioFrame> audio, [EnumeratorCancellation] CancellationToken token)
    {
        int preroll = SamplesFor(tuning.PreRollMs);
        int minimumSpeech = SamplesFor(tuning.MinSpeechMs);
        int trailingSilence = SamplesFor(tuning.TrailingSilenceMs);
        int maximumLength = SamplesFor(tuning.MaxSegmentMs);
        var leading = new Queue<short>(preroll);
        var segment = new List<float>(maximumLength);
        TimeSpan start = TimeSpan.Zero;
        TimeSpan expected = TimeSpan.Zero;
        int quiet = 0;
        int speech = 0;
        await foreach (AudioFrame frame in audio.WithCancellation(token).ConfigureAwait(false))
        {
            if (frame.Offset - expected > TimeSpan.FromMilliseconds(tuning.DiscontinuityMs))
            {
                segment.Clear();
                leading.Clear();
                quiet = speech = 0;
            }

            expected = frame.Offset + TimeSpan.FromSeconds(frame.Samples.Length / (double)SampleRate);
            bool voiced = IsVoiced(frame.Samples.Span);
            if (segment.Count == 0 && !voiced)
            {
                foreach (short value in frame.Samples.Span) leading.Enqueue(value);
                while (leading.Count > preroll) leading.Dequeue();
                continue;
            }

            if (segment.Count == 0)
            {
                start = frame.Offset - TimeSpan.FromSeconds(leading.Count / (double)SampleRate);
                foreach (short value in leading) segment.Add(value / 32768f);
                leading.Clear();
            }

            foreach (short value in frame.Samples.Span) segment.Add(value / 32768f);
            quiet = voiced ? 0 : quiet + frame.Samples.Length;
            if (voiced) speech += frame.Samples.Length;
            if (quiet < trailingSilence && segment.Count < maximumLength) continue;
            if (speech >= minimumSpeech) yield return (start, ToPaddedSamples(segment));
            segment.Clear();
            speech = quiet = 0;
        }
        if (speech >= minimumSpeech) yield return (start, ToPaddedSamples(segment));
    }

    private static float[] ToPaddedSamples(List<float> audio)
    {
        var samples = new float[Math.Max(audio.Count, SampleRate)];
        audio.CopyTo(samples);
        return samples;
    }

    private static int SamplesFor(int milliseconds) => SampleRate * milliseconds / 1000;

    private bool IsVoiced(ReadOnlySpan<short> samples)
    {
        long energy = 0;
        foreach (short sample in samples) energy += (long)sample * sample;
        return samples.Length > 0 && energy / samples.Length > (long)tuning.VoiceThreshold * tuning.VoiceThreshold;
    }
}
