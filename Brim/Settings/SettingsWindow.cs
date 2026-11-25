using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Brim.Core;
using Brim.Notch;
using Brim.Notch.Views;
using Brim.Services;
using Forms = System.Windows.Forms;

namespace Brim.Settings;

public sealed class SettingsWindow : Window
{
    static AppSettings S => App.S;
    static readonly (string key, string glyph, string label)[] Pages =
    [
        ("General", Ic.Sliders, "General"), ("Appearance", "\uE790", "Appearance"), ("Widgets", Ic.Grid4, "Widgets"),
        ("Agents", Ic.Robot, "Agents"), ("Tips", Ic.Book, "Tips"), ("WhatsNew", "\uE734", "What's new"), ("About", Ic.Info, "About"),
    ];

    readonly StackPanel _nav = new();
    readonly TextBlock _title = Ui.Big("General", 26, FontWeights.SemiBold);
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    public StackPanel Page { get; private set; } = new();
    public string Current { get; private set; } = "General";

    public SettingsWindow()
    {
        Title = "Brim Settings";
        Width = 860; Height = 600;
        MinWidth = 720; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(6), UseAeroCaptionButtons = false });
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => Backdrop();

        var root = new Grid { Background = Pal.Hex("#B8151518") };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(222) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // sidebar
        var logo = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = Pal.Hex("#1E1E24"), Child = new Border { Width = 22, Height = 9, CornerRadius = new CornerRadius(5), Background = Pal.Black, BorderBrush = Pal.Hex("#6E7BFF"), BorderThickness = new Thickness(1.2) } };
        var brand = Ui.H(10, logo, Ui.V(0, Ui.T("Brim", 15, FontWeights.SemiBold), Ui.T("Settings", 12, null, Pal.Sub)).VA(VerticalAlignment.Center));
        brand.Margin = new Thickness(18, 18, 0, 18);
        foreach (var (key, glyph, label) in Pages) _nav.Children.Add(NavItem(key, glyph, label));
        var footer = Ui.G("*,Auto", "", Ui.T("Version 1.0.0", 12, null, Pal.Dim), Ui.T("Everything included", 12, null, Pal.Dim).At(1));
        footer.Margin = new Thickness(18, 0, 16, 16);
        var side = Ui.G("*", "Auto,*,Auto", brand, _nav.At(0, 1), footer.At(0, 2));
        var sideBorder = new Border { Background = Pal.Hex("#40000000"), Child = side };
        // Drag the window from empty sidebar space only; nav items mark their clicks handled.
        sideBorder.MouseLeftButtonDown += (_, e) => { if (!e.Handled && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        root.Children.Add(sideBorder);

        // content
        var close = Ui.Circle(Ic.Close, Close, 34, 12, fg: Pal.Sub, tip: "Close");
        var min = Ui.Circle(Ic.Remove, () => WindowState = WindowState.Minimized, 34, 12, fg: Pal.Sub, tip: "Minimize");
        var head = Ui.G("*,Auto", "", _title, Ui.H(4, min, close).VA(VerticalAlignment.Top).At(1));
        head.Margin = new Thickness(30, 20, 14, 10);
        head.Background = Pal.HitClear;
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        _scroll.Padding = new Thickness(30, 0, 22, 24);
        var content = Ui.G("*", "Auto,*", head, _scroll.At(0, 1));
        root.Children.Add(content.At(1));
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Navigate("General");
    }

    void Backdrop()
    {
        var h = new WindowInteropHelper(this).Handle;
        int dark = 1, backdrop = 3, corner = 2;
        Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
        if (Native.DwmSetWindowAttribute(h, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4) != 0)
            Background = Pal.Hex("#FF18181C");
        else if (HwndSource.FromHwnd(h) is HwndSource src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
    }

    Border NavItem(string key, string glyph, string label)
    {
        var bar = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(2), Background = Pal.Clear, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0, 0, 0) };
        bar.SetResourceReference(Border.BackgroundProperty, "Accent");
        var row = Ui.G("Auto,Auto,*", "", bar, Ui.Icon(glyph, 15).M(0, 0, 12, 0).At(1), Ui.T(label, 14).At(2));
        var b = new Border { Height = 40, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 0, 10, 0), Margin = new Thickness(10, 1, 10, 1), Child = row, Cursor = Cursors.Hand, Tag = key, Background = Pal.HitClear };
        b.MouseEnter += (_, _) => { if (Current != key) b.Background = Pal.Faint; };
        b.MouseLeave += (_, _) => { if (Current != key) b.Background = Pal.HitClear; };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (Current != key) Navigate(key); };
        return b;
    }

    public void Navigate(string key)
    {
        if (Pages.All(p => p.key != key)) key = "General";
        bool samePage = key == Current && _scroll.Content != null;
        double offset = _scroll.VerticalOffset;
        Current = key;
        foreach (Border b in _nav.Children)
        {
            bool on = (string)b.Tag == key;
            b.Background = on ? Pal.Hex("#24FFFFFF") : Pal.HitClear;
            var g = (Grid)b.Child;
            g.Children[0].Visibility = on ? Visibility.Visible : Visibility.Hidden;
            ((TextBlock)g.Children[2]).FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        _title.Text = Pages.First(p => p.key == key).label;
        Page = new StackPanel();
        switch (key)
        {
            case "General": General(); break;
            case "Appearance": Appearance(); break;
            case "Widgets": Widgets(); break;
            case "Agents": AgentsPage(); break;
            case "Tips": Tips(); break;
            case "WhatsNew": WhatsNew(); break;
            default: About(); break;
        }
        _scroll.Content = Page;
        if (samePage) { _scroll.UpdateLayout(); _scroll.ScrollToVerticalOffset(offset); }
        else _scroll.ScrollToTop();
    }

    // ------------------------------------------------------------------ building blocks
    void Section(string title, string? note = null)
    {
        var g = Ui.G("*,Auto", "", Ui.T(title, 13, FontWeights.SemiBold, Pal.Sub), note == null ? null : Ui.T(note, 12, null, Pal.Dim).At(1));
        g.Margin = new Thickness(4, Page.Children.Count == 0 ? 4 : 22, 4, 10);
        Page.Children.Add(g);
    }

    StackPanel Group()
    {
        var sp = new StackPanel();
        Page.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = Pal.Hex("#0EFFFFFF"), BorderBrush = Pal.Line, BorderThickness = new Thickness(1), Child = sp });
        return sp;
    }

    static void Add(StackPanel group, FrameworkElement row)
    {
        if (group.Children.Count > 0) group.Children.Add(new Border { Height = 1, Background = Pal.Line });
        group.Children.Add(row);
    }

    static Grid Row(string glyph, string title, string? desc, FrameworkElement? control)
    {
        var icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Background = Pal.Hex("#16FFFFFF"), Child = Ui.Icon(glyph, 15), VerticalAlignment = VerticalAlignment.Center };
        var text = Ui.V(2, Ui.T(title, 14, FontWeights.SemiBold), desc == null ? null : Ui.T(desc, 12, null, Pal.Sub).Wrap());
        text.VerticalAlignment = VerticalAlignment.Center;
        var g = Ui.G("Auto,*,Auto", "", icon, text.M(14, 0, 16, 0).At(1), control?.VA(VerticalAlignment.Center).At(2));
        g.Margin = new Thickness(16, 12, 16, 12);
        return g;
    }

    static Grid Row(FrameworkElement glyph, string title, string? desc, FrameworkElement? control)
    {
        var g = Row("", title, desc, control);
        ((Border)g.Children[0]).Child = glyph;
        return g;
    }

    static ToggleButton Switch(Func<bool> get, Action<bool> set, string name) => Ui.Switch(get(), v => { set(v); S.Changed(name); });
    static Border Seg(string[] keys, Func<string> get, Action<string> set, string name, string[]? labels = null) =>
        Ui.Segmented(keys, get(), v => { set(v); S.Changed(name); }, false, labels, 32, 13);

    static FrameworkElement SliderWithValue(double min, double max, double value, Func<double, string> fmt, Action<double> set, string name, double width = 190)
    {
        var label = Ui.T(fmt(value), 13, FontWeights.SemiBold);
        label.Width = 56;
        label.TextAlignment = TextAlignment.Right;
        var s = Ui.Slider(min, max, value, v => { label.Text = fmt(v); set(v); S.Changed(name); });
        s.Width = width;
        return Ui.H(10, s, label);
    }

    // ------------------------------------------------------------------ pages
    void General()
    {
        Section("Display");
        var g = Group();
        Add(g, Row(Ic.Monitor, "Style", "A notch on the top edge, a floating pill, or a bar across the screen.", Seg(["Notch", "Pill", "Bar"], () => S.Style, v => S.Style = v, "Style")));
        Add(g, Row(Ic.Clock, "Clock format", "How the time is shown.", Seg(["12", "24"], () => S.Clock24h ? "24" : "12", v => S.Clock24h = v == "24", "Clock", ["12-hour", "24-hour"])));
        Add(g, Row(Ic.Clock, "Date", "Show the weekday, the date or nothing beside the clock.", Seg(["Day", "Date", "Off"], () => S.DateMode, v => S.DateMode = v, "DateMode")));
        Add(g, Row(Ic.Expand, "Notch / pill size", "Make everything in the notch bigger or smaller.", SliderWithValue(0.7, 1.5, S.Scale, v => $"{Math.Round(v * 100)}%", v => S.Scale = v, "Scale")));
        var screens = Forms.Screen.AllScreens.Select((s, i) => (s.DeviceName, s.Primary ? "Primary display" : $"Display {i + 1}")).ToList();
        screens.Insert(0, ("", "Primary display"));
        Add(g, Row(Ic.Monitor, "Show on", "Choose the monitor for the notch.", Ui.Dropdown(() => S.Display, screens.DistinctBy(s => s.Item2).ToList(), v => { S.Display = v; S.Changed("Display"); }, Ic.Monitor)));

        Section("Placement", "Drag the notch to move it when the position is unlocked");
        g = Group();
        Add(g, Row(Mini.Arrows(Pal.Text), "Edge", "Keep the notch on the top, left or right edge of the screen.", Seg(["Top", "Left", "Right"], () => S.Edge, v => S.Edge = v, "Edge")));
        Add(g, Row(Ic.Target, "Position along the edge", "Slide it toward a corner, or keep it centred.", SliderWithValue(0, 1, S.EdgeOffset, v => Math.Abs(v - 0.5) < 0.02 ? "Centre" : $"{Math.Round(v * 100)}%", v => S.EdgeOffset = Math.Abs(v - 0.5) < 0.02 ? 0.5 : v, "EdgeOffset")));
        Add(g, Row(Ic.Expand, "Notch length", "Length of the closed notch. It still grows to fit live activity.", SliderWithValue(120, 480, S.NotchLength, v => $"{Math.Round(v)} px", v => S.NotchLength = v, "NotchLength")));
        Add(g, Row(Ic.Expand, "Notch thickness", "Height of the closed notch, from 28 to 72 px.", SliderWithValue(28, 72, Math.Max(28, S.NotchThickness), v => $"{Math.Round(v)} px", v => S.NotchThickness = v, "NotchThickness")));

        Section("Closed notch", "What the small notch displays");
        g = Group();
        Add(g, Row(Ic.Chip, "Left side", "Battery, a live reading, your next task, or nothing at all.", Ui.Dropdown(() => S.LeftSide,
            [("Auto", "Auto"), ("Battery", "Battery"), ("CPU", "CPU"), ("Memory", "Memory"), ("Network", "Network"), ("Focus", "Next focus task"), ("Nothing", "Nothing")],
            v => { S.LeftSide = v; S.Changed("LeftSide"); }, "\uE734")));
        Add(g, Row(Ic.Speaker, "Music visualizer", "Little bars bounce on the right while audio plays.", Switch(() => S.MusicVisualizer, v => S.MusicVisualizer = v, "MusicVisualizer")));
        Add(g, Row(Ic.Robot, "AI agent activity", "See when your coding agents are busy without opening anything.", Ui.Dropdown(() => S.AgentActivity,
            [("Off", "Off"), ("Auto", "Auto"), ("Always", "Always")], v => { S.AgentActivity = v; S.Changed("AgentActivity"); }, Ic.Eye)));
        Add(g, Row(Ic.Down, "Downloads", "A bouncing arrow while a browser download is running.", Switch(() => S.DownloadsIndicator, v => S.DownloadsIndicator = v, "DownloadsIndicator")));
        Add(g, Row(Ic.Box, "Shelf count", "How many files are waiting on the shelf.", Switch(() => S.ShelfCount, v => S.ShelfCount = v, "ShelfCount")));
        Add(g, Row(Ic.Bolt, "Charging and low battery alerts", "A heads-up when the charger is connected or removed, and at 20%.", Switch(() => S.BatteryAlerts, v => S.BatteryAlerts = v, "BatteryAlerts")));

        Section("Behaviour");
        g = Group();
        Add(g, Row(Ic.Bolt, "Open at sign-in", "Launch Brim automatically when you log in.", Switch(() => S.OpenAtSignIn, v => S.OpenAtSignIn = v, "OpenAtSignIn")));
        Add(g, Row(Ic.Shrink, "Tuck away over apps", "While an app is in front, the notch shrinks to a slim handle at the top of the screen. Point at the handle to open it; on the desktop it shows normally.", Switch(() => S.HideOverApps, v => S.HideOverApps = v, "HideOverApps")));
        Add(g, Row(Ic.Eye, "Hide until hovered", "Keep the notch tucked into its handle everywhere, even on the desktop, until you point at it.", Switch(() => S.HideUntilHovered, v => S.HideUntilHovered = v, "HideUntilHovered")));
        Add(g, Row("\uE8B0", "Expand on hover", "Open the notch as soon as the pointer reaches it.", Switch(() => S.ExpandOnHover, v => S.ExpandOnHover = v, "ExpandOnHover")));
        Add(g, Row(Ic.History, "Open on the last tab", "Pick up where you left off instead of always starting on Home.", Switch(() => S.OpenLastTab, v => S.OpenLastTab = v, "OpenLastTab")));
        Add(g, Row(Ic.Shrink, "Hide behind fullscreen windows", "Get out of the way for games, full-screen video and slideshows.", Switch(() => S.HideFullscreen, v => S.HideFullscreen = v, "HideFullscreen")));
        Add(g, Row(Ic.Pin, "Keep open", "Leave the notch open until you close it yourself.", Switch(() => S.KeepOpen, v => S.KeepOpen = v, "KeepOpen")));
        Add(g, Row(Ic.Lock, "Lock position", "Turn off dragging so the notch can't be moved by accident.", Switch(() => S.LockPosition, v => S.LockPosition = v, "LockPosition")));
    }

    void Appearance()
    {
        Page.Children.Add(Preview());
        Section("Background effect", "♪ effects move with your music");
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-6, 0, -6, 0) };
        foreach (var e in EffectCanvas.All) grid.Children.Add(EffectCard(e));
        Page.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = Pal.Hex("#0EFFFFFF"), BorderBrush = Pal.Line, BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = grid });

        Section("Colour");
        var g = Group();
        Add(g, Row("\uE790", "Accent", "Used for toggles, highlights and effects.", AccentSwatches()));
        Add(g, Row(Ic.Monitor, "Resting notch", "The colour of the notch while it's closed.", Swatches(() => S.RestingColor, v => { S.RestingColor = v; S.Changed("RestingColor"); })));
        Add(g, Row(Ic.Monitor, "Expanded notch", "The panel colour when Material is Solid.", Swatches(() => S.ExpandedColor, v => { S.ExpandedColor = v; S.Changed("ExpandedColor"); })));

        Section("Shape & material");
        g = Group();
        Add(g, Row(Ic.Expand, "Corners", null, Seg(["Rounded", "Squared"], () => S.Corners, v => S.Corners = v, "Corners")));
        Add(g, Row(Ic.Layers, "Material", "Glass lets your wallpaper show through; Dark glass is more opaque; Solid uses the panel colour; Frosted glass blurs whatever is behind the notch.",
            Seg(["Glass", "DarkGlass", "Solid", "LiquidGlass"], () => S.Material, v => S.Material = v, "Material", ["Glass", "Dark glass", "Solid", "Frosted glass"])));
        Add(g, Row(Ic.Eye, "Theme on the closed notch", "Use the same material and effect on the small notch too. Moving effects then never stop, which costs a little battery.", Switch(() => S.ThemeClosed, v => S.ThemeClosed = v, "ThemeClosed")));
        Add(g, Row(Ic.Grid4, "Cards", "Darker tiles make text easier to read over busy effects.", Seg(["Light", "Dark", "Darker"], () => S.Cards, v => S.Cards = v, "Cards")));
    }

    static Border Preview()
    {
        var wall = new Grid { Height = 210 };
        wall.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = new LinearGradientBrush(Color.FromRgb(70, 84, 120), Color.FromRgb(150, 132, 120), 90) });
        var closed = new Border
        {
            Width = 140, Height = 28, CornerRadius = new CornerRadius(0, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Child = Ui.H(10, Ui.T(DateTime.Now.ToString("ddd"), 12, FontWeights.SemiBold, Pal.Sub), Ui.T(DateTime.Now.ToString("h:mm"), 13, FontWeights.Bold), new Bars { Height = 12, BarWidth = 2.4, Gap = 2 }.With(b => b.Set([.4, .9, .6, .8]))).HA(HorizontalAlignment.Center),
        };
        closed.Background = Pal.Black;
        var bar = new Grid();
        bar.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Pal.Hex("#40FFFFFF") });
        bar.Children.Add(new Border { Height = 4, Width = 140, CornerRadius = new CornerRadius(2), Background = Pal.White, HorizontalAlignment = HorizontalAlignment.Left });
        Border Line() => new() { Height = 12, CornerRadius = new CornerRadius(6), Background = Pal.Hex("#24FFFFFF"), VerticalAlignment = VerticalAlignment.Center };
        var open = new Border
        {
            Width = 470, Height = 130, CornerRadius = new CornerRadius(26), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18), Padding = new Thickness(12), Background = Pal.Hex("#B00C0C0E"),
            Child = Ui.G("190,12,*", "*,8,*,8,*",
                new Border { CornerRadius = new CornerRadius(12), Background = Pal.Hex("#24FFFFFF") }.Span(1, 5),
                Line().At(2, 0), Line().At(2, 2),
                new Border { CornerRadius = new CornerRadius(10), Background = Pal.Hex("#24FFFFFF"), Padding = new Thickness(12, 0, 12, 0), Child = bar }.At(2, 4)),
        };
        wall.Children.Add(closed);
        wall.Children.Add(open);
        return new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = wall, Margin = new Thickness(0, 4, 0, 0) };
    }

    static readonly Dictionary<string, ImageSource> Previews = [];

    /// <summary>Renders an effect once to a bitmap and reuses it (keeps the Appearance page fast).</summary>
    static ImageSource PreviewOf(string kind)
    {
        if (Previews.TryGetValue(kind, out var img)) return img;
        var fx = new EffectCanvas { Kind = kind, Width = 176, Height = 96 };
        fx.Source = () => (Color.FromRgb(70, 120, 255), Color.FromRgb(160, 80, 230), Enumerable.Range(0, 32).Select(i => 0.35 + 0.4 * Math.Abs(Math.Sin(i * 0.7))).ToArray(), 0.5, true);
        for (int i = 0; i < 40; i++) fx.Advance(0.05);
        fx.Measure(new Size(176, 96));
        fx.Arrange(new Rect(0, 0, 176, 96));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(352, 192, 192, 192, PixelFormats.Pbgra32);
        rtb.Render(fx);
        rtb.Freeze();
        Previews[kind] = rtb;
        return rtb;
    }

    Border EffectCard(EffectInfo e)
    {
        bool sel = S.Effect == e.Key;
        var previewGrid = new Grid();
        previewGrid.Children.Add(new Border { Background = Pal.Black });
        previewGrid.Children.Add(new Image { Source = PreviewOf(e.Key), Stretch = Stretch.UniformToFill });
        if (e.Music)
            previewGrid.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = Pal.Hex("#99000000"), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = Ui.T("♪ Music", 11, FontWeights.SemiBold) });
        if (sel)
            previewGrid.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Child = Ui.Icon(Ic.Check, 11, Pal.White) }.With(b => b.SetResourceReference(Border.BackgroundProperty, "Accent")));
        var preview = new Border { Height = 96, CornerRadius = new CornerRadius(12), ClipToBounds = true, BorderThickness = new Thickness(sel ? 2 : 1), BorderBrush = sel ? null : Pal.Line, Child = previewGrid };
        if (sel) preview.SetResourceReference(Border.BorderBrushProperty, "Accent");
        preview.Loaded += (_, _) => previewGrid.Clip = new RectangleGeometry(new Rect(0, 0, preview.ActualWidth - 2, preview.ActualHeight - 2), 11, 11);
        var card = new Border { Margin = new Thickness(6, 4, 6, 10), Cursor = Cursors.Hand, Background = Pal.HitClear, Child = Ui.V(6, preview, Ui.V(1, Ui.T(e.Name, 13, FontWeights.SemiBold), Ui.T(e.Desc, 11, null, Pal.Sub).Wrap())) };
        card.MouseLeftButtonUp += (_, _) => { S.Effect = e.Key; S.Changed("Effect"); Navigate("Appearance"); };
        return card;
    }

    FrameworkElement AccentSwatches()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in Pal.AccentChoices)
            sp.Children.Add(Swatch(c, !S.AccentCycle && S.Accent.Equals(c, StringComparison.OrdinalIgnoreCase), () => { S.Accent = c; S.AccentCycle = false; S.Changed("Accent"); Navigate("Appearance"); }));
        var cycle = Ui.Pill("Cycle", null, () => { S.AccentCycle = !S.AccentCycle; S.Changed("Accent"); Navigate("Appearance"); }, S.AccentCycle, 26, 12);
        sp.Children.Add(cycle.M(4, 0, 6, 0));
        sp.Children.Add(Custom(c => { S.Accent = c; S.AccentCycle = false; S.Changed("Accent"); Navigate("Appearance"); }));
        return sp;
    }

    FrameworkElement Swatches(Func<string> get, Action<string> set)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in Pal.NotchChoices)
            sp.Children.Add(Swatch(c, get().Equals(c, StringComparison.OrdinalIgnoreCase), () => { set(c); Navigate("Appearance"); }));
        sp.Children.Add(Custom(c => { set(c); Navigate("Appearance"); }));
        return sp;
    }

    static Border Swatch(string hex, bool on, Action click)
    {
        var col = Pal.Col(hex);
        var b = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Pal.Brush(col), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(on ? 2 : 1), BorderBrush = on ? Pal.White : Pal.Hex("#33FFFFFF"),
            Child = on ? Ui.Icon(Ic.Check, 11, col.R + col.G + col.B > 500 ? Pal.Ink : Pal.White) : null,
        };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    static Border Custom(Action<string> pick)
    {
        var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i <= 6; i++) lg.GradientStops.Add(new GradientStop(Pal.FromHsv(i * 60, .8, 1), i / 6.0));
        var b = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = lg, Margin = new Thickness(3, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = "Custom colour" };
        b.MouseLeftButtonUp += (_, _) =>
        {
            using var dlg = new Forms.ColorDialog { FullOpen = true };
            if (dlg.ShowDialog() == Forms.DialogResult.OK) pick($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
        };
        return b;
    }

    void Widgets()
    {
        Section("Show in the notch");
        var g = Group();
        void W(string glyph, string key, string title, string desc) =>
            Add(g, Row(glyph, title, desc, Ui.Switch(S.W(key), v => S.SetW(key, v))));
        W(Ic.Music, "NowPlaying", "Now playing", "Cover art, playback controls, a seek bar and synced lyrics.");
        W(Ic.Speaker, "MusicLights", "Music lights", "Dancing bars in the small notch while audio plays.");
        W(Ic.Sliders, "Mixer", "Volume mixer", "Set the volume of each app separately, or mute them.");
        W(Ic.Chart, "AgentUsage", "Agent activity tab", "A tab listing what your AI coding agents are doing.");
        W(Ic.Robot, "Agents", "AI coding agents", "Working state, finished turns and attention alerts for Claude Code and Codex.");
        W(Ic.Box, "Shelf", "Shelf", "Park files in the notch, copy them together, reveal them in Explorer.");
        W(Ic.Clipboard, "Clipboard", "Clipboard history", "Text, links, colours, images and pinned snippets with one-click copy.");
        W(Ic.Down, "Downloads", "Download progress", "An animated indicator while files download, and a note when they finish.");
        W(Ic.Eye, "Cloak", "Privacy Blur", "Blur the whole screen in an instant when someone walks by. Press Ctrl+Alt+H or wiggle the pointer inside the open notch; press it again, click or hit Esc to bring the screen back.");
        W(Ic.Chip, "Stats", "System stats", "Live tiles for CPU, memory, network, storage, battery and uptime. Choose which ones below.");

        var chips = new WrapPanel { Margin = new Thickness(16, 2, 16, 12) };
        foreach (var (k, glyph, color) in new[] { ("CPU", Ic.Chip, Pal.Blue), ("Memory", Ic.Ram, Pal.Purple), ("Network", Ic.Down, Pal.Green), ("Storage", Ic.Drive, Pal.Orange), ("Battery", Ic.Bolt, Pal.Green), ("Uptime", Ic.Clock, Pal.Yellow) })
        {
            bool on = S.StatTiles.Contains(k);
            var chip = new Border
            {
                Height = 32, CornerRadius = new CornerRadius(16), Padding = new Thickness(10, 0, 12, 0), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Background = on ? Pal.Hex("#26FFFFFF") : Pal.Hex("#0AFFFFFF"), BorderBrush = Pal.Line, BorderThickness = new Thickness(1),
                Child = Ui.H(6, Ui.Icon(glyph, 12, color), Ui.T(k, 13, FontWeights.SemiBold, on ? Pal.Text : Pal.Dim), on ? Ui.Icon(Ic.Check, 10, Pal.Sub) : null).VA(VerticalAlignment.Center),
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (on) { if (S.StatTiles.Count > 1) S.StatTiles.Remove(k); }
                else S.StatTiles = AppSettings_Order(S.StatTiles.Append(k));
                S.Changed("StatTiles");
                Navigate("Widgets");
            };
            chips.Children.Add(chip);
        }
        Add(g, chips);
        W(Ic.Sun, "Weather", "Weather", "Temperature and conditions in the notch header.");
        W(Ic.Cloud, "Forecast", "Weather forecast", "Hourly and weekly forecasts in their own tab. Click the temperature in the header to jump there.");
        Add(g, Row(Ic.Cloud, "Units", null, Seg(["C", "F"], () => S.Units, v => { S.Units = v; _ = App.Weather.RefreshAsync(); }, "Units", ["°C, km/h", "°F, mph"])));
        Add(g, LocationRow());
        W(Ic.Stopwatch, "Focus", "Focus timer", "A Pomodoro-style timer with a small to-do list.");
        W(Ic.Camera, "Capture", "Screenshot & recording", "Grab the whole screen or a region, or record video with system audio and your mic.");
        W(Ic.Calc, "Shortcuts", "Shortcuts", "One-click buttons on Home for Calculator, screenshots, recording and Task Manager.");
        W(Ic.Doc, "Teleprompter", "Teleprompter", "Keep your notes right below the webcam. Lines advance at the speed you set, and the notch can stay invisible to screen sharing.");
        W(Ic.Bulb, "EdgeLight", "Ring Light", "Turns the screen border into a soft light for video calls. Right-click its Home button to change thickness and colour temperature.");
        W(Ic.Stopwatch, "Stopwatch", "Stopwatch", "A stopwatch tab with laps.");
        W(Ic.Bluetooth, "Bluetooth", "Bluetooth", "Turn Bluetooth on or off and connect paired devices.");
        W(Ic.Grid4, "QuickApps", "Quick apps bar", "Five shortcuts on Home for apps, websites and folders. Edit them below.");
        Add(g, QuickAppsEditor());

        Section("Live Activity", "The small notch adapts to what's going on");
        g = Group();
        Add(g, Row("\uE734", "Live Activity", "The small notch widens to show album art, timers, recordings, agent status and downloads.", Ui.Switch(S.W("SmartMorph"), v => S.SetW("SmartMorph", v))));

        Section("Tab order", "Arrange the tabs in the open notch");
        g = Group();
        for (int i = 0; i < S.TabOrder.Count; i++)
        {
            int idx = i;
            var name = S.TabOrder[i];
            var up = Ui.Circle(Ic.ChevUp, () => Move(idx, -1), 28, 11, fg: idx == 0 ? Pal.Dim : Pal.Text);
            var down = Ui.Circle(Ic.ChevDown, () => Move(idx, 1), 28, 11, fg: idx == S.TabOrder.Count - 1 ? Pal.Dim : Pal.Text);
            bool on = NotchWindow.TabOn(name);
            var row = Ui.G("34,*,Auto", "", Ui.T((i + 1).ToString(), 13, null, Pal.Dim), Ui.T(TabName(name), 14, null, on ? Pal.Text : Pal.Dim).At(1), Ui.H(4, up, down).At(2));
            row.Margin = new Thickness(16, 6, 12, 6);
            Add(g, row);
        }
        var reset = Ui.Btn(Ui.T("Reset to the default order", 12, null, Pal.Sub), () => { S.TabOrder = [.. AppSettings.AllTabs]; S.Changed("TabOrder"); Navigate("Widgets"); });
        Page.Children.Add(reset.M(6, 10, 0, 0).HA(HorizontalAlignment.Left));
    }

    static List<string> AppSettings_Order(IEnumerable<string> tiles)
    {
        string[] order = ["CPU", "Memory", "Network", "Storage", "Battery", "Uptime"];
        return order.Where(tiles.Contains).ToList();
    }

    static string TabName(string t) => t switch
    {
        "Agents" => "Agent activity", "Music" => "Now playing", "Mixer" => "Volume mixer", "Shelf" => "Shelf",
        "Capture" => "Screenshot & recording", "Stats" => "System stats", "Focus" => "Focus timer", _ => t,
    };

    void Move(int i, int d)
    {
        int j = i + d;
        if (j < 0 || j >= S.TabOrder.Count) return;
        (S.TabOrder[i], S.TabOrder[j]) = (S.TabOrder[j], S.TabOrder[i]);
        S.Changed("TabOrder");
        var off = _scroll.VerticalOffset;
        Navigate("Widgets");
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(off);
    }

    FrameworkElement LocationRow()
    {
        var status = Ui.T(S.City.Length > 0 ? $"Fixed: {S.City}" : "Automatic: your approximate city, from your internet connection", 12, null, Pal.Sub);
        var box = Ui.Input("Search for a city", 38);
        async void Go()
        {
            status.Text = "Searching…";
            var ok = await App.Weather.SetCityAsync(box.Text);
            status.Text = ok ? (S.City.Length > 0 ? $"Fixed: {S.City}" : "Automatic") : "City not found";
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Go(); };
        var search = Ui.Pill("Search", null, Go, false, 38, 13, (Brush)Application.Current.FindResource("Accent"), Pal.White);
        var auto = Ui.Btn(Ui.T("Use automatic", 12, null, Pal.LinkBlue), async () => { box.Text = ""; await App.Weather.SetCityAsync(""); status.Text = "Automatic: your approximate city, from your internet connection"; });
        var panel = Ui.V(8,
            Ui.T("Location", 14, FontWeights.SemiBold),
            Ui.H(6, Ui.Icon(Ic.Target, 11, Pal.Sub), status),
            Ui.G("*,Auto,Auto", "", box, search.M(8, 0, 0, 0).At(1), auto.M(12, 0, 0, 0).VA(VerticalAlignment.Center).At(2)));
        panel.Margin = new Thickness(16, 12, 16, 14);
        return panel;
    }

    FrameworkElement QuickAppsEditor()
    {
        var sp = new StackPanel { Margin = new Thickness(16, 4, 16, 14) };
        while (S.QuickApps.Count < 5) S.QuickApps.Add(new QuickApp());
        for (int i = 0; i < 5; i++)
        {
            var qa = S.QuickApps[i];
            var name = Ui.Input("Name", 34); name.Text = qa.Name;
            var target = Ui.Input("App, website or folder (e.g. code, https://…, C:\\Projects)", 34); target.Text = qa.Target;
            name.LostFocus += (_, _) => { qa.Name = name.Text; S.Changed("QuickApps"); };
            target.LostFocus += (_, _) => { qa.Target = target.Text; S.Changed("QuickApps"); };
            var row = Ui.G("130,*", "", name, target.M(8, 0, 0, 0).At(1));
            row.Margin = new Thickness(0, 0, 0, 6);
            sp.Children.Add(row);
        }
        return sp;
    }

    void AgentsPage()
    {
        var intro = Ui.V(10,
            Ui.H(12, new SidekickFace(40, Pal.Orange), Ui.Big("Meet Sidekick", 20, FontWeights.SemiBold).VA(VerticalAlignment.Center)),
            Ui.T("Your desktop companion for AI coding agents. Keep track of their work without switching windows.", 13, null, Pal.Sub).Wrap(),
            Bullet("Get an alert when Claude Code, Codex, Cursor or Antigravity finishes a turn or needs you."),
            Bullet("Allow or deny an agent's permission requests right from the notch."),
            Bullet("See plan limits and when they reset, and pop out a desktop panel you can pin above other windows."),
            Bullet("Everything stays on this PC: agents report to Brim through brim-hook.exe on a local-only port."),
            Ui.Pill("Open desktop panel", Ic.Screen, () => App.OpenDesktopPanel(), true, 34, 13).HA(HorizontalAlignment.Left).M(0, 6, 0, 0));
        Page.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = Pal.Hex("#0EFFFFFF"), BorderBrush = Pal.Line, BorderThickness = new Thickness(1), Padding = new Thickness(20, 18, 20, 18), Child = intro, Margin = new Thickness(0, 4, 0, 0) });

        Section("Integrations", "Each one edits that agent's own hook file and keeps a backup");
        var g = Group();
        Add(g, AgentRow("claude", "Claude Code", Pal.Orange, "~/.claude/settings.json", () => S.ApproveClaude, v => S.ApproveClaude = v,
            "Approve permission requests from the notch. Claude waits up to two minutes for your answer, then asks in the terminal."));
        Add(g, AgentRow("codex", "Codex", Pal.Teal, "~/.codex/config.toml (notify)", null, null, null));
        Add(g, AgentRow("cursor", "Cursor", Pal.Hex("#9AA4FF"), "~/.cursor/hooks.json", () => S.ApproveCursor, v => S.ApproveCursor = v,
            "Approve terminal commands from the notch. While this is on and Brim is closed, Cursor asks you before each command."));
        Add(g, AgentRow("antigravity", "Antigravity", Pal.Hex("#5B9BFF"), "~/.gemini/config/hooks.json", () => S.ApproveAntigravity, v => S.ApproveAntigravity = v,
            "Approve terminal commands from the notch. If you don't answer, Antigravity's own rules decide."));

        Section("Usage");
        g = Group();
        Add(g, Row(new SidekickFace(22, Pal.Teal), "Codex limits", "Read from the rate-limit snapshot Codex writes to its own session logs on this PC. Always on.", null));
        Add(g, Row(new SidekickFace(22, Pal.Orange), "Claude plan limits", "Uses your Claude Code sign-in on this PC to ask Claude for your plan's 5-hour and weekly limits. The token is read from ~/.claude and sent only to Anthropic. Off: Brim shows local token totals instead.",
            Ui.Switch(S.ClaudeUsageOnline, v => { S.ClaudeUsageOnline = v; S.Changed("ClaudeUsageOnline"); _ = App.Usage.RefreshAsync(); })));
        Add(g, Row(Ic.Info, "Cursor and Antigravity", "They don't publish plan usage to local apps, so the notch shows their activity but not their limits.", null));

        Section("Try it");
        g = Group();
        Add(g, Row(Ic.Bell, "Send a test alert", "Shows what a finished-turn alert looks like.", Ui.Pill("Send", null, () =>
            App.Agents.Ingest("claude", "{\"hook_event_name\":\"Stop\",\"session_id\":\"demo\",\"cwd\":\"C:\\\\Projects\\\\demo-app\",\"last_assistant_message\":\"Your changes are ready. All checks passed.\"}"), false, 32, 13)));
        Add(g, Row(Ic.Check, "Send a test approval", "Shows a permission request. Nothing runs, whatever you pick.", Ui.Pill("Send", null, () => App.Agents.Demo(), false, 32, 13)));
    }

    Grid AgentRow(string key, string name, Brush color, string file, Func<bool>? getApprove, Action<bool>? setApprove, string? approveText)
    {
        var status = Ui.T("", 12, null, Pal.Sub).Wrap();
        var btnText = Ui.T("", 13, FontWeights.SemiBold);
        var btn = Ui.Btn(Ui.Card(btnText, 16, new Thickness(16, 7, 16, 7)), () => { });
        void Paint(string? msg = null)
        {
            bool on = AgentService.Connected(key);
            btnText.Text = on ? "Disconnect" : "Connect";
            status.Text = msg ?? (on ? $"Connected · {file}" : $"Adds Brim's hooks to {file}");
            status.Foreground = on ? Pal.Green : Pal.Sub;
        }
        btn.Click += (_, _) => Paint(AgentService.Connected(key) ? AgentService.Disconnect(key) : AgentService.Connect(key, getApprove?.Invoke() ?? false));
        var row = Row(new SidekickFace(22, color), name, null, btn);
        var text = (StackPanel)row.Children[1];
        text.Children.Add(status);
        if (getApprove != null && setApprove != null)
        {
            var sw = Ui.Switch(getApprove(), v =>
            {
                setApprove(v);
                S.Changed("Approve");
                if (AgentService.Connected(key)) Paint(AgentService.Connect(key, v));
            });
            sw.RenderTransform = new ScaleTransform(0.85, 0.85);
            text.Children.Add(Ui.G("Auto,*", "", sw.M(-3, 6, 6, 0).VA(VerticalAlignment.Top), Ui.T(approveText!, 12, null, Pal.Text).Wrap().M(0, 7, 0, 0).At(1)));
        }
        Paint();
        return row;
    }
    static FrameworkElement Bullet(string text) => Ui.G("16,*", "", Ui.T("•", 13, null, Pal.Sub).VA(VerticalAlignment.Top), Ui.T(text, 13).Wrap().At(1));

    void Tips()
    {
        var g = Group();
        Add(g, Row("\uE8B0", "Open the notch", "Point at or click the notch to see everything it can do.", null));
        Add(g, Row(Ic.Speaker, "Volume and brightness", "Press the volume or brightness keys and the notch shows the new level.", null));
        Add(g, Row("\uE8B0", "Scroll to adjust", "Use the mouse wheel on that pop-up for small adjustments.", null));
        Add(g, Row(Ic.Pin, "Keep it open", "Click the pin at the top of the open notch to stop it closing.", null));
        Add(g, Row(Mini.Arrows(Pal.Text), "Move it", "Unlock the position in General, then drag the notch to the top, left or right edge.", null));
        Add(g, Row(Ic.Next, "Seek a song", "Click the progress bar to skip to that moment.", null));
        Add(g, Row(Ic.Box, "Park files", "Drop files on the notch to keep them handy on the shelf.", null));
        Add(g, Row(Ic.Eye, "Blur the screen", "Press Ctrl+Alt+H to hide everything behind a blur.", null));
    }

    void WhatsNew()
    {
        var g = Group();
        Add(g, Row("\uE734", "Version 1.0.0", "First release. Every feature is included: no paywall, no account.", null));
        foreach (var (glyph, t, d) in new[]
        {
            (Ic.Robot, "Agents", "Alerts and activity for Claude Code and Codex."),
            (Ic.Music, "Music", "Synced lyrics, output switching and a fullscreen listening room."),
            (Ic.Box, "Shelf", "Park files on the notch and drag them back out."),
            (Ic.Clipboard, "Clipboard", "Pins, images, filters and smart actions for links, colours and email."),
            (Ic.Camera, "Capture", "Screenshots and screen recordings with system sound and microphone."),
            ("\uE790", "Appearance", "Sixteen background effects, four materials and custom colours."),
        })
            Add(g, Row(glyph, t, d, null));
    }

    void About()
    {
        var g = Group();
        Add(g, Row(Ic.Info, "Brim 1.0.0", "A Dynamic Island-style notch for Windows, built from scratch with .NET and WPF.", null));
        Add(g, Row(Ic.Folder, "Settings folder", AppSettings.DataDir, Ui.Pill("Open", null, () => Shell.Open(AppSettings.DataDir), false, 32, 13)));
        Add(g, Row("\uE734", "Credits", "Weather by Open-Meteo. Lyrics by LRCLIB. Icons from Segoe Fluent Icons.", null));
    }
}

static class WithExt
{
    public static T With<T>(this T e, Action<T> a) { a(e); return e; }
}
