using System.Runtime.InteropServices;

namespace Translator.Platform.Windows;

internal static class WindowsClipboard
{
    private const uint UnicodeText = 13;
    private static readonly object CopyGate = new();

    private static string? ReadUnicodeText(int maxSelectionLength)
    {
        IntPtr data = GetClipboardData(UnicodeText);
        if (data == IntPtr.Zero) return null;
        IntPtr pointer = GlobalLock(data);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            int count = (int)Math.Min(GlobalSize(data).ToUInt64() / sizeof(char), (ulong)maxSelectionLength + 1);
            string? text = Marshal.PtrToStringUni(pointer, count);
            int terminator = text?.IndexOf('\0') ?? -1;
            return (terminator >= 0 ? text![..terminator] : text)?.Trim();
        }
        finally { GlobalUnlock(data); }
    }

    internal static string? CopySelection(uint readerProcessId, int maxSelectionLength, Func<bool> copy, Func<bool> readerIsForeground,
        CancellationToken cancellationToken)
    {
        lock (CopyGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A window is required to restore data with EmptyClipboard and SetClipboardData.
            IntPtr owner = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, new IntPtr(-3),
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // HWND_MESSAGE
            if (owner == IntPtr.Zero) return null;
            var snapshot = new List<(uint Format, IntPtr Data)>();
            try
            {
                if (!OpenClipboard(owner)) return null;
                uint sequence;
                try
                {
                    if (!TrySaveClipboard(snapshot)) return null;
                    sequence = GetClipboardSequenceNumber();
                }
                finally { CloseClipboard(); }

                cancellationToken.ThrowIfCancellationRequested();
                if (GetClipboardSequenceNumber() != sequence || !copy()) return null;

                // Finish the restore even if automatic selection is switched off while the reader copies.
                for (int attempt = 0; attempt < 25; attempt++)
                {
                    Thread.Sleep(16);
                    if (GetClipboardSequenceNumber() == sequence || !OpenClipboard(owner)) continue;
                    try
                    {
                        GetWindowThreadProcessId(GetClipboardOwner(), out uint writer);
                        // SumatraPDF can publish text without an owner window. Only accept that
                        // immediately after requesting a copy from the still-focused reader.
                        if (writer != readerProcessId && (writer != 0 || !readerIsForeground())) return null;
                        try { return ReadUnicodeText(maxSelectionLength); }
                        finally { RestoreClipboard(snapshot); }
                    }
                    finally { CloseClipboard(); }
                }
                return null;
            }
            finally
            {
                foreach (var (_, data) in snapshot)
                    if (data != IntPtr.Zero) GlobalFree(data);
                DestroyWindow(owner);
            }
        }
    }

    private static bool TrySaveClipboard(List<(uint Format, IntPtr Data)> snapshot)
    {
        const ulong maxSnapshotBytes = 16 * 1024 * 1024;
        ulong totalBytes = 0;
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            if (!IsGlobalMemoryFormat(format)) return false;
            IntPtr data = GetClipboardData(format);
            if (data == IntPtr.Zero) return false;
            ulong size = GlobalSize(data).ToUInt64();
            if (size == 0 || size > maxSnapshotBytes - totalBytes) return false;
            IntPtr duplicate = OleDuplicateData(data, (ushort)format, 0);
            if (duplicate == IntPtr.Zero) return false;
            snapshot.Add((format, duplicate));
            totalBytes += size;
        }
        return Marshal.GetLastPInvokeError() == 0;
    }

    private static bool IsGlobalMemoryFormat(uint format)
    {
        // Standard clipboard formats backed by movable global memory.
        if (format is 1 or 4 or 7 or 8 or 11 or 12 or UnicodeText or 15 or 16 or 17 or 0x81) return true;
        if (format < 0xC000) return false;
        var name = new System.Text.StringBuilder(128);
        if (GetClipboardFormatName(format, name, name.Capacity) == 0) return false;
        return name.ToString() is "HTML Format" or "Rich Text Format" or "Rich Text Format Without Objects"
            or "UniformResourceLocator" or "UniformResourceLocatorW" or "text/html" or "text/_moz_htmlcontext"
            or "text/_moz_htmlinfo" or "text/x-moz-url" or "text/unicode" or "PNG"
            or "Chromium internal source RFH token" or "Chromium internal source URL";
    }

    private static void RestoreClipboard(List<(uint Format, IntPtr Data)> snapshot)
    {
        if (!EmptyClipboard()) return;
        for (int index = 0; index < snapshot.Count; index++)
        {
            var (format, data) = snapshot[index];
            if (SetClipboardData(format, data) != IntPtr.Zero)
                snapshot[index] = (format, IntPtr.Zero); // Windows owns the restored handle.
        }
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", EntryPoint = "GetClipboardFormatNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatName(uint format, System.Text.StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("ole32.dll")] private static extern IntPtr OleDuplicateData(IntPtr data, ushort format, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
