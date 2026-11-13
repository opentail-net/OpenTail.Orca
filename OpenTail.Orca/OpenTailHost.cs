// Tasks (alternate results, extra request handlers) are marked experimental (MCPEXP002) in SDK 2.2.0; used deliberately.
#pragma warning disable MCPEXP002
using System.Security.Claims;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenTail.Orca;

public sealed class OpenTailHostBuilder
{
    private readonly List<(IHostModule Module, ModuleLoadOptions Options)> _modules = [];
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly List<IToolInputGuardrail> _inputGuards = [];
    private readonly List<IToolOutputGuardrail> _outputGuards = [];
    private PolicyConfig _policy = PolicyConfig.Deny;
    private IActionStore? _store;
    private IAuditSink? _audit;
    private TimeProvider _clock = TimeProvider.System;
    private string _name = "OpenTail Orca";
    private TimeSpan? _lease;
    private ISessionStore? _sessionStore;
    private ITrustStore? _trust;
    private IPolicyProposalStore? _proposals;
    private INoticeStore? _noticeStore;
    private IIdentityLevelStore? _levelStore;
    private ILabelStore? _labelStore;
    private IAccessGrantStore? _grantStore;
    private IPolicyVersionStore? _policyVersions;
    private IWorkStore? _workStore;
    private IAccessSnapshotStore? _snapshots;
    private string? _startedBy;
    private IActionEventSink? _events;
    private CallerResolver _resolveCaller = _ => CallerIdentity.Local;
    private bool _callerResolverSet;
    private IToolSandbox? _sandbox;
    private bool _shadow;

    public OpenTailHostBuilder AddModule(IHostModule module) { _modules.Add((module, ModuleLoadOptions.Code)); return this; }

    /// <summary>Adds a module with explicit options (modules from configuration use this).</summary>
    public OpenTailHostBuilder AddModule(IHostModule module, ModuleLoadOptions options) { _modules.Add((module, options)); return this; }

    /// <summary>A module the operator switched off or removed. Policy entries for its tools (<c>id.*</c>) are tolerated with a warning
    /// instead of stopping startup, its pending actions expire, and promotions of its tools are cleared.</summary>
    public OpenTailHostBuilder RetireModule(string id) { _retired.Add(id); return this; }
    public OpenTailHostBuilder UsePolicy(PolicyConfig policy) { _policy = policy; return this; }
    public OpenTailHostBuilder UseStore(IActionStore store) { _store = store; return this; }
    public OpenTailHostBuilder UseAudit(IAuditSink audit) { _audit = audit; return this; }
    public OpenTailHostBuilder UseClock(TimeProvider clock) { _clock = clock; return this; }
    public OpenTailHostBuilder WithLease(TimeSpan lease) { _lease = lease; return this; }
    /// <param name="synchronous">How hard each commit is pushed to disk. Null reads <c>OPENTAIL_DB_SYNCHRONOUS</c> (the runner's way); tests pass a level explicitly.</param>
    public OpenTailHostBuilder UseSqlite(string path, SqliteSynchronous? synchronous = null)
    {
        var store = new SqliteActionStore(path, _clock, synchronous);
        _store = store;
        _audit = new SqliteAuditSink(store);
        _sessionStore = new SqliteSessionStore(store);
        _trust = new SqliteTrustStore(store);
        _noticeStore = new SqliteNoticeStore(store);
        _levelStore = new SqliteIdentityLevelStore(store);
        _labelStore = new SqliteLabelStore(store);
        _grantStore = new SqliteAccessGrantStore(store);
        _policyVersions = new SqlitePolicyVersionStore(store);
        _proposals = new SqlitePolicyProposalStore(store);
        _workStore = new SqliteWorkStore(store);
        _snapshots = new SqliteAccessSnapshotStore(store);
        return this;
    }

    /// <summary>Where action events go (notifications, webhooks). Optional.</summary>
    public OpenTailHostBuilder UseEventSink(IActionEventSink sink) { _events = sink; return this; }

