using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Translator.Desktop;

internal enum SakuraGlyph
{
    Home, Selection, Scan, Audio, Settings, Tray, Model, Window, Folder, Copy, Check,
    Stop, Pause, Play, Close, Refresh, Grip, Arrow, Type, Keyboard, Exit
}

internal static class SakuraVisuals
{
    public static readonly IBrush Pink = Brush.Parse("#942557");
    public static readonly IBrush Caption = Brush.Parse("#F533243E");
    public static readonly IBrush CaptionLine = Brush.Parse("#8F6891");
    public static readonly IBrush CaptionSecondary = Brush.Parse("#EBCDE5");
    public static readonly IBrush Frame = Brush.Parse("#E765A5");
    public static readonly IBrush Paused = Brush.Parse("#EAB366");

    public static Control Icon(SakuraGlyph glyph, IBrush? color = null, double size = 18)
    {
        string path = glyph switch
        {
            SakuraGlyph.Home => "M4 4 H10 V10 H4 Z M14 4 H20 V10 H14 Z M4 14 H10 V20 H4 Z M14 14 H20 V20 H14 Z",
            SakuraGlyph.Selection => "M5 4 L19 12 L13 14 L11 20 Z M17 3 V8 M14.5 5.5 H19.5",
            SakuraGlyph.Scan => "M4 9 V5 Q4 4 5 4 H9 M15 4 H19 Q20 4 20 5 V9 M20 15 V19 Q20 20 19 20 H15 M9 20 H5 Q4 20 4 19 V15 M3 12 H21 M8 16 H14",
            SakuraGlyph.Audio => "M4 14 V11 C4 0 20 0 20 11 V14 M4 13 Q4 11 6 11 Q8 11 8 13 V17 Q8 19 6 19 Q4 19 4 17 Z M16 13 Q16 11 18 11 Q20 11 20 13 V17 Q20 19 18 19 Q16 19 16 17 Z M11 20 H13",
            SakuraGlyph.Settings => "M9 5 L10 3 H14 L15 5 L17 6 H19 L21 10 L20 12 L21 14 L19 18 H17 L15 19 L14 21 H10 L9 19 L7 18 H5 L3 14 L4 12 L3 10 L5 6 H7 Z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12 Z",
            SakuraGlyph.Tray => "M5 5 H19 Q21 5 21 7 V17 Q21 19 19 19 H5 Q3 19 3 17 V7 Q3 5 5 5 Z M3 14 H21 M9 11 L12 8 L15 11 M16 16.5 H18",
            SakuraGlyph.Model => "M7 5 H17 Q19 5 19 7 V17 Q19 19 17 19 H7 Q5 19 5 17 V7 Q5 5 7 5 Z M9 9 H15 V15 H9 Z M9 2 V5 M15 2 V5 M9 19 V22 M15 19 V22 M2 9 H5 M2 15 H5 M19 9 H22 M19 15 H22",
            SakuraGlyph.Window => "M5 4 H19 Q21 4 21 6 V18 Q21 20 19 20 H5 Q3 20 3 18 V6 Q3 4 5 4 Z M3 9 H21 M7 13 H16 M7 16 H12",
            SakuraGlyph.Folder => "M4 7 Q4 5 6 5 H10 L13 8 H18 Q20 8 20 10 V17 Q20 19 18 19 H6 Q4 19 4 17 Z",
            SakuraGlyph.Copy => "M8 8 H18 Q20 8 20 10 V18 Q20 20 18 20 H10 Q8 20 8 18 Z M16 8 V6 Q16 4 14 4 H6 Q4 4 4 6 V14 Q4 16 6 16 H8",
            SakuraGlyph.Check => "M5 12 L10 17 L19 7",
            SakuraGlyph.Stop => "M8 6 H16 Q18 6 18 8 V16 Q18 18 16 18 H8 Q6 18 6 16 V8 Q6 6 8 6 Z",
            SakuraGlyph.Pause => "M9 6 V18 M15 6 V18",
            SakuraGlyph.Play => "M8 5 L19 12 L8 19 Z",
            SakuraGlyph.Close => "M6 6 L18 18 M18 6 L6 18",
            SakuraGlyph.Refresh => "M20 10 A8 8 0 1 0 19 16 M20 5 V10 H15",
            SakuraGlyph.Grip => "M9 6 H10 M14 6 H15 M9 12 H10 M14 12 H15 M9 18 H10 M14 18 H15",
            SakuraGlyph.Arrow => "M5 12 H19 M13 6 L19 12 L13 18",
            SakuraGlyph.Type => "M4 18 L9 6 L14 18 M6 14 H12 M15 18 L18 10 L21 18 M16 15 H20",
            SakuraGlyph.Keyboard => "M5 6 H19 Q21 6 21 8 V16 Q21 18 19 18 H5 Q3 18 3 16 V8 Q3 6 5 6 Z M7 10 H8 M11 10 H12 M16 10 H17 M8 14 H16",
            SakuraGlyph.Exit => "M12 4 H6 Q4 4 4 6 V18 Q4 20 6 20 H12 M11 12 H21 M17 8 L21 12 L17 16",
            _ => throw new ArgumentOutOfRangeException(nameof(glyph))
        };

        var drawing = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        drawing.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(path), Stroke = color ?? Pink, StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round
        });
        return new Viewbox { Width = size, Height = size, Child = drawing, IsHitTestVisible = false };
    }

    public static Control Label(SakuraGlyph glyph, string text, IBrush? color = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Icon(glyph, color));
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (color is not null) label.Foreground = color;
        row.Children.Add(label);
        return row;
    }

    public static Button Tool(SakuraGlyph glyph, string label)
    {
        var button = new Button { Classes = { "caption-tool" } };
        SetTool(button, glyph, label);
        return button;
    }

    public static void SetTool(Button button, SakuraGlyph glyph, string label)
    {
        button.Content = Icon(glyph, Brushes.White, 17);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }

    public static Border CaptionSurface(Control child) => new()
    {
        Background = Caption, BorderBrush = CaptionLine, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 11),
        ClipToBounds = true, Child = child
    };
}
