using System.Windows.Input;
using Avalonia.Controls;

namespace Translator.Desktop;

internal static class TrayMenu
{
    public static NativeMenuItem Add(NativeMenu menu, string label, Action action)
    {
        var item = new NativeMenuItem(label) { Command = new ClickCommand(action) };
        menu.Items.Add(item);
        return item;
    }

    private sealed class ClickCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
