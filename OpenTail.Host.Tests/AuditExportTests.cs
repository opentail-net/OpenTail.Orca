using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>Audit D7: the export an auditor checks with plain Python, needing none of our code.</summary>
public class AuditExportTests
{
    private const string Policy = """{ "RetentionDays": 1, "DefaultReadMode": "Execute", "Tools": { "mail.send": { "Mode": "Queue" } } }""";

    private static async Task<string> ExportAsync(TempDb db, string folder)
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2020-01-01T09:00:00Z"));
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path, clock);
        var old = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.example","body":"é <b>&</b>"}""", new ActionRequester(RequesterKind.AiSession, "key:bot"), null));
        await host.Queue.ReviewAsync(old.Id, new ActionReview("rita", ReviewDecision.Reject, "Not now — later.", clock.GetUtcNow()) { Subject = "admin:rita" });
        await host.RunMaintenanceOnceAsync();   // purges it, keeping a decision record, and records the policy
        clock.Advance(TimeSpan.FromDays(365 * 6));   // still before the export's end (now)
        await host.Queue.SubmitAsync(new ActionRequest("clock.now", "{}", new ActionRequester(RequesterKind.AiSession, "key:bot"), null));
        TempDb.ClearPools(db.Path);
        var output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--export-audit", folder], "host", _ => { }, output));
        return output.ToString();
    }

    private static string? Python()
    {
        foreach (var name in new[] { "python3", "python" })
            try
            {
                using var p = Process.Start(new ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
                p!.WaitForExit(10_000);
                if (p.ExitCode == 0) return name;
            }
            catch (System.ComponentModel.Win32Exception) { }
        return null;
    }

    private static (int Exit, string Output) Verify(string python, string folder)
    {
        using var p = Process.Start(new ProcessStartInfo(python) { ArgumentList = { "-I", Path.Combine(folder, "verify.py"), folder }, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, text);
    }

    private static void Rewrite(string folder, string file, Func<string, string> change)
    {
        var path = Path.Combine(folder, file);
        var text = change(File.ReadAllText(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
        // Fix the manifest too, so only the content checks can catch it.
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")))!;
        manifest["files"]![file] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest.ToJsonString());
    }

    [Fact]
    public async Task The_export_holds_the_trail_the_records_and_the_policy_without_arguments()
    {
        using var db = new TempDb();
        var folder = Path.Combine(Path.GetTempPath(), "otail-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var said = await ExportAsync(db, folder);
            Assert.Contains("2 decision records, 1 policy versions", said);
            Assert.True(File.Exists(Path.Combine(folder, "verify.py")));
            var decisions = File.ReadAllText(Path.Combine(folder, "decisions.jsonl"));
            Assert.Contains("Not now", decisions);
            Assert.DoesNotContain("a@b.example", decisions);
            Assert.Single(Directory.GetFiles(Path.Combine(folder, "policies")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Plain_python_verifies_the_export_and_catches_an_edit_even_with_the_manifest_fixed()
    {
        if (Python() is not { } python) return;   // no Python on this machine: the export itself is covered above
        using var db = new TempDb();
        var folder = Path.Combine(Path.GetTempPath(), "otail-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExportAsync(db, folder);
            var (exit, text) = Verify(python, folder);
            Assert.True(exit == 0, text);
            Assert.Contains("Intact.", text);

            Rewrite(folder, "audit.jsonl", t => t.Replace("\"actor\":\"system\"", "\"actor\":\"admin:sam\""));
            (exit, text) = Verify(python, folder);
            Assert.Equal(1, exit);
            Assert.Contains("says something other than what its hash covers", text);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task A_changed_decision_record_is_caught_by_plain_python()
    {
        if (Python() is not { } python) return;
        using var db = new TempDb();
        var folder = Path.Combine(Path.GetTempPath(), "otail-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExportAsync(db, folder);
            Rewrite(folder, "decisions.jsonl", t => t.Replace("Not now", "Approved"));
            var (exit, text) = Verify(python, folder);
            Assert.Equal(1, exit);
            Assert.Contains("does not match its hash", text);
        }
        finally { Directory.Delete(folder, true); }
    }
}
