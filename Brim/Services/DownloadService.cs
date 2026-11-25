using System.IO;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Services;

/// <summary>Watches the Downloads folder for in-progress browser downloads.</summary>
public sealed class DownloadService
{
    static readonly string[] TempExt = [".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp"];
    public string Folder { get; } = Native.DownloadsFolder();
    public int Active { get; private set; }
    public double BytesPerSec { get; private set; }
    public string CurrentName { get; private set; } = "";
    public event Action? Changed;
    /// <summary>Raised with the file name when a download completes.</summary>
    public event Action<string>? Completed;

    readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? _w;

    public void Start()
    {
        if (!Directory.Exists(Folder)) return;
        try
        {
            _w = new FileSystemWatcher(Folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            _w.Renamed += (_, e) =>
            {
                if (TempExt.Any(x => e.OldName?.EndsWith(x, StringComparison.OrdinalIgnoreCase) == true) &&
                    !TempExt.Any(x => e.Name?.EndsWith(x, StringComparison.OrdinalIgnoreCase) == true))
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Completed?.Invoke(e.Name ?? ""));
            };
        }
        catch { }
        var ui = System.Windows.Application.Current.Dispatcher;
        // Scan on a pool thread; only notify the UI when something is downloading or just stopped.
        _timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try { if (Poll()) ui.BeginInvoke(() => Changed?.Invoke()); }
            finally { _busy = 0; }
        }, null, 1500, 1500);
    }

    Timer? _timer;
    int _busy;

    bool Poll()
    {
        int count = 0;
        long delta = 0;
        string name = "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Folder))
            {
                if (!TempExt.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                var fi = new FileInfo(f);
                if (DateTime.Now - fi.LastWriteTime > TimeSpan.FromMinutes(2)) continue;
                count++;
                seen.Add(f);
                long size = fi.Length;
                if (_sizes.TryGetValue(f, out var prev)) delta += Math.Max(0, size - prev);
                _sizes[f] = size;
                name = Path.GetFileNameWithoutExtension(f);
            }
        }
        catch { }
        foreach (var k in _sizes.Keys.Where(k => !seen.Contains(k)).ToList()) _sizes.Remove(k);
        bool changed = count != Active || (count > 0);
        Active = count;
        BytesPerSec = delta;
        CurrentName = name;
        return changed;
    }
}
