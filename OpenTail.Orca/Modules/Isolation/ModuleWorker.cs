using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTail.Core.Tools.Abstractions;

namespace OpenTail.Orca;

/// <summary>
/// The module side of process isolation. A deployment's own program, started with <see cref="Flag"/>, becomes a worker: it builds one module
/// from the same referenced packages, reports its tools, and runs calls and previews the Host sends over stdin/stdout. It never sees an HTTP
/// credential: its HTTP clients send each request to the Host, which checks the host rules, attaches the credential and returns the answer.
/// Only <see cref="CredentialKind.Secret"/> credentials (a database password) are handed over, by name. Both deployment runners call
/// <see cref="TryRunAsync"/> first, so every deployment can act as its own worker.
/// </summary>
public static class ModuleWorker
{
    public const string Flag = "--module-worker";

    /// <summary>Runs as a worker when <paramref name="args"/> start with <see cref="Flag"/>; otherwise returns null.</summary>
    public static async Task<int?> TryRunAsync(string[] args, IEnumerable<IHostModuleFactory>? extraFactories = null, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] != Flag) return null;
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error); // whatever a module prints goes to stderr; stdout carries only the protocol
        await using var stdin = Console.OpenStandardInput();
        return await RunAsync(stdin, stdout, extraFactories, ct);
    }

    public static async Task<int> RunAsync(Stream input, Stream output, IEnumerable<IHostModuleFactory>? extraFactories = null, CancellationToken ct = default)
    {
        var worker = new Worker(extraFactories);
        var channel = new LineChannel(input, output, IsolationDefaults.MaxMessageBytes, worker.HandleAsync);
        worker.Channel = channel;
        using var reg = ct.Register(() => channel.Close("cancelled"));
        await Task.WhenAny(channel.Completion, worker.Stopped);
        channel.Close("stopped");
        return 0;
    }

    private sealed class Worker(IEnumerable<IHostModuleFactory>? extra)
    {
        private readonly ConcurrentDictionary<long, CancellationTokenSource> _calls = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, ToolRegistration> _tools = new(StringComparer.Ordinal);
        public LineChannel Channel { get; set; } = null!;
        public Task Stopped => _stopped.Task;

        public async Task<JsonNode?> HandleAsync(string op, JsonObject msg, CancellationToken ct)
        {
            switch (op)
            {
                case "init": return await InitAsync(msg, ct);
                case "call": return await CallAsync(msg, ct);
                case "preview": return await PreviewAsync(msg, ct);
                case "cancel":
                    if (_calls.TryGetValue(msg["call"]!.GetValue<long>(), out var cts)) cts.Cancel();
                    return null;
                case "shutdown":
                    _ = Task.Delay(50).ContinueWith(_ => _stopped.TrySetResult(), TaskScheduler.Default);
                    return null;
                default: throw new ChannelException("failure", $"Unknown operation '{op}'.");
            }
        }

        private async Task<JsonNode?> InitAsync(JsonObject msg, CancellationToken ct)
        {
            if (_tools.Count > 0) throw new ChannelException("failure", "Already initialised.");
            var id = msg["id"]!.GetValue<string>();
            var type = msg["type"]!.GetValue<string>();
            var prefixes = (msg["factoryAssemblies"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
            var factories = ModuleLoader.DiscoverFactories(prefixes, extra);
            if (!factories.TryGetValue(type, out var factory))
                throw new ChannelException("failure", $"This program has no module type '{type}' (found: {string.Join(", ", factories.Keys.Order())}).");

            using var configDoc = JsonDocument.Parse(msg["config"]?.ToJsonString() ?? "{}");
            var names = (msg["credentials"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
            var context = new ModuleContext
            {
                Id = id,
                Config = configDoc.RootElement.Clone(),
                Credentials = new BrokeredCredentials(Channel, names),
                Http = new BrokeredHttp(Channel, names),
                Warn = m => Channel.Post("warn", new JsonObject { ["message"] = m }),
                BaseDirectory = msg["baseDirectory"]?.GetValue<string>() ?? AppContext.BaseDirectory,
            };
            var module = await factory.CreateAsync(context, ct);
            if (module.Id != id) throw new ChannelException("failure", $"Factory '{type}' built a module with id '{module.Id}' for '{id}'.");

            var capture = new Capture();
            module.Register(capture);
            foreach (var t in capture.Tools) _tools[t.Descriptor.Name] = t;
            var assembly = module.GetType().Assembly;
            return new JsonObject
            {
                ["manifest"] = JsonSerializer.SerializeToNode(module.Manifest, IsolationDefaults.Json),
                ["assembly"] = assembly.GetName().Name,
                ["version"] = ToolCatalog.VersionOf(assembly),
                ["contract"] = ToolCatalog.ContractOf(assembly, id),
                ["pid"] = Environment.ProcessId,
                ["tools"] = new JsonArray(capture.Tools.Select(t => (JsonNode?)new JsonObject
                {
                    ["descriptor"] = JsonSerializer.SerializeToNode(t.Descriptor, IsolationDefaults.Json),
                    ["description"] = t.Function.Description,
                    ["schema"] = JsonNode.Parse(t.Function.JsonSchema.GetRawText()),
                    ["preview"] = t.Preview is not null,
                }).ToArray()),
            };
        }

        private async Task<JsonNode?> CallAsync(JsonObject msg, CancellationToken ct)
        {
            var tool = Tool(msg);
            var rid = msg["rid"]!.GetValue<long>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _calls[rid] = cts;
            try
            {
                var args = new AIFunctionArguments();
                if (msg["args"] is JsonObject a)
                    foreach (var (k, v) in a) args[k] = JsonSerializer.SerializeToElement(v);
                args.Context = new Dictionary<object, object?>();
                if (msg["context"] is JsonObject c)
                    foreach (var (k, v) in c) args.Context[k] = v?.GetValue<string>();
                var raw = await tool.Function.InvokeAsync(args, cts.Token);
                return raw switch
                {
                    null => null,
                    JsonElement e => JsonNode.Parse(e.GetRawText()),
                    JsonNode n => n,
                    string s => JsonValue.Create(s),
                    _ => JsonSerializer.SerializeToNode(raw, AIJsonUtilities.DefaultOptions),
                };
            }
            catch (ToolTransientException ex)
            {
                throw new ChannelException("transient", ex.Message, ex.RetryAfter is { } ra ? new JsonObject { ["retryAfterSeconds"] = ra.TotalSeconds } : null);
            }
            catch (ToolFailureException ex)
            {
                throw new ChannelException("failure", ex.Message, new JsonObject
                {
                    ["kind"] = ex.Kind.ToString(), ["code"] = ex.Code,
                    ["retryAfterSeconds"] = ex.RetryAfter is { } ra ? ra.TotalSeconds : null,
                });
            }
            catch (ToolGuardrailTripwireException ex)
            {
                throw new ChannelException("tripwire", ex.Reason, new JsonObject { ["guardrail"] = ex.GuardrailKey, ["tool"] = ex.ToolName });
            }
            catch (ArgumentException ex) { throw new ChannelException("argument", ex.Message); }
            finally { _calls.TryRemove(rid, out _); }
        }

        private async Task<JsonNode?> PreviewAsync(JsonObject msg, CancellationToken ct)
        {
            var tool = Tool(msg);
            if (tool.Preview is null) throw new ChannelException("failure", "This tool has no preview.");
            try
            {
                var p = await tool.Preview.PreviewAsync(msg["args"]?.GetValue<string>() ?? "{}", ct);
                return new JsonObject { ["summary"] = p.Summary, ["diffJson"] = p.DiffJson };
            }
            catch (ArgumentException ex) { throw new ChannelException("argument", ex.Message); }
        }

        private ToolRegistration Tool(JsonObject msg) =>
            _tools.TryGetValue(msg["tool"]?.GetValue<string>() ?? "", out var t) ? t : throw new ChannelException("failure", "Unknown tool.");
    }

    private sealed class Capture : IToolRegistry
    {
        public List<ToolRegistration> Tools { get; } = [];
        public void Add(ToolDescriptor descriptor, AIFunction function, IPreviewableTool? preview = null) => Tools.Add(new(descriptor, function, preview));
    }

    /// <summary>Secret credentials, fetched from the Host by name when a tool asks. HTTP credentials are refused by the Host.</summary>
    private sealed class BrokeredCredentials(LineChannel channel, IReadOnlyCollection<string> names) : ICredentialProvider
    {
        public IReadOnlyCollection<string> Names => names;

        public async ValueTask<Credential> GetAsync(string name, CancellationToken ct = default)
        {
            JsonNode? v;
            try { v = await channel.RequestAsync("secret", new JsonObject { ["name"] = name }, ct); }
            catch (ChannelException ex) { throw new InvalidOperationException(ex.Message); }
            return new Credential(Enum.Parse<CredentialKind>(v!["kind"]!.GetValue<string>()), v["secret"]!.GetValue<string>())
            {
                UserName = v["userName"]?.GetValue<string>(),
                HeaderName = v["headerName"]?.GetValue<string>(),
            };
        }

        public void Invalidate(string name) => channel.Post("invalidate", new JsonObject { ["name"] = name });
    }

    /// <summary>HTTP clients whose every request is carried out by the Host's guarded client.</summary>
    private sealed class BrokeredHttp(LineChannel channel, IReadOnlyCollection<string> names) : IModuleHttpClientFactory
    {
        public HttpClient Create(string? credentialName = null)
        {
            if (credentialName is not null && !names.Contains(credentialName))
                throw new InvalidOperationException($"Credential '{credentialName}' is not mapped to this module.");
            return new HttpClient(new Handler(channel, credentialName)) { Timeout = Timeout.InfiniteTimeSpan };
        }

        private sealed class Handler(LineChannel channel, string? credential) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var headers = new JsonArray();
                foreach (var h in request.Headers) foreach (var v in h.Value) headers.Add(new JsonArray(h.Key, v));
                if (request.Content is not null) foreach (var h in request.Content.Headers) foreach (var v in h.Value) headers.Add(new JsonArray(h.Key, v));
                var body = request.Content is null ? null : Convert.ToBase64String(await request.Content.ReadAsByteArrayAsync(ct));

                JsonNode? r;
                try
                {
                    r = await channel.RequestAsync("http", new JsonObject
                    {
                        ["credential"] = credential, ["method"] = request.Method.Method, ["url"] = request.RequestUri!.AbsoluteUri,
                        ["headers"] = headers, ["body"] = body,
                    }, ct);
                }
                catch (ChannelException ex) { throw new HttpRequestException(ex.Message); }

                var response = new HttpResponseMessage((HttpStatusCode)r!["status"]!.GetValue<int>())
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(r["body"] is { } b ? Convert.FromBase64String(b.GetValue<string>()) : []),
                };
                foreach (var pair in (r["headers"] as JsonArray) ?? [])
                {
                    var (k, v) = (pair![0]!.GetValue<string>(), pair[1]!.GetValue<string>());
                    if (!response.Headers.TryAddWithoutValidation(k, v)) response.Content.Headers.TryAddWithoutValidation(k, v);
                }
                return response;
            }
        }
    }
}

internal static class IsolationDefaults
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };
}
