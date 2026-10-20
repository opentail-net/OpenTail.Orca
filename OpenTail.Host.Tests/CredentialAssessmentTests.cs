using System.Text.Json.Nodes;

namespace OpenTail.Host.Tests;

/// <summary>Failures design, step 4: credential failures get a rule-based assessment (expected, unclear, suspicious) from facts the
/// Host holds; suspicion wins and feeds the identity-level ladder; rotation is a remedy a person approves.</summary>
public class CredentialAssessmentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");

    private static ActionRecord A(string who, string tool, ActionStatus status, DateTimeOffset at, ToolFailureKind? kind = null, bool tainted = false) => new()
    {
        Id = "act_" + Guid.NewGuid().ToString("N"), ToolName = tool, ModuleId = "m", ArgumentsJson = "{}", ArgumentsHash = "h",
        Requester = new ActionRequester(RequesterKind.AiSession, who), Status = status, PolicyMode = ToolMode.Execute, CreatedAt = at, UpdatedAt = at,
        Tainted = tainted, Error = kind is { } k ? new ActionError("http_403", "refused", true) { Kind = k } : null,
    };

    [Fact]
    public void A_tool_that_never_worked_points_to_a_new_untested_integration()
    {
        var failure = A("key:bot", "crm.update", ActionStatus.Failed, Now, ToolFailureKind.Auth);
        var r = CredentialAssessment.Assess(failure, [failure], [], IdentityLevel.Normal, Now);
        Assert.Equal(CredentialVerdict.Expected, r.Verdict);
        Assert.Contains(r.Expected, e => e.Contains("never succeeded"));
    }

    [Fact]
    public void Everyone_failing_the_same_way_points_to_the_provider()
    {
        var failure = A("key:bot", "crm.update", ActionStatus.Failed, Now, ToolFailureKind.Auth);
        var history = new[] { failure, A("key:other", "crm.update", ActionStatus.Failed, Now.AddMinutes(-5), ToolFailureKind.Auth),
            A("key:old", "crm.update", ActionStatus.Succeeded, Now.AddDays(-30)) };
        var r = CredentialAssessment.Assess(failure, history, [], IdentityLevel.Normal, Now);
        Assert.Equal(CredentialVerdict.Expected, r.Verdict);
        Assert.Contains(r.Expected, e => e.Contains("1 other identity is failing"));
    }

    [Fact]
    public void Worked_yesterday_refused_today_after_reading_outside_content_is_suspicious_and_suspicion_wins()
    {
        var failure = A("key:bot", "crm.update", ActionStatus.Failed, Now, ToolFailureKind.Auth, tainted: true);
        var history = new[] { failure, A("key:bot", "crm.update", ActionStatus.Succeeded, Now.AddDays(-1)),
            A("key:other", "crm.update", ActionStatus.Failed, Now.AddMinutes(-5), ToolFailureKind.Auth) };
        var r = CredentialAssessment.Assess(failure, history, [], IdentityLevel.Normal, Now);
        Assert.Equal(CredentialVerdict.Suspicious, r.Verdict);
        Assert.Contains(r.Suspicious, s => s.Contains("worked for this identity"));
        Assert.Contains(r.Suspicious, s => s.Contains("outside content"));
        Assert.NotEmpty(r.Expected);   // shown to the approver, but it does not cancel the suspicion
    }

    [Fact]
    public void Several_tools_refused_in_a_day_points_to_credentials_being_tried()
    {
        var failure = A("key:bot", "c.x", ActionStatus.Failed, Now, ToolFailureKind.Auth);
        var history = new[] { failure, A("key:bot", "a.x", ActionStatus.Failed, Now.AddHours(-1), ToolFailureKind.Auth),
            A("key:bot", "b.x", ActionStatus.Failed, Now.AddHours(-2), ToolFailureKind.Auth), A("key:x", "c.x", ActionStatus.Succeeded, Now.AddDays(-40)) };
        Assert.Contains(CredentialAssessment.Assess(failure, history, [], IdentityLevel.Normal, Now).Suspicious, s => s.Contains("3 different tools"));
    }

    [Fact]
    public void Nothing_to_go_on_is_unclear()
    {
        var failure = A("key:bot", "c.x", ActionStatus.Failed, Now, ToolFailureKind.Auth);
        var r = CredentialAssessment.Assess(failure, [failure, A("key:x", "c.x", ActionStatus.Succeeded, Now.AddDays(-40))], [], IdentityLevel.Normal, Now);
        Assert.Equal(CredentialVerdict.Unclear, r.Verdict);
    }

    private sealed class AuthModule : IHostModule
    {
        public bool Refuse;
        public string Id => "crm";
        public void Register(IToolRegistry registry) => registry.Add(new ToolDescriptor("crm.update", "Update.", ToolRisk.Write, Id),
            AIFunctionFactory.Create(() => Refuse ? throw new ToolFailureException(ToolFailureKind.Auth, "403", "http_403") : "ok", "crm.update", "Update."));
    }

    [Fact]
    public async Task A_dead_item_with_a_suspicious_credential_failure_carries_the_assessment_and_raises_a_soft_signal()
    {
        var crm = new AuthModule();
        var host = new OpenTailHostBuilder().AddModule(crm).UsePolicy(PolicyConfig.Parse("""
            { "Work": { "Enabled": true, "MaxRuns": 1 }, "Agents": { "clerk": { "Tools": [ "crm.*", "work.*" ] } },
              "Tools": { "crm.update": { "Mode": "Execute" } } }
            """)).Build();
        var clerk = new CallerIdentity("key:clerk", "clerk", CallerKind.Agent) { Profile = "clerk" };
        ActionRequest Call() => new("crm.update", "{}", new ActionRequester(RequesterKind.AiSession, clerk.Subject), host.Sessions.Start(clerk.Subject)) { Profile = "clerk" };
        await host.Queue.SubmitAsync(Call());          // worked
        var item = await host.Work.CreateAsync("Update ACME.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.ClaimNextAsync(clerk, null);
        crm.Refuse = true;
        await host.Queue.SubmitAsync(Call());          // now refused
        var dead = await host.Work.FailAsync(item.Id, clerk, "403 from the CRM.");

        var evidence = JsonNode.Parse(host.Work.Store.Get(dead.InspectionId!)!.InputsJson!)!;
        var cred = evidence["credentials"]!.AsArray().Single()!;
        Assert.Equal("suspicious", cred["verdict"]!.ToString());
        var trail = ((IAuditReader)host.Audit).Read();
        Assert.Contains(trail, e => e.Event == "credential.suspicious" && e.Actor == "key:clerk");
        Assert.Contains(trail, e => e.Event == "level_would_change" && e.Detail!.Contains("suspicious credential failure"));   // report only
    }

    [Fact]
    public void A_rotation_request_that_could_run_without_a_person_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Work": { "Enabled": true }, "Tools": { "work.request_rotation": { "Mode": "Execute" } } }""")).Build());
        Assert.Contains("'work.request_rotation' must be Mode Queue", ex.Message);
    }
}
