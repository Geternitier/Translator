using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Translator.Core;
using Translator.Inference;
using Translator.Platform.Windows;

namespace Translator.Desktop;

public sealed partial class App : Application
{
    internal const string DisplayName = "樱语翻译";
    private readonly NativeMenu trayMenu = new();
    private WindowsDesktopController? controller;
    private WindowsNotificationTray? windowsTray;
    private string? trayError;
    private bool exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            TrayMenu.Add(trayMenu, "打开工作台", SakuraGlyph.Home, () => OpenWorkbench(desktop));
            if (OperatingSystem.IsWindows())
            {
                trayMenu.Items.Add(new NativeMenuItemSeparator());
                try { windowsTray = new WindowsNotificationTray(trayMenu, () => OpenWorkbench(desktop),
                    () => controller?.RefreshAudioSources()); }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    trayError = exception.Message;
                }
                controller = new WindowsDesktopController(text => windowsTray?.SetTooltip(text), trayMenu,
                    () => OpenWorkbench(desktop, WorkbenchPage.Audio), new WindowsAudioSourceCatalog(), new ProcessLoopbackCapture(),
                    CreateSpeechTranscriber, CreateSpeakerDiarizer, new PaddleScreenOcr(RuntimePaths.OcrDirectory), CreateTranslationService);
            }

            TrayMenu.Add(trayMenu, "设置", SakuraGlyph.Settings, () => OpenWorkbench(desktop, WorkbenchPage.Settings));
            TrayMenu.Add(trayMenu, "退出", SakuraGlyph.Exit, () => ExitApplication(desktop));
            if (OperatingSystem.IsWindows()) Dispatcher.UIThread.Post(() =>
            {
                if (exiting) return;
                windowsTray?.Start();
                if (TrayUnavailable && desktop.MainWindow is null) OpenWorkbench(desktop);
            });
            else
            {
                using Stream iconStream = AssetLoader.Open(new Uri("avares://Translator.Desktop/Assets/translator.ico"));
                var trayIcon = new TrayIcon { Icon = new WindowIcon(iconStream), ToolTipText = DisplayName, Menu = trayMenu };
                trayIcon.Clicked += (_, _) => OpenWorkbench(desktop);
                TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
            }
            desktop.Exit += (_, _) =>
            {
                exiting = true;
                windowsTray?.Dispose();
                controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };

            if (desktop.Args?.Contains("--settings", StringComparer.Ordinal) == true)
            {
                OpenWorkbench(desktop, WorkbenchPage.Settings);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ISpeechTranscriber CreateSpeechTranscriber(AppSettings settings)
    {
        string? model = RuntimePaths.FindAsrModel(settings);
        string root = RuntimePaths.AsrRoot(settings.AudioAsrModelDirectory);
        return settings.AudioAsrBackend switch
        {
            AudioAsrBackend.Nemotron => new NemotronSpeechTranscriber(model ?? root),
            AudioAsrBackend.Qwen3Asr => new QwenSpeechTranscriber(model ?? root),
            _ => new WhisperSpeechTranscriber(RuntimePaths.WhisperModel, RuntimePaths.Configuration.Options.Whisper)
        };
    }

    private static ISpeakerDiarizer? CreateSpeakerDiarizer(AppSettings settings) => settings.SpeakerLabels
        ? new NemoSpeakerDiarizer(RuntimePaths.DiarizationLibrary, RuntimePaths.DiarizationModel) : null;

    private static ITranslationService CreateTranslationService(Func<AppSettings> readSettings, Func<bool> keepWarm)
    {
        RuntimeOptions tuning = RuntimePaths.Configuration.Options;
        var runtimeProvider = new WindowsLlamaRuntimeProvider(RuntimePaths.DirectoryPath, tuning.Windows);
        return new LlamaTranslator(() =>
        {
            AppSettings settings = readSettings();
            return new LlamaOptions(settings.ModelDirectory, settings.ModelFileName, settings.LlamaExecutable,
                settings.UseGpu, settings.AllowCpuFallback);
        }, runtimeProvider, tuning.Llama, keepWarm);
    }

    private bool TrayUnavailable => OperatingSystem.IsWindows() && windowsTray?.IsRegistered != true;

    internal void ActivateSettings()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) OpenWorkbench(desktop, WorkbenchPage.Settings);
    }

    private async void ExitApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (exiting) return;
        exiting = true;
        try { if (controller is not null) await controller.DisposeAsync(); }
        finally { desktop.Shutdown(); }
    }

    private void OpenWorkbench(IClassicDesktopStyleApplicationLifetime desktop, WorkbenchPage page = WorkbenchPage.Home)
    {
        if (exiting) return;
        if (desktop.MainWindow is not SettingsWindow window)
        {
            window = new SettingsWindow();
            if (controller is { } runtime) window.SettingsSaved += runtime.ApplySettings;
            window.AttachRuntime(controller, () => TrayUnavailable
                ? $"{controller?.Status}\n{TrayStatus()} 关闭窗口将退出应用。".TrimStart()
                : controller?.Status ?? "", TrayMenu.CreateFlyout(trayMenu, () => controller?.RefreshAudioSources()));
            window.Closed += (_, _) =>
            {
                desktop.MainWindow = null;
                if (TrayUnavailable) ExitApplication(desktop);
            };
            desktop.MainWindow = window;
            window.Navigate(page);
            window.Show();
        }
        else window.Navigate(page);

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private string TrayStatus() => trayError ?? (windowsTray?.RegistrationError switch
    {
        5 => "当前启动环境无法访问 Windows 通知区域。请在 Windows 桌面会话中启动应用。",
        > 0 and var error => $"Windows 通知区域尚未接受图标注册（错误 {error}），应用会继续重试。",
        _ => "Windows 通知区域尚未接受图标注册，应用会继续重试。"
    });
}
