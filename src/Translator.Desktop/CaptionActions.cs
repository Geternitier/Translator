using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Translator.Desktop;

internal sealed class CaptionActions : IDisposable
{
    private readonly TopLevel owner;
    private readonly Button copy = SakuraVisuals.Tool(SakuraGlyph.Copy, "复制译文");
    private readonly Button primary = SakuraVisuals.Tool(SakuraGlyph.Stop, "停止生成");
    private readonly Button close = SakuraVisuals.Tool(SakuraGlyph.Close, "关闭译文");
    private readonly DispatcherTimer resetCopy = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    private readonly string copyLabel;
    private Func<string>? copyText;
    private Action? primaryAction;
    private Action? closeAction;
    private int copyVersion;
    private bool disposed;

    public CaptionActions(TopLevel owner, string copyLabel = "复制译文")
    {
        this.owner = owner;
        this.copyLabel = copyLabel;
        View = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (owner is FloatingOverlay overlay)
        {
            Button keyboard = SakuraVisuals.Tool(SakuraGlyph.Keyboard, "启用浮窗键盘操作，Tab 切换按钮，方向键移动");
            keyboard.Click += (_, _) => overlay.FocusTools(copy.IsEnabled ? copy : primary.IsEnabled ? primary : close);
            View.Children.Add(keyboard);
        }
        View.Children.Add(copy);
        View.Children.Add(primary);
        View.Children.Add(close);
        copy.Click += async (_, _) => await CopyAsync();
        primary.Click += (_, _) => primaryAction?.Invoke();
        close.Click += (_, _) => closeAction?.Invoke();
        resetCopy.Tick += (_, _) => ResetCopy();
        ResetCopy();
    }

    public StackPanel View { get; }

    public void SetActions(Func<string>? text, Action? action, Action? dismiss,
        SakuraGlyph glyph = SakuraGlyph.Stop, string actionLabel = "停止生成", string closeLabel = "关闭译文")
    {
        View.IsVisible = text is not null || action is not null || dismiss is not null;
        SetCopyText(text);
        SetPrimary(action, glyph, actionLabel);
        closeAction = dismiss;
        close.IsEnabled = dismiss is not null;
        if (ToolTip.GetTip(close) as string != closeLabel) SakuraVisuals.SetTool(close, SakuraGlyph.Close, closeLabel);
    }

    public void SetCopyText(Func<string>? text)
    {
        copyText = text;
        copy.IsEnabled = text is not null;
        if (text is null) ResetCopy();
    }

    public void SetPrimary(Action? action, SakuraGlyph glyph, string label)
    {
        primaryAction = action;
        if (action is null && primary.IsFocused) (copy.IsEnabled ? copy : close).Focus();
        primary.IsEnabled = action is not null;
        if (ToolTip.GetTip(primary) as string != label) SakuraVisuals.SetTool(primary, glyph, label);
    }

    private async Task CopyAsync()
    {
        if (disposed || owner.Clipboard is not { } clipboard || copyText?.Invoke() is not { Length: > 0 } text) return;
        resetCopy.Stop();
        int version = ++copyVersion;
        bool copied;
        try
        {
            await clipboard.SetTextAsync(text);
            copied = true;
        }
        catch (Exception)
        {
            copied = false;
        }
        if (disposed || copyVersion != version) return;
        SakuraVisuals.SetTool(copy, copied ? SakuraGlyph.Check : SakuraGlyph.Copy, copied ? "已复制" : "复制失败，请重试");
        resetCopy.Start();
    }

    public void ResetCopy()
    {
        copyVersion++;
        resetCopy.Stop();
        if (ToolTip.GetTip(copy) as string != copyLabel) SakuraVisuals.SetTool(copy, SakuraGlyph.Copy, copyLabel);
    }

    public void Dispose()
    {
        disposed = true;
        resetCopy.Stop();
    }
}
