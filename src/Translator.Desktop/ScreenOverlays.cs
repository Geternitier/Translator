using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Translator.Core;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal sealed class ScreenPicker : Window
{
    private readonly TaskCompletionSource<ScreenRegion?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Canvas canvas = new() { Background = Brushes.Transparent };
    private readonly Border outline = new()
    {
        BorderBrush = SakuraVisuals.Frame, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4),
        Background = Brush.Parse("#28E765A5"), IsHitTestVisible = false, IsVisible = false
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
        grid.Children.Add(new Border { Background = Brush.Parse("#6033243E"), IsHitTestVisible = false });
        grid.Children.Add(canvas);
        canvas.Cursor = new Cursor(StandardCursorType.Cross);
        canvas.Children.Add(outline);
        var instructions = SakuraVisuals.CaptionSurface(
            SakuraVisuals.Label(SakuraGlyph.Scan, "拖动框选屏幕 · Esc 取消", SakuraVisuals.CaptionSecondary));
        instructions.IsHitTestVisible = false;
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
        return KeepVisible(area, point, width, height);
    }

    public static PixelPoint KeepVisible(PixelRect area, PixelPoint point, int width, int height)
    {
        const int margin = 6;
        int left = area.X + margin, top = area.Y + margin;
        return new PixelPoint(
            Math.Clamp(point.X, left, Math.Max(left, area.Right - width - margin)),
            Math.Clamp(point.Y, top, Math.Max(top, area.Bottom - height - margin)));
    }
}

internal class FloatingOverlay : Window
{
    private bool clickThrough;
    private bool keyboardInput;

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
        Deactivated += (_, _) =>
        {
            if (!keyboardInput) return;
            keyboardInput = false;
            ApplyInputStyle();
        };
    }

    public void SetClickThrough(bool value)
    {
        if (clickThrough == value) return;
        clickThrough = value;
        ApplyInputStyle();
    }

    public void FocusTools(Control target)
    {
        if (clickThrough) return;
        keyboardInput = true;
        ApplyInputStyle();
        Activate();
        target.Focus();
    }

    public void EnableKeyboardMovement(Control header, Action<PixelPoint>? moved = null, Func<bool>? canMove = null)
    {
        header.KeyDown += (_, args) =>
        {
            if (!keyboardInput || args.KeyModifiers != KeyModifiers.None || canMove?.Invoke() == false) return;
            int step = (int)Math.Ceiling(8 * RenderScaling);
            (int dx, int dy) = args.Key switch
            {
                Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0)
            };
            if (dx == 0 && dy == 0) return;
            var target = new PixelPoint(Position.X + dx, Position.Y + dy);
            if (Screens.ScreenFromWindow(this)?.WorkingArea is { } area)
                target = OverlayGeometry.KeepVisible(area, target, (int)Math.Ceiling(ClientSize.Width * RenderScaling),
                    (int)Math.Ceiling(ClientSize.Height * RenderScaling));
            Position = target;
            moved?.Invoke(target);
            args.Handled = true;
        };
    }

    private void ApplyInputStyle()
    {
        if (OperatingSystem.IsWindows())
            WindowsScreenCapture.StyleOverlay(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough, noActivate: !keyboardInput);
    }
}

internal sealed class CaptionOverlay(SakuraGlyph glyph, string title, Action<OverlayPosition>? moved = null) : IDisposable
{
    private FloatingOverlay? window;
    private TextBlock? label;
    private TextBlock? statusLabel;
    private Border? panel;
    private CaptionActions? actions;
    private (DesktopDisplay Display, ScreenRegion Anchor, OverlayPosition? SavedPosition) layout;
    private int fontSize = AppSettings.DefaultSubtitleFontSize;
    private bool draggable;
    private DispatcherTimer? timer;
    private OverlayPosition? draggedPosition;
    private IPointer? dragPointer;
    private PixelPoint firstPointer;
    private PixelPoint firstPosition;
    private bool dragMoved;

    public void Show(string text, DesktopDisplay display, ScreenRegion anchor,
        TimeSpan? duration = null, bool copyable = false, Action? close = null,
        bool draggable = false, bool keepDraggedPosition = false, OverlayPosition? savedPosition = null,
        Action? stop = null, string status = "", SakuraGlyph primaryGlyph = SakuraGlyph.Stop,
        string primaryLabel = "停止生成", string closeLabel = "关闭译文")
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

