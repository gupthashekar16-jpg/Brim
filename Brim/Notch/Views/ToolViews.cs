using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brim.Core;
using Brim.Services;

namespace Brim.Notch.Views;

/// <summary>Shelf: park files dropped on the notch.</summary>
public sealed class ShelfView : NotchView
{
    readonly StackPanel _list = new();
    readonly TextBlock _count = Ui.T("", 12, null, Pal.Sub);
    readonly Border _empty;

    readonly Border _phone = new() { Margin = new Thickness(0, 12, 0, 0) };

    public ShelfView(NotchWindow n) : base(n)
    {
        var head = Ui.G("*,Auto", "",
            Ui.H(8, Ui.Icon(Ic.Box, 14), Ui.T("Shelf", 14, FontWeights.SemiBold), _count),
            Ui.H(8,
                Ui.Pill("Phone transfer", Ic.Phone, () => { if (App.Phone.Running) App.Phone.Stop(); else App.Phone.Start(); }, false, 30, 12),
                Ui.Pill("Copy all", Ic.Copy, () => { App.Shelf.CopyAll(); N.Toast(Ic.Copy, "Files copied — paste anywhere"); }, false, 30, 12),
                Ui.Pill("Clear", Ic.Delete, () => App.Shelf.Clear(), false, 30, 12)).At(1));
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_phone.At(0, 2));
        App.Phone.Changed += () => { PaintPhone(); N.Relayout(); };
        PaintPhone();
        _empty = new Border
        {
            Height = 120, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), BorderBrush = Pal.Hex("#33FFFFFF"),
            Child = Ui.V(8, Ui.Icon(Ic.Box, 22, Pal.Sub), Ui.T("Drag files onto the notch to park them here", 13, null, Pal.Sub)).VA(VerticalAlignment.Center).HA(HorizontalAlignment.Center),
        };
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(head);
        var body = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(_empty);
        body.Children.Add(Scroll(_list, 230));
        Children.Add(body.At(0, 1));
        App.Shelf.Changed += () => { Rebuild(); N.Relayout(); };
        Rebuild();
    }

    void PaintPhone()
    {
        var p = App.Phone;
        if (!p.Running && p.Error.Length == 0) { _phone.Visibility = Visibility.Collapsed; return; }
        _phone.Visibility = Visibility.Visible;
        if (!p.Running)
        {
            _phone.Child = Ui.Card(Ui.H(8, Ui.Icon(Ic.Warning, 13, Pal.Orange), Ui.T(p.Error, 13)), 14, new Thickness(14, 10, 14, 10));
            return;
        }
        var qr = new Border
        {
            Width = 128, Height = 128, CornerRadius = new CornerRadius(12), Background = Pal.White, Padding = new Thickness(4),
            Child = new Image { Source = p.Qr, Stretch = Stretch.Uniform },
        };
        RenderOptions.SetBitmapScalingMode(qr.Child, BitmapScalingMode.NearestNeighbor);
        var url = new TextBox { Text = p.Url, IsReadOnly = true, Background = Pal.Clear, BorderThickness = new Thickness(0), Foreground = Pal.Dim, FontSize = 11, Padding = new Thickness(0) };
        var info = Ui.V(6,
            Ui.T("Scan with your phone's camera", 14, FontWeights.SemiBold),
            Ui.T("Your phone must be on the same Wi-Fi. Tap files to save them on the phone, or send files back: they land in Downloads › Brim Phone and on the shelf. If Windows asks, allow Brim on private networks.", 12, null, Pal.Sub).Wrap(),
            url,
            Ui.H(8,
                Ui.Pill("Copy link", Ic.Link, () => App.Clip.CopyText(p.Url), false, 30, 12),
                Ui.Pill("Stop", Ic.Close, () => p.Stop(), false, 30, 12)));
        var g = Ui.G("Auto,*", "", qr, info.M(16, 0, 0, 0).VA(VerticalAlignment.Center).At(1));
        _phone.Child = Ui.Card(g, 14, new Thickness(12));
    }

    void Rebuild()
    {
        _list.Children.Clear();
        _count.Text = App.Shelf.Items.Count > 0 ? $"({App.Shelf.Items.Count})" : "";
        _empty.Visibility = App.Shelf.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in App.Shelf.Items.ToList())
        {
            UIElement icon = Shell.IconFor(item.Path) is ImageSource img ? new Image { Source = img, Width = 24, Height = 24 } : Ui.Icon(Ic.Doc, 18);
            var actions = Ui.H(4,
                Ui.Circle(Ic.Open, () => Shell.Open(item.Path), 30, 12, tip: "Open"),
                Ui.Circle(Ic.Folder, () => Shell.Reveal(item.Path), 30, 12, tip: "Show in Explorer"),
                Ui.Circle(Ic.Copy, () => { ShelfService.CopyItems([item]); N.Toast(Ic.Copy, "Copied " + item.Name); }, 30, 12, tip: "Copy"),
                Ui.Circle(Ic.Delete, () => App.Shelf.Remove(item), 30, 12, tip: "Remove"));
            var g = Ui.G("Auto,*,Auto", "", icon, Ui.V(1, Ui.T(item.Name, 13, FontWeights.SemiBold), Ui.T(item.Meta, 11, null, Pal.Sub)).M(12, 0, 8, 0).At(1), actions.At(2));
            var card = Ui.Card(g, 12, new Thickness(12, 8, 8, 8));
            card.Margin = new Thickness(0, 0, 0, 8);
            card.ToolTip = item.Path;
            Point start = default;
            card.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(card);
            card.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(card);
                if (Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) < 8) return;
                DragDrop.DoDragDrop(card, ShelfService.DataFor([item]), DragDropEffects.Copy | DragDropEffects.Move);
            };
            _list.Children.Add(card);
        }
    }
}

