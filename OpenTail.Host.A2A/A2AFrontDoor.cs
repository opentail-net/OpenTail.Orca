using A2A.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenTail.Host.Admin;
using OpenTail.Host.Http;

namespace OpenTail.Host.A2A;

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

    // The tools are registered before the host exists, so their bodies reach the work through this, bound in MapEndpoints.
    private A2AWork? _work;

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
            if (!policy.Agents.ContainsKey(skill.Inbox))
                errors.Add($"A2A skill '{id}' names inbox profile '{skill.Inbox}', which the policy's \"Agents\" does not define.");
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
        var handler = new A2ARequestHandler(host, config, app.ServiceProvider.GetRequiredService<IHttpContextAccessor>(), tasks, work);
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
