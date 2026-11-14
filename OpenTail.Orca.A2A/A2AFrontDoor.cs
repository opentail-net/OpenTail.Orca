using System.Security.Cryptography;
using System.Text.Json;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenTail.Orca.Admin;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.A2A;

/// <summary>
/// The A2A front door (decision D2): the JSON-RPC endpoint at <c>/a2a</c>, the <c>a2a.submit</c> and <c>a2a.reply</c> tools, and the startup
/// checks of the A2A skills. Served only when the HTTP config's <c>A2A.Enabled</c> is true (decision D3); otherwise nothing is mapped and no
/// tool is registered. The program that runs the deployment passes this to <c>HttpDeploymentRunner</c>; the HTTP project does not reference it.
/// See docs/design/a2a.md and docs/briefs/a2a-sdk-facts.md.
/// </summary>
public sealed class A2AFrontDoor : IHttpDeploymentExtension
{
    /// <summary>The route of the JSON-RPC endpoint.</summary>
    public const string Path = "/a2a";

    /// <summary>The route of the agent card (the well-known location the SDK's own card mapping used).</summary>
    private const string CardPath = "/.well-known/agent-card.json";

    // The tools are registered before the host exists, so their bodies reach the work through this, bound in MapEndpoints.
    private A2AWork? _work;

    /// <summary>The delay between polls of a blocking send. Tests supply one that does not wait, so they need no real seconds; null means a
    /// real delay.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? PollDelay { get; init; }

    /// <summary>How long a client may reuse the agent card (the cache header carries it).</summary>
    private static readonly TimeSpan CardMaxAge = TimeSpan.FromHours(1);

    /// <summary>The window in which one partner's card read is audited once.</summary>
    private static readonly TimeSpan CardAuditInterval = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTimeOffset> _cardServed = new(StringComparer.Ordinal);
    private readonly Lock _cardGate = new();

