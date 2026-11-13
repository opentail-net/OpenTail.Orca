using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTail.Core.Tools.Abstractions;

namespace OpenTail.Orca;

/// <summary>A module whose code runs in another process: the catalog shows the real module's assembly, version and contract.</summary>
internal interface IModuleOrigin
{
    string AssemblyName { get; }
    string AssemblyVersion { get; }
    int ContractVersion { get; }
}

/// <summary>
/// The Host side of <c>"Isolation": { "Mode": "Process" }</c>. Starts the worker (the deployment's own program with
/// <see cref="ModuleWorker.Flag"/>, behind an optional operator command such as <c>systemd-run -p PrivateNetwork=yes</c>), with an
/// environment that holds none of the Host's variables. Carries out the worker's HTTP requests with the module's guarded client (so the
/// worker never holds an HTTP credential and needs no network of its own), hands over only <c>Secret</c> credentials, bounds every call by
/// a timeout and every message by a size, and stops and restarts a worker that hangs, crashes or misbehaves.
/// </summary>
public sealed class IsolatedModule : IHostModule, IModuleOrigin, IAsyncDisposable
{
    private static readonly string[] PassThrough =
        ["PATH", "SystemRoot", "windir", "TEMP", "TMP", "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "LANG", "LC_ALL", "TZ",
         "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "ProgramFiles", "ProgramData", "SystemDrive", "COMPUTERNAME", "NUMBER_OF_PROCESSORS"];

    private readonly ModuleEntry _entry;
    private readonly JsonNode _config;
    private readonly string _baseDirectory;
    private readonly IReadOnlyList<string> _factoryAssemblies;
    private readonly ModuleIsolation _iso;
    private readonly ModuleCredentials _credentials;
    private readonly GuardedHttpClientFactory _http;
    private readonly Action<string> _warn;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _start = new(1, 1);

    private Process? _process;
    private LineChannel? _channel;
    private JsonObject _info = null!;
    private IReadOnlyList<string> _toolNames = [];

    private IsolatedModule(ModuleEntry entry, JsonElement config, string baseDirectory, IReadOnlyList<string> factoryAssemblies,
        ModuleCredentials credentials, GuardedHttpClientFactory http, Action<string> warn)
    {
        _entry = entry;
        _config = JsonNode.Parse(config.GetRawText()) ?? new JsonObject();
        _baseDirectory = baseDirectory;
        _factoryAssemblies = factoryAssemblies;
        _iso = entry.Isolation!;
        _credentials = credentials;
        _http = http;
        _warn = warn;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Kill("the Host is exiting");
    }

    internal static async Task<IsolatedModule> StartAsync(ModuleEntry entry, JsonElement config, string baseDirectory, IReadOnlyList<string> factoryAssemblies,
        ModuleCredentials credentials, GuardedHttpClientFactory http, Action<string> warn, CancellationToken ct)
    {
        var m = new IsolatedModule(entry, config, baseDirectory, factoryAssemblies, credentials, http, warn);
        await m.EnsureStartedAsync(ct);
        return m;
    }

    public string Id => _entry.Id;

    public ModuleManifest Manifest { get; private set; } = null!;

    string IModuleOrigin.AssemblyName => _info["assembly"]!.GetValue<string>();
    string IModuleOrigin.AssemblyVersion => _info["version"]!.GetValue<string>();
    int IModuleOrigin.ContractVersion => _info["contract"]!.GetValue<int>();

    /// <summary>The worker's process id (for operators and tests).</summary>
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    public void Register(IToolRegistry registry)
    {
        foreach (var t in (JsonArray)_info["tools"]!)
        {
            var descriptor = t!["descriptor"].Deserialize<ToolDescriptor>(IsolationDefaults.Json)!;
            var schema = JsonSerializer.SerializeToElement(t["schema"]);
            registry.Add(descriptor, new ProxyFunction(this, descriptor.Name, t["description"]?.GetValue<string>() ?? descriptor.Description, schema),
                t["preview"]?.GetValue<bool>() == true ? new ProxyPreview(this, descriptor.Name) : null);
        }
    }

    // ---------------------------------------------------------------------------------------------------------- calls

    internal async Task<JsonNode?> CallAsync(string op, JsonObject body, CancellationToken ct)
    {
        var channel = await EnsureStartedAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_iso.CallTimeout);
        try
        {
            return await channel.RequestAsync(op, body, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill("a call timed out");
            throw new ToolTransientException($"Module '{Id}' did not answer within {_iso.CallTimeout.TotalSeconds:0} s; its process was stopped and starts again on the next call.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            channel.Post("cancel", new JsonObject { ["call"] = body["rid"]?.GetValue<long>() ?? 0 });
            throw;
        }
        catch (ChannelClosedException ex)
        {
            Kill(ex.Message);
            throw new ToolTransientException($"Module '{Id}' process stopped ({ex.Message}); it starts again on the next call.");
        }
        catch (ChannelException ex)
        {
            throw ex.Kind switch
            {
                "argument" => new ArgumentException(ex.Message),
                "transient" => new ToolTransientException(ex.Message, ex.Detail?["retryAfterSeconds"] is { } s ? TimeSpan.FromSeconds(s.GetValue<double>()) : null),
                "failure" => new ToolFailureException(
                    Enum.TryParse<ToolFailureKind>(ex.Detail?["kind"]?.GetValue<string>(), out var kind) ? kind : ToolFailureKind.Bug, ex.Message,
                    ex.Detail?["code"]?.GetValue<string>(),
                    ex.Detail?["retryAfterSeconds"] is { } fs ? TimeSpan.FromSeconds(fs.GetValue<double>()) : null),
                "tripwire" => new ToolGuardrailTripwireException(ex.Detail?["guardrail"]?.GetValue<string>() ?? "?", ex.Detail?["tool"]?.GetValue<string>() ?? "?", ex.Message),
                "too_large" => new ToolTransientException($"Module '{Id}' answered with more than {_iso.MaxMessageBytes} bytes."),
                "cancelled" => new OperationCanceledException(ex.Message),
                _ => new InvalidOperationException(ex.Message),
            };
        }
    }

    private async Task<LineChannel> EnsureStartedAsync(CancellationToken ct)
    {
        if (_channel is { IsClosed: false } live && _process is { HasExited: false }) return live;
        await _start.WaitAsync(ct);
        try
        {
            if (_channel is { IsClosed: false } again && _process is { HasExited: false }) return again;
            Kill("restarting");
            Process process;
            LineChannel channel;
            try { (process, channel) = Launch(); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException($"Module '{Id}' could not be started in its own process: {ex.Message}. Check Isolation.Command and WorkerCommand.");
            }
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startTimeout.CancelAfter(_iso.StartTimeout);
            JsonObject info;
            try
            {
                info = (JsonObject)(await channel.RequestAsync("init", new JsonObject
                {
                    ["id"] = _entry.Id, ["type"] = _entry.Type, ["config"] = _config.DeepClone(), ["baseDirectory"] = _baseDirectory,
                    ["credentials"] = new JsonArray(_credentials.Names.Select(n => (JsonNode?)n).ToArray()),
                    ["factoryAssemblies"] = new JsonArray(_factoryAssemblies.Select(n => (JsonNode?)n).ToArray()),
                }, startTimeout.Token))!;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelException or ChannelClosedException)
            {
                KillProcess(process);
                var why = ex is OperationCanceledException ? $"it did not start within {_iso.StartTimeout.TotalSeconds:0} s" : Redaction.Scrub(ex.Message);
                throw new InvalidOperationException($"Module '{Id}' could not be started in its own process: {why}.");
            }

            var names = ((JsonArray)info["tools"]!).Select(t => t!["descriptor"]!["Name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
            if (_process is not null || _info is not null)
                if (!names.SequenceEqual(_toolNames))
                {
                    KillProcess(process);
                    throw new InvalidOperationException($"Module '{Id}' came back with different tools after a restart; restart the Host.");
                }
            _toolNames = names;
            _info = info;
            Manifest = info["manifest"].Deserialize<ModuleManifest>(IsolationDefaults.Json)!;
            _process = process;
            _channel = channel;
            return channel;
        }
        finally { _start.Release(); }
    }

    private (Process, LineChannel) Launch()
    {
        var command = _iso.Command.Concat(_iso.WorkerCommand is { Count: > 0 } w ? w : DefaultWorker()).Append(ModuleWorker.Flag).ToList();
        var psi = new ProcessStartInfo(command[0])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _baseDirectory,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in command.Skip(1)) psi.ArgumentList.Add(a);
        // The worker inherits nothing from the Host's environment (where credentials often live) except what a runtime needs.
        var parent = Environment.GetEnvironmentVariables();
        psi.Environment.Clear();
        foreach (var name in PassThrough.Concat(_iso.Environment))
            if (parent[name] is string v) psi.Environment[name] = v;
        psi.Environment["OPENTAIL_MODULE_ID"] = _entry.Id;

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Module '{Id}': could not start {command[0]}.");
        _ = Task.Run(async () =>
        {
            try
            {
                var lines = 0;
                while (await process.StandardError.ReadLineAsync() is { } line)
                    if (++lines <= 1000) _warn($"Module '{Id}' (process {process.Id}): {Redaction.Scrub(line.Length > 500 ? line[..500] + "…" : line)}");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        var channel = new LineChannel(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, _iso.MaxMessageBytes, HandleWorkerAsync);
        _ = channel.Completion.ContinueWith(_ => KillProcess(process), TaskScheduler.Default);
        return (process, channel);
    }

    private static List<string> DefaultWorker()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot tell which program to start as the module worker; set Isolation.WorkerCommand.");
        return Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [path, Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("No entry assembly; set Isolation.WorkerCommand.")]
            : [path];
    }

    // ---------------------------------------------------------------------------------------------------------- the worker's requests

    private async Task<JsonNode?> HandleWorkerAsync(string op, JsonObject msg, CancellationToken ct)
    {
        switch (op)
        {
            case "http": return await HttpAsync(msg, ct);
            case "secret":
            {
                var name = msg["name"]?.GetValue<string>() ?? "";
                if (!_credentials.Names.Contains(name)) throw new ChannelException("failure", $"Credential '{name}' is not mapped to module '{Id}'.");
                var c = await _credentials.GetAsync(name, ct);
                if (c.Kind != CredentialKind.Secret)
                    throw new ChannelException("failure", $"Credential '{name}' is an HTTP credential: it stays in the Host and is attached to requests made with the module's HTTP client.");
                return new JsonObject { ["kind"] = c.Kind.ToString(), ["secret"] = c.Secret, ["userName"] = c.UserName, ["headerName"] = c.HeaderName };
            }
            case "invalidate":
                if (msg["name"]?.GetValue<string>() is { } n && _credentials.Names.Contains(n)) _credentials.Invalidate(n);
                return null;
            case "warn":
                _warn($"Module '{Id}': {Redaction.Scrub(msg["message"]?.GetValue<string>() ?? "")}");
                return null;
            default: throw new ChannelException("failure", $"Unknown operation '{op}'.");
        }
    }

    private async Task<JsonNode?> HttpAsync(JsonObject msg, CancellationToken ct)
    {
        var credential = msg["credential"]?.GetValue<string>();
        HttpClient client;
        try { client = _clients.GetOrAdd(credential ?? "", c => _http.Create(c.Length == 0 ? null : c)); }
        catch (InvalidOperationException ex) { throw new ChannelException("failure", ex.Message); }

        if (!Uri.TryCreate(msg["url"]?.GetValue<string>(), UriKind.Absolute, out var url)) throw new ChannelException("failure", "Not an absolute URL.");
        using var req = new HttpRequestMessage(new HttpMethod(msg["method"]?.GetValue<string>() ?? "GET"), url);
        if (msg["body"] is { } b) req.Content = new ByteArrayContent(Convert.FromBase64String(b.GetValue<string>()));
        foreach (var pair in (msg["headers"] as JsonArray) ?? [])
        {
            var (k, v) = (pair![0]!.GetValue<string>(), pair[1]!.GetValue<string>());
            if (k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || k.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue; // the Host attaches credentials
            if (!req.Headers.TryAddWithoutValidation(k, v)) req.Content?.Headers.TryAddWithoutValidation(k, v);
        }
        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var bytes = await ReadCappedAsync(resp, _entry.Network.MaxResponseBytes, ct);
            var headers = new JsonArray();
            foreach (var h in resp.Headers) foreach (var v in h.Value) headers.Add(new JsonArray(h.Key, v));
            foreach (var h in resp.Content.Headers) foreach (var v in h.Value) headers.Add(new JsonArray(h.Key, v));
            return new JsonObject { ["status"] = (int)resp.StatusCode, ["headers"] = headers, ["body"] = Convert.ToBase64String(bytes) };
        }
        catch (HttpRequestException ex) { throw new ChannelException("failure", Redaction.Scrub(ex.Message)); }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) throw new ChannelException("failure", $"The answer was larger than {max} bytes.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    // ---------------------------------------------------------------------------------------------------------- lifetime

    private void Kill(string reason)
    {
        var channel = _channel;
        var process = _process;
        channel?.Close(reason);
        if (process is not null) KillProcess(process);
    }

    private static void KillProcess(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is { IsClosed: false } c)
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await c.RequestAsync("shutdown", new JsonObject(), t.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelException or ChannelClosedException) { }
        }
        if (_process is { } p)
        {
            try { await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token); }
            catch (OperationCanceledException) { }
        }
        Kill("disposed");
        _http.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------- proxies

    private sealed class ProxyFunction(IsolatedModule owner, string name, string description, JsonElement schema) : AIFunction
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => schema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var args = new JsonObject();
            foreach (var (k, v) in arguments)
                args[k] = v switch
                {
                    null => null,
                    JsonElement e => JsonNode.Parse(e.GetRawText()),
                    JsonNode n => n.DeepClone(),
                    _ => JsonSerializer.SerializeToNode(v, AIJsonUtilities.DefaultOptions),
                };
            var context = new JsonObject();
            foreach (var (k, v) in arguments.Context ?? new Dictionary<object, object?>())
                if (k is string key && key.StartsWith("opentail.", StringComparison.Ordinal) && v is string value) context[key] = value;
            var result = await owner.CallAsync("call", new JsonObject { ["tool"] = name, ["args"] = args, ["context"] = context }, cancellationToken);
            return result is null ? null : JsonSerializer.SerializeToElement(result);
        }
    }

    private sealed class ProxyPreview(IsolatedModule owner, string tool) : IPreviewableTool
    {
        public async Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            var v = await owner.CallAsync("preview", new JsonObject { ["tool"] = tool, ["args"] = argumentsJson }, ct);
            return new PreviewResult(v?["summary"]?.GetValue<string>() ?? "", v?["diffJson"]?.GetValue<string>());
        }
    }
}
