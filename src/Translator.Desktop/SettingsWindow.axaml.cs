using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Translator.Core;
using Translator.Inference;
using Translator.Platform.Windows;

namespace Translator.Desktop;

public sealed partial class SettingsWindow : Window
{
    private static readonly Choice<SourceLanguage>[] Languages =
    [
        new(SourceLanguage.Automatic, "自动检测"),
        new(SourceLanguage.English, "英语"),
        new(SourceLanguage.Japanese, "日语")
    ];
    private static readonly Choice<AudioAsrBackend>[] AudioBackends =
    [
        new(AudioAsrBackend.Whisper, "Whisper Base"),
        new(AudioAsrBackend.Nemotron, "Nemotron 3.5 · 流式"),
        new(AudioAsrBackend.Qwen3Asr, "Qwen3-ASR 0.6B · 本地分块")
    ];

    private readonly SettingsStore store = new();
    private readonly GgufModelCatalog catalog = new();
    private AppSettings savedSettings = new();
    private string? listedModelDirectory;
    private Control? invalidField;
    private bool loading;
    internal event Action<AppSettings>? SettingsSaved;

    public SettingsWindow()
    {
        InitializeComponent();
        SubtitleFontBox.Minimum = AppSettings.MinimumSubtitleFontSize;
        SubtitleFontBox.Maximum = AppSettings.MaximumSubtitleFontSize;
        SubtitleOffsetBox.Minimum = AppSettings.MinimumSubtitleBottomOffset;
        SubtitleOffsetBox.Maximum = AppSettings.MaximumSubtitleBottomOffset;
        InitializeWorkbench();
        SettingsLoadResult result = store.Load();
        SetValues(result.Settings);
        Feedback.Text = result.Error ?? "设置会保存在本机。";
        ObserveEdits(ModelDirectoryBox, ModelBox, SourceLanguageBox, AudioAsrBox, AsrDirectoryBox, UseGpuBox,
            AllowCpuFallbackBox, BilingualBox, SpeakerLabelsBox, SubtitleFontBox, SubtitleOffsetBox, ContinuousOcrKeyBox, AudioKeyBox);
        UpdateWorkbench();
    }

    private void SetValues(AppSettings settings)
    {
        loading = true;
        savedSettings = settings;
        ModelDirectoryBox.Text = settings.ModelDirectory;
        BindChoices(SourceLanguageBox, Languages, settings.SourceLanguage);
        BindChoices(AudioAsrBox, AudioBackends, settings.AudioAsrBackend);
        AsrDirectoryBox.Text = settings.AudioAsrModelDirectory;
        UseGpuBox.IsChecked = settings.UseGpu;
        AllowCpuFallbackBox.IsChecked = settings.AllowCpuFallback;
        BilingualBox.IsChecked = settings.BilingualSubtitles;
        SpeakerLabelsBox.IsChecked = settings.SpeakerLabels;
        SubtitleFontBox.Value = settings.SubtitleFontSize;
        SubtitleOffsetBox.Value = settings.SubtitleBottomOffset;
        ContinuousOcrKeyBox.Text = settings.Shortcuts.ContinuousOcr;
        AudioKeyBox.Text = settings.Shortcuts.Audio;
        TryShowModels(settings.ModelDirectory, settings.ModelFileName, restoreMissing: true);
        loading = false;
        UpdateDraft();
    }

    private void ObserveEdits(params Control[] controls)
    {
        foreach (Control control in controls)
        {
            control.PropertyChanged += (_, args) =>
            {
                if (loading || (args.Property != TextBox.TextProperty && args.Property != SelectingItemsControl.SelectedItemProperty
                    && args.Property != ToggleButton.IsCheckedProperty && args.Property != NumericUpDown.ValueProperty
                    && args.Property != NumericUpDown.TextProperty)) return;
                if (invalidField == control) invalidField = null;
                UpdateDraft();
            };
        }
    }

