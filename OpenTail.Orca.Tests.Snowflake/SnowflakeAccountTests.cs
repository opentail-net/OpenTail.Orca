using OpenTail.Orca.Abstractions;
using Snowflake.Data.Client;
using Xunit;

namespace OpenTail.Orca.Tests.Snowflake;

internal static class Req
{
    public static readonly ActionRequester Me = new(RequesterKind.User, "me");
    public static ActionRequest Call(string tool, string args) => new(tool, args, Me, null);
}

/// <summary>
/// The Sql module's Snowflake provider through the real driver (<c>Snowflake.Data</c>). The factory checks need no account. The
/// account checks skip unless <c>OTAIL_SNOWFLAKE_ACCOUNT</c>, <c>OTAIL_SNOWFLAKE_USER</c>, <c>OTAIL_SNOWFLAKE_PRIVATE_KEY</c> (PEM
/// text), <c>OTAIL_SNOWFLAKE_ROLE</c> and <c>OTAIL_SNOWFLAKE_WAREHOUSE</c> are set; they read Snowflake's shared sample data
/// (<c>SNOWFLAKE_SAMPLE_DATA.TPCH_SF1</c>) and use a few seconds of the warehouse.
/// </summary>
public sealed class SnowflakeAccountTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable("OTAIL_SNOWFLAKE_" + name);

    private static bool Configured => new[] { "ACCOUNT", "USER", "PRIVATE_KEY", "ROLE", "WAREHOUSE" }.All(n => !string.IsNullOrWhiteSpace(Env(n)));

    private static Task<ModuleLoadResult> Load(string config, string credentials = "{}") => ModuleLoader.LoadAsync(
        ModulesConfig.Parse($$"""{ "Modules": [ { "Type": "sql", "Id": "wh", "Config": {{config}}, "Credentials": {{credentials}} } ] }""", null),
        ModuleLoadMode.Describe);

    [Fact]
    public async Task The_driver_resolves_and_secrets_stay_out_of_the_connection_string()
    {
        Assert.NotNull(SnowflakeDbFactory.Instance);
        Assert.NotNull(await Load("""{ "Provider": "snowflake", "ConnectionString": "account=a;user=u;authenticator=snowflake_jwt" }"""));

        foreach (var secret in new[] { "private_key=MIIEsecret", "password=MIIEsecret", "token=MIIEsecret", "private_key_pwd=MIIEsecret" })
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Load($$"""{ "Provider": "snowflake", "ConnectionString": "account=a;user=u;{{secret}}" }"""));
            Assert.Contains("must not contain a password", e.Message);
            Assert.DoesNotContain("MIIEsecret", e.Message);
        }
    }

    private static async Task<OpenTailHost> AccountHost(int maxRows = 5, int timeoutSeconds = 30)
    {
        Assert.SkipUnless(Configured, "Set OTAIL_SNOWFLAKE_ACCOUNT, _USER, _PRIVATE_KEY, _ROLE and _WAREHOUSE to run against a Snowflake account.");
        var connection = $"account={Env("ACCOUNT")};user={Env("USER")};role={Env("ROLE")};warehouse={Env("WAREHOUSE")};db=SNOWFLAKE_SAMPLE_DATA;schema=TPCH_SF1";
        var loaded = await Load(
            $$"""{ "Provider": "snowflake", "ConnectionString": "{{connection}}", "MaxRows": {{maxRows}}, "CommandTimeoutSeconds": {{timeoutSeconds}} }""",
            """{ "private-key": { "Env": "OTAIL_SNOWFLAKE_PRIVATE_KEY", "Kind": "Secret" } }""");
        return new OpenTailHostBuilder()
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "wh.schema": { "Mode": "Execute" }, "wh.query": { "Mode": "Execute" } } }"""))
            .AddModules(loaded)
            .Build();
    }

    [Fact]
    public async Task Key_pair_sign_in_a_parameterised_query_and_the_row_cap_work()
    {
        var host = await AccountHost(maxRows: 5);
        var r = await host.Queue.SubmitAsync(Req.Call("wh.query",
            """{"sql":"SELECT c_name, c_mktsegment FROM customer WHERE c_mktsegment = @segment AND c_acctbal > @min","parameters":{"segment":"BUILDING","min":0}}"""));
        Assert.True(r.Status == ActionStatus.Succeeded, r.Error?.Message);
        Assert.Contains("BUILDING", r.ResultJson);
        Assert.Contains("\"truncated\":true", r.ResultJson); // LIMIT 6 was added, five rows kept

        var schema = await host.Queue.SubmitAsync(Req.Call("wh.schema", """{"table":"customer"}"""));
        Assert.True(schema.Status == ActionStatus.Succeeded, schema.Error?.Message);
        Assert.Contains("C_MKTSEGMENT", schema.ResultJson);
        Assert.Contains("\"table_name\"", schema.ResultJson);
    }

    [Fact]
    public async Task A_query_past_the_timeout_is_cancelled_on_the_server_and_carries_the_action_id()
    {
        var host = await AccountHost(timeoutSeconds: 2);
        // GENERATOR with TIMELIMIT keeps the warehouse busy for 20 seconds unless the query is cancelled.
        var r = await host.Queue.SubmitAsync(Req.Call("wh.query", """{"sql":"SELECT COUNT(*) FROM TABLE(GENERATOR(TIMELIMIT => 20))"}"""));
        Assert.Equal(ActionStatus.Failed, r.Status);

        // The query history shows the tag and that the query stopped, not that it ran on for 20 seconds.
        await using var conn = new SnowflakeDbConnection();
        var b = new System.Data.Common.DbConnectionStringBuilder
        {
            ConnectionString = $"account={Env("ACCOUNT")};user={Env("USER")};role={Env("ROLE")};warehouse={Env("WAREHOUSE")};authenticator=snowflake_jwt",
        };
        b["private_key"] = Env("PRIVATE_KEY");
        conn.ConnectionString = b.ConnectionString;
        await conn.OpenAsync();
        string? status = null;
        for (var attempt = 0; attempt < 10 && status is null or "RUNNING"; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT execution_status FROM TABLE(SNOWFLAKE_SAMPLE_DATA.INFORMATION_SCHEMA.QUERY_HISTORY_BY_USER(RESULT_LIMIT => 100)) " +
                "WHERE query_tag = ? ORDER BY start_time DESC LIMIT 1";
            var p = cmd.CreateParameter();
            p.ParameterName = "1";
            p.Value = "opentail:" + r.Id;
            cmd.Parameters.Add(p);
            status = await cmd.ExecuteScalarAsync() as string;
        }
        Assert.NotNull(status);
        Assert.NotEqual("RUNNING", status);
        Assert.NotEqual("SUCCESS", status);
    }
}
