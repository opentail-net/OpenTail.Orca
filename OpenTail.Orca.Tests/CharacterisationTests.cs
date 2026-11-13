namespace OpenTail.Orca.Tests;

/// <summary>P0 of the reviewer gate plan: pins table A (ReviewAuthority) and table B (AiReviewRules) row by row, as the code is today.</summary>
public class CharacterisationTests
{
    private static readonly ActionRequester Bot = new(RequesterKind.AiSession, "key:bot");
    private static readonly PeopleMap NoPeople = new(null);

    private static ActionRecord Rec(ToolPolicy? policy = null, bool tainted = false, string? group = null, int required = 1) => new()
    {
        Id = "act_" + Guid.NewGuid().ToString("N"),
        ToolName = "mail.send",
        ModuleId = "fake",
        ArgumentsJson = "{}",
        ArgumentsHash = "h",
        Requester = Bot,
        Status = ActionStatus.Pending,
        PolicyMode = ToolMode.Queue,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        PolicySnapshot = policy,
        Tainted = tainted,
        ReviewerGroup = group,
        RequiredApprovals = required,
    };

    private static ToolPolicy P(ReviewerKind reviewer = ReviewerKind.Human, SelfApproval self = SelfApproval.Allow,
        RuleBudget? budget = null, ToolRisk? ceiling = null, bool refuse = false) =>
        new() { Mode = ToolMode.Queue, Reviewer = reviewer, SelfApproval = self, AiBudget = budget, AiMaxRisk = ceiling, AiMayRefuse = refuse };

    // ---- Table A: ReviewAuthority.Check ----

    [Fact]
    public void A1_an_ai_is_refused_an_approval_of_a_human_only_action()
    {
        var v = ReviewAuthority.Check(Rec(P(ReviewerKind.Human)), ReviewDecision.Approve, "ai:bot", [], NoPeople);
        Assert.False(v.Allowed);
        Assert.Contains("needs a human reviewer", v.Reason);
    }

    // P2 (D-21, D-26): an AI refuses a human-only action only where AiMayRefuse says so. An approval is never allowed.
    [Fact]
    public void A1_an_ai_may_refuse_a_human_only_action_only_where_aimayrefuse_says_so()
    {
        Assert.Contains("may not refuse", ReviewAuthority.Check(Rec(P(ReviewerKind.Human)), ReviewDecision.Reject, "ai:bot", [], NoPeople).Reason);
        Assert.True(ReviewAuthority.Check(Rec(P(ReviewerKind.Human, refuse: true)), ReviewDecision.Reject, "ai:bot", [], NoPeople).Allowed);
        Assert.True(ReviewAuthority.Check(Rec(P(ReviewerKind.Human, refuse: true)), ReviewDecision.RequestChanges, "ai:bot", [], NoPeople).Allowed);
        Assert.False(ReviewAuthority.Check(Rec(P(ReviewerKind.Human, refuse: true)), ReviewDecision.Approve, "ai:bot", [], NoPeople).Allowed);
    }

    [Fact]
    public void A2_an_ai_is_refused_an_approval_of_a_reviewer_group_action()
    {
        var v = ReviewAuthority.Check(Rec(P(ReviewerKind.Ai), group: "finance"), ReviewDecision.Approve, "ai:bot", [], NoPeople);
        Assert.False(v.Allowed);
        Assert.Contains("an AI reviewer cannot stand in", v.Reason);
    }

    [Fact]
    public void A2_an_ai_may_refuse_a_reviewer_group_action_where_aimayrefuse_says_so()
    {
        Assert.Contains("may not refuse", ReviewAuthority.Check(Rec(P(ReviewerKind.Ai), group: "finance"), ReviewDecision.Reject, "ai:bot", [], NoPeople).Reason);
        Assert.True(ReviewAuthority.Check(Rec(P(ReviewerKind.Ai, refuse: true), group: "finance"), ReviewDecision.Reject, "ai:bot", [], NoPeople).Allowed);
    }

