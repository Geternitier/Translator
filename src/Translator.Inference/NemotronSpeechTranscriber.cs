using System.Runtime.CompilerServices;
using SherpaOnnx;
using Translator.Core;
using SpeechSegment = Translator.Core.SpeechSegment;

namespace Translator.Inference;

public sealed class NemotronSpeechTranscriber(string modelDirectory) : ISpeechTranscriber
{
    private const int SampleRate = AudioFrame.SampleRate;
    private const float MaximumUtteranceSeconds = 6;
    private const float MaximumDecoderSeconds = 45;
    private const int QuietSamplesBeforeReset = SampleRate * 1200 / 1000;
    private const int QuietPcmThreshold = 500;
    private const string Tokens = "tokens.txt", Encoder = "encoder.int8.onnx",
        Decoder = "decoder.int8.onnx", Joiner = "joiner.int8.onnx";

    public static string? FindModelDirectory(string root) =>
        AsrModelFiles.FindDirectory(root, [Tokens, Encoder, Decoder, Joiner], name =>
            name.Contains("-160ms-", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    public async IAsyncEnumerable<SpeechRecognitionUpdate> TranscribeAsync(IAsyncEnumerable<AudioFrame> audio,
        SourceLanguage language, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = new OnlineRecognizerConfig
        {
            EnableEndpoint = 1,
            Rule1MinTrailingSilence = 2.4f,
            Rule2MinTrailingSilence = 0.75f,
            Rule3MinUtteranceLength = float.MaxValue,
            DecodingMethod = "greedy_search"
        };
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = AsrModelFiles.CpuThreads;
        config.ModelConfig.Tokens = Required(Tokens);
        config.ModelConfig.Transducer.Encoder = Required(Encoder);
        config.ModelConfig.Transducer.Decoder = Required(Decoder);
        config.ModelConfig.Transducer.Joiner = Required(Joiner);

        cancellationToken.ThrowIfCancellationRequested();
        using var recognizer = new OnlineRecognizer(config);
        string? languageCode = language switch
        {
            SourceLanguage.English => "en",
            SourceLanguage.Japanese => "ja",
            _ => null
        };
        OnlineStream stream = CreateStream();
        TimeSpan? streamStart = null;
        TimeSpan? utteranceStart = null;
        TimeSpan audioEnd = TimeSpan.Zero;
        int committedLength = 0;
        int committedTokens = 0;
        string previous = "";
        int previousStable = 0;
        int quietSamples = 0;
        try
        {
            await foreach (AudioFrame frame in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Samples.IsEmpty) continue;
                if (streamStart is not null && (frame.Offset - audioEnd).Duration() > TimeSpan.FromMilliseconds(100))
                {
                    if (Finish() is { } interrupted) yield return interrupted;
                    OnlineStream previousStream = stream;
                    stream = CreateStream();
                    previousStream.Dispose();
                    ResetTimeline(frame.Offset);
                }
                streamStart ??= frame.Offset;

                var samples = new float[frame.Samples.Length];
                long energy = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    short pcm = frame.Samples.Span[i];
                    samples[i] = pcm / 32768f;
                    energy += (long)pcm * pcm;
                }
                quietSamples = energy / samples.Length <= QuietPcmThreshold * QuietPcmThreshold
                    ? Math.Min(QuietSamplesBeforeReset, quietSamples + samples.Length) : 0;
                stream.AcceptWaveform(SampleRate, samples);
                audioEnd = frame.Offset + TimeSpan.FromSeconds(samples.Length / (double)SampleRate);
                if (!DecodeAvailable()) continue;

                OnlineRecognizerResult result = recognizer.GetResult(stream);
                string fullText = result.Text.Trim();
                string text = UncommittedText(fullText);
                if (text.Length != 0) utteranceStart ??= SegmentStart(result);
                bool endpoint = recognizer.IsEndpoint(stream);
                bool lineExpired = utteranceStart is { } start && (audioEnd - start).TotalSeconds >= MaximumUtteranceSeconds;
                bool reset = (endpoint && quietSamples >= QuietSamplesBeforeReset)
                    || (audioEnd - streamStart.Value).TotalSeconds >= MaximumDecoderSeconds;
                if (endpoint || lineExpired || reset)
                {
                    SpeechRecognitionUpdate? final = Final(result, text);
                    if (final is not null)
                    {
                        committedLength = fullText.Length;
                        committedTokens = result.Timestamps.Length;
                        utteranceStart = null;
                        previous = "";
                        previousStable = 0;
                    }
                    // Preserve decoder context across subtitle boundaries. Reset only after
                    // sustained acoustic silence or to bound long-running decoder state.
                    if (reset)
                    {
                        recognizer.Reset(stream);
                        ResetTimeline(audioEnd);
                    }
                    if (final is not null) yield return final;
                    continue;
                }

                if (text.Length == 0) continue;
                int stable = CommonPrefixLength(previous, text);
                if (text != previous || stable != previousStable)
                {
                    previous = text;
                    previousStable = stable;
                    yield return new SpeechRecognitionUpdate(Segment(result, text), IsFinal: false, StablePrefixLength: stable);
                }
            }
            if (streamStart is not null && Finish() is { } finalResult) yield return finalResult;
        }
        finally { stream.Dispose(); }

        OnlineStream CreateStream()
        {
            OnlineStream created = recognizer.CreateStream();
            if (languageCode is not null) created.SetOption("language", languageCode);
            return created;
        }

        bool DecodeAvailable()
        {
            bool decoded = false;
            while (recognizer.IsReady(stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
                recognizer.Decode(stream);
                decoded = true;
            }
            return decoded;
        }

        SpeechRecognitionUpdate? Finish()
        {
            // A final encoder chunk may require right-context samples after the last PCM frame.
            stream.AcceptWaveform(SampleRate, new float[SampleRate]);
            stream.InputFinished();
            DecodeAvailable();
            OnlineRecognizerResult result = recognizer.GetResult(stream);
            return Final(result, UncommittedText(result.Text.Trim()));
        }

        SpeechRecognitionUpdate? Final(OnlineRecognizerResult result, string text) =>
            text.Length != 0 || previous.Length != 0
                ? new SpeechRecognitionUpdate(Segment(result, text), IsFinal: true)
                : null;

        SpeechSegment Segment(OnlineRecognizerResult result, string text) =>
            new(utteranceStart ?? SegmentStart(result), audioEnd, text, languageCode);

        TimeSpan SegmentStart(OnlineRecognizerResult result)
        {
            if (committedTokens < result.Timestamps.Length)
            {
                float first = result.Timestamps[committedTokens];
                if (float.IsFinite(first) && first >= 0)
                    return Min(audioEnd, streamStart.GetValueOrDefault() + TimeSpan.FromSeconds(first));
            }
            return streamStart.GetValueOrDefault();
        }

        string UncommittedText(string text) => text.Length > committedLength ? text[committedLength..].TrimStart() : "";

        void ResetTimeline(TimeSpan start)
        {
            streamStart = start;
            utteranceStart = null;
            committedLength = committedTokens = previousStable = quietSamples = 0;
            previous = "";
        }
    }

    private string Required(string name) => AsrModelFiles.Require(modelDirectory, name, "Nemotron");

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static int CommonPrefixLength(string previous, string current)
    {
        // Greedy transducer decoding appends tokens. A repeated text prefix avoids speculating on
        // characters at the end of the most recent decoder result.
        int length = 0;
        while (length < previous.Length && length < current.Length && previous[length] == current[length]
            && current[length] != '\uFFFD')
            length++;
        return length > 0 && char.IsHighSurrogate(current[length - 1]) ? length - 1 : length;
    }
}