/// <summary>Clipboard history with search, filters, pins and smart actions.</summary>
public sealed class ClipboardView : NotchView
{
    static readonly string[] Filters = ["All", "Pinned", "Images", "Links", "Colors", "Code", "Text"];
    readonly StackPanel _list = new();
    readonly TextBox _search = Ui.Input("Search clips, links, colors…", 36);
    readonly TextBlock _footer = Ui.T("", 12, null, Pal.Dim);
    readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    readonly Grid _snippetRow;
    readonly TextBox _snippet = Ui.Input("Type a snippet to keep, then press Enter", 36);
    string _filter = "All";
    string? _copiedId;

    public ClipboardView(NotchWindow n) : base(n)
    {
        _search.Padding = new Thickness(34, 0, 12, 0);
        var searchWrap = new Grid();
        searchWrap.Children.Add(_search);
        searchWrap.Children.Add(Ui.Icon(Ic.Search, 12, Pal.Sub).HA(HorizontalAlignment.Left).M(13, 0, 0, 0));
        _search.TextChanged += (_, _) => Rebuild();
        var head = Ui.G("*,Auto", "", searchWrap,
            Ui.H(8,
                Ui.Pill("New Snippet", Ic.Add, () => { _snippetRow!.Visibility = Visibility.Visible; N.FocusInput(_snippet); N.Relayout(); }, false, 34, 12),
                Ui.Pill("Clear", Ic.Delete, () => App.Clip.ClearUnpinned(), false, 34, 12)).M(10, 0, 0, 0).At(1));
        _snippet.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { App.Clip.AddSnippet(_snippet.Text); _snippet.Text = ""; _snippetRow!.Visibility = Visibility.Collapsed; N.Relayout(); }
            if (e.Key == Key.Escape) { _snippetRow!.Visibility = Visibility.Collapsed; N.Relayout(); }
        };
        _snippetRow = Ui.G("*", "", _snippet);
        _snippetRow.Margin = new Thickness(0, 8, 0, 0);
        _snippetRow.Visibility = Visibility.Collapsed;
        BuildChips();
        _chips.Margin = new Thickness(0, 10, 0, 10);
        var footer = Ui.G("*,Auto", "", _footer, Ui.T("Pinned snippets are kept forever", 12, null, Pal.Dim).At(1));
        footer.Margin = new Thickness(2, 8, 2, 0);
        var root = Ui.V(0, head, _snippetRow, _chips, Scroll(_list, 214), footer);
        Children.Add(root);
        App.Clip.Changed += () => { if (IsVisible) Rebuild(); };
    }

    void BuildChips()
    {
        _chips.Children.Clear();
        foreach (var f in Filters)
        {
            bool on = f == _filter;
            string glyph = f switch { "Pinned" => Ic.Pin, "Images" => Ic.Image, "Links" => Ic.Link, "Colors" => "", "Code" => Ic.Code, "Text" => Ic.Doc, _ => "" };
            var content = Ui.H(5, glyph.Length > 0 ? Ui.Icon(glyph, 11, on ? Pal.Ink : Pal.Sub) : null, Ui.T(f, 12, FontWeights.SemiBold, on ? Pal.Ink : Pal.Text));
            var b = new Border { Height = 28, Padding = new Thickness(11, 0, 11, 0), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 6, 0), Child = content, Cursor = Cursors.Hand };
            content.VerticalAlignment = VerticalAlignment.Center;
            if (on) b.Background = Pal.White; else b.SetResourceReference(Border.BackgroundProperty, "CardBg");
            var key = f;
            b.MouseLeftButtonUp += (_, _) => { _filter = key; BuildChips(); Rebuild(); };
            _chips.Children.Add(b);
        }
    }

    public override void OnShow() => Rebuild();

    bool Match(ClipItem i)
    {
        var ok = _filter switch
        {
            "Pinned" => i.Pinned,
            "Images" => i.Kind == "Image",
            "Links" => i.Kind == "Link",
            "Colors" => i.Kind == "Color",
            "Code" => i.Kind == "Code",
            "Text" => i.Kind is "Text" or "Email",
            _ => true,
        };
        var q = _search.Text.Trim();
        return ok && (q.Length == 0 || i.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    void Rebuild()
    {
        _list.Children.Clear();
        var items = App.Clip.Items.Where(Match).OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Time).Take(40).ToList();
        foreach (var i in items) _list.Children.Add(Card(i));
        if (items.Count == 0)
            _list.Children.Add(Ui.T(App.Clip.Items.Count == 0 ? "Copy something and it shows up here" : "No clips match", 13, null, Pal.Sub).M(4, 12, 0, 12));
        _footer.Text = $"{App.Clip.Items.Count} clip{(App.Clip.Items.Count == 1 ? "" : "s")}";
        N.Relayout();
    }

    Border Card(ClipItem i)
    {
        string kindGlyph = i.Kind switch { "Link" => Ic.Link, "Image" => Ic.Image, "Color" => "", "Code" => Ic.Code, "Email" => Ic.Mail, _ => Ic.Doc };
        var meta = Ui.H(6,
            i.Pinned ? Badge(Ic.Pin, "Pinned") : null,
            Badge(kindGlyph, i.Kind),
            Ui.T(Fmt.Ago(i.Time), 11, null, Pal.Dim));
        bool copied = _copiedId == i.Id;
        var copyBtn = Ui.Pill(copied ? "Copied" : "Copy", copied ? Ic.Check : Ic.Copy, () =>
        {
            App.Clip.Copy(i);
            _copiedId = i.Id;
            Rebuild();
            Task.Delay(1500).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (_copiedId == i.Id) { _copiedId = null; Rebuild(); } }));
        }, false, 28, 12, copied ? Pal.Hex("#2E4CD964") : null, copied ? Pal.Green : null);
        var actions = Ui.H(4,
            Ui.Circle(Ic.Pin, () => App.Clip.TogglePin(i), 28, 12, fg: i.Pinned ? Pal.Yellow : Pal.Sub, tip: i.Pinned ? "Unpin" : "Pin"),
            copyBtn,
            Ui.Circle(Ic.Delete, () => App.Clip.Delete(i), 28, 12, fg: Pal.Sub, tip: "Delete"));
        var top = Ui.G("*,Auto", "", meta, actions.At(1));

        UIElement body;
        if (i.Kind == "Image" && i.ImagePath != null)
        {
            BitmapImage? bmp = null;
            try
            {
                bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelHeight = 120; bmp.UriSource = new Uri(i.ImagePath); bmp.EndInit(); bmp.Freeze();
            }
            catch { }
            body = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, MaxHeight = 90, HorizontalAlignment = HorizontalAlignment.Left, Child = new Image { Source = bmp, Stretch = Stretch.Uniform, MaxHeight = 90 } };
        }
        else if (i.Kind == "Color")
        {
            Brush sw;
            try { sw = new SolidColorBrush((Color)ColorConverter.ConvertFromString(i.Text.Trim())); } catch { sw = Pal.Track; }
            body = Ui.H(10, new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = sw, BorderBrush = Pal.Line, BorderThickness = new Thickness(1) }, Ui.T(i.Text.Trim(), 13, FontWeights.SemiBold));
        }
        else
        {
            var t = Ui.T(i.Text.Length > 400 ? i.Text[..400] + "…" : i.Text, 13, i.Kind == "Link" ? FontWeights.SemiBold : FontWeights.Normal);
            t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxHeight = 56;
            if (i.Kind == "Code") t.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            body = t;
        }

        StackPanel? smart = null;
        if (i.Kind == "Link")
            smart = Ui.H(8,
                Ui.Pill("Open in Browser", Ic.Open, () => Shell.Open(i.Text.StartsWith("www.") ? "https://" + i.Text : i.Text), false, 28, 12, Pal.Hex("#2E3B82F6"), Pal.LinkBlue),
                Ui.Pill("Clean Link", Ic.Link, () => { var c = ClipboardService.CleanLink(i.Text); App.Clip.CopyText(c); N.Toast(Ic.Link, "Clean link copied"); }, false, 28, 12));
        else if (i.Kind == "Email")
            smart = Ui.H(8, Ui.Pill("Compose email", Ic.Mail, () => Shell.Open("mailto:" + i.Text.Trim()), false, 28, 12, Pal.Hex("#2E3B82F6"), Pal.LinkBlue));
        else if (i.Kind == "Image")
            smart = Ui.H(8, Ui.Pill("Save image", Ic.Image, () => { var p = ClipboardService.SaveImageCopy(i); if (p != null) { N.Toast(Ic.Image, "Saved to Pictures\\Brim"); } }, false, 28, 12));
        else if (i.Kind == "Color")
            smart = Ui.H(8, Ui.Pill("Copy as RGB", "", () =>
            {
                try { var c = (Color)ColorConverter.ConvertFromString(i.Text.Trim()); App.Clip.CopyText($"rgb({c.R}, {c.G}, {c.B})"); N.Toast("", "RGB copied"); } catch { }
            }, false, 28, 12));

        var stack = Ui.V(8, top, body, smart);
        var card = Ui.Card(stack, 12, new Thickness(12, 10, 10, 10));
        card.Margin = new Thickness(0, 0, 0, 8);
        if (i.Pinned) card.Background = Pal.Hex("#1FFFFFFF");
        return card;
    }

    static Border Badge(string glyph, string text) => new()
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 7, 2), Background = Pal.Hex("#1AFFFFFF"),
        Child = Ui.H(4, Ui.Icon(glyph, 10, Pal.Sub), Ui.T(text, 11, FontWeights.SemiBold, Pal.Sub)),
    };
}

