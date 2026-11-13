using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenTail.Modules.Sql;

namespace OpenTail.Orca.Tests;

/// <summary>
/// The Databricks provider against a stub of the SQL Statement Execution API: the request the module sends, polling, chunks,
/// cancel on timeout, OAuth, the network allow-list, and the guard's Databricks rules. Not yet run against a real workspace.
/// </summary>
public class DatabricksTests
{
    [Theory]
    [InlineData("SELECT \"a\\\"\" ; DELETE FROM accounts WHERE 1=1; SELECT \" \"")]    // "..." is a string with backslash escapes
    [InlineData("SELECT 'a\\'' ; DELETE FROM accounts WHERE 1=1; SELECT ' '")]
    [InlineData("SELECT r'\\' ; DELETE FROM accounts WHERE 1=1; SELECT '")]              // raw strings
    [InlineData("SELECT * FROM t WHERE a = '${secret}'")]                                   // variable substitution
    [InlineData("SELECT http_request(conn => 'x', method => 'GET', path => '/')")]
    [InlineData("SELECT * FROM read_files('s3://bucket/path')")]
    [InlineData("SELECT 1 /* /* */ ' */ ; DELETE FROM accounts WHERE 1=1; -- '")]           // nested comments
    public void The_guard_refuses_what_Databricks_would_read_differently_or_should_never_run(string sql)
    {
        Assert.Throws<ArgumentException>(() => SqlGuard.Classify(sql, SqlDialect.Databricks));
        Assert.Throws<ArgumentException>(() => SqlGuard.Classify(sql));
    }

    [Theory]
    [InlineData("SELECT `order`, arr[0], m['k'] FROM main.sales.orders WHERE note = \"it's\"")]
    [InlineData("SELECT * FROM t WHERE c = :x")]
    public void The_guard_accepts_ordinary_Databricks_queries(string sql) => Assert.Equal(SqlKind.Select, SqlGuard.Classify(sql, SqlDialect.Databricks));

    [Fact]
    public async Task A_query_is_sent_with_named_markers_a_limit_and_the_action_tag_and_its_rows_come_back_typed_across_chunks()
    {
        await using var server = await StubServer.StartAsync(ctx => ctx.Request.Path.Value switch
        {
            "/api/2.0/sql/statements" => Json(ctx, Succeeded("st-1",
                [["Ada", "1", "2026-01-02"], ["Cy", null, null]], "/api/2.0/sql/statements/st-1/result/chunks/1")),
            "/api/2.0/sql/statements/st-1/result/chunks/1" => Json(ctx, new JsonObject { ["chunk_index"] = 1, ["data_array"] = Rows([["Di", "3", "2026-03-04"]]) }),
            _ => NotFound(ctx),
        });
        var host = await HostFor(server);

        var r = await host.Queue.SubmitAsync(Req.Call("dbx.query",
            """{"sql":"SELECT name, n, d FROM t WHERE city = @city","parameters":{"city":"London"}}"""));

        Assert.True(r.Status == ActionStatus.Succeeded, r.Error?.Message);
        var sent = JsonNode.Parse(server.Requests[0].Body)!;
        Assert.Equal("SELECT name, n, d FROM t WHERE city = :city\nLIMIT 6", sent["statement"]!.GetValue<string>());
        Assert.Equal("wh1", sent["warehouse_id"]!.GetValue<string>());
        Assert.Equal("main", sent["catalog"]!.GetValue<string>());
        Assert.Equal("""[{"name":"city","value":"London","type":"STRING"}]""", sent["parameters"]!.ToJsonString());
        Assert.Equal($$"""[{"key":"opentail","value":"{{r.Id}}"}]""", sent["query_tags"]!.ToJsonString());
        Assert.Equal("Bearer pat-123", server.Requests[0].Authorization);
        Assert.Equal("GET", server.Requests[1].Method);

        var rows = JsonNode.Parse(r.ResultJson!)!["rows"]!.ToJsonString();
        Assert.Equal("""[["Ada",1,"2026-01-02"],["Cy",null,null],["Di",3,"2026-03-04"]]""", rows);
    }

