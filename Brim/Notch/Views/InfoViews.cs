using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brim.Core;
using Brim.Services;

namespace Brim.Notch.Views;

/// <summary>Current conditions plus hourly / 7-day forecast.</summary>
public sealed class WeatherView : NotchView
{
    readonly TextBlock _city = Ui.T("Locating…", 13, FontWeights.SemiBold, Pal.Sub);
    readonly TextBlock _temp = Ui.Big("--°", 46, FontWeights.SemiBold);
    readonly Border _iconHost = new() { Margin = new Thickness(10, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _desc = Ui.T("", 15, FontWeights.SemiBold);
    readonly TextBlock _hl = Ui.T("", 12, null, Pal.Sub);
    readonly TextBlock _hum = Ui.T("", 12, FontWeights.SemiBold, Pal.Sub), _wind = Ui.T("", 12, FontWeights.SemiBold, Pal.Sub), _rain = Ui.T("", 12, FontWeights.SemiBold, Pal.Sub), _sunset = Ui.T("", 12, FontWeights.SemiBold, Pal.Sub);
    readonly StackPanel _cols = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _rightTitle = Ui.T("Next 24 hours", 14, FontWeights.SemiBold);
    readonly Border _segHost = new();
    string _mode = "Hourly";

    public WeatherView(NotchWindow n) : base(n)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        ColumnDefinitions.Add(new ColumnDefinition());
        Height = 246;

        var tempRow = Ui.H(0, _temp, _iconHost);
        var details = Ui.V(10,
            Ui.H(16, Ui.H(5, Mini.Drop(Pal.Sub), _hum), Ui.H(5, Mini.Wind(Pal.Sub), _wind), Ui.H(5, Mini.Umbrella(Pal.Sub), _rain)),
            Ui.H(5, Mini.Sunset(Pal.Sub), _sunset));
        var footer = Ui.G("*,Auto", "", Ui.T("Forecast: Open-Meteo", 11, null, Pal.Dim), Ui.Circle(Ic.Refresh, () => _ = App.Weather.RefreshAsync(), 24, 10, fg: Pal.Dim, tip: "Refresh").At(1));
        var left = Ui.G("*", "Auto,Auto,Auto,Auto,Auto,*,Auto",
            Ui.H(6, Ui.Icon(Ic.Target, 11, Pal.Sub), _city),
            tempRow.M(0, 2, 0, 0).At(0, 1), _desc.At(0, 2), _hl.M(0, 4, 0, 0).At(0, 3), details.M(0, 14, 0, 0).At(0, 4), footer.At(0, 6));
        left.Margin = new Thickness(0, 0, 18, 0);
        Children.Add(left);
        Children.Add(new Border { Background = Pal.Line }.At(1));

        var head = Ui.G("*,Auto", "", _rightTitle, _segHost.At(1));
        var right = Ui.G("*", "Auto,*", head, Scroll(_cols, 0, true).M(0, 12, 0, 0).At(0, 1));
        right.Margin = new Thickness(18, 0, 0, 0);
        Children.Add(right.At(2));
        BuildSeg();
        App.Weather.Changed += () => { if (IsVisible) Refresh(); };
    }

    void BuildSeg() => _segHost.Child = Ui.Segmented(["Hourly", "7 days"], _mode, m => { _mode = m; BuildSeg(); Refresh(); }, true, null, 30, 13);

    public override void OnShow()
    {
        if (!App.Weather.Ready || DateTime.Now - App.Weather.UpdatedAt > TimeSpan.FromMinutes(20)) _ = App.Weather.RefreshAsync();
        Refresh();
    }

    public override void Refresh()
    {
        var w = App.Weather;
        if (!w.Ready) { _city.Text = "Locating…"; return; }
        string D(double v) => $"{Math.Round(v)}°";
        _city.Text = w.City.Length > 0 ? w.City : "Your location";
        _temp.Text = D(w.Temp);
        _iconHost.Child = new WeatherIcon(w.Code, w.IsDay, 30);
        _desc.Text = WeatherIcon.Describe(w.Code);
        _hl.Text = $"H {D(w.High)} · L {D(w.Low)} · Feels {D(w.Feels)}";
        _hum.Text = $"{w.Humidity}%";
        _wind.Text = $"{Math.Round(w.Wind)} {w.WindUnit}";
        _rain.Text = $"{w.RainChance}%";
        _sunset.Text = DateTime.Now < w.Sunset ? w.Sunset.ToString(S.Clock24h ? "HH:mm" : "h:mm tt") : w.Sunrise.AddDays(1).ToString(S.Clock24h ? "HH:mm" : "h:mm tt");
        _rightTitle.Text = _mode == "Hourly" ? "Next 24 hours" : "Next 7 days";
        _cols.Children.Clear();
        if (_mode == "Hourly")
        {
            bool first = true;
            foreach (var h in w.Hours)
            {
                _cols.Children.Add(Column(first ? "Now" : h.Time.ToString(S.Clock24h ? "HH:mm" : "h tt"), h.Code, h.Day, D(h.Temp), h.Rain));
                first = false;
            }
        }
        else
        {
            bool first = true;
            foreach (var d in w.Days)
            {
                _cols.Children.Add(Column(first ? "Today" : d.Date.ToString("ddd"), d.Code, true, D(d.Max), d.Rain, D(d.Min)));
                first = false;
            }
        }
    }

    static Border Column(string label, int code, bool day, string temp, int rain, string? low = null)
    {
        var icon = new WeatherIcon(code, day, 20) { HorizontalAlignment = HorizontalAlignment.Center };
        var stack = Ui.V(0,
            Ui.T(label, 12, FontWeights.SemiBold, Pal.Sub).HA(HorizontalAlignment.Center),
            icon.M(0, 14, 0, 12),
            Ui.T(temp, 15, FontWeights.SemiBold).HA(HorizontalAlignment.Center),
            low == null ? null : Ui.T(low, 12, null, Pal.Sub).HA(HorizontalAlignment.Center),
            Ui.T($"{rain}%", 12, FontWeights.SemiBold, Pal.Blue).HA(HorizontalAlignment.Center).M(0, low == null ? 16 : 6, 0, 0));
        var c = Ui.Card(stack, 14, new Thickness(4, 12, 4, 12));
        c.Width = 56;
        c.Margin = new Thickness(0, 0, 6, 6);
        return c;
    }
}

/// <summary>Sidekick's little face (original drawing).</summary>
public sealed class SidekickFace : FrameworkElement
{
    readonly Brush _color;
    public bool Blink { get; set; }

    public SidekickFace(double size, Brush color)
    {
        Width = Height = size;
        _color = color;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = ActualWidth;
        dc.DrawRoundedRectangle(_color, null, new Rect(0, s * .06, s, s * .88), s * .32, s * .32);
        var eye = Pal.Ink;
        double ey = s * .42, eh = Blink ? s * .03 : s * .16;
        dc.DrawRoundedRectangle(eye, null, new Rect(s * .28, ey - eh / 2, s * .1, eh), s * .05, s * .05);
        dc.DrawRoundedRectangle(eye, null, new Rect(s * .62, ey - eh / 2, s * .1, eh), s * .05, s * .05);
        var smile = new StreamGeometry();
        using (var c = smile.Open())
        {
            c.BeginFigure(new Point(s * .38, s * .64), false, false);
            c.QuadraticBezierTo(new Point(s * .5, s * .74), new Point(s * .62, s * .64), true, true);
        }
        dc.DrawGeometry(null, new Pen(eye, Math.Max(1.2, s * .05)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, smile);
    }
}
