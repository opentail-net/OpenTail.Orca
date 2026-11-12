using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTail.Host.Admin;
using OpenTail.Host.Notifications;

namespace OpenTail.Host.Http;

/// <summary>
/// The shared <c>Main</c> for a team deployment: stdio as before, or <c>--http &lt;config.json&gt;</c> for a signed-in server.
/// <code>
///   --policy &lt;file&gt;        policy JSON (required)
///   --db &lt;file&gt;            SQLite file (default ./opentail-host.db)
///   --http &lt;config.json&gt;   serve Streamable HTTP with sign-in (see <see cref="HttpDeploymentConfig"/>)
///   --notifications &lt;file&gt; tell reviewers about pending actions (see <see cref="NotificationConfig"/>)
///   --modules &lt;file&gt;      modules enabled by configuration (see <see cref="ModulesConfig"/>)
///   --new-api-key &lt;name&gt;   print a new API key once, with the hash to put in the config, and exit
///   --new-partner &lt;name&gt;   print a new A2A partner id (a UUID, kept for good), with the config to put it in, and exit
///   env OPENTAIL_ADMIN_TOKENS  reviewer tokens, "name:token|review,admin,group:finance;name2:token2|review"
/// </code>
/// Without <c>--http</c> this is exactly <see cref="DeploymentRunner"/>.
/// </summary>
public static class HttpDeploymentRunner
{
    /// <summary>A deployment whose modules all come from <c>--modules</c>: the program names none in code.</summary>
    public static Task<int> RunAsync(string[] args, string name, CancellationToken ct = default) => RunAsync(args, name, _ => { }, ct);

