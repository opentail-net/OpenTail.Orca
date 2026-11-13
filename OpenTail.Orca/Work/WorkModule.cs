using System.ComponentModel;

namespace OpenTail.Orca;

/// <summary>
/// The built-in module holding <c>work.delegate</c>: hand work to another agent profile. It is an ordinary tool with a policy entry,
/// so delegating to a production-capable profile can be made to queue for a person. Registered when some agent profile lists
/// <see cref="AgentProfile.MayDelegateTo"/>. Delegation only narrows: the delegated agent acts under its own identity and profile.
/// </summary>
internal sealed class WorkModule(Func<WorkService> work, PolicyConfig policy) : IHostModule
{
    public const string DelegateTool = "work.delegate";

    /// <summary>A remedy an inspector proposes: put a dead item back in the inbox. Its policy must queue for a person (enforced at load).</summary>
    public const string RedriveTool = "work.redrive";

    /// <summary>A remedy an inspector proposes: ask the credential's owner to rotate it or consent again. Queues for a person; on
    /// approval it is recorded and announced. The Host never touches the secret.</summary>
    public const string RotationTool = "work.request_rotation";

    /// <summary>Remedy tools: each must be held for a person at load (Queue, Human, SelfApproval Forbid, no Execute rule, no trust ramp).</summary>
    public static readonly IReadOnlyList<string> RemedyTools = [RedriveTool, RotationTool];

    /// <summary>Host-internal keys in <c>AIFunctionArguments.Context</c> (not part of the module contract): the caller's agent profile
    /// and session, from the stored action.</summary>
    internal const string ProfileContextKey = "opentail.internal.profile";
    internal const string SessionContextKey = "opentail.internal.session";

    public string Id => "work";

    public void Register(IToolRegistry registry)
    {
        const string description = "Hand a piece of work to another kind of agent (an agent profile your job may delegate to). " +
                                   "Returns the work item's id; park on it (waitingOn) to be woken when it is done.";
        registry.Add(new ToolDescriptor(DelegateTool, description, ToolRisk.Write, Id),
            AIFunctionFactory.Create(DelegateAsync, DelegateTool, description), new Preview());
        const string redrive = "Propose putting a dead or expired work item back in the inbox, after you found and explained the cause. " +
                               "A person decides; you never approve it yourself.";
        registry.Add(new ToolDescriptor(RedriveTool, redrive, ToolRisk.Write, Id),
            AIFunctionFactory.Create(RedriveAsync, RedriveTool, redrive), new RedrivePreview());
        const string rotation = "Propose that the owner of a credential rotates it or consents again (for an expired, revoked or refused credential). " +
                                "A person decides; you never see or touch the secret.";
        registry.Add(new ToolDescriptor(RotationTool, rotation, ToolRisk.Write, Id),
            AIFunctionFactory.Create(RequestRotation, RotationTool, rotation), new RotationPreview());
    }

    private static object RequestRotation(
        [Description("The module whose credential failed (for example 'servicenow').")] string module,
        [Description("The credential's name in that module, if known.")] string? credential,
        [Description("Why: the failure, its assessment, and what rotating would fix.")] string reason,
        AIFunctionArguments? arguments = null)
    {
        // Runs only after a person approved it: the approval and this result are the record; the owner acts outside the Host.
        if (string.IsNullOrWhiteSpace(module)) throw new ArgumentException("Name the module.");
        return new { requested = "rotation", module, credential, reason, next = "The credential's owner rotates it in the Host's configuration; a redrive follows." };
    }

