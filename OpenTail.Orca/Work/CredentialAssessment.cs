using System.Text.Json.Nodes;

namespace OpenTail.Orca;

/// <summary>Expected, unclear or suspicious: the Host's rule-based reading of a credential failure.</summary>
public enum CredentialVerdict { Expected, Unclear, Suspicious }

/// <summary>
/// Failures design, step 4: an <c>auth</c> failure is a security question first, never given an automatic green light or red one.
/// The Host's own rules read the facts it already has and say expected, unclear or suspicious, with the evidence for each. Where
/// they say suspicious, the identity-level ladder hears it (a soft signal) and an inspector's opinion never overrides it.
/// </summary>
public static class CredentialAssessment
{
    public static readonly TimeSpan Recent = TimeSpan.FromDays(7);
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public sealed record Result(CredentialVerdict Verdict, IReadOnlyList<string> Expected, IReadOnlyList<string> Suspicious)
    {
        public JsonObject ToJson() => new()
        {
            ["verdict"] = Verdict.ToString().ToLowerInvariant(),
            ["pointsToExpected"] = new JsonArray([.. Expected.Select(e => (JsonNode)e)]),
            ["pointsToSuspicious"] = new JsonArray([.. Suspicious.Select(e => (JsonNode)e)]),
        };
    }

    /// <param name="failure">The action that failed with kind auth.</param>
    /// <param name="history">Recent actions on the Host (all identities), newest first is fine.</param>
    /// <param name="trail">The audit trail (policy loads, level changes).</param>
    public static Result Assess(ActionRecord failure, IReadOnlyList<ActionRecord> history, IReadOnlyList<AuditEntry> trail, IdentityLevel level,
        DateTimeOffset now)
    {
        var expected = new List<string>();
        var suspicious = new List<string>();
        var who = failure.Requester.Id;
        var tool = failure.ToolName;

        // Points to expected.
        if (!history.Any(a => a.ToolName == tool && a.Status == ActionStatus.Succeeded))
            expected.Add($"{tool} has never succeeded on this Host: a new or untested integration");
        if (trail.Any(e => e.Event == "policy_loaded" && e.Detail?.Contains("(new version)", StringComparison.Ordinal) == true && now - e.At <= Recent))
            expected.Add("the policy changed in the last 7 days");
        var others = history.Where(a => a.ToolName == tool && a.Requester.Id != who && now - a.CreatedAt <= Window
                                        && a.Error?.EffectiveKind == ToolFailureKind.Auth).Select(a => a.Requester.Id).Distinct().Count();
        if (others > 0) expected.Add($"{others} other identit{(others == 1 ? "y is" : "ies are")} failing on {tool} the same way: likely the provider or a shared credential");

        // Points to suspicious.
        if (history.Any(a => a.ToolName == tool && a.Requester.Id == who && a.Status == ActionStatus.Succeeded && now - a.CreatedAt <= Recent && a.CreatedAt < failure.CreatedAt))
            suspicious.Add($"{tool} worked for this identity in the last 7 days and is now refused");
        if (failure.Tainted) suspicious.Add("the session had read outside content before the call");
        if (level != IdentityLevel.Normal) suspicious.Add($"the identity is {level}");
        var tools = history.Where(a => a.Requester.Id == who && now - a.CreatedAt <= Window && a.Error?.EffectiveKind == ToolFailureKind.Auth)
            .Select(a => a.ToolName).Distinct().Count();
        if (tools >= 3) suspicious.Add($"{tools} different tools refused this identity in 24 hours: several credentials or scopes tried");
        if (trail.Any(e => e.Actor == who && e.Event is "level_changed" or "level_would_change" && e.Detail?.Contains("decoy", StringComparison.OrdinalIgnoreCase) == true))
            suspicious.Add("this identity has touched a decoy record");
        if (history.Any(a => a.Requester.Id == who && a.ToolName == AccessGrants.RequestTool && a.CreatedAt <= failure.CreatedAt && failure.CreatedAt - a.CreatedAt <= Window))
            suspicious.Add("the refusal came within a day of a request for more access");

        // Suspicion wins: an expected-looking reason never cancels a suspicious one.
        var verdict = suspicious.Count > 0 ? CredentialVerdict.Suspicious : expected.Count > 0 ? CredentialVerdict.Expected : CredentialVerdict.Unclear;
        return new Result(verdict, expected, suspicious);
    }
}
