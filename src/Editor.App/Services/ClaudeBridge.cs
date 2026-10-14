using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StbEditor.App.Services;

/// <summary>
/// The Claude bridge: an MCP server (Streamable HTTP, JSON responses) on 127.0.0.1 inside the editor, so Claude Code can
/// look at the Render Window, move the camera, list and edit objects, read and change record fields, search, run Use Info
/// and leave notes on the view. Off by default (View → Settings → Claude). Requests are answered on the UI thread between
/// frames; every change is an ordinary undoable step and nothing is saved to disk by Claude — the user saves (Ctrl+S).
/// Connect: <c>claude mcp add --transport http stb-editor http://127.0.0.1:47110/mcp</c>.
/// </summary>
internal sealed class ClaudeBridge : IDisposable
{
    public const string Protocol = "2025-03-26";

    /// <summary>A tool's answer: MCP content items (text, image) and whether it failed.</summary>
    public sealed record ToolResult(JsonArray Content, bool IsError = false)
    {
        public static ToolResult Text(string text, bool error = false) => new([new JsonObject { ["type"] = "text", ["text"] = text }], error);
        public static ToolResult Json(JsonNode node) => Text(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        public static ToolResult Image(byte[] png, string caption) => new(
        [
            new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
            new JsonObject { ["type"] = "text", ["text"] = caption },
        ]);
    }

    private sealed record Call(ClaudeTools.Tool Tool, JsonElement Arguments, TaskCompletionSource<ToolResult> Done);

    private readonly ConcurrentQueue<Call> _queue = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private readonly object _historyLock = new();
    private readonly List<(DateTime At, string Tool, string Summary, bool Error)> _history = [];

    public int Port { get; private set; }
    public string Status { get; private set; } = "off";
    public bool Running => _listener is not null;

    /// <summary>Work for the UI thread (keeps the editor from idling).</summary>
    public bool HasWork => !_queue.IsEmpty || !UiWork.IsEmpty;

    /// <summary>The last calls (newest first), for the Claude window.</summary>
    public List<(DateTime At, string Tool, string Summary, bool Error)> History { get { lock (_historyLock) return [.. _history]; } }

    /// <summary>Starts or stops the server to match the settings.</summary>
    public void Apply(Settings settings)
    {
        if (settings.ClaudeBridge && (_listener is null || Port != settings.ClaudeBridgePort)) Start(settings.ClaudeBridgePort);
        else if (!settings.ClaudeBridge && _listener is not null) Stop();
    }

    private void Start(int port)
    {
        Stop();
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _listener = listener;
            Port = port;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            Status = $"http://127.0.0.1:{port}/mcp";
            Log.Info($"claude bridge: listening on {Status}");
            _ = Task.Run(() => AcceptLoop(listener, token));
        }
        catch (Exception e)
        {
            _listener = null;
            Status = $"error: {e.Message}";
            Log.Warn($"claude bridge: {e.Message}");
        }
    }

