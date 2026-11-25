using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brim.Core;
using Brim.Services;

namespace Brim.Notch.Views;

/// <summary>Shared pieces for Sidekick: session cards, approval cards and the usage panel.</summary>
public static class AgentUi
{
    public static Brush Color(string agent) => agent switch
    {
        "Codex" => Pal.Teal,
        "Cursor" => Pal.Hex("#9AA4FF"),
        "Antigravity" => Pal.Hex("#5B9BFF"),
        _ => Pal.Orange,
    };

    public static Border Session(AgentSession s, bool compact = false)
    {
        var (label, color) = s.State switch
        {
            "working" => ("Working…", Pal.Orange),
            "attention" => ("Needs you", Pal.Red),
            _ => ("Done", Pal.Green),
        };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 3),
            Background = Pal.Brush(Pal.A(((SolidColorBrush)color).Color, 0x30)), Child = Ui.T(label, 11, FontWeights.Bold, color),
        };
        var title = Ui.H(8, Ui.T(s.Project, 14, FontWeights.SemiBold), compact ? null : Ui.T(s.Agent, 12, null, Pal.Sub));
        var msg = Ui.T(s.Message.Length > 0 ? s.Message : "—", 12, null, Pal.Sub).Wrap();
        msg.MaxHeight = compact ? 34 : 36;
        var right = compact ? (UIElement)badge : Ui.H(8, badge, Ui.T(Fmt.Ago(s.Updated), 11, null, Pal.Dim));
        var g = Ui.G("Auto,*,Auto", "Auto,Auto", new SidekickFace(compact ? 26 : 30, Color(s.Agent)).VA(VerticalAlignment.Top),
            title.M(12, 0, 8, 0).At(1), right.At(2), msg.M(12, 4, 0, 0).At(1, 1).Span(2));
        var card = Ui.Card(g, 14, new Thickness(12, 10, 12, 10));
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    /// <summary>Allow / Deny / Ask card for an agent waiting on a tool call.</summary>
    public static Border ApprovalCard(Approval a, bool inNotch)
    {
        var title = Ui.T($"{a.Agent} wants to use {Friendly(a.Tool)}", 14, FontWeights.SemiBold);
        var sub = Ui.T(a.Project, 12, null, Pal.Sub);
        var code = Ui.T(a.Detail.Length > 0 ? a.Detail : a.Tool, 12, null, Pal.Text).Wrap();
        code.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        code.MaxHeight = 36;
        var codeBox = new Border { CornerRadius = new CornerRadius(8), Background = Pal.Hex("#40000000"), Padding = new Thickness(10, 7, 10, 7), Child = code, Margin = new Thickness(0, 8, 0, 0) };
        var buttons = Ui.H(8,
            Ui.Pill("Allow", Ic.Check, () => App.Agents.Resolve(a, "allow"), true, 32, 13),
            Ui.Pill("Deny", Ic.Close, () => App.Agents.Resolve(a, "deny"), false, 32, 13, Pal.Hex("#33FF5F57"), Pal.Red),
            Ui.Pill($"Ask in {Short(a.Agent)}", null, () => App.Agents.Resolve(a, "ask"), false, 32, 13));
        buttons.Margin = new Thickness(0, 10, 0, 0);
        var g = Ui.G("Auto,*", "Auto,Auto,Auto,Auto", new SidekickFace(34, Color(a.Agent)).VA(VerticalAlignment.Top),
            title.M(12, 0, 0, 0).At(1), sub.M(12, 1, 0, 0).At(1, 1), codeBox.M(12, 8, 0, 0).At(1, 2), buttons.M(12, 10, 0, 0).At(1, 3));
        if (inNotch)
        {
            g.Margin = new Thickness(18, 16, 18, 16);
            g.Width = 470;
            return new Border { Child = g };
        }
        var card = Ui.Card(g, 14, new Thickness(14, 12, 14, 12));
        card.Background = Pal.Hex("#26FF9F43");
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    static string Short(string agent) => agent == "Claude Code" ? "Claude" : agent;

    static string Friendly(string tool) => tool switch
    {
        "Bash" or "run_command" or "Terminal" => "the terminal",
        "Edit" or "MultiEdit" or "Write" or "write_to_file" or "replace_file_content" => "edit files",
        "WebFetch" or "read_url_content" => "the web",
        "" => "a tool",
        _ => tool,
    };
}

