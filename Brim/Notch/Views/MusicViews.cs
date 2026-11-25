using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brim.Core;
using Brim.Services;

namespace Brim.Notch.Views;

/// <summary>Now playing with scrubber, output switch, fullscreen mode and synced lyrics.</summary>
public sealed class MusicView : NotchView
{
    readonly Border _art = new() { Width = 66, Height = 66, CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = Pal.Track };
    readonly TextBlock _title = Ui.T("Not playing", 16, FontWeights.SemiBold);
    readonly TextBlock _sub = Ui.T("Play something to see it here", 13, null, Pal.Sub);
    readonly Slider _scrub;
    readonly TextBlock _pos = Ui.T("0:00", 11, null, Pal.Sub), _left = Ui.T("-0:00", 11, null, Pal.Sub);
    readonly TextBlock _playGlyph = Ui.Icon(Ic.Play, 18, Pal.Ink);
    readonly TextBlock _prevLine = Ui.T("", 15, FontWeights.SemiBold, Pal.Dim);
    readonly TextBlock _curLine = Ui.T("", 20, FontWeights.Bold, Pal.Text);
    readonly TextBlock _nextLine = Ui.T("", 16, FontWeights.SemiBold, Pal.Dim);
    readonly TextBlock _dots = Ui.T("•  •  •", 18, FontWeights.Bold, Pal.Text);
    readonly Bars _bigBars = new() { Height = 70, BarWidth = 6, Gap = 5, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _noLyrics = Ui.T("No synced lyrics for this track", 12, null, Pal.Dim);
    readonly Grid _lyricsPanel = new();
    readonly Grid _vizPanel = new();
    readonly TextBlock _output = Ui.T(Short(App.Audio.DefaultName), 12, FontWeights.SemiBold);
    List<LyricLine>? _lyrics;
    string _lyricsKey = "";
    bool _seeking;

    public MusicView(NotchWindow n) : base(n)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        ColumnDefinitions.Add(new ColumnDefinition());
        Height = 252;

        _scrub = Ui.Slider(0, 1, 0, null, true);
        _scrub.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _scrub.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _seeking = false;
            if (App.Media.Duration > TimeSpan.Zero) App.Media.Seek(App.Media.Duration * _scrub.Value);
        };