    public void Stop()
    {
        _stop?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Status = "off";
        while (_queue.TryDequeue(out var call)) call.Done.TrySetResult(ToolResult.Text("the bridge was turned off", true));
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch { return; }
            _ = Task.Run(() => Serve(client, token), token);
        }
    }

    /// <summary>One HTTP connection: requests in turn until the client closes it.</summary>
    private async Task Serve(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var request = await ReadRequest(stream, token);
                if (request is null) return;
                var (method, path, headers, body) = request.Value;
                // DNS-rebinding guard: only pages of this machine may call (CLI clients send no Origin).
                if (headers.TryGetValue("origin", out var origin) && !IsLocal(origin))
                {
                    await Respond(stream, 403, "text/plain", "forbidden origin"u8.ToArray(), token);
                    continue;
                }
                if (!path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
                {
                    await Respond(stream, 404, "text/plain", "use /mcp"u8.ToArray(), token);
                    continue;
                }
                switch (method)
                {
                    case "POST":
                        var reply = await HandleRpc(body, token);
                        if (reply is null) await Respond(stream, 202, null, [], token);
                        else await Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(reply), token);
                        break;
                    case "DELETE":
                        await Respond(stream, 200, null, [], token);
                        break;
                    default:
                        // No server-to-client stream: everything is answered in the POST.
                        await Respond(stream, 405, "text/plain", "POST JSON-RPC to /mcp"u8.ToArray(), token);
                        break;
                }
                if (headers.TryGetValue("connection", out var connection) && connection.Equals("close", StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }

    private static bool IsLocal(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)?> ReadRequest(NetworkStream stream, CancellationToken token)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, token);
            if (n == 0) return null;
            head.Add(one[0]);
            if (head.Count >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r') break;
            if (head.Count > 64 * 1024) return null;
        }
        var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var length = headers.TryGetValue("content-length", out var l) && int.TryParse(l, out var len) ? len : 0;
        if (length > 32 * 1024 * 1024) return null;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read), token);
            if (n == 0) return null;
            read += n;
        }
        return (first[0].ToUpperInvariant(), first[1], headers, body);
    }

    private static async Task Respond(NetworkStream stream, int status, string? type, byte[] body, CancellationToken token)
    {
        var reason = status switch { 200 => "OK", 202 => "Accepted", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nContent-Length: {body.Length}\r\n");
        if (type is not null) head.Append($"Content-Type: {type}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token);
        if (body.Length > 0) await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    /// <summary>A JSON-RPC message or batch; null when it held only notifications.</summary>
    private async Task<string?> HandleRpc(byte[] body, CancellationToken token)
    {
        JsonNode? request;
        try { request = JsonNode.Parse(body); }
        catch (JsonException) { return Error(null, -32700, "parse error").ToJsonString(); }
        if (request is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
                if (await HandleOne(item as JsonObject, token) is { } reply) replies.Add(reply);
            return replies.Count == 0 ? null : replies.ToJsonString();
        }
        return (await HandleOne(request as JsonObject, token))?.ToJsonString();
    }

    private async Task<JsonObject?> HandleOne(JsonObject? message, CancellationToken token)
    {
        if (message is null) return Error(null, -32600, "invalid request");
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>() ?? "";
        if (id is null) return null; // a notification (initialized, cancelled…)
        switch (method)
        {
            case "initialize":
                var asked = message["params"]?["protocolVersion"]?.GetValue<string>();
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = asked ?? Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "stb-editor", ["version"] = typeof(ClaudeBridge).Assembly.GetName().Version?.ToString() ?? "?" },
                    ["instructions"] = ClaudeTools.Instructions,
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                var tools = new JsonArray();
                foreach (var tool in ClaudeTools.All) tools.Add(tool.Describe());
                return Result(id, new JsonObject { ["tools"] = tools });
            case "tools/call":
                var name = message["params"]?["name"]?.GetValue<string>() ?? "";
                var tool2 = ClaudeTools.All.FirstOrDefault(t => t.Name == name);
                if (tool2 is null) return Error(id, -32602, $"unknown tool {name}");
                var arguments = message["params"]?["arguments"] is { } a ? JsonDocument.Parse(a.ToJsonString()).RootElement.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                var result = await Run(tool2, arguments, token);
                Remember(name, result);
                return Result(id, new JsonObject { ["content"] = result.Content, ["isError"] = result.IsError });
            default:
                return Error(id, -32601, $"method not found: {method}");
        }
    }

    private void Remember(string tool, ToolResult result)
    {
        var text = result.Content.OfType<JsonObject>().FirstOrDefault(c => c["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>() ?? "";
        var summary = text.Length > 140 ? text[..140].Replace('\n', ' ') + "…" : text.Replace('\n', ' ');
        lock (_historyLock)
        {
            _history.Insert(0, (DateTime.Now, tool, summary, result.IsError));
            if (_history.Count > 60) _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <summary>Queues a call for the UI thread and waits for its answer (two minutes at most).</summary>
    private async Task<ToolResult> Run(ClaudeTools.Tool tool, JsonElement arguments, CancellationToken token)
    {
        var call = new Call(tool, arguments, new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Enqueue(call);
        Platform.MainWindow.Wake();
        var finished = await Task.WhenAny(call.Done.Task, Task.Delay(TimeSpan.FromMinutes(2), token));
        return finished == call.Done.Task ? call.Done.Task.Result : ToolResult.Text("the editor did not answer in time (busy loading?)", true);
    }

    private static readonly ConcurrentQueue<Action<EditorApp>> UiWork = new();

    /// <summary>Runs something on the UI thread between frames (a tool that went to a worker and comes back to commit).</summary>
    public static Task<T> OnUi<T>(EditorApp app, Func<EditorApp, T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        UiWork.Enqueue(editor =>
        {
            try { done.TrySetResult(work(editor)); }
            catch (Exception e) { done.TrySetException(e); }
        });
        Platform.MainWindow.Wake();
        return done.Task;
    }

    /// <summary>Each frame, on the UI thread: runs the queued calls.</summary>
    public void Pump(EditorApp app)
    {
        while (UiWork.TryDequeue(out var work)) work(app);
        while (_queue.TryDequeue(out var call))
        {
            if (call.Tool.Edits && !app.Settings.ClaudeBridgeEdits)
            {
                call.Done.TrySetResult(ToolResult.Text("changes by Claude are turned off (View → Settings → Claude: «Claude may change things»)", true));
                continue;
            }
            try
            {
                var task = call.Tool.Run(app, call.Arguments);
                task.ContinueWith(t => call.Done.TrySetResult(t.IsCompletedSuccessfully ? t.Result
                    : ToolResult.Text(t.Exception?.GetBaseException().Message ?? "failed", true)), TaskScheduler.Default);
            }
            catch (Exception e)
            {
                call.Done.TrySetResult(ToolResult.Text($"{e.GetType().Name}: {e.Message}", true));
            }
        }
    }

    private static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string text) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };
}
