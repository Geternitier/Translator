using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Translator.Core;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal sealed class AudioSubtitleWindow : FloatingOverlay
{
    private readonly int maxLines = RuntimePaths.Configuration.Options.Audio.VisibleLines;
    private static readonly IBrush Secondary = SakuraVisuals.CaptionSecondary;
    private readonly StackPanel lines = new() { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ScrollViewer scroll;
    private readonly TextBlock heading;
    private readonly CaptionActions actions;
    private readonly string source;
    private readonly TextBlock waiting = new() { Text = "等待语音…", Foreground = Secondary };
    private readonly TextBlock preview = new() { Foreground = Secondary, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Dictionary<long, Row> rows = [];
    private readonly Queue<long> order = new();
    private AppSettings settings;
    private OverlayPosition? userPosition;
    private PixelPoint automaticPosition;
    private bool trackPosition;
    private bool stopped;
    private SpeakerSnapshot? speakers;

    public AudioSubtitleWindow(string source, DesktopDisplay display, AppSettings settings,
        OverlayPosition? savedPosition, Action<OverlayPosition> moved, Action stop) : base(clickThrough: false)
    {
        this.source = source;
        this.settings = settings;
        userPosition = savedPosition;
        if (savedPosition is { } preferred) display = WindowsScreenCapture.DisplayAt(preferred.X, preferred.Y);
        Title = $"{source} · 翻译字幕";
        MinWidth = 320;
        MinHeight = 130;
        double scale = OverlayGeometry.ScaleAt(this, display);
        Width = Math.Min(720, display.Width / scale - 28);
        Height = Math.Min(270, display.Height / scale / 2);
        var target = savedPosition is { } restored ? new PixelPoint(restored.X, restored.Y)
            : new PixelPoint(display.X + (int)((display.Width - Width * scale) / 2),
                display.Y + Math.Max(0, display.Height - (int)((Height + settings.SubtitleBottomOffset) * scale)));
        Position = OverlayGeometry.KeepVisible(this, display, target, (int)Math.Ceiling(Width * scale),
            (int)Math.Ceiling(Height * scale));
        lines.Children.Add(waiting);
        lines.Children.Add(preview);
        scroll = new ScrollViewer
        {
            Content = lines, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        heading = new TextBlock
        {
            Foreground = Secondary, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var title = new DockPanel { LastChildFill = true };
        var grip = SakuraVisuals.Icon(SakuraGlyph.Grip, Secondary, 16);
        grip.Margin = new Thickness(0, 0, 5, 0);
        DockPanel.SetDock(grip, Dock.Left);
        title.Children.Add(grip);
        title.Children.Add(heading);
        var dragArea = new Border { Background = Brushes.Transparent, Child = title, Cursor = new Cursor(StandardCursorType.SizeAll) };
        dragArea.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(dragArea).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
        };
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(scroll, 1);
        actions = new CaptionActions(this, "复制字幕历史");
        actions.SetActions(null, stop, Close, actionLabel: "停止字幕", closeLabel: "关闭字幕");
        var header = new DockPanel();
        DockPanel.SetDock(actions.View, Dock.Right);
        header.Children.Add(actions.View);
        header.Children.Add(dragArea);
        EnableKeyboardMovement(header);
        body.Children.Add(header);
        body.Children.Add(scroll);
        Content = SakuraVisuals.CaptionSurface(body);
        SetStatus("等待语音");
        Opened += (_, _) =>
        {
            automaticPosition = Position;
            trackPosition = true;
        };
        Closed += (_, _) => { trackPosition = false; actions.Dispose(); };
        PositionChanged += (_, args) =>
        {
            if (!trackPosition || WindowState != WindowState.Normal || (userPosition is null && args.Point == automaticPosition)) return;
            var position = new OverlayPosition(args.Point.X, args.Point.Y);
            if (position == userPosition) return;
            userPosition = position;
            moved(position);
        };
    }

    public string State { get; private set; } = "";

    public void ApplySettings(AppSettings changed)
    {
        if (userPosition is null && settings.SubtitleBottomOffset != changed.SubtitleBottomOffset)
        {
            DesktopDisplay display = WindowsScreenCapture.DisplayAt(Position.X, Position.Y);
            double scale = OverlayGeometry.ScaleAt(this, display);
            int move = (int)Math.Round((settings.SubtitleBottomOffset - changed.SubtitleBottomOffset) * scale);
            int maximum = display.Y + Math.Max(0, display.Height - (int)(Bounds.Height * scale));
            automaticPosition = new PixelPoint(Position.X, Math.Clamp(Position.Y + move, display.Y, maximum));
            Position = automaticPosition;
        }
        settings = changed;
        foreach (Row row in rows.Values) Update(row);
        RefreshSpeakerLabels();
        UpdatePreview();
    }

    public void Apply(SubtitleUpdate update)
    {
        bool follow = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 24;
        switch (update)
        {
            case SubtitleUpdate.Preview live:
                preview.Text = live.Text;
                if (!string.IsNullOrWhiteSpace(live.Text)) SetStatus("正在转写");
                if (!settings.BilingualSubtitles) return;
                break;
            case SubtitleUpdate.Lines translated:
                ApplyLines(translated.Items);
                if (translated.Items.Count != 0)
                    SetStatus(translated.Items.All(line => line.IsRevised) ? "段落已修订" : "正在转写");
                break;
            case SubtitleUpdate.Speakers labels:
                speakers = labels.Snapshot;
                RefreshSpeakerLabels();
                return;
            case SubtitleUpdate.SpeakersUnavailable unavailable:
                ToolTip.SetTip(heading, unavailable.Message);
                return;
        }
        UpdatePreview();
        if (follow) Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    public void MarkStopped()
    {
        if (stopped) return;
        stopped = true;
        actions.SetPrimary(null, SakuraGlyph.Stop, "停止字幕");
        SetStatus("已停止");
        waiting.Text = "已停止转写";
        preview.Text = "";
        UpdatePreview();
    }

    private void SetStatus(string value)
    {
        State = value;
        heading.Text = $"{source} · {value}";
    }

    private void ApplyLines(IReadOnlyList<SubtitleLine> update)
    {
        bool canCopy = rows.Count != 0;
        foreach (SubtitleLine line in update)
        {
            if (rows.TryGetValue(line.Id, out Row? existing))
            {
                existing.Line = line;
                Update(existing);
            }
            else if (!line.IsRevised)
            {
                if (order.Count == maxLines)
                {
                    long oldest = order.Dequeue();
                    lines.Children.Remove(rows[oldest].Panel);
                    rows.Remove(oldest);
                }
                var row = new Row(line);
                rows.Add(line.Id, row);
                order.Enqueue(line.Id);
                lines.Children.Insert(lines.Children.Count - 1, row.Panel);
                Update(row);
            }
        }
        RefreshSpeakerLabels();
        if (!canCopy && rows.Count != 0) actions.SetCopyText(CopyTranslations);
    }

    private string CopyTranslations() => string.Join(Environment.NewLine, order.Select(id => rows[id].Line.Translation));

    private void UpdatePreview()
    {
        preview.FontSize = Math.Max(12, settings.SubtitleFontSize - 5);
        preview.IsVisible = settings.BilingualSubtitles && !string.IsNullOrWhiteSpace(preview.Text);
        waiting.IsVisible = rows.Count == 0 && !preview.IsVisible;
    }

    private void Update(Row row)
    {
        SubtitleLine line = row.Line;
        row.Translation.Text = line.Translation;
        row.Translation.FontSize = settings.SubtitleFontSize;
        row.Translation.Opacity = line.IsRevised ? 1 : 0.86;
        row.Source.Text = line.Speech.Text;
        row.Source.FontSize = Math.Max(12, settings.SubtitleFontSize - 5);
        row.Source.IsVisible = settings.BilingualSubtitles;
    }

    private void RefreshSpeakerLabels()
    {
        int? precedingSpeaker = null;
        foreach (long id in order)
        {
            Row row = rows[id];
            if (settings.SpeakerLabels && speakers?.GetAttribution(row.Line.Speech) is { } latest)
                row.Attribution = latest;
            SpeakerAttribution? label = row.Attribution;
            int? currentSpeaker = label?.SpeakerId;
            row.Speaker.Text = label?.IsOverlapping == true ? "多人重叠"
                : currentSpeaker is { } known ? $"说话者 {known}" : "说话者未确定";
            row.Speaker.IsVisible = settings.SpeakerLabels && label is not null
                && (currentSpeaker is null || currentSpeaker != precedingSpeaker);
            precedingSpeaker = currentSpeaker;
        }
    }

    private sealed class Row
    {
        public Row(SubtitleLine line)
        {
            Line = line;
            Panel.Children.Add(Speaker);
            Panel.Children.Add(Translation);
            Panel.Children.Add(Source);
        }

        public SubtitleLine Line { get; set; }
        public SpeakerAttribution? Attribution { get; set; }
        public StackPanel Panel { get; } = new() { Spacing = 3 };
        public TextBlock Speaker { get; } = new() { Foreground = Secondary, FontSize = 12, IsVisible = false };
        public TextBlock Translation { get; } = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        public TextBlock Source { get; } = new() { Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
    }
}
