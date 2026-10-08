using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZRevive.Launcher;

// Minimal Discord Rich Presence over the local IPC pipe (\\.\pipe\discord-ipc-N).
// Frame = int32 opcode LE + int32 length LE + UTF-8 JSON.
// Everything runs on a background loop: callers just Set()/Clear() and never block.
// If Discord isn't running (or restarts) the loop quietly retries.
public sealed class DiscordPresence : IDisposable
{
    const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    readonly string _clientId;
    readonly int _pid = Environment.ProcessId;
    readonly object _gate = new();
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly Task _loop;

    JsonObject? _desired;          // null = no activity
    bool _dirty;                   // _desired not yet sent on the current connection
    TaskCompletionSource _wake = NewWake();
    NamedPipeClientStream? _pipe;
    bool _disposed;

    public bool IsConnected => _pipe?.IsConnected == true;
    public event Action<string>? Log;

    /// <summary>
    /// <paramref name="clientId"/> is the Discord application id. It is deployment configuration rather
    /// than source (it names one specific Discord application), so it is supplied from the environment and
    /// a blank value is normal: the presence loop is then never started and every Set/Clear is a no-op.
    /// </summary>
    public DiscordPresence(string clientId)
    {
        _clientId = clientId;
        _loop = string.IsNullOrWhiteSpace(clientId) ? Task.CompletedTask : Task.Run(RunAsync);
    }

    public sealed record Activity(
        string? Details,
        string? State,
        string? LargeImage = null,
        string? LargeText = null,
        string? SmallImage = null,
        string? SmallText = null,
        long? StartUnixSeconds = null);

    public void Set(Activity? activity)
    {
        var json = activity == null ? null : Build(activity);
        lock (_gate)
        {
            if (_disposed) return;
            if (JsonNode.DeepEquals(json, _desired)) return;
            _desired = json;
            _dirty = true;
            _wake.TrySetResult();
        }
    }

    public void Clear() => Set(null);

    static JsonObject Build(Activity a)
    {
        var o = new JsonObject();
        // Discord rejects empty strings and strings under 2 chars.
        static bool Ok(string? s) => s != null && s.Trim().Length >= 2;
        if (Ok(a.Details)) o["details"] = Trunc(a.Details!);
        if (Ok(a.State)) o["state"] = Trunc(a.State!);
        if (a.StartUnixSeconds is long start) o["timestamps"] = new JsonObject { ["start"] = start };
        var assets = new JsonObject();
        if (Ok(a.LargeImage)) assets["large_image"] = a.LargeImage;
        if (Ok(a.LargeText)) assets["large_text"] = Trunc(a.LargeText!);
        if (Ok(a.SmallImage)) assets["small_image"] = a.SmallImage;
        if (Ok(a.SmallText)) assets["small_text"] = Trunc(a.SmallText!);
        if (assets.Count > 0) o["assets"] = assets;
        return o;
    }

    static string Trunc(string s) => s.Length <= 128 ? s : s[..127] + "…";

