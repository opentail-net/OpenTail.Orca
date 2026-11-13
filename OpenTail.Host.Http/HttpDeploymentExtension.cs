using Microsoft.AspNetCore.Routing;

namespace OpenTail.Host.Http;

/// <summary>
/// A package that adds to an HTTP deployment: modules, startup checks, warnings and endpoints on the same web app. The endpoints are
/// mapped after the Host's own routes, so the host check, origin check, security headers, sign-in, rate limit and body limit all apply
/// to them. The HTTP project does not reference any extension; the program that runs the deployment passes them to
/// <see cref="HttpDeploymentRunner.RunAsync(string[], string, Action{OpenTailHostBuilder}, CancellationToken, IReadOnlyList{IHttpDeploymentExtension})"/>.
/// </summary>
public interface IHttpDeploymentExtension
{
    /// <summary>Adds the extension's modules before the Host is built. Called once, with the HTTP options (the extension decides whether its
    /// section is enabled).</summary>
    void AddModules(OpenTailHostBuilder builder, HostHttpOptions options);

    /// <summary>Problems that refuse startup. Empty means none. Called with the built Host, before the endpoints are mapped.</summary>
    IReadOnlyList<string> Check(OpenTailHost host, HostHttpOptions options);

    /// <summary>Warnings printed at startup. They do not refuse it.</summary>
    IReadOnlyList<string> Warnings(OpenTailHost host, HostHttpOptions options);

    /// <summary>Maps the extension's endpoints on the web app, after the Host's own routes.</summary>
    void MapEndpoints(IEndpointRouteBuilder app, OpenTailHost host, HostHttpOptions options);
}
