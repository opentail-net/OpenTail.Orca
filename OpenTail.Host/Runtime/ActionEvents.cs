using System.Security.Claims;

namespace OpenTail.Host;

/// <summary>
/// Publishes <see cref="ActionEvent"/>s to the configured sink. A failing sink never breaks the action path: the failure
/// is audited (type name only) and the action carries on.
/// </summary>
public sealed class ActionEvents(IActionEventSink? sink, IAuditSink audit, ToolCatalog catalog, TimeProvider? clock = null, NoticeService? notices = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public bool Enabled => sink is not null;

    public async Task PublishAsync(ActionEventKind kind, ActionRecord r, string? detail, CancellationToken ct = default)
    {
        // Remember the decision for the AI first: that must not depend on whether anyone listens for events.
        try { notices?.OnEvent(kind, r); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        if (sink is null) return;
        ToolRisk? risk = catalog.TryGet(r.ToolName, out var reg) ? reg.Descriptor.Risk : null;
        var evt = new ActionEvent(kind, r.Id, r.ToolName, r.ModuleId, risk, r.Requester.Id, r.Status, r.Tainted, _clock.GetUtcNow(), detail);
        try
        {
            await sink.PublishAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "event_sink_error", r.Id, r.ToolName, "system", ex.GetType().Name), ct);
            }
            catch (Exception inner) when (inner is not OperationCanceledException) { /* the audit sink is also down: nothing safe left to do */ }
        }
    }
}

/// <summary>
/// The identity of whoever is making the MCP call, handed from the protocol handler to the tool function. It is read
/// <b>once</b>, at the edge, and copied into the <see cref="ActionRequest"/>; the executor, recovery, sweep and
/// dispatchers never read it (an ambient value would leak into background work started from a request).
/// </summary>
public static class CallerContext
{
    private static readonly AsyncLocal<CallerIdentity?> Value = new();

    /// <summary>The caller of the current MCP request; the local operator when there is none (stdio, tests).</summary>
    public static CallerIdentity Current => Value.Value ?? CallerIdentity.Local;

    public static IDisposable Use(CallerIdentity caller)
    {
        var previous = Value.Value;
        Value.Value = caller;
        return new Restore(previous);
    }

    private sealed class Restore(CallerIdentity? previous) : IDisposable
    {
        public void Dispose() => Value.Value = previous;
    }
}

/// <summary>Maps the authenticated principal of an MCP request (HTTP) to a caller. The default knows only the local operator.</summary>
public delegate CallerIdentity CallerResolver(ClaimsPrincipal? principal);
