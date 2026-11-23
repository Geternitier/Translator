using System.Runtime.CompilerServices;
using Translator.Core;

namespace Translator.Inference;

internal sealed record SpeechSegmentation(int PreRollMs = 150, int MinSpeechMs = 200, int TrailingSilenceMs = 350,
    int MaxSegmentMs = 4000, int DiscontinuityMs = 100, int VoiceThreshold = 180)
{
    private const int SampleRate = AudioFrame.SampleRate;

    public async IAsyncEnumerable<SpeechAudioSegment> ReadAsync(IAsyncEnumerable<AudioFrame> audio,
        [EnumeratorCancellation] CancellationToken token, int minimumInputSamples = 0)
    {
        int preroll = SamplesFor(PreRollMs);
        int minimumSpeech = SamplesFor(MinSpeechMs);
        int trailingSilence = SamplesFor(TrailingSilenceMs);
        int maximumLength = SamplesFor(MaxSegmentMs);
        TimeSpan discontinuity = TimeSpan.FromMilliseconds(DiscontinuityMs);
        var leading = new Queue<short>(preroll);
        var segment = new List<float>(maximumLength);
        TimeSpan start = TimeSpan.Zero;
        TimeSpan expected = TimeSpan.Zero;
        int quiet = 0;
        int speech = 0;
        await foreach (AudioFrame frame in audio.WithCancellation(token).ConfigureAwait(false))
        {
            if (frame.Samples.IsEmpty) continue;
            if (frame.Offset - expected > discontinuity)
            {
                if (speech >= minimumSpeech) yield return BuildSegment();
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
            if (speech >= minimumSpeech) yield return BuildSegment();
            segment.Clear();
            speech = quiet = 0;
        }
        if (speech >= minimumSpeech) yield return BuildSegment();

        SpeechAudioSegment BuildSegment()
        {
            var samples = new float[Math.Max(segment.Count, minimumInputSamples)];
            segment.CopyTo(samples);
            TimeSpan end = start + TimeSpan.FromSeconds((segment.Count - quiet) / (double)SampleRate);
            return new SpeechAudioSegment(start, end, samples);
        }
    }

    private static int SamplesFor(int milliseconds) => SampleRate * milliseconds / 1000;

    private bool IsVoiced(ReadOnlySpan<short> samples)
    {
        long energy = 0;
        foreach (short sample in samples) energy += (long)sample * sample;
        return energy / samples.Length > (long)VoiceThreshold * VoiceThreshold;
    }
}

internal sealed record SpeechAudioSegment(TimeSpan Start, TimeSpan End, float[] Samples);
