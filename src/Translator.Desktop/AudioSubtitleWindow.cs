using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Translator.Core;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal sealed class AudioSubtitleWindow : Window
{
    private readonly int maxLines = RuntimePaths.Configuration.Options.Audio.VisibleLines;
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#111C1D"));
    private static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#B2C4C2"));
    private readonly StackPanel lines = new() { Spacing = 12, Margin = new Thickness(18, 14) };
    private readonly ScrollViewer scroll;
    private readonly TextBlock waiting = new() { Text = "等待语音…", Foreground = Secondary };
    private readonly Dictionary<long, Row> rows = [];
    private readonly Queue<long> order = new();
    private AppSettings settings;
    private OverlayPosition? userPosition;
    private PixelPoint automaticPosition;
    private bool trackPosition;

    public AudioSubtitleWindow(string source, DesktopDisplay display, AppSettings settings,
        OverlayPosition? savedPosition, Action<OverlayPosition> moved)
    {
        this.settings = settings;
        userPosition = savedPosition;
        if (savedPosition is { } preferred) display = WindowsScreenCapture.DisplayAt(preferred.X, preferred.Y);
        Title = $"{source} · 翻译字幕";
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Surface;
        MinWidth = 320;
        MinHeight = 130;
        double scale = OverlayGeometry.ScaleAt(this, display);
        Width = Math.Min(720, display.Width / scale - 28);
        Height = Math.Min(270, display.Height / scale / 2);
        var target = savedPosition is { } restored ? new PixelPoint(restored.X, restored.Y)
            : new PixelPoint(display.X + (int)((display.Width - Width * scale) / 2),
                display.Y + Math.Max(0, display.Height - (int)((Height + settings.SubtitleBottomOffset + 36) * scale)));
        Position = OverlayGeometry.KeepVisible(this, display, target, (int)Math.Ceiling(Width * scale),
            (int)Math.Ceiling(Height * scale));
        lines.Children.Add(waiting);
        scroll = new ScrollViewer
        {
            Content = lines, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Content = scroll;
        Opened += (_, _) =>
        {
            WindowsScreenCapture.StyleOverlay(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: false);
            automaticPosition = Position;
            trackPosition = true;
        };
        Closed += (_, _) => trackPosition = false;
        PositionChanged += (_, args) =>
        {
            if (!trackPosition || WindowState != WindowState.Normal || (userPosition is null && args.Point == automaticPosition)) return;
            var position = new OverlayPosition(args.Point.X, args.Point.Y);
            if (position == userPosition) return;
            userPosition = position;
            moved(position);
        };
    }

    public void ApplySettings(AppSettings changed)
    {
        if (userPosition is null && settings.SubtitleBottomOffset != changed.SubtitleBottomOffset)
        {
            DesktopDisplay display = WindowsScreenCapture.DisplayAt(Position.X, Position.Y);
            double scale = OverlayGeometry.ScaleAt(this, display);
            int move = (int)Math.Round((settings.SubtitleBottomOffset - changed.SubtitleBottomOffset) * scale);
            int maximum = display.Y + Math.Max(0, display.Height - (int)(Bounds.Height * scale) - 36);
            automaticPosition = new PixelPoint(Position.X, Math.Clamp(Position.Y + move, display.Y, maximum));
            Position = automaticPosition;
        }
        settings = changed;
        foreach (Row row in rows.Values) Update(row);
    }

    public void Apply(IReadOnlyList<SubtitleLine> update)
    {
        bool follow = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 24;
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
                waiting.IsVisible = false;
                var row = new Row(line);
                rows.Add(line.Id, row);
                order.Enqueue(line.Id);
                lines.Children.Add(row.Panel);
                Update(row);
            }
        }
        if (follow) Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
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

    private sealed class Row
    {
        public Row(SubtitleLine line)
        {
            Line = line;
            Panel.Children.Add(Translation);
            Panel.Children.Add(Source);
        }

        public SubtitleLine Line { get; set; }
        public StackPanel Panel { get; } = new() { Spacing = 3 };
        public TextBlock Translation { get; } = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        public TextBlock Source { get; } = new() { Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
    }
}