    /// <summary>Where the policy versions this Host runs under are kept (audit D2). <see cref="UseSqlite"/> sets it; default in memory.</summary>
    public OpenTailHostBuilder UsePolicyVersionStore(IPolicyVersionStore store) { _policyVersions = store; return this; }

    /// <summary>Who started this Host, for the <c>policy_loaded</c> audit entry (the runners pass the operator's name when given).</summary>
    public OpenTailHostBuilder StartedBy(string? name) { _startedBy = name; return this; }

    /// <summary>How an authenticated MCP request becomes a caller. Default: everyone is the local operator (stdio).</summary>
    public OpenTailHostBuilder UseCallerResolver(CallerResolver resolver) { _resolveCaller = resolver; _callerResolverSet = true; return this; }

    public OpenTailHostBuilder UseSessionStore(ISessionStore store) { _sessionStore = store; return this; }
    public OpenTailHostBuilder UseTrustStore(ITrustStore store) { _trust = store; return this; }
    public OpenTailHostBuilder WithName(string name) { _name = name; return this; }
    public OpenTailHostBuilder AddInputGuardrail(IToolInputGuardrail g) { _inputGuards.Add(g); return this; }
    public OpenTailHostBuilder AddOutputGuardrail(IToolOutputGuardrail g) { _outputGuards.Add(g); return this; }

    /// <summary>Sandbox mode: every tool call is answered by <paramref name="sandbox"/> and no module code runs (see
    /// <see cref="IToolSandbox"/>). For the skills workbench and shadow runs, never for a production deployment.</summary>
    public OpenTailHostBuilder UseSandbox(IToolSandbox sandbox) { _sandbox = sandbox; return this; }

    /// <summary>Shadow mode, for shadow runs of a candidate skill on real traffic: Read tools run against the real systems with this
    /// instance's own credentials (give it read-only ones), and everything above Read is recorded and never run.</summary>
    public OpenTailHostBuilder UseShadow() { _sandbox = new RecordingSandbox(RecordingSandbox.ReadLive); _shadow = true; return this; }

    /// <summary>Every check a policy must pass to start with these modules: the same ones for start-up and for a policy proposal.</summary>
    internal static List<string> CheckPolicy(ToolCatalog catalog, PolicyConfig policy, IReadOnlyCollection<string> retired, bool sandboxed, List<string> warnings)
    {
        var retiredTools = policy.Tools.Keys.Where(t => retired.Any(id => t.StartsWith(id + ".", StringComparison.Ordinal))).ToList();
        foreach (var t in retiredTools)
            warnings.Add($"Policy entry '{t}' belongs to module '{t[..t.IndexOf('.')]}', which is switched off; it is ignored.");
        foreach (var (profile, p) in policy.Agents)
            foreach (var entry in p.Tools.Where(e => AgentProfiles.ModuleOf(e) is { } m && retired.Contains(m)))
                warnings.Add($"Agent profile '{profile}' names '{entry}', whose module is switched off; it grants nothing.");
        var errors = policy.Validate(catalog.All.Select(t => t.Descriptor.Name).Concat(retiredTools), retired).ToList();
        var administration = new List<string>();
        AdministrationChecks.Check(catalog, policy, sandboxed, errors, administration);
        AdministrationChecks.CheckRequestable(catalog, policy, errors);
        warnings.AddRange(administration);
        return errors;
    }

