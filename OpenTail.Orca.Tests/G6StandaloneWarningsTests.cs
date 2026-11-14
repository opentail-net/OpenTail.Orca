using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>Serialises the tests that capture standard error: the runner writes its warnings there, and <c>SkillMeasurementTests</c> swaps it too.</summary>
[CollectionDefinition(ConsoleCapture.Name, DisableParallelization = true)]
public sealed class ConsoleCapture
{
    public const string Name = "console-capture";
}

/// <summary>
/// G6 of the reviewer gate: the standalone deployment runner prints the startup warnings the HTTP runner prints, the loosenings of section 1a
/// included (batch 08). The runner's warnings go to standard error before it serves stdio, so a cancelled token makes it return straight after
/// printing them.
/// </summary>
[Collection(ConsoleCapture.Name)]
public class G6StandaloneWarningsTests
{
    [Fact]
    public async Task The_standalone_runner_prints_the_startup_warning_for_a_loosened_setting()
    {
        using var db = new TempDb();
        var policyPath = Path.Combine(Path.GetTempPath(), "otail-g6-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(policyPath, """{ "AiOwnRequest": "Allow", "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect", "AiReviewers": ["bot"] } } }""");
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            using var stop = new CancellationTokenSource();
            await stop.CancelAsync();
            Console.SetError(stderr);
            await DeploymentRunner.RunAsync(["--policy", policyPath, "--db", db.Path, "--admin-port", "0"], "g6-test",
                builder => builder.AddModule(new FakeInboxModule()), stop.Token);
        }
        finally
        {
            Console.SetError(original);
            File.Delete(policyPath);
        }

        Assert.Contains("warning: AiOwnRequest is Allow for every tool", stderr.ToString());
    }
}
