using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Translator.Core;

namespace Translator.Desktop;

internal enum WorkbenchPage { Home, Selection, Ocr, Audio, Settings }

public sealed partial class SettingsWindow
{
    private static readonly (WorkbenchPage Page, SakuraGlyph Glyph, string Title, string Description)[] Pages =
    [
        (WorkbenchPage.Home, SakuraGlyph.Home, "工作台", "译文在本机处理，支持英语和日语转简体中文。"),
        (WorkbenchPage.Selection, SakuraGlyph.Selection, "选区翻译", "选中文本后，译文浮窗出现在选区附近。"),
        (WorkbenchPage.Ocr, SakuraGlyph.Scan, "持续 OCR", "监测固定屏幕区域，在内容变化时刷新译文。"),
        (WorkbenchPage.Audio, SakuraGlyph.Audio, "音频字幕", "选择一个应用，实时转写和翻译该应用的声音。"),
        (WorkbenchPage.Settings, SakuraGlyph.Settings, "设置", "管理本地模型、翻译偏好、字幕显示与快捷键。")
    ];

    private static readonly (WorkbenchPage Page, SakuraGlyph Glyph, string Title, string Text, string Hint)[] Previews =
    [
        (WorkbenchPage.Selection, SakuraGlyph.Selection, "选区译文", "微风把樱花的香气送进敞开的窗户。", "停止生成会保留已出现的译文。"),
        (WorkbenchPage.Ocr, SakuraGlyph.Scan, "持续 OCR", "花瓣上的雨珠闪闪发亮。", "暂停保留译文和选区，恢复后继续监测。"),
        (WorkbenchPage.Audio, SakuraGlyph.Audio, "音频字幕", "欢迎来到今天的节目。", "停止字幕会保留历史，可继续复制。")
    ];

