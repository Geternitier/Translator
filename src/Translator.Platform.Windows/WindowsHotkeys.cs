using System.Runtime.InteropServices;

namespace Translator.Platform.Windows;

public readonly record struct Hotkey(uint Modifiers, uint Key)
{
    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("请输入快捷键，例如 Ctrl + Alt + O。");
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        foreach (string part in parts.SkipLast(1))
        {
            uint modifier = part.ToUpperInvariant() switch
            {
                "ALT" => 1, "CTRL" => 2, "SHIFT" => 4, "WIN" => 8,
                _ => throw new FormatException($"不支持的修饰键：{part}")
            };
            if ((modifiers & modifier) != 0) throw new FormatException($"重复的修饰键：{part}");
            modifiers |= modifier;
        }
        if (modifiers == 0) throw new FormatException("快捷键需要 Ctrl、Alt、Shift 或 Win 修饰键。");

        string key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return new Hotkey(modifiers, key[0]);
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int function) && function is >= 1 and <= 12)
            return new Hotkey(modifiers, (uint)(0x6F + function));
        throw new FormatException($"不支持的主键：{key}");
    }
}

public sealed class WindowsHotkeys : IDisposable
{
    private const uint Quit = 0x12;
    private const uint HotkeyMessage = 0x0312;
    private const int AccessDenied = 5;
    private const int HotkeyAlreadyRegistered = 1409;
    private readonly Thread thread;
    private readonly TaskCompletionSource<uint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WindowsHotkeys(IReadOnlyList<Hotkey> hotkeys, int overrideConflictAt, Action<int> trigger,
        Action<int, int> registrationFailed, Action<int> conflictOverridden)
    {
        thread = new Thread(() => Listen(hotkeys, overrideConflictAt, trigger, registrationFailed, conflictOverridden))
            { IsBackground = true, Name = "Translator hotkeys" };
        thread.Start();
    }

    private void Listen(IReadOnlyList<Hotkey> hotkeys, int overrideConflictAt, Action<int> trigger,
        Action<int, int> registrationFailed, Action<int> conflictOverridden)
    {
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        ready.SetResult(GetCurrentThreadId());
        var registered = new List<int>();
        IntPtr keyboardHook = IntPtr.Zero;
        KeyboardProc? hookCallback = null;
        try
        {
            for (int i = 0; i < hotkeys.Count; i++)
            {
                Hotkey key = hotkeys[i];
                if (RegisterHotKey(IntPtr.Zero, i, key.Modifiers | 0x4000, key.Key))
                {
                    registered.Add(i);
                    continue;
                }

                int error = Marshal.GetLastPInvokeError();
                if (i == overrideConflictAt && error is HotkeyAlreadyRegistered or AccessDenied)
                {
                    int identifier = i;
                    hookCallback = MakeConflictHook(key, () => trigger(identifier));
                    keyboardHook = SetWindowsHookEx(13, hookCallback, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
                    if (keyboardHook != IntPtr.Zero)
                    {
                        conflictOverridden(i);
                        continue;
                    }
                }
                registrationFailed(i, error);
            }

            while (GetMessage(out Message message, IntPtr.Zero, 0, 0) > 0)
                if (message.Id == HotkeyMessage) trigger((int)message.WParam);
        }
        finally
        {
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            GC.KeepAlive(hookCallback);
            foreach (int id in registered) UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    private static KeyboardProc MakeConflictHook(Hotkey shortcut, Action trigger)
    {
        bool suppressUntilReleased = false;
        return (code, message, data) =>
        {
            if (code == 0 && (uint)Marshal.ReadInt32(data) == shortcut.Key)
            {
                // Capture only the requested chord and its repeat events while the key is held.
                long eventType = message.ToInt64();
                if (eventType is 0x101 or 0x105 && suppressUntilReleased)
                {
                    suppressUntilReleased = false;
                    return new IntPtr(1);
                }
                if (eventType is 0x100 or 0x104)
                {
                    if (suppressUntilReleased) return new IntPtr(1);
                    if (ReadModifiers() == shortcut.Modifiers)
                    {
                        suppressUntilReleased = true;
                        trigger();
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
    }

    private static uint ReadModifiers() => ((GetAsyncKeyState(0x12) < 0) ? 1u : 0u)
        | ((GetAsyncKeyState(0x11) < 0) ? 2u : 0u) | ((GetAsyncKeyState(0x10) < 0) ? 4u : 0u)
        | ((GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) ? 8u : 0u);

    public void Dispose()
    {
        if (ready.Task.Wait(TimeSpan.FromSeconds(1)))
            PostThreadMessage(ready.Task.Result, Quit, UIntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(1));
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out Message msg, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message msg, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int type, KeyboardProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
