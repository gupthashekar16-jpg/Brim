using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brim.Core;
using Brim.Notch.Views;
using Brim.Overlays;
using Brim.Services;
using Forms = System.Windows.Forms;

namespace Brim.Notch;

/// <summary>The notch itself: a transparent topmost window hosting the island shape.</summary>
public sealed class NotchWindow : Window
{
    static AppSettings S => App.S;
    enum Mode { Closed, Open, Transient }

    IntPtr _hwnd;
    double _dpi = 1;
    public Forms.Screen Screen { get; private set; } = Forms.Screen.PrimaryScreen!;

    // ---- visual tree
    readonly Canvas _root = new() { Background = Brushes.Transparent };
    readonly Grid _island = new();
    readonly Image _backdrop = new() { Stretch = Stretch.Fill, Effect = new BlurEffect { Radius = 22, KernelType = KernelType.Gaussian }, IsHitTestVisible = false };
    readonly EffectCanvas _fx = new();
    readonly Border _rim = new() { BorderBrush = Pal.Hex("#1FFFFFFF"), IsHitTestVisible = false };
    readonly Grid _closed = new();
    readonly StackPanel _closedV = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Canvas _canvas = new();
    readonly Grid _open = new();
    readonly Border _viewHost = new();
    readonly Border _transient = new();
    readonly Border _toast = new();

