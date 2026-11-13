using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Orca;

/// <summary>A request the other side answered with an error. <see cref="Kind"/> says how to rethrow it.</summary>
internal sealed class ChannelException(string kind, string message, JsonObject? detail = null) : Exception(message)
{
    public string Kind { get; } = kind;
    public JsonObject? Detail { get; } = detail;
}

/// <summary>The channel closed (the other process exited, or sent something that is not the protocol).</summary>
internal sealed class ChannelClosedException(string message) : Exception(message);

/// <summary>
/// Newline-delimited JSON between the Host and a module process, the same both ways: <c>{ "op": "...", "rid": n, ... }</c> requests,
/// each answered by <c>{ "op": "reply", "rid": n, "ok": true, "value": ... }</c> or <c>{ ..., "ok": false, "error": { "kind", "message" } }</c>.
/// Requests from either side may overlap. A line longer than the limit closes the channel: a module cannot make the Host buffer without end.
/// </summary>
internal sealed class LineChannel
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly int _maxLineBytes;
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonNode?>> _handle;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private long _next;
    private int _inFlight;
    private string? _closeReason;

    public LineChannel(Stream input, Stream output, int maxLineBytes, Func<string, JsonObject, CancellationToken, Task<JsonNode?>> handle)
    {
        _input = input;
        _output = output;
        _maxLineBytes = maxLineBytes;
        _handle = handle;
        Completion = Task.Run(ReadLoopAsync);
    }

    /// <summary>Finishes when the channel closes.</summary>
    public Task Completion { get; }

    public bool IsClosed => _closed.IsCancellationRequested;

    public string CloseReason => _closeReason ?? "closed";

    /// <summary>Sends a request and waits for its reply. Errors from the other side come back as <see cref="ChannelException"/>.</summary>
    public async Task<JsonNode?> RequestAsync(string op, JsonObject body, CancellationToken ct)
    {
        if (IsClosed) throw new ChannelClosedException(CloseReason);
        var rid = Interlocked.Increment(ref _next);
        body["op"] = op;
        body["rid"] = rid;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[rid] = tcs;
        try
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            await WriteAsync(body, ct);
            var reply = await tcs.Task;
            if (reply["ok"]?.GetValue<bool>() == true) return reply["value"]?.DeepClone();
            var error = reply["error"] as JsonObject;
            throw new ChannelException(error?["kind"]?.GetValue<string>() ?? "failure", error?["message"]?.GetValue<string>() ?? "failed", error);
        }
        finally { _pending.TryRemove(rid, out _); }
    }

    /// <summary>A message that wants no answer (the reply, if any, is dropped).</summary>
    public void Post(string op, JsonObject body) => _ = RequestAsync(op, body, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    public void Close(string reason)
    {
        _closeReason ??= reason;
        if (_closed.IsCancellationRequested) return;
        _closed.Cancel();
        foreach (var p in _pending.Values) p.TrySetException(new ChannelClosedException(CloseReason));
        try { _output.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"); // JSON escapes newlines, so one message is one line
        if (bytes.Length > _maxLineBytes) throw new ChannelException("too_large", $"The message is larger than {_maxLineBytes} bytes.");
        await _write.WaitAsync(ct);
        try
        {
            await _output.WriteAsync(bytes, ct);
            await _output.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close("the other side stopped reading");
            throw new ChannelClosedException(CloseReason);
        }
        finally { _write.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        try
        {
            while (!IsClosed)
            {
                var n = await _input.ReadAsync(buffer, _closed.Token);
                if (n == 0)
                {
                    // Input ended: answer what is already being handled (briefly), then close.
                    for (var i = 0; i < 100 && Volatile.Read(ref _inFlight) > 0; i++) await Task.Delay(50);
                    Close("the other side closed the channel");
                    return;
                }
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    Dispatch(line.ToArray());
                    line.SetLength(0);
                }
                line.Write(buffer, start, n - start);
                if (line.Length > _maxLineBytes) { Close($"a message was larger than {_maxLineBytes} bytes"); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Close("the channel broke"); }
        catch (ChannelClosedException) { }
    }

    private void Dispatch(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        if (bytes.Length > _maxLineBytes) { Close($"a message was larger than {_maxLineBytes} bytes"); return; }
        JsonObject msg;
        try { msg = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { Close("the other side sent something that is not the protocol"); return; }

        var op = msg["op"]?.GetValue<string>() ?? "";
        var rid = msg["rid"]?.GetValue<long>() ?? 0;
        if (op == "reply")
        {
            if (_pending.TryGetValue(rid, out var tcs)) tcs.TrySetResult(msg);
            return;
        }
        Interlocked.Increment(ref _inFlight);
        _ = Task.Run(async () =>
        {
            try { await HandleOneAsync(op, rid, msg); }
            finally { Interlocked.Decrement(ref _inFlight); }
        });
    }

    private async Task HandleOneAsync(string op, long rid, JsonObject msg)
    {
        {
            JsonObject reply;
            try { reply = new JsonObject { ["ok"] = true, ["value"] = await _handle(op, msg, _closed.Token) }; }
            catch (ChannelException ex) { reply = Error(ex.Kind, ex.Message, ex.Detail); }
            catch (OperationCanceledException) { reply = Error("cancelled", "Cancelled."); }
            catch (Exception ex) { reply = Error("failure", ex.Message); }
            reply["op"] = "reply";
            reply["rid"] = rid;
            try { await WriteAsync(reply, CancellationToken.None); }
            catch (ChannelException ex) when (ex.Kind == "too_large")
            {
                var small = Error("too_large", ex.Message);
                small["op"] = "reply";
                small["rid"] = rid;
                try { await WriteAsync(small, CancellationToken.None); } catch (ChannelClosedException) { }
            }
            catch (ChannelClosedException) { }
        }
    }

    private static JsonObject Error(string kind, string message, JsonObject? detail = null)
    {
        var e = detail?.DeepClone() as JsonObject ?? new JsonObject();
        e["kind"] = kind;
        e["message"] = message;
        return new JsonObject { ["ok"] = false, ["error"] = e };
    }
}
