using System.Text.Json;
using Translator.Core;

namespace Translator.Desktop;

internal static class RuntimePaths
{
    public static string DirectoryPath { get; } = ResolveDirectory();
    public static string OcrDirectory => Path.Combine(DirectoryPath, "ocr");
    public static string WhisperModel => Path.Combine(DirectoryPath, "ggml-base-q5_1.bin");
    public static (RuntimeOptions Options, string? Error) Configuration { get; } = LoadConfiguration();

    private static (RuntimeOptions, string?) LoadConfiguration()
    {
        const string name = "translator.runtime.json";
        string? path = Environment.GetEnvironmentVariable("TRANSLATOR_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            string adjacent = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(Path.Combine(Environment.CurrentDirectory, "Translator.slnx")) && !Directory.Exists(Path.Combine(AppContext.BaseDirectory, "runtime")))
                path = Path.Combine(Environment.CurrentDirectory, name);
            else if (File.Exists(adjacent)) path = adjacent;
            else return (RuntimeOptions.Defaults, null);
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
