using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;

namespace Brim.Services;

public sealed class ShelfItem
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    public bool IsDir => Directory.Exists(Path);
    public string Meta
    {
        get
        {
            if (IsDir) return "Folder";
            var ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            long size = 0;
            try { size = new FileInfo(Path).Length; } catch { }
            return (ext.Length > 0 ? ext : "File") + " · " + Core.Fmt.Bytes(size);
        }
    }
}

/// <summary>Files parked in the notch until you need them.</summary>
public sealed class ShelfService
{
    public ObservableCollection<ShelfItem> Items { get; } = [];
    public event Action? Changed;

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if ((File.Exists(p) || Directory.Exists(p)) && Items.All(i => !i.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                Items.Add(new ShelfItem { Path = p });
        Changed?.Invoke();
    }

    public void Remove(ShelfItem i) { Items.Remove(i); Changed?.Invoke(); }
    public void Clear() { Items.Clear(); Changed?.Invoke(); }

    public static DataObject DataFor(IEnumerable<ShelfItem> items)
    {
        var sc = new StringCollection();
        foreach (var i in items) sc.Add(i.Path);
        var d = new DataObject();
        d.SetFileDropList(sc);
        return d;
    }

    public void CopyAll() => CopyItems(Items);
    public static void CopyItems(IEnumerable<ShelfItem> items)
    {
        try
        {
            var sc = new StringCollection();
            foreach (var i in items) sc.Add(i.Path);
            if (sc.Count > 0) Clipboard.SetFileDropList(sc);
        }
        catch { }
    }
}
