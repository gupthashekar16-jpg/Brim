using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using Brim.Core;
using Brim.Notch;
using Brim.Notch.Views;
using Brim.Services;

namespace Brim.Overlays;

/// <summary>
/// desktop panel: a detachable, resizable panel that can stay above other windows,
/// with music and lyrics, timers, network, hardware, agent work and plan usage.
/// </summary>
public sealed class DesktopPanelWindow : Window
{
    static AppSettings S => App.S;
    static readonly (string key, string glyph)[] Tabs =
        [("Music", Ic.Music), ("Timer", Ic.Stopwatch), ("Network", Ic.Wifi), ("System", Ic.Chip), ("Work", Ic.Robot), ("Usage", Ic.Chart)];

    readonly Image _bg = new() { Stretch = Stretch.UniformToFill, Opacity = 0.5, Effect = new BlurEffect { Radius = 60, RenderingBias = RenderingBias.Performance } };
    readonly Border _host = new() { Margin = new Thickness(18, 6, 18, 8) };
    readonly UniformGrid _tabBar = new() { Rows = 1 };
    readonly TextBlock _pinGlyph = Ui.Icon(Ic.Pin, 13);
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Stopwatch _sw = Stopwatch.StartNew();
    double _lastFrame;
    string _tab;
    Action? _frame, _second;

    // network history (bytes/s)
    readonly List<double> _down = [], _up = [];

