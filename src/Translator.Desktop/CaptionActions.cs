using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Translator.Desktop;

internal sealed class CaptionActions : IDisposable
{
    private static readonly Geometry CopyGlyph = Geometry.Parse("M 5,1 L 12,1 12,3 10,3 10,2 5,2 Z M 11,4 L 14,4 14,14 5,14 5,11 2,11 2,3 11,3 Z M 3,4 L 3,10 5,10 5,5 10,5 10,4 Z M 6,6 L 6,13 13,13 13,6 Z");
    private static readonly Geometry CopiedGlyph = Geometry.Parse("M 2,7 L 3,6 6,9 12,3 13,4 6,11 Z");
    private static readonly Geometry StopGlyph = Geometry.Parse("M 3,3 H 11 V 11 H 3 Z");
    private static readonly Geometry CloseGlyph = Geometry.Parse("M 3,2 L 7,6 11,2 12,3 8,7 12,11 11,12 7,8 3,12 2,11 6,7 2,3 Z");

    private readonly FloatingOverlay owner;
    private readonly FloatingOverlay window = new(clickThrough: false);
    private readonly Border panel;
    private readonly Button copy = CreateButton(CopyGlyph, "复制译文");
    private readonly Button stop = CreateButton(StopGlyph, "停止翻译");
    private readonly Button close = CreateButton(CloseGlyph, "关闭译文");
    private readonly DispatcherTimer hideDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private Func<string>? copyText;
    private Action? stopAction;
    private Action? closeAction;
    private bool enabled;
    private bool captionHovered;
    private bool toolbarHovered;

    public CaptionActions(FloatingOverlay owner)
    {
        this.owner = owner;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        row.Children.Add(copy);
        row.Children.Add(stop);
        row.Children.Add(close);
        panel = new Border
        {
            Background = CaptionOverlay.Surface, CornerRadius = new CornerRadius(7), Padding = new Thickness(4), Child = row
        };
        window.Content = panel;
        copy.Click += async (_, _) => await CopyAsync();
        stop.Click += (_, _) => stopAction?.Invoke();
        close.Click += (_, _) => closeAction?.Invoke();
        owner.PointerEntered += (_, _) => { captionHovered = true; Refresh(); };
        owner.PointerExited += (_, _) => { captionHovered = false; Refresh(); };
        window.PointerEntered += (_, _) => { toolbarHovered = true; Refresh(); };
        window.PointerExited += (_, _) => { toolbarHovered = false; Refresh(); };
        owner.PositionChanged += (_, _) => { if (window.IsVisible) Position(); };
        owner.SizeChanged += (_, _) => { if (window.IsVisible) Position(); };
        hideDelay.Tick += (_, _) =>
        {
            hideDelay.Stop();
            if (!captionHovered && !toolbarHovered && window.IsVisible) window.Hide();
        };
    }

    public bool IsEngaged => enabled && (captionHovered || toolbarHovered || window.IsVisible);

    public void SetActions(bool enabled, Func<string>? copyText, Action? stop, Action? close)
    {
        this.enabled = enabled;
        SetCopyText(copyText);
        stopAction = stop;
        closeAction = close;
        this.stop.IsEnabled = stop is not null;
        this.close.IsEnabled = close is not null;
        Refresh();
    }

    public void SetCopyText(Func<string>? value)
    {
        copyText = value;
        copy.IsEnabled = value is not null;
    }

    public void MarkStopped()
    {
        stopAction = null;
        stop.IsEnabled = false;
    }

    public void Hide()
    {
        captionHovered = toolbarHovered = false;
        hideDelay.Stop();
        if (window.IsVisible) window.Hide();
    }

    private void Refresh()
    {
        if (!enabled || !owner.IsVisible)
        {
            Hide();
            return;
        }
        if (!captionHovered && !toolbarHovered)
        {
            if (window.IsVisible && !hideDelay.IsEnabled) hideDelay.Start();
            return;
        }
        hideDelay.Stop();
        if (window.IsVisible) return;
        ResetCopyIcon();
        Position();
        window.Show();
    }

    private void Position()
    {
        double scale = owner.RenderScaling;
        panel.Measure(Size.Infinity);
        int width = (int)Math.Ceiling(panel.DesiredSize.Width * scale);
        int height = (int)Math.Ceiling(panel.DesiredSize.Height * scale);
        PixelPoint origin = owner.Position;
        int right = origin.X + (int)Math.Round(owner.ClientSize.Width * scale);
        int x = right - width;
        int y = origin.Y - height - 2;
        if (owner.Screens?.ScreenFromWindow(owner)?.WorkingArea is { } area)
        {
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));
            if (y < area.Y) y = Math.Clamp(origin.Y + 2, area.Y, Math.Max(area.Y, area.Bottom - height));
        }
        window.Width = width / scale;
        window.Height = height / scale;
        window.Position = new PixelPoint(x, y);
    }

    private async Task CopyAsync()
    {
        if (owner.Clipboard is not { } clipboard || copyText?.Invoke() is not { Length: > 0 } text) return;
        try
        {
            await clipboard.SetTextAsync(text);
            ((PathIcon)copy.Content!).Data = CopiedGlyph;
            ToolTip.SetTip(copy, "已复制");
        }
        catch (Exception)
        {
            ToolTip.SetTip(copy, "复制失败，请重试");
        }
    }

    private void ResetCopyIcon()
    {
        ((PathIcon)copy.Content!).Data = CopyGlyph;
        ToolTip.SetTip(copy, "复制译文");
    }

    private static Button CreateButton(Geometry glyph, string label)
    {
        var button = new Button
        {
            Content = new PathIcon { Data = glyph, Width = 16, Height = 16, Foreground = Brushes.White },
            Padding = new Thickness(5), Focusable = false, Cursor = new Cursor(StandardCursorType.Hand)
        };
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        return button;
    }

    public void Dispose()
    {
        Hide();
        window.Close();
    }
}