/// <summary>Focus/break timer with a task list.</summary>
public sealed class FocusView : NotchView
{
    readonly TextBlock _time = Ui.Big("25:00", 46, FontWeights.SemiBold);
    readonly TextBlock _playGlyph = Ui.Icon(Ic.Play, 18, Pal.Ink);
    readonly Meter _progress = new(Pal.White, 3);
    readonly TextBlock _focusMin = Ui.T("25 min", 14, FontWeights.SemiBold), _breakMin = Ui.T("5 min", 14, FontWeights.SemiBold);
    readonly Border _modeHost = new();
    readonly StackPanel _tasks = new();
    readonly Grid _emptyTasks;
    readonly TextBox _input = Ui.Input("New task, then Enter", 40);
    string _shownMode = "";

    public FocusView(NotchWindow n) : base(n)
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        ColumnDefinitions.Add(new ColumnDefinition());
        Height = 240;

        var reset = Ui.Circle(Ic.History, () => App.Focus.Reset(), 42, 15, Pal.Hex("#26FFFFFF"), tip: "Reset");
        var play = Ui.Btn(new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(25), Background = Pal.White, Child = _playGlyph }, () => App.Focus.Toggle());
        var timeRow = Ui.G("*,Auto", "", _time, Ui.H(10, reset, play).VA(VerticalAlignment.Center).At(1));
        Grid Stepper(string label, string which, TextBlock value) => Ui.G("*,Auto,70,Auto", "",
            Ui.T(label, 14, FontWeights.SemiBold, Pal.Sub),
            Ui.Circle(Ic.Remove, () => App.Focus.Adjust(which, which == "Focus" ? -5 : -1), 30, 11, Pal.Hex("#1AFFFFFF")).At(1),
            value.HA(HorizontalAlignment.Center).At(2),
            Ui.Circle(Ic.Add, () => App.Focus.Adjust(which, which == "Focus" ? 5 : 1), 30, 11, Pal.Hex("#1AFFFFFF")).At(3));
        var settings = Ui.Card(Ui.V(10, Stepper("Focus", "Focus", _focusMin), Stepper("Break", "Break", _breakMin)), 14, new Thickness(16, 12, 12, 12));
        var left = Ui.G("*", "Auto,Auto,Auto,Auto,*", _modeHost.HA(HorizontalAlignment.Left), timeRow.M(0, 8, 0, 0).At(0, 1), _progress.M(0, 10, 0, 0).At(0, 2), settings.M(0, 16, 0, 0).At(0, 3));
        left.Margin = new Thickness(0, 0, 20, 0);
        Children.Add(left);
        Children.Add(new Border { Background = Pal.Line }.At(1));

        _emptyTasks = Ui.G("*", "*", Ui.V(8, Ui.Icon(Ic.List, 18, Pal.Sub), Ui.T("Nothing on your list yet", 13, null, Pal.Sub)).VA(VerticalAlignment.Center).HA(HorizontalAlignment.Center));
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        var addRow = Ui.G("*,Auto", "", _input, Ui.Circle(Ic.Add, Add, 40, 14, Pal.Hex("#26FFFFFF")).M(10, 0, 0, 0).At(1));
        var listHost = new Grid();
        listHost.Children.Add(_emptyTasks);
        listHost.Children.Add(Scroll(_tasks, 0));
        var right = Ui.G("*", "Auto,*,Auto", Ui.T("Tasks", 14, FontWeights.SemiBold), listHost.M(0, 8, 0, 8).At(0, 1), addRow.At(0, 2));
        right.Margin = new Thickness(20, 0, 0, 0);
        Children.Add(right.At(2));
        App.Focus.Changed += () => { if (IsVisible) Refresh(); };
    }

    void Add() { App.Focus.AddTask(_input.Text); _input.Text = ""; RebuildTasks(); }

    public override void OnShow() { RebuildTasks(); Refresh(); }

    void RebuildTasks()
    {
        _tasks.Children.Clear();
        foreach (var t in S.Tasks)
        {
            var box = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5),
                BorderBrush = t.Done ? Pal.Green : Pal.Sub, Background = t.Done ? Pal.Green : Pal.HitClear,
                Child = t.Done ? Ui.Icon(Ic.Check, 10, Pal.Ink) : null, Cursor = Cursors.Hand,
            };
            var task = t;
            box.MouseLeftButtonUp += (_, _) => { App.Focus.ToggleTask(task); RebuildTasks(); };
            var text = Ui.T(t.Text, 13, null, t.Done ? Pal.Dim : Pal.Text);
            if (t.Done) text.TextDecorations = TextDecorations.Strikethrough;
            var row = Ui.G("Auto,*,Auto", "", box, text.M(10, 0, 6, 0).At(1), Ui.Circle(Ic.Close, () => { App.Focus.RemoveTask(task); RebuildTasks(); }, 24, 9, fg: Pal.Dim).At(2));
            row.Margin = new Thickness(2, 3, 0, 3);
            _tasks.Children.Add(row);
        }
        _emptyTasks.Visibility = S.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public override void Refresh()
    {
        var f = App.Focus;
        if (_shownMode != f.Mode)
        {
            _shownMode = f.Mode;
            _modeHost.Child = Ui.Segmented(["Focus", "Break"], f.Mode, m => App.Focus.SetMode(m), true, null, 32, 13);
        }
        _time.Text = Fmt.Clock(f.Remaining);
        _playGlyph.Text = f.Running ? Ic.Pause : Ic.Play;
        _progress.Value = f.Total > TimeSpan.Zero ? 1 - f.Remaining / f.Total : 0;
        _focusMin.Text = $"{S.FocusMinutes} min";
        _breakMin.Text = $"{S.BreakMinutes} min";
    }
}

