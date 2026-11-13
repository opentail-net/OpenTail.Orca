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
        RuleBudget? budget = null, ToolRisk? ceiling = null) =>
        new() { Mode = ToolMode.Queue, Reviewer = reviewer, SelfApproval = self, AiBudget = budget, AiMaxRisk = ceiling };

    // ---- Table A: ReviewAuthority.Check ----

    [Fact]
    public void A1_an_ai_is_refused_a_human_only_action_whatever_the_decision()
    {
        var v = ReviewAuthority.Check(Rec(P(ReviewerKind.Human)), ReviewDecision.Reject, "ai:bot", [], NoPeople);
        Assert.False(v.Allowed);
        Assert.Contains("needs a human reviewer", v.Reason);
    }

    [Fact]
    public void A2_an_ai_is_refused_a_reviewer_group_action_whatever_the_decision()
    {
        var v = ReviewAuthority.Check(Rec(P(ReviewerKind.Ai), group: "finance"), ReviewDecision.Reject, "ai:bot", [], NoPeople);
        Assert.False(v.Allowed);
        Assert.Contains("an AI reviewer cannot stand in", v.Reason);
    }

    [Fact]
    public void A3_a_person_outside_the_reviewer_group_is_refused_and_a_member_is_allowed()
    {
        var outside = ReviewAuthority.Check(Rec(P(ReviewerKind.Ai), group: "finance"), ReviewDecision.Approve, "admin:alice", [], NoPeople);
        Assert.False(outside.Allowed);
        Assert.Contains("'finance' group", outside.Reason);
        var member = ReviewAuthority.Check(Rec(P(ReviewerKind.Ai), group: "finance"), ReviewDecision.Approve, "admin:alice", ["finance"], NoPeople);
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

    [Fact]
    public void Ai_and_Either_behave_the_same_for_a_human_today()
    {
        // F15: ReviewAuthority does not tell Ai and Either apart for a person. D-28 changes this in P1.
        var ai = ReviewAuthority.Check(Rec(P(ReviewerKind.Ai)), ReviewDecision.Approve, "admin:alice", [], NoPeople);
        var either = ReviewAuthority.Check(Rec(P(ReviewerKind.Either)), ReviewDecision.Approve, "admin:alice", [], NoPeople);
        Assert.Equal(either.Allowed, ai.Allowed);
        Assert.True(ai.Allowed);
    }

    // ---- Table B: AiReviewRules.Check ----

    [Fact]
    public void B1_an_ai_never_decides_a_destructive_action_even_to_reject()
    {
        Assert.Contains("Destructive", AiReviewRules.Check(Rec(P(ReviewerKind.Ai)), ReviewDecision.Reject, ToolRisk.Destructive, false, null).Reason);
        Assert.False(AiReviewRules.Check(Rec(P(ReviewerKind.Ai)), ReviewDecision.Approve, ToolRisk.Destructive, false, null).Allowed);
    }

    [Fact]
    public void B2_above_the_default_ceiling_an_ai_may_not_decide_even_to_reject()
    {
        var rec = Rec(P(ReviewerKind.Ai));
        Assert.False(AiReviewRules.Check(rec, ReviewDecision.Approve, ToolRisk.ExternalEffect, false, null).Allowed);
        var reject = AiReviewRules.Check(rec, ReviewDecision.Reject, ToolRisk.ExternalEffect, false, null);
        Assert.False(reject.Allowed);
        Assert.Contains("above what an AI reviewer may decide", reject.Reason);
    }

    [Fact]
    public void B3_a_tainted_external_action_is_a_persons_decision_even_when_the_ceiling_allows_it()
    {
        var rec = Rec(P(ReviewerKind.Ai, ceiling: ToolRisk.ExternalEffect), tainted: true);
        Assert.Contains("a person decides", AiReviewRules.Check(rec, ReviewDecision.Approve, ToolRisk.ExternalEffect, false, null).Reason);
        Assert.Contains("a person decides", AiReviewRules.Check(rec, ReviewDecision.Reject, ToolRisk.ExternalEffect, false, null).Reason);
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
        Assert.Contains("a person decides the rest", spent.Reason);
    }

    [Fact]
    public void B8_otherwise_an_ai_may_approve_a_write_within_its_ceiling()
    {
        Assert.True(AiReviewRules.Check(Rec(P(ReviewerKind.Ai)), ReviewDecision.Approve, ToolRisk.Write, false, null).Allowed);
    }
}