    public OpenTailHost Build()
    {
        var catalog = new ToolCatalog();
        foreach (var (m, o) in _modules) catalog.AddModule(m, o);
        // Temporary access exists only where a profile names something its agent may ask for.
        var grants = new AccessGrants(_grantStore ?? new InMemoryAccessGrantStore(), _clock);
        if (_policy.Agents.Values.Any(p => p.Requestable.Count > 0)) catalog.AddModule(new AccessModule(grants), ModuleLoadOptions.Code);
        // Delegation exists only where a profile may hand work to another; the service is built below, once its parts exist.
        WorkService? work = null;
        if (_policy.Agents.Values.Any(p => p.MayDelegateTo.Count > 0) || _policy.Work.Enabled)
            catalog.AddModule(new WorkModule(() => work!, _policy), ModuleLoadOptions.Code);
        foreach (var id in _retired.Where(id => catalog.Modules.Any(m => m.Id == id)))
            throw new InvalidOperationException($"Module '{id}' is both loaded and retired.");

        var warnings = new List<string>();
        var errors = CheckPolicy(catalog, _policy, _retired, sandboxed: _sandbox is not null, warnings);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid policy: " + string.Join(" ", errors));

        var store = _store ?? new InMemoryActionStore(_clock);
        var audit = _audit ?? new InMemoryAuditSink();
        var sessions = new SessionRegistry(_sessionStore ?? new InMemorySessionStore(), _clock, _policy.TaintWindow, _policy.SessionIdleTtl);
        var resolver = new PolicyResolver(_policy, _trust, catalog.Modules.Where(m => !m.Options.AllowDefaultRead).Select(m => m.Id));
        var notices = new NoticeService(_noticeStore ?? new InMemoryNoticeStore(), store, _clock);
        var events = new ActionEvents(_events, audit, catalog, _clock, notices);
        var levels = new IdentityLevels(_levelStore ?? new InMemoryIdentityLevelStore(), _policy.Limits, audit, _clock);
        var labels = new DataLabels(_labelStore ?? new InMemoryLabelStore(), _policy, _clock);
        var executor = new ActionExecutor(catalog, store, audit, sessions, _inputGuards, _outputGuards, _clock, _lease, events, _sandbox,
            new CapacityLimits(_policy, _clock), levels, _policy.Decoys, labels, _policy.Failures, new Circuits(_policy.Failures, _clock), currentPolicyHash: () => resolver.PolicyHash);
        if (_shadow) warnings.Add("Shadow mode: Read tools run against the real systems with this instance's credentials; everything above Read is recorded, never run. Use read-only credentials and a database of its own.");
        else if (_sandbox is not null) warnings.Add("Sandbox mode: tool calls are answered by the sandbox and no module code runs.");
        var trust = new TrustAdvisor(resolver, store, audit, _clock);
        var queue = new ActionQueue(catalog, resolver, store, executor, audit, sessions, _clock, trust, events, levels, labels, grants);
        work = new WorkService(_workStore ?? new InMemoryWorkStore(), store, audit, sessions, labels, _policy, _clock, levels);
        return new OpenTailHost(_name, catalog, resolver, store, audit, sessions, executor, queue, trust, _resolveCaller, _callerResolverSet, notices, _noticeStore)
        {
            IsSandbox = _sandbox is not null,
            RetiredModules = _retired.Order(StringComparer.Ordinal).ToList(),
            StartupWarnings = warnings,
            TrustStore = _trust,
            Levels = levels,
            Labels = labels,
            Grants = grants,
            PolicyVersions = _policyVersions ?? new InMemoryPolicyVersionStore(),
            ProposalStore = _proposals ?? new InMemoryPolicyProposalStore(),
            Clock = _clock,
            Work = work,
            Runners = _policy.Work.Runners.Count > 0 ? new WorkRunners(work, audit) : null,
            AccessSnapshots = _snapshots ?? new InMemoryAccessSnapshotStore(),
            Health = new HostHealth(_clock),
            StartedByName = _startedBy,
        };
    }
}

/// <summary>A composed Host: modules + policy + queue, ready to serve MCP.</summary>
public sealed class OpenTailHost
{
    private readonly string _name;
    private readonly CallerResolver _resolveCaller;
    private readonly INoticeStore? _noticeStore;

    internal OpenTailHost(string name, ToolCatalog catalog, PolicyResolver policy, IActionStore store,
        IAuditSink audit, SessionRegistry sessions, ActionExecutor executor, ActionQueue queue, TrustAdvisor trust,
        CallerResolver resolveCaller, bool callerResolverSet, NoticeService notices, INoticeStore? noticeStore)
    {
        Notices = notices;
        _noticeStore = noticeStore;
        _resolveCaller = resolveCaller;
        CallerResolverConfigured = callerResolverSet;
        Trust = trust;
        _name = name;
        Catalog = catalog;
        Policy = policy;
        Store = store;
        Audit = audit;
        Sessions = sessions;
        Executor = executor;
        Queue = queue;
    }