/// <summary>Stopwatch with laps.</summary>
public sealed class StopwatchView : NotchView
{
    readonly TextBlock _time = Ui.Big("00:00.00", 46, FontWeights.SemiBold);
    readonly TextBlock _startText = Ui.T("Start", 13, FontWeights.SemiBold, Pal.Ink);
    readonly Border _startBtn;
    readonly StackPanel _laps = new();

    public StopwatchView(NotchWindow n) : base(n)
    {
        _time.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _time.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        _startBtn = new Border { Height = 38, Width = 110, CornerRadius = new CornerRadius(19), Background = Pal.White, Child = _startText.HA(HorizontalAlignment.Center) };
        var buttons = Ui.H(10,
            Ui.Pill("Lap", Ic.Stopwatch, () => App.Focus.SwLap(), false, 38),
            Ui.Pill("Reset", Ic.History, () => App.Focus.SwReset(), false, 38),
            Ui.Btn(_startBtn, () => App.Focus.SwToggle()));
        var top = Ui.G("*,Auto", "", _time, buttons.VA(VerticalAlignment.Center).At(1));
        Children.Add(Ui.V(12, top, Scroll(_laps, 150)));
        App.Focus.SwChanged += () => { if (IsVisible) Refresh(); };
    }

    public override bool WantsFrames => App.Focus.SwRunning;
    public override void Frame(double dt) => _time.Text = Format(App.Focus.SwElapsed);
    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.ff") : t.ToString(@"mm\:ss\.ff");

