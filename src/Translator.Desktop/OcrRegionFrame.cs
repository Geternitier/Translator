using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Translator.Core;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal sealed class OcrRegionFrame : IDisposable
{
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor DownDiagonal = new(StandardCursorType.BottomRightCorner);
    private static readonly Cursor UpDiagonal = new(StandardCursorType.BottomLeftCorner);

    private readonly FloatingOverlay outline = new(clickThrough: false);
    private readonly FloatingOverlay toolbar = new(clickThrough: false);
    private readonly Border edge = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(4) };
    private readonly Border hitSurface = new() { Background = Brush.Parse("#25D6589E") };
    private readonly Border toolbarPanel;
    private readonly Button pause = SakuraVisuals.Tool(SakuraGlyph.Pause, "暂停识别");
    private readonly TextBlock state = new() { Foreground = SakuraVisuals.CaptionSecondary, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer dragTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Action beginAdjustment;
    private readonly Action<ScreenRegion> commitAdjustment;
    private ScreenRegion region;
    private DesktopDisplay? display;
    private HitRing shape;
    private HitRing? installedShape;
    private int cornerReach;
    private DragSession? drag;
    private bool visible;
    private bool adjusting;

    public OcrRegionFrame(Action beginAdjustment, Action<ScreenRegion> commitAdjustment, Action togglePause, Action close)
    {
        this.beginAdjustment = beginAdjustment;
        this.commitAdjustment = commitAdjustment;
        hitSurface.Child = edge;
        outline.Content = hitSurface;
        outline.Opened += (_, _) => InstallHitRing(force: true);

        AttachFrameEvents();
        Button closeButton = SakuraVisuals.Tool(SakuraGlyph.Close, "结束 OCR");
        Button keyboard = SakuraVisuals.Tool(SakuraGlyph.Keyboard, "启用方向键移动，Shift 加方向键缩放");
        pause.Click += (_, _) => togglePause();
        closeButton.Click += (_, _) => close();
        keyboard.Click += (_, _) => toolbar.FocusTools(keyboard);
        toolbar.KeyDown += AdjustWithKeyboard;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(SakuraVisuals.Icon(SakuraGlyph.Scan, SakuraVisuals.CaptionSecondary, 14));
        actions.Children.Add(state);
        actions.Children.Add(keyboard);
        actions.Children.Add(pause);
        actions.Children.Add(closeButton);
        toolbarPanel = SakuraVisuals.CaptionSurface(actions);
        toolbarPanel.Padding = new Thickness(6, 4);
        toolbar.Content = toolbarPanel;

        dragTimer.Tick += (_, _) => { if (drag is not null && TryGetCursor(out PixelPoint cursor)) MoveDrag(cursor); };
    }

    public void Show(ScreenRegion updated, bool paused)
    {
        if (drag is not null) return;
        display = WindowsScreenCapture.GetDisplay(updated.DisplayId);
        region = updated;
        ApplyPauseState(paused);
        visible = true;
        Arrange();
        if (!outline.IsVisible) outline.Show();
        SetToolsVisible(true);
    }

    public void SetPaused(bool paused)
    {
        ApplyPauseState(paused);
        if (visible) ArrangeToolbar();
    }

    private void ApplyPauseState(bool paused)
    {
        SakuraVisuals.SetTool(pause, paused ? SakuraGlyph.Play : SakuraGlyph.Pause, paused ? "继续识别" : "暂停识别");
        state.Text = paused ? "已暂停" : "监测区域";
        edge.BorderBrush = paused ? SakuraVisuals.Paused : SakuraVisuals.Frame;
    }

    private void AttachFrameEvents()
    {
        hitSurface.PointerPressed += (_, args) =>
        {
            if (!visible || display is not { } monitor || drag is not null
                || !args.GetCurrentPoint(hitSurface).Properties.IsLeftButtonPressed)
                return;
            if (!TryGetCursor(out PixelPoint point)) return;
            ResizeCorner corner = CornerAt(region, point.X - monitor.X, point.Y - monitor.Y, cornerReach);
            drag = new DragSession(region, point, args.Pointer, corner);
            args.Pointer.Capture(hitSurface);
            dragTimer.Start();
            args.Handled = true;
        };
        hitSurface.PointerMoved += (_, args) =>
        {
            if (!TryGetCursor(out PixelPoint point) || display is not { } monitor) return;
            if (drag?.Pointer == args.Pointer) MoveDrag(point);
            else if (drag is null)
            {
                ResizeCorner corner = CornerAt(region, point.X - monitor.X, point.Y - monitor.Y, cornerReach);
                hitSurface.Cursor = corner switch
                {
                    ResizeCorner.TopLeft or ResizeCorner.BottomRight => DownDiagonal,
                    ResizeCorner.TopRight or ResizeCorner.BottomLeft => UpDiagonal,
                    _ => MoveCursor
                };
            }
        };
        hitSurface.PointerReleased += (_, args) =>
        {
            if (drag?.Pointer != args.Pointer || args.InitialPressMouseButton != MouseButton.Left) return;
            if (TryGetCursor(out PixelPoint cursor)) MoveDrag(cursor);
            FinishDrag();
            args.Handled = true;
        };
        hitSurface.PointerCaptureLost += (_, args) =>
        {
            if (drag?.Pointer == args.Pointer) FinishDrag();
        };
    }

    private void MoveDrag(PixelPoint pointer)
    {
        if (drag is not { } session || display is not { } monitor) return;
        ScreenRegion updated = AdjustRegion(session.Original, monitor.Width, monitor.Height,
            pointer.X - session.Cursor.X, pointer.Y - session.Cursor.Y, session.Corner);
        if (updated == region) return;
        if (!adjusting)
        {
            adjusting = true;
            beginAdjustment();
            SetToolsVisible(false);
        }
        region = updated;
        Arrange();
    }

    internal static ResizeCorner CornerAt(ScreenRegion area, int x, int y, int reach)
    {
        int horizontal = Math.Min(reach, area.Width / 3);
        int vertical = Math.Min(reach, area.Height / 3);
        bool left = x <= area.X + horizontal, right = x >= area.X + area.Width - horizontal;
        bool top = y <= area.Y + vertical, bottom = y >= area.Y + area.Height - vertical;
        if (top && left) return ResizeCorner.TopLeft;
        if (top && right) return ResizeCorner.TopRight;
        if (bottom && left) return ResizeCorner.BottomLeft;
        if (bottom && right) return ResizeCorner.BottomRight;
        return ResizeCorner.None;
    }

    internal static ScreenRegion AdjustRegion(ScreenRegion original, int displayWidth, int displayHeight, int dx, int dy,
        ResizeCorner corner)
    {
        if (corner == ResizeCorner.None) return original with
        {
            X = Math.Clamp(original.X + dx, 0, displayWidth - original.Width),
            Y = Math.Clamp(original.Y + dy, 0, displayHeight - original.Height)
        };

        int left = original.X, top = original.Y;
        int right = left + original.Width, bottom = top + original.Height;
        int minWidth = Math.Min(original.Width, 32), minHeight = Math.Min(original.Height, 32);
        if (corner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft)
            left = Math.Clamp(left + dx, 0, right - minWidth);
        else
            right = Math.Clamp(right + dx, left + minWidth, displayWidth);
        if (corner is ResizeCorner.TopLeft or ResizeCorner.TopRight)
            top = Math.Clamp(top + dy, 0, bottom - minHeight);
        else
            bottom = Math.Clamp(bottom + dy, top + minHeight, displayHeight);

        return new ScreenRegion(original.DisplayId, left, top, right - left, bottom - top);
    }

    private void FinishDrag()
    {
        DragSession? previous = drag;
        drag = null;
        dragTimer.Stop();
        if (previous is null) return;
        previous.Pointer.Capture(null);
        if (!adjusting) return;
        adjusting = false;
        commitAdjustment(region);
    }

    private void AdjustWithKeyboard(object? sender, KeyEventArgs args)
    {
        if (!visible || drag is not null || display is not { } monitor) return;
        int step = (int)Math.Ceiling(8 * OverlayGeometry.ScaleAt(outline, monitor));
        (int dx, int dy) = args.Key switch
        {
            Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0)
        };
        if (dx == 0 && dy == 0) return;
        args.Handled = true;
        ScreenRegion updated = AdjustRegion(region, monitor.Width, monitor.Height, dx, dy,
            args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? ResizeCorner.BottomRight : ResizeCorner.None);
        if (updated == region) return;
        beginAdjustment();
        region = updated;
        Arrange();
        commitAdjustment(region);
    }

    private void Arrange()
    {
        if (display is not { } monitor) return;
        double scale = OverlayGeometry.ScaleAt(outline, monitor);
        int x = monitor.X + region.X, y = monitor.Y + region.Y;
        int padding = (int)Math.Ceiling(5 * scale);
        cornerReach = (int)Math.Ceiling(15 * scale);
        shape = new HitRing(region.Width + 2 * padding, region.Height + 2 * padding,
            padding + Math.Min(padding, region.Width / 3), padding + Math.Min(padding, region.Height / 3));
        outline.Position = new PixelPoint(x - padding, y - padding);
        outline.Width = shape.Width / scale;
        outline.Height = shape.Height / scale;
        edge.Margin = new Thickness((padding - 1) / scale);
        edge.BorderThickness = new Thickness(2 / scale);
        InstallHitRing();
        ArrangeToolbar();
    }

    private void ArrangeToolbar()
    {
        if (display is not { } monitor) return;
        double scale = OverlayGeometry.ScaleAt(outline, monitor);
        int x = monitor.X + region.X, y = monitor.Y + region.Y;
        int padding = (int)Math.Ceiling(5 * scale);
        toolbarPanel.Measure(Size.Infinity);
        int toolbarWidth = (int)Math.Ceiling(toolbarPanel.DesiredSize.Width * scale);
        int toolbarHeight = (int)Math.Ceiling(toolbarPanel.DesiredSize.Height * scale);
        int toolbarX = Math.Clamp(x + region.Width - toolbarWidth, monitor.X, Math.Max(monitor.X, monitor.X + monitor.Width - toolbarWidth));
        int above = y - padding - toolbarHeight - 2;
        int toolbarY = above >= monitor.Y ? above : Math.Min(y + padding + 5, monitor.Y + monitor.Height - toolbarHeight);
        toolbar.Position = new PixelPoint(toolbarX, toolbarY);
        toolbar.Width = toolbarWidth / scale;
        toolbar.Height = toolbarHeight / scale;
    }

    private void InstallHitRing(bool force = false)
    {
        if (!force && installedShape == shape) return;
        IntPtr handle = outline.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        bool installed = WindowsScreenCapture.SetOverlayHitRing(handle, shape.Width, shape.Height, shape.InsetX, shape.InsetY);
        WindowsScreenCapture.StyleOverlay(handle, clickThrough: !installed, noActivate: true);
        installedShape = installed ? shape : null;
    }

    private void SetToolsVisible(bool show)
    {
        if (show && !toolbar.IsVisible) toolbar.Show();
        if (!show && toolbar.IsVisible) toolbar.Hide();
    }

    private static bool TryGetCursor(out PixelPoint cursor)
    {
        try
        {
            (int x, int y) = WindowsScreenCapture.CursorPosition();
            cursor = new PixelPoint(x, y);
            return true;
        }
        catch (Win32Exception) { cursor = default; return false; }
    }

    public void Dispose()
    {
        visible = false;
        FinishDrag();
        toolbar.Close();
        outline.Close();
    }

    internal enum ResizeCorner { None, TopLeft, TopRight, BottomLeft, BottomRight }
    private readonly record struct HitRing(int Width, int Height, int InsetX, int InsetY);
    private sealed record DragSession(ScreenRegion Original, PixelPoint Cursor, IPointer Pointer, ResizeCorner Corner);
}