    private readonly Dictionary<WorkbenchPage, Button> navigation = [];
    private readonly List<Button> previewTabs = [];
    private readonly Dictionary<WorkbenchPage, TextBlock> toolDetails = [];
    private readonly ContentControl previewIcon = new() { Margin = new Thickness(0, 4, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock previewTitle = new() { FontSize = 11, Foreground = SakuraVisuals.CaptionSecondary, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock previewText = new() { FontSize = 17, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock demoSpeaker = new() { Text = "说话者 1", FontSize = 11, Foreground = SakuraVisuals.CaptionSecondary };
    private readonly TextBlock demoOriginal = new() { Text = "Welcome to today’s episode.", FontSize = 12, Foreground = SakuraVisuals.CaptionSecondary, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock demoTranslation = new() { Text = "欢迎来到今天的节目。", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private WindowsDesktopController? runtime;
    private WorkbenchPage page;
    private CaptionActions? previewActions;
    private int previewIndex;
    private bool previewStopped;

    private void InitializeWorkbench()
    {
        using (Stream illustration = AssetLoader.Open(new Uri("avares://Translator.Desktop/Assets/sakura-mascot.webp")))
        {
            var bitmap = new Bitmap(illustration);
            HeroMascot.Source = bitmap;
            Closed += (_, _) => bitmap.Dispose();
        }
        ModelLabel.Content = SakuraVisuals.Label(SakuraGlyph.Model, "本地模型");
        SetButton(ModelSettingsButton, SakuraGlyph.Arrow, "模型设置");
        SetButton(TrayButton, SakuraGlyph.Tray, "托盘菜单");
        SetButton(SelectionHeroButton, SakuraGlyph.Selection, "打开选区翻译", light: true);
        SetButton(PreviewHeroButton, SakuraGlyph.Window, "查看浮窗");
        SetButton(SelectionPreviewButton, SakuraGlyph.Window, "查看浮窗");
        SetButton(OcrEnd, SakuraGlyph.Close, "结束 OCR");
        SetButton(AudioStop, SakuraGlyph.Stop, "停止字幕");
        SetButton(AudioRefresh, SakuraGlyph.Refresh, "刷新列表");
        SetButton(AudioSettingsButton, SakuraGlyph.Settings, "字幕设置");
        SetButton(BrowseButton, SakuraGlyph.Folder, "浏览");
        LocalHeading.Content = SakuraVisuals.Label(SakuraGlyph.Model, "本地模型");
        DisplayHeading.Content = SakuraVisuals.Label(SakuraGlyph.Type, "语言与显示");
        AudioHeading.Content = SakuraVisuals.Label(SakuraGlyph.Audio, "音频转写");
        ShortcutHeading.Content = SakuraVisuals.Label(SakuraGlyph.Keyboard, "全局快捷键");

        foreach (var item in Pages)
        {
            string? section = item.Page switch { WorkbenchPage.Home => "概览", WorkbenchPage.Selection => "翻译工具", WorkbenchPage.Settings => "应用", _ => null };
            if (section is not null)
                Navigation.Children.Add(new TextBlock { Text = section, Classes = { "secondary" }, FontSize = 11, Margin = new Thickness(8, 9, 0, 4) });
            var button = new Button { Classes = { "sakura-button", "sakura-nav" } };
            SetButton(button, item.Glyph, item.Title);
            button.Click += (_, _) => Navigate(item.Page);
            navigation.Add(item.Page, button);
            Navigation.Children.Add(button);
        }

        AddTool(WorkbenchPage.Selection, "选区翻译", "在原文附近阅读译文。", SakuraGlyph.Selection, "#FCE5EF", "#942557");
        AddTool(WorkbenchPage.Ocr, "持续 OCR", "识别固定的屏幕区域。", SakuraGlyph.Scan, "#F0E8FA", "#674591");
        AddTool(WorkbenchPage.Audio, "音频字幕", "为应用声音生成字幕。", SakuraGlyph.Audio, "#FFEDE5", "#A84737");
        InitializePreviews();
        AddSettingsLink("模型", ModelSection);
        AddSettingsLink("显示", DisplaySection);
        AddSettingsLink("转写", AudioSection);
        AddSettingsLink("快捷键", ShortcutSection);
        Navigate(WorkbenchPage.Home);
    }

    internal void AttachRuntime(WindowsDesktopController? controller, Func<string> status, MenuFlyout tray)
    {
        runtime = controller;
        TrayButton.Flyout = tray;
        void Refresh()
        {
            string text = status();
            if (RuntimeStatus.Text != text) RuntimeStatus.Text = text;
            if (WorkbenchFeedback.Text != text) WorkbenchFeedback.Text = text;
            UpdateWorkbench();
        }
        Refresh();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    internal void Navigate(WorkbenchPage destination)
    {
        var description = Pages.First(item => item.Page == destination);
        bool changedPage = page != destination;
        page = destination;
        PageTitle.Text = description.Title;
        PageDescription.Text = description.Description;
        HomePanel.IsVisible = destination == WorkbenchPage.Home;
        SelectionPanel.IsVisible = destination == WorkbenchPage.Selection;
        OcrPanel.IsVisible = destination == WorkbenchPage.Ocr;
        AudioPanel.IsVisible = destination == WorkbenchPage.Audio;
        SettingsPanel.IsVisible = destination == WorkbenchPage.Settings;
        WorkbenchScroll.IsVisible = !SettingsPanel.IsVisible;
        foreach ((WorkbenchPage key, Button button) in navigation) button.Classes.Set("selected", key == destination);
        if (changedPage && destination != WorkbenchPage.Settings) WorkbenchScroll.Offset = default;
        if (destination == WorkbenchPage.Audio) LoadAudioPrograms();
        UpdateWorkbench();
    }

    private void UpdateWorkbench()
    {
        AppSettings settings = runtime?.Settings ?? savedSettings;
        bool hasRuntime = runtime is not null;
        bool selection = hasRuntime && settings.AutoSelection;
        OcrMode ocr = runtime?.OcrState ?? OcrMode.Idle;
        bool hasRegion = runtime?.HasOcrRegion == true;
        bool paused = ocr == OcrMode.Paused;
        bool canAdjustOcr = ocr is not (OcrMode.Selecting or OcrMode.Adjusting);
        bool audio = runtime?.IsAudioRunning == true;

        ModelSummary.Text = settings.ModelFileName.Length == 0 ? "尚未选择 GGUF 模型" : settings.ModelFileName;
        SelectionState.Text = selection ? "划词监听已开启" : "划词监听已关闭";
        SetButton(SelectionToggle, SakuraGlyph.Selection, selection ? "关闭划词监听" : "开启划词监听", light: true);
        SelectionToggle.IsEnabled = hasRuntime;
        OcrState.Text = ocr switch
        {
            OcrMode.Selecting => "正在框选", OcrMode.Adjusting => "正在调整选区", OcrMode.Paused => "已暂停",
            OcrMode.Running => "正在监测", _ => "未开始"
        };
        OcrShortcut.Text = $"全局快捷键 · {settings.Shortcuts.ContinuousOcr}";
        SetButton(OcrStart, SakuraGlyph.Scan, hasRegion ? "重新框选" : "开始框选", light: true);
        SetButton(OcrPause, paused ? SakuraGlyph.Play : SakuraGlyph.Pause, paused ? "继续识别" : "暂停识别");
        OcrStart.IsEnabled = hasRuntime && canAdjustOcr;
        OcrPause.IsEnabled = canAdjustOcr && hasRegion;
        OcrEnd.IsEnabled = ocr != OcrMode.Selecting && hasRegion;
        string audioState = runtime?.AudioStatus ?? "未开始";
        string stateText = runtime?.CurrentAudioSource is { } source
            ? $"{audioState} · {source.DisplayName}" : audioState;
        if (AudioState.Text != stateText) AudioState.Text = stateText;
        AudioShortcut.Text = $"全局快捷键 · {settings.Shortcuts.Audio}";
        SetButton(AudioStart, SakuraGlyph.Play, audio ? "切换音频程序" : "开始字幕", light: true);
        AudioStop.IsEnabled = audio;
        AudioRefresh.IsEnabled = hasRuntime;
        UpdateAudioButtons();

        toolDetails[WorkbenchPage.Selection].Text = SelectionState.Text;
        toolDetails[WorkbenchPage.Ocr].Text = ocr == OcrMode.Idle ? settings.Shortcuts.ContinuousOcr : OcrState.Text;
        toolDetails[WorkbenchPage.Audio].Text = runtime?.CurrentAudioSource is null ? settings.Shortcuts.Audio : audioState;
    }

    private void AddTool(WorkbenchPage destination, string title, string description, SakuraGlyph glyph, string tint, string ink)
    {
        var fill = new StackPanel { Spacing = 5 };
        var tile = new Border
        {
            Background = Brush.Parse(tint), CornerRadius = new CornerRadius(9), Width = 32, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left, Child = SakuraVisuals.Icon(glyph, Brush.Parse(ink), 19)
        };
        var detail = new TextBlock { FontSize = 11, Classes = { "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis };
        var label = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold };
        fill.Children.Add(tile);
        fill.Children.Add(label);
        fill.Children.Add(new TextBlock { Text = description, Classes = { "secondary" }, FontSize = 12 });
        fill.Children.Add(detail);
        var button = new Button
        {
            Content = fill, Classes = { "sakura-button" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(11), Margin = new Thickness(0, 0, destination == WorkbenchPage.Audio ? 0 : 7, 0)
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => Navigate(destination);
        toolDetails.Add(destination, detail);
        QuickTools.Children.Add(button);
    }

    private void InitializePreviews()
    {
        for (int i = 0; i < Previews.Length; i++)
        {
            int index = i;
            var button = new Button { Classes = { "sakura-button", "sakura-nav" }, Content = Previews[i].Title, FontSize = 12, Padding = new Thickness(8, 3), MinHeight = 31 };
            button.Click += (_, _) => ShowPreview(index);
            previewTabs.Add(button);
            PreviewTabs.Children.Add(button);
        }
        previewActions = new CaptionActions(this);
        Closed += (_, _) => previewActions.Dispose();
        var header = new DockPanel();
        DockPanel.SetDock(previewActions.View, Dock.Right);
        DockPanel.SetDock(previewIcon, Dock.Left);
        header.Children.Add(previewActions.View);
        header.Children.Add(previewIcon);
        header.Children.Add(previewTitle);
        HomePreview.Content = SakuraVisuals.CaptionSurface(new StackPanel { Spacing = 5, Children = { header, previewText } });
        SettingsPreview.Content = SakuraVisuals.CaptionSurface(new StackPanel
        {
            Spacing = 5,
            Children =
            {
                SakuraVisuals.Label(SakuraGlyph.Audio, "字幕预览", SakuraVisuals.CaptionSecondary),
                demoSpeaker, demoOriginal, demoTranslation
            }
        });
        ShowPreview(0);
    }

    private void ShowPreview(int index)
    {
        previewIndex = index;
        previewStopped = false;
        previewIcon.Content = SakuraVisuals.Icon(Previews[index].Glyph, SakuraVisuals.CaptionSecondary, 14);
        previewActions!.ResetCopy();
        for (int i = 0; i < previewTabs.Count; i++) previewTabs[i].Classes.Set("selected", i == index);
        HomePreview.IsVisible = true;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var (scene, _, title, text, hint) = Previews[previewIndex];
        bool ocr = scene == WorkbenchPage.Ocr;
        bool audio = scene == WorkbenchPage.Audio;
        string state = previewStopped ? ocr ? "已暂停" : "已停止" : ocr ? "正在监测" : audio ? "正在转写" : "正在翻译";
        previewTitle.Text = $"{title} · {state}";
        previewText.Text = text;
        PreviewHint.Text = hint;
        previewActions!.SetActions(() => text, previewStopped && !ocr ? null : TogglePreview, ClosePreview,
            ocr ? previewStopped ? SakuraGlyph.Play : SakuraGlyph.Pause : SakuraGlyph.Stop,
            ocr ? previewStopped ? "继续识别" : "暂停识别" : audio ? "停止字幕" : "停止生成",
            ocr ? "结束 OCR" : audio ? "关闭字幕" : "关闭译文");
    }

    private void TogglePreview()
    {
        previewStopped = !previewStopped;
        UpdatePreview();
    }

    private void ClosePreview()
    {
        HomePreview.IsVisible = false;
        PreviewHint.Text = "已收起浮窗。选择任一场景可重新预览。";
        previewTabs[previewIndex].Focus();
    }

    private void AddSettingsLink(string title, Control control)
    {
        var button = new Button { Classes = { "sakura-button" }, Content = title, FontSize = 11, MinHeight = 30, Padding = new Thickness(7, 3) };
        button.Click += (_, _) => control.BringIntoView();
        SettingsLinks.Children.Add(button);
    }

    private void OpenSelection(object? sender, RoutedEventArgs args) => Navigate(WorkbenchPage.Selection);
    private void OpenPreview(object? sender, RoutedEventArgs args)
    {
        Navigate(WorkbenchPage.Home);
        ShowPreview(previewIndex);
        Dispatcher.UIThread.Post(() => { PreviewHeading.BringIntoView(); previewTabs[previewIndex].Focus(); }, DispatcherPriority.Loaded);
    }

    private void OpenModelSettings(object? sender, RoutedEventArgs args) => OpenSettingsSection(ModelSection);
    private void OpenAudioSettings(object? sender, RoutedEventArgs args) => OpenSettingsSection(AudioSection);

    private void OpenSettingsSection(Control control)
    {
        Navigate(WorkbenchPage.Settings);
        Dispatcher.UIThread.Post(() => control.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void ToggleSelection(object? sender, RoutedEventArgs args) { runtime?.ToggleAutoSelection(); UpdateWorkbench(); }
    private void StartOcr(object? sender, RoutedEventArgs args) { runtime?.PickOcrRegion(); UpdateWorkbench(); }
    private void ToggleOcrPause(object? sender, RoutedEventArgs args) { runtime?.ToggleOcrPause(); UpdateWorkbench(); }
    private void EndOcr(object? sender, RoutedEventArgs args) { runtime?.StopOcr(); UpdateWorkbench(); }
    private void RefreshAudio(object? sender, RoutedEventArgs args) => LoadAudioPrograms();
    private void StopAudio(object? sender, RoutedEventArgs args) { runtime?.StopAudioWithHistory(); UpdateWorkbench(); }
    private void AudioProgramSelected(object? sender, SelectionChangedEventArgs args) => UpdateAudioButtons();

    private void StartAudio(object? sender, RoutedEventArgs args)
    {
        if (AudioPrograms.SelectedItem is AudioChoice choice) runtime?.StartAudio(choice.Source);
        UpdateWorkbench();
    }

    private void LoadAudioPrograms()
    {
        string? current = (AudioPrograms.SelectedItem as AudioChoice)?.Source.Id ?? runtime?.LastAudioSource?.Id;
        var choices = runtime?.ListAudioSources().Select(item => new AudioChoice(item)).ToArray() ?? [];
        AudioPrograms.ItemsSource = choices;
        AudioPrograms.SelectedItem = choices.FirstOrDefault(choice => choice.Source.Id == current);
        AudioPrograms.IsVisible = choices.Length != 0;
        AudioEmpty.IsVisible = choices.Length == 0;
        UpdateAudioButtons();
    }

    private void UpdateAudioButtons()
    {
        if (AudioStart is null) return;
        AudioStart.IsEnabled = runtime is not null && AudioPrograms.SelectedItem is AudioChoice choice
            && (!runtime.IsAudioRunning || runtime.LastAudioSource?.Id != choice.Source.Id);
    }

    private static void SetButton(Button button, SakuraGlyph glyph, string text, bool light = false)
    {
        if (AutomationProperties.GetName(button) == text) return;
        AutomationProperties.SetName(button, text);
        button.Content = SakuraVisuals.Label(glyph, text, light ? Brushes.White : null);
    }

    private sealed record AudioChoice(AudioSource Source)
    {
        public override string ToString() => $"{Source.DisplayName} · {(Source.IsActive ? "正在播放" : "空闲")}";
    }
}
