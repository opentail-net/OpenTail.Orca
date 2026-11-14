namespace OpenTail.Orca.Tests;

/// <summary>
/// Pairwise coverage for the review authority (plan 2.2) and the AI limits (plan 2.3), batch 10. The rows are a covering array generated in this
/// file from the factors. Each row's expected result comes from an oracle written from the plan's tables, not from the code. A row where the
/// code disagrees with the plan is a finding, reported rather than adjusted here.
/// </summary>
public class Batch10PairwiseTests
{
    // ---- a covering array over factor levels: every pair of levels of every two factors appears in some row ----

    private static List<int[]> PairwiseRows(int[] levels)
    {
        var uncovered = new HashSet<(int, int, int, int)>();
        for (var i = 0; i < levels.Length; i++)
            for (var j = i + 1; j < levels.Length; j++)
                for (var a = 0; a < levels[i]; a++)
                    for (var b = 0; b < levels[j]; b++)
                        uncovered.Add((i, a, j, b));

        var all = Product(levels).ToList();
        var rows = new List<int[]>();
        while (uncovered.Count > 0)
        {
            int[]? best = null;
            var bestGain = -1;
            foreach (var row in all)
            {
                var gain = PairsOf(row).Count(p => uncovered.Contains(p));
                if (gain > bestGain) { bestGain = gain; best = row; }
            }
            rows.Add(best!);
            foreach (var p in PairsOf(best!)) uncovered.Remove(p);
        }
        return rows;
    }

    private static IEnumerable<(int, int, int, int)> PairsOf(int[] row)
    {
        for (var i = 0; i < row.Length; i++)
            for (var j = i + 1; j < row.Length; j++)
                yield return (i, row[i], j, row[j]);
    }

    private static IEnumerable<int[]> Product(int[] levels)
    {
        var row = new int[levels.Length];
        return Expand(0);

        IEnumerable<int[]> Expand(int at)
        {
            if (at == levels.Length) { yield return (int[])row.Clone(); yield break; }
            for (var v = 0; v < levels[at]; v++)
            {
                row[at] = v;
                foreach (var r in Expand(at + 1)) yield return r;
            }
        }
    }

    [Fact]
    public void The_generated_rows_cover_every_pair_of_levels()
    {
        var levels = new[] { 3, 2, 4 };
        var rows = PairwiseRows(levels);
        var covered = rows.SelectMany(PairsOf).ToHashSet();
        for (var i = 0; i < levels.Length; i++)
            for (var j = i + 1; j < levels.Length; j++)
                for (var a = 0; a < levels[i]; a++)
                    for (var b = 0; b < levels[j]; b++)
                        Assert.Contains((i, a, j, b), covered);
        Assert.True(rows.Count < levels[0] * levels[1] * levels[2]);
    }

    // ---- plan 2.2: the review authority ----

    private enum Subject { FinancePerson, OutsidePerson, Ai, Unknown }
    private enum Relation { Stranger, OwnIdentity, OwnPerson }

    private static readonly int[] AuthorityLevels = [4, 2, 2, 2, 2, 3, 2, 2];
    // subject, reviewer (0 Human, 1 Ai), AiMayRefuse, group on the action, decision (0 Approve, 1 Reject), requester relation, SelfApproval (0 Allow, 1 Forbid), approvals needed (0: one, 1: two)

    private static bool PlanAllowsAuthority(int[] row)
    {
        var subject = (Subject)row[0];
        var reviewerAi = row[1] == 1;
        var aiMayRefuse = row[2] == 1;
        var group = row[3] == 1;
        var approve = row[4] == 0;
        var relation = (Relation)row[5];
        var forbid = row[6] == 1;
        var twoApprovers = row[7] == 1;
        var aiSubject = subject == Subject.Ai;
        var humanOnly = !reviewerAi;

        // Table 2.2, row by row. Each row is a refusal; a row that does not apply does nothing.
        if (aiSubject && !approve && !aiMayRefuse) return false;                     // AI refusing, AiMayRefuse not set
        if (aiSubject && approve && humanOnly) return false;                         // AI approving a Human-only action
        if (aiSubject && approve && group) return false;                             // AI approving a reviewer-group action
        if (aiSubject && approve && relation != Relation.Stranger) return false;     // AI approving its own identity or person
        if (subject == Subject.OutsidePerson && group) return false;                 // a person outside the group, any decision
        if (approve && forbid && (subject == Subject.Unknown || (aiSubject && relation != Relation.Stranger))) return false; // four eyes
        if (twoApprovers && subject == Subject.Unknown) return false;                // several approvers, identity unknown
        return true;                                                                  // otherwise allowed
    }

