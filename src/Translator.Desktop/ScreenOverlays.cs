using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Translator.Core;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal sealed class ScreenPicker : Window
{
    private readonly TaskCompletionSource<ScreenRegion?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Canvas canvas = new() { Background = Brushes.Transparent };
    private readonly Border outline = new()
    {
        BorderBrush = new SolidColorBrush(Color.Parse("#83D5B8")), BorderThickness = new Thickness(2),
        Background = new SolidColorBrush(Color.FromArgb(24, 131, 213, 184)), IsHitTestVisible = false, IsVisible = false
    };
    private readonly ScreenRegion displayRegion;
    private readonly WriteableBitmap snapshot;
    private Point? origin;

    private ScreenPicker(DesktopDisplay display, ScreenFrame frame)
    {
        displayRegion = frame.Region;
        double scale = OverlayGeometry.ScaleAt(this, display);
        Position = new PixelPoint(display.X, display.Y);
        Width = display.Width / scale;
        Height = display.Height / scale;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        snapshot = MakeBitmap(frame);

        var grid = new Grid();
        grid.Children.Add(new Image { Source = snapshot, Stretch = Stretch.Fill, IsHitTestVisible = false });
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)), IsHitTestVisible = false });
        grid.Children.Add(canvas);
        canvas.Cursor = new Cursor(StandardCursorType.Cross);
        canvas.Children.Add(outline);
        var instructions = new Border
        {
            Padding = new Thickness(14, 8), CornerRadius = new CornerRadius(7), Background = Brushes.Black,
            IsHitTestVisible = false, Child = new TextBlock { Text = "拖动创建持续翻译框 · Esc 取消", Foreground = Brushes.White }
        };
        Canvas.SetLeft(instructions, 18);
        Canvas.SetTop(instructions, 18);
        canvas.Children.Add(instructions);
        Content = grid;

        canvas.PointerPressed += StartDrag;
        canvas.PointerMoved += MoveDrag;
        canvas.PointerReleased += EndDrag;
        KeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        Opened += (_, _) => WindowsScreenCapture.StyleOverlay(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: false);
        Closed += (_, _) => { result.TrySetResult(null); snapshot.Dispose(); };
    }

    public static async Task<ScreenRegion?> PickAsync(IScreenCapture capture, CancellationToken token)
    {
        DesktopDisplay display = WindowsScreenCapture.CursorDisplay();
        ScreenFrame frame = await capture.CaptureAsync(display.FullRegion, token);
        token.ThrowIfCancellationRequested();
        var picker = new ScreenPicker(display, frame);
        using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(() => picker.Close()));
        picker.Show();
        picker.Activate();
        return await picker.result.Task;
    }

    private void StartDrag(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        Point start = args.GetPosition(canvas);
        origin = start;
        SetOutline(new Rect(start, new Size(0, 0)));
        args.Pointer.Capture(canvas);
    }

    private void MoveDrag(object? sender, PointerEventArgs args)
    {
        if (origin is { } start) SetOutline(DragRect(start, args.GetPosition(canvas)));
    }

    private void EndDrag(object? sender, PointerReleasedEventArgs args)
    {
        if (origin is not { } start) return;
        origin = null;
        args.Pointer.Capture(null);
        Rect rect = DragRect(start, args.GetPosition(canvas));
        double scaleX = displayRegion.Width / canvas.Bounds.Width;
        double scaleY = displayRegion.Height / canvas.Bounds.Height;
        int left = (int)(rect.Left * scaleX), top = (int)(rect.Top * scaleY);
        int right = Math.Min(displayRegion.Width, (int)Math.Ceiling(rect.Right * scaleX));
        int bottom = Math.Min(displayRegion.Height, (int)Math.Ceiling(rect.Bottom * scaleY));
        if (right - left < 8 || bottom - top < 8) { outline.IsVisible = false; return; }

        result.TrySetResult(new ScreenRegion(displayRegion.DisplayId, left, top, right - left, bottom - top));
        Close();
    }

    private Rect DragRect(Point start, Point end)
    {
        var clamped = new Point(Math.Clamp(end.X, 0, canvas.Bounds.Width), Math.Clamp(end.Y, 0, canvas.Bounds.Height));
        return new Rect(new Point(Math.Min(start.X, clamped.X), Math.Min(start.Y, clamped.Y)),
            new Point(Math.Max(start.X, clamped.X), Math.Max(start.Y, clamped.Y)));
    }

    private void SetOutline(Rect rect)
    {
        Canvas.SetLeft(outline, rect.X);
        Canvas.SetTop(outline, rect.Y);
        outline.Width = rect.Width;
        outline.Height = rect.Height;
        outline.IsVisible = true;
    }

    private static WriteableBitmap MakeBitmap(ScreenFrame frame)
    {
        var image = new WriteableBitmap(new PixelSize(frame.Region.Width, frame.Region.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using ILockedFramebuffer locked = image.Lock();
        byte[] pixels = frame.Pixels.ToArray();
        for (int y = 0; y < frame.Region.Height; y++)
            Marshal.Copy(pixels, y * frame.Stride, locked.Address + y * locked.RowBytes, frame.Region.Width * 4);
        return image;
    }
}

internal static class OverlayGeometry
{
    public static double ScaleAt(TopLevel window, DesktopDisplay display)
    {
        var point = new PixelPoint(display.X + display.Width / 2, display.Y + display.Height / 2);
        return window.Screens?.ScreenFromPoint(point)?.Scaling ?? 1;
    }

    public static PixelPoint KeepVisible(TopLevel window, DesktopDisplay display, PixelPoint point, int width, int height)
    {
        var center = new PixelPoint(display.X + display.Width / 2, display.Y + display.Height / 2);
        PixelRect area = window.Screens?.ScreenFromPoint(center)?.WorkingArea
            ?? new PixelRect(display.X, display.Y, display.Width, display.Height);
        const int margin = 6;
        int left = area.X + margin, top = area.Y + margin;
        return new PixelPoint(
            Math.Clamp(point.X, left, Math.Max(left, area.Right - width - margin)),
            Math.Clamp(point.Y, top, Math.Max(top, area.Bottom - height - margin)));
    }
}

internal sealed class FloatingOverlay : Window
{
    private bool clickThrough;

    public FloatingOverlay(bool clickThrough = true)
    {
        this.clickThrough = clickThrough;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Opened += (_, _) => ApplyInputStyle();
    }

    public void SetClickThrough(bool value)
    {
        if (clickThrough == value) return;
        clickThrough = value;
        ApplyInputStyle();
    }

    private void ApplyInputStyle() =>
        WindowsScreenCapture.StyleOverlay(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough, noActivate: true);
}

internal sealed class CaptionOverlay(Action<OverlayPosition>? moved = null) : IDisposable
{
    private static readonly Geometry CopyGlyph = Geometry.Parse("M 5,1 L 12,1 12,3 10,3 10,2 5,2 Z M 11,4 L 14,4 14,14 5,14 5,11 2,11 2,3 11,3 Z M 3,4 L 3,10 5,10 5,5 10,5 10,4 Z M 6,6 L 6,13 13,13 13,6 Z");
    private static readonly Geometry CopiedGlyph = Geometry.Parse("M 2,7 L 3,6 6,9 12,3 13,4 6,11 Z");
    private static readonly Geometry CloseGlyph = Geometry.Parse("M 3,2 L 7,6 11,2 12,3 8,7 12,11 11,12 7,8 3,12 2,11 6,7 2,3 Z");
    private FloatingOverlay? window;
    private DispatcherTimer? timer;
    private OverlayPosition? draggedPosition;
    private IPointer? dragPointer;
    private PixelPoint firstPointer;
    private PixelPoint firstPosition;
    private bool dragMoved;

    public void Show(string text, DesktopDisplay display, ScreenRegion? anchor = null, int fontSize = 17,
        int bottomOffset = 64, TimeSpan? duration = null, bool copyable = false, Action? close = null,
        bool draggable = false, bool keepDraggedPosition = false, OverlayPosition? savedPosition = null)
    {
        if (string.IsNullOrWhiteSpace(text)) { Clear(); return; }

        timer?.Stop();
        timer = null;
        window ??= CreateWindow();
        if (!keepDraggedPosition)
        {
            FinishDrag();
            draggedPosition = null;
        }

        OverlayPosition? placement = draggable ? draggedPosition ?? savedPosition : null;
        if (placement is { } preferred) display = WindowsScreenCapture.DisplayAt(preferred.X, preferred.Y);
        double scale = OverlayGeometry.ScaleAt(window, display);
        double widthLimit = Math.Min(anchor is null ? 920 : 520, display.Width / scale - 24);
        var label = new TextBlock
        {
            Text = text, Foreground = Brushes.White, FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap, TextAlignment = anchor is null ? TextAlignment.Center : TextAlignment.Left
        };
        bool interactive = copyable || close is not null;
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(236, 17, 30, 30)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(15, 9), MaxWidth = widthLimit, MaxHeight = display.Height / scale / 2,
            ClipToBounds = true,
            Child = interactive ? WithActions(() => window?.Clipboard, label, text, copyable, close) : label
        };
        panel.Measure(new Size(widthLimit, panel.MaxHeight));
        int width = (int)Math.Ceiling(panel.DesiredSize.Width * scale);
        int height = (int)Math.Ceiling(panel.DesiredSize.Height * scale);
        if (draggable)
        {
            panel.Cursor = new Cursor(StandardCursorType.SizeAll);
            panel.PointerPressed += BeginDrag;
        }

        window.Content = panel;
        window.SetClickThrough(!interactive && !draggable);
        if (dragPointer is null)
        {
            int x = anchor is { } area ? area.X : (display.Width - width) / 2;
            int y = anchor is { } selection ? selection.Y + selection.Height + 12 : display.Height - height - (int)(bottomOffset * scale);
            if (anchor is { } region && y + height > display.Height - 8) y = region.Y - height - 12;
            var target = placement is { } p ? new PixelPoint(p.X, p.Y) : new PixelPoint(display.X + x, display.Y + y);
            window.Position = OverlayGeometry.KeepVisible(window, display, target, width, height);
        }
        if (!window.IsVisible) window.Show();

        if (duration is { } delay)
        {
            timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                if (dragPointer is null && (!(interactive || draggable) || !panel.IsPointerOver)) Clear();
            };
            timer.Start();
        }
    }

    private FloatingOverlay CreateWindow()
    {
        var overlay = new FloatingOverlay { SizeToContent = SizeToContent.WidthAndHeight };
        overlay.PointerMoved += MoveDrag;
        overlay.PointerReleased += (_, args) =>
        {
            if (args.Pointer == dragPointer && args.InitialPressMouseButton == MouseButton.Left)
            {
                FinishDrag();
                args.Handled = true;
            }
        };
        overlay.PointerCaptureLost += (_, args) =>
        {
            if (args.Pointer == dragPointer) FinishDrag();
        };
        return overlay;
    }

    private void BeginDrag(object? sender, PointerPressedEventArgs args)
    {
        if (window is null || dragPointer is not null || !args.GetCurrentPoint(window).Properties.IsLeftButtonPressed
            || (args.Source is Visual visual && visual.GetSelfAndVisualAncestors().Any(control => control is Button))) return;

        (int x, int y) = WindowsScreenCapture.CursorPosition();
        firstPointer = new PixelPoint(x, y);
        firstPosition = window.Position;
        dragMoved = false;
        dragPointer = args.Pointer;
        args.Pointer.Capture(window);
        args.Handled = true;
    }

    private void MoveDrag(object? sender, PointerEventArgs args)
    {
        if (window is null || args.Pointer != dragPointer) return;
        (int x, int y) = WindowsScreenCapture.CursorPosition();
        int deltaX = x - firstPointer.X, deltaY = y - firstPointer.Y;
        if (!dragMoved && Math.Abs(deltaX) + Math.Abs(deltaY) < 4) return;
        dragMoved = true;
        window.Position = new PixelPoint(firstPosition.X + deltaX, firstPosition.Y + deltaY);
        draggedPosition = new OverlayPosition(window.Position.X, window.Position.Y);
        args.Handled = true;
    }

    private void FinishDrag()
    {
        if (dragPointer is not { } pointer) return;
        dragPointer = null;
        if (pointer.Captured == window) pointer.Capture(null);
        if (dragMoved && draggedPosition is { } position) moved?.Invoke(position);
    }

    private static Grid WithActions(Func<IClipboard?> getClipboard, TextBlock label, string text, bool copyable, Action? close)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Top };
        if (copyable) actions.Children.Add(CreateCopyButton(getClipboard, text));
        if (close is not null)
        {
            Button closeButton = CreateActionButton(CloseGlyph, "关闭译文");
            closeButton.Click += (_, _) => close();
            actions.Children.Add(closeButton);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(actions, 1);
        grid.Children.Add(label);
        grid.Children.Add(actions);
        return grid;
    }

    private static Button CreateActionButton(Geometry glyph, string label)
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

    private static Button CreateCopyButton(Func<IClipboard?> getClipboard, string text)
    {
        Button button = CreateActionButton(CopyGlyph, "复制译文");
        button.Click += async (_, _) =>
        {
            if (getClipboard() is not { } clipboard) return;
            try
            {
                await clipboard.SetTextAsync(text);
                ((PathIcon)button.Content!).Data = CopiedGlyph;
                ToolTip.SetTip(button, "已复制");
            }
            catch (Exception)
            {
                ToolTip.SetTip(button, "复制失败，请重试");
            }
        };
        return button;
    }

    public void Clear()
    {
        timer?.Stop();
        timer = null;
        FinishDrag();
        draggedPosition = null;
        window?.Hide();
    }

    public void Dispose() { Clear(); window?.Close(); window = null; }
}
