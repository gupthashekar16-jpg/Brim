using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brim.Core;

namespace Brim.Notch.Views;

/// <summary>One tab of the open notch.</summary>
public abstract class NotchView : Grid
{
    protected NotchWindow N { get; }
    protected static AppSettings S => App.S;

    protected NotchView(NotchWindow n) { N = n; }

    /// <summary>Called when the tab becomes visible.</summary>
    public virtual void OnShow() => Refresh();
    public virtual void OnHide() { }
    /// <summary>Called every second while visible.</summary>
    public virtual void Refresh() { }
    /// <summary>Called every rendered frame while visible, if <see cref="WantsFrames"/>.</summary>
    public virtual void Frame(double dt) { }
    public virtual bool WantsFrames => false;

    /// <summary>Uppercase card label with a coloured glyph (stats style).</summary>
    protected static StackPanel Label(string glyph, string text, Brush color) =>
        Ui.H(7, Ui.Icon(glyph, 13, color), Ui.T(text.ToUpperInvariant(), 12, FontWeights.SemiBold, Pal.Sub));

    protected static ScrollViewer Scroll(UIElement content, double maxHeight, bool horizontal = false)
    {
        var sv = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Focusable = false,
            PanningMode = horizontal ? PanningMode.HorizontalOnly : PanningMode.VerticalOnly,
        };
        if (maxHeight > 0) sv.MaxHeight = maxHeight;
        if (horizontal)
            sv.PreviewMouseWheel += (_, e) => { sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0); e.Handled = true; };
        return sv;
    }
}

/// <summary>Tiny vector icons not in Segoe Fluent Icons.</summary>
public static class Mini
{
    static Path P(string data, Brush fill, double size, bool stroke = false)
    {
        var p = new Path
        {
            Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (stroke) { p.Stroke = fill; p.StrokeThickness = 1.4; p.StrokeLineJoin = PenLineJoin.Round; p.StrokeStartLineCap = p.StrokeEndLineCap = PenLineCap.Round; }
        else p.Fill = fill;
        return p;
    }

    public static Path Drop(Brush b, double s = 12) => P("M6,0 C6,0 0,7 0,10.5 A6,6 0 0 0 12,10.5 C12,7 6,0 6,0 Z", b, s, true);
    public static Path Wind(Brush b, double s = 12) => P("M0,4 H9 A2.5,2.5 0 1 0 6.5,1.5 M0,8 H12 A2.5,2.5 0 1 1 9.5,10.5 M0,12 H6", b, s, true);
    public static Path Umbrella(Brush b, double s = 12) => P("M0,7 A7,7 0 0 1 14,7 Z M7,7 V12.5 A1.5,1.5 0 0 1 4,12.5", b, s, true);
    public static Path Sunset(Brush b, double s = 12) => P("M1,13 H15 M4,10 A4,4 0 0 1 12,10 M8,1 V4.5 M6,3 L8,5 L10,3", b, s, true);
    public static Path Rocket(Brush b, double s = 14) => P("M5,11 L3,13 M12,1 C8,1 5,5 4,8 L8,12 C11,11 15,8 15,4 Z M4,8 L1,8 L3,5 L6,5 M8,12 L8,15 L11,13 L11,10", b, s, true);
    public static Path BellOff(Brush b, double s = 14) => P("M3,11 H13 C12,10 11.5,9 11.5,6.5 A3.5,3.5 0 0 0 5,4.8 M4.5,6.5 C4.5,9 4,10 3,11 M6.5,13.5 A1.6,1.6 0 0 0 9.5,13.5 M1,1 L15,15", b, s, true);
    public static Path Arrows(Brush b, double s = 14) => P("M4,1 V15 M1,12 L4,15 L7,12 M12,15 V1 M9,4 L12,1 L15,4", b, s, true);
}
