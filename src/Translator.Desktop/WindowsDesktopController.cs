using System.Diagnostics;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Threading;
using Translator.Core;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal sealed class WindowsDesktopController : IAsyncDisposable
{
    private readonly SettingsStore store = new();
    private readonly RuntimeOptions tuning = RuntimePaths.Configuration.Options;
    private readonly WindowsScreenCapture screen = new(RuntimePaths.Configuration.Options.Ocr);
    private readonly ITextSelectionSource selection;
    private readonly IAudioSourceCatalog sources;
    private readonly IApplicationAudioCapture audioCapture;
    private readonly Func<AppSettings, ISpeechTranscriber> createSpeechTranscriber;
    private readonly Func<AppSettings, ISpeakerDiarizer?> createSpeakerDiarizer;
    private readonly IOpticalCharacterRecognizer ocr;
    private readonly ITranslationService translator;
    private readonly CaptionOverlay instantCaption = new();
    private readonly CaptionOverlay ocrCaption;
    private readonly DispatcherTimer placementTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private OverlayPositions placements;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Action<string> setTrayTooltip;
    private readonly Action openTrayMenu;
    private readonly NativeMenuItem autoMenu;
    private readonly NativeMenuItem regionMenu;
    private readonly NativeMenuItem pauseOcrMenu;
    private readonly NativeMenuItem audioMenu;
    private readonly NativeMenu audioSources = new();
    private AudioSource? selectedAudio;
    private AudioSubtitleWindow? audioWindow;
    private WindowsHotkeys? hotkeys;
    private AppSettings settings;
    private CancellationTokenSource? autoRun;
    private CancellationTokenSource? modelPreparation;
    private CancellationTokenSource? instantRun;
    private CancellationTokenSource? ocrRun;
    private CancellationTokenSource? audioRun;
    private OcrRegionFrame? ocrFrame;
    private ScreenRegion? watchedRegion;
    private bool? resumeAfterAdjustment;
    private bool pickerOpen;
    private bool disposed;
    private string? feedback;
    private readonly SortedDictionary<int, string> shortcutFeedback = new();
    private bool shortcutRegistrationFailed;
    private int hotkeyRegistration;

    public WindowsDesktopController(Action<string> setTrayTooltip, NativeMenu menu, Action openTrayMenu,
        IAudioSourceCatalog sources, IApplicationAudioCapture capture, Func<AppSettings, ISpeechTranscriber> createSpeechTranscriber,
        Func<AppSettings, ISpeakerDiarizer?> createSpeakerDiarizer,
        IOpticalCharacterRecognizer ocr, Func<Func<AppSettings>, Func<bool>, ITranslationService> createTranslationService)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        this.setTrayTooltip = setTrayTooltip;
        this.openTrayMenu = openTrayMenu;
        this.sources = sources;
        audioCapture = capture;
        this.createSpeechTranscriber = createSpeechTranscriber;
        this.createSpeakerDiarizer = createSpeakerDiarizer;
        this.ocr = ocr;
        selection = new WindowsSelectedTextSource(tuning.Llama.MaxInputCharacters);
        placements = store.LoadOverlayPositions();
        ocrCaption = new CaptionOverlay(position => RememberPositions(placements with { Ocr = position }));
        placementTimer.Tick += (_, _) => SavePendingPositions();
        SettingsLoadResult loaded = store.Load();
        settings = loaded.Settings;
        feedback = loaded.Error;
        translator = createTranslationService(() => Volatile.Read(ref settings),
            () => IsSelectionOrOcrActive() || Volatile.Read(ref audioRun) is not null);

        autoMenu = TrayMenu.Add(menu, "选区翻译", ToggleAutoSelection);
        regionMenu = TrayMenu.Add(menu, "持续 OCR", ToggleContinuousOcr);
        pauseOcrMenu = TrayMenu.Add(menu, "暂停持续 OCR", ToggleOcrPause);
        audioMenu = TrayMenu.Add(menu, "开始音频字幕", ToggleAudio);
        menu.Items.Add(new NativeMenuItem("选择音频程序") { Menu = audioSources });
        TrayMenu.Add(menu, "释放模型", ReleaseModel);
        menu.Items.Add(new NativeMenuItemSeparator());
        ApplySettings(settings);
        if (loaded.Error is not null) feedback = loaded.Error;
    }

    public string Status => string.Join('\n', new[]
        {
            settings.ModelFileName.Length == 0 ? "请在设置中选择模型目录和翻译模型。" : translator.Status,
            RuntimePaths.Configuration.Error, feedback
        }.Concat(shortcutFeedback.Values)
        .Where(line => !string.IsNullOrEmpty(line)));

    public void ApplySettings(AppSettings changed)
    {
        ShortcutSettings previousShortcuts = settings.Shortcuts;
        SourceLanguage previousLanguage = settings.SourceLanguage;
        bool modelChanged = TranslationSettings(settings) != TranslationSettings(changed);
        bool audioBackendChanged = settings.AudioAsrBackend != changed.AudioAsrBackend
            || settings.AudioAsrModelDirectory != changed.AudioAsrModelDirectory || settings.SpeakerLabels != changed.SpeakerLabels;
        Volatile.Write(ref settings, changed);
        instantCaption.SetFontSize(changed.SubtitleFontSize);
        ocrCaption.SetFontSize(changed.SubtitleFontSize);
        feedback = null;
        if (hotkeys is null || shortcutRegistrationFailed || previousShortcuts != changed.Shortcuts) RegisterHotkeys();
        bool startingSelection = settings.AutoSelection && autoRun is null;
        if (startingSelection)
        {
            var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            autoRun = run;
            Run(() => WatchSelectionAsync(run));
        }
        if (!settings.AutoSelection)
        {
            Cancel(ref autoRun);
            DismissSelection();
            CancelUnusedPreparation();
        }
        if (modelChanged)
        {
            Cancel(ref modelPreparation);
            DismissSelection();
            Run(translator.ReleaseObsoleteModelAsync);
        }
        if ((modelChanged || previousLanguage != changed.SourceLanguage) && watchedRegion is { } area && ocrRun is not null)
            StartOcr(area);
        if ((modelChanged || audioBackendChanged || previousLanguage != changed.SourceLanguage)
            && audioRun is not null && selectedAudio is { } source)
            StartAudio(source);
        else audioWindow?.ApplySettings(changed);
        if (startingSelection || (modelChanged && settings.AutoSelection)) PrepareModel();
        RefreshMenu();
    }

    private bool IsSelectionOrOcrActive() =>
        Volatile.Read(ref settings).AutoSelection || Volatile.Read(ref ocrRun) is not null;

    private void PrepareModel()
    {
        if (disposed || modelPreparation is not null || settings.ModelFileName.Length == 0) return;
        var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        modelPreparation = run;
        Run(async () =>
        {
            try { await translator.PrepareAsync(run.Token); }
            finally
            {
                if (ReferenceEquals(modelPreparation, run)) modelPreparation = null;
                run.Dispose();
            }
        });
    }

    private void CancelUnusedPreparation()
    {
        if (!IsSelectionOrOcrActive() && audioRun is null) Cancel(ref modelPreparation);
    }

    private void ReleaseModel()
    {
        Cancel(ref modelPreparation);
        Run(translator.ReleaseAsync);
    }

    private static (string, string, string, bool, bool) TranslationSettings(AppSettings current) =>
        (current.ModelDirectory, current.ModelFileName, current.LlamaExecutable, current.UseGpu, current.AllowCpuFallback);

    private void RegisterHotkeys()
    {
        int registration = ++hotkeyRegistration;
        hotkeys?.Dispose();
        hotkeys = null;
        shortcutFeedback.Clear();
        shortcutRegistrationFailed = false;
        (string Gesture, string Name, Action Trigger)[] bindings =
        [
            (settings.Shortcuts.ContinuousOcr, "持续翻译框", ToggleContinuousOcr),
            (settings.Shortcuts.Audio, "音频字幕", ToggleAudio)
        ];
        try
        {
            Hotkey[] keys = bindings.Select(binding => Hotkey.Parse(binding.Gesture)).ToArray();
            int allowOverride = keys[0] == Hotkey.Parse(ShortcutSettings.DefaultContinuousOcr) ? 0 : -1;
            hotkeys = new WindowsHotkeys(keys, allowOverride,
                index => PostCurrent(bindings[index].Trigger),
                (index, error) => PostCurrent(() =>
                {
                    var binding = bindings[index];
                    shortcutRegistrationFailed = true;
                    shortcutFeedback[index] = error == 1409
                        ? $"{binding.Name}的快捷键 {binding.Gesture} 已被其他程序占用。"
                        : $"{binding.Name}的快捷键 {binding.Gesture} 注册失败（错误 {error}）。";
                }),
                index => PostCurrent(() => shortcutFeedback[index] = $"{bindings[index].Gesture} 已由持续翻译框处理。"));
        }
        catch (FormatException exception) { shortcutFeedback[0] = exception.Message; }

        void PostCurrent(Action action) => Dispatcher.UIThread.Post(() =>
        {
            if (!disposed && registration == hotkeyRegistration) action();
        });
    }

    private void ToggleAutoSelection()
    {
        var changed = settings with { AutoSelection = !settings.AutoSelection };
        try { store.Save(changed); ApplySettings(changed); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Report(exception.Message); }
    }

    private async Task WatchSelectionAsync(CancellationTokenSource run)
    {
        try
        {
            await foreach (SelectedText? selected in selection.WatchAsync(run.Token))
            {
                if (!ReferenceEquals(autoRun, run) || run.IsCancellationRequested) break;
                if (pickerOpen) continue;
                if (selected is null) { DismissSelection(); continue; }
                if (selected.Text.Length <= tuning.Llama.MaxInputCharacters) StartSelectionTranslation(selected);
            }
        }
        finally { if (ReferenceEquals(autoRun, run)) autoRun = null; run.Dispose(); }
    }

    private void DismissSelection()
    {
        Cancel(ref instantRun);
        instantCaption.Clear();
    }

    private void StartSelectionTranslation(SelectedText selected)
    {
        if (disposed || pickerOpen) return;
        Cancel(ref instantRun);
        var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        instantRun = run;
        Run(async () =>
        {
            try { await TranslateSelectionAsync(selected, run.Token); }
            finally
            {
                if (ReferenceEquals(instantRun, run)) instantRun = null;
                run.Dispose();
            }
        });
    }

    private async Task TranslateSelectionAsync(SelectedText selected, CancellationToken token)
    {
        if (!TranslationText.NeedsTranslation(selected.Text)) { instantCaption.Clear(); return; }
        ScreenRegion anchor = selected.Region ?? CursorAnchor();
        string displayed = "";
        ShowInstant("正在翻译…", anchor, close: DismissSelection, draggable: true, stop: Stop);
        string translated = await RenderTranslationAsync(selected.Text, TranslationSource.Selection, text =>
        {
            displayed = text;
            ShowInstant(text, anchor, copyable: true, close: DismissSelection, draggable: true,
                keepDraggedPosition: true, stop: Stop);
        }, token);
        token.ThrowIfCancellationRequested();
        ShowInstant(translated, anchor, TimeSpan.FromSeconds(Math.Clamp(translated.Length / 9.0, 5, 14)),
            copyable: true, close: DismissSelection, draggable: true, keepDraggedPosition: true);

        void Stop()
        {
            if (token.IsCancellationRequested) return;
            Cancel(ref instantRun);
            ShowInstant(displayed.Length == 0 ? "已停止翻译" : displayed, anchor, copyable: displayed.Length != 0,
                close: DismissSelection, draggable: true, keepDraggedPosition: true);
        }
    }

    private async Task<string> RenderTranslationAsync(string text, TranslationSource source, Action<string> render,
        CancellationToken token)
    {
        var request = new TranslationRequest(text, settings.SourceLanguage, source, []);
        string latest = "";
        string displayed = "";
        long displayedAt = 0;
        TimeSpan refresh = TimeSpan.FromMilliseconds(tuning.Audio.RefreshMs);
        await foreach (string partial in translator.StreamTranslationAsync(request, token).WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(partial)) continue;
            latest = partial;
            if (latest == displayed || (displayedAt != 0 && Stopwatch.GetElapsedTime(displayedAt) < refresh)) continue;
            render(latest);
            displayed = latest;
            displayedAt = Stopwatch.GetTimestamp();
        }
        token.ThrowIfCancellationRequested();
        if (latest != displayed) render(latest);
        return latest;
    }

    private void ToggleContinuousOcr()
    {
        if (watchedRegion is not null) StopOcr();
        else Run(() => PickContinuousOcrAsync(lifetime.Token));
    }

    private async Task PickContinuousOcrAsync(CancellationToken token)
    {
        if (pickerOpen) return;
        pickerOpen = true;
        instantCaption.Clear();
        ScreenRegion? picked;
        try { picked = await ScreenPicker.PickAsync(screen, token); }
        finally { pickerOpen = false; }
        if (picked is { } region && !token.IsCancellationRequested) StartOcr(region);
    }

    private void StartOcr(ScreenRegion region)
    {
        Cancel(ref ocrRun);
        watchedRegion = region;
        resumeAfterAdjustment = null;
        var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ocrRun = run;
        ocrCaption.Clear();
        ShowOcrFrame();
        RefreshMenu();
        PrepareModel();
        Run(() => ReadOcrAsync(region, run));
    }

    private async Task ReadOcrAsync(ScreenRegion region, CancellationTokenSource run)
    {
        var pending = Latest<(long Version, string Text)>();
        long newest = 0;
        CancellationTokenSource? translating = null;
        var reader = new ContinuousScreenReader(screen, ocr, tuning.Ocr);
        Task producer = ProduceAsync();
        try
        {
            await foreach ((long version, string text) in pending.Reader.ReadAllAsync(run.Token))
            {
                if (text.Length == 0 || version != newest) continue;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
                translating = request;
                try
                {
                    DesktopDisplay display = WindowsScreenCapture.GetDisplay(region.DisplayId);
                    await RenderTranslationAsync(text, TranslationSource.ContinuousOcr, translated =>
                    {
                        if (!request.IsCancellationRequested && version == newest && !pickerOpen)
                            ocrCaption.Show(translated, display, region, copyable: true, close: StopOcr,
                                draggable: true, keepDraggedPosition: true, savedPosition: placements.Ocr,
                                stop: () => PauseOcr(keepCaption: true));
                    }, request.Token);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested && !run.IsCancellationRequested) { }
                finally { translating = null; }
            }
        }
        finally
        {
            if (ReferenceEquals(ocrRun, run))
            {
                ocrRun = null;
                CancelUnusedPreparation();
                ocrCaption.Clear();
                ocrFrame?.SetPaused(true);
                RefreshMenu();
            }
            await run.CancelAsync();
            await producer;
            run.Dispose();
        }

        async Task ProduceAsync()
        {
            try
            {
                await foreach (string text in reader.ReadChangesAsync(region, run.Token))
                {
                    if (run.IsCancellationRequested) break;
                    bool shouldTranslate = TranslationText.NeedsTranslation(text);
                    long version = ++newest;
                    translating?.Cancel();
                    if (!shouldTranslate) ocrCaption.Clear();
                    pending.Writer.TryWrite((version, shouldTranslate ? text : ""));
                }
                pending.Writer.TryComplete();
            }
            catch (Exception exception) { pending.Writer.TryComplete(exception); }
        }
    }

    private void ToggleOcrPause()
    {
        if (watchedRegion is not { } region || resumeAfterAdjustment is not null) return;
        if (ocrRun is null) StartOcr(region);
        else PauseOcr();
    }

    private void PauseOcr(bool keepCaption = false)
    {
        Cancel(ref ocrRun);
        CancelUnusedPreparation();
        if (keepCaption) ocrCaption.MarkStopped();
        else ocrCaption.Clear();
        ocrFrame?.SetPaused(true);
        RefreshMenu();
    }

    private void StopOcr()
    {
        watchedRegion = null;
        resumeAfterAdjustment = null;
        Cancel(ref ocrRun);
        CancelUnusedPreparation();
        ocrCaption.Clear();
        ocrFrame?.Dispose();
        ocrFrame = null;
        RefreshMenu();
    }

    private void ShowOcrFrame()
    {
        if (watchedRegion is not { } area || pickerOpen) return;
        ocrFrame ??= new OcrRegionFrame(BeginOcrAdjustment, CompleteOcrAdjustment, ToggleOcrPause, StopOcr);
        ocrFrame.Show(area, paused: ocrRun is null);
    }

    private void BeginOcrAdjustment()
    {
        resumeAfterAdjustment = ocrRun is not null;
        Cancel(ref ocrRun);
        ocrCaption.Clear();
    }

    private void CompleteOcrAdjustment(ScreenRegion region)
    {
        if (disposed || watchedRegion is null || resumeAfterAdjustment is not { } wasRunning) return;
        resumeAfterAdjustment = null;
        if (wasRunning) StartOcr(region);
        else
        {
            watchedRegion = region;
            ShowOcrFrame();
            RefreshMenu();
        }
    }

    private void ToggleAudio()
    {
        if (audioRun is not null) { StopAudio(); return; }
        AudioSource? source = selectedAudio is { } last ? sources.ListSources().FirstOrDefault(candidate => candidate.Id == last.Id) : null;
        if (source is null) { openTrayMenu(); return; }
        StartAudio(source);
    }

    public void RefreshAudioSources()
    {
        audioSources.Items.Clear();
        foreach (AudioSource source in sources.ListSources())
        {
            var item = TrayMenu.Add(audioSources, source.IsActive ? $"{source.DisplayName} · 活跃" : source.DisplayName,
                () => { if (audioRun is null || selectedAudio?.Id != source.Id) StartAudio(source); });
            item.IsChecked = source.Id == selectedAudio?.Id;
        }
        if (audioSources.Items.Count == 0)
            audioSources.Items.Add(new NativeMenuItem("暂无可选程序") { IsEnabled = false });
    }

    private void StartAudio(AudioSource source)
    {
        if (disposed) return;
        StopAudio(cancelPreparation: false);
        selectedAudio = source;
        var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        audioRun = run;
        var window = new AudioSubtitleWindow(source.DisplayName, WindowsScreenCapture.CursorDisplay(), settings,
            placements.Audio, position => RememberPositions(placements with { Audio = position }),
            () => StopAudio(keepWindow: true));
        audioWindow = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(audioWindow, window)) return;
            audioWindow = null;
            StopAudio();
        };
        window.Show();
        RefreshMenu();
        PrepareModel();
        AppSettings audioSettings = settings;
        Run(() => RunSubtitlesAsync(source, audioSettings, window, run));
    }

    private async Task RunSubtitlesAsync(AudioSource source, AppSettings audioSettings,
        AudioSubtitleWindow window, CancellationTokenSource run)
    {
        try
        {
            var subtitles = new AudioSubtitleSession(createSpeechTranscriber(audioSettings), translator,
                translator as ISubtitleParagraphTranslator, tuning.Audio, createSpeakerDiarizer(audioSettings));
            await foreach (SubtitleUpdate update in subtitles.RunAsync(audioCapture.CaptureAsync(source, run.Token),
                audioSettings.SourceLanguage, run.Token))
            {
                if (run.IsCancellationRequested || !ReferenceEquals(audioRun, run)) break;
                if (update is SubtitleUpdate.SpeakersUnavailable unavailable) feedback = unavailable.Message;
                window.Apply(update);
            }
        }
        catch (Exception) when (!ReferenceEquals(audioRun, run)) { }
        finally
        {
            if (ReferenceEquals(audioRun, run)) StopAudio(keepWindow: true);
            run.Dispose();
        }
    }

    private void StopAudio(bool cancelPreparation = true, bool keepWindow = false)
    {
        Cancel(ref audioRun);
        if (cancelPreparation) CancelUnusedPreparation();
        if (keepWindow) audioWindow?.MarkStopped();
        else
        {
            AudioSubtitleWindow? window = audioWindow;
            audioWindow = null;
            if (window?.IsVisible == true) window.Close();
        }
        RefreshMenu();
    }

    private void RememberPositions(OverlayPositions positions)
    {
        placements = positions;
        placementTimer.Stop();
        placementTimer.Start();
    }

    private void SavePendingPositions()
    {
        if (!placementTimer.IsEnabled) return;
        placementTimer.Stop();
        try { store.SaveOverlayPositions(placements); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            feedback = $"保存译文窗口位置失败。{error.Message}";
        }
    }

    private void ShowInstant(string text, ScreenRegion? area, TimeSpan? duration = null,
        bool copyable = false, Action? close = null, bool draggable = false, bool keepDraggedPosition = false,
        Action? stop = null)
    {
        if (pickerOpen || disposed) return;
        ScreenRegion anchor = area ?? CursorAnchor();
        instantCaption.Show(text, WindowsScreenCapture.GetDisplay(anchor.DisplayId), anchor,
            duration: duration, copyable: copyable, close: close,
            draggable: draggable, keepDraggedPosition: keepDraggedPosition, stop: stop);
    }

    private static ScreenRegion CursorAnchor()
    {
        (int x, int y) = WindowsScreenCapture.CursorPosition();
        DesktopDisplay display = WindowsScreenCapture.DisplayAt(x, y);
        return new ScreenRegion(display.Id, x - display.X, y - display.Y, 1, 1);
    }

    private void RefreshMenu()
    {
        autoMenu.IsChecked = settings.AutoSelection;
        regionMenu.Header = watchedRegion is null ? "开始持续 OCR" : "停止持续 OCR";
        pauseOcrMenu.Header = ocrRun is null ? "恢复持续 OCR" : "暂停持续 OCR";
        pauseOcrMenu.IsEnabled = watchedRegion is not null;
        audioMenu.Header = audioRun is null ? "开始音频字幕" : "停止音频字幕";
        setTrayTooltip(audioRun is not null ? "桌面翻译 · 音频字幕"
            : ocrRun is not null ? "桌面翻译 · 正在监测选区" : "桌面翻译");
    }

    private void Report(string message)
    {
        if (disposed) return;
        feedback = message;
        if (message.Length > 200) message = message[..200];
        ShowInstant(message, null, TimeSpan.FromSeconds(7));
    }

    private async void Run(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Report(exception.Message); }
    }

    private static Channel<T> Latest<T>() => Channel.CreateBounded<T>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    private static void Cancel(ref CancellationTokenSource? active)
    {
        CancellationTokenSource? previous = active;
        active = null;
        previous?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        hotkeys?.Dispose();
        instantCaption.Dispose();
        ocrCaption.Dispose();
        StopAudio();
        SavePendingPositions();
        ocrFrame?.Dispose();
        if (ocr is IDisposable resource) await Task.Run(resource.Dispose).ConfigureAwait(false);
        await translator.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}
