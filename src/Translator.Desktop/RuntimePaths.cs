using System.Text.Json;
using Translator.Core;
using Translator.Inference;

namespace Translator.Desktop;

internal static class RuntimePaths
{
    public static string DirectoryPath { get; } = ResolveDirectory();
    public static string OcrDirectory => Path.Combine(DirectoryPath, "ocr");
    public static string WhisperModel => Path.Combine(DirectoryPath, "ggml-base-q5_1.bin");
    public static string DiarizationModel => Path.Combine(DirectoryPath, "diarization", "Nemotron-3-Diarization.q8_0.gguf");
    // The native adapter targets the ABI of the runtime version installed by setup-runtime.ps1.
    public static string DiarizationLibrary => Path.Combine(DirectoryPath, "diarization", "nemo-speech-0.3.0-windows-x86_64-cpu",
        "bin", "nemo_speech_asr_c.dll");
    public static string AsrRoot(string configuredDirectory) => configuredDirectory.Length == 0
        ? Path.Combine(DirectoryPath, "asr") : configuredDirectory;

    public static string? FindAsrModel(AppSettings settings) => settings.AudioAsrBackend switch
    {
        AudioAsrBackend.Nemotron => NemotronSpeechTranscriber.FindModelDirectory(AsrRoot(settings.AudioAsrModelDirectory)),
        AudioAsrBackend.Qwen3Asr => QwenSpeechTranscriber.FindModelDirectory(AsrRoot(settings.AudioAsrModelDirectory)),
        _ => File.Exists(WhisperModel) ? WhisperModel : null
    };
    public static (RuntimeOptions Options, string? Error) Configuration { get; } = LoadConfiguration();

    private static (RuntimeOptions, string?) LoadConfiguration()
    {
        const string name = "translator.runtime.json";
        string? path = Environment.GetEnvironmentVariable("TRANSLATOR_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(SettingsStore.UserDirectory, name);
            if (!File.Exists(path)) return (RuntimeOptions.Defaults, null);
        }

        try { return (RuntimeOptions.Load(path), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return (RuntimeOptions.Defaults, $"无法读取运行配置。应用使用内置默认值。{error.Message}");
        }
    }

    private static string ResolveDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("TRANSLATER_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        string adjacent = Path.Combine(AppContext.BaseDirectory, "runtime");
        if (Directory.Exists(adjacent)) return adjacent;
        if (File.Exists(Path.Combine(Environment.CurrentDirectory, "Translator.slnx")))
            return Path.Combine(Environment.CurrentDirectory, "runtime");
        return Path.Combine(SettingsStore.UserDirectory, "runtime");
    }
}
