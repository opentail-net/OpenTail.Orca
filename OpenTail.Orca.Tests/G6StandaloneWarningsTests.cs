using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>
/// G6 of the reviewer gate (batch 07, part C): the standalone deployment runner prints the same no-list warning as the HTTP runner. The
/// runner's warnings go to standard error before it serves stdio, so a cancelled token makes it return straight after printing them.
/// </summary>
public class G6StandaloneWarningsTests
{
    [Fact]
    public async Task The_standalone_runner_prints_the_no_list_warning()
    {
        using var db = new TempDb();
        var policyPath = Path.Combine(Path.GetTempPath(), "otail-g6-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(policyPath, """{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect" } } }""");
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

        Assert.Contains("warning: tool 'mail.send' lets an AI reviewer decide it and names no reviewer", stderr.ToString());
    }
}