    public static async Task<int> RunAsync(string[] args, string name, Action<OpenTailHostBuilder> addModules, CancellationToken ct = default)
    {
        if (await ModuleWorker.TryRunAsync(args, ct: ct) is { } workerExit) return workerExit;
        if (await PolicyCli.TryRunAsync(args, name, addModules, Console.Out) is { } policyExit) return policyExit;

        var newKey = ValueOf(args, "--new-api-key");
        if (newKey is not null) return PrintNewKey(newKey);

        var newPartner = ValueOf(args, "--new-partner");
        if (newPartner is not null) return PrintNewPartner(newPartner);

        var configPath = ValueOf(args, "--http");
        if (configPath is null) return await DeploymentRunner.RunAsync(args, name, addModules, ct);

        var policyPath = ValueOf(args, "--policy");
        var dbPath = ValueOf(args, "--db") ?? "opentail-host.db";
        if (policyPath is null)
        {
            await Console.Error.WriteLineAsync($"Usage: {name} --policy <file> --http <config.json> [--db <file>]");
            return 64;
        }

        OpenTailHost host;
        HostHttpOptions options;
        Notifier? notifier = null;
        try
        {
            options = HttpDeploymentConfig.Load(configPath).ToOptions(Environment.GetEnvironmentVariable(DeploymentRunner.TokensVariable));
            if (options.Teams is { } teams) options.TeamsBot = new Teams.TeamsBot(teams, dbPath, options.PublicBaseUrl);
            var builder = new OpenTailHostBuilder().WithName(name).UsePolicy(PolicyConfig.Load(policyPath)).UseSqlite(dbPath).UseHttpSignIn()
                .StartedBy(OpenTail.Host.Admin.DeploymentRunner.StartedBy(args));
            await ModulesCli.ApplyAsync(builder, args, ModuleLoadMode.Run, Console.Error, ct);
            if (args.Contains("--shadow"))
            {
                // A shadow instance must never reach the people who review the real one.
                if (ValueOf(args, "--notifications") is not null || options.Teams is not null)
                    throw new ArgumentException("--shadow cannot be used with notifications or Teams: a shadow instance must not reach reviewers.");
                builder.UseShadow();
            }
            if (ValueOf(args, "--notifications") is { } notificationsPath)
            {
                notifier = Notifier.Create(NotificationConfig.Load(notificationsPath), dbPath, options.PublicBaseUrl,
                    botChannel: options.TeamsBot is { } bot ? bot.Channel : null);
                builder.UseEventSink(notifier);
            }
            addModules(builder);
            host = builder.Build();
            foreach (var line in ModulesCli.Describe(host)) await Console.Error.WriteLineAsync(line);
            foreach (var w in host.StartupWarnings.Concat(options.CheckProfiles(host.Policy.Config).Warnings))
                await Console.Error.WriteLineAsync("warning: " + w);
            notifier?.Attach(host.Store, t => host.Catalog.TryGet(t, out var reg) ? reg.Descriptor.Risk : null, host.Audit);
            if (host.Audit is SqliteAuditSink chained)
                notifier?.AttachAuditCheckpoint(() => chained.Head()?.ToString()).AttachAuditStream(seq => [.. chained.ReadChainAfter(seq)
                    .Select(e => new AuditStreamEntry(e.Seq, e.At, e.Event, e.ActionId, e.Tool, e.Actor, e.Detail, e.PrevHash, e.Hash))]);
            if (notifier is not null) host.Health.Add("notifications", () => notifier.DeliveryFailingRecently() ? HealthState.Degraded : HealthState.Ok);
            // Wake-up by URL (work design, step 3): ids only, to the agent platform's webhook.
            if (notifier is not null) host.Work.Ready += item => notifier.EnqueueWorkReady(item.Id, item.For, item.Kind.ToString().ToLowerInvariant());
            options.TeamsBot?.Attach(host);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or JsonException)
        {
            await Console.Error.WriteLineAsync($"Cannot start: {ex.Message}");
            return 2;
        }

        RunningHostHttp server;
        try { server = await HostHttp.StartAsync(host, options, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            await Console.Error.WriteLineAsync($"Cannot start: {ex.Message}");
            return 2;
        }

        await Console.Error.WriteLineAsync("Listening on " + string.Join(", ", server.Addresses));
        using var notifierCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task notifierTask = Task.CompletedTask;
        if (notifier is not null)
        {
            notifier.Error += m => Console.Error.WriteLine(m);
            notifierTask = notifier.RunAsync(notifierCts.Token);
            await Console.Error.WriteLineAsync("Notifications on.");
        }
        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.TrySetResult(); });
        using var reg = ct.Register(() => stop.TrySetResult());
        await stop.Task;
        await notifierCts.CancelAsync();
        await notifierTask;
        if (notifier is not null)
        {
            // A last checkpoint at shutdown, so the newest entries are covered too; best effort, with a short deadline.
            notifier.EnqueueAuditCheckpoint();
            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await notifier.DispatchOnceAsync(last.Token); } catch (OperationCanceledException) { }
        }
        await server.DisposeAsync();
        notifier?.Dispose();
        return 0;
    }

    private static int PrintNewKey(string name)
    {
        var (key, hash) = ApiKeys.Generate();
        Console.WriteLine("API key for '" + name + "' (shown once; give it to the agent, do not store it here):");
        Console.WriteLine("  " + key);
        Console.WriteLine();
        Console.WriteLine("Add this to \"ApiKeys\" in the HTTP config (only the hash is kept):");
        // The line stays valid as printed (scripts paste it); the profile is a separate step.
        Console.WriteLine("  { \"Name\": \"" + name + "\", \"Hash\": \"" + hash + "\" }");
        Console.WriteLine();
        Console.WriteLine("Then add \"Profile\": \"<name>\" to it, naming an entry of the policy's \"Agents\": the tools this key may call.");
        Console.WriteLine("Without a profile the key may call every tool the policy allows (a warning now, refused in a later release).");
        return 0;
    }

    /// <summary>A new A2A partner: a UUID the operator keeps for good, and a display name for people (it may change later).</summary>
    internal static A2aPartner NewPartner(string displayName) => new(Guid.NewGuid().ToString("D"), displayName.Trim());

    private static int PrintNewPartner(string displayName)
    {
        if (displayName.Trim().Length == 0)
        {
            Console.Error.WriteLine("Usage: --new-partner <displayName>");
            return 64;
        }
        var partner = NewPartner(displayName);
        Console.WriteLine("A2A partner '" + partner.DisplayName + "' (its id never changes; do not reuse it for another partner):");
        Console.WriteLine("  " + JsonSerializer.Serialize(partner));
        Console.WriteLine();
        Console.WriteLine("Add it to the \"Partners\" list of \"A2A\" in the HTTP config, and set \"Enabled\": true there when A2A is wanted.");
        Console.WriteLine("Each of the partner's API keys (--new-api-key) then gets \"Scopes\": [ \"a2a:call\" ], \"Profile\": \"<name>\" and \"Partner\": \"" + partner.Id + "\".");
        return 0;
    }

    private static string? ValueOf(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>The JSON file behind <c>--http</c>. Secrets do not belong here: API keys are hashes, reviewer tokens come from the environment.</summary>
public sealed class HttpDeploymentConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public List<string> Urls { get; init; } = ["http://127.0.0.1:5600"];
    public string? PublicBaseUrl { get; init; }
    public bool TrustProxy { get; init; }
    public JwtConfig? Jwt { get; init; }
    public List<ApiKeyEntry> ApiKeys { get; init; } = [];
    public List<string> AllowedOrigins { get; init; } = [];
    public int RateLimitPerMinute { get; init; } = 120;
    public int MaxBodyBytes { get; init; } = 1024 * 1024;
    public bool RecordTelemetry { get; init; } = true;

    public sealed class JwtConfig
    {
        public string? Authority { get; init; }
        public string? AuthorizationServer { get; init; }
        public string Audience { get; init; } = "";
        public string RequiredScope { get; init; } = Roles.Tools;
        public string ReviewScope { get; init; } = Roles.Review;
        public string AdminScope { get; init; } = Roles.Admin;
        public string RoleClaim { get; init; } = "roles";
        public string? PageClientId { get; init; }
        public string? PageScope { get; init; }
    }

    /// <summary>The Teams bot (cards with Approve, Approve with changes, Reject), see <see cref="Teams.TeamsBotOptions"/>.</summary>
    public Teams.TeamsBotOptions? Teams { get; init; }

    /// <summary>The A2A section (phase 0: partners and startup checks; no endpoint is served yet). Disabled by default.</summary>
    public A2aOptions? A2A { get; init; }

    public static HttpDeploymentConfig Load(string path) =>
        JsonSerializer.Deserialize<HttpDeploymentConfig>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException("The HTTP config is empty.");

    public HostHttpOptions ToOptions(string? adminTokens)
    {
        var tokens = DeploymentRunner.ParseTokens(adminTokens);
        var roles = DeploymentRunner.ParseTokenRoles(adminTokens);
        return new HostHttpOptions
        {
            Urls = Urls,
            PublicBaseUrl = PublicBaseUrl,
            TrustProxy = TrustProxy,
            Jwt = Jwt is null ? null : new JwtSignInOptions
            {
                Authority = Jwt.Authority,
                AuthorizationServer = Jwt.AuthorizationServer,
                Audience = Jwt.Audience,
                RequiredScope = Jwt.RequiredScope,
                ReviewScope = Jwt.ReviewScope,
                AdminScope = Jwt.AdminScope,
                RoleClaim = Jwt.RoleClaim,
                PageClientId = Jwt.PageClientId,
                PageScope = Jwt.PageScope,
            },
            Teams = Teams,
            A2A = A2A,
            ApiKeys = ApiKeys,
            Admin = tokens.Count > 0 ? new AdminOptions { Tokens = tokens, TokenRoles = roles } : null,
            AllowedOrigins = AllowedOrigins,
            RateLimitPerMinute = RateLimitPerMinute,
            MaxBodyBytes = MaxBodyBytes,
            RecordTelemetry = RecordTelemetry,
        };
    }
}
