using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brim.Core;
using QRCoder;

namespace Brim.Services;

/// <summary>
/// Phone transfer: a small web page on your Wi-Fi, opened by scanning a QR code, for moving files
/// between your phone and the Shelf. No app or account. Each session has a random secret
/// in the address and stops by itself after 30 minutes of inactivity.
/// </summary>
public sealed class PhoneLinkService
{
    TcpListener? _listener;
    string _token = "";
    DateTime _lastUse;
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMinutes(1) };

    bool _preview;
    public bool Running => _listener != null || _preview;

    /// <summary>Snapshot mode only: shows the panel for an address without opening a port.</summary>
    public void Preview(string url)
    {
        _preview = true;
        Url = url;
        Qr = MakeQr(url);
        Changed?.Invoke();
    }
    public string Url { get; private set; } = "";
    public BitmapImage? Qr { get; private set; }
    public string Error { get; private set; } = "";
    public string ReceiveFolder => Path.Combine(Native.DownloadsFolder(), "Brim Phone");
    public event Action? Changed;
    /// <summary>Raised with the file name when the phone sends a file.</summary>
    public event Action<string>? Received;

    public PhoneLinkService()
    {
        _idle.Tick += (_, _) => { if (Running && DateTime.Now - _lastUse > TimeSpan.FromMinutes(30)) Stop(); };
    }

    public void Start()
    {
        if (Running) return;
        Error = "";
        var ip = LanAddress();
        if (ip == null) { Error = "Connect to Wi-Fi first"; Changed?.Invoke(); return; }
        try
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }
        catch (Exception e) { _listener = null; Error = "Couldn't open a port: " + e.Message; Changed?.Invoke(); return; }
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(9)).ToLowerInvariant();
        Url = $"http://{ip}:{port}/{_token}/";
        Qr = MakeQr(Url);
        _lastUse = DateTime.Now;
        _idle.Start();
        var l = _listener;
        Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(() => Handle(c));
            }
        });
        Changed?.Invoke();
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _preview = false;
        Url = "";
        Qr = null;
        _idle.Stop();
        Changed?.Invoke();
    }

    static string? LanAddress()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                         .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1))
            {
                var props = n.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                if (n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)) continue;
                var a = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (a != null) return a.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    static BitmapImage MakeQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var (method, target, headers, leftover) = await ReadHead(s);
                var prefix = "/" + _token + "/";
                if (_token.Length == 0 || !target.StartsWith(prefix)) { await Send(s, 404, "text/plain", "Not found"u8.ToArray()); return; }
                _lastUse = DateTime.Now;
                var rest = target[prefix.Length..];
                var files = await Application.Current.Dispatcher.InvokeAsync(() => App.Shelf.Items.Where(i => !i.IsDir).Select(i => i.Path).ToList());

                if (method == "GET" && (rest == "" || rest.StartsWith('?')))
                {
                    await Send(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page(files)));
                }
                else if (method == "GET" && rest.StartsWith("f/") && int.TryParse(rest[2..], out int idx) && idx >= 0 && idx < files.Count && File.Exists(files[idx]))
                {
                    var path = files[idx];
                    var fi = new FileInfo(path);
                    var name = Uri.EscapeDataString(fi.Name);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {fi.Length}\r\nContent-Disposition: attachment; filename*=UTF-8''{name}\r\nConnection: close\r\n\r\n");
                    await s.WriteAsync(head);
                    await using var fs = File.OpenRead(path);
                    await fs.CopyToAsync(s);
                }
                else if (method == "POST" && rest.StartsWith("up"))
                {
                    var q = rest.Contains('?') ? rest[(rest.IndexOf('?') + 1)..] : "";
                    var rawName = q.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == "name").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault() ?? "file";
                    var safe = string.Concat(Path.GetFileName(rawName).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
                    if (safe.Length == 0) safe = "file";
                    long len = headers.TryGetValue("content-length", out var cl) && long.TryParse(cl, out var l) ? l : -1;
                    Directory.CreateDirectory(ReceiveFolder);
                    var dest = Path.Combine(ReceiveFolder, safe);
                    for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(ReceiveFolder, $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}");
                    await using (var outFs = File.Create(dest))
                    {
                        await outFs.WriteAsync(leftover);
                        long remaining = len < 0 ? long.MaxValue : len - leftover.Length;
                        var buf = new byte[81920];
                        while (remaining > 0)
                        {
                            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)));
                            if (n <= 0) break;
                            await outFs.WriteAsync(buf.AsMemory(0, n));
                            remaining -= n;
                            _lastUse = DateTime.Now;
                        }
                    }
                    await Application.Current.Dispatcher.InvokeAsync(() => { App.Shelf.Add([dest]); Received?.Invoke(Path.GetFileName(dest)); });
                    await Send(s, 200, "application/json", "{\"ok\":true}"u8.ToArray());
                }
                else await Send(s, 404, "text/plain", "Not found"u8.ToArray());
            }
            catch (Exception e) { App.Log("phone transfer: " + e.Message); }
        }
    }

    static async Task<(string method, string target, Dictionary<string, string> headers, byte[] leftover)> ReadHead(NetworkStream s)
    {
        var buf = new byte[16 * 1024];
        int read = 0, end = -1;
        while (end < 0 && read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read));
            if (n <= 0) break;
            read += n;
            end = buf.AsSpan(0, read).IndexOf("\r\n\r\n"u8);
        }
        if (end < 0) return ("", "", [], []);
        var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in lines.Skip(1))
        {
            int i = h.IndexOf(':');
            if (i > 0) headers[h[..i].Trim().ToLowerInvariant()] = h[(i + 1)..].Trim();
        }
        return (first[0], first.Length > 1 ? first[1] : "", headers, buf[(end + 4)..read]);
    }

    static async Task Send(NetworkStream s, int code, string type, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(body);
    }

    static string Page(List<string> files)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < files.Count; i++)
        {
            var fi = new FileInfo(files[i]);
            sb.Append($"<a class=f href='f/{i}' download><span>{WebUtility.HtmlEncode(fi.Name)}</span><small>{Fmt.Bytes(fi.Exists ? fi.Length : 0)}</small></a>");
        }
        var list = files.Count == 0 ? "<p class=m>Nothing on the shelf yet. Drop files on the notch to share them.</p>" : sb.ToString();
        return $$"""
<!doctype html><html><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Brim · Phone transfer</title>
<style>
body{margin:0;font:16px -apple-system,Segoe UI,Roboto,sans-serif;background:#0d0d10;color:#f5f5f7;padding:22px}
h1{font-size:22px;margin:0 0 4px}p.m{color:#9a9aa2;margin:0 0 18px}
.f{display:flex;justify-content:space-between;gap:12px;align-items:center;background:#1c1c21;border-radius:14px;padding:14px 16px;margin:0 0 10px;color:#f5f5f7;text-decoration:none}
.f span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.f small{color:#9a9aa2;flex:none}
label{display:block;text-align:center;background:#fff;color:#111;border-radius:16px;padding:16px;font-weight:600;margin:22px 0 10px}
input{display:none}#s{color:#9a9aa2;text-align:center;min-height:22px}
</style></head><body>
<h1>Shelf</h1><p class=m>Tap a file to save it on your phone.</p>
{{list}}
<label>Send files to your PC<input id=i type=file multiple></label>
<div id=s></div>
<script>
const i=document.getElementById('i'),s=document.getElementById('s');
i.onchange=async()=>{for(const f of i.files){s.textContent='Sending '+f.name+'…';
const r=await fetch('up?name='+encodeURIComponent(f.name),{method:'POST',body:f});
s.textContent=r.ok?'Sent '+f.name:'Failed: '+f.name;}setTimeout(()=>location.reload(),900);};
</script></body></html>
""";
    }
}
