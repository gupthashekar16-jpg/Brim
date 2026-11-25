using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace Brim.Services;

public sealed record UsageWindow(string Label, double UsedPercent, DateTime? ResetsAt);

public sealed class ProviderUsage
{
    public required string Name { get; init; }
    public List<UsageWindow> Windows { get; init; } = [];
    /// <summary>Short status when no provider numbers are available.</summary>
    public string Note { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime Updated { get; set; }
    public double? LeftPercent => Windows.Count > 0 ? Math.Clamp(100 - Windows[0].UsedPercent, 0, 100) : null;
}

/// <summary>
/// Plan usage for AI coding agents.
/// Codex: the rate-limit snapshot Codex itself writes into its local session logs.
/// Claude Code: the plan limits from your Claude sign-in (opt-in), plus local token totals from transcripts.
/// </summary>
public sealed class UsageService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public ProviderUsage Codex { get; private set; } = new() { Name = "Codex", Note = "No data yet" };
    public ProviderUsage Claude { get; private set; } = new() { Name = "Claude", Note = "No data yet" };
    public ProviderUsage Cursor { get; } = new() { Name = "Cursor", Note = "Cursor doesn't publish usage to local apps" };
    public ProviderUsage Antigravity { get; } = new() { Name = "Antigravity", Note = "Antigravity doesn't publish usage to local apps" };
    public long ClaudeTokens5h { get; private set; }
    public long ClaudeTokensWeek { get; private set; }
    public IEnumerable<ProviderUsage> All => [Codex, Claude, Antigravity, Cursor];
    public event Action? Changed;
    DateTime _last;
    bool _busy;

    public void RefreshIfStale(TimeSpan maxAge)
    {
        if (DateTime.Now - _last > maxAge) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _last = DateTime.Now;
        try
        {
            var codex = await Task.Run(ReadCodex);
            var (t5, tw) = await Task.Run(ReadClaudeLocal);
            ProviderUsage claude = App.S.ClaudeUsageOnline ? await ReadClaudeOnline() : new ProviderUsage { Name = "Claude", Note = "Turn on plan limits in Settings → Sidekick" };
            Codex = codex;
            Claude = claude;
            ClaudeTokens5h = t5;
            ClaudeTokensWeek = tw;
        }
        catch (Exception e) { App.Log("usage: " + e.Message); }
        finally { _busy = false; }
        Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    // ---------------------------------------------------------------- Codex
    static ProviderUsage ReadCodex()
    {
        var u = new ProviderUsage { Name = "Codex", Source = "From Codex's local session logs" };
        var dir = Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(dir)) { u.Note = "Codex isn't set up on this PC"; return u; }
        var files = new DirectoryInfo(dir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(6);
        foreach (var f in files)
        {
            foreach (var line in Tail(f.FullName, 512 * 1024).Reverse())
            {
                if (!line.Contains("rate_limits")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var rl = Find(doc.RootElement, "rate_limits");
                    if (rl is not JsonElement limits || limits.ValueKind != JsonValueKind.Object) continue;
                    DateTime stamp = f.LastWriteTime;
                    if (doc.RootElement.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                        stamp = parsed.ToLocalTime();
                    foreach (var key in new[] { "primary", "secondary" })
                        if (limits.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object)
                            u.Windows.Add(Window(w, stamp));
                    if (u.Windows.Count == 0) continue;
                    u.Updated = stamp;
                    u.Note = "";
                    return u;
                }
                catch { }
            }
        }
        u.Note = "Run Codex once to see its limits";
        return u;
    }

    static UsageWindow Window(JsonElement w, DateTime stamp)
    {
        double used = w.TryGetProperty("used_percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
        int minutes = w.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
        DateTime? resets = null;
        if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
            resets = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).LocalDateTime;
        else if (w.TryGetProperty("resets_in_seconds", out var ri) && ri.ValueKind == JsonValueKind.Number)
            resets = stamp.AddSeconds(ri.GetDouble());
        var label = minutes switch { 300 => "5 hours", 10080 => "Weekly", > 0 when minutes % 1440 == 0 => $"{minutes / 1440} days", > 0 => $"{minutes / 60} hours", _ => "Limit" };
        // a window that has already reset counts as unused
        if (resets is DateTime r && r < DateTime.Now) used = 0;
        return new UsageWindow(label, used, resets);
    }

    static JsonElement? Find(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == name) return p.Value;
                if (Find(p.Value, name) is JsonElement f) return f;
            }
        }
        return null;
    }

    static IEnumerable<string> Tail(string path, int bytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, fs.Length - bytes), SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n');
        }
        catch { return []; }
    }

    // ---------------------------------------------------------------- Claude (opt-in, uses your own sign-in)
    static async Task<ProviderUsage> ReadClaudeOnline()
    {
        var u = new ProviderUsage { Name = "Claude", Source = "From your Claude sign-in" };
        try
        {
            var cred = Path.Combine(Home, ".claude", ".credentials.json");
            if (!File.Exists(cred)) { u.Note = "Sign in to Claude Code first"; return u; }
            string? token;
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(cred)))
                token = doc.RootElement.TryGetProperty("claudeAiOauth", out var o) && o.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token)) { u.Note = "No Claude sign-in found"; return u; }
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.UserAgent.ParseAdd("Brim/1.0");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { u.Note = $"Claude didn't return usage ({(int)resp.StatusCode})"; return u; }
            using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var (key, label) in new[] { ("five_hour", "5 hours"), ("seven_day", "Weekly"), ("seven_day_opus", "Weekly · Opus"), ("seven_day_sonnet", "Weekly · Sonnet") })
            {
                if (!body.RootElement.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                double used = w.TryGetProperty("utilization", out var ut) && ut.ValueKind == JsonValueKind.Number ? ut.GetDouble() : 0;
                DateTime? resets = w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(ra.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime() : null;
                u.Windows.Add(new UsageWindow(label, used, resets));
            }
            u.Updated = DateTime.Now;
            if (u.Windows.Count == 0) u.Note = "No limits reported for this plan";
        }
        catch (Exception e) { u.Note = "Couldn't reach Claude"; App.Log("claude usage: " + e.Message); }
        return u;
    }

    // ---------------------------------------------------------------- Claude Code local token totals
    static (long fiveHours, long week) ReadClaudeLocal()
    {
        long h5 = 0, wk = 0;
        var dir = Path.Combine(Home, ".claude", "projects");
        if (!Directory.Exists(dir)) return (0, 0);
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>();
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
        {
            if (now - f.LastWriteTimeUtc > TimeSpan.FromDays(7)) continue;
            try
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!line.Contains("\"usage\"") || !line.Contains("\"assistant\"")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        if (!r.TryGetProperty("timestamp", out var ts) || !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) continue;
                        if (now - at > TimeSpan.FromDays(7)) continue;
                        if (!r.TryGetProperty("message", out var m) || !m.TryGetProperty("usage", out var u)) continue;
                        var id = m.TryGetProperty("id", out var mid) ? mid.GetString() ?? "" : "";
                        if (id.Length > 0 && !seen.Add(id)) continue;
                        long Tok(string k) => u.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                        long t = Tok("input_tokens") + Tok("output_tokens") + Tok("cache_creation_input_tokens") + Tok("cache_read_input_tokens");
                        wk += t;
                        if (now - at <= TimeSpan.FromHours(5)) h5 += t;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return (h5, wk);
    }

    public static string Tokens(long n) => n >= 1_000_000_000 ? $"{n / 1e9:0.0}B" : n >= 1_000_000 ? $"{n / 1e6:0.0}M" : n >= 1000 ? $"{n / 1e3:0}K" : n.ToString();
}
