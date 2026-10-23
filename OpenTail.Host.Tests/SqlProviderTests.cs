using System.Data.Common;
using OpenTail.Modules.Sql;

namespace OpenTail.Host.Tests;

/// <summary>Oracle's guard rules and parameter style, Redshift's defaults, and how the factory finds add-on providers.</summary>
public class SqlProviderTests
{
    [Theory]
    [InlineData("SELECT q'[ ' ]' ; DELETE FROM accounts WHERE 1=1; SELECT ' ' FROM dual")]    // q'' alternative quoting
    [InlineData("SELECT Q'{ x }' FROM dual")]
    [InlineData("SELECT nq'< x >' FROM dual")]
    [InlineData("SELECT UTL_HTTP.REQUEST('http://example.com') FROM dual")]                  // network calls from the database
    [InlineData("SELECT dbms_pipe.receive_message('x', 10) FROM dual")]
    [InlineData("BEGIN DELETE FROM accounts; END")]
    public void The_guard_refuses_what_Oracle_would_read_differently_or_should_never_run(string sql)
    {
        Assert.Throws<ArgumentException>(() => SqlGuard.Classify(sql, SqlDialect.Oracle));
        Assert.Throws<ArgumentException>(() => SqlGuard.Classify(sql)); // Unknown applies Oracle's refusals too
    }

    [Theory]
    [InlineData("SELECT name FROM customers WHERE city = 'it''s' AND note = 'C:\\temp' ORDER BY name")] // no backslash escapes
    [InlineData("SELECT \"Order\", n'x' FROM t")]
    [InlineData("SELECT v$session.sid FROM v$session")]                                               // $ in names is fine
    public void The_guard_accepts_ordinary_Oracle_queries(string sql) => Assert.Equal(SqlKind.Select, SqlGuard.Classify(sql, SqlDialect.Oracle));

    [Fact]
    public void Named_parameters_become_colon_names_and_a_database_link_becomes_an_unbound_name()
    {
        var sql = SqlGuard.RewriteParameters("SELECT ':no' FROM t@remote WHERE a = @a OR b = @a", SqlParameterStyle.ColonName, out var names);
        Assert.Equal("SELECT ':no' FROM t:remote WHERE a = :a OR b = :a", sql);
        Assert.Equal(["remote", "a", "a"], names);
    }

    [Fact]
    public void Redshift_sets_compatibility_mode_unless_the_operator_did()
    {
        var b = new DbConnectionStringBuilder { ConnectionString = "Host=x;Database=d" };
        SqlProviders.Redshift.PrepareConnection!(b);
        Assert.Equal("Redshift", b["Server Compatibility Mode"]);

        var own = new DbConnectionStringBuilder { ConnectionString = "Host=x;Server Compatibility Mode=NoTypeLoading" };
        SqlProviders.Redshift.PrepareConnection!(own);
        Assert.Equal("NoTypeLoading", own["Server Compatibility Mode"]);
    }

    [Fact]
    public async Task Redshift_queries_are_tagged_by_query_group_limited_and_read_only()
    {
        var sent = new List<SnowflakeTests.RecordingCommand>();
        var host = new OpenTailHostBuilder()
            .AddModule(new SqlModule(new SqlModuleOptions(SqlProviders.Redshift) { Name = "rs", ConnectionFactory = () => new SnowflakeTests.RecordingConnection(sent), MaxRows = 10 }))
            .UsePolicy(PolicyConfig.Parse("""{ "DefaultReadMode": "Execute" }"""))
            .Build();

        var r = await host.Queue.SubmitAsync(Req.Call("rs.query", """{"sql":"SELECT a FROM t WHERE b = @b","parameters":{"b":1}}"""));

        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Equal(
            [$"SET query_group TO 'opentail:{r.Id}'", "START TRANSACTION READ ONLY", "SELECT a FROM t WHERE b = @b\nLIMIT 11", "ROLLBACK"],
            sent.Select(c => c.CommandText));
    }

    [Fact]
    public async Task The_factory_names_the_package_for_an_add_on_provider_and_knows_redshift()
    {
        static Task<ModuleLoadResult> Load(string config) => ModuleLoader.LoadAsync(
            ModulesConfig.Parse($$"""{ "Modules": [ { "Type": "sql", "Id": "db", "Config": {{config}} } ] }""", null), ModuleLoadMode.Describe);

        var oracle = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Load("""{ "Provider": "oracle", "ConnectionString": "User Id=agent;Data Source=db1/SALES" }"""));
        Assert.Contains("reference package OpenTail.Modules.Sql.Oracle", oracle.Message);

        // Npgsql is not referenced by these tests: Redshift resolves to the Npgsql driver and names it.
        var redshift = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Load("""{ "Provider": "redshift", "ConnectionString": "Host=c1.redshift.amazonaws.com;Database=dev" }"""));
        Assert.Contains("reference package Npgsql", redshift.Message);
    }
}
