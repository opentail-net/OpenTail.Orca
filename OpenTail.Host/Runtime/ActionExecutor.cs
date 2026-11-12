using System.Text.Json;
using OpenTail.Host.Abstractions;

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
    Circuits? circuits = null,
    Func<string>? currentPolicyHash = null) : IActionExecutor
{
    /// <summary>Why an approval no longer holds at its claim (reason codes of the approval protocol). The detail of an
    /// <see cref="AuditEvents.ExecutionDenied"/> entry is one of these.</summary>
    public const string ReasonApprovalExpired = "approval_expired";
    public const string ReasonPolicyVersionMismatch = "policy_version_mismatch";
    public const string ReasonActionDigestMismatch = "action_digest_mismatch";

    private static readonly Dictionary<string, string> RefusalMessages = new()
    {
        [ReasonApprovalExpired] = "The request had expired by the time it was approved, so nothing was run.",
        [ReasonPolicyVersionMismatch] = "The policy changed after this was decided, so nothing was run. Submit it again under the current policy.",
        [ReasonActionDigestMismatch] = "The arguments no longer match what was approved, so nothing was run.",
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly FailuresConfig _failures = failures ?? new FailuresConfig();

    /// <summary>Paused routes (failures design, step 2). Null when the Host was built without them.</summary>
    public Circuits? Circuits => circuits;

    /// <summary>How long an Executing claim is valid. A tool that legitimately runs longer than this
    /// must be split or its lease extended (not yet supported): recovery would treat it as interrupted.</summary>
    public TimeSpan LeaseDuration { get; } = lease ?? TimeSpan.FromMinutes(5);

    /// <summary>Claims an Approved action (compare-and-set, so only one caller wins) and runs it, or fails it if its rate limit is spent.</summary>
    public async Task<ActionRecord> ExecuteAsync(string actionId, CancellationToken ct = default)
    {
        var current = await store.GetAsync(actionId, ct) ?? throw new KeyNotFoundException(actionId);
        if (current.Status == ActionStatus.Approved && await ClaimAndRunAsync(current, countAttempt: false, ct) is { } ran)
            return ran;
        return await store.GetAsync(actionId, ct) ?? throw new KeyNotFoundException(actionId);
    }

    /// <summary>Runs an action already in Executing (execute-mode creation, or just claimed).</summary>
    internal async Task<ActionRecord> RunClaimedAsync(ActionRecord record, CancellationToken ct, bool startedWritten = false)
    {
        // A claim writes its own "execution_started" (see ClaimAndRunAsync); an action created already running writes it here.
        if (!startedWritten) await Audit("execution_started", record, null, ct);
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

        // The rate limit took its slot in the transaction that claimed (or created) this action; see RateGate.

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
        var usage = store as IUsageStore;
        string value;
        ResultStats? stats = null;
        IReadOnlyList<string> entityHashes = [];
        var measureFailed = false;
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

            // Usage: what the result touched, for the daily rollup and telemetry. Bookkeeping only: if it fails the counts are lost,
            // the outcome is not.
            try
            {
                (stats, entityHashes) = ResultMeasure.Measure(tool.Descriptor.Counts, args, result,
                    usage is null || tool.Descriptor.Counts?.EntityKeys is null ? null : usage.EntitySalt);
            }
            catch (Exception)
            {
                measureFailed = true;
            }
            value = result;
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

        // The tool has run and its result is in hand. The outcome is committed as Succeeded here, outside the try above, so nothing that
        // follows can be reported as a tool failure: the external effect has already happened.
        var done = await Finish(record, value, null, ct, started, stats);
        await AfterSuccessAsync(done, tool.Descriptor, value, entityHashes, usage, measureFailed, ct);
        return done;
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
            if (await ClaimAndRunAsync(r, countAttempt: false, ct) is null) continue;
            started++;
        }

        foreach (var r in await store.QueryAsync(new ActionQuery { Status = ActionStatus.Executing, Limit = int.MaxValue }, ct))
        {
            if (r.LeaseUntil is { } until && until > now) continue;

            var failed = await AtomicTrail.ChangeAsync(store, audit, r.Id, ActionStatus.Executing, ActionStatus.Failed,
                x => x with
                {
                    LeaseUntil = null,
                    Error = new ActionError("interrupted", "The Host stopped while this action was running; the outcome is unknown.", true),
                }, AtomicTrail.Entry("interrupted", null), ct);
            if (failed is null) continue;
            interrupted++;

            if (catalog.TryGet(r.ToolName, out var tool) && tool.Descriptor.IdempotentByKey)
            {
                var again = await AtomicTrail.ChangeAsync(store, audit, r.Id, ActionStatus.Failed, ActionStatus.Approved,
                    x => x with
                    {
                        Attempts = x.Attempts + 1,
                        Error = null,
                        Reviews = [.. x.Reviews, new ActionReview("system", ReviewDecision.Retry,
                            "Tool is idempotent by key; retried after interruption.", now)],
                    }, AtomicTrail.Entry("retry_automatic", "after an interruption; the tool is idempotent by key"), ct);
                if (again is null) continue;
                if (await ClaimAndRunAsync(again, countAttempt: false, ct) is null) continue;
                retried++;
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
            var again = await AtomicTrail.ChangeAsync(store, audit, r.Id, ActionStatus.Failed, ActionStatus.Approved, x => x with
            {
                Attempts = held ? x.Attempts : x.Attempts + 1,
                Error = null,
                RetryAt = null,
                Reviews = [.. x.Reviews, new ActionReview("system", ReviewDecision.Retry,
                    held ? "Held while its route was paused; released." : "Transient failure of a tool idempotent by key; retried automatically.", now)],
            }, AtomicTrail.EntryWith("retry_automatic", d => held ? "released from a paused route" : $"attempt {d.Attempts}"), ct);
            if (again is null) continue;
            if (await ClaimAndRunAsync(again, countAttempt: false, ct) is null) continue;
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

        await NoteCircuitAsync(record, error, ct);
        var next = error is null ? ActionStatus.Succeeded : ActionStatus.Failed;
        long? bytes = resultJson is null ? null : ResultMeasure.Bytes(resultJson);
        Func<ActionRecord, ActionRecord> update = r => r with
        {
            ResultJson = resultJson, ResultBytes = bytes, ResultStats = stats, Error = error, LeaseUntil = null, RetryAt = retryAt,
        };
        // With the trail in the same database, the outcome and its entry are one transaction: neither is recorded without the other. Otherwise
        // the entry is appended after the outcome, and a crash between the two loses it.
        var done = AtomicOutcomes
            ? await AtomicTrail.For(store, audit)!.TransitionAuditedAsync(record.Id, ActionStatus.Executing, next, update, OutcomeEntry(error), ct)
            : await store.TransitionAsync(record.Id, ActionStatus.Executing, next, update, ct);
        if (done is null)
            throw new InvalidOperationException($"Action {record.Id} left Executing unexpectedly.");
        // The outcome is committed. Reporting it cannot change the outcome or turn it into a failure.
        await ReportAsync(done, error, startedAt, auditWritten: AtomicOutcomes, ct);
        return done;
    }

    /// <summary>Bookkeeping after a success: data labels, the decoy check, usage and the result's audit line. The outcome is already
    /// committed, so each step is best-effort (see <see cref="BestEffort"/>).</summary>
    private async Task AfterSuccessAsync(ActionRecord done, ToolDescriptor descriptor, string value, IReadOnlyList<string> entityHashes,
        IUsageStore? usage, bool measureFailed, CancellationToken ct)
    {
        if (measureFailed) await RecordFailure(done, "usage", "result not measured", ct);
        await BestEffort(done, "data labels", () =>
        {
            labels?.RememberValues(done.SessionId, done.Requester.Id, descriptor, value);
            return Task.CompletedTask;
        }, ct);
        await BestEffort(done, "decoy check", async () =>
        {
            // A decoy record in what came back: no real task needs one.
            if (levels is not null && decoys is { Count: > 0 } && IdentityLevels.ContainsDecoy(decoys, value))
                await levels.DecoyReadAsync(done.Requester.Id, done.ToolName, ct);
        }, ct);
        await BestEffort(done, "usage", () =>
        {
            if (usage is not null && entityHashes.Count > 0)
                usage.RecordEntities(DateOnly.FromDateTime(done.UpdatedAt.UtcDateTime), done.Requester.Id, done.ToolName, [.. entityHashes]);
            return Task.CompletedTask;
        }, ct);
        await BestEffort(done, "result audit", () =>
        {
            if (descriptor.AuditResult is { } ra && ResultField(value, ra.Field) is { } detail)
                return Audit(ra.Event, done, detail, ct);
            return Task.CompletedTask;
        }, ct);
    }

    /// <summary>Runs one bookkeeping step: a trail line, telemetry, a notification, or the result's audit line. A failure is written to the trail
    /// (see <see cref="RecordFailure"/>) and does not propagate: a call that ran must not be reported as failed, and a failed bookkeeping
    /// step must not stop an outcome from being committed.</summary>
    private async Task BestEffort(ActionRecord done, string step, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            await RecordFailure(done, step, ex.GetType().Name, ct);
        }
    }

    /// <summary>Records that a bookkeeping step failed. Names the step and the exception type only, never its message, which may hold
    /// arguments. If the trail itself is failing there is nothing more to do without changing the committed outcome.</summary>
    private async Task RecordFailure(ActionRecord done, string step, string kind, CancellationToken ct)
    {
        try
        {
            await Audit("bookkeeping_failed", done, $"{step}: {kind}", ct);
        }
        catch (Exception)
        {
            // The trail is down; the committed outcome stands.
        }
    }

    /// <summary>
    /// Claims an approved action and runs it. The tool's rate limit is checked in the claim's own transaction (see <see cref="RateGate"/>):
    /// a refused claim fails the action instead of running it. Returns null when the action is no longer approved (another caller took it).
    /// </summary>
    internal async Task<ActionRecord?> ClaimAndRunAsync(ActionRecord approved, bool countAttempt, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // The approval was given for one request, under one policy, for these arguments. Check that it still holds before anything is
        // claimed. A refused approval fails the action, nothing runs, and the refusal is audited with its reason.
        if (RecheckRefusal(approved, now) is { } reason)
        {
            var refused = await AtomicTrail.ChangeAsync(store, audit, approved.Id, ActionStatus.Approved, ActionStatus.Failed,
                r => (r with { Error = new ActionError(reason, RefusalMessages[reason], false) })
                    .WithResolution(ResolutionOutcomes.ExecutionDenied, "system", reason, now),
                d => new AuditEntry(d.UpdatedAt, AuditEvents.ExecutionDenied, d.Id, d.ToolName, d.Requester.Id, reason), ct);
            if (refused is null) return null;
            return await ReportRefusalAsync(refused, ct, auditWritten: true);
        }

        var gate = RateGate.For(approved.ToolName, approved.PolicySnapshot?.RateLimit);
        // The claim and its entry are one transaction: "approval_consumed" when the approval is spent on this run, "execution_failed" when
        // the rate limit refuses it. Nothing is recorded as consumed that was not.
        var claimed = await AtomicTrail.ChangeAdmittedAsync(store, audit, approved.Id, ActionStatus.Approved, ActionStatus.Executing,
            r => r with { LeaseUntil = now + LeaseDuration, Attempts = countAttempt ? r.Attempts + 1 : r.Attempts },
            gate.Admissions, (i, current) => gate.Refuse(current, i, now),
            d => d.Status == ActionStatus.Executing
                ? new AuditEntry(d.UpdatedAt, AuditEvents.ApprovalConsumed, d.Id, d.ToolName, d.Requester.Id, null)
                : new AuditEntry(d.UpdatedAt, "execution_failed", d.Id, d.ToolName, d.Requester.Id, d.Error?.Code), ct);
        if (claimed is null) return null;
        return claimed.Status == ActionStatus.Failed
            ? await ReportRefusalAsync(claimed, ct, auditWritten: true)
            : await RunClaimedAsync(claimed, ct, startedWritten: false);
    }

    /// <summary>Why an approved action must not run, or null when its approval still holds: the request expired before it was approved,
    /// the policy it was decided under is no longer the one in force (or the Host cannot say what that is), the arguments that would
    /// run are not the ones approved, or the action's binding (requester, tool and its current schema, arguments, policy) no longer
    /// recomputes to the binding the approval holds for. An edit is checked against the digests recorded with its review; without one,
    /// the stored arguments are checked against the digest made at submission. A missing or unrecomputable binding fails closed.</summary>
    internal string? RecheckRefusal(ActionRecord approved, DateTimeOffset now)
    {
        if (approved.ExpiresAt is { } deadline && deadline <= now) return ReasonApprovalExpired;
        var current = currentPolicyHash?.Invoke();
        if (current is null || approved.PolicyHash is null || approved.PolicyHash != current) return ReasonPolicyVersionMismatch;
        var lastEdit = approved.Reviews.LastOrDefault(r => r.EditedArgumentsJson is not null);
        var expected = lastEdit is null ? approved.ArgumentsHash : lastEdit.EditedArgumentsHash;
        if (expected is null || ArgumentDigest.Of(approved.EffectiveArgumentsJson) != expected) return ReasonActionDigestMismatch;

        // The whole action, recomputed from the stored record and the tool as it is loaded now: a module upgrade that changes the tool's
        // parameter schema changes the current schema version, and so refuses the approvals given before it.
        var bound = approved.EffectiveBindingDigest;
        var recomputed = ActionBindings.Digest(approved.Requester.Id, approved.ToolName, catalog.SchemaVersionOf(approved.ToolName),
            approved.EffectiveArgumentsJson, approved.PolicyHash);
        if (bound is null || recomputed is null || recomputed != bound) return ReasonActionDigestMismatch;
        return null;
    }

    /// <summary>A refusal at the claim or at creation: the action never ran. It is reported as any failure is, and counts toward the
    /// route's health as it always has.</summary>
    internal async Task<ActionRecord> ReportRefusalAsync(ActionRecord refused, CancellationToken ct, bool auditWritten = false)
    {
        await NoteCircuitAsync(refused, refused.Error, ct);
        await ReportAsync(refused, refused.Error, null, auditWritten, ct);
        return refused;
    }

    /// <summary>The route's health: the probe that succeeds reopens it; repeated failures of one kind pause it (once per episode in the trail).
    /// The trail lines are bookkeeping: a failure to write one never stops an outcome from being committed.</summary>
    private async Task NoteCircuitAsync(ActionRecord record, ActionError? error, CancellationToken ct)
    {
        if (circuits is null || error?.Code == "circuit_open") return;
        if (error is null)
        {
            if (circuits.Succeeded(record.ToolName) == CircuitEvent.Closed)
                await BestEffort(record, "circuit", () => Audit("circuit.closed", record, "a probe call succeeded; held calls are released a few at a time", ct), ct);
        }
        else if (circuits.Failed(record.ToolName, error.EffectiveKind, error.Code) is { Event: not CircuitEvent.None } change)
        {
            var name = change.Event switch { CircuitEvent.WouldOpen => "circuit.would_open", CircuitEvent.Reopened => "circuit.reopened", _ => "circuit.opened" };
            await BestEffort(record, "circuit", () => Audit(name, record, $"{change.State!.Fingerprint}; probe after {change.State.ProbeAt:u}", ct), ct);
            HostTelemetry.Circuits.Add(1, new KeyValuePair<string, object?>("tool", record.ToolName), new KeyValuePair<string, object?>("event", name));
        }
    }

    /// <summary>True when an outcome is committed together with its audit entry: the store and the trail are the same database.</summary>
    private bool AtomicOutcomes => AtomicTrail.For(store, audit) is not null;

    /// <summary>The audit entry of an outcome, built from the record as committed (the fields <see cref="Audit"/> would write).</summary>
    private static Func<ActionRecord, AuditEntry> OutcomeEntry(ActionError? error) =>
        d => new AuditEntry(d.UpdatedAt, error is null ? "execution_succeeded" : "execution_failed", d.Id, d.ToolName, d.Requester.Id, error?.Code);

    /// <summary>Reports a committed outcome: the audit line (unless the outcome was committed with it), telemetry and the notification.
    /// The parts after the commit are best-effort: they cannot change the outcome or turn it into a failure.</summary>
    private async Task ReportAsync(ActionRecord done, ActionError? error, long? startedAt, bool auditWritten, CancellationToken ct)
    {
        if (!auditWritten)
            await BestEffort(done, "outcome audit", () => Audit(error is null ? "execution_succeeded" : "execution_failed", done, error?.Code, ct), ct);
        HostTelemetry.Count(done);
        if (done.Status == ActionStatus.Succeeded) HostTelemetry.CountResult(done);
        if (startedAt is { } t0)
            HostTelemetry.ExecutionDuration.Record(_clock.GetElapsedTime(t0).TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", done.ToolName),
                new KeyValuePair<string, object?>("status", done.Status.ToString()));
        System.Diagnostics.Activity.Current?.SetTag("opentail.status", done.Status.ToString());
        if (events is not null)
            await BestEffort(done, "events", () => events.PublishAsync(
                done.Status == ActionStatus.Succeeded ? ActionEventKind.Succeeded : ActionEventKind.Failed, done, error?.Code, ct), ct);
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
