using Avalonia;
using Avalonia.Threading;
using Translator.Inference;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--check-models", StringComparer.Ordinal)) return CheckModels();

        DesktopInstance? instance;
        try { instance = DesktopInstance.Acquire(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"无法获取桌面翻译的实例锁。{exception.Message}");
            return 1;
        }

        if (instance is null) return 0;
        using (instance)
        {
            return BuildAvaloniaApp().AfterSetup(builder =>
            {
                if (builder.Instance is App app)
                    instance.Listen(() => Dispatcher.UIThread.Post(app.ActivateSettings));
            }).StartWithClassicDesktopLifetime(args);
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();

    private static int CheckModels()
    {
        var store = new SettingsStore();
        SettingsLoadResult result = store.Load();
        if (result.Error is not null)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        if (RuntimePaths.Configuration.Error is { } configError) Console.Error.WriteLine(configError);
        var catalog = new GgufModelCatalog();
        ModelFile? selected = result.Settings.ModelFileName.Length == 0
            ? null : catalog.Inspect(result.Settings.ModelDirectory, result.Settings.ModelFileName);
        Console.WriteLine(selected is null
            ? "Selected\tNotConfigured"
            : $"Selected\t{selected.Status}\t{selected.Path}");
        foreach (ModelFile file in catalog.Inspect(result.Settings.ModelDirectory))
            Console.WriteLine($"{file.FileName}\tGGUF\t{file.Status}\t{file.SizeBytes}\t{file.Path}");

        if (OperatingSystem.IsWindows())
        {
            AppSettings settings = result.Settings;
            Console.WriteLine($"Audio ASR\t{settings.AudioAsrBackend}");
            Console.WriteLine($"ONNX ASR model directory\t{RuntimePaths.AsrRoot(settings.AudioAsrModelDirectory)}");
            Console.WriteLine($"Audio ASR model\t{RuntimePaths.FindAsrModel(settings) ?? "Missing"}");
            bool diarizationAvailable = File.Exists(RuntimePaths.DiarizationLibrary) && File.Exists(RuntimePaths.DiarizationModel);
            Console.WriteLine($"Speaker diarization\t{(diarizationAvailable ? "Available" : "Missing")}\t{(settings.SpeakerLabels ? "Enabled" : "Disabled")}");
            var runtimes = new WindowsLlamaRuntimeProvider(RuntimePaths.DirectoryPath, RuntimePaths.Configuration.Options.Windows);
            foreach (var runtime in runtimes.Resolve(settings.LlamaExecutable, settings.UseGpu, settings.AllowCpuFallback))
                Console.WriteLine($"Runtime candidate\t{runtime.BackendName}\t{runtime.Executable}");
            Console.WriteLine($"Application audio sources: {new WindowsAudioSourceCatalog().ListSources().Count}");
        }

        return selected?.Status == ModelFileStatus.Available ? 0 : 1;
    }
}