        TextBlock caption = label!;
        Border surface = panel!;
        caption.Text = text;
        SetStatus(status);
        this.draggable = draggable;
        bool interactive = copyable || close is not null || stop is not null;
        actions!.SetActions(copyable ? GetCopyText : null, stop, close, primaryGlyph, primaryLabel, closeLabel);
        window.SetClickThrough(!interactive && !draggable);
        layout = (display, anchor, savedPosition);
        LayoutCaption(window, surface);
        if (!window.IsVisible) window.Show();

        if (duration is { } delay)
        {
            timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                if (dragPointer is null && !surface.IsPointerOver && !window.IsActive) Clear();
            };
            timer.Start();
        }
    }

    public void SetFontSize(int value)
    {
        if (fontSize == value) return;
        fontSize = value;
        if (label is null) return;
        label.FontSize = value;
        if (window?.IsVisible == true) LayoutCaption(window, panel!);
    }

    private void LayoutCaption(FloatingOverlay overlay, Border surface)
    {
        var (display, anchor, savedPosition) = layout;
        OverlayPosition? placement = draggable ? draggedPosition ?? savedPosition : null;
        if (placement is { } preferred) display = WindowsScreenCapture.DisplayAt(preferred.X, preferred.Y);
        double scale = OverlayGeometry.ScaleAt(overlay, display);
        double widthLimit = Math.Min(520, display.Width / scale - 24);
        surface.MaxWidth = widthLimit;
        surface.MaxHeight = display.Height / scale / 2;
        surface.Measure(new Size(widthLimit, surface.MaxHeight));
        if (dragPointer is not null) return;

        int width = (int)Math.Ceiling(surface.DesiredSize.Width * scale);
        int height = (int)Math.Ceiling(surface.DesiredSize.Height * scale);
        int y = anchor.Y + anchor.Height + 12;
        if (y + height > display.Height - 8) y = anchor.Y - height - 12;
        var target = placement is { } p ? new PixelPoint(p.X, p.Y) : new PixelPoint(display.X + anchor.X, display.Y + y);
        overlay.Position = OverlayGeometry.KeepVisible(overlay, display, target, width, height);
    }

    private string GetCopyText() => label?.Text ?? "";

    private FloatingOverlay CreateWindow()
    {
        var overlay = new FloatingOverlay { SizeToContent = SizeToContent.WidthAndHeight };
        label = new TextBlock { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = fontSize };
        statusLabel = new TextBlock
        {
            Foreground = SakuraVisuals.CaptionSecondary, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        heading.Children.Add(SakuraVisuals.Icon(SakuraGlyph.Grip, SakuraVisuals.CaptionSecondary, 14));
        heading.Children.Add(SakuraVisuals.Icon(glyph, SakuraVisuals.CaptionSecondary, 15));
        heading.Children.Add(statusLabel);
        var dragArea = new Border { Child = heading, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll) };
        dragArea.PointerPressed += BeginDrag;
        actions = new CaptionActions(overlay);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(actions.View, Dock.Right);
        header.Children.Add(actions.View);
        header.Children.Add(dragArea);
        overlay.EnableKeyboardMovement(header, position =>
        {
            draggedPosition = new OverlayPosition(position.X, position.Y);
            moved?.Invoke(draggedPosition);
        }, () => draggable);
        panel = SakuraVisuals.CaptionSurface(new StackPanel { Children = { header, label } });
        overlay.Content = panel;
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
        if (!draggable || window is null || dragPointer is not null
            || !args.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;

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

    public void Clear()
    {
        timer?.Stop();
        timer = null;
        actions?.ResetCopy();
        FinishDrag();
        draggedPosition = null;
        window?.Hide();
    }

    public void SetPrimary(Action action, SakuraGlyph glyph, string label, string status)
    {
        SetStatus(status);
        actions?.SetPrimary(action, glyph, label);
        if (window?.IsVisible == true) LayoutCaption(window, panel!);
    }

    public void Reanchor(ScreenRegion anchor)
    {
        if (window?.IsVisible != true) return;
        if (layout.Display.Id != anchor.DisplayId) layout.Display = WindowsScreenCapture.GetDisplay(anchor.DisplayId);
        layout.Anchor = anchor;
        LayoutCaption(window, panel!);
    }

    private void SetStatus(string value)
    {
        if (statusLabel is not null) statusLabel.Text = value.Length == 0 ? title : $"{title} · {value}";
    }

    public void Dispose()
    {
        Clear();
        actions?.Dispose();
        actions = null;
        window?.Close();
        window = null;
    }
}