    // ---- closed content
    readonly StackPanel _left = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _clock = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Ring _closedRing = new(17, 2.2);
    readonly Border _closedArt = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), ClipToBounds = true };
    readonly Bars _closedBars = new() { Height = 14, BarWidth = 2.6, Gap = 2, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _privacyDot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Pal.Orange, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _leftText = Ui.T("", 12, FontWeights.SemiBold);
    readonly TextBlock _downloadGlyph = Ui.Icon(Ic.Down, 12, Pal.Blue);
    readonly Border _shelfBadge = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Background = Pal.Hex("#33FFFFFF"), VerticalAlignment = VerticalAlignment.Center };
    readonly SidekickFace _closedFace = new(16, Pal.Orange);
    readonly Border _recDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Pal.Red, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _recText = Ui.T("0:00", 12, FontWeights.SemiBold, Pal.Red);
    readonly TextBlock _vTime = Ui.T("", 11, FontWeights.Bold);

    // ---- header
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    readonly Dictionary<string, (Border bg, TextBlock glyph)> _tabButtons = [];
    readonly Border _hdrPrivacy = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Pal.Orange, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _hdrWeather = Ui.T("--°", 13, FontWeights.SemiBold);
    readonly Border _hdrWeatherIcon = new();
    readonly TextBlock _hdrBattery = Ui.T("", 13, FontWeights.SemiBold);
    readonly Ring _hdrRing = new(19, 2.2);
    readonly TextBlock _pinGlyph = Ui.Icon(Ic.Pin, 14, Pal.Sub);
    readonly Border _agentDot = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Pal.Orange, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    Border? _weatherBtn;

    // ---- views
    readonly Dictionary<string, NotchView> _views = [];
    NotchView? _view;
    string _tab = "Home";

    // ---- state
    Mode _mode = Mode.Closed;
    bool _hover, _pinned, _dragging, _mouseDown, _dragOver, _fsHidden, _captureTemp, _loopOn;
    Point _downAt;
    DateTime _transientUntil;
    bool _transientSticky;
    Approval? _transientApproval;
    readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1800) };
    readonly Stopwatch _sw = Stopwatch.StartNew();
    double _lastT, _frameAcc, _viewAcc, _backdropAcc;
    readonly TranslateTransform _downloadMove = new();
    readonly List<(double x, double t)> _shake = [];

    // ---- animation state: size, radius, hide offset
    double _w = 214, _h = 30, _r = 10, _off;
    double _vw, _vh, _vr, _voff;
    double _tw = 214, _th = 30, _tr = 10, _toff;
    double _envW, _envH;

    // ---- tucking away over apps
    readonly Border _handle = new()
    {
        CornerRadius = new CornerRadius(3), BorderBrush = Pal.Hex("#66FFFFFF"), BorderThickness = new Thickness(1),
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    bool _tucked, _appInFront;
    Native.WinEventProc? _fgProc;
    IntPtr _fgHook;

    /// <summary>Closed and not pointed at, while an app is in front (or always, with Hide until hovered).</summary>
    bool Tucked => _mode == Mode.Closed && !_hover && !_dragging && !_fsHidden &&
                   (S.HideUntilHovered || (S.HideOverApps && _appInFront));

    /// <summary>Snapshot mode only: pretend an app is (or isn't) in front.</summary>
    public void SimulateAppInFront(bool inFront) { _appInFront = inFront; Retarget(); }

    public bool Pinned => _pinned;
    public bool IsOpen => _mode == Mode.Open;
    public FrameworkElement Island => _island;

    /// <summary>Jumps every animation to its target (used for snapshots).</summary>
    public void Settle()
    {
        Retarget();
        _w = _tw; _h = _th; _r = _tr; _off = _toff;
        _vw = _vh = _vr = _voff = 0;
        _closed.Opacity = _closedV.Opacity = _mode == Mode.Closed && !_tucked ? 1 : 0;
        _open.Opacity = _mode == Mode.Open ? 1 : 0;
        _transient.Opacity = _mode == Mode.Transient ? 1 : 0;
        _open.Visibility = _mode == Mode.Open ? Visibility.Visible : Visibility.Hidden;
        _transient.Visibility = _mode == Mode.Transient ? Visibility.Visible : Visibility.Hidden;
        Place(true);
        UpdateLayout();
        ApplyFrame();
    }

    public NotchWindow()
    {
        Title = "Brim";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Width = 600; Height = 80;
        Content = _root;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        BuildIsland();
        BuildClosed();
        BuildOpen();

        _openTimer.Tick += (_, _) => { _openTimer.Stop(); if (_hover && _mode != Mode.Open && !_dragging) Open(); };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); if (_mode == Mode.Open && !StayOpen()) Collapse(); else if (_mode == Mode.Open) _closeTimer.Start(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visibility = Visibility.Collapsed; };

        SourceInitialized += (_, _) => OnSourceInitialized();
        Loaded += (_, _) =>
        {
            ApplySettings();
            // Build every tab in the background so the first open of each one is instant.
            var pending = new Queue<string>(S.TabOrder.Where(TabOn));
            void Next()
            {
                if (pending.Count == 0) return;
                View(pending.Dequeue());
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Next);
            }
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Next);
        };
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += (_, _) => { if (_mode == Mode.Open && !_pinned && !S.KeepOpen && !CursorInside(10)) Collapse(); MakeNoActivate(true); };

        S.OnChanged += OnSettingChanged;
        App.Sys.Tick += OnSecond;
        App.Media.Changed += () => { UpdateClosed(); Retarget(); };
        App.Audio.VolumeChanged += () => ShowOsd("volume");
        App.Sys.BrightnessChanged += () => ShowOsd("brightness");
        App.Sys.PowerChanged += plugged => { if (S.BatteryAlerts) ShowBatteryAlert(plugged); };
        App.Sys.LowBattery += () => { if (S.BatteryAlerts) ShowBatteryAlert(false, true); };
        App.Focus.Finished += mode => ShowAlert(Ui.Icon(Ic.Cup, 18), mode == "Focus" ? "Focus session complete" : "Break is over",
            mode == "Focus" ? "Nice work. Time for a short break." : "Ready for the next focus session?",
            mode == "Focus" ? ("Start break", () => { App.Focus.SetMode("Break"); App.Focus.Toggle(); }) : ("Start focus", () => { App.Focus.SetMode("Focus"); App.Focus.Toggle(); }));
        App.Agents.Alert += s =>
        {
            if (S.AgentActivity == "Off") return;
            var title = s.State == "attention" ? $"{s.Project} · {s.Agent} needs you" : $"{s.Project} · {s.Agent} finished";
            ShowAlert(new SidekickFace(34, s.Agent == "Codex" ? Pal.Teal : Pal.Orange), title, s.Message, null, 9);
        };
        App.Agents.Changed += () => UpdateClosed();
        App.Agents.ApprovalRequested += ShowApproval;
        App.Agents.ApprovalClosed += a =>
        {
            if (!ReferenceEquals(_transientApproval, a)) return;
            _transientApproval = null;
            if (App.Agents.Pending.FirstOrDefault() is Approval next) ShowApproval(next);
            else EndTransient();
        };
        App.Phone.Received += name => ShowAlert(Ui.Icon(Ic.Phone, 18), "Received from your phone", name,
            ("Show", () => Shell.Reveal(Path.Combine(App.Phone.ReceiveFolder, name))), 5);
        App.Downloads.Changed += () => { UpdateClosed(); Retarget(); };
        App.Downloads.Completed += name => { if (S.W("Downloads")) ShowAlert(Ui.Icon(Ic.Down, 18, Pal.Blue), "Download complete", name, ("Show", () => Shell.Reveal(Path.Combine(App.Downloads.Folder, name)))); };
        App.Shelf.Changed += () => { RefreshTabBadges(); UpdateClosed(); };
        App.Capture.Changed += () => { UpdateClosed(); Retarget(); UpdateCaptureExclusion(); };
        App.Focus.Changed += () => { if (_mode != Mode.Open) { UpdateClosed(); } };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => { PickScreen(); Place(true); });
    }

    // =====================================================================
    // Build
    // =====================================================================
    void BuildIsland()
    {
        _island.Children.Add(_backdrop);
        _island.Children.Add(_fx);
        _island.Children.Add(_closed);
        _island.Children.Add(_closedV);
        _island.Children.Add(_canvas);
        _island.Children.Add(_rim);
        _island.Children.Add(_handle);
        _canvas.Children.Add(_open);
        _canvas.Children.Add(_transient);
        _fx.Source = () =>
        {
            var m = App.Media;
            var info = EffectCanvas.All.FirstOrDefault(e => e.Key == _fx.Kind);
            bool reactive = info?.Music == true && m.IsPlaying;
            return (m.HasSession ? m.ArtColor : Color.FromRgb(70, 120, 255), m.HasSession ? m.ArtColor2 : Color.FromRgb(140, 70, 220),
                reactive ? App.Audio.Bands(32) : [], reactive ? App.Audio.Level : 0, m.IsPlaying);
        };
        _root.Children.Add(_island);

        _island.MouseEnter += (_, _) =>
        {
            _hover = true;
            _closeTimer.Stop();
            // a transient card may hold buttons (alerts, approvals): don't swap it out from under the pointer
            if (_mode == Mode.Closed && S.ExpandOnHover && !_dragging) _openTimer.Start();
            Retarget();
        };
        _island.MouseLeave += (_, _) =>
        {
            _hover = false;
            _openTimer.Stop();
            if (_mode == Mode.Open) _closeTimer.Start();
            Retarget();
        };
        _island.MouseLeftButtonDown += (_, e) =>
        {
            if (_mode == Mode.Open) return;
            _mouseDown = true;
            _downAt = PointToScreen(e.GetPosition(this));
            _island.CaptureMouse();
            e.Handled = true;
        };
        _island.MouseMove += OnIslandMouseMove;
        _island.MouseLeftButtonUp += (_, e) =>
        {
            if (!_mouseDown) return;
            _mouseDown = false;
            _island.ReleaseMouseCapture();
            if (_dragging) { _dragging = false; S.Changed("Edge"); Place(true); }
            else if (_mode != Mode.Open) Open(_transientApproval != null ? "Agents" : null);
            e.Handled = true;
        };
        _island.MouseWheel += (_, e) =>
        {
            if (_mode != Mode.Transient || _transient.Tag is not string kind) return;
            double d = e.Delta > 0 ? 0.03 : -0.03;
            if (kind == "volume") App.Audio.Volume = Math.Clamp(App.Audio.Volume + (float)d, 0, 1);
            else if (App.Sys.Brightness is int b) { App.Sys.SetBrightness(b + (int)(d * 100)); ShowOsd("brightness"); }
            e.Handled = true;
        };

        _island.AllowDrop = true;
        _island.DragEnter += (_, e) =>
        {
            if (!S.W("Shelf") || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            _dragOver = true;
            e.Effects = DragDropEffects.Copy;
            if (_mode != Mode.Open) Open("Shelf"); else Select("Shelf");
        };
        _island.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _island.DragLeave += (_, _) => { _dragOver = false; };
        _island.Drop += (_, e) =>
        {
            _dragOver = false;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { App.Shelf.Add(files); Select("Shelf"); }
        };
    }

    void BuildClosed()
    {
        _closed.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _closed.ColumnDefinitions.Add(new ColumnDefinition());
        _closed.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _closed.Margin = new Thickness(12, 0, 12, 0);
        _closed.Children.Add(_left);
        _closed.Children.Add(_clock.At(1));
        _closed.Children.Add(_right.At(2));
        _clock.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        _closedBars.Fill = Pal.White;
        _downloadGlyph.RenderTransform = _downloadMove;
        _shelfBadge.Child = Ui.T("", 11, FontWeights.Bold);
        _closedV.Children.Add(_vTime);
        _vTime.TextAlignment = TextAlignment.Center;
    }

    void BuildOpen()
    {
        _open.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _open.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _open.Margin = new Thickness(0);
        var header = BuildHeader();
        _open.Children.Add(header);
        _viewHost.Margin = new Thickness(18, 12, 18, 18);
        _open.Children.Add(_viewHost.At(0, 1));
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.Margin = new Thickness(0, 0, 0, 10);
        _toast.CornerRadius = new CornerRadius(14);
        _toast.Padding = new Thickness(14, 7, 14, 7);
        _toast.Background = Pal.Hex("#F22A2A30");
        _toast.Visibility = Visibility.Collapsed;
        _toast.IsHitTestVisible = false;
        Grid.SetRowSpan(_toast, 2);
        _open.Children.Add(_toast);
        _open.Visibility = Visibility.Hidden;
        _transient.Visibility = Visibility.Hidden;
    }

    Grid BuildHeader()
    {
        var tabsPill = new Border { CornerRadius = new CornerRadius(19), Padding = new Thickness(3), Child = _tabs };
        tabsPill.SetResourceReference(Border.BackgroundProperty, "CardBg");
        RebuildTabs();

        var robot = new Grid { Width = 30, Height = 30 };
        robot.Children.Add(Ui.Icon(Ic.Robot, 15));
        robot.Children.Add(_agentDot.M(0, 5, 4, 0));
        var robotBtn = Ui.Btn(new Border { CornerRadius = new CornerRadius(15), Background = Pal.HitClear, Child = robot }.Hoverable(Pal.HitClear, Pal.Hover), () => Select("Agents"), "Sidekick · AI agents");

        _weatherBtn = new Border { Padding = new Thickness(6, 0, 6, 0), Height = 30, CornerRadius = new CornerRadius(15), Child = Ui.H(5, _hdrWeatherIcon, _hdrWeather).VA(VerticalAlignment.Center) };
        _weatherBtn.Hoverable(Pal.HitClear, Pal.Hover);
        var weatherBtn = Ui.Btn(_weatherBtn, () => Select("Weather"), "Weather");

        var battery = Ui.H(6, _hdrBattery, _hdrRing);
        battery.VerticalAlignment = VerticalAlignment.Center;
        var pin = Ui.Btn(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = _pinGlyph }.Hoverable(Pal.HitClear, Pal.Hover), () => SetPinned(!_pinned), "Keep open");
        var gear = Ui.Circle(Ic.Gear, () => { App.OpenSettings(); }, 30, 14, fg: Pal.Sub, tip: "Settings");
        Button? power = null;
        power = Ui.Circle(Ic.Power, () =>
        {
            var m = new ContextMenu { PlacementTarget = power, Placement = PlacementMode.Bottom };
            var hide = new MenuItem { Header = "Hide for 1 hour" };
            hide.Click += (_, _) => { Collapse(); Visibility = Visibility.Hidden; var t = new DispatcherTimer { Interval = TimeSpan.FromHours(1) }; t.Tick += (_, _) => { t.Stop(); Visibility = Visibility.Visible; }; t.Start(); };
            var restart = new MenuItem { Header = "Restart Brim" };
            restart.Click += (_, _) => App.Restart();
            var quit = new MenuItem { Header = "Quit Brim" };
            quit.Click += (_, _) => App.Quit();
            m.Items.Add(hide); m.Items.Add(restart); m.Items.Add(quit);
            m.IsOpen = true;
        }, 30, 14, fg: Pal.Sub, tip: "Power");

        var right = Ui.H(6, _hdrPrivacy.M(0, 0, 4, 0), robotBtn, weatherBtn, battery.M(4, 0, 6, 0), pin, gear, power);
        right.VerticalAlignment = VerticalAlignment.Center;
        var g = Ui.G("Auto,*,Auto", "", tabsPill, right.At(2));
        g.Margin = new Thickness(16, 14, 14, 0);
        return g;
    }

    static string TabGlyph(string t) => t switch
    {
        "Home" => Ic.Home, "Agents" => Ic.Chart, "Music" => Ic.Music, "Controls" => Ic.Sliders, "Mixer" => Ic.Speaker,
        "Bluetooth" => Ic.Bluetooth, "Shelf" => Ic.Box, "Clipboard" => Ic.Clipboard, "Focus" => Ic.Cup, "Weather" => Ic.Cloud,
        "Teleprompter" => Ic.Doc, "Capture" => Ic.Camera, "Stopwatch" => Ic.Stopwatch, "Stats" => Ic.Pulse, _ => Ic.Home,
    };

    static string TabTitle(string t) => t switch
    {
        "Agents" => "Agent activity", "Music" => "Now playing", "Mixer" => "Volume mixer", "Shelf" => "Shelf",
        "Capture" => "Screenshot & recording", "Stats" => "System stats", "Focus" => "Focus timer", _ => t,
    };

    public static bool TabOn(string t) => t switch
    {
        "Home" or "Controls" => true,
        "Agents" => S.W("Agents") || S.W("AgentUsage"),
        "Music" => S.W("NowPlaying"),
        "Mixer" => S.W("Mixer"),
        "Bluetooth" => S.W("Bluetooth"),
        "Shelf" => S.W("Shelf"),
        "Clipboard" => S.W("Clipboard"),
        "Focus" => S.W("Focus"),
        "Weather" => S.W("Forecast"),
        "Teleprompter" => S.W("Teleprompter"),
        "Capture" => S.W("Capture"),
        "Stopwatch" => S.W("Stopwatch"),
        "Stats" => S.W("Stats"),
        _ => false,
    };

    void RebuildTabs()
    {
        _tabs.Children.Clear();
        _tabButtons.Clear();
        foreach (var t in S.TabOrder.Where(TabOn))
        {
            var glyph = Ui.Icon(TabGlyph(t), 14, Pal.Hex("#C8C8CE"));
            var bg = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Pal.HitClear, Child = glyph, Cursor = Cursors.Hand, ToolTip = TabTitle(t) };
            var cell = new Grid();
            cell.Children.Add(bg);
            if (t == "Shelf")
            {
                var badge = new Border { MinWidth = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Pal.Orange, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0), Child = Ui.T("", 9, FontWeights.Bold, Pal.Ink).HA(HorizontalAlignment.Center), IsHitTestVisible = false, Tag = "badge" };
                cell.Children.Add(badge);
            }
            var key = t;
            bg.MouseEnter += (_, _) => { if (key != _tab) bg.Background = Pal.Hover; };
            bg.MouseLeave += (_, _) => { if (key != _tab) bg.Background = Pal.HitClear; };
            bg.MouseLeftButtonUp += (_, _) => Select(key);
            _tabs.Children.Add(cell);
            _tabButtons[t] = (bg, glyph);
        }
        RefreshTabBadges();
        HighlightTab();
    }

    void RefreshTabBadges()
    {
        foreach (Grid cell in _tabs.Children.OfType<Grid>())
            foreach (var b in cell.Children.OfType<Border>().Where(b => (b.Tag as string) == "badge"))
            {
                int n = App.Shelf.Items.Count;
                b.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)b.Child).Text = n.ToString();
            }
    }

    void HighlightTab()
    {
        foreach (var (k, (bg, glyph)) in _tabButtons)
        {
            bool on = k == _tab;
            bg.Background = on ? Pal.Hex("#33FFFFFF") : Pal.HitClear;
            glyph.Foreground = on ? Pal.White : Pal.Hex("#C8C8CE");
        }
    }

    NotchView View(string tab)
    {
        if (_views.TryGetValue(tab, out var v)) return v;
        v = tab switch
        {
            "Agents" => new AgentsView(this),
            "Music" => new MusicView(this),
            "Controls" => new ControlsView(this),
            "Mixer" => new MixerView(this),
            "Bluetooth" => new BluetoothView(this),
            "Shelf" => new ShelfView(this),
            "Clipboard" => new ClipboardView(this),
            "Focus" => new FocusView(this),
            "Weather" => new WeatherView(this),
            "Teleprompter" => new TeleprompterView(this),
            "Capture" => new CaptureView(this),
            "Stopwatch" => new StopwatchView(this),
            "Stats" => new StatsView(this),
            _ => new HomeView(this),
        };
        _views[tab] = v;
        return v;
    }

    // =====================================================================
    // Public actions
    // =====================================================================
    public void Select(string tab)
    {
        if (!TabOn(tab)) tab = "Home";
        if (_mode != Mode.Open) { Open(tab); return; }
        var v = View(tab);
        if (!ReferenceEquals(v, _view))
        {
            _view?.OnHide();
            _view = v;
            _viewHost.Child = v;
        }
        _tab = tab;
        S.LastTab = tab;
        HighlightTab();
        v.OnShow();
        UpdateCaptureExclusion();
        Relayout();
        EnsureLoop();
    }

    public void Open(string? tab = null)
    {
        _openTimer.Stop();
        _mode = Mode.Open;
        _open.Visibility = Visibility.Visible;
        _transient.Visibility = Visibility.Hidden;
        RefreshHeader();
        Select(tab ?? (S.OpenLastTab ? S.LastTab : "Home"));
    }

    public void Collapse()
    {
        if (_mode == Mode.Closed) return;
        _closeTimer.Stop();
        if (_mode == Mode.Open) { _view?.OnHide(); }
        _mode = Mode.Closed;
        MakeNoActivate(true);
        UpdateClosed();
        UpdateCaptureExclusion();
        Retarget();
    }

    public void SetPinned(bool on)
    {
        _pinned = on;
        _pinGlyph.Foreground = on ? (Brush)FindResource("Accent") : Pal.Sub;
        if (!on && !_hover && _mode == Mode.Open) _closeTimer.Start();
    }

    public void Relayout() => Dispatcher.BeginInvoke(DispatcherPriority.Render, Retarget);

    public void Toast(string glyph, string text)
    {
        if (_mode != Mode.Open) { ShowAlert(Ui.Icon(glyph, 16), text, null, null, 2.2); return; }
        _toast.Child = Ui.H(8, Ui.Icon(glyph, 12), Ui.T(text, 12, FontWeights.SemiBold));
        _toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void FocusInput(Control c)
    {
        MakeNoActivate(false);
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { c.Focus(); Keyboard.Focus(c); });
    }

    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<TextBox>(d) is TextBox tb) FocusInput(tb);
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // =====================================================================
    // Window plumbing
    // =====================================================================
    void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        PickScreen();
        RegisterCloakHotkey();
        var topmost = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        topmost.Tick += (_, _) => { if (!_fsHidden) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); };
        topmost.Start();

        // React the moment another window comes to the front (the 1-second check is only a fallback).
        _fgProc = (_, _, hwnd, _, _, _, _) => OnForeground(hwnd);
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Closed += (_, _) => { if (_fgHook != IntPtr.Zero) Native.UnhookWinEvent(_fgHook); };
        OnForeground(Native.GetForegroundWindow());
    }

    static readonly HashSet<string> ShellClasses =
    [
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "#32768", "TaskListThumbnailWnd",
        "MultitaskingViewFrame", "ForegroundStaging", "Xaml_WindowedPopupClass", "Windows.UI.Input.InputSite.WindowClass",
    ];

    /// <summary>Decides whether an app is in front: the desktop and Brim's own windows don't count; taskbar and
    /// Start/flyouts keep the previous state.</summary>
    void OnForeground(IntPtr fg)
    {
        if (fg == _hwnd && fg != IntPtr.Zero) return;
        // When focus is on the taskbar, a flyout, a minimised window or nothing at all, stay tucked only
        // while some real app window is still open on this screen; otherwise go back to the regular notch.
        bool inFront = ClassifyForeground(fg) ?? (_appInFront && VisibleAppWindows().Count > 0);
        if (inFront == _appInFront) return;
        _appInFront = inFront;
        Retarget();
    }

    /// <summary>true = an app is in front, false = desktop or Brim, null = can't tell from the foreground alone.</summary>
    bool? ClassifyForeground(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return false;
        var cls = ClassOf(fg);
        if (cls is "Progman" or "WorkerW") return false;
        if (ShellClasses.Contains(cls) || !Native.IsWindowVisible(fg) || Native.IsIconic(fg) || Cloaked(fg)) return null;
        return true;
    }

    static string ClassOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(128);
        Native.GetClassName(h, sb, 128);
        return sb.ToString();
    }

    static bool Cloaked(IntPtr h) => Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;

    /// <summary>Normal app windows that are open (not minimised, hidden, cloaked or tool windows) on the notch's screen.</summary>
    public List<string> VisibleAppWindows()
    {
        var list = new List<string>();
        var mon = Native.MonitorFromWindow(_hwnd, 2);
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE)) != 0) return true;
            if (Native.GetWindowTextLength(h) == 0 || Cloaked(h)) return true;
            var cls = ClassOf(h);
            if (cls is "Progman" or "WorkerW" || ShellClasses.Contains(cls)) return true;
            if (!Native.GetWindowRect(h, out var r) || r.Width < 120 || r.Height < 80) return true;
            if (Native.MonitorFromWindow(h, 2) != mon) return true;
            var sb = new System.Text.StringBuilder(256);
            Native.GetWindowText(h, sb, 256);
            list.Add($"{sb} [{cls}]");
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>For the self-test: what the tuck logic currently sees.</summary>
    public string ForegroundReport()
    {
        var fg = Native.GetForegroundWindow();
        var c = ClassifyForeground(fg);
        return $"foreground class: {ClassOf(fg)} -> {(c == null ? "unclear" : c == true ? "app" : "desktop/Brim")}\n" +
               "open app windows counted:\n  " + string.Join("\n  ", VisibleAppWindows());
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0312 && w.ToInt32() == 0xB417) { App.ToggleCloak(); handled = true; }
        if (msg == 0x02E0) Dispatcher.BeginInvoke(() => { _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; Place(true); });
        return IntPtr.Zero;
    }

    void RegisterCloakHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.UnregisterHotKey(_hwnd, 0xB417);
        if (S.W("Cloak")) Native.RegisterHotKey(_hwnd, 0xB417, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x48);
    }

    void MakeNoActivate(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (on) Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE);
        else Native.AddExStyle(_hwnd, 0, Native.WS_EX_NOACTIVATE);
    }

    void PickScreen()
    {
        Screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == S.Display) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
    }

    public void UpdateCaptureExclusion()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool tele = _mode == Mode.Open && _tab == "Teleprompter" && S.TeleHide;
        bool exclude = tele || _captureTemp || App.Capture.Recording || S.Material == "LiquidGlass";
        Native.SetWindowDisplayAffinity(_hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    void OnSettingChanged(string name)
    {
        switch (name)
        {
            case "Widgets":
            case "TabOrder":
                RebuildTabs();
                if (_views.TryGetValue("Home", out var h) && h is HomeView hv) hv.Rebuild();
                RegisterCloakHotkey();
                if (!TabOn(_tab) && _mode == Mode.Open) Select("Home");
                break;
            case "StatTiles":
                if (_views.TryGetValue("Stats", out var st) && st is StatsView sv) sv.Build();
                break;
            case "QuickApps":
                if (_views.TryGetValue("Home", out var h2) && h2 is HomeView hv2) hv2.Rebuild();
                break;
            case "Display":
                PickScreen();
                break;
        }
        ApplySettings();
    }

    /// <summary>Applies appearance and geometry settings.</summary>
    public void ApplySettings()
    {
        _fx.Kind = S.Effect;
        _fx.Visibility = S.Effect == "None" ? Visibility.Collapsed : Visibility.Visible;
        _backdrop.Visibility = S.Material == "LiquidGlass" ? Visibility.Visible : Visibility.Collapsed;
        _rim.Visibility = S.Material == "LiquidGlass" ? Visibility.Visible : Visibility.Collapsed;
        UpdateCaptureExclusion();
        UpdateClosed();
        Retarget();
        Place(true);
    }

    Brush MaterialBrush(bool open)
    {
        if (!open && !S.ThemeClosed) return Pal.Brush(Pal.Col(S.RestingColor));
        return S.Material switch
        {
            "DarkGlass" => Pal.Hex("#E30A0A0C"),
            "Solid" => Pal.Brush(Pal.Col(open ? S.ExpandedColor : S.RestingColor)),
            "LiquidGlass" => Pal.Hex("#3A101014"),
            _ => open ? Pal.Hex("#A60C0C0E") : Pal.Hex("#D00A0A0C"),
        };
    }

    // =====================================================================
    // Closed (collapsed) content
    // =====================================================================
    void OnSecond()
    {
        UpdateClock();
        if (_mode == Mode.Open) { RefreshHeader(); _view?.Refresh(); }
        else UpdateClosed();
        if (_mode == Mode.Transient && !_hover && !_transientSticky && DateTime.Now > _transientUntil) { _mode = Mode.Closed; UpdateClosed(); Retarget(); }
        CheckFullscreen();
        OnForeground(Native.GetForegroundWindow());
    }

    string _clockKey = "", _closedSig = "", _materialKey = "";

    /// <summary>Redraws the clock only when the shown text changes. Returns true if it changed.</summary>
    bool UpdateClock()
    {
        var now = DateTime.Now;
        string day = S.DateMode switch { "Day" => now.ToString("ddd"), "Date" => now.ToString("d MMM"), _ => "" };
        var key = day + now.ToString(S.Clock24h ? "HH:mm" : "h:mm tt");
        if (key == _clockKey) return false;
        _clockKey = key;
        _clock.Inlines.Clear();
        if (day.Length > 0) _clock.Inlines.Add(new Run(day + "  ") { Foreground = Pal.Sub, FontSize = 12, FontWeight = FontWeights.SemiBold });
        if (S.Clock24h) _clock.Inlines.Add(new Run(now.ToString("HH:mm")) { Foreground = Pal.Text, FontSize = 13, FontWeight = FontWeights.Bold });
        else
        {
            _clock.Inlines.Add(new Run(now.ToString("h:mm")) { Foreground = Pal.Text, FontSize = 13, FontWeight = FontWeights.Bold });
            _clock.Inlines.Add(new Run(" " + now.ToString("tt")) { Foreground = Pal.Sub, FontSize = 10, FontWeight = FontWeights.SemiBold });
        }
        _vTime.Text = now.ToString(S.Clock24h ? "HH\nmm" : "h\nmm");
        return true;
    }

    /// <summary>Everything the closed notch shows, as text, so unchanged seconds cost nothing.</summary>
    string ClosedSignature()
    {
        var m = App.Media;
        var sys = App.Sys;
        bool morph = S.W("SmartMorph");
        var sb = new System.Text.StringBuilder();
        if (App.Capture.Recording) sb.Append("rec").Append(Fmt.Clock(DateTime.Now - App.Capture.RecordStart));
        else if (S.AgentActivity != "Off" && App.Agents.AnyWorking && (morph || S.AgentActivity == "Always")) sb.Append("agent");
        else if (morph && m.IsPlaying && S.W("NowPlaying")) sb.Append("art").Append(m.Art?.GetHashCode());
        else if (morph && App.Focus.Running) sb.Append("focus").Append(App.Focus.Mode).Append(Fmt.Clock(App.Focus.Remaining));
        else sb.Append(S.LeftSide).Append(S.LeftSide switch
        {
            "CPU" => Math.Round(sys.Cpu * 100).ToString(),
            "Memory" => Math.Round(sys.MemTotal > 0 ? 100.0 * sys.MemUsed / sys.MemTotal : 0).ToString(),
            "Network" => Fmt.Speed(sys.Down),
            "Focus" => App.Focus.NextTask,
            "Nothing" => "",
            _ => sys.HasBattery ? $"{sys.Battery}{sys.Plugged}{sys.Charging}" : Math.Round(sys.Cpu * 100).ToString(),
        });
        sb.Append('|').Append(sys.MicInUse || sys.CamInUse).Append(S.DownloadsIndicator && S.W("Downloads") && App.Downloads.Active > 0)
          .Append(S.ShelfCount && S.W("Shelf") ? App.Shelf.Items.Count : 0).Append(S.MusicVisualizer && S.W("MusicLights") && m.IsPlaying)
          .Append('|').Append(S.Style).Append(S.Edge).Append(S.NotchLength).Append(S.NotchThickness).Append(S.Scale).Append(S.HideUntilHovered)
          .Append(S.Material).Append(S.ThemeClosed).Append(S.RestingColor).Append(_mode).Append(_hover);
        return sb.ToString();
    }

    void UpdateClosed()
    {
        bool clockChanged = UpdateClock();
        var sig = ClosedSignature();
        if (sig == _closedSig && !clockChanged) return;
        _closedSig = sig;
        bool morph = S.W("SmartMorph");
        var m = App.Media;
        var sys = App.Sys;
        _left.Children.Clear();
        _right.Children.Clear();
        foreach (FrameworkElement e in new FrameworkElement[] { _recDot, _recText, _leftText, _closedFace, _closedArt, _closedRing, _privacyDot, _downloadGlyph, _shelfBadge, _closedBars })
            if (e.Parent is Panel p) p.Children.Remove(e);

        void L(UIElement e) => _left.Children.Add(e);
        void R(UIElement e) { if (_right.Children.Count > 0) _right.Children.Add(new Border { Width = 7 }); _right.Children.Add(e); }

        if (App.Capture.Recording)
        {
            _recText.Text = Fmt.Clock(DateTime.Now - App.Capture.RecordStart);
            L(Ui.H(6, _recDot, _recText));
        }
        else if (S.AgentActivity != "Off" && App.Agents.AnyWorking && (morph || S.AgentActivity == "Always"))
            L(_closedFace);
        else if (morph && m.IsPlaying && S.W("NowPlaying"))
        {
            _closedArt.Child = m.Art != null ? new Image { Source = m.Art, Stretch = Stretch.UniformToFill } : Ui.Icon(Ic.Music, 11);
            _closedArt.Background = Pal.Track;
            L(_closedArt);
        }
        else if (morph && App.Focus.Running)
        {
            _leftText.Text = Fmt.Clock(App.Focus.Remaining);
            L(Ui.H(5, Ui.Icon(Ic.Cup, 12, App.Focus.Mode == "Focus" ? Pal.Orange : Pal.Green), _leftText));
        }
        else
        {
            switch (S.LeftSide)
            {
                case "CPU": _leftText.Text = $"{Math.Round(sys.Cpu * 100)}%"; L(Ui.H(5, Ui.Icon(Ic.Chip, 12, Pal.Blue), _leftText)); break;
                case "Memory": _leftText.Text = $"{Math.Round(sys.MemTotal > 0 ? 100.0 * sys.MemUsed / sys.MemTotal : 0)}%"; L(Ui.H(5, Ui.Icon(Ic.Ram, 12, Pal.Purple), _leftText)); break;
                case "Network": _leftText.Text = Fmt.Speed(sys.Down); L(Ui.H(4, Ui.Icon(Ic.Down, 10, Pal.Green), _leftText)); break;
                case "Focus":
                    var task = App.Focus.NextTask;
                    if (task.Length > 0) { _leftText.Text = task.Length > 18 ? task[..17] + "…" : task; L(Ui.H(5, Ui.Icon(Ic.Check, 11, Pal.Green), _leftText)); }
                    break;
                case "Nothing": break;
                default:
                    if (sys.HasBattery) { PaintRing(_closedRing); L(_closedRing); }
                    else { _leftText.Text = $"{Math.Round(sys.Cpu * 100)}%"; L(Ui.H(5, Ui.Icon(Ic.Chip, 12, Pal.Blue), _leftText)); }
                    break;
            }
        }

        if (sys.MicInUse || sys.CamInUse) R(_privacyDot);
        if (S.DownloadsIndicator && S.W("Downloads") && App.Downloads.Active > 0) R(_downloadGlyph);
        if (S.ShelfCount && S.W("Shelf") && App.Shelf.Items.Count > 0)
        {
            ((TextBlock)_shelfBadge.Child).Text = App.Shelf.Items.Count.ToString();
            R(Ui.H(4, Ui.Icon(Ic.Box, 11, Pal.Sub), _shelfBadge));
        }
        if (S.MusicVisualizer && S.W("MusicLights") && m.IsPlaying) R(_closedBars);
        Retarget();
    }

    void PaintRing(Ring r)
    {
        var s = App.Sys;
        double v = s.HasBattery ? s.Battery / 100.0 : 1;
        var color = !s.HasBattery || s.Plugged ? Pal.Green : s.Battery <= 20 ? Pal.Red : s.Battery <= 40 ? Pal.Orange : Pal.Green;
        r.Set(v, color, s.Charging || s.Plugged ? Ic.Bolt : null);
    }

    void RefreshHeader()
    {
        var sys = App.Sys;
        _hdrPrivacy.Visibility = sys.MicInUse || sys.CamInUse ? Visibility.Visible : Visibility.Collapsed;
        _hdrBattery.Text = sys.HasBattery ? $"{sys.Battery}%" : "";
        PaintRing(_hdrRing);
        var w = App.Weather;
        if (_weatherBtn != null) _weatherBtn.Visibility = S.W("Weather") ? Visibility.Visible : Visibility.Collapsed;
        if (w.Ready)
        {
            _hdrWeather.Text = $"{Math.Round(w.Temp)}{w.Unit}";
            _hdrWeatherIcon.Child = new WeatherIcon(w.Code, w.IsDay, 12);
        }
        bool working = App.Agents.AnyWorking;
        bool attention = App.Agents.Sessions.Any(s => s.State == "attention" && DateTime.Now - s.Updated < TimeSpan.FromMinutes(10));
        _agentDot.Visibility = working || attention ? Visibility.Visible : Visibility.Collapsed;
        _agentDot.Background = attention ? Pal.Red : Pal.Orange;
    }

    // =====================================================================
    // Transient content: OSD and alerts
    // =====================================================================
    void ShowOsd(string kind)
    {
        if (_mode == Mode.Open || _fsHidden) return;
        double v;
        string glyph;
        if (kind == "volume")
        {
            v = App.Audio.Muted ? 0 : App.Audio.Volume;
            glyph = App.Audio.Muted || v == 0 ? Ic.Mute : Ic.Speaker;
        }
        else
        {
            if (App.Sys.Brightness is not int b) return;
            v = b / 100.0;
            glyph = Ic.Sun;
        }
        var meter = new Meter(Pal.White, 5) { Value = v, Width = 150 };
        var g = Ui.G("Auto,*,Auto", "", Ui.Icon(glyph, 14), meter.M(12, 0, 12, 0).At(1), Ui.T($"{Math.Round(v * 100)}", 13, FontWeights.SemiBold).At(2));
        g.Margin = new Thickness(18, 0, 18, 0);
        g.Height = Math.Max(34, ClosedThickness() + 6);
        _transient.Tag = kind;
        ShowTransient(g, 1.6, false);
    }

    void ShowBatteryAlert(bool plugged, bool low = false)
    {
        var ring = new Ring(26, 3);
        var s = App.Sys;
        ring.Set(s.Battery / 100.0, low ? Pal.Red : Pal.Green, plugged ? Ic.Bolt : null);
        var title = low ? "Low battery" : plugged ? "Charging" : "On battery";
        var g = Ui.G("Auto,*,Auto", "", ring, Ui.T(title, 14, FontWeights.SemiBold).M(12, 0, 24, 0).At(1), Ui.Big($"{s.Battery}%", 17, FontWeights.SemiBold, low ? Pal.Red : Pal.Green).At(2));
        g.Margin = new Thickness(14, 8, 18, 8);
        _transient.Tag = null;
        ShowTransient(g, 3, false);
    }

    public void ShowAlert(FrameworkElement icon, string title, string? body, (string label, Action act)? action, double seconds = 5)
    {
        if (_mode == Mode.Open)
        {
            _toast.Child = Ui.H(8, Ui.T(title, 12, FontWeights.SemiBold));
            _toast.Visibility = Visibility.Visible;
            _toastTimer.Stop(); _toastTimer.Start();
            return;
        }
        var text = Ui.V(3, Ui.T(title, 14, FontWeights.SemiBold), string.IsNullOrWhiteSpace(body) ? null : Ui.T(body!, 13, null, Pal.Sub).Wrap());
        if (text.Children.Count > 1 && text.Children[^1] is TextBlock bt) { bt.MaxWidth = 360; bt.MaxHeight = 54; }
        var buttons = Ui.H(8,
            action is { } a ? Ui.Pill(a.label, null, () => { a.act(); EndTransient(); }, true, 30, 12) : null,
            Ui.Pill("OK", null, EndTransient, false, 30, 12));
        var g = Ui.G("Auto,*", "Auto,Auto", icon.VA(VerticalAlignment.Top).M(0, 2, 0, 0), text.M(14, 0, 0, 0).At(1), buttons.M(14, 12, 0, 0).At(1, 1));
        g.Margin = new Thickness(18, Math.Max(14, ClosedThickness() * 0.5), 18, 16);
        g.MaxWidth = 470;
        g.MinWidth = 320;
        _transient.Tag = null;
        ShowTransient(g, seconds, false);
    }

    /// <summary>Shows an agent's permission request as a card in the notch (or on the Sidekick tab if open).</summary>
    public void ShowApproval(Approval a)
    {
        if (_mode == Mode.Open) { Select("Agents"); return; }
        _transientApproval = a;
        _transient.Tag = null;
        ShowTransient(AgentUi.ApprovalCard(a, true), 115, true, force: true);
    }

    void ShowTransient(FrameworkElement content, double seconds, bool sticky, bool force = false)
    {
        if (_mode == Mode.Open) return;
        if (!force && (_fsHidden || _transientApproval != null)) return;
        if (_fsHidden) { _fsHidden = false; Visibility = Visibility.Visible; Place(true); }
        _transient.Child = content;
        _transientUntil = DateTime.Now.AddSeconds(seconds);
        _transientSticky = sticky;
        _mode = Mode.Transient;
        _transient.Visibility = Visibility.Visible;
        Retarget();
    }

    void EndTransient()
    {
        _transientApproval = null;
        if (_mode != Mode.Transient) return;
        _mode = Mode.Closed;
        UpdateClosed();
        Retarget();
    }

    // =====================================================================
    // Geometry and animation
    // =====================================================================
    double Scale => Math.Clamp(S.Scale, 0.6, 1.6);
    double ClosedThickness() => Math.Clamp(S.NotchThickness, 22, 72) * Scale;
    bool Vertical => S.Edge is "Left" or "Right";
    Rect MonitorLogical()
    {
        var b = Screen.Bounds;
        return new Rect(b.Left / _dpi, b.Top / _dpi, b.Width / _dpi, b.Height / _dpi);
    }

    /// <summary>Recomputes target size for the current mode.</summary>
    void Retarget()
    {
        bool squared = S.Corners == "Squared";
        if (_mode == Mode.Open)
        {
            double cw = Math.Max(612, MeasureHeaderWidth());
            _open.Width = cw + 36;
            _open.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _tw = _open.DesiredSize.Width;
            _th = _open.DesiredSize.Height;
            _tr = squared ? 8 : 24;
        }
        else if (_mode == Mode.Transient)
        {
            _transient.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _tw = Math.Max(_transient.DesiredSize.Width, S.NotchLength * Scale);
            _th = Math.Max(_transient.DesiredSize.Height, ClosedThickness());
            _tr = squared ? 6 : Math.Min(22, _th / 2);
        }
        else
        {
            double t = ClosedThickness();
            if (Vertical)
            {
                _tw = t;
                _th = Math.Max(70, S.NotchLength * Scale * 0.45);
                _tr = squared ? 4 : t / 2;
            }
            else if (S.Style == "Bar")
            {
                _tw = MonitorLogical().Width;
                _th = t * 0.92;
                _tr = 0;
            }
            else
            {
                _closed.Measure(new Size(double.PositiveInfinity, t));
                double natural = _closed.DesiredSize.Width + 8;
                _tw = Math.Max(Math.Clamp(S.NotchLength, 120, 600) * Scale, natural);
                _th = t;
                _tr = squared ? 5 : S.Style == "Pill" ? t / 2 : Math.Min(12, t * 0.42);
            }
        }
        // Tucked: shrink to a slim handle with an invisible, wider hover strip around it.
        bool tucked = Tucked;
        if (tucked)
        {
            if (Vertical) { _tw = 10; _th = Math.Max(_th, 170); }
            else { _tw = S.Style == "Bar" ? MonitorLogical().Width : Math.Max(_tw, 220) + 60; _th = 10; }
            _tr = 5;
            _handle.HorizontalAlignment = S.Edge switch { "Left" => HorizontalAlignment.Left, "Right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
            _handle.VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Top;
            _handle.Width = Vertical ? 6 : 70 * Scale;
            _handle.Height = Vertical ? 70 * Scale : 6;
            _handle.Margin = S.Edge switch { "Left" => new Thickness(2, 0, 0, 0), "Right" => new Thickness(0, 0, 2, 0), _ => new Thickness(0, 2, 0, 0) };
            _handle.Background = App.Capture.Recording ? Pal.Red : App.Agents.AnyWorking ? Pal.Orange : Pal.Hex("#E6222226");
        }
        _handle.Visibility = tucked ? Visibility.Visible : Visibility.Collapsed;
        _toff = 0;
        if (tucked != _tucked) { _tucked = tucked; _envW = 0; _envH = 0; }
        var materialKey = $"{S.Material}|{S.ThemeClosed}|{S.RestingColor}|{S.ExpandedColor}|{_mode != Mode.Closed}|{tucked}";
        if (materialKey != _materialKey) { _materialKey = materialKey; _island.Background = tucked ? Pal.HitClear : MaterialBrush(_mode != Mode.Closed); }
        _closed.Visibility = !Vertical || _mode != Mode.Closed ? Visibility.Visible : Visibility.Collapsed;
        _closedV.Visibility = Vertical ? Visibility.Visible : Visibility.Collapsed;
        _closed.IsHitTestVisible = false;
        _open.IsHitTestVisible = _mode == Mode.Open;
        _transient.IsHitTestVisible = _mode == Mode.Transient;
        Place(false);
        EnsureLoop();
    }

    double MeasureHeaderWidth()
    {
        if (_open.Children.Count == 0) return 0;
        var header = _open.Children[0] as FrameworkElement;
        if (header == null) return 0;
        header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return header.DesiredSize.Width - 30;
    }

    /// <summary>Sizes and positions the window envelope around the island.</summary>
    void Place(bool force)
    {
        if (_hwnd == IntPtr.Zero) return;
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double envW = Math.Ceiling(Math.Max(_w, _tw)) + 4, envH = Math.Ceiling(Math.Max(_h, _th)) + 12;
        if (!force && envW <= _envW && envH <= _envH && !_dragging) return;
        _envW = envW; _envH = envH;
        var m = Screen.Bounds;
        int x, y, w = (int)Math.Ceiling(envW * _dpi), h = (int)Math.Ceiling(envH * _dpi);
        double pillTop = S.Style == "Pill" && !Vertical && !_tucked ? 6 * _dpi : 0;
        switch (S.Edge)
        {
            case "Left":
                x = m.Left;
                y = (int)Math.Clamp(m.Top + S.EdgeOffset * m.Height - h / 2.0, m.Top, m.Bottom - h);
                break;
            case "Right":
                x = m.Right - w;
                y = (int)Math.Clamp(m.Top + S.EdgeOffset * m.Height - h / 2.0, m.Top, m.Bottom - h);
                break;
            default:
                if (S.Style == "Bar") w = Math.Max(w, m.Width);
                double anchor = S.Style == "Bar" ? m.Left + m.Width / 2.0 : m.Left + S.EdgeOffset * m.Width;
                x = (int)Math.Clamp(anchor - w / 2.0, m.Left, m.Right - w);
                y = m.Top + (int)pillTop;
                h += (int)pillTop;
                break;
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | (_fsHidden ? 0 : Native.SWP_SHOWWINDOW));
        _winX = x; _winY = y; _winW = w; _winH = h;
        ApplyFrame();
    }

    int _winX, _winY, _winW, _winH;

    /// <summary>Applies the current animated values to the island.</summary>
    (double, double, double, double, double, double, double, string) _lastFrame;

    void ApplyFrame()
    {
        // Skip frames where nothing moved: any change here repaints the whole layered window.
        var key = (Math.Round(_w, 1), Math.Round(_h, 1), Math.Round(_r, 1), Math.Round(_off, 1), _open.ActualWidth, _transient.ActualWidth,
                   _winX * 100000.0 + _winW, S.Edge + S.Style + _winY + _winH);
        if (key == _lastFrame) return;
        _lastFrame = key;
        _island.Width = Math.Max(1, _w);
        _island.Height = Math.Max(1, _h);
        var m = Screen.Bounds;
        double winW = _winW / _dpi, winH = _winH / _dpi;
        double left, top;
        switch (S.Edge)
        {
            case "Left":
                left = _off; top = Center(m.Top + S.EdgeOffset * m.Height, m.Top, m.Bottom, _winY, _h, winH);
                break;
            case "Right":
                left = winW - _w - _off; top = Center(m.Top + S.EdgeOffset * m.Height, m.Top, m.Bottom, _winY, _h, winH);
                break;
            default:
                double anchor = S.Style == "Bar" ? m.Left + m.Width / 2.0 : m.Left + S.EdgeOffset * m.Width;
                left = Center(anchor, m.Left, m.Right, _winX, _w, winW);
                top = _off;
                break;
        }
        Canvas.SetLeft(_island, left);
        Canvas.SetTop(_island, top);
        _island.Clip = Shape(_w, _h, _r);
        _rim.CornerRadius = S.Edge switch
        {
            "Left" => new CornerRadius(0, _r, _r, 0),
            "Right" => new CornerRadius(_r, 0, 0, _r),
            _ => S.Style == "Pill" ? new CornerRadius(_r) : new CornerRadius(0, 0, _r, _r),
        };
        _rim.BorderThickness = new Thickness(1);
        // content placement inside the island
        Canvas.SetLeft(_open, (_w - _open.ActualWidth) / 2);
        Canvas.SetTop(_open, Vertical ? (_h - _open.ActualHeight) / 2 : 0);
        Canvas.SetLeft(_transient, (_w - _transient.ActualWidth) / 2);
        Canvas.SetTop(_transient, Vertical ? (_h - _transient.ActualHeight) / 2 : 0);
    }

    double Center(double anchorPhys, double min, double max, int winOrigin, double size, double winSize)
    {
        double half = size * _dpi / 2;
        double c = Math.Clamp(anchorPhys, min + half, max - half);
        return Math.Clamp((c - winOrigin) / _dpi - size / 2, 0, Math.Max(0, winSize - size));
    }

    Geometry Shape(double w, double h, double r)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
        if (S.Style == "Pill" && !Vertical) return new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            var size = new Size(r, r);
            switch (S.Edge)
            {
                case "Left":
                    c.BeginFigure(new Point(0, 0), true, true);
                    c.LineTo(new Point(w - r, 0), false, false);
                    c.ArcTo(new Point(w, r), size, 0, false, SweepDirection.Clockwise, false, false);
                    c.LineTo(new Point(w, h - r), false, false);
                    c.ArcTo(new Point(w - r, h), size, 0, false, SweepDirection.Clockwise, false, false);
                    c.LineTo(new Point(0, h), false, false);
                    break;
                case "Right":
                    c.BeginFigure(new Point(w, 0), true, true);
                    c.LineTo(new Point(w, h), false, false);
                    c.LineTo(new Point(r, h), false, false);
                    c.ArcTo(new Point(0, h - r), size, 0, false, SweepDirection.Clockwise, false, false);
                    c.LineTo(new Point(0, r), false, false);
                    c.ArcTo(new Point(r, 0), size, 0, false, SweepDirection.Clockwise, false, false);
                    break;
                default:
                    c.BeginFigure(new Point(0, 0), true, true);
                    c.LineTo(new Point(w, 0), false, false);
                    c.LineTo(new Point(w, h - r), false, false);
                    c.ArcTo(new Point(w - r, h), size, 0, false, SweepDirection.Clockwise, false, false);
                    c.LineTo(new Point(r, h), false, false);
                    c.ArcTo(new Point(0, h - r), size, 0, false, SweepDirection.Clockwise, false, false);
                    break;
            }
        }
        g.Freeze();
        return g;
    }

    void EnsureLoop()
    {
        if (_loopOn) return;
        _loopOn = true;
        _lastT = _sw.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnRendering;
    }

    static bool Spring(ref double x, ref double v, double target, double dt, double k, double c)
    {
        if (Math.Abs(target - x) < 0.25 && Math.Abs(v) < 0.5) { x = target; v = 0; return false; }
        int steps = (int)Math.Ceiling(dt / 0.008);
        double h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            double a = k * (target - x) - c * v;
            v += a * h;
            x += v * h;
        }
        return true;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        double now = _sw.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastT, 0, 0.05);
        _lastT = now;
        bool opening = _mode != Mode.Closed;
        // stiff, lightly damped springs: settles in ~0.2 s with a hint of bounce
        double k = opening ? 560 : 700, c = opening ? 40 : 54;
        bool moving = Spring(ref _w, ref _vw, _tw, dt, k, c);
        moving |= Spring(ref _h, ref _vh, _th, dt, k, c);
        moving |= Spring(ref _r, ref _vr, _tr, dt, k, c);
        moving |= Spring(ref _off, ref _voff, _toff, dt, 420, 42);
        ApplyFrame();

        // cross-fade content
        double fade = Math.Min(1, dt * 22);
        double progress = _th > 1 ? Math.Clamp(_h / _th, 0, 1.2) : 1;
        Fade(_closed, _mode == Mode.Closed && !_tucked ? 1 : 0, fade * 1.4);
        if (Vertical) Fade(_closedV, _mode == Mode.Closed && !_tucked ? 1 : 0, fade * 1.4);
        Fade(_open, _mode == Mode.Open && progress > 0.3 ? 1 : 0, fade);
        Fade(_transient, _mode == Mode.Transient && progress > 0.3 ? 1 : 0, fade);
        if (_open.Opacity < 0.01 && _mode != Mode.Open) _open.Visibility = Visibility.Hidden;
        if (_transient.Opacity < 0.01 && _mode != Mode.Transient) _transient.Visibility = Visibility.Hidden;
        moving |= Math.Abs(_open.Opacity - (_mode == Mode.Open ? 1 : 0)) > 0.01 || Math.Abs(_closed.Opacity - (_mode == Mode.Closed && !_tucked ? 1 : 0)) > 0.01
                  || Math.Abs(_transient.Opacity - (_mode == Mode.Transient ? 1 : 0)) > 0.01;

        // 30 fps consumers
        _frameAcc += dt;
        bool effect = _fx.Visibility == Visibility.Visible && (_mode != Mode.Closed || S.ThemeClosed) && !_fsHidden;
        bool bars = _mode == Mode.Closed && _closedBars.Parent != null && App.Media.IsPlaying;
        bool view = _mode == Mode.Open && _view?.WantsFrames == true;
        bool face = _mode == Mode.Closed && _closedFace.Parent != null;
        bool liquid = S.Material == "LiquidGlass" && !_fsHidden;
        _viewAcc += dt;
        if (_frameAcc >= 1 / 30.0)
        {
            if (effect) _fx.Advance(_frameAcc);
            if (bars) _closedBars.Set(App.Audio.Bands(5).Select(b => 0.25 + b * 0.75).ToArray());
            if (face) { bool blink = (now % 4) < 0.15; if (blink != _closedFace.Blink) { _closedFace.Blink = blink; _closedFace.InvalidateVisual(); } }
            _frameAcc = 0;
        }
        // views (progress bars, lyrics, timers) only need ~12 updates a second
        if (view && _viewAcc >= 1 / 12.0) { _view!.Frame(_viewAcc); _viewAcc = 0; }
        if (liquid)
        {
            _backdropAcc += dt;
            if (_backdropAcc > 0.35 && !moving) { _backdropAcc = 0; RefreshBackdrop(); }
        }
        if (_downloadGlyph.Parent != null) _downloadMove.Y = Math.Round(Math.Sin(now * 5) * 2, 1);

        if (!moving)
        {
            // shrink the envelope once settled
            double envW = Math.Ceiling(_tw) + 4, envH = Math.Ceiling(_th) + 12;
            if (envW < _envW - 1 || envH < _envH - 1) { _envW = 0; _envH = 0; Place(true); }
        }
        bool downloads = _downloadGlyph.Parent != null;
        if (!moving && !effect && !bars && !view && !face && !liquid && !downloads)
        {
            CompositionTarget.Rendering -= OnRendering;
            _loopOn = false;
        }
    }

    static void Fade(UIElement e, double target, double rate)
    {
        if (target > 0 && e.Visibility != Visibility.Visible) e.Visibility = Visibility.Visible;
        e.Opacity += (target - e.Opacity) * Math.Min(1, rate);
        if (Math.Abs(e.Opacity - target) < 0.01) e.Opacity = target;
    }

    void RefreshBackdrop()
    {
        try
        {
            var p = _island.PointToScreen(new Point(0, 0));
            int w = (int)(_island.ActualWidth * _dpi), h = (int)(_island.ActualHeight * _dpi);
            if (w < 4 || h < 4) return;
            using var bmp = CaptureService.Grab(new System.Drawing.Rectangle((int)p.X, (int)p.Y, w, h));
            var hb = bmp.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _backdrop.Source = src;
            }
            finally { DeleteObject(hb); }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    // =====================================================================
    // Behaviour helpers
    // =====================================================================
    bool CursorInside(double slack)
    {
        if (!Native.GetCursorPos(out var p)) return false;
        try
        {
            var tl = _island.PointToScreen(new Point(0, 0));
            var br = _island.PointToScreen(new Point(_island.ActualWidth, _island.ActualHeight));
            return p.X >= tl.X - slack && p.X <= br.X + slack && p.Y >= tl.Y - slack && p.Y <= br.Y + slack;
        }
        catch { return false; }
    }

    bool StayOpen() =>
        _pinned || S.KeepOpen || _dragOver || CursorInside(6) || Ui.OpenMenus > 0 ||
        (IsActive && Keyboard.FocusedElement is TextBox) || (_view is TeleprompterView && App.Focus != null && _pinned);

    void OnIslandMouseMove(object sender, MouseEventArgs e)
    {
        if (_mouseDown && !S.LockPosition && _mode != Mode.Open)
        {
            var p = PointToScreen(e.GetPosition(this));
            if (!_dragging && Math.Abs(p.X - _downAt.X) + Math.Abs(p.Y - _downAt.Y) > 8) _dragging = true;
            if (_dragging)
            {
                var m = Screen.Bounds;
                double dTop = p.Y - m.Top, dLeft = p.X - m.Left, dRight = m.Right - p.X;
                string edge = dTop <= Math.Min(dLeft, dRight) || dTop < 80 ? "Top" : dLeft < dRight ? "Left" : "Right";
                S.Edge = edge;
                S.EdgeOffset = edge == "Top" ? Math.Clamp((p.X - m.Left) / m.Width, 0, 1) : Math.Clamp((p.Y - m.Top) / m.Height, 0, 1);
                Retarget();
                Place(true);
            }
            return;
        }
        // shake the pointer inside the open notch to cloak the screen
        if (_mode == Mode.Open && S.W("Cloak"))
        {
            double x = e.GetPosition(this).X, t = _sw.Elapsed.TotalSeconds;
            _shake.Add((x, t));
            _shake.RemoveAll(s => t - s.t > 0.7);
            int reversals = 0;
            for (int i = 2; i < _shake.Count; i++)
            {
                double a = _shake[i - 1].x - _shake[i - 2].x, b = _shake[i].x - _shake[i - 1].x;
                if (Math.Abs(a) > 4 && Math.Abs(b) > 4 && Math.Sign(a) != Math.Sign(b)) reversals++;
            }
            if (reversals >= 5 && _shake.Max(s => s.x) - _shake.Min(s => s.x) > 60) { _shake.Clear(); Collapse(); App.ToggleCloak(); }
        }
    }

    void CheckFullscreen()
    {
        bool hide = false;
        if (S.HideFullscreen && !App.SnapshotMode && _transientApproval == null)
        {
            var fg = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fg, out var fgPid);
            // Only true fullscreen (games, F11 video, slideshows) hides the notch. A maximized window
            // also covers the whole monitor when the taskbar auto-hides, so it must not count.
            bool candidate = fg != IntPtr.Zero && fg != _hwnd && fgPid != Environment.ProcessId && !Native.IsZoomed(fg)
                             && fg != Native.GetShellWindow() && fg != Native.GetDesktopWindow();
            if (Native.SHQueryUserNotificationState(out var quns) == 0 && quns is 3 or 4) candidate = fg != IntPtr.Zero && fgPid != Environment.ProcessId;
            if (candidate)
            {
                var sb = new System.Text.StringBuilder(64);
                Native.GetClassName(fg, sb, 64);
                var cls = sb.ToString();
                if (cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd") && Native.GetWindowRect(fg, out var r))
                {
                    var mon = Native.MonitorFromWindow(fg, 2);
                    var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
                    if (Native.GetMonitorInfo(mon, ref mi) && r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                        r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom && mi.rcMonitor.Left == Screen.Bounds.Left && mi.rcMonitor.Top == Screen.Bounds.Top)
                        hide = true;
                }
            }
        }
        if (hide == _fsHidden) return;
        _fsHidden = hide;
        if (hide) { Collapse(); Visibility = Visibility.Hidden; }
        else { Visibility = Visibility.Visible; Place(true); }
    }

    // =====================================================================
    // Capture, overlays
    // =====================================================================
    public async void Screenshot(bool area)
    {
        Collapse();
        _captureTemp = true;
        UpdateCaptureExclusion();
        await Task.Delay(260);
        try
        {
            using var full = CaptureService.Grab(Screen.Bounds);
            System.Drawing.Bitmap? shot = full;
            if (area)
            {
                var rect = await AreaSelectWindow.PickAsync(Screen, full);
                shot = rect is System.Drawing.Rectangle r && r.Width > 2 && r.Height > 2 ? full.Clone(r, full.PixelFormat) : null;
            }
            if (shot != null)
            {
                var path = CaptureService.Save(shot);
                if (!ReferenceEquals(shot, full)) shot.Dispose();
                ShowAlert(Ui.Icon(Ic.Camera, 18), "Screenshot saved", "Copied to the clipboard, ready to paste.", ("Show", () => Shell.Reveal(path)), 4);
            }
        }
        catch (Exception ex) { App.Log("screenshot: " + ex); }
        finally { _captureTemp = false; UpdateCaptureExclusion(); }
    }

    public async void ToggleRecording(bool? area)
    {
        if (App.Capture.Recording)
        {
            var path = await App.Capture.StopRecordingAsync();
            UpdateCaptureExclusion();
            if (path != null && File.Exists(path))
                ShowAlert(Ui.Icon("", 18, Pal.Red), "Recording saved", Path.GetFileName(path), ("Show", () => Shell.Reveal(path)), 5);
            else ShowAlert(Ui.Icon(Ic.Warning, 18, Pal.Orange), "Recording failed", "The recording could not be saved.", null, 4);
            return;
        }
        Collapse();
        System.Drawing.Rectangle? region = null;
        if (area == true)
        {
            await Task.Delay(200);
            region = await AreaSelectWindow.PickAsync(Screen, null);
            if (region == null) return;
            await Task.Delay(150);
        }
        _captureTemp = true;
        UpdateCaptureExclusion();
        var started = await App.Capture.StartRecordingAsync(Screen.Bounds, region, S.RecordSystemSound, S.RecordMic);
        _captureTemp = false;
        UpdateCaptureExclusion();
        if (started == null) ShowAlert(Ui.Icon(Ic.Warning, 18, Pal.Orange), "Couldn't start recording", "Screen capture isn't available right now.", null, 4);
    }

    public void ToggleEdgeLight() => App.ToggleEdgeLight(Screen);

    public void EdgeLightPopup(FrameworkElement target)
    {
        var width = Ui.Slider(12, 120, S.EdgeLightWidth, v => { S.EdgeLightWidth = v; S.Changed("EdgeLight"); App.RefreshEdgeLight(); });
        var warm = Ui.Slider(0, 1, S.EdgeLightWarmth, v => { S.EdgeLightWarmth = v; S.Changed("EdgeLight"); App.RefreshEdgeLight(); }, true, new LinearGradientBrush(Color.FromRgb(200, 220, 255), Color.FromRgb(255, 190, 120), 0));
        var panel = new Border
        {
            Width = 240, Padding = new Thickness(16, 12, 16, 14), CornerRadius = new CornerRadius(14), Background = Pal.Hex("#F21E1E22"),
            BorderBrush = Pal.Line, BorderThickness = new Thickness(1),
            Child = Ui.V(8, Ui.T("Ring Light", 14, FontWeights.SemiBold), Ui.T("Width", 12, null, Pal.Sub), width, Ui.T("Warmth", 12, null, Pal.Sub), warm),
        };
        var pop = new Popup { Child = panel, PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 6 };
        Ui.TrackPopup(pop);
        pop.IsOpen = true;
    }

    public void OpenFullscreenMusic()
    {
        Collapse();
        new FullscreenMusicWindow().Show();
    }
}
