using System.Text.Json;
using System.Text.Json.Serialization;
using Translator.Core;
using Translator.Inference;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal enum AudioAsrBackend { Whisper, Nemotron, Qwen3Asr }

internal sealed record AppSettings
{
    public const int DefaultSubtitleFontSize = 22;

    public string ModelDirectory { get; init; } = Path.Combine(SettingsStore.UserDirectory, "Models");
    public SourceLanguage SourceLanguage { get; init; } = SourceLanguage.Automatic;
    public AudioAsrBackend AudioAsrBackend { get; init; } = AudioAsrBackend.Whisper;
    public string AudioAsrModelDirectory { get; init; } = "";
    public string ModelFileName { get; init; } = "";
    public string LlamaExecutable { get; init; } = "";
    public bool UseGpu { get; init; } = true;
    public bool AllowCpuFallback { get; init; } = true;
    public bool AutoSelection { get; init; } = true;
    public bool BilingualSubtitles { get; init; }
    public bool SpeakerLabels { get; init; }
    public int SubtitleFontSize { get; init; } = DefaultSubtitleFontSize;
    public int SubtitleBottomOffset { get; init; } = 64;
    public ShortcutSettings Shortcuts { get; init; } = new();
}

internal sealed record ShortcutSettings
{
    public const string DefaultContinuousOcr = "Ctrl + Alt + O";
    public string ContinuousOcr { get; init; } = DefaultContinuousOcr;
    public string Audio { get; init; } = "Ctrl + Alt + S";

    public string[] Values() => [ContinuousOcr, Audio];
}

internal sealed record SettingsLoadResult(AppSettings Settings, string? Error = null);

internal sealed record OverlayPosition(int X, int Y);
internal sealed record OverlayPositions(OverlayPosition? Ocr = null, OverlayPosition? Audio = null);

internal sealed class SettingsStore
{
    internal static readonly string UserDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Translater");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string settingsPath;
    private readonly string positionsPath;

    public SettingsStore()
    {
        string adjacent = Path.Combine(AppContext.BaseDirectory, "translater.local.json");
        string workspace = Path.Combine(Environment.CurrentDirectory, "translater.local.json");
        settingsPath = File.Exists(adjacent) ? adjacent : File.Exists(workspace) ? workspace : Path.Combine(UserDirectory, "settings.json");
        positionsPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "overlay-positions.json");
    }

    public SettingsLoadResult Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(settingsPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            AppSettings settings = root.Deserialize<AppSettings>(Options)
                ?? throw new JsonException("设置内容为空。");
            string directory = NormalizeDirectory(settings.ModelDirectory);
            string model = settings.ModelFileName ?? "";
            if (!model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                model = "";
            ShortcutSettings shortcuts = settings.Shortcuts ?? new ShortcutSettings();
            bool allowCpuFallback = settings.AllowCpuFallback;
            bool legacyModelSettings = !root.TryGetProperty("modelFileName", out _);
            if (legacyModelSettings || (root.TryGetProperty("shortcuts", out JsonElement oldShortcuts)
                    && oldShortcuts.ValueKind == JsonValueKind.Object && oldShortcuts.TryGetProperty("instantOcr", out _)))
                shortcuts = MigrateContinuousShortcut(shortcuts);
            if (!root.TryGetProperty("allowCpuFallback", out _) && root.TryGetProperty("allowFallback", out JsonElement oldFallback)
                && oldFallback.ValueKind is JsonValueKind.True or JsonValueKind.False)
                allowCpuFallback = oldFallback.GetBoolean();
            if (model.Length != 0) new GgufModelCatalog().Inspect(directory, model);
            return new SettingsLoadResult(settings with
            {
                ModelDirectory = directory,
                AudioAsrModelDirectory = NormalizeAsrDirectory(settings.AudioAsrModelDirectory),
                ModelFileName = model,
                LlamaExecutable = settings.LlamaExecutable?.Trim() ?? "",
                Shortcuts = shortcuts,
                AllowCpuFallback = allowCpuFallback,
                SubtitleFontSize = Math.Clamp(settings.SubtitleFontSize, 14, 44),
                SubtitleBottomOffset = Math.Clamp(settings.SubtitleBottomOffset, 16, 240)
            });
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SettingsLoadResult(new AppSettings());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        {
            return new SettingsLoadResult(new AppSettings(), $"读取设置失败。{exception.Message}");
        }
    }

    private static ShortcutSettings MigrateContinuousShortcut(ShortcutSettings shortcuts)
    {
        try
        {
            Hotkey original = Hotkey.Parse("Ctrl + Alt + Shift + O");
            Hotkey updated = Hotkey.Parse(ShortcutSettings.DefaultContinuousOcr);
            if (Hotkey.Parse(shortcuts.ContinuousOcr) == original
                && Hotkey.Parse(shortcuts.Audio) != updated)
                return shortcuts with { ContinuousOcr = ShortcutSettings.DefaultContinuousOcr };
        }
        catch (FormatException) { }
        return shortcuts;
    }

    public static string NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory.Trim()))
        {
            throw new ArgumentException("请输入模型目录的完整路径。");
        }

        return Path.GetFullPath(directory.Trim());
    }

    public static string NormalizeAsrDirectory(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ? "" : NormalizeDirectory(directory);

    public OverlayPositions LoadOverlayPositions()
    {
        try
        {
            using FileStream stream = File.OpenRead(positionsPath);
            return JsonSerializer.Deserialize<OverlayPositions>(stream, Options) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void SaveOverlayPositions(OverlayPositions positions) => SaveFile(positionsPath, positions);

    public void Save(AppSettings settings) => SaveFile(settingsPath, settings);

    private static void SaveFile<T>(string path, T value)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream tempFile = File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(tempFile, value, Options);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
