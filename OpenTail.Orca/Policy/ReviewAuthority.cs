namespace OpenTail.Orca;

/// <summary>
/// Which subjects are the same human. Sign-in mechanisms produce different, namespaced subjects (<c>admin:alice</c>, <c>jwt:...|123</c>,
/// <c>key:alices-agent</c>); the operator says who is who in the policy file's <c>People</c> map, and self-approval is judged on that.
/// </summary>
public sealed class PeopleMap
{
    private readonly Dictionary<string, string> _personOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _subjects = new(StringComparer.Ordinal);

    public PeopleMap(IReadOnlyDictionary<string, List<string>>? people)
    {
        if (people is null) return;
        foreach (var (person, subjects) in people)
        {
            _subjects[person] = subjects.Select(CanonicalSubject).ToList();
            foreach (var s in subjects) _personOf[CanonicalSubject(s)] = person;
        }
    }

    /// <summary>The form a subject is compared in. A Teams subject (<c>teams:&lt;object id&gt;</c>) is case-insensitive, because an object id
    /// is: the bot stores and matches it in lower case, so the policy's copy must be too. Other subjects compare as written.</summary>
    public static string CanonicalSubject(string subject) =>
        subject.StartsWith("teams:", StringComparison.Ordinal) ? "teams:" + subject[6..].ToLowerInvariant() : subject;

    /// <summary>Every subject of the person <paramref name="subject"/> belongs to (just the subject when the operator did not say): how the
    /// Host finds the Teams chat of the person whose AI asked.</summary>
    public IReadOnlyList<string> SubjectsOfSamePerson(string subject) =>
        _personOf.TryGetValue(CanonicalSubject(subject), out var p) ? _subjects[p] : [CanonicalSubject(subject)];

    /// <summary>The person a subject belongs to, or the (canonical) subject itself when the operator did not say.</summary>
    public string PersonOf(string subject) =>
        _personOf.TryGetValue(CanonicalSubject(subject), out var p) ? "person:" + p : CanonicalSubject(subject);

    public bool Same(string a, string b) => PersonOf(a) == PersonOf(b);
}

public sealed record ReviewVerdict(bool Allowed, string? Reason)
{
    public static ReviewVerdict Yes { get; } = new(true, null);
    public static ReviewVerdict No(string reason) => new(false, reason);
}

/// <summary>
/// Who may decide an action, and how approvals add up. These are pure functions of the stored action and the (server-authenticated)
/// reviewer, used both by the queue, which enforces them, and by the approval page, which shows what a reviewer can do.
/// </summary>
public static class ReviewAuthority
{
    /// <summary>May this reviewer make this decision on this action?</summary>
    public static ReviewVerdict Check(ActionRecord r, ReviewDecision decision, string? subject, IReadOnlyList<string> groups, PeopleMap people)
    {
        var policy = r.PolicySnapshot;
        var isAi = subject is not null && Subjects.IsAi(subject);
        var refusal = IsRefusal(decision);

        // A non-human reviewer decides what the policy hands it: approvals under the rules below, and refusals where AiMayRefuse says so.
        if (isAi)
        {
            // D-21 (P2): an AI refuses only where the policy says AiMayRefuse. Without a snapshot the AI limits refuse it anyway (D-34).
            if (refusal && policy is not null && !policy.AiMayRefuse)
                return ReviewVerdict.No("An AI reviewer may not refuse this action: its policy does not say AiMayRefuse.");
            // D-26 (P2): a refusal may cross the Human-only and reviewer-group boundaries; an approval never does (rows A1 and A2).
            if (!refusal && policy is not null && policy.Reviewer == ReviewerKind.Human)
                return ReviewVerdict.No("This action needs a human reviewer.");
            if (!refusal && r.ReviewerGroup is { } reviewerGroup)
                return ReviewVerdict.No($"This action needs a reviewer in the '{reviewerGroup}' group; an AI reviewer cannot stand in.");
        }

        // D-19 (P1): an `Ai` action is decided by an AI reviewer. A person may refuse it (reject or ask for changes) but not approve or retry
        // it; `Either` is the policy that lets a person approve. An unknown identity counts as a person here: it is refused.
        if (!isAi && policy?.Reviewer == ReviewerKind.Ai && decision is ReviewDecision.Approve or ReviewDecision.Retry)
            return ReviewVerdict.No("This action is decided by an AI reviewer: a person may refuse it, but not approve it. Its policy would need Either for a person to approve.");

        // D-37 (P1): an AI reviewer may not approve a request from its own identity, or from its own person (the People map). That is four eyes
        // for AI. Refusals are not affected: a refusal cannot run anything.
        if (isAi && subject is not null && (decision is ReviewDecision.Approve or ReviewDecision.Retry)
            && (subject == r.Requester.Id || people.Same(subject, r.Requester.Id)))
            return ReviewVerdict.No("An AI reviewer may not approve a request from its own identity or its own person.");

        // A person outside the group is refused, whatever the decision. (An AI never stands in a group for an approval: above.)
        if (!isAi && r.ReviewerGroup is { } group && !groups.Contains(group, StringComparer.Ordinal))
            return ReviewVerdict.No($"Only reviewers in the '{group}' group may decide this action.");

        if (decision is ReviewDecision.Approve or ReviewDecision.Retry)
        {
            if (policy?.SelfApproval == SelfApproval.Forbid)
            {
                if (subject is null) return ReviewVerdict.No("This action forbids approving your own request, and your identity is not known.");
                if (people.Same(subject, r.Requester.Id)) return ReviewVerdict.No("You cannot approve your own request.");
            }
        }

        if (r.RequiredApprovals > 1 && subject is null)
            return ReviewVerdict.No("This action needs several approvers, and your identity is not known.");

        return ReviewVerdict.Yes;
    }