    public override void Refresh()
    {
        var f = App.Focus;
        _time.Text = Format(f.SwElapsed);
        _startText.Text = f.SwRunning ? "Stop" : f.SwElapsed > TimeSpan.Zero ? "Resume" : "Start";
        _startBtn.Background = f.SwRunning ? Pal.Red : Pal.White;
        _startText.Foreground = f.SwRunning ? Pal.White : Pal.Ink;
        _laps.Children.Clear();
        for (int i = 0; i < f.Laps.Count; i++)
        {
            var split = i + 1 < f.Laps.Count ? f.Laps[i] - f.Laps[i + 1] : f.Laps[i];
            var row = Ui.G("*,*,*", "", Ui.T($"Lap {f.Laps.Count - i}", 13, null, Pal.Sub), Ui.T("+" + Format(split), 13).At(1), Ui.T(Format(f.Laps[i]), 13, FontWeights.SemiBold).HA(HorizontalAlignment.Right).At(2));
            var c = Ui.Card(row, 10, new Thickness(14, 7, 14, 7));
            c.Margin = new Thickness(0, 0, 0, 6);
            _laps.Children.Add(c);
        }
        N.Relayout();
    }
}

/// <summary>Teleprompter that scrolls your script under the camera.</summary>
public sealed class TeleprompterView : NotchView
{
    readonly TextBlock _cur = Ui.T("", 21, FontWeights.Bold);
    readonly TextBlock _next = Ui.T("", 16, FontWeights.SemiBold, Pal.Dim);
    readonly TextBlock _next2 = Ui.T("", 16, FontWeights.SemiBold, Pal.Dim);
    readonly TextBlock _lineInfo = Ui.T("", 12, FontWeights.SemiBold, Pal.Sub);
    readonly TextBlock _wpm = Ui.T("150", 15, FontWeights.Bold);
    readonly TextBlock _startText = Ui.T("Start", 13, FontWeights.SemiBold, Pal.Ink);
    readonly TextBlock _startGlyph = Ui.Icon(Ic.Play, 11, Pal.Ink);
    readonly Slider _seek;
    readonly TextBlock _elapsed = Ui.T("0:00", 11, null, Pal.Sub), _remain = Ui.T("-0:00", 11, null, Pal.Sub);
    readonly TextBox _editor = new();
    readonly Grid _reader, _editPanel;
    readonly TextBlock _hideText = Ui.T("Hide from sharing", 12, FontWeights.SemiBold, Pal.Sub);
    readonly TextBlock _hideGlyph = Ui.Icon(Ic.Lock, 11, Pal.Sub);
    List<string> _lines = [];
    int _index;
    double _lineProgress;
    bool _running, _seeking;

