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
/// The A2A front door (decision D2): the JSON-RPC endpoint at <c>/a2a</c>, the <c>a2a.submit</c> module, and the startup checks of the A2A
/// skills. Served only when the HTTP config's <c>A2A.Enabled</c> is true (decision D3); otherwise nothing is mapped and no tool is registered.
/// The program that runs the deployment passes this to <c>HttpDeploymentRunner</c>; the HTTP project does not reference it.
/// See docs/design/a2a.md and docs/briefs/a2a-sdk-facts.md.
/// </summary>
public sealed class A2AFrontDoor : IHttpDeploymentExtension
{
    /// <summary>The route of the JSON-RPC endpoint.</summary>
    public const string Path = "/a2a";

    public void AddModules(OpenTailHostBuilder builder, HostHttpOptions options)
    {
        if (options.A2A is not { Enabled: true } config) return;
        builder.AddModule(new A2ASubmitModule(config));
    }

    public IReadOnlyList<string> Check(OpenTailHost host, HostHttpOptions options)
    {
        var errors = new List<string>();
        if (options.A2A is not { Enabled: true } config) return errors;
        var policy = host.Policy.Config;
        if (!policy.Tools.ContainsKey(A2ASubmitModule.Tool))
            errors.Add($"A2A is enabled, so the policy needs an entry for \"{A2ASubmitModule.Tool}\": its mode decides whether a person reviews each partner request.");
        if (config.Skills.Count == 0)
            errors.Add("A2A is enabled with no skills: add at least one under \"Skills\" in the A2A section.");
        foreach (var (id, skill) in config.Skills)
            if (!policy.Agents.ContainsKey(skill.Inbox))
                errors.Add($"A2A skill '{id}' names inbox profile '{skill.Inbox}', which the policy's \"Agents\" does not define.");
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
        var handler = new A2ARequestHandler(host, config, app.ServiceProvider.GetRequiredService<IHttpContextAccessor>(),
            new A2ATaskTable(store.DatabasePath, store.Synchronous));
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