    /// <summary>True when this partner's card read should be audited: its first in the window. In memory, so a restart starts a new window.</summary>
    private bool CardAuditDue(string partner)
    {
        lock (_cardGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cardServed.TryGetValue(partner, out var last) && now - last < CardAuditInterval) return false;
            _cardServed[partner] = now;
            return true;
        }
    }

    public void AddModules(OpenTailHostBuilder builder, HostHttpOptions options)
    {
        if (options.A2A is not { Enabled: true } config) return;
        builder.AddModule(new A2ASubmitModule(config, () => _work ?? throw new InvalidOperationException("A2A is not bound to a host yet.")));
    }

    public IReadOnlyList<string> Check(OpenTailHost host, HostHttpOptions options)
    {
        var errors = new List<string>();
        if (options.A2A is not { Enabled: true } config) return errors;
        var policy = host.Policy.Config;
        if (!policy.Work.Enabled)
            errors.Add("A2A is enabled, so Work.Enabled must be true: an approved partner request becomes a work item for an inbox profile.");
        if (policy.Tools.TryGetValue(A2ASubmitModule.Tool, out var submit) && submit.Reviewer != ReviewerKind.Human)
            errors.Add($"\"{A2ASubmitModule.Tool}\" must have Reviewer Human: a person decides each partner request.");
        // B1 (G3): a rule can name its own reviewer, so a submit rule must leave a person deciding each partner request. An Execute rule stays allowed.
        if (policy.Tools.TryGetValue(A2ASubmitModule.Tool, out var submitTool))
            foreach (var rule in submitTool.Rules ?? [])
                if (rule.Reviewer is { } ruleKind && ruleKind != ReviewerKind.Human)
                    errors.Add($"\"{A2ASubmitModule.Tool}\" rule \"{rule.Name}\" names reviewer {ruleKind}: a person must decide each partner request.");
        if (!policy.Tools.ContainsKey(A2ASubmitModule.Tool))
            errors.Add($"A2A is enabled, so the policy needs an entry for \"{A2ASubmitModule.Tool}\": its mode decides whether a person reviews each partner request.");
        if (policy.Tools.TryGetValue(A2ASubmitModule.ReplyTool, out var reply))
        {
            if (reply.Mode == ToolMode.Execute)
                errors.Add($"\"{A2ASubmitModule.ReplyTool}\" is Execute: a reply leaves the Host for a partner, which is an exit, so it must be Queue or Deny.");
            if (reply.Reviewer != ReviewerKind.Human)
                errors.Add($"\"{A2ASubmitModule.ReplyTool}\" must have Reviewer Human: a person decides each reply.");
            if (reply.AllowEdits)
                errors.Add($"\"{A2ASubmitModule.ReplyTool}\" must have AllowEdits false: the text a partner receives is the text that was reviewed.");
            // A rule can relax a Queue tool to Execute, and can name its own reviewer: both must leave a person deciding every reply.
            foreach (var rule in reply.Rules ?? [])
            {
                if (rule.Mode == ToolMode.Execute)
                    errors.Add($"\"{A2ASubmitModule.ReplyTool}\" has rule \"{rule.Name}\" in mode Execute: a reply must never run without a person.");
                if (rule.Reviewer is { } kind && kind != ReviewerKind.Human)
                    errors.Add($"\"{A2ASubmitModule.ReplyTool}\" rule \"{rule.Name}\" names reviewer {kind}: a person must decide each reply.");
            }
        }
        else
            errors.Add($"A2A is enabled, so the policy needs an entry for \"{A2ASubmitModule.ReplyTool}\": Queue (a person decides each reply) or Deny.");
        if (config.Skills.Count == 0)
            errors.Add("A2A is enabled with no skills: add at least one under \"Skills\" in the A2A section.");
        foreach (var (id, skill) in config.Skills)
        {
            if (!policy.Agents.ContainsKey(skill.Inbox))
                errors.Add($"A2A skill '{id}' names inbox profile '{skill.Inbox}', which the policy's \"Agents\" does not define.");
            // Batch 13: a skill's Profiles, when set, name profiles that may use it, and each must list a2a.submit to do so.
            if (skill.Profiles is { } profiles)
            {
                if (profiles.Count == 0)
                    errors.Add($"A2A skill '{id}' has an empty \"Profiles\" list: leave it out to let any profile that lists a2a.submit use it, or name the profiles that may.");
                foreach (var profile in profiles)
                    if (!policy.Agents.TryGetValue(profile, out var agent) || !agent.Tools.Contains(A2ASubmitModule.Tool, StringComparer.Ordinal))
                        errors.Add($"A2A skill '{id}' lists profile '{profile}', which the policy's \"Agents\" does not define with \"{A2ASubmitModule.Tool}\".");
            }
        }
        // A3: the card names the address clients reach the Host at. The message is the one HostHttpOptions gives for a missing PublicBaseUrl,
        // extended with the A2A rule (https, or http on loopback), which only A2A needs.
        if (options.PublicBaseUrl is null || !Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var publicUri)
            || (publicUri.Scheme != Uri.UriSchemeHttps && !(publicUri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(publicUri.Host))))
            errors.Add("PublicBaseUrl is required (the address clients reach this server at), and with A2A it must be https, or http on a loopback address: the agent card names it.");
        if (config.BlockingWait < TimeSpan.FromSeconds(1) || config.BlockingWait > TimeSpan.FromMinutes(5))
            errors.Add("A2A BlockingWait must be from 1 second to 5 minutes (the default is 00:00:30).");
        if (config.MaxBlockingWaitsPerPartner is < 1 or > 64)
            errors.Add("A2A MaxBlockingWaitsPerPartner must be from 1 to 64 (the default is 4).");
        foreach (var key in options.ApiKeys.Where(k => k.Scopes?.Contains(Roles.A2aCall) == true))
        {
            if (key.Profile is null || !policy.Agents.TryGetValue(key.Profile, out var agent))
            {
                errors.Add($"API key '{key.Name}' has a2a:call, so it needs a profile that the policy's \"Agents\" defines.");
                continue;
            }
            foreach (var tool in new[] { A2ASubmitModule.Tool, A2ASubmitModule.ReplyTool })
                if (!agent.Tools.Contains(tool, StringComparer.Ordinal))
                    errors.Add($"API key '{key.Name}' has a2a:call, so its profile '{key.Profile}' must list \"{tool}\" in its Tools.");
        }
        return errors;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));

    /// <summary>The operator's <c>tasks</c> command in Teams (batch 05, B2). With A2A off there is no command at all.</summary>
    public IReadOnlyList<ChatCommand> ChatCommands(OpenTailHost host, HostHttpOptions options)
    {
        if (options.A2A is not { Enabled: true } config || host.Store is not SqliteActionStore store) return [];
        return
        [
            new ChatCommand(A2ATasksView.Command,
                "A2A tasks in flight, newest first: the state a partner sees, the age, and what each waits for. No text.",
                ct => A2ATasksView.LinesAsync(host, config, new A2ATaskTable(store.DatabasePath, store.Synchronous), DateTimeOffset.UtcNow, ct)),
        ];
    }

    public IReadOnlyList<string> Warnings(OpenTailHost host, HostHttpOptions options)
    {
        var warnings = new List<string>();
        if (options.A2A is not { Enabled: true }) return warnings;
        if (host.Policy.Config.Tools.TryGetValue(A2ASubmitModule.Tool, out var tool) && tool.Mode == ToolMode.Execute && (tool.Rules is null || tool.Rules.Count == 0))
            warnings.Add($"\"{A2ASubmitModule.Tool}\" runs without a person's review for every partner (Execute, no rule). That is allowed when you trust every partner; no partner request is then reviewed.");
        return warnings;
    }

    public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, OpenTailHost host, HostHttpOptions options)
    {
        if (options.A2A is not { Enabled: true } config) return;
        if (host.Store is not SqliteActionStore store)
            throw new InvalidOperationException("A2A needs the SQLite store; its startup check should have refused this.");
        var tasks = new A2ATaskTable(store.DatabasePath, store.Synchronous);
        var work = new A2AWork(host, config, tasks);
        _work = work;
        host.Work.Completed += work.OnCompleted;
        // A1, A4, A5 and batch 13: the card is not public. It takes the same sign-in and role as /a2a. It lists the skills this caller's profile
        // may use, so it is built per request, with the SDK's JSON options. Its cache header is private (the SDK's default is public), and its
        // ETag is the hash of the body. A partner's read is audited at most once per partner in each 10 minutes.
        app.MapGet(CardPath, async (HttpContext http, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = $"private, max-age={(int)CardMaxAge.TotalSeconds}";
                var user = http.User;
                var profile = user.FindFirst(HostClaims.Profile)?.Value;
                var visible = config.Skills.Where(s => A2ASkillSelection.MayUse(host, s.Value, profile)).Select(s => (s.Key, s.Value));
                var body = JsonSerializer.SerializeToUtf8Bytes(A2ACard.Build(config.Card, options.PublicBaseUrl!, visible), A2AJsonUtilities.DefaultOptions);
                var etag = "\"" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant() + "\"";
                http.Response.Headers.ETag = etag;
                if (user.FindFirst(HostClaims.Partner)?.Value is { } partner && CardAuditDue(partner))
                    await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "a2a.card_served", null, null, A2AWork.SubjectOf(partner), null), ct);
                if (http.Request.Headers.IfNoneMatch.ToString() == etag)
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Bytes(body, "application/json");
            })
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireRole(Roles.A2aCall));

        var pause = PollDelay ?? ((delay, ct) => Task.Delay(delay, ct));
        var handler = new A2ARequestHandler(host, config, app.ServiceProvider.GetRequiredService<IHttpContextAccessor>(), tasks, work, pause);
        app.MapA2A(handler, Path)
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireRole(Roles.A2aCall))
            .Add(endpoint => endpoint.FilterFactories.Add((_, next) => async invocation =>
            {
                // The version gate runs after sign-in and authorisation (the filter sees only authorised requests) and before the SDK.
                var request = invocation.HttpContext.Request;
                var values = request.Headers[A2AVersionGate.Header];
                if (!A2AVersionGate.Accepts(values)) return A2AVersionGate.Refusal();
                // The SDK reads the header as one joined value, so an accepted list of 1.0s is handed to it as a single 1.0.
                if (values.Count != 0) request.Headers[A2AVersionGate.Header] = A2AVersionGate.Served;
                return await next(invocation);
            }));
    }
}