    public TeleprompterView(NotchWindow n) : base(n)
    {
        var hide = Ui.Btn(Ui.H(6, _hideGlyph, _hideText), () => { S.TeleHide = !S.TeleHide; S.Changed("TeleHide"); Paint(); N.UpdateCaptureExclusion(); });
        var head = Ui.G("*,Auto", "", Ui.T("Teleprompter", 14, FontWeights.SemiBold), Ui.H(16, hide, _lineInfo).At(1));

        var caret = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Pal.Yellow, Margin = new Thickness(0, 3, 10, 3) };
        _cur.TextWrapping = TextWrapping.Wrap; _cur.TextTrimming = TextTrimming.None; _cur.TextAlignment = TextAlignment.Center;
        _next.TextAlignment = _next2.TextAlignment = TextAlignment.Center;
        var curRow = Ui.G("Auto,*", "", caret, _cur.At(1));
        curRow.HorizontalAlignment = HorizontalAlignment.Center;
        var underline = new Border { Height = 1, Background = Pal.Hex("#33FFFFFF"), Margin = new Thickness(40, 4, 40, 6) };
        var text = Ui.V(0, curRow, underline, _next, _next2);
        text.VerticalAlignment = VerticalAlignment.Center;

        var startBtn = Ui.Btn(new Border { Height = 36, Padding = new Thickness(18, 0, 22, 0), CornerRadius = new CornerRadius(18), Background = Pal.White, Child = Ui.H(8, _startGlyph, _startText).VA(VerticalAlignment.Center) }, Toggle);
        var nav = Ui.Card(Ui.H(0,
            Ui.Circle(Ic.History, Restart, 32, 13, tip: "Restart"),
            Ui.Circle(Ic.ChevUp, () => Go(_index - 1), 32, 12, tip: "Previous line"),
            Ui.Circle(Ic.ChevDown, () => Go(_index + 1), 32, 12, tip: "Next line")), 18, new Thickness(3));
        var wpm = Ui.Card(Ui.H(6,
            Ui.Circle(Ic.Remove, () => Wpm(-10), 28, 10),
            Ui.V(-3, _wpm.HA(HorizontalAlignment.Center), Ui.T("WPM", 9, FontWeights.Bold, Pal.Sub).HA(HorizontalAlignment.Center)),
            Ui.Circle(Ic.Add, () => Wpm(10), 28, 10)), 18, new Thickness(4, 2, 4, 2));
        var controls = Ui.G("Auto,Auto,*,Auto,Auto", "", nav, startBtn.M(10, 0, 0, 0).At(1), wpm.At(3), Ui.Square(Ic.Edit, ToggleEdit, 36, 13, "Edit script").M(10, 0, 0, 0).At(4));

        _seek = Ui.Slider(0, 1, 0, null, true);
        _seek.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _seek.PreviewMouseLeftButtonUp += (_, _) => { _seeking = false; Go((int)Math.Round(_seek.Value * Math.Max(0, _lines.Count - 1))); };
        var seekRow = Ui.G("Auto,*,Auto", "", _elapsed, _seek.M(12, 0, 12, 0).At(1), _remain.At(2));

        _reader = Ui.G("*", "Auto,*,Auto,Auto", head, text.M(0, 10, 0, 10).At(0, 1), controls.At(0, 2), seekRow.M(0, 10, 0, 0).At(0, 3));
        _reader.Height = 230;

