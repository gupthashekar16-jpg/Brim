using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brim.Core;
using Brim.Services;

namespace Brim.Notch.Views;

/// <summary>Mute, night light, focus, brightness and volume.</summary>
public sealed class ControlsView : NotchView
{
    readonly (Border circle, FrameworkElement glyph, TextBlock state) _mute, _night, _focus;
    readonly BigSlider _bright = new(Ic.Sun, "Brightness");
    readonly BigSlider _vol = new(Ic.Speaker, "Volume");
    readonly TextBlock _brightNote = Ui.T("Brightness can't be changed for this display", 12, null, Pal.Dim);

    public ControlsView(NotchWindow n) : base(n)
    {
        var tiles = new UniformGrid { Rows = 1, Margin = new Thickness(-4, 0, -4, 0) };
        tiles.Children.Add(Tile("Mute", Ui.Icon(Ic.Speaker, 14), out _mute, () => { App.Audio.Muted = !App.Audio.Muted; Refresh(); }));
        tiles.Children.Add(Tile("Night light", Ui.Icon(Ic.Moon, 14), out _night, () => { SystemService.ToggleNightLight(); Task.Delay(300).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(Refresh))); }));
        tiles.Children.Add(Tile("Focus", Mini.BellOff(Pal.Text, 15), out _focus, () =>
        {
            if (App.Focus.Running) App.Focus.Toggle();
            else { App.Focus.SetMode("Focus"); App.Focus.Toggle(); }
            Refresh();
        }));
        _bright.ValueChanged += v => App.Sys.SetBrightness((int)Math.Round(v * 100));
        _vol.ValueChanged += v => { App.Audio.Volume = (float)v; if (App.Audio.Muted && v > 0) App.Audio.Muted = false; };
        App.Audio.VolumeChanged += () => { if (IsVisible && !_vol.Dragging) Refresh(); };
        var brightBlock = Ui.V(0, _bright, _brightNote.M(4, 6, 0, 0));
        var body = Ui.V(12, tiles, brightBlock, _vol);
        Children.Add(body);
    }

    static Border Tile(string title, FrameworkElement glyph, out (Border, FrameworkElement, TextBlock) parts, Action click)
    {
        var circle = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Pal.Hex("#26FFFFFF"), Child = glyph };
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var state = Ui.T("Off", 12, null, Pal.Sub);
        var g = Ui.G("Auto,*", "", circle, Ui.V(1, Ui.T(title, 14, FontWeights.SemiBold), state).M(12, 0, 0, 0).VA(VerticalAlignment.Center).At(1));
        var card = Ui.Card(g, 14, new Thickness(10, 8, 10, 8));
        card.Margin = new Thickness(4, 0, 4, 0);
        card.Hoverable(null, Pal.Hover);
        card.OnClick(click);
        parts = (circle, glyph, state);
        return card;
    }

    static void Paint((Border circle, FrameworkElement glyph, TextBlock state) t, bool on, string? text = null)
    {
        t.circle.Background = on ? Pal.White : Pal.Hex("#26FFFFFF");
        if (t.glyph is TextBlock tb) tb.Foreground = on ? Pal.Ink : Pal.Text;
        if (t.glyph is System.Windows.Shapes.Path p) p.Stroke = on ? Pal.Ink : Pal.Text;
        t.state.Text = text ?? (on ? "On" : "Off");
    }

    public override void Refresh()
    {
        bool muted = App.Audio.Muted;
        Paint(_mute, muted);
        if (_mute.glyph is TextBlock mg) mg.Text = muted ? Ic.Mute : Ic.Speaker;
        var night = SystemService.NightLightOn();
        Paint(_night, night == true, night == null ? "Open settings" : null);
        Paint(_focus, App.Focus.Running && App.Focus.Mode == "Focus", App.Focus.Running ? Fmt.Clock(App.Focus.Remaining) + " left" : "Off");
        var b = App.Sys.Brightness;
        _bright.IsEnabled = b != null;
        _bright.Opacity = b != null ? 1 : 0.45;
        _brightNote.Visibility = b == null ? Visibility.Visible : Visibility.Collapsed;
        if (!_bright.Dragging) _bright.Value = (b ?? 0) / 100.0;
        if (!_vol.Dragging) _vol.Value = App.Audio.Volume;
    }
}

