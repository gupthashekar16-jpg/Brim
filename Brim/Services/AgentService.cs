using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;

namespace Brim.Services;

public sealed class AgentSession
{
    public string Key { get; init; } = "";
    public string Agent { get; init; } = "";        // Claude Code | Codex | Cursor | Antigravity
    public string Project { get; set; } = "";
    public string State { get; set; } = "working";  // working | done | attention
    public string Message { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>A tool call an agent is waiting to run; answered from the notch.</summary>
public sealed class Approval
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string AgentKey { get; init; }
    public required string Agent { get; init; }
    public string Project { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Detail { get; init; } = "";
    public DateTime At { get; } = DateTime.Now;
    internal TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Receives activity and permission requests from AI coding agents on a loopback-only port
/// (via brim-hook.exe), and installs or removes those hooks on request.
/// </summary>
public sealed partial class AgentService
{
    public const int Port = 47833;
    static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(110);

    public List<AgentSession> Sessions { get; } = [];
    public List<Approval> Pending { get; } = [];
    public event Action? Changed;
    /// <summary>Raised when an agent finishes or needs attention.</summary>
    public event Action<AgentSession>? Alert;
    /// <summary>Raised when an agent asks for permission to run a tool.</summary>
    public event Action<Approval>? ApprovalRequested;
    public event Action<Approval>? ApprovalClosed;

    public bool AnyWorking => Sessions.Any(s => s.State == "working" && DateTime.Now - s.Updated < TimeSpan.FromMinutes(30));

    public static string Display(string key) => key switch
    {
        "claude" => "Claude Code", "codex" => "Codex", "cursor" => "Cursor", "antigravity" => "Antigravity", _ => key,
    };

    // ===================================================================== server
    public void Start()
    {
        Task.Run(async () =>
        {
            TcpListener l;
            try { l = new TcpListener(IPAddress.Loopback, Port); l.Start(); }
            catch (Exception e) { App.Log("agent listener: " + e.Message); return; }
            while (true)
            {
                try
                {
                    var c = await l.AcceptTcpClientAsync();
                    _ = Task.Run(() => Handle(c));
                }
                catch { await Task.Delay(500); }
            }
        });
    }

    async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var (method, target, body) = await ReadRequest(s);
                string reply = "{}";
                if (method == "POST" && target.StartsWith("/hook/"))
                {
                    var path = target[6..];
                    string? evt = null;
                    int q = path.IndexOf('?');
                    if (q >= 0)
                    {
                        var m = Regex.Match(path[q..], @"[?&]event=([^&]+)");
                        if (m.Success) evt = Uri.UnescapeDataString(m.Groups[1].Value);
                        path = path[..q];
                    }
                    var agent = path.Trim('/').ToLowerInvariant();
                    var approval = await Application.Current.Dispatcher.InvokeAsync(() => Ingest(agent, body, evt));
                    if (approval != null)
                    {
                        var winner = await Task.WhenAny(approval.Decision.Task, Task.Delay(ApprovalTimeout));
                        var decision = winner == approval.Decision.Task ? approval.Decision.Task.Result : "timeout";
                        await Application.Current.Dispatcher.InvokeAsync(() => Close(approval));
                        reply = DecisionJson(agent, decision);
                    }
                }
                var bytes = Encoding.UTF8.GetBytes(reply);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(head);
                await s.WriteAsync(bytes);
            }
            catch (Exception e) { App.Log("agent request: " + e.Message); }
        }
    }