        _editor.Style = Ui.Res("Input");
        _editor.AcceptsReturn = true;
        _editor.TextWrapping = TextWrapping.Wrap;
        _editor.VerticalContentAlignment = VerticalAlignment.Top;
        _editor.Padding = new Thickness(14, 10, 14, 10);
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.Tag = "Paste or type your script. Each line or sentence becomes a step.";
        _editPanel = Ui.G("*", "Auto,*,Auto",
            Ui.T("Edit script", 14, FontWeights.SemiBold),
            _editor.M(0, 10, 0, 10).At(0, 1),
            Ui.H(8, Ui.Pill("Done", Ic.Check, ToggleEdit, true, 34)).HA(HorizontalAlignment.Right).At(0, 2));
        _editPanel.Height = 230;
        _editPanel.Visibility = Visibility.Collapsed;
        Children.Add(_reader);
        Children.Add(_editPanel);
        Parse();
    }

    void Parse()
    {
        _lines = S.Script.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(l => System.Text.RegularExpressions.Regex.Split(l, @"(?<=[.!?])\s+(?=[A-Z0-9])"))
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (_lines.Count == 0) _lines = ["Tap the pencil to add your script."];
        _index = Math.Clamp(_index, 0, _lines.Count - 1);
        Paint();
    }

    double LineSeconds(int i) => Math.Max(1.2, _lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / (S.Wpm / 60.0));
    double TotalSeconds => Enumerable.Range(0, _lines.Count).Sum(LineSeconds);
    double ElapsedSeconds => Enumerable.Range(0, _index).Sum(LineSeconds) + _lineProgress * LineSeconds(_index);

    void Paint()
    {
        _cur.Text = _lines[_index];
        _next.Text = _index + 1 < _lines.Count ? _lines[_index + 1] : "";
        _next2.Text = _index + 2 < _lines.Count ? _lines[_index + 2] : "";
        _lineInfo.Text = $"Line {_index + 1} of {_lines.Count}";
        _wpm.Text = S.Wpm.ToString();
        _startText.Text = _running ? "Pause" : "Start";
        _startGlyph.Text = _running ? Ic.Pause : Ic.Play;
        _hideText.Foreground = _hideGlyph.Foreground = S.TeleHide ? Pal.Yellow : Pal.Sub;
        _hideGlyph.Text = S.TeleHide ? Ic.Lock : Ic.Unlock;
        var total = TotalSeconds;
        var el = ElapsedSeconds;
        if (!_seeking) _seek.Value = _lines.Count > 1 ? (double)_index / (_lines.Count - 1) : 0;
        _elapsed.Text = Fmt.Clock(TimeSpan.FromSeconds(el));
        _remain.Text = "-" + Fmt.Clock(TimeSpan.FromSeconds(Math.Max(0, total - el)));
    }

    void Toggle()
    {
        _running = !_running;
        if (_running && _index >= _lines.Count - 1 && _lineProgress >= 1) Restart();
        N.SetPinned(_running || N.Pinned);
        Paint();
    }

    void Restart() { _index = 0; _lineProgress = 0; Paint(); }
    void Go(int i) { _index = Math.Clamp(i, 0, _lines.Count - 1); _lineProgress = 0; Paint(); }
    void Wpm(int d) { S.Wpm = Math.Clamp(S.Wpm + d, 60, 300); S.Changed("Wpm"); Paint(); }

    void ToggleEdit()
    {
        bool editing = _editPanel.Visibility == Visibility.Visible;
        if (editing)
        {
            S.Script = _editor.Text;
            S.Changed("Script");
            _index = 0; _lineProgress = 0;
            Parse();
        }
        else
        {
            _running = false;
            _editor.Text = S.Script;
            N.FocusInput(_editor);
        }
        _editPanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _reader.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }

    public override bool WantsFrames => _running;

    public override void Frame(double dt)
    {
        if (!_running) return;
        _lineProgress += dt / LineSeconds(_index);
        if (_lineProgress >= 1)
        {
            if (_index < _lines.Count - 1) { _index++; _lineProgress = 0; }
            else { _lineProgress = 1; _running = false; }
            Paint();
        }
        else
        {
            var el = ElapsedSeconds;
            _elapsed.Text = Fmt.Clock(TimeSpan.FromSeconds(el));
            _remain.Text = "-" + Fmt.Clock(TimeSpan.FromSeconds(Math.Max(0, TotalSeconds - el)));
        }
    }

    public override void OnShow() { Paint(); N.UpdateCaptureExclusion(); }
    public override void OnHide() { N.UpdateCaptureExclusion(); }
}

/// <summary>Screenshots and screen recordings.</summary>
public sealed class CaptureView : NotchView
{
    readonly Grid _recIdle, _recActive;
    readonly TextBlock _recTime = Ui.T("0:00", 14, FontWeights.Bold);
    readonly Border _sysPill, _micPill;

    public CaptureView(NotchWindow n) : base(n)
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ColumnDefinitions.Add(new ColumnDefinition());