/// <summary>Provider rings plus detail bars for the selected provider.</summary>
public sealed class UsagePanel : StackPanel
{
    readonly bool _narrow;
    readonly UniformGrid _tiles = new();
    readonly StackPanel _detail = new();

    public UsagePanel(bool narrow)
    {
        _narrow = narrow;
        _tiles.Columns = narrow ? 2 : 4;
        _tiles.Margin = new Thickness(-4, 0, -4, 0);
        Children.Add(_tiles);
        Children.Add(_detail);
        App.Usage.Changed += () => { if (IsVisible) Build(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) { App.Usage.RefreshIfStale(TimeSpan.FromMinutes(2)); Build(); } };
    }

    public void Build()
    {
        _tiles.Children.Clear();
        foreach (var p in App.Usage.All) _tiles.Children.Add(Tile(p));
        _detail.Children.Clear();
        var sel = App.Usage.All.FirstOrDefault(p => p.Name == App.S.UsageProvider) ?? App.Usage.Codex;
        var head = Ui.G("*,Auto", "",
            Ui.V(1, Ui.T(sel.Name, 14, FontWeights.SemiBold), Ui.T(sel.Source.Length > 0 ? sel.Source : sel.Note, 11, null, Pal.Dim)),
            Ui.Circle(Ic.Refresh, () => _ = App.Usage.RefreshAsync(), 28, 11, Pal.Hex("#1AFFFFFF"), tip: "Refresh").At(1));
        head.Margin = new Thickness(2, 12, 2, 8);
        _detail.Children.Add(head);
        if (sel.Windows.Count == 0)
            _detail.Children.Add(Ui.Card(Ui.T(sel.Note.Length > 0 ? sel.Note : "No usage reported", 13, null, Pal.Sub).Wrap(), 12, new Thickness(14, 12, 14, 12)));
        foreach (var w in sel.Windows)
        {
            double left = Math.Clamp(100 - w.UsedPercent, 0, 100);
            var meter = new Meter(left > 50 ? Pal.White : left > 20 ? Pal.Orange : Pal.Red, 5) { Value = left / 100 };
            var resets = w.ResetsAt is DateTime r ? " · Resets " + (r.Date == DateTime.Today ? r.ToString(App.S.Clock24h ? "HH:mm" : "h:mm tt") : r.ToString("d MMM, HH:mm")) : "";
            var body = Ui.V(6,
                Ui.G("*,Auto", "", Ui.T(w.Label, 13, FontWeights.SemiBold), Ui.H(4, Ui.Big($"{Math.Round(left)}", 17, FontWeights.SemiBold), Ui.T("% remaining", 11, null, Pal.Sub).VA(VerticalAlignment.Bottom).M(0, 0, 0, 2)).At(1)),
                meter,
                Ui.T($"{Math.Round(w.UsedPercent)}% used{resets}", 11, null, Pal.Dim));
            var c = Ui.Card(body, 12, new Thickness(14, 10, 14, 10));
            c.Margin = new Thickness(0, 0, 0, 6);
            _detail.Children.Add(c);
        }
        if (sel.Name == "Claude" && App.Usage.ClaudeTokensWeek > 0)
            _detail.Children.Add(Ui.T($"This PC: {UsageService.Tokens(App.Usage.ClaudeTokens5h)} tokens in the last 5 hours · {UsageService.Tokens(App.Usage.ClaudeTokensWeek)} this week", 11, null, Pal.Dim).Wrap().M(4, 4, 4, 0));
    }