    /// <summary>False when every caller is the local operator (stdio). A server that signs callers in must set a resolver, or
    /// every caller would share one identity and ownership would mean nothing.</summary>
    public bool CallerResolverConfigured { get; }

    public NoticeService Notices { get; }

    /// <summary>True when built with <see cref="OpenTailHostBuilder.UseSandbox"/>: no module code runs.</summary>
    public bool IsSandbox { get; init; }

    /// <summary>Modules switched off in configuration (see <see cref="OpenTailHostBuilder.RetireModule"/>).</summary>
    public IReadOnlyList<string> RetiredModules { get; init; } = [];

    /// <summary>Things the operator should read, found while building (written to stderr by the runners).</summary>
    public IReadOnlyList<string> StartupWarnings { get; init; } = [];

    internal ITrustStore? TrustStore { get; init; }

    /// <summary>Identity levels (adaptive limits): who is Elevated or Suspended, and clearing them.</summary>
    public IdentityLevels Levels { get; init; } = null!;

    /// <summary>Data labels: what sessions have read, and the data-flow check at outbound tools.</summary>
    public DataLabels Labels { get; init; } = null!;

    /// <summary>Temporary access grants (<c>access.request</c>).</summary>
    public AccessGrants Grants { get; init; } = null!;

    /// <summary>What <c>/healthz</c> reports: whether the maintenance sweep is running, and the checks other parts add.</summary>
    public HostHealth Health { get; init; } = null!;

    /// <summary>The access last seen (API keys), for auditing what changed at the next start (audit D4).</summary>
    public IAccessSnapshotStore AccessSnapshots { get; init; } = null!;

    /// <summary>Cancelled when the Host is disposed: calls that became tasks run on until then, not until their request ends.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Host-started runners, when the policy lists any (work design, step 3).</summary>
    public WorkRunners? Runners { get; init; }

    /// <summary>Work items and continuations: the ledger of work owed and work waited on.</summary>
    public WorkService Work { get; init; } = null!;

    /// <summary>Every policy version this database has run under (audit D2).</summary>
    public IPolicyVersionStore PolicyVersions { get; init; } = null!;

    internal IPolicyProposalStore ProposalStore { get; init; } = null!;
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    private PolicyProposals? _policyProposals;

    /// <summary>Proposed policy changes: validated, replayed, approved by a second admin, deployed by the operator (plan v7, A3).</summary>
    public PolicyProposals Proposals => _policyProposals ??= new PolicyProposals(this, ProposalStore, Clock);

    internal string? StartedByName { get; init; }

    /// <summary>The name this Host gives itself to MCP clients.</summary>
    public string Name => _name;

    private int _policyRecorded;
    public ToolCatalog Catalog { get; }
    public PolicyResolver Policy { get; }
    public IActionStore Store { get; }
    public IAuditSink Audit { get; }
    public SessionRegistry Sessions { get; }
    public ActionExecutor Executor { get; }
    public ActionQueue Queue { get; }
    public TrustAdvisor Trust { get; }

    /// <summary>Operator-facing: why this tool would be handled the way it is.</summary>
    public ExplainResult Explain(string tool, bool tainted = false) => Policy.Explain(Catalog, tool, tainted);

    /// <summary>Tools advertised over MCP: only those policy does not deny, plus the built-ins. Sorted by name.</summary>
    public IReadOnlyList<McpServerTool> BuildTools() => BuildTools(profileFilter: false, profile: null);

    /// <summary>The tools a caller with this agent profile sees: those policy does not deny and the profile admits, plus the built-ins.</summary>
    public IReadOnlyList<McpServerTool> BuildTools(string? profile) => BuildTools(profileFilter: true, profile);

