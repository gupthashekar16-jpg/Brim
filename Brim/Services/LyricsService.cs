using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Brim.Services;

public sealed record LyricLine(TimeSpan At, string Text);

/// <summary>Time-synced lyrics from the public LRCLIB service, cached per track.</summary>
public sealed partial class LyricsService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static LyricsService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Brim/1.0 (desktop notch)");
    readonly Dictionary<string, List<LyricLine>?> _cache = [];
    string _pending = "";

    public async Task<List<LyricLine>?> GetAsync(string title, string artist, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var key = $"{artist}|{title}";
        if (_cache.TryGetValue(key, out var hit)) return hit;
        if (_pending == key) return null;
        _pending = key;
        List<LyricLine>? result = null;
        try
        {
            var cleanTitle = Noise().Replace(title, "").Trim();
            string? lrc = null;
            if (!string.IsNullOrWhiteSpace(artist))
            {
                var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(artist)}";
                if (duration > TimeSpan.Zero) url += "&duration=" + ((int)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                try
                {
                    using var r = await Http.GetAsync(url);
                    if (r.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                        lrc = Synced(doc.RootElement);
                    }
                }
                catch { }
            }
            if (lrc == null)
            {
                var q = string.IsNullOrWhiteSpace(artist) ? cleanTitle : $"{artist} {cleanTitle}";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(q)));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    lrc = Synced(e);
                    if (lrc != null) break;
                }
            }
            if (lrc != null) result = Parse(lrc);
        }
        catch { }
        _cache[key] = result;
        _pending = "";
        return result;
    }

    static string? Synced(JsonElement e) =>
        e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString())
            ? s.GetString() : null;

    static List<LyricLine> Parse(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var m = Stamp().Match(raw);
            if (!m.Success) continue;
            var at = new TimeSpan(0, 0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                (int)(double.Parse("0." + (m.Groups[3].Success ? m.Groups[3].Value : "0"), CultureInfo.InvariantCulture) * 1000));
            list.Add(new LyricLine(at, raw[m.Length..].Trim()));
        }
        return list.OrderBy(l => l.At).ToList();
    }

    [GeneratedRegex(@"^\[(\d+):(\d+)(?:\.(\d+))?\]")] private static partial Regex Stamp();
    [GeneratedRegex(@"\s*[\(\[](official|lyric|audio|video|remaster|hd|4k|visuali[sz]er|explicit)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)] private static partial Regex Noise();
}