        var head = Ui.G("Auto,*", "", _art, Ui.V(4, _title, _sub).M(14, 0, 0, 0).VA(VerticalAlignment.Center).At(1));
        var times = Ui.G("*,*", "", _pos, _left.HA(HorizontalAlignment.Right).At(1));
        var play = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Pal.White, Child = _playGlyph };
        var controls = Ui.H(40,
            Ui.Btn(Ui.Icon(Ic.Prev, 16), () => App.Media.Prev()),
            Ui.Btn(play, () => App.Media.Toggle()),
            Ui.Btn(Ui.Icon(Ic.Next, 16), () => App.Media.Next()));
        controls.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (UIElement c in controls.Children) if (c is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;

        var outPill = new Border
        {
            Height = 32, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 0, 10, 0), MaxWidth = 156,
            Child = Ui.G("Auto,*,Auto", "", Ui.Icon(Ic.Speaker, 12, Pal.LinkBlue), _output.M(7, 0, 0, 0).At(1), Ui.Icon(Ic.ChevDown, 9, Pal.Sub).M(7, 0, 0, 0).At(2)),
        };
        outPill.SetResourceReference(Border.BackgroundProperty, "CardBg");
        outPill.Hoverable(null, Pal.Hover);
        Button? output = null;
        output = Ui.Btn(outPill, () =>
        {
            var m = new ContextMenu { PlacementTarget = output, Placement = PlacementMode.Bottom };
            foreach (var (id, name) in App.Audio.Outputs())
            {
                var mi = new MenuItem { Header = name, IsChecked = id == App.Audio.DefaultId };
                mi.Click += (_, _) => { App.Audio.SetDefaultOutput(id); _output.Text = Short(name); };
                m.Items.Add(mi);
            }
            m.IsOpen = true;
        }, "Audio output");
        App.Audio.DevicesChanged += () => _output.Text = Short(App.Audio.DefaultName);

        var vizBtn = Ui.Circle(Ic.Robot, () => { S.LyricsPanel = !S.LyricsPanel; S.Changed("LyricsPanel"); UpdatePanels(); }, 32, 14, Pal.Hex("#26FFFFFF"), tip: "Lyrics / visualizer");
        var full = Ui.Pill("Fullscreen", Ic.Expand, () => N.OpenFullscreenMusic(), false, 32, 12);
        var bottom = Ui.H(8, output, vizBtn, full);

        var left = Ui.G("*", "Auto,*,Auto,Auto,*,Auto,*,Auto", head, _scrub.M(0, 0, 0, 0).At(0, 2), times.At(0, 3), controls.At(0, 5), bottom.At(0, 7));
        Children.Add(left);

        _curLine.TextWrapping = TextWrapping.Wrap; _curLine.TextTrimming = TextTrimming.None;
        _nextLine.TextWrapping = TextWrapping.Wrap; _nextLine.TextTrimming = TextTrimming.None;
        _curLine.MaxHeight = 82; _nextLine.MaxHeight = 44;
        var lyr = Ui.V(10, _prevLine, _curLine, _nextLine, _dots);
        lyr.VerticalAlignment = VerticalAlignment.Center;
        _lyricsPanel.Children.Add(lyr);
        _lyricsPanel.Children.Add(Ui.V(10, _noLyrics.HA(HorizontalAlignment.Center)).VA(VerticalAlignment.Bottom));
        _vizPanel.Children.Add(_bigBars.VA(VerticalAlignment.Center));
        var right = new Grid();
        right.Children.Add(_lyricsPanel);
        right.Children.Add(_vizPanel);
        Children.Add(right.At(2));
        App.Media.Changed += () => { if (IsVisible) RefreshMedia(); };
    }

    static string Short(string s) => s.Length > 18 ? s[..17] + "…" : s;

    void UpdatePanels()
    {
        bool lyricsMode = S.LyricsPanel && _lyrics is { Count: > 0 };
        _lyricsPanel.Visibility = S.LyricsPanel ? Visibility.Visible : Visibility.Collapsed;
        _vizPanel.Visibility = S.LyricsPanel && lyricsMode ? Visibility.Collapsed : Visibility.Visible;
        _noLyrics.Visibility = S.LyricsPanel && !lyricsMode && App.Media.HasSession ? Visibility.Visible : Visibility.Collapsed;
        if (!lyricsMode) { _prevLine.Text = _curLine.Text = _nextLine.Text = ""; _dots.Visibility = Visibility.Collapsed; }
    }

    public override void OnShow() { RefreshMedia(); }
    public override bool WantsFrames => true;

    void RefreshMedia()
    {
        var m = App.Media;
        if (m.HasSession)
        {
            _title.Text = m.Title;
            _sub.Text = m.Artist.Length > 0 ? m.Artist : m.IsPlaying ? "Playing" : "Paused";
            _art.Child = m.Art != null ? new Image { Source = m.Art, Stretch = Stretch.UniformToFill } : Ui.Icon(Ic.Music, 22, Pal.Sub);
            var c = m.ArtColor;
            _scrub.Foreground = Pal.Brush(Pal.Mix(c, Colors.White, 0.15));
            _bigBars.Fill = new LinearGradientBrush(Pal.Mix(c, Colors.White, .3), c, 90);
        }
        else
        {
            _title.Text = "Not playing";
            _sub.Text = "Play something to see it here";
            _art.Child = Ui.Icon(Ic.Music, 22, Pal.Sub);
        }
        _playGlyph.Text = m.IsPlaying ? Ic.Pause : Ic.Play;
        var key = m.Artist + "|" + m.Title;
        if (key != _lyricsKey)
        {
            _lyricsKey = key;
            _lyrics = null;
            if (m.HasSession) _ = LoadLyrics(m.Title, m.Artist, m.Duration, key);
        }
        UpdatePanels();
    }

    async Task LoadLyrics(string title, string artist, TimeSpan dur, string key)
    {
        var l = await App.Lyrics.GetAsync(title, artist, dur);
        if (key != _lyricsKey) return;
        _lyrics = l;
        UpdatePanels();
    }

    public override void Frame(double dt)
    {
        var m = App.Media;
        var d = m.Duration;
        var p = m.CurrentPosition;
        if (!_seeking) _scrub.Value = d > TimeSpan.Zero ? p / d : 0;
        _pos.Text = Fmt.Clock(_seeking && d > TimeSpan.Zero ? d * _scrub.Value : p);
        _left.Text = "-" + Fmt.Clock(d - p);
        if (_vizPanel.Visibility == Visibility.Visible) _bigBars.Set(m.IsPlaying ? App.Audio.Bands(22) : new double[22]);
        if (_lyrics is { Count: > 0 } && S.LyricsPanel)
        {
            int idx = _lyrics.FindLastIndex(l => l.At <= p + TimeSpan.FromMilliseconds(250));
            var cur = idx >= 0 ? _lyrics[idx].Text : "";
            _prevLine.Text = idx > 0 ? _lyrics[idx - 1].Text : "";
            _curLine.Text = cur;
            _nextLine.Text = idx + 1 < _lyrics.Count ? _lyrics[idx + 1].Text : "";
            _dots.Visibility = string.IsNullOrWhiteSpace(cur) ? Visibility.Visible : Visibility.Collapsed;
            _curLine.Visibility = string.IsNullOrWhiteSpace(cur) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}

/// <summary>Per-app volume mixer.</summary>
public sealed class MixerView : NotchView
{
    readonly StackPanel _list = new();
    readonly TextBlock _empty = Ui.T("Nothing is playing sound right now", 13, null, Pal.Sub);
    List<AudioSessionInfo> _sessions = [];
    readonly Dictionary<int, (Slider s, TextBlock pct, TextBlock mute)> _rows = [];

    public MixerView(NotchWindow n) : base(n)
    {
        var head = Ui.G("*,Auto", "",
            Ui.H(8, Ui.Icon(Ic.Speaker, 14), Ui.T("Volume mixer", 14, FontWeights.SemiBold)),
            Ui.H(8,
                Ui.Pill("Mute all", Ic.Mute, () => { foreach (var s in _sessions) s.Muted = true; Rebuild(); }, false, 30, 12),
                Ui.Pill("Unmute all", Ic.Speaker, () => { foreach (var s in _sessions) s.Muted = false; Rebuild(); }, false, 30, 12)).At(1));
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(head);
        var body = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(Scroll(_list, 236));
        body.Children.Add(_empty.HA(HorizontalAlignment.Center).M(0, 30, 0, 30));
        Children.Add(body.At(0, 1));
    }

    public override void OnShow() => Rebuild();

    void Rebuild()
    {
        _sessions = App.Audio.Sessions();
        _list.Children.Clear();
        _rows.Clear();
        _empty.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // master first
        _list.Children.Add(MasterRow());
        foreach (var s in _sessions)
        {
            UIElement icon = s.ExePath != null && Shell.IconFor(s.ExePath) is ImageSource img
                ? new Image { Source = img, Width = 20, Height = 20 }
                : Ui.Icon(s.Name == "System sounds" ? Ic.Bell : Ic.Speaker, 15, Pal.Sub);
            var pct = Ui.T($"{Math.Round(s.Volume * 100)}%", 12, FontWeights.SemiBold, Pal.Sub);
            var slider = Ui.Slider(0, 1, s.Volume, v => { s.Volume = (float)v; pct.Text = $"{Math.Round(v * 100)}%"; });
            var muteGlyph = Ui.Icon(s.Muted ? Ic.Mute : Ic.Speaker, 13, s.Muted ? Pal.Red : Pal.Text);
            var mute = Ui.Btn(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Pal.Hex("#1AFFFFFF"), Child = muteGlyph },
                () => { s.Muted = !s.Muted; muteGlyph.Text = s.Muted ? Ic.Mute : Ic.Speaker; muteGlyph.Foreground = s.Muted ? Pal.Red : Pal.Text; });
            var row = Ui.G("Auto,150,*,48,Auto", "", icon, Ui.T(s.Name, 13, FontWeights.SemiBold).M(12, 0, 8, 0).At(1), slider.At(2),
                pct.HA(HorizontalAlignment.Right).At(3), mute.M(10, 0, 0, 0).At(4));
            var card = Ui.Card(row, 14, new Thickness(14, 8, 10, 8));
            card.Margin = new Thickness(0, 0, 0, 8);
            _list.Children.Add(card);
            _rows[s.Pid] = (slider, pct, muteGlyph);
        }
    }

    Border MasterRow()
    {
        var pct = Ui.T($"{Math.Round(App.Audio.Volume * 100)}%", 12, FontWeights.SemiBold);
        var slider = Ui.Slider(0, 1, App.Audio.Volume, v => { App.Audio.Volume = (float)v; pct.Text = $"{Math.Round(v * 100)}%"; });
        var row = Ui.G("Auto,150,*,48,Auto", "", Ui.Icon(Ic.Speaker, 15), Ui.T(App.Audio.DefaultName, 13, FontWeights.SemiBold).M(12, 0, 8, 0).At(1),
            slider.At(2), pct.HA(HorizontalAlignment.Right).At(3), new Border { Width = 40 }.At(4));
        var card = Ui.Card(row, 14, new Thickness(14, 8, 10, 8));
        card.Margin = new Thickness(0, 0, 0, 8);
        card.Background = Pal.Hex("#22FFFFFF");
        return card;
    }
}
