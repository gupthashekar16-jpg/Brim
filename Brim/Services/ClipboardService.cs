using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Services;

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Text";      // Text | Link | Color | Email | Code | Image
    public string Text { get; set; } = "";
    public string? ImagePath { get; set; }
    public bool Pinned { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>Clipboard history with pins, images and smart classification.</summary>
public sealed partial class ClipboardService
{
    const int MaxItems = 60;
    public ObservableCollection<ClipItem> Items { get; } = [];
    public event Action? Changed;
    HwndSource? _src;
    bool _suppress;
    readonly string _file = Path.Combine(AppSettings.DataDir, "clips.json");
    readonly string _imgDir = Path.Combine(AppSettings.DataDir, "clips");

    public void Start()
    {
        Load();
        _src = new HwndSource(new HwndSourceParameters("BrimClipboard") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _src.AddHook(Hook);
        Native.AddClipboardFormatListener(_src.Handle);
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x031D) // WM_CLIPBOARDUPDATE
        {
            if (_suppress) _suppress = false;
            else Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, async () => { await Task.Delay(60); Read(); });
        }
        return IntPtr.Zero;
    }

    void Read()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var t = Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(t) || t.Length > 200_000) return;
                    var existing = Items.FirstOrDefault(i => i.Kind != "Image" && i.Text == t);
                    if (existing != null) { Items.Remove(existing); existing.Time = DateTime.Now; Items.Insert(0, existing); }
                    else Items.Insert(0, new ClipItem { Text = t, Kind = Classify(t) });
                }
                else if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img == null) return;
                    Directory.CreateDirectory(_imgDir);
                    var path = Path.Combine(_imgDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                    using (var fs = File.Create(path))
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(img));
                        enc.Save(fs);
                    }
                    Items.Insert(0, new ClipItem { Kind = "Image", ImagePath = path, Text = $"Image {img.PixelWidth} × {img.PixelHeight}" });
                }
                else return;
                Trim();
                Save();
                Changed?.Invoke();
                return;
            }
            catch { Thread.Sleep(40); }
        }
    }

    void Trim()
    {
        while (Items.Count(i => !i.Pinned) > MaxItems)
        {
            var last = Items.Last(i => !i.Pinned);
            DeleteFile(last);
            Items.Remove(last);
        }
    }

    static void DeleteFile(ClipItem i)
    {
        if (i.ImagePath != null) try { File.Delete(i.ImagePath); } catch { }
    }

    public static string Classify(string t)
    {
        var s = t.Trim();
        if (UrlRx().IsMatch(s)) return "Link";
        if (EmailRx().IsMatch(s)) return "Email";
        if (ColorRx().IsMatch(s)) return "Color";
        if (s.Contains('\n') && (s.Contains('{') || s.Contains(';') || s.Contains("=>") || s.Contains("def ") || s.Contains("</")))
            return "Code";
        if (!s.Contains(' ') && (s.Contains("()") || s.Contains("::") || s.StartsWith('$'))) return "Code";
        return "Text";
    }

    [GeneratedRegex(@"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase)] private static partial Regex UrlRx();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[a-z]{2,}$", RegexOptions.IgnoreCase)] private static partial Regex EmailRx();
    [GeneratedRegex(@"^(#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})|rgba?\([\d\s,.%]+\)|hsla?\([\d\s,.%deg]+\))$", RegexOptions.IgnoreCase)] private static partial Regex ColorRx();

    public void Copy(ClipItem i)
    {
        try
        {
            _suppress = true;
            if (i.Kind == "Image" && i.ImagePath != null && File.Exists(i.ImagePath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(i.ImagePath); bmp.EndInit();
                Clipboard.SetImage(bmp);
            }
            else Clipboard.SetText(i.Text);
        }
        catch { _suppress = false; }
    }

    public void CopyImageSilently(BitmapSource src)
    {
        try { Clipboard.SetImage(src); } catch { }
    }

    public void CopyText(string text)
    {
        try { _suppress = true; Clipboard.SetText(text); } catch { _suppress = false; }
    }

    public void Delete(ClipItem i) { DeleteFile(i); Items.Remove(i); Save(); Changed?.Invoke(); }

    public void ClearUnpinned()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) { DeleteFile(i); Items.Remove(i); }
        Save(); Changed?.Invoke();
    }

    public void TogglePin(ClipItem i) { i.Pinned = !i.Pinned; Save(); Changed?.Invoke(); }

    public void AddSnippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Items.Insert(0, new ClipItem { Text = text, Kind = Classify(text), Pinned = true });
        Save(); Changed?.Invoke();
    }

    /// <summary>Strips tracking parameters from a link.</summary>
    public static string CleanLink(string url)
    {
        try
        {
            var u = new Uri(url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url);
            var keep = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                {
                    var k = p.Split('=')[0].ToLowerInvariant();
                    return !(k.StartsWith("utm_") || k is "fbclid" or "gclid" or "dclid" or "msclkid" or "mc_cid" or "mc_eid"
                             or "igshid" or "si" or "ref" or "ref_src" or "spm" or "_hsenc" or "_hsmi" or "yclid");
                }).ToList();
            var b = new UriBuilder(u) { Query = string.Join("&", keep) };
            var s = b.Uri.ToString();
            return keep.Count == 0 ? s.TrimEnd('?') : s;
        }
        catch { return url; }
    }

    public static string? SaveImageCopy(ClipItem i)
    {
        if (i.ImagePath == null || !File.Exists(i.ImagePath)) return null;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Brim");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "Clip " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".png");
        File.Copy(i.ImagePath, dest, true);
        return dest;
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var list = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(_file)) ?? [];
            foreach (var i in list.Where(i => i.Kind != "Image" || (i.ImagePath != null && File.Exists(i.ImagePath)))) Items.Add(i);
        }
        catch { }
    }

    void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(Items.ToList())); } catch { }
    }
}