    internal static async Task<(string method, string target, string body)> ReadRequest(NetworkStream s)
    {
        var buf = new byte[64 * 1024];
        int read = 0, headerEnd = -1;
        while (headerEnd < 0 && read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read));
            if (n <= 0) break;
            read += n;
            headerEnd = buf.AsSpan(0, read).IndexOf("\r\n\r\n"u8);
        }
        if (headerEnd < 0) return ("", "", "");
        var head = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var first = head.Split("\r\n")[0].Split(' ');
        int len = 0;
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out len);
        var body = new byte[Math.Min(len, 4 * 1024 * 1024)];
        int have = Math.Min(read - headerEnd - 4, body.Length);
        Array.Copy(buf, headerEnd + 4, body, 0, have);
        while (have < body.Length)
        {
            int n = await s.ReadAsync(body.AsMemory(have));
            if (n <= 0) break;
            have += n;
        }
        return (first.Length > 0 ? first[0] : "", first.Length > 1 ? first[1] : "", Encoding.UTF8.GetString(body, 0, have));
    }

    static string DecisionJson(string agent, string decision) => (agent, decision) switch
    {
        ("claude", "allow") => """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""",
        ("claude", "deny") => """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"The user denied this from the Brim notch."}}}""",
        ("claude", _) => "{}",
        ("cursor", "allow") => """{"permission":"allow"}""",
        ("cursor", "deny") => """{"permission":"deny","user_message":"Denied from the Brim notch.","agent_message":"The user denied this command."}""",
        ("cursor", _) => """{"permission":"ask"}""",
        ("antigravity", "allow") => """{"decision":"auto_approve"}""",
        ("antigravity", "deny") => """{"decision":"deny","reason":"The user denied this from the Brim notch."}""",
        ("antigravity", "ask") => """{"decision":"ask"}""",
        ("antigravity", _) => """{"decision":"allow"}""",
        _ => "{}",
    };

    /// <summary>Shows a pretend permission request (nothing runs) so you can see the flow.</summary>
    public async void Demo()
    {
        var a = Ingest("claude", """{"hook_event_name":"PermissionRequest","session_id":"demo","cwd":"C:\\Projects\\demo-app","tool_name":"Bash","tool_input":{"command":"npm run build && npm test"}}""");
        if (a == null) return;
        await Task.WhenAny(a.Decision.Task, Task.Delay(ApprovalTimeout));
        Close(a);
    }

    /// <summary>Answers a pending approval: "allow", "deny" or "ask" (hand back to the agent's own prompt).</summary>
    public void Resolve(Approval a, string decision) => a.Decision.TrySetResult(decision);

    void Close(Approval a)
    {
        if (!Pending.Remove(a)) return;
        var s = Sessions.FirstOrDefault(x => x.Agent == a.Agent && x.Project == a.Project && x.State == "attention");
        if (s != null)
        {
            s.State = "working";
            s.Updated = DateTime.Now;
            if (s.Message.StartsWith("Wants to use")) s.Message = "";
        }
        ApprovalClosed?.Invoke(a);
        Changed?.Invoke();
    }

    // ===================================================================== ingest
    /// <summary>Records an agent event (UI thread). Returns an approval if the agent is waiting for a decision.</summary>
    public Approval? Ingest(string agent, string json, string? evt = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var r = doc.RootElement;
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string FirstOf(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? v[0].GetString() ?? "" : "";

            string id, folder;
            switch (agent)
            {
                case "cursor": id = Str("conversation_id"); folder = FirstOf("workspace_roots"); evt ??= Str("hook_event_name"); break;
                case "antigravity": id = Str("conversationId"); folder = FirstOf("workspacePaths"); break;
                case "codex": id = Str("thread-id"); folder = Str("cwd"); evt ??= Str("type"); break;
                default: id = Str("session_id"); folder = Str("cwd"); evt ??= Str("hook_event_name"); break;
            }
            var name = Display(agent);
            var key = agent + ":" + (id.Length > 0 ? id : folder);
            var sess = Sessions.FirstOrDefault(x => x.Key == key);
            if (sess == null)
            {
                sess = new AgentSession { Key = key, Agent = name };
                Sessions.Insert(0, sess);
                if (Sessions.Count > 16) Sessions.RemoveAt(Sessions.Count - 1);
            }
            if (folder.Length > 0) sess.Project = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (sess.Project.Length == 0) sess.Project = "Your project";
            sess.Updated = DateTime.Now;
            bool alert = false;
            Approval? approval = null;

            switch (agent, evt)
            {
                // ---- Claude Code
                case ("claude", "UserPromptSubmit"):
                    sess.State = "working"; sess.Message = Str("prompt"); break;
                case ("claude", "PreToolUse" or "PostToolUse"):
                    sess.State = "working"; break;
                case ("claude", "Notification"):
                    if (Pending.Any(p => p.AgentKey == "claude")) break;
                    sess.State = "attention";
                    sess.Message = Str("message");
                    alert = true;
                    break;
                case ("claude", "Stop" or "SubagentStop"):
                    sess.State = "done";
                    sess.Message = Str("last_assistant_message") is { Length: > 0 } last ? last : LastAssistantText(Str("transcript_path")) ?? "Finished its turn.";
                    alert = evt == "Stop";
                    break;
                case ("claude", "SessionEnd"):
                    Sessions.Remove(sess);
                    break;
                case ("claude", "PermissionRequest"):
                {
                    var tool = Str("tool_name");
                    var detail = r.TryGetProperty("tool_input", out var ti) ? Describe(tool, ti) : "";
                    approval = new Approval { AgentKey = agent, Agent = name, Project = sess.Project, Tool = tool, Detail = detail };
                    break;
                }

                // ---- Cursor
                case ("cursor", "beforeSubmitPrompt"):
                    sess.State = "working"; sess.Message = Str("prompt"); break;
                case ("cursor", "afterAgentResponse"):
                    sess.Message = Str("text"); break;
                case ("cursor", "stop"):
                    sess.State = Str("status") == "error" ? "attention" : "done";
                    if (Str("status") == "aborted") sess.Message = "Stopped.";
                    else if (Str("status") == "error") sess.Message = "Stopped with an error.";
                    else if (sess.Message.Length == 0 || sess.Message.StartsWith("Wants to use")) sess.Message = "Finished its turn.";
                    alert = Str("status") != "aborted";
                    break;
                case ("cursor", "beforeShellExecution"):
                    approval = new Approval { AgentKey = agent, Agent = name, Project = sess.Project, Tool = "Terminal", Detail = Str("command") };
                    break;

                // ---- Antigravity
                case ("antigravity", "PreInvocation" or "SessionStart"):
                    sess.State = "working";
                    if (Str("lastUserInput") is { Length: > 0 } input) sess.Message = input;
                    break;
                case ("antigravity", "Stop"):
                {
                    var reason = Str("terminationReason");
                    bool idle = !r.TryGetProperty("fullyIdle", out var fi) || fi.ValueKind != JsonValueKind.False;
                    sess.State = reason == "error" ? "attention" : "done";
                    sess.Message = reason switch { "error" => "Stopped with an error.", "max_steps_exceeded" => "Stopped after reaching the step limit.", _ => "Finished its task." };
                    alert = idle;
                    break;
                }
                case ("antigravity", "PreToolUse"):
                {
                    string tool = "", detail = "";
                    if (r.TryGetProperty("toolCall", out var tc))
                    {
                        if (tc.TryGetProperty("name", out var n)) tool = n.GetString() ?? "";
                        if (tc.TryGetProperty("args", out var a)) detail = Describe(tool, a);
                    }
                    approval = new Approval { AgentKey = agent, Agent = name, Project = sess.Project, Tool = tool, Detail = detail };
                    break;
                }

                // ---- Codex
                case ("codex", "agent-turn-complete"):
                    sess.State = "done";
                    sess.Message = Str("last-assistant-message");
                    alert = true;
                    break;
            }

            sess.Message = Trim(sess.Message);
            if (approval != null)
            {
                sess.State = "attention";
                sess.Message = $"Wants to use {Friendly(approval.Tool)}";
                Pending.Add(approval);
                ApprovalRequested?.Invoke(approval);
            }
            Changed?.Invoke();
            if (alert) Alert?.Invoke(sess);
            return approval;
        }
        catch (Exception e) { App.Log("ingest: " + e.Message); return null; }
    }

    static string Friendly(string tool) => tool switch
    {
        "Bash" or "run_command" or "Terminal" => "the terminal",
        "Edit" or "MultiEdit" or "Write" or "write_to_file" or "replace_file_content" => "edit files",
        "WebFetch" or "read_url_content" => "the web",
        "" => "a tool",
        _ => tool,
    };

    /// <summary>One-line summary of a tool's input (the command, the file, the URL…).</summary>
    static string Describe(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return input.ToString();
        foreach (var k in new[] { "command", "CommandLine", "commandLine", "file_path", "TargetFile", "path", "url", "Url", "pattern", "query" })
            if (input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) return Trim(v.GetString() ?? "", 220);
        var raw = input.GetRawText();
        return Trim(raw, 220);
    }

    static string Trim(string s, int max = 160)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }

    /// <summary>Reads the last assistant text block from a Claude Code transcript (older versions).</summary>
    static string? LastAssistantText(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, fs.Length - 256 * 1024), SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i].Trim();
                if (!l.StartsWith('{') || !l.Contains("\"assistant\"")) continue;
                try
                {
                    using var d = JsonDocument.Parse(l);
                    if (!d.RootElement.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in content.EnumerateArray().Reverse())
                        if (part.TryGetProperty("type", out var t) && t.GetString() == "text" && part.TryGetProperty("text", out var tx))
                            return tx.GetString();
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ===================================================================== hook installation
    /// <summary>Home folder whose agent configs are edited (overridable for the self-test).</summary>
    public static string Home { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string ClaudeFile => Path.Combine(Home, ".claude", "settings.json");
    static string CodexFile => Path.Combine(Home, ".codex", "config.toml");
    static string CursorFile => Path.Combine(Home, ".cursor", "hooks.json");
    static string AntigravityFile => Path.Combine(Home, ".gemini", "config", "hooks.json");
    const string Marker = "brim-hook";

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string longPath, [Out] char[] shortPath, uint size);

    /// <summary>brim-hook.exe beside Brim.exe, as a space-free forward-slash path every shell accepts.</summary>
    static string? HookExe()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "brim-hook.exe");
        if (!File.Exists(p)) return null;
        if (p.Contains(' '))
        {
            var buf = new char[1024];
            var n = GetShortPathName(p, buf, (uint)buf.Length);
            if (n > 0 && n < buf.Length) p = new string(buf, 0, (int)n);
        }
        return p.Replace('\\', '/');
    }

    static string Missing => "brim-hook.exe was not found next to Brim.exe.";
    static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static JsonObject LoadJson(string file)
    {
        if (!File.Exists(file)) return new JsonObject();
        var text = File.ReadAllText(file);
        return text.Trim().Length == 0 ? new JsonObject() : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject();
    }

    static void SaveWithBackup(string file, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var backup = file + ".brim-backup";
        if (File.Exists(file) && !File.Exists(backup)) File.Copy(file, backup);
        File.WriteAllText(file, content);
    }

    static bool FileHas(string file) { try { return File.Exists(file) && File.ReadAllText(file).Contains(Marker); } catch { return false; } }

    public static bool Connected(string agent) => agent switch
    {
        "claude" => FileHas(ClaudeFile),
        "codex" => FileHas(CodexFile) || (File.Exists(CodexFile) && File.ReadAllText(CodexFile).Contains("--notify")),
        "cursor" => FileHas(CursorFile),
        "antigravity" => FileHas(AntigravityFile),
        _ => false,
    };

    /// <summary>Removes Brim's entries from an event → [entries] map.</summary>
    static void Strip(JsonObject events)
    {
        foreach (var ev in events.Select(k => k.Key).ToList())
        {
            if (events[ev] is not JsonArray arr) continue;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                var s = arr[i]?.ToJsonString() ?? "";
                if (s.Contains(Marker) || s.Contains("/hook/claude")) arr.RemoveAt(i);
            }
            if (arr.Count == 0) events.Remove(ev);
        }
    }

    public static string Connect(string agent, bool approvals)
    {
        try
        {
            var exe = HookExe();
            if (exe == null) return Missing;
            switch (agent)
            {
                case "claude":
                {
                    var root = LoadJson(ClaudeFile);
                    var hooks = root["hooks"] as JsonObject ?? new JsonObject();
                    root["hooks"] = hooks;
                    Strip(hooks);
                    void Add(string ev, int timeout)
                    {
                        var arr = hooks[ev] as JsonArray ?? new JsonArray();
                        hooks[ev] = arr;
                        arr.Add(new JsonObject
                        {
                            ["matcher"] = "",
                            ["hooks"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = exe, ["args"] = new JsonArray("claude"), ["timeout"] = timeout } },
                        });
                    }
                    Add("UserPromptSubmit", 10);
                    Add("Notification", 10);
                    Add("Stop", 10);
                    Add("SessionEnd", 5);
                    if (approvals) Add("PermissionRequest", 120);
                    SaveWithBackup(ClaudeFile, root.ToJsonString(Pretty));
                    return approvals ? "Connected with notch approvals. New Claude Code sessions pick this up." : "Connected. New Claude Code sessions report to the notch.";
                }
                case "cursor":
                {
                    var root = LoadJson(CursorFile);
                    root["version"] ??= 1;
                    var hooks = root["hooks"] as JsonObject ?? new JsonObject();
                    root["hooks"] = hooks;
                    Strip(hooks);
                    var cmd = $"$input | & '{exe}' cursor";
                    void Add(string ev, int timeout)
                    {
                        var arr = hooks[ev] as JsonArray ?? new JsonArray();
                        hooks[ev] = arr;
                        arr.Add(new JsonObject { ["command"] = cmd, ["timeout"] = timeout });
                    }
                    Add("beforeSubmitPrompt", 10);
                    Add("afterAgentResponse", 10);
                    Add("stop", 10);
                    if (approvals) Add("beforeShellExecution", 120);
                    SaveWithBackup(CursorFile, root.ToJsonString(Pretty));
                    return "Connected. Restart Cursor to load the hooks.";
                }
                case "antigravity":
                {
                    var root = LoadJson(AntigravityFile);
                    root.Remove("brim");
                    var mine = new JsonObject { ["enabled"] = true };
                    JsonObject H(string ev, int timeout) => new() { ["type"] = "command", ["command"] = $"{exe} antigravity {ev}", ["timeout"] = timeout };
                    mine["PreInvocation"] = new JsonArray { H("PreInvocation", 10) };
                    mine["Stop"] = new JsonArray { H("Stop", 10) };
                    if (approvals)
                        mine["PreToolUse"] = new JsonArray { new JsonObject { ["matcher"] = "run_command", ["hooks"] = new JsonArray { H("PreToolUse", 120) } } };
                    root["brim"] = mine;
                    SaveWithBackup(AntigravityFile, root.ToJsonString(Pretty));
                    return "Connected. Restart Antigravity to load the hooks.";
                }
                case "codex":
                {
                    var text = File.Exists(CodexFile) ? File.ReadAllText(CodexFile) : "";
                    var line = $"notify = [\"{exe}\", \"codex\"]";
                    var existing = Regex.Match(text, @"(?m)^\s*notify\s*=.*$");
                    if (existing.Success && !existing.Value.Contains(Marker) && !existing.Value.Contains("--notify"))
                        return "Codex already has a notify program set; leaving it alone.";
                    text = existing.Success ? text.Replace(existing.Value, line) : line + "\n" + text;
                    SaveWithBackup(CodexFile, text);
                    return "Connected. Codex reports finished turns to the notch.";
                }
            }
            return "Unknown agent.";
        }
        catch (Exception e) { return "Could not update the config: " + e.Message; }
    }

    public static string Disconnect(string agent)
    {
        try
        {
            switch (agent)
            {
                case "claude":
                {
                    if (!File.Exists(ClaudeFile)) return "Not connected.";
                    var root = LoadJson(ClaudeFile);
                    if (root["hooks"] is JsonObject hooks) { Strip(hooks); if (hooks.Count == 0) root.Remove("hooks"); }
                    File.WriteAllText(ClaudeFile, root.ToJsonString(Pretty));
                    return "Disconnected.";
                }
                case "cursor":
                {
                    if (!File.Exists(CursorFile)) return "Not connected.";
                    var root = LoadJson(CursorFile);
                    if (root["hooks"] is JsonObject hooks) Strip(hooks);
                    File.WriteAllText(CursorFile, root.ToJsonString(Pretty));
                    return "Disconnected. Restart Cursor to apply.";
                }
                case "antigravity":
                {
                    if (!File.Exists(AntigravityFile)) return "Not connected.";
                    var root = LoadJson(AntigravityFile);
                    root.Remove("brim");
                    File.WriteAllText(AntigravityFile, root.ToJsonString(Pretty));
                    return "Disconnected. Restart Antigravity to apply.";
                }
                case "codex":
                {
                    if (!File.Exists(CodexFile)) return "Not connected.";
                    var text = Regex.Replace(File.ReadAllText(CodexFile), @"(?m)^\s*notify\s*=.*(brim-hook|--notify).*\r?\n?", "");
                    File.WriteAllText(CodexFile, text);
                    return "Disconnected.";
                }
            }
            return "Unknown agent.";
        }
        catch (Exception e) { return "Could not update the config: " + e.Message; }
    }

    /// <summary>Used by "Brim.exe --notify codex {json}" (older Codex setups) to forward to the running instance.</summary>
    public static void Forward(string agent, string json)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, Port);
            var body = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes($"POST /hook/{agent} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            var s = c.GetStream();
            s.Write(head);
            s.Write(body);
            s.Flush();
            s.ReadTimeout = 2000;
            try { s.ReadExactly(new byte[1]); } catch { }
        }
        catch { }
    }
}
