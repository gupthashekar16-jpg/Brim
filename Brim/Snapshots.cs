using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brim.Core;
using Brim.Notch;
using Brim.Settings;

namespace Brim;

/// <summary>"Brim.exe --snapshot DIR" renders every screen to PNG for visual checks, then exits.</summary>
internal static class Snapshots
{
    public static async Task RunAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var n = App.Notch;
        await Task.Delay(3500);

        n.SimulateAppInFront(false);
        n.Settle();
        Save(n.Island, Path.Combine(dir, "00-closed.png"));
        n.SimulateAppInFront(true);
        n.Settle();
        Save(n.Island, Path.Combine(dir, "00b-tucked.png"));
        n.SimulateAppInFront(false);
        n.Settle();

        int i = 1;
        foreach (var tab in App.S.TabOrder.Where(NotchWindow.TabOn))
        {
            n.Open(tab);
            await Task.Delay(tab is "Bluetooth" or "Weather" ? 2500 : 700);
            n.Settle();
            await Task.Delay(150);
            n.Settle();
            Save(n.Island, Path.Combine(dir, $"{i++:00}-{tab}.png"));
        }
        n.Collapse();

        App.S.Effect = "Synthwave";
        n.ApplySettings();
        n.Open("Home");
        await Task.Delay(500);
        n.Settle();
        Save(n.Island, Path.Combine(dir, "50-effect-synthwave.png"));
        App.S.Effect = "None";
        n.Collapse();
        n.ApplySettings();

        n.ShowAlert(new Notch.Views.SidekickFace(34, Pal.Orange), "demo-app · Claude Code finished", "Your changes are ready. All checks passed.", null, 9);
        await Task.Delay(400);
        n.Settle();
        Save(n.Island, Path.Combine(dir, "51-agent-alert.png"));

        // approval card in the closed notch, then on the open Sidekick tab
        n.Collapse();
        await Task.Delay(200);
        App.Agents.Demo();
        await Task.Delay(400);
        n.Settle();
        Save(n.Island, Path.Combine(dir, "52-approval-closed.png"));
        n.Open("Agents");
        await Task.Delay(500);
        n.Settle();
        Save(n.Island, Path.Combine(dir, "53-approval-open.png"));
        foreach (var a in App.Agents.Pending.ToList()) App.Agents.Resolve(a, "deny");
        await Task.Delay(300);

        // usage panel
        await App.Usage.RefreshAsync();
        var usage = new Notch.Views.UsagePanel(false);
        usage.Build();
        Save(Detached(usage, 640), Path.Combine(dir, "54-usage.png"), dark: true);

        // drop shelf with phone transfer (preview only: no port is opened)
        App.Phone.Preview("http://192.168.1.20:52144/3f9a1c0d2b7e4a6f81/");
        n.Open("Shelf");
        await Task.Delay(500);
        n.Settle();
        Save(n.Island, Path.Combine(dir, "55-shelf-phone.png"));
        App.Phone.Stop();
        n.Collapse();

        // desktop panel
        var screen = App.OpenDesktopPanel();
        foreach (var tab in new[] { "Music", "Timer", "Network", "System", "Work", "Usage" })
        {
            screen.ShowTab(tab);
            await Task.Delay(tab == "Network" ? 2500 : 600);
            screen.UpdateLayout();
            Save((FrameworkElement)screen.Content, Path.Combine(dir, $"56-screen-{tab}.png"), dark: true);
        }
        screen.Close();

