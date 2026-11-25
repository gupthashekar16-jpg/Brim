using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Brim.Core;

public sealed class QuickApp
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class FocusTask
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>All user preferences and small bits of persisted state.</summary>
public sealed class AppSettings
{
    public static readonly string[] AllTabs =
        ["Home", "Agents", "Music", "Controls", "Mixer", "Bluetooth", "Shelf", "Clipboard",
         "Focus", "Weather", "Teleprompter", "Capture", "Stopwatch", "Stats"];

    // ---- Display
    public string Style { get; set; } = "Notch";          // Notch | Pill | Bar
    public bool Clock24h { get; set; }
    public string DateMode { get; set; } = "Day";          // Day | Date | Off
    public double Scale { get; set; } = 1.0;
    public string Display { get; set; } = "";              // "" = primary

    // ---- Closed notch
    public string LeftSide { get; set; } = "Auto";         // Auto | Battery | CPU | Memory | Network | Focus | Nothing
    public bool MusicVisualizer { get; set; } = true;
    public string AgentActivity { get; set; } = "Auto";    // Off | Auto | Always
    public bool DownloadsIndicator { get; set; } = true;
    public bool ShelfCount { get; set; } = true;
    public bool BatteryAlerts { get; set; } = true;

    // ---- Behaviour
    public bool OpenAtSignIn { get; set; } = true;
    public bool HideUntilHovered { get; set; }
    public bool HideOverApps { get; set; } = true;
    public bool ExpandOnHover { get; set; } = true;
    public bool OpenLastTab { get; set; } = true;
    public bool HideFullscreen { get; set; } = true;
    public bool KeepOpen { get; set; }
    public bool LockPosition { get; set; } = true;

    // ---- Placement and dimensions
    public string Edge { get; set; } = "Top";              // Top | Left | Right
    public double EdgeOffset { get; set; } = 0.5;
    public double NotchLength { get; set; } = 214;
    public double NotchThickness { get; set; } = 30;

    // ---- Appearance
    public string Effect { get; set; } = "None";
    public string Accent { get; set; } = "#3B82F6";
    public bool AccentCycle { get; set; }
    public string RestingColor { get; set; } = "#000000";
    public string ExpandedColor { get; set; } = "#000000";
    public string Corners { get; set; } = "Rounded";       // Rounded | Squared
    public string Material { get; set; } = "Glass";        // Glass | DarkGlass | Solid | LiquidGlass
    public bool ThemeClosed { get; set; } = true;
    public string Cards { get; set; } = "Light";           // Light | Dark | Darker

    // ---- Widgets
    public Dictionary<string, bool> Widgets { get; set; } = new();
    public List<string> StatTiles { get; set; } = ["CPU", "Memory", "Network", "Storage", "Battery", "Uptime"];
    public string Units { get; set; } = "C";
    public string City { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public List<string> TabOrder { get; set; } = [.. AllTabs];
    public List<QuickApp> QuickApps { get; set; } =
    [
        new() { Name = "Explorer", Target = "explorer.exe" },
        new() { Name = "Browser", Target = "https://www.google.com" },
        new() { Name = "ChatGPT", Target = "https://chatgpt.com" },
        new() { Name = "Claude", Target = "https://claude.ai" },
        new() { Name = "Settings", Target = "ms-settings:" },
    ];

    // ---- Persisted state
    public string LastTab { get; set; } = "Home";
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public List<FocusTask> Tasks { get; set; } = [];
    public string Script { get; set; } =
        "Good morning, and thank you all for making the time.\n" +
        "I want to walk you through the work we finished this week.\n" +
        "First, the new onboarding flow is live for everyone.\n" +
        "Sign-ups are up, and support tickets are down.\n" +
        "Second, the dashboard now loads in under a second.\n" +
        "That was the most requested fix of the quarter.\n" +
        "Finally, a quick look at what comes next.\n" +
        "We will focus on offline mode and better search.\n" +
        "I would love your feedback before we lock the plan.\n" +
        "Thanks again, and let's open it up for questions.";
    public int Wpm { get; set; } = 150;
    public bool TeleHide { get; set; } = true;
    public double EdgeLightWidth { get; set; } = 42;
    public double EdgeLightWarmth { get; set; } = 0.35;
    public bool RecordSystemSound { get; set; } = true;
    public bool RecordMic { get; set; }
    public bool LyricsPanel { get; set; } = true;

    // ---- Sidekick
    public bool ApproveClaude { get; set; }
    public bool ApproveCursor { get; set; }
    public bool ApproveAntigravity { get; set; }
    public bool ClaudeUsageOnline { get; set; }
    public string UsageProvider { get; set; } = "Codex";
    public bool ScreenPinned { get; set; } = true;
    public double ScreenLeft { get; set; } = double.NaN;
    public double ScreenTop { get; set; } = double.NaN;
    public double ScreenWidth { get; set; } = 360;
    public double ScreenHeight { get; set; } = 540;
    public string ScreenTab { get; set; } = "Music";

    static readonly HashSet<string> OffByDefault = ["Stopwatch"];

    public bool W(string key) => Widgets.TryGetValue(key, out var v) ? v : !OffByDefault.Contains(key);
    public void SetW(string key, bool on) { Widgets[key] = on; Changed("Widgets"); }

    // ---- Persistence
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Brim");
    public static string DataDir { get { Directory.CreateDirectory(Dir); return Dir; } }
    static string FilePath => Path.Combine(DataDir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // ScreenLeft/Top start as NaN ("not placed yet"); plain JSON can't hold NaN without this
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s != null)
                {
                    foreach (var t in AllTabs) if (!s.TabOrder.Contains(t)) s.TabOrder.Add(t);
                    s.TabOrder.RemoveAll(t => !AllTabs.Contains(t));
                    return s;
                }
            }
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    DispatcherTimer? _saveTimer;

    /// <summary>Raised (on the UI thread) after any setting changes, with the property name.</summary>
    public event Action<string>? OnChanged;

    public void Changed(string name)
    {
        // Call each listener separately so one failure can't stop the others (or the save).
        if (OnChanged != null)
            foreach (Action<string> listener in OnChanged.GetInvocationList())
            {
                try { listener(name); }
                catch (Exception e) { App.Log($"setting '{name}' listener failed: {e}"); }
            }
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            SaveNow();
        }, Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        if (App.SnapshotMode) return;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch { /* ignore disk errors */ }
    }
}