/// <summary>Bluetooth radio toggle and paired devices.</summary>
public sealed class BluetoothView : NotchView
{
    readonly ToggleButton _toggle;
    readonly TextBlock _status = Ui.T("Checking…", 12, null, Pal.Sub);
    readonly Border _onBadge = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Background = Pal.Hex("#26FFFFFF") };
    readonly TextBlock _count = Ui.T("", 12, null, Pal.Dim);
    readonly StackPanel _list = new();
    bool _loading, _suppress;

    public BluetoothView(NotchWindow n) : base(n)
    {
        _toggle = Ui.Switch(false, on => { if (!_suppress) _ = SetRadio(on); });
        _onBadge.Child = Ui.T("On", 11, FontWeights.SemiBold);
        var title = Ui.H(8, Ui.T("Bluetooth", 15, FontWeights.SemiBold), _onBadge);
        var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Pal.Hex("#26FFFFFF"), Child = Ui.Icon(Ic.Bluetooth, 15) };
        var header = Ui.Card(Ui.G("Auto,*,Auto", "", icon,
            Ui.V(2, title, _status).M(12, 0, 0, 0).VA(VerticalAlignment.Center).At(1),
            Ui.H(8,
                Ui.Circle(Ic.Refresh, () => _ = Load(), 32, 13, Pal.Hex("#1AFFFFFF"), tip: "Refresh"),
                Ui.Circle(Ic.Gear, () => { Shell.Open("ms-settings:bluetooth"); N.Collapse(); }, 32, 13, Pal.Hex("#1AFFFFFF"), tip: "Bluetooth settings"),
                _toggle).VA(VerticalAlignment.Center).At(2)), 14, new Thickness(14, 10, 14, 10));
        var sub = Ui.G("*,Auto", "", Ui.T("PAIRED DEVICES", 12, FontWeights.SemiBold, Pal.Sub), _count.At(1));
        sub.Margin = new Thickness(4, 14, 4, 8);
        var content = Ui.V(0, header, sub, _list);
        Children.Add(Scroll(content, 300));
    }

    public override void OnShow() => _ = Load();

    async Task SetRadio(bool on)
    {
        await App.Bt.SetRadioAsync(on);
        await Task.Delay(600);
        await Load();
    }

    async Task Load()
    {
        if (_loading) return;
        _loading = true;
        _status.Text = "Looking for devices…";
        var on = await App.Bt.RadioOnAsync();
        _suppress = true;
        _toggle.IsChecked = on == true;
        _suppress = false;
        _onBadge.Visibility = on == true ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)_onBadge.Child).Text = "On";
        List<BtDevice> devices = on == false ? [] : await App.Bt.PairedAsync();
        _list.Children.Clear();
        foreach (var d in devices) _list.Children.Add(Row(d));
        _count.Text = devices.Count > 0 ? $"{devices.Count} paired" : "";
        int connected = devices.Count(d => d.Connected);
        _status.Text = on == null ? "No Bluetooth adapter found" : on == false ? "Off" : connected > 0 ? $"{connected} connected" : "Not connected to any device";
        if (devices.Count == 0 && on == true)
            _list.Children.Add(Ui.T("No paired devices yet. Pair one in Settings.", 13, null, Pal.Sub).M(6, 4, 0, 4));
        _loading = false;
        N.Relayout();
    }

    Border Row(BtDevice d)
    {
        var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Pal.Hex("#1AFFFFFF"), Child = Ui.Icon(d.Glyph, 14) };
        var sub = d.Connected ? (d.Battery is int b ? $"Connected · {b}% battery" : "Connected") : d.Battery is int b2 ? $"Not connected · {b2}%" : "Not connected";
        var btnText = Ui.T(d.Connected ? "Disconnect" : "Connect", 13, FontWeights.SemiBold, d.Connected ? Pal.Text : Pal.LinkBlue);
        var btnBorder = new Border { Height = 30, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(8), Background = d.Connected ? Pal.Hex("#26FFFFFF") : Pal.Hex("#2E3B82F6"), Child = btnText };
        var btn = Ui.Btn(btnBorder, async () =>
        {
            btnText.Text = d.Connected ? "Disconnecting…" : "Connecting…";
            await App.Bt.SetConnectedAsync(d, !d.Connected);
            await Task.Delay(1500);
            await Load();
        });
        var g = Ui.G("Auto,*,Auto", "", icon, Ui.V(1, Ui.T(d.Name, 14, FontWeights.SemiBold), Ui.T(sub, 12, null, d.Connected ? Pal.Green : Pal.Sub)).M(12, 0, 0, 0).VA(VerticalAlignment.Center).At(1), btn.VA(VerticalAlignment.Center).At(2));
        var card = Ui.Card(g, 14, new Thickness(12, 8, 10, 8));
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }
}

/// <summary>CPU, memory, network, storage, battery and uptime tiles.</summary>
public sealed class StatsView : NotchView
{
    readonly UniformGrid _grid = new() { Columns = 3 };
    readonly Dictionary<string, (TextBlock value, FrameworkElement extra, TextBlock sub)> _tiles = [];
    readonly Sparkline _spark = new(Color.FromRgb(90, 200, 250)) { Height = 22, Width = 64, HorizontalAlignment = HorizontalAlignment.Right };
    readonly Meter _mem = new(Pal.Purple, 6), _disk = new(Pal.Orange, 6), _bat = new(Pal.Green, 6);
    readonly TextBlock _optText = Ui.T("Optimize memory", 14, FontWeights.SemiBold);
    bool _optimizing;

