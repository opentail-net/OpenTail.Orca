using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>
/// Batch 08b of the reviewer gate (follow-ups to batch 08, from the planner): a notice for an AI-open tool that no reviewer will decide (not a
/// loosening), the announcement of a Destructive tool set to <c>Once</c>, and the two-reviewer refusal in the standalone server. The standalone
/// runner writes to standard error, so the class shares the console-capture collection with <c>G6StandaloneWarningsTests</c>.
/// </summary>
[Collection(ConsoleCapture.Name)]
public class Batch08bTests
{
    private const string Open = """{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect" } } }""";
    private const string Named = """{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }""";
    private const string Defaulted = """{ "DefaultAiReviewers": ["bot"], "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai" } } }""";
    private const string OpenNotice = "tool 'mail.send' is open to an AI reviewer but names none, and no DefaultAiReviewers is set: no AI reviewer will decide it.";

    [Fact]
    public void An_ai_open_tool_with_no_reviewers_gets_a_startup_notice_that_is_not_a_loosening()
    {
        var (host, _, _) = Hosts.Build(Open);
        Assert.Contains(OpenNotice, host.StartupWarnings);
        Assert.DoesNotContain(host.StartupWarnings, w => w.StartsWith("DefaultAiReviewers names", StringComparison.Ordinal));
        Assert.Equal(OpenNotice, PolicySettings.NoticeFor("mail.send"));
    }

    [Theory]
    [InlineData(Named)]
    [InlineData(Defaulted)]
    public void No_notice_when_the_tool_or_the_policy_names_reviewers(string policy)
    {
        var (host, _, _) = Hosts.Build(policy);
        Assert.DoesNotContain(host.StartupWarnings, w => w.Contains("is open to an AI reviewer but names none", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_standalone_runner_prints_the_notice_at_startup()
    {
        using var db = new TempDb();
        var policyPath = Path.Combine(Path.GetTempPath(), "otail-b08b-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(policyPath, Open);
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            using var stop = new CancellationTokenSource();
            await stop.CancelAsync();
            Console.SetError(stderr);
            await DeploymentRunner.RunAsync(["--policy", policyPath, "--db", db.Path, "--admin-port", "0"], "b08b-test",
                builder => builder.AddModule(new FakeInboxModule()), stop.Token);
        }
        finally
        {
            Console.SetError(original);
            File.Delete(policyPath);
        }

        Assert.Contains("warning: " + OpenNotice, stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_gives_the_same_notice_for_the_tool()
    {
        var policyPath = Path.Combine(Path.GetTempPath(), "otail-b08b-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(policyPath, Open);
        var output = new StringWriter();
        try
        {
            await PolicyCli.TryRunAsync(["--policy", policyPath, "--explain", "mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}"], "b08b-test",
                builder => builder.AddModule(new FakeInboxModule()), output);
        }
        finally
        {
            File.Delete(policyPath);
        }

        Assert.Contains("note: " + OpenNotice, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_destructive_tool_set_to_once_is_announced_and_a_policy_wide_once_is_not()
    {
        var tool = Hosts.Build("""{ "Tools": { "files.delete": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "UnknownOutcomeRetry": "Once" } } }""");
        Assert.Contains(tool.Host.StartupWarnings, w => w.StartsWith(
            "tool 'files.delete' is Destructive and sets UnknownOutcomeRetry Once: a person may retry its unknown outcome once, with a reason (its strict value is Never).",
            StringComparison.Ordinal));

        // A policy-wide Once does not reach a Destructive tool (it stays Never), so nothing is loosened there.
        var wide = Hosts.Build("""{ "UnknownOutcomeRetry": "Once", "Tools": { "files.delete": { "Mode": "Queue" } } }""");
        Assert.DoesNotContain(wide.Host.StartupWarnings, w => w.Contains("is Destructive and sets UnknownOutcomeRetry Once", StringComparison.Ordinal));

        // The same setting on a tool that is not Destructive is the default, and is not announced.
        var plain = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "UnknownOutcomeRetry": "Once" } } }""");
        Assert.DoesNotContain(plain.Host.StartupWarnings, w => w.Contains("is Destructive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_standalone_server_refuses_two_reviewers_when_a_tool_names_none_and_starts_when_a_default_names_them()
    {
        var (open, _, _) = Hosts.Build(Open);
        var reviewers = new Dictionary<string, string>
        {
            ["bot"] = "sha256:" + new string('a', 64),
            ["second"] = "sha256:" + new string('b', 64),
        };
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => AdminServer.StartAsync(open, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { ["admin-token-0123456789"] = "alice" },
            AiReviewers = reviewers,
        }));
        Assert.Contains("is open to AI reviewers and names none", refused.Message, StringComparison.Ordinal);

        var (defaulted, _, _) = Hosts.Build(Defaulted);
        var admin = await AdminServer.StartAsync(defaulted, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { ["admin-token-0123456789"] = "alice" },
            AiReviewers = reviewers,
        });
        await admin.DisposeAsync();
    }
}
