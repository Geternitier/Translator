using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SharpGen.Runtime;
using Translator.Core;

namespace Translator.Platform.Windows;

public sealed record DesktopDisplay(string Id, int X, int Y, int Width, int Height)
{
    public ScreenRegion FullRegion => new(Id, 0, 0, Width, Height);
}

public sealed class WindowsScreenCapture(OcrTuning tuning) : IScreenCapture
{
    public static DesktopDisplay CursorDisplay()
    {
        if (!GetCursorPos(out NativePoint point)) throw new Win32Exception();
        return DisplayAt(point.X, point.Y);
    }

    public static (int X, int Y) CursorPosition()
    {
        if (!GetCursorPos(out NativePoint point)) throw new Win32Exception();
        return (point.X, point.Y);
    }

    public static DesktopDisplay DisplayAt(int x, int y) => ReadDisplay(MonitorFromPoint(new NativePoint(x, y), 2));

    public static DesktopDisplay GetDisplay(string id) => ReadDisplay(new IntPtr(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)));

    private static DesktopDisplay ReadDisplay(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("选区所在显示器已不可用，请重新框选。");
        return new DesktopDisplay(monitor.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
    }

    public ValueTask<ScreenFrame> CaptureAsync(ScreenRegion region, CancellationToken cancellationToken) =>
        new(Task.Run(() => Capture(region), cancellationToken));

    public async IAsyncEnumerable<ScreenFrame> WatchAsync(ScreenRegion region, TimeSpan samplingInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var latest = Channel.CreateBounded<ScreenFrame>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        Task<bool> capture = Task.Factory.StartNew(WatchDxgi, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool captured;
        try
        {
            await foreach (ScreenFrame frame in latest.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return frame;
        }
        finally
        {
            await captureStop.CancelAsync().ConfigureAwait(false);
            captured = await capture.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (captured) yield break;

        using var timer = new PeriodicTimer(samplingInterval);
        do { yield return await CaptureAsync(region, cancellationToken).ConfigureAwait(false); }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));

        bool WatchDxgi()
        {
            try
            {
                DxgiRegionCapture.Watch(region, latest.Writer, tuning, captureStop.Token);
                return true;
            }
            catch (Exception error) when (error is SharpGenException or NotSupportedException
                or DllNotFoundException or EntryPointNotFoundException)
            {
                Trace.WriteLine($"Desktop duplication unavailable. {error.Message}");
                return false;
            }
            finally { latest.Writer.TryComplete(); }
        }
    }

    private static ScreenFrame Capture(ScreenRegion region)
    {
        DesktopDisplay display = GetDisplay(region.DisplayId);
        if (region.Width <= 0 || region.Height <= 0 || region.X < 0 || region.Y < 0
            || region.Width > display.Width - region.X || region.Height > display.Height - region.Y)
            throw new InvalidOperationException("屏幕尺寸已变化，请重新框选。");

        var bitmapInfo = new BitmapInfo
        {
            Size = 40, Width = region.Width, Height = -region.Height, Planes = 1, BitCount = 32
        };
        IntPtr desktop = GetDC(IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        IntPtr target = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            target = CreateCompatibleDC(desktop);
            if (target == IntPtr.Zero) throw new Win32Exception();
            bitmap = CreateDIBSection(target, ref bitmapInfo, 0, out IntPtr pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new Win32Exception();
            oldBitmap = SelectObject(target, bitmap);
            if (oldBitmap == IntPtr.Zero || oldBitmap == new IntPtr(-1)) throw new Win32Exception();
            if (!BitBlt(target, 0, 0, region.Width, region.Height, desktop,
                display.X + region.X, display.Y + region.Y, 0x00CC0020)) throw new Win32Exception();

            int stride = checked(region.Width * 4);
            byte[] frame = new byte[checked(stride * region.Height)];
            Marshal.Copy(pixels, frame, 0, frame.Length);
            return new ScreenFrame(region, stride, frame);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && oldBitmap != new IntPtr(-1)) SelectObject(target, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (target != IntPtr.Zero) DeleteDC(target);
            ReleaseDC(IntPtr.Zero, desktop);
        }
    }

    public static void StyleOverlay(IntPtr handle, bool clickThrough, bool noActivate = false)
    {
        if (handle == IntPtr.Zero) return;
        const int extendedStyle = -20;
        const long transparent = 0x20L, noActivation = 0x08000000L;
        long style = GetWindowLongPtr(handle, extendedStyle).ToInt64();
        long updated = (style & ~(transparent | noActivation))
            | (clickThrough ? transparent : 0L) | (noActivate || clickThrough ? noActivation : 0L);
        if (updated != style) SetWindowLongPtr(handle, extendedStyle, new IntPtr(updated));
        SetWindowDisplayAffinity(handle, 0x11);
    }

    public static bool SetOverlayHitRing(IntPtr handle, int width, int height, int insetX, int insetY)
    {
        const int subtract = 4; // RGN_DIFF
        IntPtr ring = CreateRectRgn(0, 0, width, height);
        IntPtr center = CreateRectRgn(insetX, insetY, width - insetX, height - insetY);
        try
        {
            if (ring == IntPtr.Zero || center == IntPtr.Zero || CombineRgn(ring, ring, center, subtract) == 0
                || SetWindowRgn(handle, ring, true) == 0) return false;
            ring = IntPtr.Zero; // Windows owns the installed region.
            return true;
        }
        finally
        {
            if (center != IntPtr.Zero) DeleteObject(center);
            if (ring != IntPtr.Zero) DeleteObject(ring);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int srcX, int srcY, uint op);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
