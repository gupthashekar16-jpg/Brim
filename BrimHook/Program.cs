// brim-hook: relays an AI agent's hook payload to the running Brim notch and prints its reply.
//
// Usage:  brim-hook <agent> [event] [json]
//   agent  claude | codex | cursor | antigravity
//   event  optional event name (Antigravity payloads don't carry one)
//   json   optional payload as an argument (Codex passes it this way); otherwise read from stdin
//
// Always exits 0. If Brim isn't running, prints a safe "no opinion" reply where the agent needs one.
using System.Net;
using System.Net.Sockets;
using System.Text;

const int Port = 47833;
string agent = args.Length > 0 && !args[0].StartsWith('{') ? args[0].ToLowerInvariant() : "claude";
string? evt = args.Skip(1).FirstOrDefault(a => !a.StartsWith('{'));
string payload = args.FirstOrDefault(a => a.StartsWith('{')) ?? "";
if (payload.Length == 0 && Console.IsInputRedirected)
{
    try
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        payload = stdin.ReadToEnd();
    }
    catch { }
}
if (string.IsNullOrWhiteSpace(payload)) payload = "{}";

bool approval = agent switch
{
    "claude" => payload.Contains("\"PermissionRequest\""),
    "cursor" => payload.Contains("\"beforeShellExecution\""),
    "antigravity" => evt == "PreToolUse",
    _ => false,
};

string reply = "";
try
{
    using var c = new TcpClient();
    var connect = c.ConnectAsync(IPAddress.Loopback, Port);
    if (!connect.Wait(TimeSpan.FromSeconds(1.5))) throw new TimeoutException();
    var s = c.GetStream();
    s.ReadTimeout = approval ? 125_000 : 5_000;
    var body = Encoding.UTF8.GetBytes(payload);
    var path = $"/hook/{agent}" + (evt != null ? "?event=" + Uri.EscapeDataString(evt) : "");
    var head = Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
    s.Write(head);
    s.Write(body);
    s.Flush();
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int n;
    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
    var resp = Encoding.UTF8.GetString(ms.ToArray());
    int split = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    reply = split >= 0 ? resp[(split + 4)..].Trim() : "";
}
catch
{
    // Brim isn't running: keep each agent's normal behaviour.
    reply = !approval ? "" : agent switch
    {
        "cursor" => "{\"permission\":\"ask\"}",
        "antigravity" => "{\"decision\":\"allow\"}",
        _ => "",
    };
}

if (reply.Length > 0 && reply != "{}")
{
    using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
    stdout.Write(reply);
}
return 0;
