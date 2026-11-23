using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Translator.Core;

namespace Translator.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPdfSelection : IDisposable
{
    private const ushort LeftButton = 1, RightButton = 2, MiddleButton = 4;
    private const ushort Shift = 0x10, Control = 0x11, Alt = 0x12, LeftWin = 0x5B, RightWin = 0x5C;
    private const uint LeftButtonDown = 0x201, LeftButtonUp = 0x202;
    private const uint Quit = 0x12;
    private readonly int dragWidth = GetSystemMetrics(68); // SM_CXDRAG
    private readonly int dragHeight = GetSystemMetrics(69); // SM_CYDRAG
    private readonly int doubleClickWidth = GetSystemMetrics(36); // SM_CXDOUBLECLK
    private readonly int doubleClickHeight = GetSystemMetrics(37); // SM_CYDOUBLECLK
    private readonly TaskCompletionSource<uint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private FinishedSelection? pending;
    private int pressVersion;
    private IntPtr dragWindow;
    private Point dragStart;
    private IntPtr lastClickWindow;
    private Point lastClick;
    private long lastClickTime;

    public WindowsPdfSelection()
    {
        thread = new Thread(Listen) { IsBackground = true, Name = "Translator mouse selection" };
        thread.Start();
    }

    public int PressVersion => Volatile.Read(ref pressVersion);

    public static bool IsReaderWindow(IntPtr window) =>
        GetWindowThreadProcessId(window, out uint processId) != 0 && IsReader(processId);

    public bool TakeFinishedSelection(IntPtr foreground)
    {
        FinishedSelection? selection = Volatile.Read(ref pending);
        if (selection is null || Environment.TickCount64 < selection.ReadAfter) return false;
        return ReferenceEquals(Interlocked.CompareExchange(ref pending, null, selection), selection)
            && RootWindow(foreground) == selection.Window;
    }

    private void Listen()
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        MouseProcedure procedure = OnMouseEvent;
        IntPtr hook = SetWindowsHookEx(14, procedure, GetModuleHandle(null), 0); // WH_MOUSE_LL
        ready.SetResult(GetCurrentThreadId());
        if (hook == IntPtr.Zero) return;
        try { while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } }
        finally
        {
            UnhookWindowsHookEx(hook);
            GC.KeepAlive(procedure);
        }
    }

    private IntPtr OnMouseEvent(int code, IntPtr message, IntPtr data)
    {
        if (code == 0 && message.ToInt64() is LeftButtonDown or LeftButtonUp)
        {
            Point point = Marshal.PtrToStructure<Point>(data);
            if (message.ToInt64() == LeftButtonDown)
            {
                if (dragWindow != IntPtr.Zero) return CallNextHookEx(IntPtr.Zero, code, message, data);
                Interlocked.Increment(ref pressVersion);
                Volatile.Write(ref pending, null);
                dragStart = point;
                dragWindow = RootWindow(GetForegroundWindow());
            }
            else if (dragWindow != IntPtr.Zero) CompleteGesture(point);
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void CompleteGesture(Point point)
    {
        // The reader can keep mouse capture when the pointer leaves its client area.
        bool dragged = Math.Abs(point.X - dragStart.X) >= dragWidth || Math.Abs(point.Y - dragStart.Y) >= dragHeight;
        long now = Environment.TickCount64;
        bool doubleClick = !dragged && lastClickWindow == dragWindow && now - lastClickTime <= GetDoubleClickTime()
            && Math.Abs(point.X - lastClick.X) <= doubleClickWidth / 2
            && Math.Abs(point.Y - lastClick.Y) <= doubleClickHeight / 2;
        if (dragged || doubleClick)
            Volatile.Write(ref pending, new FinishedSelection(dragWindow, now + 90));

        lastClickWindow = dragged ? IntPtr.Zero : dragWindow;
        lastClick = point;
        lastClickTime = now;
        dragWindow = IntPtr.Zero;
    }

    public static SelectedText? TryRead(IntPtr window, int maxSelectionLength, CancellationToken token)
    {
        if (GetForegroundWindow() != window || !GetPhysicalCursorPos(out Point point)
            || GetWindowThreadProcessId(window, out uint processId) == 0 || !IsReader(processId))
            return null;

        if (!CanCopy(window)) return null;

        string? text = WindowsClipboard.CopySelection(processId, maxSelectionLength,
            () => CanCopy(window) && SendCopyShortcut(), () => GetForegroundWindow() == window, token);
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxSelectionLength || GetForegroundWindow() != window) return null;

        DesktopDisplay display = WindowsScreenCapture.DisplayAt(point.X, point.Y);
        return new SelectedText(text, new ScreenRegion(display.Id, point.X - display.X, point.Y - display.Y, 1, 1));
    }

    private static bool CanCopy(IntPtr window) => GetForegroundWindow() == window
        && !IsDown(LeftButton) && !IsDown(RightButton) && !IsDown(MiddleButton) && !ModifierDown();

    private static bool SendCopyShortcut()
    {
        const uint keyUp = 2;
        Input[] keys = [Key(Control), Key('C'), Key('C', keyUp), Key(Control, keyUp)];
        int size = Marshal.SizeOf<Input>();
        uint sent = SendInput((uint)keys.Length, keys, size);
        if (sent != 0 && sent != keys.Length)
            SendInput(2, [Key('C', keyUp), Key(Control, keyUp)], size);
        return sent == keys.Length;
    }

    private static bool ModifierDown() =>
        IsDown(Control) || IsDown(Shift) || IsDown(Alt) || IsDown(LeftWin) || IsDown(RightWin);

    private static bool IsDown(ushort key) => GetAsyncKeyState(key) < 0;

    private static bool IsReader(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string name = process.ProcessName;
            return name.Equals("zotero", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SumatraPDF", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static IntPtr RootWindow(IntPtr window) => GetAncestor(window, 3); // GA_ROOTOWNER
    private static Input Key(ushort key, uint flags = 0) => new(1, new InputData { Keyboard = new(key, 0, flags, 0, UIntPtr.Zero) });

    public void Dispose()
    {
        if (ready.Task.Wait(TimeSpan.FromSeconds(1)))
            PostThreadMessage(ready.Task.Result, Quit, UIntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(1));
    }

    private sealed record FinishedSelection(IntPtr Window, long ReadAfter);
    private delegate IntPtr MouseProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Cursor;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Input(uint Type, InputData Data);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct KeyboardInput(ushort Key, ushort Scan, uint Flags, uint Time, UIntPtr Extra);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MouseInput(int X, int Y, uint Data, uint Flags, uint Time, UIntPtr Extra);
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(ushort key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(int hook, MouseProcedure procedure, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
}
