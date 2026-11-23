using System.Runtime.CompilerServices;
using SherpaOnnx;
using Translator.Core;
using SpeechSegment = Translator.Core.SpeechSegment;

namespace Translator.Inference;

/// <summary>Offline ONNX inference on completed utterances from a live audio stream.</summary>
public sealed class QwenSpeechTranscriber(string modelDirectory) : ISpeechTranscriber
{
    private const string Frontend = "conv_frontend.onnx", Encoder = "encoder.int8.onnx",
        Decoder = "decoder.int8.onnx", Tokenizer = "tokenizer";
    private static readonly string[] TokenizerFiles = ["merges.txt", "vocab.json", "tokenizer_config.json"];

    public static string? FindModelDirectory(string root) =>
        AsrModelFiles.FindDirectory(root, [Frontend, Encoder, Decoder, .. TokenizerFiles.Select(TokenizerFile)]);

    public async IAsyncEnumerable<SpeechRecognitionUpdate> TranscribeAsync(IAsyncEnumerable<AudioFrame> audio,
        SourceLanguage language, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioFrame.SampleRate;
        config.FeatConfig.FeatureDim = 128;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = AsrModelFiles.CpuThreads;
        config.ModelConfig.Qwen3Asr.ConvFrontend = Required(Frontend);
        config.ModelConfig.Qwen3Asr.Encoder = Required(Encoder);
        config.ModelConfig.Qwen3Asr.Decoder = Required(Decoder);
        foreach (string file in TokenizerFiles) Required(TokenizerFile(file));
        config.ModelConfig.Qwen3Asr.Tokenizer = Path.Combine(modelDirectory, Tokenizer);

        cancellationToken.ThrowIfCancellationRequested();
        using var recognizer = new OfflineRecognizer(config);
        (string? requestedLanguage, string? languageCode) = language switch
        {
            SourceLanguage.English => ("English", "en"),
            SourceLanguage.Japanese => ("Japanese", "ja"),
            _ => (null, null)
        };
        var segments = new SpeechSegmentation();
        await foreach (SpeechAudioSegment segment in segments.ReadAsync(audio, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using OfflineStream stream = recognizer.CreateStream();
            if (requestedLanguage is not null) stream.SetOption("language", requestedLanguage);
            stream.AcceptWaveform(AudioFrame.SampleRate, segment.Samples);
            recognizer.Decode(stream);
            cancellationToken.ThrowIfCancellationRequested();
            string text = stream.Result.Text.Trim();
            if (text.Length != 0)
                yield return new SpeechRecognitionUpdate(
                    new SpeechSegment(segment.Start, segment.End, text, languageCode), IsFinal: true);
        }
    }

    private string Required(string name) => AsrModelFiles.Require(modelDirectory, name, "Qwen3-ASR");

    private static string TokenizerFile(string name) => Path.Combine(Tokenizer, name);
}
