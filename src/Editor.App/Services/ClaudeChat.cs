using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace StbEditor.App.Services;

/// <summary>
/// «Ask Claude» in the Claude window: runs Claude Code (<c>claude -p</c>) with this editor's bridge as its only MCP server
/// and only those tools allowed, streams what it says and which tools it calls, and keeps the conversation
/// (<c>--resume</c>). Uses the user's own Claude Code install and account.
/// </summary>
internal sealed class ClaudeChat
{
    public sealed record Entry(string Kind, string Text);

    private readonly ConcurrentQueue<Entry> _incoming = new();
    private Process? _process;
    private string? _session;

    public List<Entry> Transcript { get; } = [];
    public bool Running => _process is { HasExited: false };
    public string? Error { get; private set; }

    /// <summary>claude.exe / claude.cmd: on PATH, in ~/.local/bin (the native install) or npm's folder.</summary>
    public static string? FindCli()
    {
        var names = new[] { "claude.exe", "claude.cmd" };
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var folder in folders)
            foreach (var name in names)
            {
                try { if (File.Exists(Path.Combine(folder, name))) return Path.Combine(folder, name); } catch { }
            }
        return null;
    }

    /// <summary>Sends a message (a new conversation, or the next turn of this one).</summary>
    public void Send(EditorApp app, string text)
    {
        if (Running || text.Trim().Length == 0) return;
        Error = null;
        if (FindCli() is not { } cli) { Error = "Claude Code (claude) is not installed on this computer"; return; }
        Transcript.Add(new Entry("user", text));
        var config = Path.Combine(Path.GetTempPath(), "stbeditor-claude-mcp.json");
        var url = $"http://127.0.0.1:{app.Settings.ClaudeBridgePort}/mcp";
        File.WriteAllText(config, JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["stb-editor"] = new { type = "http", url } } }));
        var prompt = _session is null
            ? "You are working inside STB Editor (a Skyrim Creation Kit replacement) through the stb-editor tools, with the user watching the editor. "
              + "Start with get_status; look with screenshot; for changes the user should check, prefer propose_changes. Answer in the user's language.\n\n" + text
            : text;
        var start = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in new[] { "-p", prompt, "--output-format", "stream-json", "--verbose", "--mcp-config", config, "--strict-mcp-config", "--allowedTools", "mcp__stb-editor" })
            start.ArgumentList.Add(arg);
        if (_session is { } id) { start.ArgumentList.Add("--resume"); start.ArgumentList.Add(id); }
        try
        {
            _process = Process.Start(start);
            if (_process is null) { Error = "could not start claude"; return; }
            var process = _process;
            _ = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line) Read(line);
                var errors = await process.StandardError.ReadToEndAsync();
                if (process.ExitCode != 0 && errors.Trim().Length > 0) _incoming.Enqueue(new Entry("error", errors.Trim()));
                Platform.MainWindow.Wake();
            });
        }
        catch (Exception e) { Error = e.Message; }
    }

    public void Stop()
    {
        try { if (Running) _process!.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>A new conversation next time.</summary>
    public void Reset()
    {
        Stop();
        _session = null;
        Transcript.Clear();
        Error = null;
    }

    /// <summary>One stream-json line: the assistant's text, its tool calls, the session id.</summary>
    private void Read(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("session_id", out var sid) && sid.GetString() is { Length: > 0 } s) _session = s;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "assistant" && root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                foreach (var block in content.EnumerateArray())
                {
                    var kind = block.TryGetProperty("type", out var k) ? k.GetString() : null;
                    if (kind == "text" && block.GetProperty("text").GetString() is { Length: > 0 } text) _incoming.Enqueue(new Entry("claude", text.Trim()));
                    else if (kind == "tool_use" && block.TryGetProperty("name", out var name))
                        _incoming.Enqueue(new Entry("tool", (name.GetString() ?? "?").Replace("mcp__stb-editor__", "")));
                }
            else if (type == "result" && root.TryGetProperty("is_error", out var bad) && bad.ValueKind == JsonValueKind.True)
                _incoming.Enqueue(new Entry("error", root.TryGetProperty("result", out var r) ? r.GetString() ?? "error" : "error"));
        }
        catch (JsonException) { }
        Platform.MainWindow.Wake();
    }

    /// <summary>Each frame: what arrived from the process into the transcript.</summary>
    public void Pump()
    {
        while (_incoming.TryDequeue(out var entry)) Transcript.Add(entry);
    }
}