        var shotHead = Ui.G("*,Auto", "", Ui.H(8, Ui.Icon(Ic.Camera, 14), Ui.T("Screenshot", 14, FontWeights.SemiBold)),
            Ui.Circle(Ic.Folder, () => { Directory.CreateDirectory(CaptureService.ShotsDir); Shell.Open(CaptureService.ShotsDir); }, 28, 12, tip: "Open folder").At(1));
        var shotTiles = Ui.G("*,10,*", "", Tile(Ic.Monitor, "Full screen", () => N.Screenshot(false), false), Tile(Ic.Crop, "Area", () => N.Screenshot(true), false).At(2));
        var shotNote = Ui.T("Screenshots go to Pictures › Screenshots and onto your clipboard.", 12, null, Pal.Dim).Wrap();
        var shot = Ui.Card(Ui.V(12, shotHead, shotTiles, shotNote), 16, new Thickness(14, 12, 14, 14));
        Children.Add(shot);

        var recHead = Ui.G("*,Auto", "", Ui.H(8, Ui.Icon("", 14, Pal.Red), Ui.T("Screen recording", 14, FontWeights.SemiBold)),
            Ui.Circle(Ic.Folder, () => { Directory.CreateDirectory(CaptureService.VideoDir); Shell.Open(CaptureService.VideoDir); }, 28, 12, tip: "Open folder").At(1));
        var recTiles = Ui.G("*,10,*", "", Tile(Ic.Monitor, "Full screen", () => N.ToggleRecording(false), true), Tile(Ic.Crop, "Area", () => N.ToggleRecording(true), true).At(2));
        _sysPill = Toggle(Ic.Speaker, "System sound", () => { S.RecordSystemSound = !S.RecordSystemSound; S.Changed("Rec"); Paint(); });
        _micPill = Toggle(Ic.Mic, "Microphone", () => { S.RecordMic = !S.RecordMic; S.Changed("Rec"); Paint(); });
        _recIdle = Ui.G("*", "Auto,Auto", recTiles, Ui.H(8, _sysPill, _micPill).M(0, 12, 0, 0).At(0, 1));
        var stop = new Border
        {
            Height = 66, CornerRadius = new CornerRadius(14), Background = Pal.Hex("#33FF5F57"),
            Child = Ui.H(10, new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = Pal.Red }, Ui.T("Stop recording", 14, FontWeights.SemiBold, Pal.Red), _recTime).HA(HorizontalAlignment.Center).VA(VerticalAlignment.Center),
        };
        _recActive = Ui.G("*", "Auto", Ui.Btn(stop, () => N.ToggleRecording(null)));
        var recBody = new Grid();
        recBody.Children.Add(_recIdle);
        recBody.Children.Add(_recActive);
        var rec = Ui.Card(Ui.V(12, recHead, recBody), 16, new Thickness(14, 12, 14, 14));
        Children.Add(rec.At(2));
        App.Capture.Changed += () => { if (IsVisible) Refresh(); };
        Paint();
    }

    static Border Tile(string glyph, string text, Action a, bool red)
    {
        var fg = red ? Pal.Red : Pal.Text;
        var b = new Border
        {
            Height = 66, CornerRadius = new CornerRadius(14),
            Background = red ? Pal.Hex("#2EFF5F57") : Pal.Hex("#1AFFFFFF"),
            Child = Ui.V(6, Ui.Icon(glyph, 17, fg), Ui.T(text, 13, FontWeights.SemiBold, fg).HA(HorizontalAlignment.Center)).VA(VerticalAlignment.Center),
        };
        b.Hoverable(b.Background, red ? Pal.Hex("#40FF5F57") : Pal.Hex("#2AFFFFFF"));
        b.OnClick(a);
        return b;
    }

    static Border Toggle(string glyph, string text, Action a)
    {
        var b = new Border { Height = 32, Padding = new Thickness(12, 0, 14, 0), CornerRadius = new CornerRadius(16), Cursor = Cursors.Hand };
        b.Child = Ui.H(6, Ui.Icon(glyph, 12), Ui.T(text, 13, FontWeights.SemiBold)).VA(VerticalAlignment.Center);
        b.MouseLeftButtonUp += (_, _) => a();
        return b;
    }

    void Paint()
    {
        void P(Border b, bool on)
        {
            b.Background = on ? Pal.White : Pal.Hex("#1AFFFFFF");
            foreach (var t in ((StackPanel)b.Child).Children.OfType<TextBlock>()) t.Foreground = on ? Pal.Ink : Pal.Sub;
        }
        P(_sysPill, S.RecordSystemSound);
        P(_micPill, S.RecordMic);
    }

    public override void Refresh()
    {
        bool rec = App.Capture.Recording;
        _recIdle.Visibility = rec ? Visibility.Collapsed : Visibility.Visible;
        _recActive.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        _recTime.Text = Fmt.Clock(DateTime.Now - App.Capture.RecordStart);
    }
}