    Border Tile(ProviderUsage p)
    {
        bool sel = App.S.UsageProvider == p.Name;
        var left = p.LeftPercent;
        var ring = new Ring(_narrow ? 30 : 36, 3);
        ring.Set((left ?? 0) / 100, left is null ? Pal.Dim : left > 50 ? Pal.Green : left > 20 ? Pal.Orange : Pal.Red, null);
        var face = new Grid { Width = ring.Width, Height = ring.Height };
        face.Children.Add(ring);
        face.Children.Add(Ui.T(p.Name[..1], 13, FontWeights.Bold, left is null ? Pal.Dim : Pal.Text).HA(HorizontalAlignment.Center));
        var stack = Ui.V(4, face.HA(HorizontalAlignment.Center), Ui.T(p.Name, 12, FontWeights.SemiBold).HA(HorizontalAlignment.Center),
            Ui.T(left is double l ? $"{Math.Round(l)}% left" : "—", 11, null, Pal.Sub).HA(HorizontalAlignment.Center));
        var b = Ui.Card(stack, 14, new Thickness(6, 10, 6, 10));
        b.Margin = new Thickness(4);
        if (sel) { b.Background = Pal.Hex("#26FFFFFF"); b.BorderBrush = Pal.Hex("#40FFFFFF"); b.BorderThickness = new Thickness(1); }
        b.OnClick(() => { App.S.UsageProvider = p.Name; App.S.Changed("UsageProvider"); Build(); });
        return b;
    }
}

/// <summary>Sidekick tab: agent activity, plan usage and pending approvals.</summary>
public sealed class AgentsView : NotchView
{
    readonly StackPanel _pending = new();
    readonly StackPanel _list = new();
    readonly UsagePanel _usage = new(false);
    readonly Border _segHost = new();
    readonly Grid _activity = new();
    string _mode = "Activity";

    public AgentsView(NotchWindow n) : base(n)
    {
        var head = Ui.G("*,Auto", "",
            Ui.H(10, new SidekickFace(22, Pal.Orange), Ui.T("Agents", 15, FontWeights.SemiBold), _segHost),
            Ui.H(8,
                Ui.Pill("Desktop panel", Ic.Screen, () => { N.Collapse(); App.OpenDesktopPanel(); }, false, 30, 12),
                Ui.Circle(Ic.Gear, () => App.OpenSettings("Agents"), 30, 13, Pal.Hex("#1AFFFFFF"), tip: "Set up agents")).At(1));
        _activity.Children.Add(Scroll(_list, 236));
        var body = new Grid();
        body.Children.Add(_activity);
        body.Children.Add(Scroll(_usage, 300));
        Children.Add(Ui.V(12, head, _pending, body));
        BuildSeg();
        App.Agents.Changed += () => { if (IsVisible) Refresh(); };
    }

    void BuildSeg() => _segHost.Child = Ui.Segmented(["Activity", "Usage"], _mode, m => { _mode = m; BuildSeg(); Refresh(); N.Relayout(); }, true, null, 28, 12);

    public override void OnShow() => Refresh();

    public override void Refresh()
    {
        _pending.Children.Clear();
        foreach (var a in App.Agents.Pending) _pending.Children.Add(AgentUi.ApprovalCard(a, false));
        _activity.Visibility = _mode == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)_usage.Parent).Visibility = _mode == "Usage" ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == "Usage") { _usage.Build(); return; }

        _list.Children.Clear();
        if (App.Agents.Sessions.Count == 0)
        {
            var hint = Ui.T("Connect Claude Code, Codex, Cursor or Antigravity in Settings → Sidekick. When an agent finishes, needs you, or asks to run something, the notch tells you.", 12, null, Pal.Sub).Wrap();
            hint.TextAlignment = TextAlignment.Center;
            hint.MaxWidth = 440;
            var empty = Ui.V(8, new SidekickFace(46, Pal.Orange).HA(HorizontalAlignment.Center),
                Ui.T("No agent activity yet", 14, FontWeights.SemiBold).HA(HorizontalAlignment.Center), hint.HA(HorizontalAlignment.Center));
            empty.Margin = new Thickness(0, 14, 0, 14);
            _list.Children.Add(empty);
        }
        else foreach (var s in App.Agents.Sessions) _list.Children.Add(AgentUi.Session(s));
        N.Relayout();
    }
}
