using System.Text.Json;

namespace OpenTail.Host;

/// <summary>One call the executor would have run, handed to a sandbox instead.</summary>
/// <param name="Tool">The real tool's descriptor: name, risk and the rest, exactly as the module declared them.</param>
/// <param name="ArgumentsJson">The arguments the action carries (after any reviewer edit).</param>
/// <param name="ActionId">The action being executed.</param>
/// <param name="RunModule">Runs the real module function. Only a shadow sandbox calls it, and only for Read tools.</param>
public sealed record SandboxCall(ToolDescriptor Tool, string ArgumentsJson, string ActionId, Func<CancellationToken, Task<string>> RunModule);

/// <summary>
/// Sandbox mode (<see cref="OpenTailHostBuilder.UseSandbox"/>): the executor hands every tool call here and never calls the module
/// itself. Policy, taint, previews, queueing and audit are the real thing; only what a tool does is replaced. Used by the skills
/// workbench (answers from a scenario's world) and by shadow runs (reads live, records everything else).
/// </summary>
public interface IToolSandbox
{
    /// <summary>The tool's result as JSON.</summary>
    Task<string> InvokeAsync(SandboxCall call, CancellationToken ct);
}

/// <summary>A call a <see cref="RecordingSandbox"/> recorded instead of running.</summary>
public sealed record RecordedCall(string ToolName, ToolRisk Risk, string ArgumentsJson, string ActionId, DateTimeOffset At);

/// <summary>
/// The sandbox both uses need: every tool above Read is recorded and answered with <c>{"recorded":true}</c>, never run; a Read
/// tool is answered by <paramref name="read"/>. A workbench passes a function that answers from a scenario's world; a shadow Host
/// passes <see cref="ReadLive"/>, which runs the real module, for Read tools only.
/// </summary>
public sealed class RecordingSandbox(Func<SandboxCall, CancellationToken, Task<string>> read, TimeProvider? clock = null) : IToolSandbox
{
    private readonly List<RecordedCall> _recorded = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Shadow mode's read: the real module answers, with the instance's own credentials.</summary>
    public static Task<string> ReadLive(SandboxCall call, CancellationToken ct) => call.RunModule(ct);

    public static string RecordedResult { get; } = JsonSerializer.Serialize(new { recorded = true, note = "Sandbox: recorded, not run." });

    /// <summary>Everything recorded so far, oldest first.</summary>
    public IReadOnlyList<RecordedCall> Recorded { get { lock (_recorded) return [.. _recorded]; } }

    public Task<string> InvokeAsync(SandboxCall call, CancellationToken ct)
    {
        if (call.Tool.Risk == ToolRisk.Read) return read(call, ct);
        lock (_recorded) _recorded.Add(new RecordedCall(call.Tool.Name, call.Tool.Risk, call.ArgumentsJson, call.ActionId, _clock.GetUtcNow()));
        return Task.FromResult(RecordedResult);
    }
}
