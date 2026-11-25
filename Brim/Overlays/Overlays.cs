using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brim.Core;
using Brim.Notch;
using Brim.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Brim.Overlays;

/// <summary>Base for borderless windows that cover one monitor exactly.</summary>
public abstract class MonitorWindow : Window
{
    protected readonly Forms.Screen Mon;
    protected IntPtr Hwnd;
    protected double Dpi = 1;

    protected MonitorWindow(Forms.Screen screen, bool clickThrough = false)
    {
        Mon = screen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        var b = screen.Bounds;
        Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
        SourceInitialized += (_, _) =>
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.WS_EX_TOOLWINDOW;
            if (clickThrough) ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
            Native.AddExStyle(Hwnd, ex);
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_NOACTIVATE);
            Dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        };
    }

    protected static BitmapSource ToSource(Drawing.Bitmap bmp)
    {
        var h = bmp.GetHbitmap();
        try
        {
            var s = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            s.Freeze();
            return s;
        }
        finally { DeleteObject(h); }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
}

/// <summary>Drag a rectangle on screen. Returns it in monitor-relative physical pixels.</summary>
public sealed class AreaSelectWindow : MonitorWindow
{
    readonly TaskCompletionSource<Drawing.Rectangle?> _tcs = new();
    readonly Path _dim = new() { Fill = Pal.Hex("#73000000"), IsHitTestVisible = false };
    readonly Rectangle _sel = new() { Stroke = Pal.White, StrokeThickness = 1.2, StrokeDashArray = [4, 3], IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Border _label = new() { Background = Pal.Hex("#E61E1E22"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Canvas _canvas = new() { Background = Pal.HitClear };
    Point? _start;
    Rect _rect;

    AreaSelectWindow(Forms.Screen screen, Drawing.Bitmap? frozen) : base(screen)
    {
        Cursor = Cursors.Cross;
        var root = new Grid();
        if (frozen != null) root.Children.Add(new Image { Source = ToSource(frozen), Stretch = Stretch.Fill });
        root.Children.Add(_canvas);
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_sel);
        _canvas.Children.Add(_label);
        var hint = new Border
        {
            Background = Pal.Hex("#E61E1E22"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 60, 0, 0),
            Child = Ui.T("Drag to select an area · Esc to cancel", 13, FontWeights.SemiBold), IsHitTestVisible = false,
        };
        root.Children.Add(hint);
        Content = root;
        Loaded += (_, _) => { UpdateDim(); Activate(); Focus(); };
        SizeChanged += (_, _) => UpdateDim();
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(this); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_start is not Point s) return;
            var p = e.GetPosition(this);
            _rect = new Rect(s, p);
            UpdateDim();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            ReleaseMouseCapture();
            if (_start == null) return;
            _start = null;
            if (_rect.Width < 4 || _rect.Height < 4) { Finish(null); return; }
            Finish(new Drawing.Rectangle((int)(_rect.X * Dpi), (int)(_rect.Y * Dpi), (int)(_rect.Width * Dpi), (int)(_rect.Height * Dpi)));
        };
        MouseRightButtonUp += (_, _) => Finish(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Deactivated += (_, _) => { if (!_tcs.Task.IsCompleted && _start == null) Finish(null); };
    }

    void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        if (_rect.Width < 1)
        {
            _dim.Data = full;
            return;
        }
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_rect));
        _sel.Visibility = _label.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sel, _rect.X); Canvas.SetTop(_sel, _rect.Y);
        _sel.Width = _rect.Width; _sel.Height = _rect.Height;
        _label.Child = Ui.T($"{(int)(_rect.Width * Dpi)} × {(int)(_rect.Height * Dpi)}", 12, FontWeights.SemiBold);
        Canvas.SetLeft(_label, _rect.X);
        Canvas.SetTop(_label, Math.Max(0, _rect.Y - 28));
    }

    void Finish(Drawing.Rectangle? r)
    {
        if (_tcs.Task.IsCompleted) return;
        _tcs.TrySetResult(r);
        Close();
    }

    public static Task<Drawing.Rectangle?> PickAsync(Forms.Screen screen, Drawing.Bitmap? frozen)
    {
        var w = new AreaSelectWindow(screen, frozen);
        w.Show();
        return w._tcs.Task;
    }
}

/// <summary>A soft bright band around the screen that lights your face on calls.</summary>
public sealed class EdgeLightWindow : MonitorWindow
{
    readonly Border _band = new();
    readonly Border _glow = new();

