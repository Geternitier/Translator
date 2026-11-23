using Avalonia.Controls;
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

    private readonly SettingsStore store = new();
    private readonly GgufModelCatalog catalog = new();
    internal event Action<AppSettings>? SettingsSaved;

    public SettingsWindow()
    {
        InitializeComponent();

        SettingsLoadResult result = store.Load();
        AppSettings settings = result.Settings;
        ModelDirectoryBox.Text = settings.ModelDirectory;
        BindChoices(SourceLanguageBox, Languages, settings.SourceLanguage);
        UseGpuBox.IsChecked = settings.UseGpu;
        AllowCpuFallbackBox.IsChecked = settings.AllowCpuFallback;
        BilingualBox.IsChecked = settings.BilingualSubtitles;
        SubtitleFontBox.Value = settings.SubtitleFontSize;
        SubtitleOffsetBox.Value = settings.SubtitleBottomOffset;
        ContinuousOcrKeyBox.Text = settings.Shortcuts.ContinuousOcr;
        AudioKeyBox.Text = settings.Shortcuts.Audio;
        Feedback.Text = result.Error;
        TryShowModels(settings.ModelDirectory, settings.ModelFileName, restoreMissing: true);
    }

    internal void MonitorStatus(Func<string> status)
    {
        RuntimeStatus.Text = status();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RuntimeStatus.Text = status();
        timer.Start();
        Closed += (_, _) => timer.Stop();
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
            if (TryShowModels(directory, SelectedModel)) Feedback.Text = "请选择模型，保存后生效。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            Feedback.Text = $"无法打开目录。{exception.Message}";
        }
    }

    private string SelectedModel => (ModelBox.SelectedItem as ModelChoice)?.File.FileName ?? "";

    private void RefreshModelDirectory(object? sender, RoutedEventArgs args)
    {
        TryShowModels(ModelDirectoryBox.Text ?? "", SelectedModel);
    }

    private void SaveSettings(object? sender, RoutedEventArgs args)
    {
        try
        {
            string directory = SettingsStore.NormalizeDirectory(ModelDirectoryBox.Text);
            var shortcuts = new ShortcutSettings
            {
                ContinuousOcr = ContinuousOcrKeyBox.Text?.Trim() ?? "", Audio = AudioKeyBox.Text?.Trim() ?? ""
            };
            if (shortcuts.Values().Select(Hotkey.Parse).Distinct().Count() != shortcuts.Values().Length)
                throw new FormatException("快捷键不可重复。");
            AppSettings previous = store.Load().Settings;
            string model = SelectedModel;
            if (model.Length != 0 && (directory != previous.ModelDirectory || model != previous.ModelFileName)
                && catalog.Inspect(directory, model).Status != ModelFileStatus.Available)
                throw new ArgumentException("请选择目录中可读取的 GGUF 模型。");
            AppSettings settings = previous with
            {
                ModelDirectory = directory,
                SourceLanguage = ReadChoice<SourceLanguage>(SourceLanguageBox),
                ModelFileName = model,
                UseGpu = UseGpuBox.IsChecked == true, AllowCpuFallback = AllowCpuFallbackBox.IsChecked == true,
                BilingualSubtitles = BilingualBox.IsChecked == true,
                SubtitleFontSize = (int)(SubtitleFontBox.Value ?? 22), SubtitleBottomOffset = (int)(SubtitleOffsetBox.Value ?? 64),
                Shortcuts = shortcuts
            };
            store.Save(settings);
            SettingsSaved?.Invoke(settings);
            ModelDirectoryBox.Text = directory;
            Feedback.Text = "设置已保存。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or FormatException)
        {
            Feedback.Text = $"保存失败。{exception.Message}";
        }
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
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ModelBox.ItemsSource = null;
            Feedback.Text = $"无法读取模型目录。{exception.Message}";
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