    private List<McpServerTool> BuildTools(bool profileFilter, string? profile, IReadOnlySet<string>? granted = null)
    {
        var tools = new List<McpServerTool>();
        foreach (var t in Catalog.All.Where(t => profileFilter
                     ? Policy.IsAdvertised(t.Descriptor, profile) || granted?.Contains(t.Descriptor.Name) == true && Policy.IsAdvertised(t.Descriptor)
                     : Policy.IsAdvertised(t.Descriptor)))
        {
            var d = t.Descriptor;
            tools.Add(McpServerTool.Create(new QueuedToolFunction(t, Queue, Notices), new McpServerToolCreateOptions
            {
                ReadOnly = d.Risk == ToolRisk.Read,
                Destructive = d.Risk is ToolRisk.Destructive,
                Idempotent = d.IdempotentByKey,
                OpenWorld = d.Risk == ToolRisk.ExternalEffect || d.ReadsUntrusted,
            }));
        }
        var work = Policy.Config.Work.Enabled ? Work : null;
        tools.AddRange(BuiltInTools.Create(Sessions, Store, Queue, Catalog, Notices, work));
        if (work is not null) tools.AddRange(BuiltInTools.CreateWork(work, Sessions));
        return tools.OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The <see cref="ModuleManifest.ServerInstructions"/> of modules with at least one advertised tool, sorted by module id.</summary>
    private string ModuleInstructions()
    {
        var advertised = Catalog.All.Where(t => Policy.IsAdvertised(t.Descriptor)).Select(t => t.Descriptor.ModuleId).ToHashSet(StringComparer.Ordinal);
        var lines = Catalog.Modules.Where(m => advertised.Contains(m.Id) && !string.IsNullOrWhiteSpace(m.Manifest.ServerInstructions))
            .OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Manifest.ServerInstructions!.Trim());
        return string.Concat(lines.Select(l => " " + l));
    }

    /// <summary>When work is enabled, agents are told the tools exist and how to use them (SOP gap G13).</summary>
    private string WorkInstructions() => !Policy.Config.Work.Enabled ? "" :
        " Work can be handed to you: call next_work to claim an item (you hold it for a while), and finish it with complete_work, fail_work " +
        "or hand_back_work. Before you stop while waiting for an approval or other work, call park with a note for your future self; you " +
        "get a resume item through next_work when the waits come back.";

