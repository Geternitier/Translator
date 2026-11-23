using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using Translator.Core;

namespace Translator.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSelectedTextSource(int maxSelectionLength) : ITextSelectionSource
{
    public async IAsyncEnumerable<SelectedText?> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(30));
        using var pdfSelection = new WindowsPdfSelection();
        var history = new SelectionHistory();
        long nextRead = 0;
        int pressVersion = 0;
        IntPtr copiedWindow = IntPtr.Zero;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            IntPtr window = GetForegroundWindow();
            int currentPress = pdfSelection.PressVersion;
            bool finishedPdfSelection = pdfSelection.TakeFinishedSelection(window) && WindowsPdfSelection.IsReaderWindow(window);
            if (copiedWindow != window || currentPress != pressVersion) copiedWindow = IntPtr.Zero;
            pressVersion = currentPress;
            if (!finishedPdfSelection && (copiedWindow != IntPtr.Zero || Environment.TickCount64 < nextRead)) continue;

            nextRead = Environment.TickCount64 + 360;
            SelectedText? selected = Read(window, out bool canCopy, out bool selectionKnown);
            bool fromCopy = false;
            if (finishedPdfSelection && selected is null && canCopy)
            {
                selected = await Task.Run(() => WindowsPdfSelection.TryRead(window, maxSelectionLength, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (selected is not null)
                {
                    copiedWindow = window;
                    fromCopy = true;
                }
            }

            if (history.TryReport(window, selected?.Text, selectionKnown, finishedPdfSelection, fromCopy))
                yield return selected;
        }
    }

    internal sealed class SelectionHistory
    {
        private IntPtr previousWindow;
        private IntPtr reportedWindow;
        private string? previousText;
        private string? reportedText;
        private bool reportedFromCopy;

        public bool TryReport(IntPtr window, string? text, bool selectionKnown, bool finishedPdfSelection, bool fromCopy)
        {
            // Losing focus or an accessibility provider is not evidence that the selection was cleared.
            if (text is null && (!selectionKnown || window != reportedWindow || reportedFromCopy))
            {
                previousWindow = IntPtr.Zero;
                previousText = null;
                return false;
            }

            bool stable = (text is not null && finishedPdfSelection)
                || (window == previousWindow && text == previousText);
            previousWindow = window;
            previousText = text;
            if (!stable) return false;

            bool changed = window != reportedWindow || text != reportedText;
            reportedWindow = window;
            reportedText = text;
            reportedFromCopy = fromCopy;
            return changed;
        }
    }

    private SelectedText? Read(IntPtr window, out bool canCopy, out bool selectionKnown)
    {
        canCopy = false;
        selectionKnown = false;
        if (GetWindowThreadProcessId(window, out uint pid) == 0 || pid == Environment.ProcessId)
            return null;
        canCopy = true;

        IUIAutomation? automation = null;
        IUIAutomationElement? element = null;
        IUIAutomationTextPattern? pattern = null;
        IUIAutomationTextRangeArray? ranges = null;
        try
        {
            automation = new CUIAutomation();
            element = automation.GetFocusedElement();
            if (element is not null && (element.CurrentIsPassword != 0 || element.CurrentProcessId == Environment.ProcessId))
            {
                canCopy = false;
                return null;
            }
            if (element is null) return null;
            pattern = element.GetCurrentPattern(10014) as IUIAutomationTextPattern;
            if (pattern is null) return null;
            ranges = pattern.GetSelection();
            if (ranges is null)
            {
                selectionKnown = true;
                return null;
            }

            var text = new System.Text.StringBuilder();
            ScreenRegion? anchor = null;
            for (int index = 0; index < ranges.Length && text.Length <= maxSelectionLength; index++)
            {
                IUIAutomationTextRange? range = ranges.GetElement(index);
                try
                {
                    string? value = range?.GetText(maxSelectionLength + 1 - text.Length)?.Trim();
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (text.Length != 0) text.AppendLine();
                    text.Append(value);
                    anchor ??= GetAnchor(range!.GetBoundingRectangles());
                }
                finally { Release(range); }
            }

            selectionKnown = true;
            return text.Length == 0 ? null : new SelectedText(text.ToString(), anchor);
        }
        catch (COMException) { return null; }
        finally
        {
            Release(ranges);
            Release(pattern);
            Release(element);
            Release(automation);
        }
    }

    private static ScreenRegion? GetAnchor(double[]? rectangle)
    {
        if (rectangle is not { Length: >= 4 } || rectangle[2] <= 0 || rectangle[3] <= 0) return null;
        int x = (int)rectangle[0], y = (int)rectangle[1];
        DesktopDisplay display = WindowsScreenCapture.DisplayAt(x, y);
        return new ScreenRegion(display.Id, x - display.X, y - display.Y, (int)rectangle[2], (int)rectangle[3]);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