    [Fact]
    public void A3_a_person_outside_the_reviewer_group_is_refused_and_a_member_is_allowed()
    {
        // Either, not Ai: an Ai action refuses a person's approval before the group is looked at (A8, D-19).
        var outside = ReviewAuthority.Check(Rec(P(ReviewerKind.Either), group: "finance"), ReviewDecision.Approve, "admin:alice", [], NoPeople);
        Assert.False(outside.Allowed);
        Assert.Contains("'finance' group", outside.Reason);
        var member = ReviewAuthority.Check(Rec(P(ReviewerKind.Either), group: "finance"), ReviewDecision.Approve, "admin:alice", ["finance"], NoPeople);
        Assert.True(member.Allowed);
    }

    [Fact]
    public void A4_under_four_eyes_an_approval_with_an_unknown_identity_is_refused()
    {
        var v = ReviewAuthority.Check(Rec(P(ReviewerKind.Either, SelfApproval.Forbid)), ReviewDecision.Approve, null, [], NoPeople);
        Assert.False(v.Allowed);
        Assert.Contains("your identity is not known", v.Reason);
    }

    [Fact]
    public void A5_under_four_eyes_the_requesters_own_person_cannot_approve()
    {
        var people = new PeopleMap(new Dictionary<string, List<string>> { ["bot-owner"] = ["key:bot", "admin:bot"] });
        var v = ReviewAuthority.Check(Rec(P(ReviewerKind.Either, SelfApproval.Forbid)), ReviewDecision.Approve, "admin:bot", [], people);
        Assert.False(v.Allowed);
        Assert.Contains("cannot approve your own request", v.Reason);
    }

    [Fact]
    public void A6_a_several_approver_action_needs_an_identity_for_each_approval()
    {
        var v = ReviewAuthority.Check(Rec(P(ReviewerKind.Either), required: 2), ReviewDecision.Approve, null, [], NoPeople);
        Assert.False(v.Allowed);
        Assert.Contains("several approvers", v.Reason);
    }

    [Fact]
    public void A7_otherwise_a_person_may_decide_an_ordinary_action()
    {
        Assert.True(ReviewAuthority.Check(Rec(P()), ReviewDecision.Approve, "admin:alice", [], NoPeople).Allowed);
    }

    // A8 (D-19, D-28; P1): an Ai action is decided by an AI. A person may refuse it but not approve or retry it. Either lets a person approve.
    // This row replaces the P0 pin that Ai and Either behaved the same for a person (F15).
    [Fact]
    public void A8_a_person_may_not_approve_or_retry_an_ai_action_but_may_refuse_it()
    {
        var ai = Rec(P(ReviewerKind.Ai));
        var approve = ReviewAuthority.Check(ai, ReviewDecision.Approve, "admin:alice", [], NoPeople);
        Assert.False(approve.Allowed);
        Assert.Contains("decided by an AI reviewer", approve.Reason);
        Assert.False(ReviewAuthority.Check(ai, ReviewDecision.Retry, "admin:alice", [], NoPeople).Allowed);
        Assert.True(ReviewAuthority.Check(ai, ReviewDecision.Reject, "admin:alice", [], NoPeople).Allowed);
        Assert.True(ReviewAuthority.Check(ai, ReviewDecision.RequestChanges, "admin:alice", [], NoPeople).Allowed);
    }

    [Fact]
    public void A8_an_either_action_takes_a_persons_approval_and_an_ai_approval_alike()
    {
        var either = Rec(P(ReviewerKind.Either));
        Assert.True(ReviewAuthority.Check(either, ReviewDecision.Approve, "admin:alice", [], NoPeople).Allowed);
        Assert.True(ReviewAuthority.Check(either, ReviewDecision.Approve, "ai:bot", [], NoPeople).Allowed);
    }

    [Fact]
    public void A8_an_unknown_identity_is_a_person_for_an_ai_action_and_is_refused_an_approval()
    {
        Assert.False(ReviewAuthority.Check(Rec(P(ReviewerKind.Ai)), ReviewDecision.Approve, null, [], NoPeople).Allowed);
    }

    // ---- Table B: AiReviewRules.Check ----