    /// <summary>Is this decision a refusal (reject or ask for changes) rather than an approval? Refusals run nothing (D-21, D-26).</summary>
    public static bool IsRefusal(ReviewDecision decision) => decision is ReviewDecision.Reject or ReviewDecision.RequestChanges;

    /// <summary>Distinct approvals the action has right now (since the last edit of its arguments).</summary>
    public static int Approvals(ActionRecord r, PeopleMap people)
    {
        var lastEdit = -1;
        for (var i = 0; i < r.Reviews.Count; i++) if (r.Reviews[i].EditedArgumentsJson is not null) lastEdit = i;
        return r.Reviews.Skip(Math.Max(lastEdit, 0))
            .Where(a => a.Decision is ReviewDecision.Approve or ReviewDecision.Retry && (r.RequiredApprovals == 1 || a.Subject is null || !Subjects.IsAi(a.Subject)))
            .Select(a => people.PersonOf(a.Subject ?? "name:" + a.Reviewer)).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>How many distinct approvals the action has, counting the incoming one.</summary>
    /// <remarks>
    /// Approvals given before a reviewer edited the arguments do not count (they approved different arguments). An AI reviewer's approval
    /// counts only when one approval is enough: it never makes up numbers in a several-approver action. The same person approving twice
    /// counts once.
    /// </remarks>
    public static (int Count, bool Duplicate) Tally(ActionRecord r, ActionReview incoming, PeopleMap people)
    {
        var lastEdit = -1;
        for (var i = 0; i < r.Reviews.Count; i++) if (r.Reviews[i].EditedArgumentsJson is not null) lastEdit = i;

        var prior = incoming.EditedArgumentsJson is not null
            ? new List<ActionReview>()                                   // the incoming edit voids everything before it
            : r.Reviews.Skip(Math.Max(lastEdit, 0)).ToList();

        string Key(ActionReview a) => people.PersonOf(a.Subject ?? "name:" + a.Reviewer);
        bool Counts(ActionReview a) =>
            a.Decision is ReviewDecision.Approve or ReviewDecision.Retry
            && (r.RequiredApprovals == 1 || a.Subject is null || !Subjects.IsAi(a.Subject));

        var approvers = prior.Where(Counts).Select(Key).ToHashSet(StringComparer.Ordinal);
        var duplicate = Counts(incoming) && approvers.Contains(Key(incoming));
        if (Counts(incoming)) approvers.Add(Key(incoming));
        return (approvers.Count, duplicate);
    }
}
