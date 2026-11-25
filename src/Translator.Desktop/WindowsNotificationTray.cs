using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Translator.Platform.Windows;

namespace Translator.Desktop;

internal sealed class WindowsNotificationTray : IDisposable
{
    private const uint IconId = 1;
    private const uint TrayNotification = 0x8001;
    private const uint AddIcon = 0, ModifyIcon = 1, DeleteIcon = 2, SetIconVersion = 4;
    private const int ContextMenu = 0x7B, LeftButtonUp = 0x202, RightButtonUp = 0x205;
    private const int SelectIcon = 0x400, KeySelectIcon = 0x401;
    private readonly Window owner = new() { ShowInTaskbar = false, ShowActivated = false };
    private readonly DispatcherTimer retry = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly NativeMenu menu;
    private readonly Action openWorkbench;
    private readonly Action refreshMenu;
    private readonly IntPtr handle;
    private readonly IntPtr icon;
    private readonly uint taskbarCreated;
    private string tooltip = App.DisplayName;
    private bool version4;
    private bool menuQueued;
    private bool menuOpen;
    private bool disposed;

    public WindowsNotificationTray(NativeMenu menu, Action openWorkbench, Action refreshMenu)
    {
        this.menu = menu;
        this.openWorkbench = openWorkbench;
        this.refreshMenu = refreshMenu;
        handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "translator.ico");
        int size = GetSystemMetrics(49); // SM_CXSMICON
        icon = LoadImage(IntPtr.Zero, iconPath, 1, size, size, 0x10); // IMAGE_ICON, LR_LOADFROMFILE
        if (handle == IntPtr.Zero || icon == IntPtr.Zero)
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            owner.Close();
            throw new Win32Exception("无法创建 Windows 托盘图标。");
        }

        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        Win32Properties.AddWndProcHookCallback(owner, OnMessage);
        retry.Tick += (_, _) => TryRegister();
    }

    public bool IsRegistered { get; private set; }
    public int RegistrationError { get; private set; }

    public void Start()
    {
        TryRegister();
        retry.Start();
    }

    public void SetTooltip(string text)
    {
        if (tooltip == text) return;
        tooltip = text;
        if (!IsRegistered) return;
        var data = IconData();
        IsRegistered = ShellNotifyIcon(ModifyIcon, ref data);
        RegistrationError = IsRegistered ? 0 : Marshal.GetLastPInvokeError();
    }

    private void TryRegister()
    {
        if (disposed || IsRegistered) return;
        var data = IconData();
        IsRegistered = ShellNotifyIcon(AddIcon, ref data);
        RegistrationError = IsRegistered ? 0 : Marshal.GetLastPInvokeError();
        if (IsRegistered)
        {
            data.Version = 4;
            version4 = ShellNotifyIcon(SetIconVersion, ref data);
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (taskbarCreated != 0 && message == taskbarCreated)
        {
            IsRegistered = false;
            TryRegister();
        }
        else if (message == TrayNotification)
        {
            int notification = (int)(lParam.ToInt64() & 0xFFFF);
            if ((notification is ContextMenu or LeftButtonUp or SelectIcon)
                || (!version4 && notification == RightButtonUp))
            {
                QueueMenu(version4, unchecked((int)wParam.ToInt64()));
            }
            else if (notification == KeySelectIcon)
                Dispatcher.UIThread.Post(openWorkbench);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void QueueMenu(bool hasPosition, int coordinates)
    {
        if (disposed || menuOpen || menuQueued) return;
        menuQueued = true;
        int x = (short)(coordinates & 0xFFFF), y = (short)(coordinates >> 16);
        Dispatcher.UIThread.Post(() =>
        {
            menuQueued = false;
            ShowMenu(hasPosition, x, y);
        });
    }

    private void ShowMenu(bool hasPosition, int x, int y)
    {
        if (disposed || menuOpen) return;
        if (!hasPosition) (x, y) = WindowsScreenCapture.CursorPosition();
        refreshMenu();
        IntPtr popup = CreatePopupMenu();
        if (popup == IntPtr.Zero) return;
        menuOpen = true;
        try
        {
            var commands = new List<NativeMenuItem>();
            AddEntries(popup, menu);
            SetForegroundWindow(handle);
            uint selection = TrackPopupMenuEx(popup, 0x0102, x, y, handle, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
            PostMessage(handle, 0, IntPtr.Zero, IntPtr.Zero);
            if (selection >= 1 && selection <= commands.Count)
                TrayMenu.InvokeAfterClose(commands[(int)selection - 1]);

            void AddEntries(IntPtr parent, NativeMenu current)
            {
                foreach (NativeMenuItemBase entry in current.Items)
                {
                    if (entry is NativeMenuItemSeparator)
                    {
                        AppendMenu(parent, 0x800, UIntPtr.Zero, null);
                        continue;
                    }
                    if (entry is not NativeMenuItem item) continue;
                    uint flags = (item.IsEnabled ? 0u : 1u) | (item.IsChecked ? 8u : 0u);
                    string? label = item.Header?.ToString()?.Replace("&", "&&");
                    if (item.Menu is { } submenu)
                    {
                        IntPtr child = CreatePopupMenu();
                        if (child == IntPtr.Zero) continue;
                        AddEntries(child, submenu);
                        if (!AppendMenu(parent, flags | 0x10, (UIntPtr)(nuint)child, label))
                            DestroyMenu(child);
                        continue;
                    }
                    commands.Add(item);
                    AppendMenu(parent, flags, (UIntPtr)commands.Count, label);
                }
            }
        }
        finally
        {
            DestroyMenu(popup);
            menuOpen = false;
        }
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = handle, Id = IconId,
        Flags = 0x87, Callback = TrayNotification, Icon = icon, Tip = tooltip, Info = "", InfoTitle = ""
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        retry.Stop();
        var data = IconData();
        ShellNotifyIcon(DeleteIcon, ref data);
        IsRegistered = false;
        Win32Properties.RemoveWndProcHookCallback(owner, OnMessage);
        owner.Close();
        DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr module, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? label);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr reserved);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