    private AppSettings ReadDraft(AppSettings baseline) => baseline with
    {
        ModelDirectory = ModelDirectoryBox.Text?.Trim() ?? "",
        ModelFileName = SelectedModel,
        SourceLanguage = ReadChoice<SourceLanguage>(SourceLanguageBox),
        AudioAsrBackend = ReadChoice<AudioAsrBackend>(AudioAsrBox),
        AudioAsrModelDirectory = AsrDirectoryBox.Text?.Trim() ?? "",
        UseGpu = UseGpuBox.IsChecked == true,
        AllowCpuFallback = AllowCpuFallbackBox.IsChecked == true,
        BilingualSubtitles = BilingualBox.IsChecked == true,
        SpeakerLabels = SpeakerLabelsBox.IsChecked == true,
        SubtitleFontSize = ReadNumber(SubtitleFontBox),
        SubtitleBottomOffset = ReadNumber(SubtitleOffsetBox),
        Shortcuts = new ShortcutSettings
        {
            ContinuousOcr = ContinuousOcrKeyBox.Text?.Trim() ?? "", Audio = AudioKeyBox.Text?.Trim() ?? ""
        }
    };

    private static int ReadNumber(NumericUpDown control) =>
        int.TryParse(control.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value)
            ? value : control.Text is null && control.Value is { } number ? (int)number : 0;

    private void UpdateDraft()
    {
        AppSettings draft = ReadDraft(savedSettings);
        bool dirty = draft != savedSettings;
        SaveButton.IsEnabled = DiscardButton.IsEnabled = dirty;
        demoSpeaker.IsVisible = draft.SpeakerLabels;
        demoOriginal.IsVisible = draft.BilingualSubtitles;
        demoTranslation.FontSize = draft.SubtitleFontSize == 0 ? savedSettings.SubtitleFontSize
            : Math.Clamp(draft.SubtitleFontSize, AppSettings.MinimumSubtitleFontSize, AppSettings.MaximumSubtitleFontSize);
        if (invalidField is null) SetFeedback(dirty ? "有未保存的修改。" : "设置会保存在本机。");
    }

