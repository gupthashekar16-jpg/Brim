using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Brim.Core;

public static class Shell
{
    public static void Open(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = args ?? "" }); }
        catch { }
    }

    public static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shell icon for a file, folder or exe (null if none).</summary>
    public static ImageSource? IconFor(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = Directory.Exists(path) ? "<dir>" : File.Exists(path) && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetExtension(path) : path;
        if (IconCache.TryGetValue(key, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            if (Directory.Exists(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\explorer.exe";
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico != null)
            {
                img = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
            }
        }
        catch { }
        IconCache[key] = img;
        return img;
    }

    /// <summary>Resolves a short name like "code" or "explorer.exe" to a full path if possible.</summary>
    public static string? Resolve(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (File.Exists(target) || Directory.Exists(target)) return target;
        if (target.Contains("://") || target.EndsWith(':')) return null;
        var exe = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : target + ".exe";
        var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
        if (File.Exists(win)) return win;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}")
                       ?? Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (k?.GetValue("") is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
        }
        catch { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var f = Path.Combine(dir.Trim(), exe); if (File.Exists(f)) return f; } catch { }
        }
        return null;
    }

    /// <summary>Path of the default browser's exe, for showing its icon.</summary>
    public static string? DefaultBrowserExe()
    {
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = uc?.GetValue("ProgId") as string;
            if (progId == null) return null;
            using var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var s = cmd?.GetValue("") as string;
            if (s == null) return null;
            s = s.Trim();
            var exe = s.StartsWith('"') ? s[1..s.IndexOf('"', 1)] : s.Split(' ')[0];
            return File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (on && Environment.ProcessPath is string p) k.SetValue("Brim", $"\"{p}\"");
            else k.DeleteValue("Brim", false);
        }
        catch { }
    }
}