    public DesktopPanelWindow()
    {
        Title = "Sidekick";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = true;
        MinWidth = 340; MinHeight = 420;
        Width = Math.Max(MinWidth, S.ScreenWidth);
        Height = Math.Max(MinHeight, S.ScreenHeight);
        if (double.IsFinite(S.ScreenLeft) && double.IsFinite(S.ScreenTop))
        {
            Left = S.ScreenLeft; Top = S.ScreenTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = S.ScreenPinned;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(8), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0) });
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        _tab = Tabs.Any(t => t.key == S.ScreenTab) ? S.ScreenTab : "Music";

        var face = new SidekickFace(26, Pal.Orange);
        var pin = Ui.Btn(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Pal.Hex("#1AFFFFFF"), Child = _pinGlyph }, TogglePin, "Keep above other windows");
        var close = Ui.Circle(Ic.Close, Close, 30, 11, Pal.Hex("#1AFFFFFF"), tip: "Close");
        var header = Ui.G("Auto,*,Auto", "", Ui.H(9, face, Ui.T("sidekick", 16, FontWeights.Bold)).VA(VerticalAlignment.Center), Ui.H(6, pin, close).At(2));
        header.Margin = new Thickness(16, 14, 14, 6);
        header.Background = Pal.HitClear;
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        _tabBar.Margin = new Thickness(8, 0, 8, 8);
        foreach (var (key, glyph) in Tabs) _tabBar.Children.Add(TabButton(key, glyph));

        var layout = Ui.G("*", "Auto,*,Auto", header, _host.At(0, 1), _tabBar.At(0, 2));
        var surface = new Grid { ClipToBounds = true };
        surface.Children.Add(new Border { Background = Pal.Hex("#F0121216") });
        surface.Children.Add(_bg);
        surface.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(60, 0, 0, 0), Color.FromArgb(200, 8, 8, 10), 90) });
        surface.Children.Add(layout);
        var card = new Border { CornerRadius = new CornerRadius(24), BorderBrush = Pal.Hex("#26FFFFFF"), BorderThickness = new Thickness(1), Child = surface, Margin = new Thickness(6) };
        card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 };
        SizeChanged += (_, _) => surface.Clip = new RectangleGeometry(new Rect(0, 0, card.ActualWidth - 2, card.ActualHeight - 2), 23, 23);
        Content = card;

        PaintPin();
        Show(_tab);
        _tick.Tick += (_, _) => { SampleNetwork(); _second?.Invoke(); };
        _tick.Start();
        CompositionTarget.Rendering += OnRender;
        App.Media.Changed += OnMedia;
        App.Agents.Changed += OnAgents;
        OnMedia();
        Closed += (_, _) =>
        {
            _tick.Stop();
            CompositionTarget.Rendering -= OnRender;
            App.Media.Changed -= OnMedia;
            App.Agents.Changed -= OnAgents;
            S.ScreenLeft = Left; S.ScreenTop = Top; S.ScreenWidth = ActualWidth; S.ScreenHeight = ActualHeight;
            S.Changed("Screen");
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    void OnMedia() { _bg.Source = App.Media.Art; if (_tab == "Music") Show("Music"); }
    void OnAgents() { if (_tab is "Work") Show(_tab); }

    void TogglePin() { S.ScreenPinned = !S.ScreenPinned; S.Changed("ScreenPinned"); Topmost = S.ScreenPinned; PaintPin(); }
    void PaintPin() => _pinGlyph.Foreground = S.ScreenPinned ? (Brush)FindResource("Accent") : Pal.Sub;

    Border TabButton(string key, string glyph)
    {
        var icon = Ui.Icon(glyph, 15, Pal.Sub);
        var label = Ui.T(key, 10, FontWeights.SemiBold, Pal.Sub).HA(HorizontalAlignment.Center);
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(0, 7, 0, 6), Margin = new Thickness(1), Cursor = Cursors.Hand, Tag = key, Background = Pal.HitClear, Child = Ui.V(3, icon, label) };
        b.MouseLeftButtonUp += (_, _) => Show(key);
        return b;
    }

    void Show(string key)
    {
        _tab = key;
        S.ScreenTab = key;
        foreach (Border b in _tabBar.Children)
        {
            bool on = (string)b.Tag == key;
            b.Background = on ? Pal.Hex("#24FFFFFF") : Pal.HitClear;
            foreach (var t in ((StackPanel)b.Child).Children.OfType<TextBlock>()) t.Foreground = on ? (t.FontSize > 12 ? (Brush)FindResource("Accent") : Pal.Text) : Pal.Sub;
        }
        _frame = null;
        _second = null;
        _host.Child = key switch
        {
            "Timer" => TimerPage(),
            "Network" => NetworkPage(),
            "System" => SystemPage(),
            "Work" => WorkPage(),
            "Usage" => UsagePage(),
            _ => MusicPage(),
        };
        _second?.Invoke();
    }

    void OnRender(object? s, EventArgs e)
    {
        double now = _sw.Elapsed.TotalSeconds;
        if (now - _lastFrame < 1 / 30.0) return;
        _lastFrame = now;
        _frame?.Invoke();
    }

    // ------------------------------------------------------------------ pages
    FrameworkElement MusicPage()
    {
        var m = App.Media;
        var art = new Border { CornerRadius = new CornerRadius(18), ClipToBounds = true, Background = Pal.Track, Child = m.Art != null ? new Image { Source = m.Art, Stretch = Stretch.UniformToFill } : Ui.Icon(Ic.Music, 40, Pal.Sub) };
        art.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.5 };
        var artBox = new Viewbox { Child = new Border { Width = 200, Height = 200, Child = art }, MaxWidth = 210, MaxHeight = 210, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        var title = Ui.T(m.HasSession ? m.Title : "Nothing playing", 17, FontWeights.Bold).HA(HorizontalAlignment.Center);
        var artist = Ui.T(m.HasSession ? (m.Artist.Length > 0 ? m.Artist : m.AppName) : "Play something to see it here", 13, null, Pal.Sub).HA(HorizontalAlignment.Center);
        var lyric = Ui.T("", 15, FontWeights.SemiBold).Wrap();
        lyric.TextAlignment = TextAlignment.Center;
        lyric.MaxHeight = 44;
        var next = Ui.T("", 12, null, Pal.Dim).HA(HorizontalAlignment.Center);
        var meter = new Meter(Pal.White, 4);
        var pos = Ui.T("0:00", 11, null, Pal.Sub);
        var left = Ui.T("-0:00", 11, null, Pal.Sub);
        var playGlyph = Ui.Icon(m.IsPlaying ? Ic.Pause : Ic.Play, 18, Pal.White);
        var play = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Background = Pal.Hex("#FF7A3D"), Child = playGlyph };
        var controls = Ui.H(34, Ui.Btn(Ui.Icon(Ic.Prev, 15), () => App.Media.Prev()), Ui.Btn(play, () => App.Media.Toggle()), Ui.Btn(Ui.Icon(Ic.Next, 15), () => App.Media.Next()));
        foreach (FrameworkElement c in controls.Children) c.VerticalAlignment = VerticalAlignment.Center;
        controls.HorizontalAlignment = HorizontalAlignment.Center;
        List<LyricLine>? lines = null;
        if (m.HasSession) _ = App.Lyrics.GetAsync(m.Title, m.Artist, m.Duration).ContinueWith(t => Dispatcher.BeginInvoke(() => lines = t.Result));
        _frame = () =>
        {
            var p = App.Media.CurrentPosition;
            var d = App.Media.Duration;
            meter.Value = d > TimeSpan.Zero ? p / d : 0;
            pos.Text = Fmt.Clock(p);
            left.Text = "-" + Fmt.Clock(d - p);
            playGlyph.Text = App.Media.IsPlaying ? Ic.Pause : Ic.Play;
            if (lines is { Count: > 0 })
            {
                int i = lines.FindLastIndex(l => l.At <= p + TimeSpan.FromMilliseconds(250));
                lyric.Text = i >= 0 && lines[i].Text.Length > 0 ? lines[i].Text : "•  •  •";
                next.Text = i + 1 < lines.Count ? lines[i + 1].Text : "";
            }
        };
        return Ui.G("*", "*,Auto,Auto,Auto,Auto,Auto,Auto,Auto",
            artBox.VA(VerticalAlignment.Center), title.M(0, 12, 0, 0).At(0, 1), artist.M(0, 2, 0, 0).At(0, 2),
            lyric.M(0, 10, 0, 0).At(0, 3), next.M(0, 2, 0, 0).At(0, 4), meter.M(0, 12, 0, 0).At(0, 5),
            Ui.G("*,*", "", pos, left.HA(HorizontalAlignment.Right).At(1)).M(0, 5, 0, 0).At(0, 6), controls.M(0, 10, 0, 6).At(0, 7));
    }

    FrameworkElement TimerPage()
    {
        var f = App.Focus;
        var mode = Ui.T("", 13, FontWeights.SemiBold, Pal.Sub).HA(HorizontalAlignment.Center);
        var time = Ui.Big("25:00", 54, FontWeights.SemiBold).HA(HorizontalAlignment.Center);
        var ring = new Meter(Pal.Orange, 5);
        var playGlyph = Ui.Icon(Ic.Play, 18, Pal.Ink);
        var buttons = Ui.H(12,
            Ui.Circle(Ic.History, () => f.Reset(), 44, 15, Pal.Hex("#26FFFFFF"), tip: "Reset"),
            Ui.Btn(new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Background = Pal.White, Child = playGlyph }, () => f.Toggle()),
            Ui.Circle(Ic.Next, () => f.SetMode(f.Mode == "Focus" ? "Break" : "Focus"), 44, 13, Pal.Hex("#26FFFFFF"), tip: "Switch focus / break"));
        foreach (FrameworkElement c in buttons.Children) c.VerticalAlignment = VerticalAlignment.Center;
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        var task = Ui.T("", 13, null, Pal.Sub).Wrap();
        task.TextAlignment = TextAlignment.Center;
        var sw = Ui.Big("00:00.0", 26, FontWeights.SemiBold).HA(HorizontalAlignment.Center);
        var swBtn = Ui.T("Start", 12, FontWeights.SemiBold);
        var swRow = Ui.H(8, Ui.Pill("Lap", null, () => f.SwLap(), false, 30, 12), Ui.Btn(Ui.Card(swBtn, 15, new Thickness(16, 6, 16, 6)), () => f.SwToggle()), Ui.Pill("Reset", null, () => f.SwReset(), false, 30, 12));
        swRow.HorizontalAlignment = HorizontalAlignment.Center;
        void Update()
        {
            mode.Text = f.Mode == "Focus" ? (f.Running ? "Focusing" : "Focus") : (f.Running ? "On a break" : "Break");
            time.Text = Fmt.Clock(f.Remaining);
            ring.Value = f.Total > TimeSpan.Zero ? 1 - f.Remaining / f.Total : 0;
            ring.Fill = f.Mode == "Focus" ? Pal.Orange : Pal.Green;
            playGlyph.Text = f.Running ? Ic.Pause : Ic.Play;
            task.Text = f.NextTask.Length > 0 ? "Next: " + f.NextTask : "";
            var e = f.SwElapsed;
            sw.Text = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss\.f");
            swBtn.Text = f.SwRunning ? "Stop" : "Start";
        }
        _frame = Update;
        var stopwatch = Ui.Card(Ui.V(8, Ui.T("Stopwatch", 12, FontWeights.SemiBold, Pal.Sub).HA(HorizontalAlignment.Center), sw, swRow), 16, new Thickness(12));
        return Ui.G("*", "*,Auto,Auto,Auto,Auto,Auto,*,Auto", mode.At(0, 1), time.At(0, 2), ring.M(30, 8, 30, 0).At(0, 3), buttons.M(0, 16, 0, 0).At(0, 4), task.M(0, 10, 0, 0).At(0, 5), stopwatch.M(0, 0, 0, 4).At(0, 7));
    }

    void SampleNetwork()
    {
        _down.Add(App.Sys.Down); _up.Add(App.Sys.Up);
        if (_down.Count > 60) { _down.RemoveAt(0); _up.RemoveAt(0); }
    }

    FrameworkElement NetworkPage()
    {
        var downV = Ui.Big("0 B/s", 24, FontWeights.SemiBold);
        var upV = Ui.Big("0 B/s", 24, FontWeights.SemiBold);
        var downS = new Sparkline(Color.FromRgb(76, 217, 100)) { Height = 70, Capacity = 60 };
        var upS = new Sparkline(Color.FromRgb(90, 200, 250)) { Height = 70, Capacity = 60 };
        var peak = Ui.T("", 11, null, Pal.Dim);
        Border Block(string label, string glyph, Brush c, TextBlock v, Sparkline s) =>
            Ui.Card(Ui.V(6, Ui.H(6, Ui.Icon(glyph, 12, c), Ui.T(label.ToUpperInvariant(), 11, FontWeights.SemiBold, Pal.Sub)), v, s), 16, new Thickness(14, 12, 14, 12));
        _second = () =>
        {
            downV.Text = Fmt.Speed(App.Sys.Down);
            upV.Text = Fmt.Speed(App.Sys.Up);
            double max = Math.Max(1024, Math.Max(_down.DefaultIfEmpty(0).Max(), _up.DefaultIfEmpty(0).Max()));
            downS.SetAll(_down.Select(v => v / max));
            upS.SetAll(_up.Select(v => v / max));
            peak.Text = $"Scale: {Fmt.Speed(max)} · last minute";
        };
        return Ui.V(10, Block("Download", Ic.Down, Pal.Green, downV, downS), Block("Upload", Ic.Upload, Pal.Blue, upV, upS), peak.HA(HorizontalAlignment.Center));
    }

    FrameworkElement SystemPage()
    {
        var rows = new StackPanel();
        var items = new List<(TextBlock val, Meter? meter, TextBlock sub)>();
        (TextBlock, Meter?, TextBlock) Row(string glyph, Brush c, string label, bool meter)
        {
            var val = Ui.T("", 14, FontWeights.SemiBold);
            var sub = Ui.T("", 11, null, Pal.Dim);
            Meter? m = meter ? new Meter(c, 5) : null;
            var g = Ui.G("Auto,*,Auto", "Auto,Auto,Auto", Ui.Icon(glyph, 13, c), Ui.T(label, 13, FontWeights.SemiBold, Pal.Sub).M(8, 0, 0, 0).At(1), val.At(2),
                m == null ? null : m.M(0, 8, 0, 0).At(0, 1).Span(3), sub.M(0, 4, 0, 0).At(0, 2).Span(3));
            var card = Ui.Card(g, 14, new Thickness(14, 10, 14, 10));
            card.Margin = new Thickness(0, 0, 0, 8);
            rows.Children.Add(card);
            return (val, m, sub);
        }
        var cpu = Row(Ic.Chip, Pal.Blue, "CPU", true);
        var mem = Row(Ic.Ram, Pal.Purple, "Memory", true);
        var disk = Row(Ic.Drive, Pal.Orange, "Storage", true);
        var bat = Row(Ic.Bolt, Pal.Green, "Battery", true);
        var up = Row(Ic.Clock, Pal.Yellow, "Uptime", false);
        _second = () =>
        {
            var s = App.Sys;
            cpu.Item1.Text = $"{Math.Round(s.Cpu * 100)}%"; cpu.Item2!.Value = s.Cpu; cpu.Item3.Text = "";
            double mu = s.MemTotal > 0 ? (double)s.MemUsed / s.MemTotal : 0;
            mem.Item1.Text = $"{Math.Round(mu * 100)}%"; mem.Item2!.Value = mu; mem.Item3.Text = $"{Fmt.Bytes(s.MemUsed)} of {Fmt.Bytes(s.MemTotal)}";
            double du = s.DiskTotal > 0 ? 1 - (double)s.DiskFree / s.DiskTotal : 0;
            disk.Item1.Text = Fmt.Bytes(s.DiskFree) + " free"; disk.Item2!.Value = du; disk.Item3.Text = "of " + Fmt.Bytes(s.DiskTotal);
            bat.Item1.Text = s.HasBattery ? $"{s.Battery}%" : "AC"; bat.Item2!.Value = s.HasBattery ? s.Battery / 100.0 : 1; bat.Item3.Text = !s.HasBattery ? "No battery" : s.Charging ? "Charging" : s.Plugged ? "Plugged in" : "On battery";
            up.Item1.Text = Fmt.Uptime(s.Uptime); up.Item3.Text = "since last restart";
        };
        return new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    FrameworkElement WorkPage()
    {
        var sp = new StackPanel();
        foreach (var a in App.Agents.Pending) sp.Children.Add(AgentUi.ApprovalCard(a, false));
        if (App.Agents.Sessions.Count == 0 && App.Agents.Pending.Count == 0)
        {
            var hint = Ui.T("When Claude Code, Codex, Cursor or Antigravity is working, it shows up here.", 12, null, Pal.Sub).Wrap();
            hint.TextAlignment = TextAlignment.Center;
            sp.Children.Add(Ui.V(10, new SidekickFace(52, Pal.Orange).HA(HorizontalAlignment.Center), Ui.T("No agent activity yet", 14, FontWeights.SemiBold).HA(HorizontalAlignment.Center), hint).M(10, 40, 10, 0));
        }
        foreach (var s in App.Agents.Sessions) sp.Children.Add(AgentUi.Session(s, true));
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    FrameworkElement UsagePage()
    {
        var panel = new UsagePanel(true);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    /// <summary>For snapshots: render a given tab.</summary>
    public void ShowTab(string key) => Show(key);
}