        var sw = new SettingsWindow();
        sw.Show();
        foreach (var page in new[] { "General", "Appearance", "Widgets", "Agents", "Tips", "About" })
        {
            sw.Navigate(page);
            await Task.Delay(500);
            sw.UpdateLayout();
            Save((FrameworkElement)sw.Content, Path.Combine(dir, $"60-settings-{page}.png"), dark: true);
            Save(sw.Page, Path.Combine(dir, $"61-settings-{page}-full.png"), dark: true);
        }
        sw.Close();
    }

    /// <summary>"Brim.exe --snapshot DIR --record-test" also records 4 s of the screen and reports the file.</summary>
    public static async Task RecordTestAsync(string dir)
    {
        var report = Path.Combine(dir, "record-test.txt");
        try
        {
            var path = await App.Capture.StartRecordingAsync(App.Notch.Screen.Bounds, null, true, false);
            if (path == null) { File.WriteAllText(report, "start failed (see log)"); return; }
            await Task.Delay(4000);
            var saved = await App.Capture.StopRecordingAsync();
            var len = saved != null && File.Exists(saved) ? new FileInfo(saved).Length : -1;
            File.WriteAllText(report, $"saved={saved}\nbytes={len}");
        }
        catch (Exception e) { File.WriteAllText(report, "error: " + e); }
    }

    /// <summary>"--snapshot DIR --hook-test": drives brim-hook.exe end to end and checks the installers in a temp home.</summary>
    public static async Task HookTestAsync(string dir)
    {
        var sb = new System.Text.StringBuilder();
        App.Agents.Start();
        await Task.Delay(600);
        var exe = Path.Combine(AppContext.BaseDirectory, "brim-hook.exe");
        sb.AppendLine("hook exe exists: " + File.Exists(exe));

        async Task<string> Run(string args, string stdin, string? decision)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
            if (decision != null)
            {
                for (int i = 0; i < 60 && App.Agents.Pending.Count == 0; i++) await Task.Delay(100);
                await Task.Delay(300);
                foreach (var a in App.Agents.Pending.ToList()) App.Agents.Resolve(a, decision);
            }
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return $"exit={p.ExitCode} out=[{output}]";
        }

        sb.AppendLine("claude UserPromptSubmit: " + await Run("claude", """{"hook_event_name":"UserPromptSubmit","session_id":"s1","cwd":"C:\\work\\shop-api","prompt":"add tests"}""", null));
        sb.AppendLine("claude PermissionRequest allow: " + await Run("claude", """{"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"C:\\work\\shop-api","tool_name":"Bash","tool_input":{"command":"npm test"}}""", "allow"));
        sb.AppendLine("claude PermissionRequest deny: " + await Run("claude", """{"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"C:\\work\\shop-api","tool_name":"Bash","tool_input":{"command":"rm -rf dist"}}""", "deny"));
        sb.AppendLine("claude PermissionRequest ask: " + await Run("claude", """{"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"C:\\work\\shop-api","tool_name":"Write","tool_input":{"file_path":"a.txt"}}""", "ask"));
        sb.AppendLine("claude Stop: " + await Run("claude", """{"hook_event_name":"Stop","session_id":"s1","cwd":"C:\\work\\shop-api","last_assistant_message":"Tests added and passing."}""", null));
        sb.AppendLine("cursor beforeShellExecution allow: " + await Run("cursor", """{"hook_event_name":"beforeShellExecution","conversation_id":"c1","workspace_roots":["C:/work/site"],"command":"pnpm build","cwd":"C:/work/site"}""", "allow"));
        sb.AppendLine("cursor stop: " + await Run("cursor", """{"hook_event_name":"stop","conversation_id":"c1","workspace_roots":["C:/work/site"],"status":"completed"}""", null));
        sb.AppendLine("antigravity PreToolUse deny: " + await Run("antigravity PreToolUse", """{"conversationId":"g1","workspacePaths":["C:\\work\\game"],"toolCall":{"name":"run_command","args":{"CommandLine":"git push --force"}},"stepIdx":3}""", "deny"));
        sb.AppendLine("antigravity Stop: " + await Run("antigravity Stop", """{"conversationId":"g1","workspacePaths":["C:\\work\\game"],"executionNum":1,"terminationReason":"model_stop","fullyIdle":true}""", null));
        sb.AppendLine("codex notify (argv): " + await Run("codex \"{\\\"type\\\":\\\"agent-turn-complete\\\",\\\"thread-id\\\":\\\"x1\\\",\\\"cwd\\\":\\\"C:\\\\\\\\work\\\\\\\\cli\\\",\\\"last-assistant-message\\\":\\\"Refactor done.\\\"}\"", "", null));
        sb.AppendLine();
        sb.AppendLine("sessions:");
        foreach (var s in App.Agents.Sessions) sb.AppendLine($"  {s.Agent} | {s.Project} | {s.State} | {s.Message}");

        // installers, against a throwaway home folder
        var home = Path.Combine(dir, "fake-home");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", "settings.json"), """{"model":"opus","hooks":{"Stop":[{"matcher":"","hooks":[{"type":"command","command":"echo mine"}]}]}}""");
        Services.AgentService.Home = home;
        foreach (var a in new[] { "claude", "codex", "cursor", "antigravity" })
            sb.AppendLine($"connect {a}: {Services.AgentService.Connect(a, true)} | connected={Services.AgentService.Connected(a)}");
        sb.AppendLine();
        foreach (var f in new[] { ".claude/settings.json", ".codex/config.toml", ".cursor/hooks.json", ".gemini/config/hooks.json" })
            sb.AppendLine($"--- {f}\n{File.ReadAllText(Path.Combine(home, f))}");
        foreach (var a in new[] { "claude", "codex", "cursor", "antigravity" })
            sb.AppendLine($"disconnect {a}: {Services.AgentService.Disconnect(a)} | connected={Services.AgentService.Connected(a)}");
        sb.AppendLine($"--- .claude/settings.json after disconnect\n{File.ReadAllText(Path.Combine(home, ".claude/settings.json"))}");
        File.WriteAllText(Path.Combine(dir, "hook-test.txt"), sb.ToString());
    }

    /// <summary>"--snapshot DIR --settings-test": flips every setting with all tabs built and reports listener errors.</summary>
    public static async Task SettingsTestAsync(string dir)
    {
        var S = App.S;
        var n = App.Notch;
        var report = new System.Text.StringBuilder();
        await Task.Delay(1500);
        foreach (var tab in AppSettings.AllTabs) { n.Open(tab); await Task.Delay(120); }
        n.Open("Home");
        await Task.Delay(200);
        var sw = new SettingsWindow();
        sw.Show();
        int steps = 0;
        async Task Step(string what, Action change, string name)
        {
            change();
            S.Changed(name);
            steps++;
            await Task.Delay(40);
        }
        foreach (var page in new[] { "General", "Appearance", "Widgets", "Agents", "Tips", "WhatsNew", "About" }) { sw.Navigate(page); await Task.Delay(80); }
        sw.Navigate("Widgets");
        string[] keys = ["NowPlaying", "MusicLights", "Mixer", "AgentUsage", "Agents", "Shelf", "Clipboard", "Downloads", "Cloak", "Stats", "Weather",
                         "Forecast", "Focus", "Capture", "Shortcuts", "Teleprompter", "EdgeLight", "Stopwatch", "Bluetooth", "QuickApps", "SmartMorph"];
        foreach (var k in keys)
        {
            bool v = S.W(k);
            S.SetW(k, !v); steps++; await Task.Delay(40);
            S.SetW(k, v); steps++; await Task.Delay(40);
        }
        foreach (var st in new[] { "Pill", "Bar", "Notch" }) await Step("style", () => S.Style = st, "Style");
        foreach (var e in new[] { "Left", "Right", "Top" }) await Step("edge", () => S.Edge = e, "Edge");
        foreach (var m in new[] { "DarkGlass", "Solid", "LiquidGlass", "Glass" }) await Step("material", () => S.Material = m, "Material");
        foreach (var c in new[] { "Dark", "Darker", "Light" }) await Step("cards", () => S.Cards = c, "Cards");
        foreach (var c in new[] { "Squared", "Rounded" }) await Step("corners", () => S.Corners = c, "Corners");
        foreach (var d in new[] { "Date", "Off", "Day" }) await Step("date", () => S.DateMode = d, "DateMode");
        foreach (var l in new[] { "CPU", "Memory", "Network", "Focus", "Nothing", "Auto" }) await Step("left", () => S.LeftSide = l, "LeftSide");
        foreach (var fx in Notch.EffectCanvas.All) await Step("effect", () => S.Effect = fx.Key, "Effect");
        await Step("effect", () => S.Effect = "None", "Effect");
        await Step("clock", () => S.Clock24h = !S.Clock24h, "Clock");
        await Step("clock", () => S.Clock24h = !S.Clock24h, "Clock");
        await Step("scale", () => S.Scale = 1.3, "Scale");
        await Step("scale", () => S.Scale = 1.0, "Scale");
        await Step("accent", () => S.Accent = "#EF5350", "Accent");
        await Step("accent", () => S.Accent = "#3B82F6", "Accent");
        await Step("tiles", () => S.StatTiles = ["CPU", "Memory"], "StatTiles");
        await Step("tiles", () => S.StatTiles = ["CPU", "Memory", "Network", "Storage", "Battery", "Uptime"], "StatTiles");
        await Step("order", () => (S.TabOrder[1], S.TabOrder[2]) = (S.TabOrder[2], S.TabOrder[1]), "TabOrder");
        await Step("order", () => (S.TabOrder[1], S.TabOrder[2]) = (S.TabOrder[2], S.TabOrder[1]), "TabOrder");
        await Step("quickapps", () => S.QuickApps[0].Name = "Files", "QuickApps");
        foreach (var page in new[] { "General", "Appearance", "Widgets" }) { sw.Navigate(page); await Task.Delay(80); }
        sw.Close();
        n.Collapse();
        await Task.Delay(300);
        report.AppendLine($"steps run: {steps}");
        File.WriteAllText(Path.Combine(dir, "settings-test.txt"), report.ToString());
    }

    static FrameworkElement Detached(FrameworkElement el, double width)
    {
        var host = new System.Windows.Controls.Border { Width = width, Child = el, Padding = new Thickness(16) };
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        return host;
    }

    static void Save(FrameworkElement el, string path, bool dark = false)
    {
        el.UpdateLayout();
        double w = el.ActualWidth, h = el.ActualHeight, pad = 24, scale = 1.5;
        if (w < 1 || h < 1) return;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bounds = new Rect(0, 0, w + pad * 2, h + pad * 2);
            if (dark) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 22, 26)), null, bounds);
            else
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(120, 28, 24), Color.FromRgb(30, 8, 10), 90), null, bounds);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(240, 220, 90)), null, new Point(bounds.Width * 0.55, bounds.Height * 0.8), bounds.Height * 0.45, bounds.Height * 0.45);
            }
            var vb = new VisualBrush(el) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h), AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            dc.DrawRectangle(vb, null, new Rect(pad, pad, w, h));
        }
        var rtb = new RenderTargetBitmap((int)((w + pad * 2) * scale), (int)((h + pad * 2) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