    public StatsView(NotchWindow n) : base(n)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.Margin = new Thickness(-5, -5, -5, 0);
        Children.Add(_grid);
        var opt = Ui.Card(Ui.H(9, Mini.Rocket(Pal.Text, 15), _optText).HA(HorizontalAlignment.Center), 14, new Thickness(0));
        opt.Height = 44;
        opt.Margin = new Thickness(0, 10, 0, 0);
        opt.Hoverable(null, Pal.Hover);
        opt.OnClick(async () =>
        {
            if (_optimizing) return;
            _optimizing = true;
            _optText.Text = "Optimizing…";
            var freed = await SystemService.OptimizeMemoryAsync();
            _optText.Text = freed > 0 ? $"Freed {Fmt.Bytes(freed)}" : "Memory is already lean";
            await Task.Delay(2500);
            _optText.Text = "Optimize memory";
            _optimizing = false;
        });
        Children.Add(opt.At(0, 1));
        Build();
    }

    public void Build()
    {
        _grid.Children.Clear();
        _tiles.Clear();
        foreach (var key in S.StatTiles)
        {
            (string glyph, Brush color, FrameworkElement extra) spec = key switch
            {
                "CPU" => (Ic.Chip, Pal.Blue, _spark),
                "Memory" => (Ic.Ram, Pal.Purple, _mem),
                "Network" => (Ic.Down, Pal.Green, new Border()),
                "Storage" => (Ic.Drive, Pal.Orange, _disk),
                "Battery" => (Ic.Bolt, Pal.Green, _bat),
                _ => (Ic.Clock, Pal.Yellow, new Border()),
            };
            if (spec.extra.Parent is Panel p) p.Children.Remove(spec.extra);
            var value = Ui.Big("—", 21, FontWeights.SemiBold);
            var sub = Ui.T("", 12, null, Pal.Sub);
            var body = Ui.G("*", "Auto,Auto,*,Auto", Label(spec.glyph, key, spec.color), value.M(0, 6, 0, 0).At(0, 1));
            if (key == "CPU") body.Children.Add(spec.extra.At(0, 3).VA(VerticalAlignment.Bottom));
            else if (spec.extra is Meter) { body.Children.Add(spec.extra.At(0, 2).M(0, 8, 0, 4)); body.Children.Add(sub.At(0, 3)); }
            else body.Children.Add(sub.At(0, 3));
            var card = Ui.Card(body, 16, new Thickness(16, 12, 16, 12));
            card.Height = 96;
            card.Margin = new Thickness(5);
            _grid.Children.Add(card);
            _tiles[key] = (value, spec.extra, sub);
        }
        Refresh();
    }

    public override void Refresh()
    {
        var s = App.Sys;
        foreach (var (key, t) in _tiles)
        {
            switch (key)
            {
                case "CPU":
                    t.value.Text = $"{Math.Round(s.Cpu * 100)}%";
                    _spark.Push(s.Cpu);
                    break;
                case "Memory":
                    t.value.Text = Fmt.Bytes(s.MemUsed);
                    _mem.Value = s.MemTotal > 0 ? (double)s.MemUsed / s.MemTotal : 0;
                    t.sub.Text = "of " + Fmt.Bytes(s.MemTotal);
                    break;
                case "Network":
                    t.value.Text = "↓ " + Fmt.Speed(s.Down);
                    t.sub.Text = "↑ " + Fmt.Speed(s.Up);
                    break;
                case "Storage":
                    t.value.Text = Fmt.Bytes(s.DiskFree) + " free";
                    _disk.Value = s.DiskTotal > 0 ? 1 - (double)s.DiskFree / s.DiskTotal : 0;
                    t.sub.Text = "of " + Fmt.Bytes(s.DiskTotal);
                    break;
                case "Battery":
                    t.value.Text = s.HasBattery ? $"{s.Battery}%" : "AC";
                    _bat.Value = s.HasBattery ? s.Battery / 100.0 : 1;
                    _bat.Fill = s.HasBattery && s.Battery <= 20 && !s.Plugged ? Pal.Red : Pal.Green;
                    t.sub.Text = !s.HasBattery ? "No battery" : s.Charging ? "Charging" : s.Plugged ? "Plugged in" : "On battery";
                    break;
                default:
                    t.value.Text = Fmt.Uptime(s.Uptime);
                    t.sub.Text = "since last restart";
                    break;
            }
        }
    }
}
