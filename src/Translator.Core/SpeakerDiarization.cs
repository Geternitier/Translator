namespace Translator.Core;

public interface ISpeakerDiarizer
{
    IAsyncEnumerable<SpeakerSnapshot> TrackAsync(IAsyncEnumerable<AudioFrame> audio, CancellationToken cancellationToken);
}

public sealed record SpeakerSpan(int SpeakerId, TimeSpan Start, TimeSpan End);

public readonly record struct SpeakerAttribution(int? SpeakerId, bool IsOverlapping = false);

/// <summary>Speaker activity on the capture timeline, including simultaneous speech.</summary>
public sealed record SpeakerSnapshot(TimeSpan Start, TimeSpan End, IReadOnlyList<SpeakerSpan> Spans)
{
    public SpeakerAttribution? GetAttribution(SpeechSegment speech)
    {
        if (speech.Start < Start || speech.End > End || speech.End <= speech.Start) return null;
        var active = Spans.Where(span => span.Start < speech.End && span.End > speech.Start)
            .Select(span => span with
            {
                Start = span.Start > speech.Start ? span.Start : speech.Start,
                End = span.End < speech.End ? span.End : speech.End
            }).ToArray();

        for (int i = 0; i < active.Length; i++)
        {
            SpeakerSpan first = active[i];
            for (int j = i + 1; j < active.Length; j++)
            {
                SpeakerSpan second = active[j];
                if (first.SpeakerId == second.SpeakerId) continue;
                TimeSpan start = first.Start > second.Start ? first.Start : second.Start;
                TimeSpan end = first.End < second.End ? first.End : second.End;
                if ((end - start).TotalMilliseconds >= 120) return new SpeakerAttribution(null, IsOverlapping: true);
            }
        }

        var durations = active.GroupBy(span => span.SpeakerId)
            .Select(group => (SpeakerId: group.Key, Duration: group.Sum(span => (span.End - span.Start).TotalMilliseconds)))
            .ToArray();
        if (durations.Length == 0) return new SpeakerAttribution(null);
        var best = durations.MaxBy(speaker => speaker.Duration);
        return new SpeakerAttribution(best.Duration >= 120 && best.Duration >= durations.Sum(speaker => speaker.Duration) * 0.6
            ? best.SpeakerId : null);
    }
}
