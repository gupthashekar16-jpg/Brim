using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Brim.Core;

/// <summary>Segoe Fluent Icons glyphs.</summary>
public static class Ic
{
    public const string Home = "", Music = "", Note = "", Sliders = "", Speaker = "",
        Mute = "", Bluetooth = "", Box = "", Clipboard = "", Cup = "", Cloud = "",
        Sun = "", Moon = "", Doc = "", Camera = "", Pulse = "", Robot = "",
        Chart = "", Stopwatch = "", Pin = "", Gear = "", Power = "", Chip = "",
        Ram = "", Down = "", Drive = "", Bolt = "", Clock = "", Calc = "",
        Record = "", Crop = "", Monitor = "", Folder = "", FolderClosed = "", Mic = "",
        Bell = "", Refresh = "", Copy = "", Delete = "", Lock = "", Unlock = "",
        Edit = "", Expand = "", Shrink = "", Headphones = "", Phone = "", Qr = "",
        Bulb = "", Target = "", Prev = "", Next = "", Play = "", Pause = "",
        PlayLine = "", PauseLine = "",
        ChevDown = "", ChevUp = "", ChevRight = "", ChevLeft = "", Add = "",
        Remove = "", Check = "", Close = "", History = "", Eye = "", Wifi = "",
        Link = "", Search = "", Open = "", Info = "", Image = "", Book = "",
        Puzzle = "", Warning = "", Gears = "", Mail = "", List = "", Toggle = "",
        Grid4 = "", Upload = "", Flame = "", Layers = "", Code = "", Tag = "",
        Flask = "", Screen = "";
}

/// <summary>Shared colours.</summary>
public static class Pal
{
    public static SolidColorBrush Hex(string h)
    {
        var b = new SolidColorBrush(Col(h));
        b.Freeze();
        return b;
    }

    public static Color Col(string h)
    {
        try { return (Color)ColorConverter.ConvertFromString(h); }
        catch { return Colors.Black; }
    }

    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    public static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public static readonly SolidColorBrush
        Text = Hex("#F5F5F7"), Sub = Hex("#9A9AA2"), Dim = Hex("#6B6B73"), White = Hex("#FFFFFF"), Black = Hex("#000000"),
        Ink = Hex("#111114"), Blue = Hex("#5AC8FA"), Purple = Hex("#B36AE2"), Green = Hex("#4CD964"), Orange = Hex("#F5A623"),
        Yellow = Hex("#FFD60A"), Red = Hex("#FF5F57"), Teal = Hex("#40C8C8"), LinkBlue = Hex("#6EA8FE"),
        Track = Hex("#2EFFFFFF"), Hover = Hex("#22FFFFFF"), Faint = Hex("#0FFFFFFF"), Line = Hex("#14FFFFFF"),
        Clear = Hex("#00000000"), HitClear = Hex("#01000000");

    public static readonly string[] AccentChoices =
        ["#3B82F6", "#3FB8C9", "#4CC764", "#F5D547", "#F5A33B", "#EF5350", "#F0506E", "#B266E8", "#FFFFFF"];
    public static readonly string[] NotchChoices =
        ["#000000", "#101014", "#141826", "#1A1A16", "#1B2550", "#22224A", "#0F2A1A", "#2A1250", "#F5F5F7"];
}

public static class Fmt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Bytes(double b)
    {
        if (b >= 1e12) return (b / 1e12).ToString("0.0", Inv) + " TB";
        if (b >= 1e9) return (b / 1073741824d).ToString(b >= 1e11 ? "0" : "0.0", Inv) + " GB";
        if (b >= 1e6) return (b / 1048576d).ToString("0.0", Inv) + " MB";
        if (b >= 1e3) return (b / 1024d).ToString("0.0", Inv) + " KB";
        return b.ToString("0", Inv) + " B";
    }

    public static string Speed(double bps)
    {
        if (bps < 1024) return bps.ToString("0", Inv) + " B/s";
        if (bps < 1048576) return (bps / 1024).ToString("0.0", Inv) + " KB/s";
        return (bps / 1048576).ToString("0.0", Inv) + " MB/s";
    }

    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m";

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}

