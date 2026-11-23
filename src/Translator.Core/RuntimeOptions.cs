using System.Text.Json;
using System.Text.Json.Serialization;

namespace Translator.Core;

public sealed record RuntimeOptions(
    ModelDefaults Models, LlamaTuning Llama, OcrTuning Ocr, AudioTuning Audio, WhisperTuning Whisper, WindowsTuning Windows)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static RuntimeOptions Defaults { get; } = LoadDefaults();

    public static RuntimeOptions Load(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Read(file);
    }

    private static RuntimeOptions LoadDefaults()
    {
        using Stream resource = typeof(RuntimeOptions).Assembly.GetManifestResourceStream("Translator.Core.RuntimeDefaults.json")
            ?? throw new InvalidOperationException("Missing embedded runtime configuration.");
        return Read(resource);
    }

    private static RuntimeOptions Read(Stream stream)
    {
        RuntimeOptions value = JsonSerializer.Deserialize<RuntimeOptions>(stream, JsonOptions)
            ?? throw new InvalidDataException("运行配置为空。");
        if (value.Models is not { } models || value.Llama is not { } llama || value.Ocr is not { } ocr
            || value.Audio is not { } audio || value.Whisper is not { } whisper || value.Windows is not { } windows)
            throw new InvalidDataException("运行配置缺少区块。");

        if (!IsModelName(models.DefaultFile) || !IsModelName(models.LightFile))
            throw new InvalidDataException("models 必须指定有效的 GGUF 文件名。");
        if (llama.ContextTokens is < 256 or > 262144
            || !Within(1, llama.ContextTokens - 1, llama.TextTokens, llama.SubtitleTokens, llama.ParagraphTokens)
            || !Within(1, 1_000_000, llama.MaxInputCharacters, llama.ContextLineCharacters)
            || !Within(0, 1_000_000, llama.GpuReserveMiB, llama.MinimumFreeGpuMiB)
            || !Within(1, 86_400, llama.MemoryCheckSeconds, llama.IdleSeconds, llama.IdlePollSeconds, llama.StartupSeconds, llama.RequestSeconds)
            || !Within(1, 60_000, llama.StartupPollMs) || llama.LogVerbosity is < 0 or > 10
            || llama.TranslationTemperature is < 0 or > 2 || llama.RevisionTemperature is < 0 or > 2)
            throw new InvalidDataException("llama 的容量、超时或采样设置不在支持范围内。");
        if (!Within(1, 60_000, ocr.FallbackIntervalMs, ocr.QuietMs, ocr.MaxWaitMs, ocr.BlankConfirmationMs,
            ocr.DxgiAcquireMs, ocr.DxgiReadbackMs, ocr.DxgiFirstFrameMs, ocr.DxgiRecoveryMs) || ocr.QuietMs > ocr.MaxWaitMs)
            throw new InvalidDataException("ocr 的等待时间不在支持范围内。");
        if (!Within(1, 1000, audio.PendingSegments, audio.HistoryLines, audio.VisibleLines) || audio.ParagraphLines is < 2 or > 100
            || !Within(1, 600_000, audio.ShortBudgetMs, audio.RefreshMs, audio.ParagraphDelayMs, audio.ParagraphBudgetMs, audio.ParagraphGapMs))
            throw new InvalidDataException("audio 的队列、时间预算或行数不在支持范围内。");
        if (!Within(1, 60_000, whisper.PreRollMs, whisper.MinSpeechMs, whisper.TrailingSilenceMs, whisper.MaxSegmentMs, whisper.DiscontinuityMs)
            || whisper.MinSpeechMs > whisper.MaxSegmentMs || whisper.VoiceThreshold is < 0 or >= short.MaxValue || whisper.MaxThreads is < 1 or > 256)
            throw new InvalidDataException("whisper 的分段、能量阈值或线程数不在支持范围内。");
        if (string.IsNullOrWhiteSpace(windows.CudaDevice) || !Within(1, 60_000, windows.MemoryProbeMs))
            throw new InvalidDataException("windows 的 CUDA 设备或探测超时无效。");
        return value;
    }

    private static bool Within(int minimum, int maximum, params int[] values) =>
        values.All(value => value >= minimum && value <= maximum);

    private static bool IsModelName(string? filename) => !string.IsNullOrWhiteSpace(filename)
        && filename == Path.GetFileName(filename) && filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
}

public sealed record ModelDefaults(string DefaultFile, string LightFile);

public sealed record LlamaTuning(
    int ContextTokens, int MaxInputCharacters, int TextTokens, int SubtitleTokens, int ParagraphTokens,
    double TranslationTemperature, double RevisionTemperature, int GpuReserveMiB, int MinimumFreeGpuMiB,
    int MemoryCheckSeconds, int IdleSeconds, int IdlePollSeconds, int StartupSeconds, int StartupPollMs,
    int RequestSeconds, int LogVerbosity, int ContextLineCharacters);

public sealed record OcrTuning(
    int FallbackIntervalMs, int QuietMs, int MaxWaitMs, int BlankConfirmationMs, int DxgiAcquireMs,
    int DxgiReadbackMs, int DxgiFirstFrameMs, int DxgiRecoveryMs);

public sealed record AudioTuning(
    int PendingSegments, int ShortBudgetMs, int RefreshMs, int ParagraphDelayMs, int ParagraphBudgetMs,
    int ParagraphLines, int ParagraphGapMs, int HistoryLines, int VisibleLines);

public sealed record WhisperTuning(
    int PreRollMs, int MinSpeechMs, int TrailingSilenceMs, int MaxSegmentMs, int DiscontinuityMs,
    int VoiceThreshold, int MaxThreads);

public sealed record WindowsTuning(string CudaDevice, int MemoryProbeMs);