    [Fact]
    public async Task A_running_statement_is_polled_until_it_finishes()
    {
        var polls = 0;
        await using var server = await StubServer.StartAsync(ctx => (ctx.Request.Method, ctx.Request.Path.Value) switch
        {
            ("POST", "/api/2.0/sql/statements") => Json(ctx, Status("st-2", "PENDING")),
            ("GET", "/api/2.0/sql/statements/st-2") => Json(ctx, ++polls < 2 ? Status("st-2", "RUNNING") : Succeeded("st-2", [["x", "1", null]])),
            _ => NotFound(ctx),
        });
        var r = await (await HostFor(server)).Queue.SubmitAsync(Req.Call("dbx.query", """{"sql":"SELECT 1"}"""));

        Assert.True(r.Status == ActionStatus.Succeeded, r.Error?.Message);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task A_statement_past_the_timeout_is_cancelled_on_the_warehouse()
    {
        await using var server = await StubServer.StartAsync(ctx => (ctx.Request.Method, ctx.Request.Path.Value) switch
        {
            ("POST", "/api/2.0/sql/statements") or ("GET", "/api/2.0/sql/statements/st-3") => Json(ctx, Status("st-3", "RUNNING")),
            ("POST", "/api/2.0/sql/statements/st-3/cancel") => Json(ctx, new JsonObject()),
            _ => NotFound(ctx),
        });
        var r = await (await HostFor(server, timeoutSeconds: 1)).Queue.SubmitAsync(Req.Call("dbx.query", """{"sql":"SELECT 1"}"""));

        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Contains(server.Requests, q => q is { Method: "POST", PathAndQuery: "/api/2.0/sql/statements/st-3/cancel" });
    }

    [Fact]
    public async Task A_failed_statement_fails_the_action()
    {
        await using var server = await StubServer.StartAsync(ctx => Json(ctx, new JsonObject
        {
            ["statement_id"] = "st-4",
            ["status"] = new JsonObject { ["state"] = "FAILED", ["error"] = new JsonObject { ["error_code"] = "TABLE_OR_VIEW_NOT_FOUND", ["message"] = "no such table t" } },
        }));
        var r = await (await HostFor(server)).Queue.SubmitAsync(Req.Call("dbx.query", """{"sql":"SELECT * FROM t"}"""));

        Assert.True(r.Status == ActionStatus.Failed, r.Status + ": " + r.Error?.Message + " " + r.PolicyRule);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_service_principal_signs_in_once_with_oauth_and_reuses_the_token()
    {
        await using var server = await StubServer.StartAsync(ctx => ctx.Request.Path.Value switch
        {
            "/oidc/v1/token" => Json(ctx, new JsonObject { ["access_token"] = "oauth-tok", ["expires_in"] = 3600 }),
            "/api/2.0/sql/statements" => Json(ctx, Succeeded("st-5", [["x", "1", null]])),
            _ => NotFound(ctx),
        });
        var host = await HostFor(server, clientId: "sp-client");

        await host.Queue.SubmitAsync(Req.Call("dbx.query", """{"sql":"SELECT 1"}"""));
        await host.Queue.SubmitAsync(Req.Call("dbx.query", """{"sql":"SELECT 2"}"""));

        var token = Assert.Single(server.Requests, q => q.PathAndQuery == "/oidc/v1/token");
        Assert.Equal("Basic " + Convert.ToBase64String("sp-client:pat-123"u8.ToArray()), token.Authorization);
        Assert.Contains("grant_type=client_credentials", token.Body);
        Assert.All(server.Requests.Where(q => q.PathAndQuery != "/oidc/v1/token"), q => Assert.Equal("Bearer oauth-tok", q.Authorization));
    }

    [Fact]
    public async Task Without_the_workspace_in_network_hosts_nothing_is_sent()
    {
        await using var server = await StubServer.StartAsync(ctx => Json(ctx, Succeeded("st-6", [])));
        var r = await (await HostFor(server, listHost: false)).Queue.SubmitAsync(Req.Call("dbx.query", """{"sql":"SELECT 1"}"""));

        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Secrets_in_the_connection_string_and_writes_are_refused()
    {
        static Task<ModuleLoadResult> Load(string config) => ModuleLoader.LoadAsync(
            ModulesConfig.Parse($$"""{ "Modules": [ { "Type": "sql", "Id": "dbx", "Config": {{config}} } ] }""", null), ModuleLoadMode.Describe);

        var secret = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Load("""{ "Provider": "databricks", "ConnectionString": "Host=https://x.cloud.databricks.com;WarehouseId=w;Password=dapiSECRET" }"""));
        Assert.DoesNotContain("dapiSECRET", secret.Message);

        var writes = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Load("""{ "Provider": "databricks", "ConnectionString": "Host=https://x.cloud.databricks.com;WarehouseId=w", "AllowWrites": true }"""));
        Assert.Contains("read-only", writes.Message);
    }

    // -- stub ----------------------------------------------------------------------------------------

    private static async Task<OpenTailHost> HostFor(StubServer server, int timeoutSeconds = 15, string? clientId = null, bool listHost = true)
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_DBX_SECRET", "pat-123");
        var connection = $"Host={server.Origin};WarehouseId=wh1;Catalog=main" + (clientId is null ? "" : ";ClientId=" + clientId);
        var network = listHost ? "\"Network\": { \"Hosts\": [ \"" + server.Origin + "\" ] }," : "";
        var modules = ModulesConfig.Parse($$"""
            { "Modules": [ { "Type": "sql", "Id": "dbx", {{network}}
                "Credentials": { "password": { "Env": "OTAIL_TEST_DBX_SECRET", "Kind": "Secret" } },
                "Config": { "Provider": "databricks", "ConnectionString": "{{connection}}", "MaxRows": 5, "CommandTimeoutSeconds": {{timeoutSeconds}} } } ] }
            """);
        return new OpenTailHostBuilder()
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "dbx.schema": { "Mode": "Execute" }, "dbx.query": { "Mode": "Execute" } } }"""))
            .AddModules(await ModuleLoader.LoadAsync(modules, ModuleLoadMode.Run))
            .Build();
    }

    private static JsonObject Status(string id, string state) =>
        new() { ["statement_id"] = id, ["status"] = new JsonObject { ["state"] = state } };

    private static JsonObject Succeeded(string id, string?[][] rows, string? nextChunk = null)
    {
        var o = Status(id, "SUCCEEDED");
        o["manifest"] = new JsonObject
        {
            ["format"] = "JSON_ARRAY",
            ["schema"] = new JsonObject
            {
                ["columns"] = new JsonArray(
                    new JsonObject { ["name"] = "name", ["type_name"] = "STRING", ["position"] = 0 },
                    new JsonObject { ["name"] = "n", ["type_name"] = "LONG", ["position"] = 1 },
                    new JsonObject { ["name"] = "d", ["type_name"] = "DATE", ["position"] = 2 }),
            },
        };
        o["result"] = new JsonObject { ["chunk_index"] = 0, ["data_array"] = Rows(rows), ["next_chunk_internal_link"] = nextChunk };
        return o;
    }

    private static JsonArray Rows(string?[][] rows) =>
        new(rows.Select(r => (JsonNode)new JsonArray(r.Select(v => v is null ? null : (JsonNode)JsonValue.Create(v)).ToArray())).ToArray());

    private static Task Json(HttpContext ctx, JsonNode body)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(body.ToJsonString());
    }

    private static Task NotFound(HttpContext ctx)
    {
        ctx.Response.StatusCode = 404;
        return ctx.Response.WriteAsync("""{"message":"not found"}""");
    }
}