    public EdgeLightWindow(Forms.Screen screen) : base(screen, clickThrough: true)
    {
        var g = new Grid { IsHitTestVisible = false };
        g.Children.Add(_glow);
        g.Children.Add(_band);
        Content = g;
        Refresh();
    }

    public void Refresh()
    {
        var s = App.S;
        var c = Pal.Mix(Color.FromRgb(236, 244, 255), Color.FromRgb(255, 196, 128), s.EdgeLightWarmth);
        _band.BorderBrush = new SolidColorBrush(c);
        _band.BorderThickness = new Thickness(s.EdgeLightWidth);
        _glow.BorderBrush = new SolidColorBrush(Pal.A(c, 140));
        _glow.BorderThickness = new Thickness(s.EdgeLightWidth * 1.6);
        _glow.Effect = new BlurEffect { Radius = Math.Max(10, s.EdgeLightWidth) };
        _band.CornerRadius = _glow.CornerRadius = new CornerRadius(0);
    }
}

/// <summary>Privacy Blur: frosts everything on screen until dismissed.</summary>
public sealed class CloakWindow : MonitorWindow
{
    public CloakWindow(Forms.Screen screen, Drawing.Bitmap shot) : base(screen)
    {
        var g = new Grid();
        g.Children.Add(new Image { Source = ToSource(shot), Stretch = Stretch.Fill, Effect = new BlurEffect { Radius = 70, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } });
        g.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(120, 24, 24, 30), Color.FromArgb(170, 10, 10, 14), 90) });
        g.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 60),
            Background = Pal.Hex("#99000000"), CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 8, 16, 8),
            Child = Ui.H(8, Ui.Icon(Ic.Eye, 13, Pal.Sub), Ui.T("Blurred · click, press Esc or Ctrl+Alt+H to show the screen", 13, FontWeights.SemiBold, Pal.Sub)),
        });
        Content = g;
        Cursor = Cursors.Arrow;
        MouseDown += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => { Activate(); Focus(); };
    }
}

/// <summary>Fullscreen listening room with cover-art lighting, visualizer and lyrics.</summary>
public sealed class FullscreenMusicWindow : Window
{
    readonly Image _bg = new() { Stretch = Stretch.UniformToFill, Opacity = 0.6, Effect = new BlurEffect { Radius = 90, RenderingBias = RenderingBias.Performance } };
    readonly EffectCanvas _fx = new() { Kind = "Ambient" };
    readonly Bars _bars = new() { Height = 90, BarWidth = 7, Gap = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.55 };
    readonly Border _art = new() { Width = 340, Height = 340, CornerRadius = new CornerRadius(26), ClipToBounds = true, Background = Pal.Track };
    readonly TextBlock _title = Ui.Big("", 34, FontWeights.Bold);
    readonly TextBlock _artist = Ui.T("", 18, FontWeights.SemiBold, Pal.Sub);
    readonly Meter _progress = new(Pal.White, 5);
    readonly TextBlock _pos = Ui.T("0:00", 13, null, Pal.Sub), _left = Ui.T("-0:00", 13, null, Pal.Sub);
    readonly TextBlock _play = Ui.Icon(Ic.Play, 24, Pal.Ink);
    readonly TextBlock _lyric = Ui.Big("", 32, FontWeights.Bold);
    readonly TextBlock _lyricNext = Ui.T("", 20, FontWeights.SemiBold, Pal.Dim);
    readonly Stopwatch _sw = Stopwatch.StartNew();
    List<LyricLine>? _lyrics;
    string _key = "";
    double _last;

    public FullscreenMusicWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = Brushes.Black;
        var b = App.Notch.Screen.Bounds;
        Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
        Loaded += (_, _) => { WindowState = WindowState.Maximized; Activate(); };
        _fx.Source = () => (App.Media.ArtColor, App.Media.ArtColor2, App.Media.IsPlaying ? App.Audio.Bands(32) : [], App.Audio.Level, App.Media.IsPlaying);