    private async void BrowseModels(object? sender, RoutedEventArgs args)
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "选择模型目录", AllowMultiple = false });
            if (folders.Count == 0)
            {
                return;
            }

            using IStorageFolder folder = folders[0];
            string directory = SettingsStore.NormalizeDirectory(folder.TryGetLocalPath());
            ModelDirectoryBox.Text = directory;
            if (TryShowModels(directory, SelectedModel)) SetFeedback("请选择模型，保存后生效。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            SetFeedback($"无法打开目录。{exception.Message}", error: true);
        }
    }

    private string SelectedModel => (ModelBox.SelectedItem as ModelChoice)?.File.FileName ?? "";

    private void RefreshModelDirectory(object? sender, RoutedEventArgs args)
    {
        string directory = ModelDirectoryBox.Text?.Trim() ?? "";
        if (string.Equals(directory, listedModelDirectory, StringComparison.OrdinalIgnoreCase)) return;
        bool returningToSavedDirectory = string.Equals(directory, savedSettings.ModelDirectory, StringComparison.OrdinalIgnoreCase);
        TryShowModels(directory, returningToSavedDirectory ? savedSettings.ModelFileName : SelectedModel, restoreMissing: returningToSavedDirectory);
    }

    private void SaveSettings(object? sender, RoutedEventArgs args)
    {
        Control? field = null;
        try
        {
            AppSettings previous = store.Load().Settings;
            AppSettings draft = ReadDraft(previous);
            field = ModelDirectoryBox;
            string directory = SettingsStore.NormalizeDirectory(draft.ModelDirectory);
            field = ModelBox;
            if (draft.ModelFileName.Length != 0 && (directory != previous.ModelDirectory || draft.ModelFileName != previous.ModelFileName)
                && catalog.Inspect(directory, draft.ModelFileName).Status != ModelFileStatus.Available)
                throw new ArgumentException("请选择目录中可读取的 GGUF 模型。");
            field = SubtitleFontBox;
            if (draft.SubtitleFontSize is < AppSettings.MinimumSubtitleFontSize or > AppSettings.MaximumSubtitleFontSize)
                throw RangeError("浮窗译文字号", SubtitleFontBox);
            field = SubtitleOffsetBox;
            if (draft.SubtitleBottomOffset is < AppSettings.MinimumSubtitleBottomOffset or > AppSettings.MaximumSubtitleBottomOffset)
                throw RangeError("字幕底部间距", SubtitleOffsetBox);
            field = AsrDirectoryBox;
            string asrDirectory = SettingsStore.NormalizeAsrDirectory(draft.AudioAsrModelDirectory);
            field = ContinuousOcrKeyBox;
            Hotkey ocrKey = Hotkey.Parse(draft.Shortcuts.ContinuousOcr);
            field = AudioKeyBox;
            if (ocrKey == Hotkey.Parse(draft.Shortcuts.Audio)) throw new FormatException("快捷键不可重复。");
            field = null;
            AppSettings settings = draft with
            {
                ModelDirectory = directory,
                AudioAsrModelDirectory = asrDirectory
            };
            store.Save(settings);
            SettingsSaved?.Invoke(settings);
            invalidField = null;
            SetValues(settings);
            SetFeedback("设置已保存。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or FormatException)
        {
            invalidField = field;
            if (field is not null)
                Dispatcher.UIThread.Post(() => { field.Focus(); field.BringIntoView(); }, DispatcherPriority.Loaded);
            SetFeedback($"保存失败。{exception.Message}", error: true);
        }
    }

    private static FormatException RangeError(string label, NumericUpDown field) =>
        new($"{label}应为 {field.Minimum} 至 {field.Maximum} 的整数。");

    private void DiscardSettings(object? sender, RoutedEventArgs args)
    {
        invalidField = null;
        SetValues(savedSettings);
        SetFeedback("已撤销未保存的修改。");
    }

    private void SetFeedback(string text, bool error = false)
    {
        Feedback.Text = text;
        Feedback.Classes.Set("validation-error", error);
    }

    private bool TryShowModels(string directory, string selectedName, bool restoreMissing = false)
    {
        try
        {
            directory = SettingsStore.NormalizeDirectory(directory);
            var models = catalog.Inspect(directory).Select(file => new ModelChoice(file)).ToList();
            ModelChoice? selected = models.Find(item => string.Equals(item.File.FileName, selectedName, StringComparison.OrdinalIgnoreCase));
            ModelChoice? previous = ModelBox.SelectedItem as ModelChoice;
            if (selected is null && selectedName.Length != 0
                && (restoreMissing || string.Equals(previous?.File.Path, Path.Combine(directory, selectedName),
                    StringComparison.OrdinalIgnoreCase)))
            {
                selected = new ModelChoice(catalog.Inspect(directory, selectedName));
                models.Insert(0, selected);
            }
            ModelBox.ItemsSource = models;
            ModelBox.SelectedItem = selected;
            listedModelDirectory = directory;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ModelBox.ItemsSource = null;
            listedModelDirectory = null;
            SetFeedback($"无法读取模型目录。{exception.Message}", error: true);
            return false;
        }
    }

    private sealed record ModelChoice(ModelFile File)
    {
        public override string ToString() => File.Status switch
        {
            ModelFileStatus.Available => File.FileName,
            ModelFileStatus.InvalidHeader => $"{File.FileName}（模型格式不支持）",
            ModelFileStatus.Unreadable => $"{File.FileName}（无法读取）",
            _ => $"{File.FileName}（文件不完整）"
        };
    }

    private static void BindChoices<T>(ComboBox box, Choice<T>[] choices, T value) where T : struct, Enum
    {
        box.ItemsSource = choices;
        box.SelectedItem = choices.First(choice => EqualityComparer<T>.Default.Equals(choice.Value, value));
    }

    private static T ReadChoice<T>(ComboBox box) where T : struct, Enum => ((Choice<T>)box.SelectedItem!).Value;

    private sealed record Choice<T>(T Value, string Label) where T : struct, Enum
    {
        public override string ToString() => Label;
    }
}
