using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Translator.Desktop;

internal static class TrayMenu
{
    public static NativeMenuItem Add(NativeMenu menu, string label, SakuraGlyph glyph, Action action)
    {
        var item = new IconMenuItem(label, glyph) { Command = new ClickCommand(action) };
        menu.Items.Add(item);
        return item;
    }

    public static void AddSubmenu(NativeMenu menu, string label, SakuraGlyph glyph, NativeMenu submenu) =>
        menu.Items.Add(new IconMenuItem(label, glyph) { Menu = submenu });

    public static MenuFlyout CreateFlyout(NativeMenu menu, Action refresh)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            refresh();
            flyout.Items.Clear();
            foreach (Control item in CreateItems(menu)) flyout.Items.Add(item);
        };
        return flyout;
    }

    private static IEnumerable<Control> CreateItems(NativeMenu menu)
    {
        foreach (NativeMenuItemBase entry in menu.Items)
        {
            if (entry is NativeMenuItemSeparator)
            {
                yield return new Separator();
                continue;
            }
            if (entry is not NativeMenuItem source) continue;

            var item = new MenuItem
            {
                Header = source.Header,
                IsEnabled = source.IsEnabled,
                Icon = source is IconMenuItem icon ? SakuraVisuals.Icon(icon.Glyph, size: 15) : null,
                ToggleType = source.ToggleType,
                IsChecked = source.IsChecked
            };
            if (source.Menu is { } submenu)
            {
                foreach (Control child in CreateItems(submenu)) item.Items.Add(child);
            }
            else item.Click += (_, _) => InvokeAfterClose(source);
            yield return item;
        }
    }

    public static void InvokeAfterClose(NativeMenuItem item) => Dispatcher.UIThread.Post(() =>
    {
        if (item.IsEnabled && item.Command is { } command && command.CanExecute(item.CommandParameter))
            command.Execute(item.CommandParameter);
    }, DispatcherPriority.Background);

    private sealed class IconMenuItem(string label, SakuraGlyph glyph) : NativeMenuItem(label)
    {
        public SakuraGlyph Glyph { get; } = glyph;
    }

    private sealed class ClickCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
