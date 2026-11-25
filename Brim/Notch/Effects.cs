using System.Windows;
using System.Windows.Media;
using Brim.Core;

namespace Brim.Notch;

public sealed record EffectInfo(string Key, string Name, string Desc, bool Music, bool Premium);

/// <summary>Animated backgrounds drawn behind the notch content.</summary>
public sealed class EffectCanvas : FrameworkElement
{
    public static readonly EffectInfo[] All =
    [
        new("None", "None", "No background", false, false),
        new("Visualizer", "Visualizer", "Bars that dance to your music", true, false),
        new("Ambient", "Ambient", "A soft glow from the album art", true, false),
        new("Waves", "Waves", "Rolling waves in album colours", true, false),
        new("Synthwave", "Synthwave", "An eighties sunset over a neon grid", true, false),
        new("Fireflies", "Fireflies", "Little lights drifting about", true, false),
        new("Holographic", "Holographic", "A shifting rainbow sheen", false, false),
        new("Topographic", "Topographic", "Drifting map contours", false, false),
        new("Aurora", "Aurora", "Ribbons of green and violet light", false, false),
        new("NightSky", "Night sky", "Twinkling stars and the odd shooting star", false, false),
        new("MidnightLiquid", "Ink Silk", "Slow dark silk tinted by the album art", true, true),
        new("LiquidTitanium", "Chrome Flow", "Brushed metal with moving highlights", true, true),
        new("Supernova", "Black Hole", "A dark sun ringed by glowing dust", true, true),
        new("CyberMatrix", "Hex Tunnel", "A spinning hexagon in a neon tunnel", true, true),
        new("Orrery", "Orbits", "A golden planet with circling moons", true, true),
        new("Prism", "Crystal", "A turning crystal that splits light into colour", true, true),
    ];

    public string Kind { get; set; } = "None";
    public Func<(Color a, Color b, double[] bands, double level, bool playing)>? Source { get; set; }
    double _t;
    readonly Random _rng = new(11);
    readonly (double x, double y, double vx, double vy, double ph)[] _flies;
    readonly (double x, double y, double s, double ph)[] _stars;
    double _meteor = -1, _mx, _my;

    public EffectCanvas()
    {
        IsHitTestVisible = false;
        _flies = Enumerable.Range(0, 28).Select(_ => (_rng.NextDouble(), _rng.NextDouble(), (_rng.NextDouble() - .5) * .02, (_rng.NextDouble() - .5) * .02, _rng.NextDouble() * 6.28)).ToArray();
        _stars = Enumerable.Range(0, 90).Select(_ => (_rng.NextDouble(), _rng.NextDouble(), _rng.NextDouble() * 1.3 + .4, _rng.NextDouble() * 6.28)).ToArray();
    }

    public void Advance(double dt)
    {
        _t += dt;
        if (Kind == "Fireflies")
            for (int i = 0; i < _flies.Length; i++)
            {
                var f = _flies[i];
                f.vx += (_rng.NextDouble() - .5) * .004; f.vy += (_rng.NextDouble() - .5) * .004;
                f.vx = Math.Clamp(f.vx, -.03, .03); f.vy = Math.Clamp(f.vy, -.03, .03);
                f.x = (f.x + f.vx * dt + 1) % 1; f.y = (f.y + f.vy * dt + 1) % 1;
                _flies[i] = f;
            }
        InvalidateVisual();
    }

