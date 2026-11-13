using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenTail.Orca.Tests;

/// <summary>
/// Talks to a real child process over real stdio: the published shape of a deployment.
/// Needs the FilesOpenApi sample to have been built (the solution build does that); otherwise the test returns early.
/// </summary>
public class RealProcessTests
{
    private const string Spec = """
        { "openapi": "3.0.3", "info": { "title": "t", "version": "1" }, "servers": [ { "url": "https://api.example.test/v1" } ],
          "paths": { "/customers/{id}": { "get": { "operationId": "getCustomer",
            "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
            "responses": { "200": { "description": "ok" } } } } } }
        """;

    private static string? FindSampleDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenTail.Orca.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        var bin = Path.Combine(dir.FullName, "OpenTail.Orca.Sample.FilesOpenApi", "bin");
        return Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "opentail-orca-files-api.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    [Fact]
    public async Task A_deployment_runs_as_a_real_process_over_stdio_and_keeps_its_queue_on_disk()
    {
        var dll = FindSampleDll();
        if (dll is null) return; // sample not built in this environment

        var work = Path.Combine(Path.GetTempPath(), "otail-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "files"));
        File.WriteAllText(Path.Combine(work, "files", "hello.txt"), "hi there");
        File.WriteAllText(Path.Combine(work, "spec.json"), Spec);
        File.WriteAllText(Path.Combine(work, "policy.json"),
            """{ "DefaultReadMode": "Execute", "Tools": { "files.write": { "Mode": "Queue" } } }""");
        var dbPath = Path.Combine(work, "queue.db");

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "sample",
                Command = "dotnet",
                Arguments = [dll, "--policy", Path.Combine(work, "policy.json"), "--db", dbPath, "--admin-port", "0"],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["FILES_ROOT"] = Path.Combine(work, "files"),
                    ["OPENAPI_SPEC"] = Path.Combine(work, "spec.json"),
                    ["OPENAPI_NAMESPACE"] = "crm",
                },
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);

            var names = (await client.ListToolsAsync(cancellationToken: cts.Token)).Select(t => t.Name).ToList();
            Assert.Contains("files.list", names);
            Assert.Contains("files.write", names);
            Assert.Contains("crm.getCustomer", names);
            Assert.Contains("start_session", names);

            var read = await client.CallToolAsync("files.read", new Dictionary<string, object?> { ["path"] = "hello.txt" }, cancellationToken: cts.Token);
            Assert.Contains("hi there", Text(read));

            var write = await client.CallToolAsync("files.write",
                new Dictionary<string, object?> { ["path"] = "new.txt", ["content"] = "draft" }, cancellationToken: cts.Token);
            var json = (JsonObject)JsonNode.Parse(Text(write))!;
            Assert.Equal("pending_approval", json["status"]!.ToString());
            Assert.False(File.Exists(Path.Combine(work, "files", "new.txt")));

            var status = await client.CallToolAsync("get_action_status",
                new Dictionary<string, object?> { ["actionId"] = json["actionId"]!.ToString() }, cancellationToken: cts.Token);
            Assert.Equal("pending_approval", JsonNode.Parse(Text(status))!["status"]!.ToString());

            await client.DisposeAsync();

            // The queue is in the database file, so a restarted process would still see it. Disposing the client does not wait for
            // the child to finish closing the file, and on Windows opening it meanwhile can fail, so retry briefly.
            var store = await OpenWhenReleasedAsync(dbPath);
            var pending = await store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending });
            Assert.Equal("files.write", Assert.Single(pending).ToolName);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_bad_policy_stops_the_process_with_a_clear_message_and_a_nonzero_exit()
    {
        var dll = FindSampleDll();
        if (dll is null) return;

        var work = Path.Combine(Path.GetTempPath(), "otail-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "files"));
        File.WriteAllText(Path.Combine(work, "spec.json"), Spec);
        File.WriteAllText(Path.Combine(work, "policy.json"), """{ "Tools": { "files.wrtie": { "Mode": "Execute" } } }""");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false,
            };
            foreach (var a in new[] { dll, "--policy", Path.Combine(work, "policy.json"), "--db", Path.Combine(work, "q.db"), "--admin-port", "0" })
                psi.ArgumentList.Add(a);
            psi.Environment["FILES_ROOT"] = Path.Combine(work, "files");
            psi.Environment["OPENAPI_SPEC"] = Path.Combine(work, "spec.json");

            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync(timeout.Token);

            Assert.Equal(2, p.ExitCode);
            Assert.Contains("files.wrtie", err);
            Assert.Equal("", await p.StandardOutput.ReadToEndAsync()); // stdout is reserved for the protocol
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static string Text(CallToolResult r) => r.Content.OfType<TextContentBlock>().First().Text;

    private static async Task<SqliteActionStore> OpenWhenReleasedAsync(string dbPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new SqliteActionStore(dbPath);
            }
            catch (Microsoft.Data.Sqlite.SqliteException) when (DateTime.UtcNow < deadline)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                await Task.Delay(200);
            }
        }
    }
}