    private static ActionRecord Action(ToolPolicy policy, bool tainted, string? group, int required) => new()
    {
        Id = "act_" + Guid.NewGuid().ToString("N"),
        ToolName = "mail.send",
        ModuleId = "fake",
        ArgumentsJson = "{}",
        ArgumentsHash = "h",
        Requester = new ActionRequester(RequesterKind.AiSession, "key:alice-agent"),
        Status = ActionStatus.Pending,
        PolicyMode = ToolMode.Queue,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        PolicySnapshot = policy,
        Tainted = tainted,
        ReviewerGroup = group,
        RequiredApprovals = required,
    };

    private static readonly PeopleMap People = new(new Dictionary<string, List<string>> { ["owner"] = ["ai:bot", "key:bot-agent"] });

    [Fact]
    public void The_pairwise_rows_of_the_review_authority_have_the_outcome_the_plan_gives()
    {
        var mismatches = new List<string>();
        var rows = PairwiseRows(AuthorityLevels);
        foreach (var row in rows)
        {
            var subject = (Subject)row[0];
            var relation = (Relation)row[5];
            var policy = new ToolPolicy
            {
                Mode = ToolMode.Queue,
                Reviewer = row[1] == 1 ? ReviewerKind.Ai : ReviewerKind.Human,
                AiMayRefuse = row[2] == 1,
                SelfApproval = row[6] == 1 ? SelfApproval.Forbid : SelfApproval.Allow,
                AiReviewers = ["bot"],
            };
            var group = row[3] == 1 ? "finance" : null;
            var record = Action(policy, false, group, row[7] == 1 ? 2 : 1) with
            {
                Requester = relation switch
                {
                    Relation.OwnIdentity => new ActionRequester(RequesterKind.AiSession, "ai:bot"),
                    Relation.OwnPerson => new ActionRequester(RequesterKind.AiSession, "key:bot-agent"),
                    _ => new ActionRequester(RequesterKind.AiSession, "key:alice-agent"),
                },
            };
            var (identity, groups) = subject switch
            {
                Subject.FinancePerson => ((string?)"admin:rita", (IReadOnlyList<string>)["finance"]),
                Subject.OutsidePerson => ("admin:lee", (IReadOnlyList<string>)[]),
                Subject.Ai => ("ai:bot", (IReadOnlyList<string>)[]),
                _ => (null, (IReadOnlyList<string>)[]),
            };
            var decision = row[4] == 0 ? ReviewDecision.Approve : ReviewDecision.Reject;

            var code = ReviewAuthority.Check(record, decision, identity, groups, People).Allowed;
            var plan = PlanAllowsAuthority(row);
            if (code != plan) mismatches.Add($"{Describe(row)}: plan says {(plan ? "allowed" : "refused")}, code says {(code ? "allowed" : "refused")}");
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {rows.Count} rows disagree with the plan:\n" + string.Join("\n", mismatches));
    }

    private static string Describe(int[] row) =>
        $"subject={(Subject)row[0]}, reviewer={(row[1] == 1 ? "Ai" : "Human")}, AiMayRefuse={row[2] == 1}, group={row[3] == 1}, " +
        $"decision={(row[4] == 0 ? "Approve" : "Reject")}, requester={(Relation)row[5]}, SelfApproval={(row[6] == 1 ? "Forbid" : "Allow")}, " +
        $"approvers={(row[7] == 1 ? 2 : 1)}";

    // ---- plan 2.3: the AI limits ----

    // risk (0 Read, 1 Write, 2 ExternalEffect, 3 Destructive, 4 not loaded), AiMaxRisk (0 Write, 1 ExternalEffect), tainted, decision (0 Approve,
    // 1 Reject), edits, chain refusals (0 none, 1 at the maximum of 2), SelfApproval (0 Allow, 1 Forbid), AiBudget used (0 no, 1 yes)
    private static readonly int[] LimitLevels = [5, 2, 2, 2, 2, 2, 2, 2];

    private static bool PlanAllowsLimits(int[] row)
    {
        var risk = row[0];
        var maxRisk = row[1] == 0 ? 1 : 2;   // Write or ExternalEffect, in the order Read, Write, ExternalEffect
        var tainted = row[2] == 1;
        var approve = row[3] == 0;
        var edits = row[4] == 1;
        var chainAtMax = row[5] == 1;
        var forbid = row[6] == 1;
        var budgetUsed = row[7] == 1;

        // Table 2.3, row by row.
        if (chainAtMax) return false;                                                      // any decision: refusals along the chain at the maximum
        if (approve && (risk == 3 || risk == 4)) return false;                             // Destructive, or the tool is no longer loaded
        if (approve && risk == 2 && maxRisk < 2) return false;                             // above AiMaxRisk (ExternalEffect above Write)
        if (approve && tainted && risk == 2) return false;                                 // tainted ExternalEffect
        if (approve && edits) return false;                                                // edited arguments
        if (approve && forbid) return false;                                               // SelfApproval Forbid
        if (approve && budgetUsed) return false;                                           // AiBudget used up
        return true;                                                                        // otherwise allowed
    }

    [Fact]
    public void The_pairwise_rows_of_the_ai_limits_have_the_outcome_the_plan_gives()
    {
        var mismatches = new List<string>();
        var rows = PairwiseRows(LimitLevels);
        foreach (var row in rows)
        {
            var policy = new ToolPolicy
            {
                Mode = ToolMode.Queue,
                Reviewer = ReviewerKind.Ai,
                AiMaxRisk = row[1] == 0 ? ToolRisk.Write : ToolRisk.ExternalEffect,
                AiMaxRejections = 2,
                AiBudget = new RuleBudget { Per = TimeSpan.FromHours(1), MaxCount = 1 },
                SelfApproval = row[6] == 1 ? SelfApproval.Forbid : SelfApproval.Allow,
                AiReviewers = ["bot"],
            };
            var record = Action(policy, row[2] == 1, null, 1);
            var risk = row[0] switch
            {
                0 => (ToolRisk?)ToolRisk.Read,
                1 => ToolRisk.Write,
                2 => ToolRisk.ExternalEffect,
                3 => ToolRisk.Destructive,
                _ => null,
            };
            var chain = row[5] == 1 ? 2 : 0;
            var history = new AiReviewHistory(chain, row[7] == 1 ? 1 : 0);
            var decision = row[3] == 0 ? ReviewDecision.Approve : ReviewDecision.Reject;

            var code = AiReviewRules.Check(record, decision, risk, row[4] == 1, history).Allowed;
            var plan = PlanAllowsLimits(row);
            if (code != plan) mismatches.Add($"{DescribeLimits(row)}: plan says {(plan ? "allowed" : "refused")}, code says {(code ? "allowed" : "refused")}");
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {rows.Count} rows disagree with the plan:\n" + string.Join("\n", mismatches));
    }

    private static string DescribeLimits(int[] row) =>
        $"risk={new[] { "Read", "Write", "ExternalEffect", "Destructive", "not loaded" }[row[0]]}, AiMaxRisk={(row[1] == 0 ? "Write" : "ExternalEffect")}, " +
        $"tainted={row[2] == 1}, decision={(row[3] == 0 ? "Approve" : "Reject")}, edits={row[4] == 1}, chain at max={row[5] == 1}, " +
        $"SelfApproval={(row[6] == 1 ? "Forbid" : "Allow")}, budget used={row[7] == 1}";
}
