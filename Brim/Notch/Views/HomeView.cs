using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brim.Core;

namespace Brim.Notch.Views;

/// <summary>Home: now playing card, focus, CPU/RAM, network, privacy, shortcuts and quick launch.</summary>
public sealed class HomeView : NotchView
{
    readonly Image _bgArt = new() { Stretch = Stretch.UniformToFill, Opacity = 0.55, Effect = new BlurEffect { Radius = 46 } };
    readonly Border _art = new() { Width = 54, Height = 54, CornerRadius = new CornerRadius(10), ClipToBounds = true };
    readonly TextBlock _title = Ui.T("Not playing", 15, FontWeights.SemiBold);
    readonly TextBlock _sub = Ui.T("Play something to see it here", 13, null, Pal.Sub);
    readonly Meter _progress = new(Pal.White, 4);
    readonly TextBlock _pos = Ui.T("0:00", 11, null, Pal.Sub), _dur = Ui.T("0:00", 11, null, Pal.Sub);
    readonly TextBlock _playGlyph = Ui.Icon(Ic.Play, 16, Pal.Ink);
    readonly TextBlock _focusValue = Ui.T("25:00", 13, FontWeights.SemiBold, Pal.Sub);
    readonly TextBlock _focusLabel = Ui.T("Focus timer", 13, FontWeights.SemiBold);
    readonly Meter _cpu = new(Pal.White, 5), _ram = new(Pal.White, 5);
    readonly TextBlock _cpuT = Ui.T("0%", 12, FontWeights.SemiBold), _ramT = Ui.T("0%", 12, FontWeights.SemiBold);
    readonly TextBlock _down = Ui.T("0 B/s", 12, FontWeights.SemiBold), _up = Ui.T("0 B/s", 12, FontWeights.SemiBold);
    readonly TextBlock _inUse = Ui.T("Nothing", 13, FontWeights.SemiBold, Pal.Sub);
    readonly Border _inUseDot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Pal.Orange, Margin = new Thickness(0, 0, 7, 0) };
    TextBlock _recGlyph = Ui.Icon("", 15, Pal.Red);
    readonly StackPanel _quickLaunch = new();
    readonly UniformGrid _shortcuts = new() { Rows = 1 };

    public HomeView(NotchWindow n) : base(n)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(262) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Children.Add(MediaCard());
        Children.Add(RightColumn().At(2));
        Children.Add(_quickLaunch.At(0, 1).Span(3));
        BuildQuickLaunch();
        App.Media.Changed += () => { if (IsVisible) RefreshMedia(); };
    }

    Border MediaCard()
    {
        var bg = new Grid { ClipToBounds = true };
        bg.Children.Add(_bgArt);
        bg.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(40, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), 90) });

        _art.Background = Pal.Track;
        var badge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = Pal.Hex("#CC1C1C1E"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -5, -5), Child = Ui.Icon(Ic.Music, 11),
        };
        var artWrap = new Grid { Width = 54, Height = 54 };
        artWrap.Children.Add(_art);
        artWrap.Children.Add(badge);

        var head = Ui.G("Auto,*", "", artWrap, Ui.V(3, _title, _sub).M(14, 0, 0, 0).VA(VerticalAlignment.Center).At(1));
        var times = Ui.G("*,*", "", _pos, _dur.HA(HorizontalAlignment.Right).At(1));
        var play = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = Pal.White, Child = _playGlyph };
        var controls = Ui.H(34,
            Ui.Btn(Ui.Icon(Ic.Prev, 13), () => App.Media.Prev()),
            Ui.Btn(play, () => App.Media.Toggle()),
            Ui.Btn(Ui.Icon(Ic.Next, 13), () => App.Media.Next()));
        controls.HorizontalAlignment = HorizontalAlignment.Center;
        controls.VerticalAlignment = VerticalAlignment.Center;
        foreach (UIElement c in controls.Children) if (c is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;

        var body = Ui.G("*", "Auto,*,Auto,Auto,*,Auto", head,
            _progress.At(0, 2), times.M(0, 6, 0, 0).At(0, 3), controls.At(0, 5));
        body.Margin = new Thickness(18, 18, 18, 18);
        bg.Children.Add(body);
        var card = Ui.Card(bg, 16, new Thickness(0));
        card.ClipToBounds = true;
        bg.Clip = null;
        card.Loaded += (_, _) => bg.Clip = new RectangleGeometry(new Rect(0, 0, card.ActualWidth, card.ActualHeight), 16, 16);
        card.SizeChanged += (_, _) => bg.Clip = new RectangleGeometry(new Rect(0, 0, card.ActualWidth, card.ActualHeight), 16, 16);
        _progress.Cursor = System.Windows.Input.Cursors.Hand;
        _progress.Background = Pal.HitClear;
        _progress.MouseLeftButtonUp += (_, e) =>
        {
            if (App.Media.Duration <= TimeSpan.Zero) return;
            App.Media.Seek(App.Media.Duration * (e.GetPosition(_progress).X / _progress.ActualWidth));
        };
        return card;
    }

    Grid RightColumn()
    {
        var focus = Row(Ui.H(10, Ui.Icon(Ic.Cup, 15), _focusLabel), Ui.H(6, _focusValue, Ui.Icon(Ic.ChevRight, 10, Pal.Sub)));
        focus.OnClick(() => N.Select("Focus"));

        var cpuRam = Ui.Card(Ui.V(10,
            Ui.G("44,*,48", "", Ui.T("CPU", 13, null, Pal.Sub), _cpu.At(1), _cpuT.HA(HorizontalAlignment.Right).At(2)),
            Ui.G("44,*,48", "", Ui.T("RAM", 13, null, Pal.Sub), _ram.At(1), _ramT.HA(HorizontalAlignment.Right).At(2))), 14, new Thickness(16, 12, 16, 12));
        cpuRam.OnClick(() => N.Select("Stats"));

        var net = Row(Ui.T("Network", 13, FontWeights.SemiBold),
            Ui.H(14, Ui.H(5, Ui.Icon(Ic.Down, 10, Pal.Sub), _down), Ui.H(5, Ui.Icon(Ic.Upload, 10, Pal.Sub), _up)));
        var inUse = Row(Ui.T("In use", 13, FontWeights.SemiBold), Ui.H(0, _inUseDot, _inUse));

        BuildShortcuts();
        return Ui.G("*", "Auto,*,Auto,*,Auto,*,Auto,*,Auto", focus, cpuRam.At(0, 2), net.At(0, 4), inUse.At(0, 6), _shortcuts.At(0, 8));
    }

    static Border Row(UIElement left, UIElement right)
    {
        var g = Ui.G("*,Auto", "", left, right.At(1));
        if (right is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        var c = Ui.Card(g, 14, new Thickness(16, 0, 16, 0));
        c.Height = 40;
        return c;
    }

    void BuildShortcuts()
    {
        _shortcuts.Children.Clear();
        if (!S.W("Shortcuts")) { _shortcuts.Visibility = Visibility.Collapsed; return; }
        _shortcuts.Visibility = Visibility.Visible;
        Border Tile(UIElement icon, Action a, string tip)
        {
            var b = Ui.Card(icon, 14, new Thickness(0));
            b.Height = 40;
            b.Margin = new Thickness(3, 0, 3, 0);
            b.Hoverable(null, Pal.Hover);
            b.ToolTip = tip;
            b.OnClick(a);
            return b;
        }
        _shortcuts.Children.Add(Tile(Ui.Icon(Ic.Calc, 15), () => Shell.Open("calc.exe"), "Calculator"));
        _shortcuts.Children.Add(Tile(Ui.Icon(Ic.Camera, 15), () => N.Screenshot(false), "Screenshot"));
        // a fresh glyph each rebuild: the old one still belongs to the previous tile
        _recGlyph = Ui.Icon(App.Capture.Recording ? "" : "", 15, Pal.Red);
        _shortcuts.Children.Add(Tile(_recGlyph, () => N.ToggleRecording(null), "Record screen"));
        _shortcuts.Children.Add(Tile(Ui.Icon(Ic.Pulse, 15), () => Shell.Open("taskmgr.exe"), "Task Manager"));
        if (S.W("EdgeLight"))
        {
            var bulb = Tile(Ui.Icon(Ic.Bulb, 15), () => N.ToggleEdgeLight(), "Ring Light (right-click to adjust)");
            bulb.MouseRightButtonUp += (_, e) => { N.EdgeLightPopup(bulb); e.Handled = true; };
            _shortcuts.Children.Add(bulb);
        }
        _shortcuts.Margin = new Thickness(-3, 0, -3, 0);
    }

    void BuildQuickLaunch()
    {
        _quickLaunch.Children.Clear();
        if (!S.W("QuickApps")) { _quickLaunch.Visibility = Visibility.Collapsed; return; }
        _quickLaunch.Visibility = Visibility.Visible;
        _quickLaunch.Margin = new Thickness(0, 12, 0, 0);
        var head = Ui.G("*,Auto", "",
            Ui.H(6, Ui.T("Quick Launch", 12, FontWeights.SemiBold, Pal.Sub), Ui.T("· your shortcuts", 12, null, Pal.Dim)),
            Ui.Btn(Ui.H(5, Ui.Icon(Ic.List, 11, Pal.Sub), Ui.T("Customize", 12, null, Pal.Sub)), () => App.OpenSettings("Widgets")).At(1));
        var grid = new UniformGrid { Rows = 1, Margin = new Thickness(-3, 8, -3, 0) };
        foreach (var qa in S.QuickApps.Take(5))
        {
            var target = qa.Target;
            ImageSource? icon = null;
            var resolved = Shell.Resolve(target);
            if (resolved != null) icon = Shell.IconFor(resolved);
            else if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Shell.DefaultBrowserExe() is string br) icon = Shell.IconFor(br);
            UIElement glyph = icon != null
                ? new Image { Source = icon, Width = 16, Height = 16 }
                : Ui.Icon(target.StartsWith("ms-settings") ? Ic.Gear : Ic.Open, 13);
            var pill = Ui.Card(Ui.H(7, glyph, Ui.T(qa.Name, 12, FontWeights.SemiBold)).HA(HorizontalAlignment.Center), 12, new Thickness(8, 0, 8, 0));
            pill.Height = 34;
            pill.Margin = new Thickness(3, 0, 3, 0);
            pill.Hoverable(null, Pal.Hover);
            pill.ToolTip = target;
            pill.OnClick(() => { Shell.Open(resolved ?? target); N.Collapse(); });
            grid.Children.Add(pill);
        }
        _quickLaunch.Children.Add(head);
        _quickLaunch.Children.Add(grid);
    }

    public void Rebuild() { BuildShortcuts(); BuildQuickLaunch(); }

    public override void OnShow() { RefreshMedia(); Refresh(); }

    void RefreshMedia()
    {
        var m = App.Media;
        if (m.HasSession)
        {
            _title.Text = m.Title;
            _sub.Text = m.Artist.Length > 0 ? m.Artist : m.IsPlaying ? "Playing" : "Paused";
            _art.Child = m.Art != null ? new Image { Source = m.Art, Stretch = Stretch.UniformToFill } : Ui.Icon(Ic.Music, 20, Pal.Sub);
            _bgArt.Source = m.Art;
        }
        else
        {
            _title.Text = "Not playing";
            _sub.Text = "Play something to see it here";
            _art.Child = Ui.Icon(Ic.Music, 20, Pal.Sub);
            _bgArt.Source = null;
        }
        _playGlyph.Text = m.IsPlaying ? Ic.Pause : Ic.Play;
        UpdateProgress();
    }

    void UpdateProgress()
    {
        var m = App.Media;
        var d = m.Duration;
        var p = m.CurrentPosition;
        _progress.Value = d > TimeSpan.Zero ? p / d : 0;
        _pos.Text = Fmt.Clock(p);
        _dur.Text = Fmt.Clock(d);
    }

    public override void Refresh()
    {
        UpdateProgress();
        var sys = App.Sys;
        _cpu.Value = sys.Cpu; _cpuT.Text = $"{Math.Round(sys.Cpu * 100)}%";
        var ram = sys.MemTotal > 0 ? (double)sys.MemUsed / sys.MemTotal : 0;
        _ram.Value = ram; _ramT.Text = $"{Math.Round(ram * 100)}%";
        _down.Text = Fmt.Speed(sys.Down);
        _up.Text = Fmt.Speed(sys.Up);
        var uses = new List<string>();
        if (sys.MicInUse) uses.Add("Microphone");
        if (sys.CamInUse) uses.Add("Camera");
        _inUse.Text = uses.Count == 0 ? "Nothing" : string.Join(", ", uses);
        _inUse.Foreground = uses.Count == 0 ? Pal.Sub : Pal.Text;
        _inUseDot.Visibility = uses.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var f = App.Focus;
        _focusLabel.Text = f.Running ? (f.Mode == "Focus" ? "Focusing" : "On a break") : "Focus timer";
        _focusValue.Text = Fmt.Clock(f.Remaining);
        _focusValue.Foreground = f.Running ? Pal.Text : Pal.Sub;
        _recGlyph.Text = App.Capture.Recording ? "" : "";
    }
}
