using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Translator.Inference;
using Translator.Platform.Windows;

namespace Translator.Desktop;

public sealed partial class App : Application
{
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
            var menu = new NativeMenu();
            if (OperatingSystem.IsWindows())
            {
                try { windowsTray = new WindowsNotificationTray(menu, () => OpenSettings(desktop),
                    () => controller?.RefreshAudioSources()); }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    trayError = exception.Message;
                }
                controller = new WindowsDesktopController(text => windowsTray?.SetTooltip(text), menu,
                    () => windowsTray?.OpenMenuAtCursor(), new WindowsAudioSourceCatalog(), new ProcessLoopbackCapture(),
                    new WhisperSpeechTranscriber(RuntimePaths.WhisperModel, RuntimePaths.Configuration.Options.Whisper),
                    new WindowsLlamaRuntimeProvider(RuntimePaths.DirectoryPath, RuntimePaths.Configuration.Options.Windows));
            }

            TrayMenu.Add(menu, "设置", () => OpenSettings(desktop));
            TrayMenu.Add(menu, "退出", () => ExitApplication(desktop));
            if (OperatingSystem.IsWindows()) Dispatcher.UIThread.Post(() =>
            {
                if (exiting) return;
                windowsTray?.Start();
                if (TrayUnavailable) OpenSettings(desktop);
            });
            else
            {
                using Stream iconStream = AssetLoader.Open(new Uri("avares://Translator.Desktop/Assets/translator.ico"));
                var trayIcon = new TrayIcon { Icon = new WindowIcon(iconStream), ToolTipText = "桌面翻译", Menu = menu };
                trayIcon.Clicked += (_, _) => OpenSettings(desktop);
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
                OpenSettings(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private bool TrayUnavailable => OperatingSystem.IsWindows() && windowsTray?.IsRegistered != true;

    internal void ActivateSettings()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) OpenSettings(desktop);
    }

    private async void ExitApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (exiting) return;
        exiting = true;
        try { if (controller is not null) await controller.DisposeAsync(); }
        finally { desktop.Shutdown(); }
    }

    private void OpenSettings(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (exiting) return;
        if (desktop.MainWindow is not SettingsWindow window)
        {
            window = new SettingsWindow();
            if (controller is { } runtime)
            {
                window.SettingsSaved += runtime.ApplySettings;
                window.MonitorStatus(() => TrayUnavailable
                    ? $"{runtime.Status}\n{TrayStatus()} 关闭设置将退出应用。"
                    : runtime.Status);
            }
            window.Closed += (_, _) =>
            {
                desktop.MainWindow = null;
                if (TrayUnavailable) ExitApplication(desktop);
            };
            desktop.MainWindow = window;
            window.Show();
        }

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