/// <summary>Small fluent helpers for building UI in code.</summary>
public static class Ui
{
    public static Style Res(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>Number of context menus / popups currently open (keeps the notch from collapsing under them).</summary>
    public static int OpenMenus;

    public static void HookMenus()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((_, _) => OpenMenus++));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((_, _) => OpenMenus = Math.Max(0, OpenMenus - 1)));
    }

    public static void TrackPopup(Popup p)
    {
        p.Opened += (_, _) => OpenMenus++;
        p.Closed += (_, _) => OpenMenus = Math.Max(0, OpenMenus - 1);
    }

    public static TextBlock T(string text, double size = 13, FontWeight? weight = null, Brush? fg = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = fg ?? Pal.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        return t;
    }

    public static TextBlock Big(string text, double size, FontWeight? weight = null, Brush? fg = null)
    {
        var t = T(text, size, weight ?? FontWeights.SemiBold, fg);
        t.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        return t;
    }

    public static TextBlock Icon(string glyph, double size = 16, Brush? fg = null)
    {
        var t = new TextBlock
        {
            Text = glyph,
            FontSize = size,
            Foreground = fg ?? Pal.Text,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return t;
    }

    public static Border Card(UIElement? child = null, double radius = 12, Thickness? pad = null)
    {
        var b = new Border { CornerRadius = new CornerRadius(radius), Padding = pad ?? new Thickness(14, 10, 14, 10), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, "CardBg");
        return b;
    }

    public static StackPanel H(double gap, params UIElement?[] kids) => Stack(Orientation.Horizontal, gap, kids);
    public static StackPanel V(double gap, params UIElement?[] kids) => Stack(Orientation.Vertical, gap, kids);

    static StackPanel Stack(Orientation o, double gap, UIElement?[] kids)
    {
        var sp = new StackPanel { Orientation = o };
        bool first = true;
        foreach (var k in kids)
        {
            if (k == null) continue;
            if (!first && gap > 0)
                sp.Children.Add(new Border { Width = o == Orientation.Horizontal ? gap : 0, Height = o == Orientation.Vertical ? gap : 0 });
            sp.Children.Add(k);
            first = false;
        }
        return sp;
    }

    static GridLength Len(string s)
    {
        s = s.Trim();
        if (s.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return GridLength.Auto;
        if (s.EndsWith('*')) return new GridLength(s.Length == 1 ? 1 : double.Parse(s[..^1], CultureInfo.InvariantCulture), GridUnitType.Star);
        return new GridLength(double.Parse(s, CultureInfo.InvariantCulture));
    }

    public static Grid G(string cols, string rows, params UIElement?[] kids)
    {
        var g = new Grid();
        if (!string.IsNullOrWhiteSpace(cols)) foreach (var c in cols.Split(',')) g.ColumnDefinitions.Add(new ColumnDefinition { Width = Len(c) });
        if (!string.IsNullOrWhiteSpace(rows)) foreach (var r in rows.Split(',')) g.RowDefinitions.Add(new RowDefinition { Height = Len(r) });
        foreach (var k in kids) if (k != null) g.Children.Add(k);
        return g;
    }

    public static Button Btn(UIElement content, Action onClick, string? tip = null)
    {
        var b = new Button { Content = content, Style = Res("Plain") };
        b.Click += (_, _) => onClick();
        if (tip != null) b.ToolTip = tip;
        return b;
    }

    /// <summary>Gives a border a hover background.</summary>
    public static T Hoverable<T>(this T b, Brush? normal = null, Brush? hover = null) where T : Border
    {
        var n = normal ?? b.Background;
        if (normal != null) b.Background = normal;
        b.MouseEnter += (_, _) => b.Background = hover ?? Pal.Hover;
        b.MouseLeave += (_, _) => { if (n != null) b.Background = n; else b.SetResourceReference(Border.BackgroundProperty, "CardBg"); };
        return b;
    }

    public static Button Circle(string glyph, Action onClick, double size = 32, double icon = 14, Brush? bg = null, Brush? fg = null, string? tip = null)
    {
        var bd = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = bg ?? Pal.Clear, Child = Icon(glyph, icon, fg),
        };
        if (bg == null) bd.Hoverable(Pal.HitClear, Pal.Hover);
        return Btn(bd, onClick, tip);
    }

    /// <summary>Rounded square icon button using the card background.</summary>
    public static Button Square(string glyph, Action onClick, double size = 32, double icon = 14, string? tip = null, Brush? fg = null)
    {
        var bd = Card(Icon(glyph, icon, fg), 10, new Thickness(0));
        bd.Width = size; bd.Height = size;
        bd.Hoverable(null, Pal.Hover);
        return Btn(bd, onClick, tip);
    }

    public static Button Pill(string text, string? glyph, Action onClick, bool primary = false, double h = 34, double fs = 13, Brush? bg = null, Brush? fg = null)
    {
        var f = fg ?? (primary ? Pal.Ink : Pal.Text);
        var content = H(7, glyph == null ? null : Icon(glyph, fs - 1, f), T(text, fs, FontWeights.SemiBold, f));
        var bd = new Border
        {
            Height = h, CornerRadius = new CornerRadius(h / 2), Padding = new Thickness(h * 0.45, 0, h * 0.45, 0), Child = content,
        };
        content.VerticalAlignment = VerticalAlignment.Center;
        if (bg != null) bd.Background = bg;
        else if (primary) bd.Background = Pal.White;
        else { bd.SetResourceReference(Border.BackgroundProperty, "CardBg"); bd.Hoverable(null, Pal.Hover); }
        return Btn(bd, onClick);
    }

    public static ToggleButton Switch(bool on, Action<bool> changed)
    {
        var t = new ToggleButton { Style = Res("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        t.Checked += (_, _) => changed(true);
        t.Unchecked += (_, _) => changed(false);
        return t;
    }

    public static Slider Slider(double min, double max, double value, Action<double>? changed, bool thumb = true, Brush? fill = null)
    {
        var s = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            Style = Res(thumb ? "Flat" : "FlatNoThumb"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (fill != null) s.Foreground = fill;
        if (changed != null) s.ValueChanged += (_, e) => changed(e.NewValue);
        return s;
    }

    public static TextBox Input(string placeholder, double height = 40)
    {
        return new TextBox { Style = Res("Input"), Tag = placeholder, Height = height };
    }

    /// <summary>Segmented control. light = white selected pill (notch style); otherwise grey (settings style).</summary>
    public static Border Segmented(IList<string> options, string selected, Action<string> onSelect, bool light = false,
        IList<string>? labels = null, double h = 34, double fs = 13)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new Border
        {
            CornerRadius = new CornerRadius(light ? h / 2 + 3 : 10),
            Padding = new Thickness(3), Child = row,
            Background = light ? null : Pal.Hex("#26000000"),
            BorderBrush = light ? null : Pal.Line, BorderThickness = new Thickness(light ? 0 : 1),
        };
        if (light) host.SetResourceReference(Border.BackgroundProperty, "CardBg");
        var items = new List<(Border b, TextBlock t, string key)>();
        void Paint(string sel)
        {
            foreach (var (b, t, k) in items)
            {
                bool on = k == sel;
                b.Background = on ? (light ? Pal.White : Pal.Hex("#33FFFFFF")) : Pal.HitClear;
                t.Foreground = on ? (light ? Pal.Ink : Pal.Text) : Pal.Sub;
            }
        }
        for (int i = 0; i < options.Count; i++)
        {
            var key = options[i];
            var t = T(labels?[i] ?? key, fs, FontWeights.SemiBold);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border
            {
                Height = h, MinWidth = h * 1.6, Padding = new Thickness(h * 0.42, 0, h * 0.42, 0),
                CornerRadius = new CornerRadius(light ? h / 2 : 8), Child = t, Cursor = Cursors.Hand,
            };
            b.MouseLeftButtonUp += (_, _) => { Paint(key); onSelect(key); };
            items.Add((b, t, key));
            row.Children.Add(b);
        }
        Paint(selected);
        return host;
    }

    /// <summary>A button that pops a dark menu of choices.</summary>
    public static Button Dropdown(Func<string> current, IList<(string key, string label)> choices, Action<string> onPick,
        string? glyph = null, double minWidth = 150)
    {
        var label = T("", 13, FontWeights.SemiBold);
        void Sync() => label.Text = choices.FirstOrDefault(c => c.key == current()).label ?? current();
        Sync();
        var content = G("Auto,*,Auto", "",
            glyph == null ? null : Icon(glyph, 13).M(0, 0, 8, 0),
            label.At(1),
            Icon(Ic.ChevDown, 10, Pal.Sub).M(10, 0, 0, 0).At(2));
        var bd = new Border
        {
            Height = 36, MinWidth = minWidth, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 0, 12, 0),
            Background = Pal.Hex("#26000000"), BorderBrush = Pal.Line, BorderThickness = new Thickness(1), Child = content,
        };
        Button? btn = null;
        btn = Btn(bd, () =>
        {
            var m = new ContextMenu { PlacementTarget = btn, Placement = PlacementMode.Bottom };
            foreach (var (key, text) in choices)
            {
                var mi = new MenuItem { Header = text, IsChecked = key == current() };
                mi.Click += (_, _) => { onPick(key); Sync(); };
                m.Items.Add(mi);
            }
            m.IsOpen = true;
        });
        return btn;
    }

    // ---- fluent layout extensions
    public static T M<T>(this T e, double l, double t, double r, double b) where T : FrameworkElement { e.Margin = new Thickness(l, t, r, b); return e; }
    public static T M<T>(this T e, double all) where T : FrameworkElement { e.Margin = new Thickness(all); return e; }
    public static T At<T>(this T e, int col, int row = 0) where T : UIElement { Grid.SetColumn(e, col); Grid.SetRow(e, row); return e; }
    public static T Span<T>(this T e, int cols, int rows = 1) where T : UIElement { Grid.SetColumnSpan(e, cols); Grid.SetRowSpan(e, rows); return e; }
    public static T HA<T>(this T e, HorizontalAlignment a) where T : FrameworkElement { e.HorizontalAlignment = a; return e; }
    public static T VA<T>(this T e, VerticalAlignment a) where T : FrameworkElement { e.VerticalAlignment = a; return e; }
    public static T Size<T>(this T e, double w, double h) where T : FrameworkElement { if (w > 0) e.Width = w; if (h > 0) e.Height = h; return e; }
    public static T Wd<T>(this T e, double w) where T : FrameworkElement { e.Width = w; return e; }
    public static T Ht<T>(this T e, double h) where T : FrameworkElement { e.Height = h; return e; }
    public static T Tip<T>(this T e, string tip) where T : FrameworkElement { e.ToolTip = tip; return e; }
    public static T Wrap<T>(this T e) where T : TextBlock { e.TextWrapping = TextWrapping.Wrap; e.TextTrimming = TextTrimming.None; return e; }
    public static T Show<T>(this T e, bool visible) where T : UIElement { e.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; return e; }
    public static T OnClick<T>(this T e, Action a) where T : UIElement
    {
        e.MouseLeftButtonUp += (_, ev) => { a(); ev.Handled = true; };
        if (e is FrameworkElement fe) fe.Cursor = Cursors.Hand;
        return e;
    }
}
