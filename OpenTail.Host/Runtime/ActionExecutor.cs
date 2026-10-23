using System.Text.Json;

namespace OpenTail.Host;

/// <summary>Claims and runs approved actions, applying input/output guardrails and recording the outcome.</summary>
public sealed class ActionExecutor(
    ToolCatalog catalog,
    IActionStore store,
    IAuditSink audit,
    SessionRegistry sessions,
    IReadOnlyList<IToolInputGuardrail>? inputGuardrails = null,
    IReadOnlyList<IToolOutputGuardrail>? outputGuardrails = null,
    TimeProvider? clock = null,
    TimeSpan? lease = null,
    ActionEvents? events = null,
    IToolSandbox? sandbox = null,
    CapacityLimits? capacity = null,
    IdentityLevels? levels = null,
    IReadOnlyList<string>? decoys = null,
    DataLabels? labels = null,
    FailuresConfig? failures = null,
    Circuits? circuits = null) : IActionExecutor
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly FailuresConfig _failures = failures ?? new FailuresConfig();

    /// <summary>Paused routes (failures design, step 2). Null when the Host was built without them.</summary>
    public Circuits? Circuits => circuits;

    /// <summary>How long an Executing claim is valid. A tool that legitimately runs longer than this
    /// must be split or its lease extended (not yet supported): recovery would treat it as interrupted.</summary>
    public TimeSpan LeaseDuration { get; } = lease ?? TimeSpan.FromMinutes(5);

    /// <summary>Claims an Approved action (compare-and-set, so only one caller wins) and runs it.</summary>
    public async Task<ActionRecord> ExecuteAsync(string actionId, CancellationToken ct = default)
    {
        var claimed = await store.TransitionAsync(actionId, ActionStatus.Approved, ActionStatus.Executing,
            r => r with { LeaseUntil = _clock.GetUtcNow() + LeaseDuration }, ct);
        if (claimed is null)
            return await store.GetAsync(actionId, ct) ?? throw new KeyNotFoundException(actionId);
        return await RunClaimedAsync(claimed, ct);
    }

    /// <summary>Runs an action already in Executing (execute-mode creation, or just claimed).</summary>
    internal async Task<ActionRecord> RunClaimedAsync(ActionRecord record, CancellationToken ct)
    {
        await Audit("execution_started", record, null, ct);
        using var activity = HostTelemetry.Source.StartActivity("action.execute");
        activity?.SetTag("opentail.tool", record.ToolName);
        activity?.SetTag("opentail.module", record.ModuleId);
        activity?.SetTag("opentail.tainted", record.Tainted);
        activity?.SetTag("opentail.action_id", record.Id);
        var started = _clock.GetTimestamp();

        // Checked on every path that runs an action (execute, automatic retry, recovery after a restart), not only at submission
        // and approval: suspending a compromised identity must also stop work approved before it.
        if (levels?.Effective(record.Requester.Id) == IdentityLevel.Suspended)
            return await Finish(record, null, new ActionError("suspended",
                "The requester is suspended: nothing it asked for runs until an operator clears it. A reviewer can retry it then.", true), ct, started);

        if (record.PolicySnapshot?.RateLimit is { } limit && await ExceedsAsync(record, limit, ct) is { } which)
            return await Finish(record, null,
                new ActionError("rate_limited", $"Limit of {which} reached for this tool. A reviewer can retry later.", true), ct, started);

        if (!catalog.TryGet(record.ToolName, out var tool))
            return await Finish(record, null, new ActionError("unknown_tool", "Tool is not loaded.", false), ct, started);

        // A paused route: the call is held, not run, and tried again once a probe call succeeds. It never reached the tool, so trying
        // again is safe whatever the tool is.
        if (circuits?.Gate(record.ToolName) is { Allowed: false, Paused: { } paused })
            return await Finish(record, null, new ActionError("circuit_open",
                $"Calls to {record.ToolName} are paused after repeated {FailureKinds.Name(paused.Kind)} failures; this one is held and tried again " +
                $"after {paused.ProbeAt:u}.", true) { Kind = paused.Kind }, ct, started, retryAt: paused.ProbeAt);

        // Capacity: a short wait when the tool (or its module) is busy, a retryable refusal when the wait would be too long.
        if (capacity?.Take(record.ToolName) is { } room && (room.Wait > TimeSpan.Zero || room.RetryAfter is not null))
        {
            if (room.EpisodeStarted) await Audit("backoff", record, $"capacity of {capacity.LimitFor(record.ToolName)?.Key} reached", ct);
            HostTelemetry.Backoff.Add(1, new KeyValuePair<string, object?>("tool", record.ToolName),
                new KeyValuePair<string, object?>("outcome", room.RetryAfter is null ? "delayed" : "refused"));
            if (room.RetryAfter is { } after)
                return await Finish(record, null, new ActionError("slow_down",
                    $"This tool is busy. Retry after {Math.Ceiling(after.TotalSeconds)} s.", true), ct, started);
            await Task.Delay(room.Wait, ct);
        }

        // Taint on the attempt, not the outcome: a failed read may still have exposed untrusted content.
        if (tool.Descriptor.ReadsUntrusted)
        {
            if (record.SessionId is { } sid) sessions.Taint(sid);
            else sessions.TaintSubject(record.Requester.Id);
        }

        var args = record.EffectiveArgumentsJson;
        try
        {
            if (inputGuardrails is { Count: > 0 })
                foreach (var g in inputGuardrails)
                {
                    var r = await g.CheckInputAsync(record.ToolName, args, ct);
                    if (!r.Passed)
                        return await Finish(record, null,
                            new ActionError("guardrail_blocked", Redaction.Scrub(r.Violation ?? "Input blocked."), false), ct, started);
                }

            var arguments = ToArguments(args);
            arguments.Context ??= new Dictionary<object, object?>();
            arguments.Context[HostContract.IdempotencyContextKey] = record.IdempotencyKey ?? record.Id;
            arguments.Context[HostContract.ActionIdContextKey] = record.Id;
            arguments.Context[HostContract.RequesterContextKey] = record.Requester.Id;
            arguments.Context[WorkModule.ProfileContextKey] = record.Profile;
            arguments.Context[WorkModule.SessionContextKey] = record.SessionId;
            // Sandbox mode: the sandbox answers and the module never runs, unless the sandbox itself asks for it (shadow reads).
            var result = sandbox is null
                ? ToJson(await tool.Function.InvokeAsync(arguments, ct))
                : await sandbox.InvokeAsync(new SandboxCall(tool.Descriptor, args, record.Id,
                    async c => ToJson(await tool.Function.InvokeAsync(arguments, c))), ct);

            if (outputGuardrails is { Count: > 0 })
                foreach (var g in outputGuardrails)
                {
                    var r = await g.CheckOutputAsync(record.ToolName, result, ct);
                    if (!r.Passed)
                        return await Finish(record, null,
                            new ActionError("guardrail_blocked", Redaction.Scrub(r.Violation ?? "Output blocked."), false), ct, started);
                    if (r.RewrittenValue is not null) result = r.RewrittenValue;
                }

            // Usage: what the result touched, for the daily rollup (written with the transition) and telemetry.
            var usage = store as IUsageStore;
            var (stats, entityHashes) = ResultMeasure.Measure(tool.Descriptor.Counts, args, result, usage is null || tool.Descriptor.Counts?.EntityKeys is null ? null : usage.EntitySalt);
            var done = await Finish(record, result, null, ct, started, stats);
            labels?.RememberValues(done.SessionId, done.Requester.Id, tool.Descriptor, result);
            // A decoy record in what came back: no real task needs one.
            if (levels is not null && decoys is { Count: > 0 } && IdentityLevels.ContainsDecoy(decoys, result))
                await levels.DecoyReadAsync(done.Requester.Id, done.ToolName, ct);
            if (usage is not null && entityHashes.Count > 0)
                usage.RecordEntities(DateOnly.FromDateTime(done.UpdatedAt.UtcDateTime), done.Requester.Id, done.ToolName, [.. entityHashes]);
            if (tool.Descriptor.AuditResult is { } ra && ResultField(result, ra.Field) is { } detail)
                await Audit(ra.Event, done, detail, ct);
            return done;
        }
        catch (ToolTransientException ex)
        {
            var after = ex.RetryAfter is { } ra ? $" Try again after {Math.Ceiling(ra.TotalSeconds)} s." : "";
            return await Finish(record, null, new ActionError("tool_unavailable", Redaction.Scrub(ex.Message) + after, true)
                { RetryAfter = ex.RetryAfter }, ct, started);
        }
        catch (ToolFailureException ex)
        {
            var after = ex.RetryAfter is { } ra ? $" Try again after {Math.Ceiling(ra.TotalSeconds)} s." : "";
            return await Finish(record, null, new ActionError(ex.Code ?? FailureCode(ex.Kind), Redaction.Scrub(ex.Message) + after, ReviewerMayRetry(ex.Kind))
                { Kind = ex.Kind, RetryAfter = ex.RetryAfter }, ct, started);
        }
        catch (ToolGuardrailTripwireException ex)
        {
            return await Finish(record, null, new ActionError("guardrail_tripwire", Redaction.Scrub(ex.Reason), false), ct, started);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Outcome unknown: leave Executing so restart handling (Phase 3) can decide.
            throw;
        }
        catch (Exception ex)
        {
            // Message only, no stack or arguments: errors flow back to the model and into logs. Unclassified is a bug, except arguments
            // the tool refused while running, which are the request's problem.
            return await Finish(record, null, new ActionError("tool_exception", Redaction.Scrub(ex.Message), false)
                { Kind = ex is ArgumentException ? ToolFailureKind.Contract : ToolFailureKind.Bug }, ct, started);
        }
    }

    /// <summary>
    /// Crash recovery, run at startup and periodically.
    /// (1) Approved but never started: run it (exactly once; the claim is a compare-and-set).
    /// (2) Executing with a lapsed lease: the process died mid-run. The outcome is unknown, so the action becomes
    ///     Failed(retryable, "interrupted"). If the tool declares IdempotentByKey it is retried with the same key;
    ///     otherwise it waits for a reviewer. A non-idempotent external effect is never silently re-run.
    /// </summary>
    public async Task<RecoveryReport> RecoverAsync(CancellationToken ct = default)
    {
        int started = 0, retried = 0, interrupted = 0;
        var now = _clock.GetUtcNow();

        foreach (var r in await store.QueryAsync(new ActionQuery { Status = ActionStatus.Approved, Limit = int.MaxValue }, ct))
        {
            var claimed = await store.TransitionAsync(r.Id, ActionStatus.Approved, ActionStatus.Executing,
                x => x with { LeaseUntil = now + LeaseDuration }, ct);
            if (claimed is null) continue;
            started++;
            await RunClaimedAsync(claimed, ct);
        }

        foreach (var r in await store.QueryAsync(new ActionQuery { Status = ActionStatus.Executing, Limit = int.MaxValue }, ct))
        {
            if (r.LeaseUntil is { } until && until > now) continue;

            var failed = await store.TransitionAsync(r.Id, ActionStatus.Executing, ActionStatus.Failed,
                x => x with
                {
                    LeaseUntil = null,
                    Error = new ActionError("interrupted", "The Host stopped while this action was running; the outcome is unknown.", true),
                }, ct);
            if (failed is null) continue;
            interrupted++;
            await Audit("interrupted", failed, null, ct);

            if (catalog.TryGet(r.ToolName, out var tool) && tool.Descriptor.IdempotentByKey)
            {
                var again = await store.TransitionAsync(r.Id, ActionStatus.Failed, ActionStatus.Approved,
                    x => x with
                    {
                        Attempts = x.Attempts + 1,
                        Error = null,
                        Reviews = [.. x.Reviews, new ActionReview("system", ReviewDecision.Retry,
                            "Tool is idempotent by key; retried after interruption.", now)],
                    }, ct);
                if (again is null) continue;
                var claimed = await store.TransitionAsync(r.Id, ActionStatus.Approved, ActionStatus.Executing,
                    x => x with { LeaseUntil = now + LeaseDuration }, ct);
                if (claimed is null) continue;
                retried++;
                await RunClaimedAsync(claimed, ct);
            }
        }

        return new RecoveryReport(started, retried, interrupted);
    }

    /// <summary>
    /// The maintenance sweep's automatic retries: failed actions whose <see cref="ActionRecord.RetryAt"/> has come. A transient failure
    /// runs again (and counts as an attempt); a call held on a paused route runs once the route is open again, a few per sweep, and
    /// does not count, since it never reached the tool.
    /// </summary>
    public async Task<int> RetryDueAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var released = new Dictionary<string, int>(StringComparer.Ordinal);
        var ran = 0;
        foreach (var r in (await store.QueryAsync(new ActionQuery { Status = ActionStatus.Failed, Limit = int.MaxValue }, ct))
                     .Where(r => r.RetryAt is { } at && at <= now).OrderBy(r => r.RetryAt))
        {
            var held = r.Error?.Code == "circuit_open";
            if (held)
            {
                // Wait for the route: held calls go only once it is closed (or the probe slot is free), and a few at a time.
                if (circuits is not null && !circuits.IsClosed(r.ToolName) && circuits.ProbeAt(r.ToolName) is { } probe && probe > now) continue;
                var n = released.GetValueOrDefault(r.ToolName);
                if (n >= _failures.ReleasePerSweep) continue;
                released[r.ToolName] = n + 1;
            }
            var again = await store.TransitionAsync(r.Id, ActionStatus.Failed, ActionStatus.Approved, x => x with
            {
                Attempts = held ? x.Attempts : x.Attempts + 1,
                Error = null,
                RetryAt = null,
                Reviews = [.. x.Reviews, new ActionReview("system", ReviewDecision.Retry,
                    held ? "Held while its route was paused; released." : "Transient failure of a tool idempotent by key; retried automatically.", now)],
            }, ct);
            if (again is null) continue;
            await Audit("retry_automatic", again, held ? "released from a paused route" : $"attempt {again.Attempts}", ct);
            var claimed = await store.TransitionAsync(r.Id, ActionStatus.Approved, ActionStatus.Executing,
                x => x with { LeaseUntil = now + LeaseDuration }, ct);
            if (claimed is null) continue;
            await RunClaimedAsync(claimed, ct);
            ran++;
        }
        return ran;
    }

    /// <summary>The code a classified failure gets when the tool named none.</summary>
    internal static string FailureCode(ToolFailureKind kind) => kind switch
    {
        ToolFailureKind.Transient => "tool_unavailable",
        ToolFailureKind.Environment => "environment",
        ToolFailureKind.Contract => "contract",
        ToolFailureKind.Auth => "auth",
        ToolFailureKind.Policy => "refused",
        ToolFailureKind.UnknownOutcome => "unknown_outcome",
        _ => "tool_exception",
    };

    /// <summary>Whether a reviewer may retry a failure of this kind once the cause is fixed. A bad request, a refusal by policy and a bug
    /// cannot succeed by running the same call again. Automatic retries are narrower: transient failures only.</summary>
    internal static bool ReviewerMayRetry(ToolFailureKind kind) =>
        kind is ToolFailureKind.Transient or ToolFailureKind.Environment or ToolFailureKind.Auth or ToolFailureKind.UnknownOutcome;

    private async Task<ActionRecord> Finish(ActionRecord record, string? resultJson, ActionError? error, CancellationToken ct, long? startedAt = null,
        ResultStats? stats = null, DateTimeOffset? retryAt = null)
    {
        // Every failure leaves with a kind (failures design, step 1); the Host's own codes get theirs from the code.
        if (error is { Kind: null }) error = error with { Kind = error.EffectiveKind };

        // Failures design, step 2: only a transient failure of a tool idempotent by key is retried by the Host, a few times, backing off.
        if (error is { Kind: ToolFailureKind.Transient } && error.Code != "circuit_open" && retryAt is null && record.Attempts <= _failures.AutoRetries
            && catalog.TryGet(record.ToolName, out var reg) && reg.Descriptor.IdempotentByKey && circuits is not null)
            retryAt = _clock.GetUtcNow() + circuits.Backoff(record.Attempts, error.RetryAfter);

        // The route's health: the probe that succeeds reopens it; repeated failures of one kind pause it (once per episode in the trail).
        if (circuits is not null && error?.Code != "circuit_open")
        {
            if (error is null)
            {
                if (circuits.Succeeded(record.ToolName) == CircuitEvent.Closed)
                    await Audit("circuit.closed", record, "a probe call succeeded; held calls are released a few at a time", ct);
            }
            else if (circuits.Failed(record.ToolName, error.EffectiveKind, error.Code) is { Event: not CircuitEvent.None } change)
            {
                var name = change.Event switch { CircuitEvent.WouldOpen => "circuit.would_open", CircuitEvent.Reopened => "circuit.reopened", _ => "circuit.opened" };
                await Audit(name, record, $"{change.State!.Fingerprint}; probe after {change.State.ProbeAt:u}", ct);
                HostTelemetry.Circuits.Add(1, new KeyValuePair<string, object?>("tool", record.ToolName), new KeyValuePair<string, object?>("event", name));
            }
        }
        var next = error is null ? ActionStatus.Succeeded : ActionStatus.Failed;
        long? bytes = resultJson is null ? null : ResultMeasure.Bytes(resultJson);
        var done = await store.TransitionAsync(record.Id, ActionStatus.Executing, next,
            r => r with { ResultJson = resultJson, ResultBytes = bytes, ResultStats = stats, Error = error, LeaseUntil = null, RetryAt = retryAt }, ct);
        if (done is null)
            throw new InvalidOperationException($"Action {record.Id} left Executing unexpectedly.");
        await Audit(error is null ? "execution_succeeded" : "execution_failed", done, error?.Code, ct);
        HostTelemetry.Count(done);
        if (done.Status == ActionStatus.Succeeded) HostTelemetry.CountResult(done);
        if (startedAt is { } t0)
            HostTelemetry.ExecutionDuration.Record(_clock.GetElapsedTime(t0).TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", done.ToolName),
                new KeyValuePair<string, object?>("status", done.Status.ToString()));
        System.Diagnostics.Activity.Current?.SetTag("opentail.status", done.Status.ToString());
        if (events is not null)
            await events.PublishAsync(done.Status == ActionStatus.Succeeded ? ActionEventKind.Succeeded : ActionEventKind.Failed,
                done, error?.Code, ct);
        return done;
    }

    /// <summary>Blast-radius check, enforced at execution time so it also binds approved actions.</summary>
    private async Task<string?> ExceedsAsync(ActionRecord record, RateLimit limit, CancellationToken ct)
    {
        async Task<int> CountSince(TimeSpan window)
        {
            var since = _clock.GetUtcNow() - window;
            var n = 0;
            foreach (var status in new[] { ActionStatus.Succeeded, ActionStatus.Executing })
                n += (await store.QueryAsync(new ActionQuery
                {
                    ToolName = record.ToolName, Status = status, UpdatedSince = since, Limit = int.MaxValue,
                }, ct)).Count(r => r.Id != record.Id);
            return n;
        }

        if (limit.PerHour > 0 && await CountSince(TimeSpan.FromHours(1)) >= limit.PerHour) return $"{limit.PerHour}/hour";
        if (limit.PerDay > 0 && await CountSince(TimeSpan.FromDays(1)) >= limit.PerDay) return $"{limit.PerDay}/day";
        return null;
    }

    private Task Audit(string evt, ActionRecord r, string? detail, CancellationToken ct) =>
        audit.AppendAsync(new AuditEntry(r.UpdatedAt, evt, r.Id, r.ToolName, r.Requester.Id, detail), ct);

    /// <summary>A short text field of a result, for <see cref="ToolDescriptor.AuditResult"/>: identifiers only, so it is capped and scrubbed.</summary>
    private static string? ResultField(string resultJson, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(field, out var v)
                || v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 } s) return null;
            return Redaction.Scrub(s.Length > 120 ? s[..120] : s);
        }
        catch (JsonException) { return null; }
    }

    private static AIFunctionArguments ToArguments(string json)
    {
        var args = new AIFunctionArguments();
        if (string.IsNullOrWhiteSpace(json)) return args;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return args;
        foreach (var p in doc.RootElement.EnumerateObject())
            args[p.Name] = p.Value.Clone();
        return args;
    }

    private static string ToJson(object? value) => value switch
    {
        null => "null",
        JsonElement e => JsonSerializer.Serialize(e), // compact: factory-made functions return indented JSON
        string s => JsonSerializer.Serialize(s),
        _ => JsonSerializer.Serialize(value),
    };
}

public sealed record RecoveryReport(int StartedApproved, int RetriedIdempotent, int Interrupted);
