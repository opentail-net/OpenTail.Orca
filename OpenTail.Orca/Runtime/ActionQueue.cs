using System.Security.Cryptography;
using System.Text;

namespace OpenTail.Orca;

/// <summary>Applies policy to a request: execute now, hold for a reviewer, or deny. Never blocks waiting for a reviewer.</summary>
public sealed class ActionQueue(
    ToolCatalog catalog,
    PolicyResolver policy,
    IActionStore store,
    ActionExecutor executor,
    IAuditSink audit,
    SessionRegistry sessions,
    TimeProvider? clock = null,
    TrustAdvisor? trust = null,
    ActionEvents? events = null,
    IdentityLevels? levels = null,
    DataLabels? labels = null,
    AccessGrants? grants = null) : IActionQueue
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ActionRecord> SubmitAsync(ActionRequest request, CancellationToken ct = default)
    {
        using var activity = HostTelemetry.Source.StartActivity("action.submit");
        activity?.SetTag("opentail.tool", request.ToolName);
        var now = _clock.GetUtcNow();
        var hash = Hash(request.ArgumentsJson);
        var id = "act_" + Guid.NewGuid().ToString("N");
        activity?.SetTag("opentail.action_id", id);
        // The decision trace: every check, in order, written as it runs (audit D1). Records copy it when they are created.
        var trace = new List<DecisionStep>();
        ActionRecord Rec() => Base(request, id, hash, now) with
        {
            DecisionTrace = [.. trace],
            PolicyHash = policy.PolicyHash,
            BindingDigest = BindingDigestOf(request, policy.PolicyHash),
        };

        // A session is owned by whoever started it. An AI caller naming someone else's session is told it is unknown
        // (no existence oracle). Trusted in-process callers (admin, scripts) are not bound to a session owner.
        if (request.SessionId is { } sid)
        {
            var known = request.Requester.Kind == RequesterKind.AiSession
                ? sessions.Exists(sid, request.Requester.Id)
                : sessions.Exists(sid);
            if (!known) throw new ArgumentException("Unknown sessionId. Call start_session first.", nameof(request));
            sessions.Touch(sid);
        }

        if (!catalog.TryGet(request.ToolName, out var tool))
        {
            trace.Add(new("tool", "Deny", "no loaded module declares this tool", Changed: true));
            return await CreateDenied(Rec() with
            {
                ModuleId = "unknown",
                Status = ActionStatus.Denied,
                PolicyMode = ToolMode.Deny,
                Error = new ActionError("unknown_tool", "No such tool.", false),
            }, "unknown_tool", ct);
        }

        // Outside the caller's agent profile: refused before policy, taint or anything else, and audited (a later tripwire reads
        // these). The profile comes from the credential, so no argument or prompt can widen it.
        // In-process callers (admin, scripts) and the stdio operator are trusted; only signed-in MCP callers are bound by profiles.
        var localOperator = request.Requester.Kind != RequesterKind.AiSession || request.Requester.Id == Subjects.Local;
        var admitted = policy.Admits(tool.Descriptor, request.Profile, localOperator);
        // A temporary grant admits one more tool, one call at a time, until it ends (time or calls, whichever first).
        if (!admitted && grants?.Use(request.Requester.Id, tool.Descriptor.Name) is { } grant)
        {
            admitted = true;
            trace.Add(new("profile", "allowed by grant", $"{grant.Id}; profile {request.Profile ?? "none"} does not list the tool"));
            await Audit("granted_call", id, request, tool.Descriptor.Name, $"under {grant.Id}: {grant.CallsLeft} call(s) left, until {grant.Until:u}", now, ct);
        }
        else
            trace.Add(new("profile", admitted ? "allowed" : "Deny",
                request.Profile is { } pn ? "profile " + pn : localOperator ? "local operator" : "no profile", Changed: !admitted));
        // A request for access must name a tool the caller's profile lists as requestable.
        if (admitted && tool.Descriptor.Name == AccessGrants.RequestTool && request.Profile is { } asker && RequestedTool(request.ArgumentsJson) is var wanted
            && !(policy.Config.Agents.TryGetValue(asker, out var ap) && wanted is not null && ap.Requestable.Contains(wanted, StringComparer.Ordinal)))
        {
            trace.Add(new("access request", "Deny", $"'{wanted}' is not requestable under profile {asker}", Changed: true));
            return await CreateDenied(Rec() with
            {
                ModuleId = tool.Descriptor.ModuleId,
                Status = ActionStatus.Denied,
                PolicyMode = ToolMode.Deny,
                Error = new ActionError("invalid_arguments", $"'{wanted}' is not a tool you may ask for.", false),
            }, "not_requestable", ct);
        }
        if (!admitted)
        {
            var refused = await CreateDenied(Rec() with
            {
                ModuleId = tool.Descriptor.ModuleId,
                Status = ActionStatus.Denied,
                PolicyMode = ToolMode.Deny,
                Error = new ActionError("not_in_profile", "This tool is not available to you.", false),
            }, $"not_in_profile ({request.Profile ?? "none"})", ct);
            HostTelemetry.Count(refused);
            // A healthy agent never sees a tool outside its profile, so naming one is a tripwire. The local operator has no profile.
            if (levels is not null && request.Profile is not null) await levels.OutOfProfileAsync(request.Requester.Id, tool.Descriptor.Name, ct);
            return refused;
        }

        // A decoy record's marker leaving through an outbound tool: a tripwire. Checked before anything runs.
        if (levels is not null && policy.Config.Decoys.Count > 0 && tool.Descriptor.Risk == ToolRisk.ExternalEffect
            && IdentityLevels.ContainsDecoy(policy.Config.Decoys, request.ArgumentsJson)
            && await levels.DecoySentAsync(request.Requester.Id, tool.Descriptor.Name, ct))
        {
            trace.Add(new("decoy", "Deny", "a decoy record's marker in the arguments of an outbound tool; identity suspended", Changed: true));
            var decoy = await CreateDenied(Rec() with
            {
                ModuleId = tool.Descriptor.ModuleId,
                Status = ActionStatus.Denied,
                PolicyMode = ToolMode.Deny,
                Error = new ActionError("suspended", "This identity is suspended. An operator must clear it.", false),
            }, "decoy_sent", ct);
            HostTelemetry.Count(decoy);
            return decoy;
        }

        // A suspended identity is refused everything until a person clears it.
        var level = levels?.Effective(request.Requester.Id) ?? IdentityLevel.Normal;
        trace.Add(new("level", level == IdentityLevel.Suspended ? "Deny" : level.ToString(), level == IdentityLevel.Suspended ? "identity suspended" : null,
            Changed: level == IdentityLevel.Suspended));
        if (level == IdentityLevel.Suspended)
        {
            var suspended = await CreateDenied(Rec() with
            {
                ModuleId = tool.Descriptor.ModuleId,
                Status = ActionStatus.Denied,
                PolicyMode = ToolMode.Deny,
                Error = new ActionError("suspended", "This identity is suspended. An operator must clear it.", false),
            }, "suspended", ct);
            HostTelemetry.Count(suspended);
            return suspended;
        }

        var tainted = (request.SessionId is { } s ? sessions.IsTainted(s) : policy.Config.SessionlessTainted)
                      || sessions.IsSubjectTainted(request.Requester.Id);
        var decision = policy.Resolve(tool.Descriptor, tainted, request.ArgumentsJson, now, request.Requester.Id);
        trace.AddRange(decision.Steps);
        // Elevated: everything above Read waits for a person, whatever policy, rules or trust said.
        if (level == IdentityLevel.Elevated && tool.Descriptor.Risk > ToolRisk.Read && decision.Mode == ToolMode.Execute)
        {
            trace.Add(new("level", levels!.Config.ElevatedDelay is null ? "Queue" : "Execute with delay", "identity is Elevated; the tool is above Read", Changed: true));
            decision = levels!.Config.ElevatedDelay is { } slower
                // The operator's softer option: announced and stoppable rather than held, and never shorter than the tool's own delay.
                ? decision with { Policy = decision.Policy with { DelayFor = decision.Policy.EffectiveDelay is { } own && own > slower ? own : slower, Delay = false },
                                  Rule = decision.Rule + "+level:elevated-delay", Admissions = [] }
                : decision with { Mode = ToolMode.Queue, Rule = decision.Rule + "+level:elevated" };
        }

        // Soft signals (breadth, repeated high-impact attempts after outside content): may raise the identity to Elevated.
        if (levels is not null) await levels.ObserveAsync(request.Requester.Id, tool.Descriptor.Name, tainted && tool.Descriptor.IsHighImpact, ct);

        // Data flow: labels the session already holds, checked at an outbound tool. Only tightens; report only unless enforced.
        var held = labels?.Held(request.SessionId, request.Requester.Id) ?? [];
        var valueMatched = false;
        if (labels is not null && labels.Mode != LimitMode.Off && held.Count > 0 && decision.Mode != ToolMode.Deny
            && labels.Check(tool.Descriptor, held.Select(h => h.Label), request.ArgumentsJson) is { Count: > 0 } brokenRules)
        {
            // Value matching: values returned by labelled reads, found in these arguments, are strong evidence; their absence may relax
            // a rule that opted in (never a Deny).
            valueMatched = labels.ArgumentsMatch(request.SessionId, request.Requester.Id, request.ArgumentsJson);
            if (valueMatched) await Audit("flow_matched", id, request, tool.Descriptor.Name, "the arguments contain values returned by labelled reads", now, ct);
            foreach (var broken in brokenRules.Where(b => valueMatched || !(b.RelaxWithoutMatch && b.Mode == ToolMode.Queue)))
            {
                var enforced = labels.Mode == LimitMode.Enforce;
                await Audit(enforced ? "flow_checked" : "flow_would_check", id, request, tool.Descriptor.Name,
                    $"{broken.Label}: {tool.Descriptor.Name} is not an allowed destination -> {broken.Mode}", now, ct);
                var changes = enforced && broken.Mode < decision.Mode;
                trace.Add(new("flow", enforced ? broken.Mode.ToString() : $"would be {broken.Mode} (report only)",
                    $"label {broken.Label}: {tool.Descriptor.Name} is not an allowed destination{(valueMatched ? "; the arguments contain values from labelled reads" : "")}",
                    Changed: changes));
                if (changes)
                    decision = decision with { Mode = broken.Mode, Rule = decision.Rule + "+flow:" + broken.Label };
            }
        }
        // The call's own labels are gained when it is submitted, like taint (a denied call returns nothing).
        if (labels is not null && decision.Mode != ToolMode.Deny && labels.Of(tool.Descriptor).Count > 0)
            labels.Gain(request.SessionId, request.Requester.Id, tool.Descriptor);

        // Taint when the call is SUBMITTED, not when it finishes: two parallel calls ("read mail" and "send") must not
        // both see a clean session. A denied call returns nothing, so it taints nothing.
        if (tool.Descriptor.ReadsUntrusted && decision.Mode != ToolMode.Deny)
        {
            if (request.SessionId is { } ts) sessions.Taint(ts);
            else sessions.TaintSubject(request.Requester.Id);
        }
        await Audit("policy_decision", id, request, tool.Descriptor.Name,
            $"{decision.Mode} via {decision.Rule}", now, ct);
        activity?.SetTag("opentail.module", tool.Descriptor.ModuleId);
        activity?.SetTag("opentail.risk", tool.Descriptor.Risk.ToString());
        activity?.SetTag("opentail.mode", decision.Mode.ToString());
        activity?.SetTag("opentail.tainted", tainted);

        ActionRecord WithDecision(PolicyDecision d) => Rec() with
        {
            ModuleId = tool.Descriptor.ModuleId,
            ModuleVersion = catalog.ModuleOf(tool.Descriptor.Name)?.Version,
            PolicyMode = d.Mode,
            PolicySnapshot = d.Policy,
            PolicyRule = d.RuleName,
            ReviewerGroup = d.Policy.ReviewerGroup,
            RequiredApprovals = d.Policy.RequiredApprovals,
            Tainted = tainted,
            SessionLabels = [.. held.Select(h => $"{h.Label} ({string.Join(", ", h.Sources)})"),
                .. valueMatched ? ["the arguments contain values returned by these reads"] : Array.Empty<string>()],
        };
        var baseRecord = WithDecision(decision);

        async Task<ActionRecord> QueueAsync(ActionRecord baseRecord, PolicyDecision decision, DateTimeOffset? runAt = null)
        {
        trace.Add(runAt is { } at
            ? new DecisionStep("delay", "Execute with undo window", $"runs at {at:u} unless a person stops it", Changed: true)
            : new DecisionStep("queue", "Queue", $"reviewer group {decision.Policy.ReviewerGroup ?? "any"}, {decision.Policy.RequiredApprovals} approval(s)"));
        baseRecord = baseRecord with { DecisionTrace = [.. trace] };
        string? preview = null;
        if (tool.Preview is not null)
        {
            try
            {
                var p = await tool.Preview.PreviewAsync(request.ArgumentsJson, ct);
                preview = System.Text.Json.JsonSerializer.Serialize(p);
            }
            catch (ArgumentException ex)
            {
                trace.Add(new("preview", "Deny", "the tool says these arguments can never be valid", Changed: true));
                // The tool says these arguments could never be approved. Tell the AI now instead of wasting a review.
                var invalid = await CreateDenied(baseRecord with
                {
                    DecisionTrace = [.. trace],
                    Status = ActionStatus.Denied,
                    Error = new ActionError("invalid_arguments", Redaction.Scrub(ex.Message), true),
                }, "invalid_arguments", ct);
                HostTelemetry.Count(invalid);
                return invalid;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Audit("preview_failed", id, request, tool.Descriptor.Name, ex.GetType().Name, now, ct);
            }
        }

        var pendingRecord = baseRecord with
        {
            Status = ActionStatus.Pending,
            PreviewJson = preview,
            PreviewUnavailable = preview is null && tool.Descriptor.IsHighImpact,
            // A delayed action does not expire: it runs, or someone stops it, or it is held after an outage.
            ExpiresAt = runAt is null && decision.Policy.ExpireAfter is { } e ? now + e : null,
            RunAt = runAt,
        };

        // The cap is counted in the same transaction that inserts, so it holds across processes. The "approval_requested" entry is written in that
        // transaction as well.
        var cap = policy.Config.MaxPendingPerRequester;
        IReadOnlyList<Admission> capAdmissions = cap > 0
            ? [new Admission(new ActionQuery { Status = ActionStatus.Pending, RequesterId = request.Requester.Id }, null, cap)]
            : [];
        var pending = await CreateAudited(pendingRecord, capAdmissions,
            EntryOf(runAt is null ? AuditEvents.ApprovalRequested : "delayed", runAt is null ? null : $"runs at {runAt:u} unless stopped"), ct);
        if (pending is null)
        {
            trace.Add(new("queue cap", "Deny", $"the requester already has {cap} actions waiting", Changed: true));
            var full = await CreateDenied(baseRecord with
            {
                DecisionTrace = [.. trace],
                Status = ActionStatus.Denied,
                Error = new ActionError("queue_full",
                    $"You already have {cap} actions waiting for approval. Wait for decisions before submitting more.", true),
            }, "queue_full", ct);
            HostTelemetry.Count(full);
            await Publish(ActionEventKind.Denied, full, "queue_full", ct);
            return full;
        }

        if (pending.Id == id)
        {
            HostTelemetry.Pending.Add(1);
            HostTelemetry.Count(pending);
            // Reviewers are told about a delayed action too (the Going-out-soon list is the review page's Pending list); nobody has to act.
            await Publish(ActionEventKind.Queued, pending, runAt is null ? null : $"runs at {runAt:u} unless stopped", ct);
        }
        return pending;
        }

        switch (decision.Mode)
        {
            case ToolMode.Deny:
            {
                var denied = await CreateDenied(baseRecord with
                {
                    Status = ActionStatus.Denied,
                    Error = new ActionError("denied", $"Denied by policy ({decision.Rule}).", false),
                }, decision.Rule, ct);
                HostTelemetry.Count(denied);
                return denied;
            }

            case ToolMode.Queue:
                return await QueueAsync(baseRecord, decision);

            default: // Execute
            {
                // Execute with an undo window: announced now, run by the maintenance sweep at RunAt unless someone stops it.
                if (decision.Policy.EffectiveDelay is { } delay && tool.Descriptor.Risk > ToolRisk.Read)
                {
                    var runAt = now + delay;
                    // Nobody is around outside working hours: run at the next opening plus the window instead, if the tool opted in.
                    if (policy.Config.Tools.TryGetValue(tool.Descriptor.Name, out var own) && own.DelayUntilWorkingHours
                        && policy.Config.WorkingHours is { } hours && !hours.Contains(runAt))
                        runAt = hours.NextOpening(runAt) + delay;
                    return await QueueAsync(baseRecord, decision, runAt);
                }
                var executing = baseRecord with { Status = ActionStatus.Executing, Attempts = 1, LeaseUntil = now + executor.LeaseDuration };
                // A budget on the rule and the tool's rate limit are counted in the same transaction that inserts, so they hold across
                // processes. A spent budget queues the call for a reviewer; a spent rate limit fails it (see RateGate).
                var gate = RateGate.For(baseRecord.ToolName, decision.Policy.RateLimit);
                var budgets = decision.Admissions.Count;
                IReadOnlyList<Admission> admissions = [.. decision.Admissions, .. gate.Admissions];
                // The entry of the call is written with it: "execution_started" for a call that runs, "execution_failed" for one the rate limit refused.
                var running = await AtomicTrail.CreateAdmittedAsync(store, audit, executing, admissions,
                    i => i < budgets ? null : gate.Refuse(executing, i - budgets, now),
                    d => d.Status == ActionStatus.Executing
                        ? new AuditEntry(_clock.GetUtcNow(), "execution_started", d.Id, d.ToolName, d.Requester.Id, null)
                        : new AuditEntry(_clock.GetUtcNow(), "execution_failed", d.Id, d.ToolName, d.Requester.Id, d.Error?.Code), ct);
                if (running is null)
                {
                    // The budget is spent: the rule no longer applies, so the call waits for a reviewer like any other.
                    trace.Add(new("budget", "Queue", "the rule's budget is spent", Changed: true));
                    var queued = decision with
                    {
                        Mode = ToolMode.Queue,
                        Policy = decision.FallbackPolicy ?? decision.Policy,
                        Rule = decision.Rule + "+budget:queue",
                        RuleName = null,
                        Admissions = [],
                    };
                    await Audit("policy_decision", id, request, tool.Descriptor.Name, $"Queue via {queued.Rule}", now, ct);
                    return await QueueAsync(WithDecision(queued), queued);
                }
                // An idempotent resubmission returns the existing record; only run what we just created.
                if (running.Id != id) return running;
                if (running.Status == ActionStatus.Failed) return await executor.ReportRefusalAsync(running, ct, auditWritten: true); // its rate limit is spent
                // The MCP handler may answer with a task if this runs long: let it see the action now, before it finishes.
                ActionCapture.Record(running);
                var ran = await executor.RunClaimedAsync(running, ct, startedWritten: true);
                if (trust is not null) await trust.DemoteIfFailedAsync(ran, ct);
                return ran;
            }
        }
    }

    public async Task<ActionRecord> ReviewAsync(string actionId, ActionReview review, CancellationToken ct = default)
    {
        using var activity = HostTelemetry.Source.StartActivity("action.review");
        activity?.SetTag("opentail.action_id", actionId);
        activity?.SetTag("opentail.decision", review.Decision.ToString());
        // Who may decide this is the queue's rule to enforce (group, no AI on human-only, self-approval, known identity), not the page's.
        var existing = await store.GetAsync(actionId, ct) ?? throw new KeyNotFoundException(actionId);

        // The digests of a reviewer's edit are stored with the review, so the claim can check the arguments that will run: the argument
        // digest, and the binding of the whole action as it would then stand (same requester, tool, current schema, policy it was decided under).
        var recorded = review with
        {
            EditedArgumentsHash = review.EditedArgumentsJson is null ? null : Hash(review.EditedArgumentsJson),
            EditedBindingDigest = review.EditedArgumentsJson is null ? null
                : ActionBindings.Digest(existing.Requester.Id, existing.ToolName, catalog.SchemaVersionOf(existing.ToolName),
                    review.EditedArgumentsJson, existing.PolicyHash),
        };
        Func<ActionRecord, ActionRecord> addReview = r => r with { Reviews = [.. r.Reviews, recorded] };

        var verdict = ReviewAuthority.Check(existing, review.Decision, review.Subject, review.Groups, policy.People);
        if (!verdict.Allowed)
        {
            await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "review_refused", existing.Id, existing.ToolName, review.Subject ?? review.Reviewer, review.Decision.ToString()), ct);
            throw new UnauthorizedAccessException(verdict.Reason);
        }
        if (review.Subject is { } reviewer && Subjects.IsAi(reviewer))
        {
            var ai = await AiVerdictAsync(existing, review.Decision, review.EditedArgumentsJson is not null, ct);
            if (!ai.Allowed)
            {
                await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "review_refused", existing.Id, existing.ToolName, reviewer, $"{review.Decision}: {ai.Reason}"), ct);
                throw new UnauthorizedAccessException(ai.Reason);
            }
        }

        // P0 (F14, F11): a retry needs a retryable failure, and an unknown outcome is never retried unless the tool is idempotent by key.
        // This holds for every reviewer and through the HTTP endpoint, not only on the page.
        if (review.Decision == ReviewDecision.Retry)
        {
            var idempotent = catalog.TryGet(existing.ToolName, out var retryTool) && retryTool.Descriptor.IdempotentByKey;
            var byAi = review.Subject is { } retrySubject && Subjects.IsAi(retrySubject);
            if (RetryRules.Refusal(existing.Error, idempotent, byAi, review.Reason) is { } retryReason)
            {
                await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "review_refused", existing.Id, existing.ToolName,
                    review.Subject ?? review.Reviewer, $"Retry: {retryReason}"), ct);
                throw new UnauthorizedAccessException(retryReason);
            }
        }

        switch (review.Decision)
        {
            case ReviewDecision.Approve:
            {
                if (levels?.Effective(existing.Requester.Id) == IdentityLevel.Suspended)
                {
                    await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "review_refused", existing.Id, existing.ToolName, review.Subject ?? review.Reviewer, "requester suspended"), ct);
                    throw new InvalidOperationException("The requester is suspended: its pending actions are frozen until an operator clears it. Reject it, or clear the identity first.");
                }
                // The sweep runs periodically, so an overdue action may still say Pending. Never approve past its deadline.
                var due = await store.GetAsync(actionId, ct) ?? throw new KeyNotFoundException(actionId);

                // Some tools take no edits (administration tools always): approve or reject exactly what was asked.
                if (review.EditedArgumentsJson is not null && due.Status == ActionStatus.Pending && !EditsAllowed(due))
                {
                    await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "edit_refused", due.Id, due.ToolName, review.Reviewer, "AllowEdits: false"), ct);
                    throw new InvalidOperationException("This action cannot be edited: approve or reject it as asked, or request changes.");
                }
                // A reviewer edit is new input: it must not slip past a Deny rule that the original arguments did not trigger, nor
                // a data-flow rule, and later approvers must see a preview of what will run, not of the arguments first asked for.
                var edits = review.EditedArgumentsJson is not null && due.Status == ActionStatus.Pending;
                string? editedPreview = null;
                var editedPreviewUnavailable = false;
                if (edits && catalog.TryGet(due.ToolName, out var editedTool))
                {
                    var edited = policy.Resolve(editedTool.Descriptor, due.Tainted, review.EditedArgumentsJson, _clock.GetUtcNow(), due.Requester.Id);
                    if (edited.Mode == ToolMode.Deny)
                    {
                        await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "edit_refused", due.Id, due.ToolName, review.Reviewer, edited.RuleName ?? edited.Rule), ct);
                        throw new InvalidOperationException($"The edited arguments are refused by policy ({edited.RuleName ?? edited.Rule}).");
                    }

                    // Enforced flow rules that deny: a Queue rule is already met, since a person is reviewing.
                    if (labels is { Mode: LimitMode.Enforce } && labels.Held(due.SessionId, due.Requester.Id) is { Count: > 0 } held
                        && labels.Check(editedTool.Descriptor, held.Select(h => h.Label), review.EditedArgumentsJson!)
                            .FirstOrDefault(b => b.Mode == ToolMode.Deny) is { } denied)
                    {
                        await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "edit_refused", due.Id, due.ToolName, review.Reviewer, "flow:" + denied.Label), ct);
                        throw new InvalidOperationException($"The edited arguments would send data labelled {denied.Label} where it may not go.");
                    }

                    if (editedTool.Preview is not null)
                    {
                        try
                        {
                            editedPreview = System.Text.Json.JsonSerializer.Serialize(await editedTool.Preview.PreviewAsync(review.EditedArgumentsJson!, ct));
                        }
                        catch (ArgumentException ex)
                        {
                            await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "edit_refused", due.Id, due.ToolName, review.Reviewer, "invalid_arguments"), ct);
                            throw new InvalidOperationException("The edited arguments can never be valid: " + Redaction.Scrub(ex.Message));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "preview_failed", due.Id, due.ToolName, review.Reviewer, ex.GetType().Name), ct);
                        }
                    }
                    editedPreviewUnavailable = editedPreview is null && editedTool.Descriptor.IsHighImpact;
                }
                if (due.Status == ActionStatus.Pending && due.ExpiresAt is { } deadline && deadline <= _clock.GetUtcNow())
                {
                    var expired = await ChangeAudited(actionId, ActionStatus.Pending, ActionStatus.Expired,
                        x => x.WithResolution(ResolutionOutcomes.Expired, "system", ActionExecutor.ReasonApprovalExpired, _clock.GetUtcNow()),
                        EntryOf(AuditEvents.ApprovalExpired, null), ct);
                    if (expired is not null)
                    {
                        LeftPending(expired);
                        HostTelemetry.Count(expired);
                        await Publish(ActionEventKind.Expired, expired, null, ct);
                    }
                    throw new InvalidOperationException($"Action {actionId} expired before it was approved.");
                }

                // Decided inside the store's transaction: "is this the n-th distinct approval?" must be judged against what is stored
                // at that instant, or two reviewers approving together could each think they were the first.
                ActionTransition? Decide(ActionRecord r)
                {
                    var (count, duplicate) = ReviewAuthority.Tally(r, review, policy.People);
                    if (duplicate) return null;
                    var updated = addReview(r);
                    // The stored preview is of the edited arguments (none when the tool could not make one: never the old one).
                    if (edits) updated = updated with { PreviewJson = editedPreview, PreviewUnavailable = editedPreviewUnavailable };
                    return count >= r.RequiredApprovals
                        ? new ActionTransition(ActionStatus.Approved, (updated with { Attempts = r.Attempts + 1 })
                            .WithResolution(ResolutionOutcomes.Allowed, ResolverOf(review), null, _clock.GetUtcNow()))
                        : new ActionTransition(ActionStatus.Pending, updated);
                }

                // The entry says which of the two happened, and is written in the same transaction as the decision.
                Func<ActionRecord, AuditEntry> decisionEntry = d => d.Status == ActionStatus.Pending
                    ? Entry("approval_recorded", d, $"{ReviewAuthority.Tally(d with { Reviews = d.Reviews.Take(d.Reviews.Count - 1).ToList() }, review, policy.People).Count} of {d.RequiredApprovals}")
                    : Entry(AuditEvents.ApprovalAllowed, d, review.Reviewer);
                var approved = await DecideAudited(actionId, ActionStatus.Pending, Decide, decisionEntry, ct);
                if (approved is null)
                {
                    if ((await store.GetAsync(actionId, ct))?.Status == ActionStatus.Pending)
                        throw new InvalidOperationException("You have already approved this action.");
                    return await Conflict(actionId, ActionStatus.Pending, ct);
                }
                // Not enough approvals yet (recorded, still waiting) is returned as it is.
                if (approved.Status == ActionStatus.Pending) return approved;
                LeftPending(approved);
                await Publish(ActionEventKind.Approved, approved, null, ct);
                return await executor.ExecuteAsync(actionId, ct);
            }
            case ReviewDecision.Retry:
            {
                var retry = await ChangeAudited(actionId, ActionStatus.Failed, ActionStatus.Approved,
                    r => addReview(r) with { Attempts = r.Attempts + 1, Error = null, ResultJson = null }, EntryOf("retry_approved", review.Reviewer), ct);
                if (retry is null) return await Conflict(actionId, ActionStatus.Failed, ct);
                return await executor.ExecuteAsync(actionId, ct);
            }
            case ReviewDecision.Reject:
            case ReviewDecision.RequestChanges:
            {
                var next = review.Decision == ReviewDecision.Reject ? ActionStatus.Rejected : ActionStatus.ChangesRequested;
                var done = await ChangeAudited(actionId, ActionStatus.Pending, next,
                    r => next == ActionStatus.Rejected
                        ? addReview(r).WithResolution(ResolutionOutcomes.Denied, ResolverOf(review), null, _clock.GetUtcNow())
                        : addReview(r),
                    EntryOf(next == ActionStatus.Rejected ? AuditEvents.ApprovalDenied : "changes_requested", review.Reviewer), ct);
                if (done is null) return await Conflict(actionId, ActionStatus.Pending, ct);
                LeftPending(done);
                HostTelemetry.Count(done);
                await Publish(next == ActionStatus.Rejected ? ActionEventKind.Rejected : ActionEventKind.ChangesRequested, done, null, ct);
                if (trust is not null) await trust.DemoteIfFailedAsync(done, ct);
                if (levels is not null && next == ActionStatus.Rejected && done.Requester.Kind == RequesterKind.AiSession)
                    await levels.RejectedAsync(done.Requester.Id, done.Id, done.ToolName, ct);
                return done;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(review));
        }
    }

    /// <summary>Whether an AI reviewer may make this decision (<see cref="AiReviewRules"/>), with the history that needs the store: AI
    /// refusals along the resubmission chain and AI approvals within the tool's <c>AiBudget</c>. The runner asks this before offering.</summary>
    public async Task<ReviewVerdict> AiVerdictAsync(ActionRecord r, ReviewDecision decision, bool editsArguments = false, CancellationToken ct = default)
    {
        // P0 (F9): without a policy snapshot an AI reviewer would be judged by defaults. It decides nothing about such an action.
        if (r.PolicySnapshot is null) return ReviewVerdict.No("This action has no policy snapshot, so an AI reviewer cannot decide it.");
        ToolRisk? risk = catalog.TryGet(r.ToolName, out var tool) ? tool.Descriptor.Risk : null;
        var quick = AiReviewRules.Check(r, decision, risk, editsArguments, null);
        return quick.Allowed ? AiReviewRules.Check(r, decision, risk, editsArguments, await AiHistoryAsync(r, ct)) : quick;
    }

    private async Task<AiReviewHistory> AiHistoryAsync(ActionRecord r, CancellationToken ct)
    {
        var refusals = r.Reviews.Count(AiReviewRules.IsAiRefusal);
        var parent = r.ParentActionId;
        for (var hops = 0; parent is not null && hops < 100; hops++)
        {
            if (await store.GetAsync(parent, ct) is not { } p) break;
            refusals += p.Reviews.Count(AiReviewRules.IsAiRefusal);
            parent = p.ParentActionId;
        }

        var approvals = 0;
        if (r.PolicySnapshot?.AiBudget is { MaxCount: > 0 } budget)
        {
            // Counted when the AI decides, not in the store's transaction: two AI reviewers deciding at the same instant could both get
            // the last approval. One runner per Host is the supported shape.
            var since = _clock.GetUtcNow() - budget.Per;
            var recent = await store.QueryAsync(new ActionQuery
            {
                ToolName = r.ToolName,
                RequesterId = budget.Scope == BudgetScope.Requester ? r.Requester.Id : null,
                UpdatedSince = since,
                Limit = int.MaxValue,
            }, ct);
            approvals = recent.Sum(a => a.Reviews.Count(v => AiReviewRules.IsAiApproval(v) && v.At >= since));
        }
        return new AiReviewHistory(refusals, approvals);
    }

    public async Task<ActionRecord> CancelAsync(string actionId, ActionRequester requester, CancellationToken ct = default)
    {
        var current = await store.GetAsync(actionId, ct) ?? throw new KeyNotFoundException(actionId);
        if (current.Requester != requester)
            throw new UnauthorizedAccessException("Only the requester can cancel an action.");
        var done = await ChangeAudited(actionId, ActionStatus.Pending, ActionStatus.Cancelled,
            r => r.WithResolution(ResolutionOutcomes.Cancelled, r.Requester.Id, null, _clock.GetUtcNow()),
            EntryOf(AuditEvents.ApprovalCancelled, null), ct);
        if (done is null) return await Conflict(actionId, ActionStatus.Pending, ct);
        LeftPending(done);
        HostTelemetry.Count(done);
        await Publish(ActionEventKind.Cancelled, done, null, ct);
        return done;
    }

    /// <summary>Edits need both the policy the action was queued under and today's policy to allow them.</summary>
    private bool EditsAllowed(ActionRecord r) =>
        (r.PolicySnapshot?.AllowEdits ?? true)
        && !(policy.Config.Tools.TryGetValue(r.ToolName, out var current) && !current.AllowEdits)
        && !(catalog.TryGet(r.ToolName, out var tool) && policy.IsAdministration(tool.Descriptor));

    /// <summary>Resubmit after ChangesRequested: a new action linked to the old one, same requester only.</summary>
    public async Task<ActionRecord> ResubmitAsync(
        string actionId, string? editedArgumentsJson, ActionRequester requester, CancellationToken ct = default, string? profile = null)
    {
        var parent = await store.GetAsync(actionId, ct) ?? throw new KeyNotFoundException(actionId);
        if (parent.Requester != requester)
            throw new UnauthorizedAccessException("Only the requester can resubmit an action.");
        if (parent.Status != ActionStatus.ChangesRequested)
            throw new InvalidOperationException($"Action is {parent.Status}; only ChangesRequested can be resubmitted.");
        return await SubmitAsync(new ActionRequest(
            parent.ToolName, editedArgumentsJson ?? parent.ArgumentsJson, requester,
            parent.SessionId, null, parent.Id) { Profile = profile }, ct);
    }

    /// <summary>
    /// Runs delayed actions whose time has come (a system approval, audited as delay_elapsed). One far past its time (more than its
    /// delay again, after an outage) is held for a person instead, so a backlog of stale actions does not fire at once. Returns how many ran.
    /// </summary>
    public async Task<int> RunDueDelayedAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var ran = 0;
        foreach (var r in await store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, Limit = int.MaxValue }, ct))
        {
            if (r.RunAt is not { } at || at > now) continue;
            var delay = at - r.CreatedAt;
            if (now - at > delay)
            {
                await ChangeAudited(r.Id, ActionStatus.Pending, ActionStatus.Pending, x => x with { RunAt = null },
                    EntryOf("delay_held", $"{(now - at).TotalMinutes:0} min past its time: a person decides"), ct);
                continue;
            }
            var approved = await ChangeAudited(r.Id, ActionStatus.Pending, ActionStatus.Approved, x => (x with
            {
                Reviews = [.. x.Reviews, new ActionReview("system", ReviewDecision.Approve, "Undo window elapsed; nobody stopped it.", now)],
            }).WithResolution(ResolutionOutcomes.Allowed, "system", null, now), EntryOf("delay_elapsed", null), ct);
            if (approved is null) continue;
            LeftPending(approved);
            if (await executor.ClaimAndRunAsync(approved, countAttempt: true, ct) is null) continue;
            ran++;
        }
        return ran;
    }

    /// <summary>
    /// Stops a delayed action before it runs. Anyone allowed to review it may stop it (the reviewer group, when the action has one), and
    /// so may the requester; stopping needs no second person, because stopping is always the safe direction.
    /// </summary>
    public async Task<ActionRecord> StopAsync(string actionId, string stoppedBy, IReadOnlyList<string> groups, CancellationToken ct = default)
    {
        var r = await store.GetAsync(actionId, ct) ?? throw new KeyNotFoundException(actionId);
        if (r.Status != ActionStatus.Pending || r.RunAt is null) throw new InvalidOperationException("Only a delayed action that has not run yet can be stopped.");
        var mayStop = stoppedBy == r.Requester.Id || r.ReviewerGroup is null || groups.Contains(r.ReviewerGroup, StringComparer.Ordinal);
        if (!mayStop) throw new UnauthorizedAccessException($"Only the requester or the '{r.ReviewerGroup}' group can stop this action.");
        var done = await ChangeAudited(actionId, ActionStatus.Pending, ActionStatus.Cancelled, x => (x with
        {
            Reviews = [.. x.Reviews, new ActionReview(stoppedBy, ReviewDecision.Reject, "Stopped during the undo window.", _clock.GetUtcNow())],
        }).WithResolution(ResolutionOutcomes.Cancelled, stoppedBy, "stopped", _clock.GetUtcNow()),
            EntryOf("stopped", stoppedBy), ct) ?? throw new InvalidOperationException("The action has already run or been decided.");
        LeftPending(done);
        HostTelemetry.Count(done);
        await Publish(ActionEventKind.Cancelled, done, "stopped", ct);
        return done;
    }

    /// <summary>Moves overdue Pending actions to Expired. Returns how many.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var pending = await store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, Limit = int.MaxValue }, ct);
        var count = 0;
        foreach (var r in pending.Where(p => p.ExpiresAt is { } at && at <= now))
        {
            var done = await ChangeAudited(r.Id, ActionStatus.Pending, ActionStatus.Expired,
                x => x.WithResolution(ResolutionOutcomes.Expired, "system", ActionExecutor.ReasonApprovalExpired, now),
                EntryOf(AuditEvents.ApprovalExpired, null), ct);
            if (done is null) continue;
            count++;
            LeftPending(done);
            HostTelemetry.Count(done);
            await Publish(ActionEventKind.Expired, done, null, ct);
        }
        return count;
    }

    private Task Publish(ActionEventKind kind, ActionRecord r, string? detail, CancellationToken ct) =>
        events is null ? Task.CompletedTask : events.PublishAsync(kind, r, detail, ct);

    private void LeftPending(ActionRecord r)
    {
        HostTelemetry.Pending.Add(-1);
        HostTelemetry.TimeToDecision.Record((r.UpdatedAt - r.CreatedAt).TotalSeconds,
            new KeyValuePair<string, object?>("tool", r.ToolName),
            new KeyValuePair<string, object?>("status", r.Status.ToString()));
    }

    private async Task<ActionRecord> Conflict(string id, ActionStatus expected, CancellationToken ct)
    {
        var now = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException(id);
        throw new InvalidOperationException($"Action {id} is {now.Status}, expected {expected}.");
    }

    private static ActionRecord Base(ActionRequest r, string id, string hash, DateTimeOffset now) => new()
    {
        Id = id,
        ToolName = r.ToolName,
        ModuleId = "",
        ArgumentsJson = r.ArgumentsJson,
        ArgumentsHash = hash,
        Requester = r.Requester,
        Status = ActionStatus.Pending,
        PolicyMode = ToolMode.Deny,
        CreatedAt = now,
        UpdatedAt = now,
        SessionId = r.SessionId,
        IdempotencyKey = r.IdempotencyKey,
        ParentActionId = r.ParentActionId,
        Profile = r.Profile,
    };

    private static string? RequestedTool(string argumentsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            return doc.RootElement.TryGetProperty("tool", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string Hash(string json) => ArgumentDigest.Of(json);

    /// <summary>Who a resolution names for a reviewer's decision: the authenticated subject, or the display name when there is none.</summary>
    private static string ResolverOf(ActionReview review) => review.Subject ?? review.Reviewer;

    /// <summary>The binding digest of a new action (<see cref="ActionBinding"/>). Null when the tool is unknown (it has no schema to bind
    /// to) or the arguments are not JSON (they could not run anyway).</summary>
    private string? BindingDigestOf(ActionRequest r, string policyVersion) =>
        ActionBindings.Digest(r.Requester.Id, r.ToolName, catalog.SchemaVersionOf(r.ToolName), r.ArgumentsJson, policyVersion);

    private Task Audit(string evt, ActionRecord r, string? detail, CancellationToken ct) =>
        audit.AppendAsync(Entry(evt, r, detail), ct);

    /// <summary>The audit entry of a change, stamped when it is written (the fields <see cref="Audit(string, ActionRecord, string?, CancellationToken)"/> writes).</summary>
    private AuditEntry Entry(string evt, ActionRecord r, string? detail) =>
        new(_clock.GetUtcNow(), evt, r.Id, r.ToolName, r.Requester.Id, detail);

    private Func<ActionRecord, AuditEntry> EntryOf(string evt, string? detail) => r => Entry(evt, r, detail);

    /// <summary>A change to an action and the audit entry that records it: one transaction when the trail is the store's own database, so the
    /// change cannot happen without its entry, nor the entry without the change. Otherwise the change is applied, then its entry is written.</summary>
    private Task<ActionRecord?> ChangeAudited(string id, ActionStatus expected, ActionStatus next, Func<ActionRecord, ActionRecord> update,
        Func<ActionRecord, AuditEntry> entry, CancellationToken ct) =>
        AtomicTrail.ChangeAsync(store, audit, id, expected, next, update, entry, ct);

    /// <summary>A refused request is stored as a denied action, and the denial is written in the same transaction.</summary>
    private async Task<ActionRecord> CreateDenied(ActionRecord record, string detail, CancellationToken ct) =>
        await CreateAudited(record, [], EntryOf("denied", detail), ct) ?? throw new InvalidOperationException("A denied action could not be stored.");

    /// <summary>As <see cref="ChangeAudited"/>, for a change whose next status is decided inside the store's transaction.</summary>
    private Task<ActionRecord?> DecideAudited(string id, ActionStatus expected, Func<ActionRecord, ActionTransition?> decide,
        Func<ActionRecord, AuditEntry> entry, CancellationToken ct) =>
        AtomicTrail.DecideAsync(store, audit, id, expected, decide, entry, ct);

    /// <summary>Creates an action and writes its entry in the same transaction when the trail is the store's own database. An idempotent repeat
    /// returns the existing action and writes nothing, since nothing changed; a refusal returns null and writes nothing.</summary>
    private Task<ActionRecord?> CreateAudited(ActionRecord record, IReadOnlyList<Admission> admissions,
        Func<ActionRecord, AuditEntry> entry, CancellationToken ct) =>
        AtomicTrail.CreateAsync(store, audit, record, admissions, entry, ct);

    private Task Audit(string evt, string id, ActionRequest req, string tool, string detail, DateTimeOffset at, CancellationToken ct) =>
        audit.AppendAsync(new AuditEntry(at, evt, id, tool, req.Requester.Id, detail), ct);
}