    [Fact]
    public void B1_an_ai_never_approves_a_destructive_action_but_may_refuse_it()
    {
        Assert.Contains("Destructive", AiReviewRules.Check(Rec(P(ReviewerKind.Ai)), ReviewDecision.Approve, ToolRisk.Destructive, false, null).Reason);
        Assert.True(AiReviewRules.Check(Rec(P(ReviewerKind.Ai)), ReviewDecision.Reject, ToolRisk.Destructive, false, null).Allowed);
    }

    [Fact]
    public void B2_above_the_default_ceiling_an_ai_may_not_approve_but_may_refuse()
    {
        var rec = Rec(P(ReviewerKind.Ai));
        var approve = AiReviewRules.Check(rec, ReviewDecision.Approve, ToolRisk.ExternalEffect, false, null);
        Assert.False(approve.Allowed);
        Assert.Contains("above what an AI reviewer may approve", approve.Reason);
        Assert.True(AiReviewRules.Check(rec, ReviewDecision.Reject, ToolRisk.ExternalEffect, false, null).Allowed);
    }

    [Fact]
    public void B3_a_tainted_external_action_is_not_approved_by_an_ai_but_may_be_refused()
    {
        var rec = Rec(P(ReviewerKind.Ai, ceiling: ToolRisk.ExternalEffect), tainted: true);
        Assert.Contains("may not decide this", AiReviewRules.Check(rec, ReviewDecision.Approve, ToolRisk.ExternalEffect, false, null).Reason);
        Assert.True(AiReviewRules.Check(rec, ReviewDecision.Reject, ToolRisk.ExternalEffect, false, null).Allowed);
    }

    [Fact]
    public void B4_after_the_refusal_limit_an_ai_may_not_decide_even_to_reject()
    {
        var rec = Rec(P(ReviewerKind.Ai));
        Assert.True(AiReviewRules.Check(rec, ReviewDecision.Reject, ToolRisk.Write, false, new AiReviewHistory(1, 0)).Allowed);
        var atLimit = AiReviewRules.Check(rec, ReviewDecision.Reject, ToolRisk.Write, false, new AiReviewHistory(2, 0));
        Assert.False(atLimit.Allowed);
        Assert.Contains("turned this request down", atLimit.Reason);
    }

    [Fact]
    public void B5_an_approval_may_not_change_the_arguments_but_a_rejection_may_carry_edits()
    {
        var rec = Rec(P(ReviewerKind.Ai));
        Assert.Contains("cannot change the arguments", AiReviewRules.Check(rec, ReviewDecision.Approve, ToolRisk.Write, true, null).Reason);
        Assert.True(AiReviewRules.Check(rec, ReviewDecision.Reject, ToolRisk.Write, true, null).Allowed);
    }

    [Fact]
    public void B6_under_four_eyes_an_ai_may_reject_but_not_approve()
    {
        var rec = Rec(P(ReviewerKind.Ai, SelfApproval.Forbid));
        Assert.Contains("four eyes", AiReviewRules.Check(rec, ReviewDecision.Approve, ToolRisk.Write, false, null).Reason);
        Assert.True(AiReviewRules.Check(rec, ReviewDecision.Reject, ToolRisk.Write, false, null).Allowed);
    }

    [Fact]
    public void B7_the_ai_approval_budget_is_counted_per_window()
    {
        var rec = Rec(P(ReviewerKind.Ai, budget: new RuleBudget { Per = TimeSpan.FromHours(1), MaxCount = 1 }));
        Assert.True(AiReviewRules.Check(rec, ReviewDecision.Approve, ToolRisk.Write, false, new AiReviewHistory(0, 0)).Allowed);
        var spent = AiReviewRules.Check(rec, ReviewDecision.Approve, ToolRisk.Write, false, new AiReviewHistory(0, 1));
        Assert.False(spent.Allowed);
        Assert.Contains("may not approve more in this window", spent.Reason);
    }

    [Fact]
    public void B8_otherwise_an_ai_may_approve_a_write_within_its_ceiling()
    {
        Assert.True(AiReviewRules.Check(Rec(P(ReviewerKind.Ai)), ReviewDecision.Approve, ToolRisk.Write, false, null).Allowed);
    }
}