    static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);
    static Brush Radial(Color c, double alpha) => new RadialGradientBrush(A(c, alpha), A(c, 0));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2 || Kind == "None") return;
        var (a, b, bands, level, playing) = Source?.Invoke() ?? (Color.FromRgb(70, 120, 255), Color.FromRgb(150, 70, 220), [], 0, false);
        if (!playing) level = 0.15 + 0.08 * Math.Sin(_t * 1.3);
        double t = _t;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        switch (Kind)
        {
            case "Visualizer":
            {
                int n = Math.Max(8, (int)(w / 9));
                var fill = new LinearGradientBrush(A(a, .75), A(b, .35), 90);
                for (int i = 0; i < n; i++)
                {
                    double v = bands.Length > 0 ? bands[(int)((double)i / n * bands.Length)] : 0;
                    if (!playing) v = .06 + .04 * Math.Sin(t * 2 + i * .5);
                    double bh = Math.Max(2, v * h * .5);
                    dc.DrawRoundedRectangle(fill, null, new Rect(i * 9 + 2, h - bh - 2, 4, bh), 2, 2);
                }
                break;
            }
            case "Ambient":
            {
                double r = Math.Max(w, h) * (.55 + level * .25);
                dc.DrawEllipse(Radial(a, .55), null, new Point(w * (.28 + .1 * Math.Sin(t * .3)), h * .65), r, r * .9);
                dc.DrawEllipse(Radial(b, .5), null, new Point(w * (.74 + .1 * Math.Cos(t * .25)), h * .3), r * .8, r * .8);
                break;
            }
            case "Waves":
                for (int i = 0; i < 3; i++)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(0, h), true, true);
                        for (double x = 0; x <= w; x += 6)
                            c.LineTo(new Point(x, h * (.58 + .1 * i) + Math.Sin(x / w * 6.28 * 1.2 + t * (.8 + .35 * i) + i) * h * .07 * (1 + level)), true, false);
                        c.LineTo(new Point(w, h), true, false);
                    }
                    dc.DrawGeometry(new SolidColorBrush(A(Pal.Mix(a, b, i / 2.0), .32)), null, g);
                }
                break;
            case "Synthwave":
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(150, 30, 10, 60), Color.FromArgb(150, 90, 20, 90), 90), null, new Rect(0, 0, w, h));
                double hz = h * .62, sr = h * .3 * (1 + level * .08);
                var sun = new Point(w / 2, hz - sr * .25);
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, hz)));
                dc.DrawEllipse(new LinearGradientBrush(Color.FromRgb(255, 214, 90), Color.FromRgb(255, 70, 140), 90), null, sun, sr, sr);
                for (int i = 0; i < 5; i++)
                {
                    double y = sun.Y + sr * (.15 + i * .17);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(255, 45, 12, 70)), null, new Rect(0, y, w, 2 + i * 1.3));
                }
                dc.Pop();
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 80, 200)), 1);
                for (int i = -10; i <= 10; i++) dc.DrawLine(pen, new Point(w / 2 + i * 8, hz), new Point(w / 2 + i * w / 7, h));
                for (int i = 0; i < 8; i++)
                {
                    double p = (i + (t * .5 % 1)) / 8, y = hz + (h - hz) * p * p;
                    dc.DrawLine(pen, new Point(0, y), new Point(w, y));
                }
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(200, 120, 230, 255)), 1.2), new Point(0, hz), new Point(w, hz));
                break;
            }
            case "Fireflies":
                foreach (var f in _flies)
                {
                    double tw = .45 + .55 * Math.Sin(t * 1.7 + f.ph);
                    double r = 7 + 5 * level;
                    var c = Pal.Mix(Color.FromRgb(235, 225, 120), a, .25);
                    dc.DrawEllipse(Radial(c, .7 * tw), null, new Point(f.x * w, f.y * h), r, r);
                    dc.DrawEllipse(new SolidColorBrush(A(c, tw)), null, new Point(f.x * w, f.y * h), 1.6, 1.6);
                }
                break;
            case "Holographic":
            {
                var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                for (int i = 0; i <= 6; i++) lg.GradientStops.Add(new GradientStop(A(Pal.FromHsv(t * 25 + i * 55, .45, 1), .16), i / 6.0));
                dc.DrawRectangle(lg, null, new Rect(0, 0, w, h));
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)), 1);
                for (double x = -h + (t * 12 % 14); x < w; x += 14) dc.DrawLine(pen, new Point(x, h), new Point(x + h, 0));
                break;
            }
            case "Topographic":
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(70, 90, 140, 255)), 1);
                var c0 = new Point(w * .55, h * .55);
                for (int k = 1; k <= 12; k++)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        for (int j = 0; j <= 72; j++)
                        {
                            double th = j / 72.0 * 6.2832;
                            double r = k * Math.Max(w, h) * .055 * (1 + .16 * Math.Sin(3 * th + t * .3 + k * .4) + .08 * Math.Sin(5 * th - t * .2));
                            var p = new Point(c0.X + Math.Cos(th) * r * 1.4, c0.Y + Math.Sin(th) * r);
                            if (j == 0) c.BeginFigure(p, false, true); else c.LineTo(p, true, true);
                        }
                    }
                    dc.DrawGeometry(null, pen, g);
                }
                break;
            }
            case "Aurora":
            {
                Color[] cols = [Color.FromRgb(60, 255, 170), Color.FromRgb(60, 200, 255), Color.FromRgb(170, 90, 255)];
                for (int i = 0; i < 3; i++)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(0, 0), true, true);
                        for (double x = 0; x <= w; x += 8)
                            c.LineTo(new Point(x, h * (.35 + .12 * i) + Math.Sin(x / w * 5 + t * (.4 + i * .15) + i * 2) * h * .12), true, false);
                        c.LineTo(new Point(w, 0), true, false);
                    }
                    var br = new LinearGradientBrush(A(cols[i], 0), A(cols[i], .32), 90);
                    dc.DrawGeometry(br, null, g);
                }
                break;
            }
            case "NightSky":
            {
                foreach (var s in _stars)
                {
                    double tw = .35 + .65 * Math.Abs(Math.Sin(t * .9 + s.ph));
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(200 * tw), 255, 255, 255)), null, new Point(s.x * w, s.y * h), s.s * .6, s.s * .6);
                }
                if (_meteor < 0 && _rng.NextDouble() < .006) { _meteor = 0; _mx = _rng.NextDouble() * w * .7 + w * .2; _my = _rng.NextDouble() * h * .3; }
                if (_meteor >= 0)
                {
                    _meteor += .025;
                    var head = new Point(_mx - _meteor * w * .35, _my + _meteor * h * .5);
                    var tail = new Point(head.X + 60, head.Y - 34);
                    var br = new LinearGradientBrush(Color.FromArgb(230, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 1), new Point(1, 0));
                    dc.DrawLine(new Pen(br, 1.5), head, tail);
                    if (_meteor > 1) _meteor = -1;
                }
                break;
            }
            case "MidnightLiquid":
            {
                var dark = Pal.Mix(a, Colors.Black, .55);
                for (int i = 0; i < 4; i++)
                {
                    var p = new Point(w * (.5 + .4 * Math.Sin(t * (.13 + i * .05) + i * 1.7)), h * (.5 + .45 * Math.Cos(t * (.11 + i * .04) + i)));
                    double r = Math.Max(w, h) * (.35 + .1 * i) * (1 + level * .15);
                    dc.DrawEllipse(Radial(i % 2 == 0 ? dark : Pal.Mix(b, a, .4), .55), null, p, r, r * .7);
                }
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1.2);
                for (int k = 0; k < 4; k++)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(0, h * (.2 + k * .2)), false, false);
                        for (double x = 0; x <= w; x += 10) c.LineTo(new Point(x, h * (.2 + k * .2) + Math.Sin(x / w * 4 + t * .5 + k) * h * .08), true, true);
                    }
                    dc.DrawGeometry(null, pen, g);
                }
                break;
            }
            case "LiquidTitanium":
            {
                var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                for (int i = 0; i <= 8; i++)
                {
                    double v = .35 + .3 * Math.Sin(i * 1.3 + t * .6);
                    lg.GradientStops.Add(new GradientStop(Color.FromArgb(120, (byte)(160 * v + 40), (byte)(170 * v + 45), (byte)(185 * v + 55)), i / 8.0));
                }
                dc.DrawRectangle(lg, null, new Rect(0, 0, w, h));
                for (int k = 0; k < 6; k++)
                {
                    var p = new Point(w * (.5 + .45 * Math.Sin(t * .4 + k * 1.9)), h * (.5 + .4 * Math.Sin(t * .55 + k * 2.3)));
                    dc.DrawEllipse(Radial(Colors.White, .14 + level * .1), null, p, w * .18, h * .3);
                }
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 235, 240, 255)), 1);
                for (int k = 0; k < 5; k++)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(0, h * (k + .5) / 5), false, false);
                        for (double x = 0; x <= w; x += 8) c.LineTo(new Point(x, h * (k + .5) / 5 + Math.Sin(x / w * 9 + t * 1.1 + k) * 5 * (1 + level)), true, true);
                    }
                    dc.DrawGeometry(null, pen, g);
                }
                break;
            }
            case "Supernova":
            {
                var cpt = new Point(w * .5, h * .55);
                double r = Math.Min(w, h) * .26;
                dc.DrawEllipse(Radial(Color.FromRgb(40, 70, 160), .5), null, cpt, w * .6, h * .8);
                dc.PushTransform(new RotateTransform(-8, cpt.X, cpt.Y));
                for (int i = 0; i < 6; i++)
                {
                    double rx = r * (1.6 + i * .28), ry = r * (.32 + i * .05);
                    var c = Pal.Mix(Color.FromRgb(255, 170, 60), Color.FromRgb(90, 140, 255), i / 5.0);
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(c, .5 - i * .06 + level * .2)), 2.2), cpt, rx, ry);
                }
                double ang = t * 1.4;
                dc.DrawEllipse(Radial(Color.FromRgb(255, 220, 160), .9), null, new Point(cpt.X + Math.Cos(ang) * r * 1.9, cpt.Y + Math.Sin(ang) * r * .4), 14, 6);
                dc.Pop();
                dc.DrawEllipse(Radial(Color.FromRgb(255, 160, 70), .6), null, cpt, r * 1.25, r * 1.25);
                dc.DrawEllipse(Brushes.Black, new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 190, 110)), 1.6), cpt, r, r);
                break;
            }
            case "CyberMatrix":
            {
                var cpt = new Point(w * .5, h * .52);
                var teal = Color.FromRgb(60, 230, 220);
                for (int i = 0; i < 7; i++)
                {
                    double p = (i + t * .35 % 1) / 7;
                    double sx = w * .08 + p * w * .55, sy = h * .08 + p * h * .55;
                    dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(A(teal, .45 * (1 - p) + .05)), 1), new Rect(cpt.X - sx, cpt.Y - sy, sx * 2, sy * 2), 6, 6);
                }
                foreach (var corner in new[] { new Point(0, 0), new Point(w, 0), new Point(0, h), new Point(w, h) })
                    dc.DrawLine(new Pen(new SolidColorBrush(A(teal, .18)), 1), cpt, corner);
                for (int ring = 0; ring < 2; ring++)
                {
                    double rr = h * (.16 - ring * .07) * (1 + level * .2), rot = t * (ring == 0 ? .6 : -.9);
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                        for (int j = 0; j <= 6; j++)
                        {
                            var p = new Point(cpt.X + Math.Cos(rot + j * Math.PI / 3) * rr, cpt.Y + Math.Sin(rot + j * Math.PI / 3) * rr);
                            if (j == 0) c.BeginFigure(p, true, true); else c.LineTo(p, true, true);
                        }
                    dc.DrawGeometry(new SolidColorBrush(A(teal, .08)), new Pen(new SolidColorBrush(A(teal, .8)), 1.4), g);
                }
                dc.DrawEllipse(Radial(teal, .7), null, cpt, 10 + level * 8, 10 + level * 8);
                break;
            }
            case "Orrery":
            {
                var cpt = new Point(w * .5, h * .52);
                double r = Math.Min(w, h) * .2;
                var gold = Color.FromRgb(230, 180, 90);
                for (int i = 0; i < 3; i++)
                {
                    dc.PushTransform(new RotateTransform(-18 + i * 14, cpt.X, cpt.Y));
                    double rx = r * (1.8 + i * .55), ry = r * (.45 + i * .12);
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(A(gold, .45)), 1), cpt, rx, ry);
                    double an = t * (.5 - i * .12) + i * 2;
                    dc.DrawEllipse(new SolidColorBrush(A(Color.FromRgb(255, 235, 200), .95)), null, new Point(cpt.X + Math.Cos(an) * rx, cpt.Y + Math.Sin(an) * ry), 3 + i, 3 + i);
                    dc.Pop();
                }
                var sphere = new RadialGradientBrush(Color.FromRgb(240, 190, 120), Color.FromRgb(70, 40, 30)) { GradientOrigin = new Point(.35, .3), Center = new Point(.4, .38) };
                dc.DrawEllipse(Radial(gold, .35 + level * .3), null, cpt, r * 1.6, r * 1.6);
                dc.DrawEllipse(sphere, null, cpt, r, r);
                break;
            }
            case "Prism":
            {
                var cpt = new Point(w * .5, h * .5);
                double s = Math.Min(w, h) * .3 * (1 + level * .1);
                for (int i = 0; i < 7; i++)
                {
                    var col = Pal.FromHsv(i * 45, .7, 1);
                    double ang = -.35 + i * .1;
                    dc.DrawLine(new Pen(new SolidColorBrush(A(col, .35)), 2.2), cpt, new Point(cpt.X + Math.Cos(ang) * w, cpt.Y + Math.Sin(ang) * w));
                }
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 2), new Point(0, cpt.Y + s * .4), cpt);
                double ry = t * .7, rx = .5 + .2 * Math.Sin(t * .3);
                (double x, double y, double z)[] v = [(1, 0, 0), (-1, 0, 0), (0, 1.4, 0), (0, -1.4, 0), (0, 0, 1), (0, 0, -1)];
                Point P((double x, double y, double z) q)
                {
                    double x1 = q.x * Math.Cos(ry) + q.z * Math.Sin(ry), z1 = -q.x * Math.Sin(ry) + q.z * Math.Cos(ry);
                    double y1 = q.y * Math.Cos(rx) - z1 * Math.Sin(rx);
                    return new Point(cpt.X + x1 * s * .7, cpt.Y + y1 * s * .7);
                }
                int[][] faces = [[0, 2, 4], [4, 2, 1], [1, 2, 5], [5, 2, 0], [0, 3, 4], [4, 3, 1], [1, 3, 5], [5, 3, 0]];
                for (int i = 0; i < faces.Length; i++)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open()) { c.BeginFigure(P(v[faces[i][0]]), true, true); c.LineTo(P(v[faces[i][1]]), true, true); c.LineTo(P(v[faces[i][2]]), true, true); }
                    dc.DrawGeometry(new SolidColorBrush(A(Pal.FromHsv(200 + i * 20 + t * 20, .35, 1), .12)), new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), .8), g);
                }
                break;
            }
        }
        dc.Pop();
    }
}
