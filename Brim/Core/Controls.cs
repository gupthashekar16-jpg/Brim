using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Brim.Core;

/// <summary>Horizontal fill meter.</summary>
public sealed class Meter : Grid
{
    readonly ColumnDefinition _a = new(), _b = new();
    readonly Border _fill;

    public Meter(Brush fill, double height = 6, Brush? track = null)
    {
        Height = height;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(_a);
        ColumnDefinitions.Add(_b);
        var t = new Border { CornerRadius = new CornerRadius(height / 2), Background = track ?? Pal.Track };
        SetColumnSpan(t, 2);
        _fill = new Border { CornerRadius = new CornerRadius(height / 2), Background = fill };
        Children.Add(t);
        Children.Add(_fill);
        Value = 0;
    }

    public double Value
    {
        set
        {
            var v = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            _a.Width = new GridLength(Math.Max(v, 0.0001), GridUnitType.Star);
            _b.Width = new GridLength(Math.Max(1 - v, 0.0001), GridUnitType.Star);
            _fill.Visibility = v <= 0.002 ? Visibility.Hidden : Visibility.Visible;
        }
    }

    public Brush Fill { set => _fill.Background = value; }
}

/// <summary>Tiny line chart of recent values in 0..1.</summary>
public sealed class Sparkline : FrameworkElement
{
    readonly Queue<double> _vals = new();
    readonly Pen _pen;
    readonly Brush _area;
    public int Capacity { get; set; } = 40;

    public Sparkline(Color c)
    {
        _pen = new Pen(new SolidColorBrush(c), 1.6) { LineJoin = PenLineJoin.Round };
        _pen.Freeze();
        _area = new LinearGradientBrush(Pal.A(c, 90), Pal.A(c, 0), 90);
        _area.Freeze();
    }

    public void SetAll(IEnumerable<double> values)
    {
        _vals.Clear();
        foreach (var v in values.TakeLast(Capacity)) _vals.Enqueue(Math.Clamp(v, 0, 1));
        InvalidateVisual();
    }

    public void Push(double v)
    {
        _vals.Enqueue(Math.Clamp(v, 0, 1));
        while (_vals.Count > Capacity) _vals.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_vals.Count < 2 || ActualWidth < 2) return;
        double w = ActualWidth, h = ActualHeight, step = w / (Capacity - 1);
        var pts = _vals.ToArray();
        double x0 = w - (pts.Length - 1) * step;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        using (var a = area.Open())
        {
            for (int i = 0; i < pts.Length; i++)
            {
                var p = new Point(x0 + i * step, h - 1 - pts[i] * (h - 2));
                if (i == 0) { g.BeginFigure(p, false, false); a.BeginFigure(new Point(p.X, h), true, true); a.LineTo(p, false, false); }
                else { g.LineTo(p, true, true); a.LineTo(p, false, false); }
            }
            a.LineTo(new Point(w, h), false, false);
        }
        dc.DrawGeometry(_area, null, area);
        dc.DrawGeometry(null, _pen, line);
    }
}

/// <summary>Circular progress ring with an optional centre glyph.</summary>
public sealed class Ring : FrameworkElement
{
    double _value;
    Brush _color = Pal.Green;
    string? _glyph;
    readonly double _thickness;

    public Ring(double size, double thickness = 2.4)
    {
        Width = Height = size;
        _thickness = thickness;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public void Set(double value, Brush color, string? glyph)
    {
        _value = Math.Clamp(value, 0, 1);
        _color = color;
        _glyph = glyph;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight), r = (s - _thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Pal.Track, _thickness), c, r, r);
        if (_value > 0.001)
        {
            var pen = new Pen(_color, _thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (_value >= 0.999) dc.DrawEllipse(null, pen, c, r, r);
            else
            {
                double a = _value * 2 * Math.PI;
                var start = new Point(c.X, c.Y - r);
                var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    ctx.ArcTo(end, new Size(r, r), 0, _value > 0.5, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(null, pen, g);
            }
        }
        if (_glyph != null)
        {
            var ft = new FormattedText(_glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                s * 0.46, _color, 1.5);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }
    }
}

/// <summary>Animated equaliser bars fed with levels in 0..1.</summary>
public sealed class Bars : FrameworkElement
{
    double[] _levels = [];
    public Brush Fill { get; set; } = Pal.White;
    public double Gap { get; set; } = 2;
    public double BarWidth { get; set; } = 3;

    public void Set(double[] levels)
    {
        bool resize = levels.Length != _levels.Length;
        _levels = levels;
        if (resize) InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_levels.Length == 0) return;
        double h = ActualHeight, x = 0;
        foreach (var l in _levels)
        {
            if (l < 0.01) { x += BarWidth + Gap; continue; }
            double bh = Math.Max(BarWidth, Math.Clamp(l, 0, 1) * h);
            dc.DrawRoundedRectangle(Fill, null, new Rect(x, (h - bh) / 2, BarWidth, bh), BarWidth / 2, BarWidth / 2);
            x += BarWidth + Gap;
        }
    }

    protected override Size MeasureOverride(Size available) =>
        new(Math.Max(0, _levels.Length * (BarWidth + Gap) - Gap), double.IsInfinity(available.Height) ? 14 : available.Height);
}

/// <summary>Wide slider with an inverted label where it is filled (Controls tab).</summary>
public sealed class BigSlider : Grid
{
    readonly Border _fill;
    readonly Grid _dark;
    readonly TextBlock _pctL, _pctD;
    double _value;
    bool _drag;
    public event Action<double>? ValueChanged;