    private sealed class RotationPreview : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            var args = System.Text.Json.JsonDocument.Parse(argumentsJson).RootElement;
            string? S(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            var reason = S("reason") ?? "";
            if (reason.Length > 300) reason = reason[..300] + "…";
            return Task.FromResult(new PreviewResult($"Ask the owner of {S("module")}{(S("credential") is { } c ? $" / {c}" : "")} to rotate the credential. Reason given: {reason}"));
        }
    }

    private async Task<object> RedriveAsync(
        [Description("The dead or expired work item.")] string workId,
        [Description("Why it should run again: what the cause was and why it is fixed.")] string reason,
        [Description("Optional: give it to another agent profile instead (a person checks that this does not widen access).")] string? profile = null,
        [Description("Exclude the identities that failed it (for suspicious failures).")] bool excludeFailed = false,
        [Description("Do not pass the old thread note on (for suspicious failures); the inspection's findings go instead.")] bool dropNote = false,
        AIFunctionArguments? arguments = null)
    {
        // This runs only after a person approved the proposal; the approval is on the action, and the approver is the one who redrives.
        var approver = arguments?.Context?.TryGetValue(HostContract.ActionIdContextKey, out var a) == true ? $"approved proposal {a}" : "approved proposal";
        if (profile is not null && !policy.Agents.ContainsKey(profile)) throw new ArgumentException($"'{profile}' is not an agent profile.");
        var redriven = await work().RedriveAsync(workId, approver, profile is null ? null : WorkService.ForProfile(profile), excludeFailed, dropNote);
        return redriven is null
            ? throw new ArgumentException($"{workId} is not a dead or expired work item.")
            : new { workId = redriven.Id, status = "open", @for = redriven.For, reason };
    }

    private sealed class RedrivePreview : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            var args = System.Text.Json.JsonDocument.Parse(argumentsJson).RootElement;
            string? S(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            bool B(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
            var reason = S("reason") ?? "";
            if (reason.Length > 300) reason = reason[..300] + "…";
            return Task.FromResult(new PreviewResult(
                $"Redrive {S("workId")}{(S("profile") is { } p ? $" to profile '{p}'" : "")}{(B("excludeFailed") ? ", excluding the identities that failed it" : "")}" +
                $"{(B("dropNote") ? ", without the old thread note" : "")}. Reason given: {reason}"));
        }
    }

    private async Task<object> DelegateAsync(
        [Description("What is wanted, in words the other agent can act on.")] string goal,
        [Description("The agent profile to hand it to.")] string profile,
        [Description("Optional structured inputs, as a JSON object string.")] string? inputs = null,
        [Description("Optional: the work item you are working on, so this becomes part of its thread.")] string? fromWork = null,
        [Description("Optional: when it should be done (ISO 8601).")] string? due = null,
        AIFunctionArguments? arguments = null)
    {
        var context = arguments?.Context;
        var subject = context?.TryGetValue(HostContract.RequesterContextKey, out var s) == true ? s as string : null;
        if (string.IsNullOrEmpty(subject)) throw new InvalidOperationException("No requester to delegate for.");
        var callerProfile = context?.TryGetValue(ProfileContextKey, out var p) == true ? p as string : null;
        var session = context?.TryGetValue(SessionContextKey, out var se) == true ? se as string : null;

        if (!policy.Agents.ContainsKey(profile)) throw new ArgumentException($"'{profile}' is not an agent profile.");
        // The local operator (no profile) may delegate to any profile; an agent only to those its profile lists.
        if (callerProfile is not null && !(policy.Agents.TryGetValue(callerProfile, out var own) && own.MayDelegateTo.Contains(profile, StringComparer.Ordinal)))
            throw new ArgumentException($"Your profile may not delegate to '{profile}'.");

        var service = work();
        WorkItem? parent = null;
        if (fromWork is not null)
        {
            parent = service.Store.Get(fromWork);
            if (parent is null || parent.ClaimedBy != subject || parent.Status != WorkStatus.Claimed)
                throw new ArgumentException($"You do not hold {fromWork}.");
            if (parent.Depth + 1 > service.Config.MaxDelegationDepth)
                throw new ArgumentException($"Delegation is limited to {service.Config.MaxDelegationDepth} levels; do this part yourself or hand it back.");
        }
        DateTimeOffset? dueAt = null;
        if (due is not null)
        {
            if (!DateTimeOffset.TryParse(due, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) throw new ArgumentException($"'{due}' is not a time.");
            dueAt = d;
        }
        var (tainted, carried) = service.Carry(subject, session);
        try
        {
            var item = await service.CreateAsync(goal, WorkService.ForProfile(profile), subject, inputs, parent, dueAt, tainted: tainted, carried: carried);
            return new { workId = item.Id, thread = item.Thread, @for = profile };
        }
        catch (WorkException ex) { throw new ArgumentException(ex.Message); }
    }

    private sealed class Preview : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            var args = System.Text.Json.JsonDocument.Parse(argumentsJson).RootElement;
            var to = args.TryGetProperty("profile", out var p) ? p.GetString() : "?";
            var goal = args.TryGetProperty("goal", out var g) ? g.GetString() ?? "" : "";
            if (goal.Length > 200) goal = goal[..200] + "…";
            return Task.FromResult(new PreviewResult($"Hand work to agent profile '{to}': {goal}"));
        }
    }
}