    public McpServerOptions CreateServerOptions()
    {
        // Handlers rather than ToolCollection: the collection does not preserve order, and the spec
        // asks for a deterministic tools/list (it also keeps prompt caches stable).
        var sorted = BuildTools();
        var byName = sorted.ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
        var protocolTools = sorted.Select(t => t.ProtocolTool).ToList();
        // tools/list per agent profile, built once per profile name. A call by name to a tool outside the caller's profile still
        // reaches the queue, which refuses and audits it.
        var byProfile = new System.Collections.Concurrent.ConcurrentDictionary<string, List<Tool>>(StringComparer.Ordinal);
        // The key "\0" stands for a signed-in caller without a profile: everything but administration tools.
        List<Tool> ToolsFor(CallerIdentity caller)
        {
            if (caller.Profile is null && caller.Kind == CallerKind.Local) return protocolTools;
            // A caller with a temporary grant sees the granted tool too, until the grant ends (not cached: grants come and go).
            if (Grants.Active(caller.Subject) is { Count: > 0 } active)
                return BuildTools(profileFilter: true, caller.Profile, active.Select(g => g.Tool).ToHashSet(StringComparer.Ordinal)).Select(t => t.ProtocolTool).ToList();
            return byProfile.GetOrAdd(caller.Profile ?? "\0", _ => BuildTools(caller.Profile).Select(t => t.ProtocolTool).ToList());
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = _name, Version = "0.1.0" },
            ServerInstructions =
                "Some actions are not executed immediately: they return status 'pending_approval' with an actionId. " +
                "Call get_action_status (or wait_for_action, which waits a few seconds) to see the decision, but do not poll in a loop: tell the user " +
                "it is waiting and carry on. Decisions made while you were away arrive in a 'notices' field on later responses, and get_updates lists them; " +
                "call get_updates at the start of a conversation. If the status is 'changes_requested', read the reviewer's reason, " +
                "fix the arguments and call resubmit_action. If 'rejected', do not retry the same action. " +
                "Optionally call start_session first and pass sessionId to later calls." + WorkInstructions() + ModuleInstructions(),
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
                // The Tasks extension: offered to everyone, used only for a client that opts in on a request (docs/design/mcp-tasks.md).
                Extensions = new Dictionary<string, object> { [ActionTasks.ExtensionId] = new System.Text.Json.Nodes.JsonObject() },
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (request, _) => ValueTask.FromResult(new ListToolsResult { Tools = ToolsFor(_resolveCaller(request.User)) }),
                CallToolWithAlternateHandler = async (request, ct) =>
                {
                    var name = request.Params?.Name ?? "";
                    if (!byName.TryGetValue(name, out var tool))
                        throw new McpProtocolException($"Unknown tool '{name}'.", McpErrorCode.InvalidParams);
                    // Who is calling is decided here, at the protocol edge, from the authenticated principal.
                    using var _ = CallerContext.Use(_resolveCaller(request.User));
                    using var captured = ActionCapture.Begin();
                    CallToolResult result;
                    if (ActionTasks.OptedIn(request.JsonRpcRequest))
                    {
                        // A client that takes tasks: the call runs on, detached from this request, and if it outlasts the inline limit the
                        // client gets a task for the action that is still running (step 3). The action finishes either way.
                        var run = Task.Run(() => tool.InvokeAsync(request, _lifetime.Token).AsTask(), CancellationToken.None);
                        var limit = Task.Delay(Policy.Config.TaskInlineLimit, ct);
                        if (await Task.WhenAny(run, limit) != run && captured.Record is { } running && ActionTasks.Unfinished(running))
                        {
                            HostTelemetry.TaskCreated(running);
                            // Observe a late failure so it is not an unobserved task exception; the action records the outcome.
                            var observed = run.ContinueWith(static t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                            return ResultOrAlternate<CallToolResult>.FromAlternate(
                                ActionTasks.Created(await Store.GetAsync(running.Id, ct) ?? running, Policy.Config.RetentionDays, DateTimeOffset.UtcNow),
                                ModelContextProtocol.Extensions.Tasks.McpTasksJsonContext.Default.CreateTaskResult);
                        }
                        result = await run;
                    }
                    else result = await tool.InvokeAsync(request, ct);
                    // A call that did not finish inline becomes a task, if the client asked for tasks on this request. The action was
                    // created and stored either way; this only changes how the client watches it.
                    if (captured.Record is { } record && ActionTasks.Unfinished(record) && ActionTasks.OptedIn(request.JsonRpcRequest))
                    {
                        HostTelemetry.TaskCreated(record);
                        return ResultOrAlternate<CallToolResult>.FromAlternate(
                            ActionTasks.Created(record, Policy.Config.RetentionDays, DateTimeOffset.UtcNow),
                            ModelContextProtocol.Extensions.Tasks.McpTasksJsonContext.Default.CreateTaskResult);
                    }
                    return new ResultOrAlternate<CallToolResult>(result);
                },
            },
            RequestHandlers =
            [
                new McpServerRequestHandler { Method = "tasks/get", RoutingNameParameter = "taskId", Handler = GetTaskAsync },
                new McpServerRequestHandler { Method = "tasks/cancel", RoutingNameParameter = "taskId", Handler = CancelTaskAsync },
                new McpServerRequestHandler { Method = "tasks/update", RoutingNameParameter = "taskId", Handler = UpdateTaskAsync },
            ],
        };
    }

    /// <summary>tasks/get: the same read as get_action_status (ownership, taint, notices), answered as a task.</summary>
    private async ValueTask<System.Text.Json.Nodes.JsonNode?> GetTaskAsync(JsonRpcRequest request, CancellationToken ct)
    {
        ActionTasks.Gate(request, "tasks/get");
        var taskId = ActionTasks.TaskIdOf(request, "tasks/get");
        var caller = _resolveCaller(request.Context?.User);
        var r = await BuiltInTools.ReadOwnAsync(taskId, caller.Subject, null, Sessions, Store, Catalog, Notices, ct)
                ?? throw ActionTasks.NotFound(taskId);
        return System.Text.Json.JsonSerializer.SerializeToNode(ActionTasks.Read(r, Policy.Config.RetentionDays, DateTimeOffset.UtcNow),
            ModelContextProtocol.Extensions.Tasks.McpTasksJsonContext.Default.GetTaskResult);
    }

    /// <summary>tasks/cancel: cancel_action for a pending action. A running or decided one cannot be cancelled; the standard allows
    /// acknowledging anyway, and the task ends as it ends.</summary>
    private async ValueTask<System.Text.Json.Nodes.JsonNode?> CancelTaskAsync(JsonRpcRequest request, CancellationToken ct)
    {
        ActionTasks.Gate(request, "tasks/cancel");
        var taskId = ActionTasks.TaskIdOf(request, "tasks/cancel");
        var caller = _resolveCaller(request.Context?.User);
        var r = await Store.GetAsync(taskId, ct);
        if (r is null || r.Requester.Id != caller.Subject) throw ActionTasks.NotFound(taskId);
        if (r.Status == ActionStatus.Pending)
            try { await Queue.CancelAsync(taskId, r.Requester, ct); }
            catch (InvalidOperationException) { /* decided meanwhile: it ends as it ends */ }
        return System.Text.Json.JsonSerializer.SerializeToNode(new ModelContextProtocol.Extensions.Tasks.CancelTaskResult(),
            ModelContextProtocol.Extensions.Tasks.McpTasksJsonContext.Default.CancelTaskResult);
    }

    /// <summary>tasks/update: the Host asks the client nothing mid-task (a reviewer's decision is not client input).</summary>
    private ValueTask<System.Text.Json.Nodes.JsonNode?> UpdateTaskAsync(JsonRpcRequest request, CancellationToken ct)
    {
        ActionTasks.Gate(request, "tasks/update");
        throw new McpProtocolException("This task asks for no input.", McpErrorCode.InvalidParams);
    }

    /// <summary>Serve over any transport (stdio, in-memory pipes in tests).</summary>
    public McpServer CreateServer(ITransport transport) => McpServer.Create(transport, CreateServerOptions());

    /// <summary>Recovers interrupted/approved-but-unstarted actions and expires overdue ones. Safe to call repeatedly.</summary>
    public async Task<RecoveryReport> RunMaintenanceOnceAsync(CancellationToken ct = default)
    {
        using var activity = HostTelemetry.Source.StartActivity("maintenance.sweep");
        // The first sweep (at start-up) records the policy this process runs under: its version and a policy_loaded entry.
        if (Interlocked.Exchange(ref _policyRecorded, 1) == 0)
        {
            await PolicyLoad.RecordAsync(Policy, PolicyVersions, Audit, DateTimeOffset.UtcNow, StartedByName, ct);
            await Proposals.MarkAppliedAsync(ct);
        }
        await RetireAsync(ct);
        var report = await Executor.RecoverAsync(ct);
        await Executor.RetryDueAsync(ct);
        await Queue.ExpireDueAsync(ct);
        await Queue.RunDueDelayedAsync(ct);
        Sessions.PurgeExpired();
        Labels.Purge();
        await Work.SweepAsync(ct);
        await PurgeOldAsync(ct);
        Health.SweepFinished();
        return report;
    }

    /// <summary>Pending actions of a switched-off module can never run: expire them. Promotions of its tools are cleared, so a
    /// reinstall starts from the policy file again. (Approved-but-unstarted ones are refused at their claim as action_digest_mismatch: their tool is no longer loaded, so the binding cannot be recomputed.)</summary>
    private async Task RetireAsync(CancellationToken ct)
    {
        if (RetiredModules.Count == 0) return;
        var retired = RetiredModules.ToHashSet(StringComparer.Ordinal);
        foreach (var r in await Store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, Limit = int.MaxValue }, ct))
        {
            if (!retired.Contains(r.ModuleId)) continue;
            await AtomicTrail.ChangeAsync(Store, Audit, r.Id, ActionStatus.Pending, ActionStatus.Expired,
                x => (x with { Error = new ActionError("module_removed", $"Module '{x.ModuleId}' was switched off before this was decided.", false) })
                    .WithResolution(ResolutionOutcomes.Expired, "system", "module_removed", DateTimeOffset.UtcNow),
                d => new AuditEntry(d.UpdatedAt, AuditEvents.ApprovalExpired, d.Id, d.ToolName, "system", "module_removed"), ct);
        }
        if (TrustStore is { } trust)
            foreach (var tool in trust.ListPromoted().Where(t => retired.Any(id => t.StartsWith(id + ".", StringComparison.Ordinal))))
                if (trust.Demote(tool))
                    await Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "trust_demoted", null, tool, "system", "module_removed"), ct);
    }

    /// <summary>Deletes finished actions (they hold argument values) after <c>RetentionDays</c>, and old notices. Audit is kept.</summary>
    private async Task PurgeOldAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        _noticeStore?.Purge(now - TimeSpan.FromDays(30));
        if (Store is IUsageStore usage) usage.PurgeUsage(now);
        if (Policy.Config.RetentionDays > 0 && Store is SqliteActionStore sqlite)
        {
            var purged = sqlite.PurgeTerminalBefore(now - TimeSpan.FromDays(Policy.Config.RetentionDays));
            // The digest of the decision records kept in their place goes into the chained audit trail: the records cannot be altered
            // later without breaking it, though the trail never holds their content.
            if (purged.Removed > 0)
                await Audit.AppendAsync(new AuditEntry(now, "retention_purge", null, null, "system",
                    $"{purged.Removed} finished action(s) older than {Policy.Config.RetentionDays} days; decision records kept: batch {purged.Batch}, digest {purged.Digest}"), ct);
        }
        if (Policy.Config.AuditRetentionDays > 0 && Store is SqliteActionStore records
            && records.PurgeDecisionRecordsBefore(now - TimeSpan.FromDays(Policy.Config.AuditRetentionDays)) is var dropped and > 0)
            await Audit.AppendAsync(new AuditEntry(now, "decision_records_purge", null, null, "system",
                $"{dropped} decision record(s) older than {Policy.Config.AuditRetentionDays} days"), ct);

        // D-41: the old part of the audit trail leaves the live database for the archive, and archives past their time are removed. A failure is
        // recorded in the trail and changes nothing: the rows that could not be archived stay where they are.
        if (Store is SqliteActionStore archiveStore && (Policy.Config.AuditLiveDays ?? 28) > 0)
        {
            var archiver = new AuditArchiver(archiveStore);
            var directory = Policy.Config.AuditArchiveDirectory ?? archiveStore.DatabasePath + ".archive";
            try
            {
                archiver.Cut(now, directory, TimeSpan.FromDays(Policy.Config.AuditLiveDays ?? 28));
                archiver.ExpireArchives(now, TimeSpan.FromDays(Policy.Config.AuditArchiveDays ?? 2555));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                await Audit.AppendAsync(new AuditEntry(now, "audit_archive_failed", null, null, "system", ex.Message), ct);
            }
        }
    }

    /// <summary>Serve over this process's stdin/stdout, with recovery at startup and a periodic maintenance sweep.</summary>
    public async Task RunStdioAsync(CancellationToken ct = default)
    {
        await RunMaintenanceOnceAsync(ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sweep = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try { while (await timer.WaitForNextTickAsync(linked.Token)) await RunMaintenanceOnceAsync(linked.Token); }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        await using var server = McpServer.Create(new StdioServerTransport(_name), CreateServerOptions());
        try { await server.RunAsync(ct); }
        finally { await linked.CancelAsync(); await sweep; }
    }
}
