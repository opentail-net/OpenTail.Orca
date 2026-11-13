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

    /// <summary>Read-only commands a reviewer may type in a personal Teams chat (batch 05, B2). They never change state, and what they answer
    /// is plain text, which the bot escapes. Empty by default; an extension whose section is off returns none, so its command does not exist.</summary>
    IReadOnlyList<ChatCommand> ChatCommands(OpenTailHost host, HostHttpOptions options) => [];
}

/// <summary>A typed chat command: its exact word, one line of help for <c>help</c>, and the lines it answers with.</summary>
/// <param name="Name">The word a reviewer types, exactly (compared without case).</param>
/// <param name="Help">One line, shown by <c>help</c>.</param>
/// <param name="Lines">Answers the command with plain lines. Each line is escaped before it is posted.</param>
public sealed record ChatCommand(string Name, string Help, Func<CancellationToken, Task<IReadOnlyList<string>>> Lines);
