using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Brim.Core;
using Brim.Notch;
using Brim.Overlays;
using Brim.Services;
using Brim.Settings;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Brim;

public partial class App : Application
{
    public static AppSettings S { get; private set; } = null!;
    public static MediaService Media { get; private set; } = null!;
    public static AudioService Audio { get; private set; } = null!;
    public static SystemService Sys { get; private set; } = null!;
    public static BluetoothService Bt { get; private set; } = null!;
    public static ClipboardService Clip { get; private set; } = null!;
    public static WeatherService Weather { get; private set; } = null!;
    public static LyricsService Lyrics { get; private set; } = null!;
    public static FocusService Focus { get; private set; } = null!;
    public static AgentService Agents { get; private set; } = null!;
    public static DownloadService Downloads { get; private set; } = null!;
    public static CaptureService Capture { get; private set; } = null!;
    public static ShelfService Shelf { get; private set; } = null!;
    public static UsageService Usage { get; private set; } = null!;
    public static PhoneLinkService Phone { get; private set; } = null!;
    static DesktopPanelWindow? _screen;
    public static NotchWindow Notch { get; private set; } = null!;
    public static bool SnapshotMode { get; private set; }

    static Mutex? _mutex;
    static Forms.NotifyIcon? _tray;
    static SettingsWindow? _settings;
    static EdgeLightWindow? _edge;
    static CloakWindow? _cloak;
    static readonly DispatcherTimer AccentTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    static double _hue;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Length >= 2 && args[0] == "--notify")
        {
            AgentService.Forward(args[1], args.Length >= 3 ? args[^1] : "{}");
            Shutdown();
            return;
        }
        string? snapDir = args.Length >= 2 && args[0] == "--snapshot" ? args[1] : null;
        SnapshotMode = snapDir != null;

        if (!SnapshotMode)
        {
            _mutex = new Mutex(true, "Brim.Notch.SingleInstance", out bool created);
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Brim.Notch.ShowSettings");
            if (!created) { show.Set(); Shutdown(); return; }
            new Thread(() => { while (show.WaitOne()) Current.Dispatcher.BeginInvoke(() => OpenSettings()); }) { IsBackground = true }.Start();
        }

        DispatcherUnhandledException += (_, ev) => { Log(ev.Exception.ToString()); ev.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log(ev.ExceptionObject?.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log(ev.Exception.ToString()); ev.SetObserved(); };

        S = AppSettings.Load();
        ApplyTheme();
        S.OnChanged += n => { if (n is "Accent" or "Cards") ApplyTheme(); if (n == "OpenAtSignIn" && !SnapshotMode) Shell.SetAutostart(S.OpenAtSignIn); };
        AccentTimer.Tick += (_, _) =>
        {
            if (!S.AccentCycle) return;
            _hue = (_hue + 1.2) % 360;
            Current.Resources["Accent"] = new SolidColorBrush(Pal.FromHsv(_hue, 0.62, 0.98));
        };
        AccentTimer.Start();
        Ui.HookMenus();

        Media = new MediaService();
        Audio = new AudioService();
        Sys = new SystemService();
        Bt = new BluetoothService();
        Clip = new ClipboardService();
        Weather = new WeatherService(S);
        Lyrics = new LyricsService();
        Focus = new FocusService(S);
        Agents = new AgentService();
        Downloads = new DownloadService();
        Capture = new CaptureService();
        Shelf = new ShelfService();
        Usage = new UsageService();
        Phone = new PhoneLinkService();

        Clip.Start();
        Downloads.Start();
        if (!SnapshotMode) Agents.Start();
        await Media.InitAsync();
        _ = Weather.RefreshAsync();

        Notch = new NotchWindow();
        Notch.Show();

        if (SnapshotMode)
        {
            if (args.Contains("--record-test")) await Snapshots.RecordTestAsync(snapDir!);
            else if (args.Contains("--hook-test")) await Snapshots.HookTestAsync(snapDir!);
            else if (args.Contains("--settings-test")) await Snapshots.SettingsTestAsync(snapDir!);
            else if (args.Contains("--fg-test")) { await Task.Delay(1000); System.IO.File.WriteAllText(System.IO.Path.Combine(snapDir!, "fg-test.txt"), Notch.ForegroundReport()); }
            else await Snapshots.RunAsync(snapDir!);
            Shutdown();
            return;
        }
        Shell.SetAutostart(S.OpenAtSignIn);
        CreateTray();
    }

    static void ApplyTheme()
    {
        var r = Current.Resources;
        var accent = Pal.Col(S.Accent);
        if (!S.AccentCycle) r["Accent"] = new SolidColorBrush(accent);
        r["AccentSoft"] = Pal.Brush(Pal.A(accent, 0x33));
        r["CardBg"] = S.Cards switch { "Dark" => Pal.Hex("#5C000000"), "Darker" => Pal.Hex("#8F000000"), _ => Pal.Hex("#14FFFFFF") };
    }

    static void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "Brim", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open notch", null, (_, _) => Notch.Open());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Brim", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Notch.Open(); };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    /// <summary>Draws the tray icon: a small notch pill.</summary>
    static Drawing.Icon MakeIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var path = new Drawing.Drawing2D.GraphicsPath();
            var r = new Drawing.RectangleF(3, 10, 26, 12);
            float d = 12;
            path.AddArc(r.Left, r.Top, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
            path.CloseFigure();
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 18, 18, 22));
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(255, 235, 235, 240), 2f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 110, 123, 255));
            g.FillEllipse(dot, 20, 14, 4, 4);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public static void OpenSettings(string? page = null)
    {
        if (_settings == null || !_settings.IsLoaded)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (page != null) _settings.Navigate(page);
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
        Notch?.Collapse();
    }

    public static DesktopPanelWindow OpenDesktopPanel()
    {
        if (_screen == null || !_screen.IsLoaded)
        {
            _screen = new DesktopPanelWindow();
            _screen.Closed += (_, _) => _screen = null;
            _screen.Show();
        }
        _screen.Activate();
        return _screen;
    }

    public static void ToggleEdgeLight(Forms.Screen screen)
    {
        if (_edge != null) { _edge.Close(); _edge = null; return; }
        _edge = new EdgeLightWindow(screen);
        _edge.Closed += (_, _) => _edge = null;
        _edge.Show();
    }

    public static void RefreshEdgeLight() => _edge?.Refresh();

    public static void ToggleCloak()
    {
        if (_cloak != null) { _cloak.Close(); _cloak = null; return; }
        try
        {
            var screen = Notch.Screen;
            using var shot = CaptureService.Grab(screen.Bounds);
            _cloak = new CloakWindow(screen, shot);
            _cloak.Closed += (_, _) => _cloak = null;
            _cloak.Show();
        }
        catch (Exception e) { Log("cloak: " + e); }
    }

    public static void Quit()
    {
        try { if (Capture.Recording) Capture.StopRecordingAsync().Wait(5000); } catch { }
        foreach (var a in Agents.Pending.ToList()) Agents.Resolve(a, "ask");
        Phone.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        S.SaveNow();
        Current.Shutdown();
    }

    public static void Restart()
    {
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); _mutex = null; } catch { }
        if (Environment.ProcessPath is string p) Process.Start(p);
        Quit();
    }

    static readonly object LogLock = new();

    public static void Log(string text)
    {
        try
        {
            lock (LogLock)
            {
                var f = Path.Combine(AppSettings.DataDir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f);
                File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