    public BigSlider(string glyph, string title, double height = 52)
    {
        Height = height;
        Cursor = Cursors.Hand;
        Background = Pal.HitClear;
        Children.Add(Ui.Card(null, 14, new Thickness(0)));
        _fill = new Border { CornerRadius = new CornerRadius(14, 0, 0, 14), Background = Pal.White, HorizontalAlignment = HorizontalAlignment.Left };
        Children.Add(_fill);
        Grid Layer(Brush fg, out TextBlock pct)
        {
            pct = Ui.T("0%", 14, FontWeights.SemiBold, fg);
            return Ui.G("Auto,*,Auto", "",
                Ui.Icon(glyph, 16, fg).M(20, 0, 0, 0),
                Ui.T(title, 14, FontWeights.SemiBold, fg).M(16, 0, 0, 0).At(1),
                pct.M(0, 0, 18, 0).At(2));
        }
        var light = Layer(Pal.Text, out _pctL);
        _dark = Layer(Pal.Ink, out _pctD);
        light.IsHitTestVisible = _dark.IsHitTestVisible = false;
        Children.Add(light);
        Children.Add(_dark);
        SizeChanged += (_, _) => Paint();
        MouseLeftButtonDown += (_, e) => { _drag = true; CaptureMouse(); FromMouse(e); e.Handled = true; };
        MouseMove += (_, e) => { if (_drag) FromMouse(e); };
        MouseLeftButtonUp += (_, e) => { _drag = false; ReleaseMouseCapture(); e.Handled = true; };
        MouseWheel += (_, e) => { Value += e.Delta > 0 ? 0.02 : -0.02; ValueChanged?.Invoke(_value); e.Handled = true; };
    }

    void FromMouse(MouseEventArgs e)
    {
        if (ActualWidth <= 0) return;
        Value = e.GetPosition(this).X / ActualWidth;
        ValueChanged?.Invoke(_value);
    }

    public bool Dragging => _drag;

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Paint(); }
    }

    void Paint()
    {
        double w = ActualWidth * _value;
        _fill.Width = w;
        Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 14, 14);
        _dark.Clip = new RectangleGeometry(new Rect(0, 0, w, ActualHeight));
        _pctL.Text = _pctD.Text = $"{Math.Round(_value * 100)}%";
    }
}

/// <summary>Weather condition glyph built from Segoe icons (WMO codes).</summary>
public sealed class WeatherIcon : Grid
{
    public WeatherIcon(int code, bool day, double size)
    {
        Width = size * 1.25;
        Height = size * 1.1;
        var kind = WeatherKind(code);
        Brush cloud = Pal.Text;
        void Add(UIElement e) => Children.Add(e);
        TextBlock G(string g, double s, Brush b, double x, double y)
        {
            var t = Ui.Icon(g, s, b);
            t.HorizontalAlignment = HorizontalAlignment.Left;
            t.VerticalAlignment = VerticalAlignment.Top;
            t.Margin = new Thickness(x, y, 0, 0);
            return t;
        }
        switch (kind)
        {
            case "clear":
                Add(G(day ? Ic.Sun : Ic.Moon, size, day ? Pal.Yellow : Pal.Text, size * 0.1, 0));
                break;
            case "partly":
                Add(G(day ? Ic.Sun : Ic.Moon, size * 0.62, day ? Pal.Yellow : Pal.Sub, size * 0.5, -size * 0.05));
                Add(G(Ic.Cloud, size, cloud, 0, size * 0.12));
                break;
            case "cloudy":
                Add(G(Ic.Cloud, size, cloud, size * 0.1, size * 0.05));
                break;
            case "fog":
                Add(G(Ic.Cloud, size, Pal.Sub, size * 0.1, 0));
                for (int i = 0; i < 2; i++)
                    Add(new Border { Height = 1.5, Width = size * 0.8, Background = Pal.Sub, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(size * 0.2, size * (0.82 + i * 0.16), 0, 0) });
                break;
            case "rain":
            case "drizzle":
            case "snow":
            case "storm":
                Add(G(Ic.Cloud, size, cloud, size * 0.1, -size * 0.06));
                if (kind == "storm")
                    Add(G(Ic.Bolt, size * 0.5, Pal.Yellow, size * 0.42, size * 0.5));
                else
                {
                    var b = kind == "snow" ? Pal.White : Pal.Blue;
                    int n = kind == "drizzle" ? 2 : 3;
                    for (int i = 0; i < n; i++)
                    {
                        var x = size * (0.38 + i * 0.22);
                        if (kind == "snow")
                            Add(new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Fill = b, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, size * 0.85, 0, 0) });
                        else
                            Add(new Border { Width = 1.8, Height = size * 0.26, CornerRadius = new CornerRadius(1), Background = b, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, size * 0.78, 0, 0), RenderTransform = new SkewTransform(-18, 0) });
                    }
                }
                break;
        }
    }

    public static string WeatherKind(int code) => code switch
    {
        0 => "clear",
        1 or 2 => "partly",
        3 => "cloudy",
        45 or 48 => "fog",
        51 or 53 or 55 or 56 or 57 => "drizzle",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "rain",
        71 or 73 or 75 or 77 or 85 or 86 => "snow",
        95 or 96 or 99 => "storm",
        _ => "cloudy",
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 => "Rain showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Storm with hail",
        _ => "—",
    };
}