        var root = new Grid { ClipToBounds = true };
        root.Children.Add(_bg);
        root.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(90, 0, 0, 0), Color.FromArgb(210, 0, 0, 0), 90) });
        root.Children.Add(_fx);
        root.Children.Add(_bars.M(0, 0, 0, 30));

        _art.Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 0, Opacity = 0.6 };
        _title.TextWrapping = TextWrapping.Wrap; _title.TextTrimming = TextTrimming.CharacterEllipsis; _title.MaxHeight = 92;
        var play = new Border { Width = 70, Height = 70, CornerRadius = new CornerRadius(35), Background = Pal.White, Child = _play };
        var controls = Ui.H(48, Ui.Btn(Ui.Icon(Ic.Prev, 22), () => App.Media.Prev()), Ui.Btn(play, () => App.Media.Toggle()), Ui.Btn(Ui.Icon(Ic.Next, 22), () => App.Media.Next()));
        foreach (FrameworkElement c in controls.Children) c.VerticalAlignment = VerticalAlignment.Center;
        controls.HorizontalAlignment = HorizontalAlignment.Left;
        var info = Ui.V(10, _title, _artist, _progress.M(0, 24, 0, 0), Ui.G("*,*", "", _pos, _left.HA(HorizontalAlignment.Right).At(1)), controls.M(0, 20, 0, 0));
        info.Width = 440;
        info.VerticalAlignment = VerticalAlignment.Center;
        var player = Ui.H(48, _art, info);
        player.VerticalAlignment = VerticalAlignment.Center;

        _lyric.TextWrapping = TextWrapping.Wrap; _lyric.TextTrimming = TextTrimming.None;
        _lyricNext.TextWrapping = TextWrapping.Wrap; _lyricNext.TextTrimming = TextTrimming.None;
        var lyrics = Ui.V(18, _lyric, _lyricNext);
        lyrics.Width = 520;
        lyrics.VerticalAlignment = VerticalAlignment.Center;

        var main = Ui.G("*,Auto,80,Auto,*", "*", player.At(1), lyrics.At(3));
        root.Children.Add(main);

        var close = Ui.Circle(Ic.Close, Close, 44, 16, Pal.Hex("#33FFFFFF"), tip: "Exit fullscreen (Esc)");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, 28, 28, 0);
        root.Children.Add(close);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Space) App.Media.Toggle();
            if (e.Key == Key.Right) App.Media.Next();
            if (e.Key == Key.Left) App.Media.Prev();
        };
        App.Media.Changed += Sync;
        CompositionTarget.Rendering += OnFrame;
        Closed += (_, _) => { App.Media.Changed -= Sync; CompositionTarget.Rendering -= OnFrame; };
        Sync();
    }

    void Sync()
    {
        var m = App.Media;
        _title.Text = m.HasSession ? m.Title : "Nothing playing";
        _artist.Text = m.HasSession ? (m.Artist.Length > 0 ? m.Artist : m.AppName) : "Start some music and it shows up here";
        _art.Child = m.Art != null ? new Image { Source = m.Art, Stretch = Stretch.UniformToFill } : Ui.Icon(Ic.Music, 60, Pal.Sub);
        _bg.Source = m.Art;
        _play.Text = m.IsPlaying ? Ic.Pause : Ic.Play;
        _bars.Fill = new LinearGradientBrush(Pal.Mix(m.ArtColor, Colors.White, .35), m.ArtColor, 90);
        var key = m.Artist + "|" + m.Title;
        if (key != _key)
        {
            _key = key;
            _lyrics = null;
            _lyric.Text = _lyricNext.Text = "";
            if (m.HasSession) _ = Load(m.Title, m.Artist, m.Duration, key);
        }
    }

    async Task Load(string t, string a, TimeSpan d, string key)
    {
        var l = await App.Lyrics.GetAsync(t, a, d);
        if (key == _key) _lyrics = l;
    }

    void OnFrame(object? s, EventArgs e)
    {
        double now = _sw.Elapsed.TotalSeconds, dt = now - _last;
        if (dt < 1 / 40.0) return;
        _last = now;
        _fx.Advance(dt);
        var m = App.Media;
        _bars.Set(m.IsPlaying ? App.Audio.Bands(48) : new double[48]);
        var p = m.CurrentPosition;
        _progress.Value = m.Duration > TimeSpan.Zero ? p / m.Duration : 0;
        _pos.Text = Fmt.Clock(p);
        _left.Text = "-" + Fmt.Clock(m.Duration - p);
        if (_lyrics is { Count: > 0 })
        {
            int i = _lyrics.FindLastIndex(l => l.At <= p + TimeSpan.FromMilliseconds(250));
            _lyric.Text = i >= 0 && _lyrics[i].Text.Length > 0 ? _lyrics[i].Text : "•  •  •";
            _lyricNext.Text = i + 1 < _lyrics.Count ? _lyrics[i + 1].Text : "";
        }
    }
}
