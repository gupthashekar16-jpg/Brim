using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brim.Core;
using Windows.Media.Control;

namespace Brim.Services;

/// <summary>Now-playing info and transport controls through the Windows media session API.</summary>
public sealed class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    string _artKey = "";
    DateTimeOffset _updated;

    public bool HasSession => _session != null && Title.Length > 0;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string AppName { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public ImageSource? Art { get; private set; }
    public Color ArtColor { get; private set; } = Color.FromRgb(90, 90, 110);
    public Color ArtColor2 { get; private set; } = Color.FromRgb(40, 40, 60);
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }

    /// <summary>Raised on the UI thread when track, art or play state change.</summary>
    public event Action? Changed;

    public TimeSpan CurrentPosition
    {
        get
        {
            var p = Position;
            if (IsPlaying && _updated != default) p += DateTimeOffset.Now - _updated;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Ui(() => Attach(_mgr.GetCurrentSession()));
            _mgr.SessionsChanged += (_, _) => Ui(() => Attach(_mgr.GetCurrentSession()));
            Attach(_mgr.GetCurrentSession());
        }
        catch { /* media sessions unavailable */ }
    }

    static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    void Attach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (ReferenceEquals(s, _session) && s != null) return;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnProps;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = s;
        if (s == null)
        {
            Title = Artist = Album = AppName = "";
            IsPlaying = false;
            Art = null;
            _artKey = "";
            Changed?.Invoke();
            return;
        }
        s.MediaPropertiesChanged += OnProps;
        s.PlaybackInfoChanged += OnPlayback;
        s.TimelinePropertiesChanged += OnTimeline;
        AppName = FriendlyApp(s.SourceAppUserModelId);
        ReadPlayback();
        ReadTimeline();
        _ = ReadPropsAsync();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Ui(() => _ = ReadPropsAsync());
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Ui(() => { ReadPlayback(); Changed?.Invoke(); });
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => Ui(() => { ReadTimeline(); Changed?.Invoke(); });

    void ReadPlayback()
    {
        try { IsPlaying = _session?.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { IsPlaying = false; }
    }

    void ReadTimeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t == null) return;
            Position = t.Position;
            Duration = t.EndTime - t.StartTime;
            _updated = t.LastUpdatedTime;
            if (_updated.Year < 2000) _updated = DateTimeOffset.Now;
        }
        catch { }
    }

    async Task ReadPropsAsync()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(s, _session)) return;
            Title = p.Title ?? "";
            Artist = p.Artist ?? "";
            Album = p.AlbumTitle ?? "";
            var key = Title + "|" + Artist + "|" + Album;
            if (key != _artKey)
            {
                _artKey = key;
                Art = null;
                if (p.Thumbnail != null)
                {
                    using var ras = await p.Thumbnail.OpenReadAsync();
                    using var src = ras.AsStreamForRead();
                    var ms = new MemoryStream();
                    await src.CopyToAsync(ms);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 320;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    Art = bmp;
                    (ArtColor, ArtColor2) = Palette(bmp);
                }
            }
            ReadTimeline();
            ReadPlayback();
        }
        catch { }
        Changed?.Invoke();
    }

    /// <summary>Picks a vivid and a dark colour from the artwork.</summary>
    static (Color, Color) Palette(BitmapSource bmp)
    {
        try
        {
            var small = new TransformedBitmap(new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0),
                new ScaleTransform(24.0 / bmp.PixelWidth, 24.0 / bmp.PixelHeight));
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);
            double br = 0, bg = 0, bb = 0, bw = 0, ar = 0, ag = 0, ab = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2];
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max == 0 ? 0 : (max - min) / max, val = max / 255;
                double weight = sat * sat * (0.3 + val) + 0.01;
                br += r * weight; bg += g * weight; bb += b * weight; bw += weight;
                ar += r; ag += g; ab += b;
            }
            int n = px.Length / 4;
            var vivid = Color.FromRgb((byte)(br / bw), (byte)(bg / bw), (byte)(bb / bw));
            var avg = Color.FromRgb((byte)(ar / n * 0.45), (byte)(ag / n * 0.45), (byte)(ab / n * 0.45));
            return (vivid, avg);
        }
        catch { return (Color.FromRgb(90, 90, 110), Color.FromRgb(40, 40, 60)); }
    }

    static string FriendlyApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var s = aumid;
        if (s.Contains('!')) s = s[..s.IndexOf('!')];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        if (s.Contains('_')) s = s[..s.IndexOf('_')];
        if (s.Contains('.')) s = s[(s.LastIndexOf('.') + 1)..];
        return s switch
        {
            "ZuneMusic" => "Media Player",
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            _ => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s,
        };
    }

    public void Toggle() => _ = Run(s => s.TryTogglePlayPauseAsync().AsTask());
    public void Next() => _ = Run(s => s.TrySkipNextAsync().AsTask());
    public void Prev() => _ = Run(s => s.TrySkipPreviousAsync().AsTask());

    public void Seek(TimeSpan t)
    {
        Position = t;
        _updated = DateTimeOffset.Now;
        _ = Run(s => s.TryChangePlaybackPositionAsync(t.Ticks).AsTask());
    }

    async Task Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> f)
    {
        if (_session == null) return;
        try { await f(_session); } catch { }
    }
}
