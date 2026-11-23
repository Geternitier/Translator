using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Threading;
using Translator.Core;
using Translator.Inference;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal sealed class WindowsDesktopController : IAsyncDisposable
{
    private readonly SettingsStore store = new();
    private readonly RuntimeOptions tuning = RuntimePaths.Configuration.Options;
    private readonly WindowsScreenCapture screen = new(RuntimePaths.Configuration.Options.Ocr);
    private readonly ITextSelectionSource selection;
    private readonly IAudioSourceCatalog sources;
    private readonly AudioSubtitleSession subtitles;
    private readonly PaddleScreenOcr ocr = new(RuntimePaths.OcrDirectory);
    private readonly LlamaTranslator translator;
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
        IAudioSourceCatalog sources, IApplicationAudioCapture capture, ISpeechTranscriber speech,
        ILlamaRuntimeProvider llamaRuntimeProvider)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        this.setTrayTooltip = setTrayTooltip;
        this.openTrayMenu = openTrayMenu;
        this.sources = sources;
        selection = new WindowsSelectedTextSource(tuning.Llama.MaxInputCharacters);
        placements = store.LoadOverlayPositions();
        ocrCaption = new CaptionOverlay(position => RememberPositions(placements with { Ocr = position }));
        placementTimer.Tick += (_, _) => SavePendingPositions();
        SettingsLoadResult loaded = store.Load();
        settings = loaded.Settings;
        feedback = loaded.Error;
        translator = new LlamaTranslator(ReadTranslationOptions, llamaRuntimeProvider, tuning.Llama,
            () => IsSelectionOrOcrActive() || Volatile.Read(ref audioRun) is not null);
        subtitles = new AudioSubtitleSession(capture, speech, translator, translator, tuning.Audio);

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

    public string Status => string.Join('\n', new[] { translator.Status, RuntimePaths.Configuration.Error, feedback }.Concat(shortcutFeedback.Values)
        .Where(line => !string.IsNullOrEmpty(line)));

    public void ApplySettings(AppSettings changed)
    {
        ShortcutSettings previousShortcuts = settings.Shortcuts;
        SourceLanguage previousLanguage = settings.SourceLanguage;
        bool modelChanged = TranslationOptionsFor(settings) != TranslationOptionsFor(changed);
        Volatile.Write(ref settings, changed);
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
        if ((modelChanged || previousLanguage != changed.SourceLanguage) && audioRun is not null && selectedAudio is { } source)
            StartAudio(source);
        else audioWindow?.ApplySettings(changed);
        if (startingSelection || (modelChanged && settings.AutoSelection)) PrepareModel();
        RefreshMenu();
    }

    private bool IsSelectionOrOcrActive() =>
        Volatile.Read(ref settings).AutoSelection || Volatile.Read(ref ocrRun) is not null;

    private void PrepareModel()
    {
        if (disposed || modelPreparation is not null) return;
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

    private LlamaOptions ReadTranslationOptions() => TranslationOptionsFor(Volatile.Read(ref settings));

    internal static LlamaOptions TranslationOptionsFor(AppSettings current) => new(current.ModelDirectory, current.ModelFileName,
        current.LlamaExecutable, current.UseGpu, current.AllowCpuFallback);

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
        ShowInstant("正在翻译…", anchor, close: DismissSelection, draggable: true);
        string translated = await TranslateAsync(selected.Text, TranslationSource.Selection, [], token);
        token.ThrowIfCancellationRequested();
        ShowInstant(translated, anchor, TimeSpan.FromSeconds(Math.Clamp(translated.Length / 9.0, 5, 14)),
            copyable: true, close: DismissSelection, draggable: true, keepDraggedPosition: true);
    }

    private Task<string> TranslateAsync(string text, TranslationSource source, IReadOnlyList<string> context,
        CancellationToken token, string? detectedLanguage = null)
    {
        var request = new TranslationRequest(text, settings.SourceLanguage, source, context, detectedLanguage);
        return translator.TranslateAsync(request, token).AsTask();
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
                    string translated = await TranslateAsync(text, TranslationSource.ContinuousOcr, [], request.Token);
                    if (!request.IsCancellationRequested && version == newest && !pickerOpen)
                        ocrCaption.Show(translated, WindowsScreenCapture.GetDisplay(region.DisplayId), region,
                            draggable: true, keepDraggedPosition: true, savedPosition: placements.Ocr);
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
        else
        {
            Cancel(ref ocrRun);
            CancelUnusedPreparation();
            ocrCaption.Clear();
            ocrFrame?.SetPaused(true);
            RefreshMenu();
        }
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
            placements.Audio, position => RememberPositions(placements with { Audio = position }));
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
        SourceLanguage language = settings.SourceLanguage;
        Run(() => RunSubtitlesAsync(source, language, window, run));
    }

    private async Task RunSubtitlesAsync(AudioSource source, SourceLanguage language,
        AudioSubtitleWindow window, CancellationTokenSource run)
    {
        try
        {
            await foreach (IReadOnlyList<SubtitleLine> update in subtitles.RunAsync(source, language, run.Token))
            {
                if (run.IsCancellationRequested || !ReferenceEquals(audioRun, run)) break;
                window.Apply(update);
            }
        }
        catch (Exception) when (!ReferenceEquals(audioRun, run)) { }
        finally
        {
            if (ReferenceEquals(audioRun, run)) StopAudio();
            run.Dispose();
        }
    }

    private void StopAudio(bool cancelPreparation = true)
    {
        Cancel(ref audioRun);
        if (cancelPreparation) CancelUnusedPreparation();
        AudioSubtitleWindow? window = audioWindow;
        audioWindow = null;
        if (window?.IsVisible == true) window.Close();
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
        bool copyable = false, Action? close = null, bool draggable = false, bool keepDraggedPosition = false)
    {
        if (pickerOpen || disposed) return;
        ScreenRegion anchor = area ?? CursorAnchor();
        instantCaption.Show(text, WindowsScreenCapture.GetDisplay(anchor.DisplayId), anchor,
            duration: duration, copyable: copyable, close: close,
            draggable: draggable, keepDraggedPosition: keepDraggedPosition);
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
        await Task.Run(ocr.Dispose).ConfigureAwait(false);
        await translator.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}