    static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------- connection loop ----------
    async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            // Nothing to show and not connected: don't bother Discord, wait for a Set().
            Task wake;
            bool wantSomething;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted) _wake = NewWake();
                wake = _wake.Task;
                wantSomething = _desired != null;
            }
            if (!wantSomething)
            {
                try { await wake.WaitAsync(ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            TimeSpan backoff = RetryDelay;
            try
            {
                if (await ConnectAsync(ct))
                {
                    backoff = TimeSpan.FromSeconds(3); // Discord just went away; try again soon
                    await ServeAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (DiscordClosedException ex)
            {
                Log?.Invoke("Discord closed the connection: " + ex.Message);
                backoff = TimeSpan.FromSeconds(60);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Discord IPC error: " + ex.Message);
            }
            finally
            {
                DropPipe();
            }

            // Wait before retrying; a new Set() doesn't shorten this (avoids hammering a missing Discord).
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
        }
    }

    async Task<bool> ConnectAsync(CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                pipe.Dispose();
                continue;
            }

            try
            {
                await WriteFrameAsync(pipe, OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = _clientId }, ct);
                using var hs = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, hs.Token);
                var (op, body) = await ReadFrameAsync(pipe, linked.Token);
                if (op == OpClose) throw new DiscordClosedException(Describe(body));
                if (op != OpFrame || body?["evt"]?.GetValue<string>() != "READY")
                    throw new IOException("unexpected handshake reply: " + body?.ToJsonString());
            }
            catch
            {
                pipe.Dispose();
                throw;
            }

            _pipe = pipe;
            lock (_gate) _dirty = true; // (re)send current activity on every new connection
            Log?.Invoke($"Connected to Discord on discord-ipc-{i}");
            return true;
        }
        return false;
    }

    async Task ServeAsync(CancellationToken ct)
    {
        var pipe = _pipe!;
        var reader = ReadLoopAsync(pipe, ct);
        while (!ct.IsCancellationRequested)
        {
            Task wake;
            JsonObject? send = null;
            bool dirty;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted) _wake = NewWake();
                wake = _wake.Task;
                dirty = _dirty;
                if (dirty) { send = _desired; _dirty = false; }
            }

            if (dirty)
            {
                await SendActivityAsync(pipe, send, ct);
                continue;
            }

            var done = await Task.WhenAny(wake, reader).WaitAsync(ct);
            if (done == reader)
            {
                await reader; // rethrows the reason the connection ended
                return;
            }
        }
    }

    async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (op, body) = await ReadFrameAsync(pipe, ct);
            switch (op)
            {
                case OpPing:
                    await WriteFrameAsync(pipe, OpPong, body ?? new JsonObject(), ct);
                    break;
                case OpClose:
                    throw new IOException("Discord sent CLOSE: " + Describe(body));
                case OpFrame:
                    if (body?["evt"]?.GetValue<string>() == "ERROR")
                        Log?.Invoke("Discord rejected the activity: " + Describe(body?["data"] as JsonObject));
                    else
                        Log?.Invoke($"Discord: {body?["cmd"]} ok {body?["data"]?.ToJsonString()}");
                    break;
            }
        }
    }

    Task SendActivityAsync(NamedPipeClientStream pipe, JsonObject? activity, CancellationToken ct)
    {
        var args = new JsonObject { ["pid"] = _pid };
        args["activity"] = activity?.DeepClone(); // null clears the presence
        var msg = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = args,
            ["nonce"] = Guid.NewGuid().ToString()
        };
        return WriteFrameAsync(pipe, OpFrame, msg, ct);
    }

    async Task WriteFrameAsync(Stream pipe, int op, JsonNode payload, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), json.Length);
        json.CopyTo(frame, 8);
        await _writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    static async Task<(int op, JsonObject? body)> ReadFrameAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct); // EndOfStreamException when Discord quits
        int op = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (len < 0 || len > 4 * 1024 * 1024) throw new IOException("bad frame length " + len);
        var data = new byte[len];
        await pipe.ReadExactlyAsync(data, ct);
        JsonObject? body = null;
        try { body = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { }
        return (op, body);
    }

    static string Describe(JsonObject? o) =>
        o == null ? "(no details)" : $"{o["code"]} {o["message"]}".Trim();

    void DropPipe()
    {
        var p = _pipe;
        _pipe = null;
        try { p?.Dispose(); } catch { }
    }

    // Best-effort: clear the activity (Discord also clears it when the pipe closes), then stop.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        var pipe = _pipe;
        if (pipe?.IsConnected == true)
        {
            try
            {
                using var t = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                Task.Run(() => SendActivityAsync(pipe, null, t.Token)).Wait(600);
            }
            catch { }
        }
        _cts.Cancel();
        DropPipe();
        try { _loop.Wait(1000); } catch { }
        _cts.Dispose();
    }

    sealed class DiscordClosedException(string message) : Exception(message);
}
