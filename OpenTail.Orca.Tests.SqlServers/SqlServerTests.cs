using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using OpenTail.Orca.Abstractions;
using OpenTail.Modules.Sql;
using OpenTail.Modules.Sql.Oracle;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace OpenTail.Orca.Tests.SqlServers;

internal static class Req
{
    public static readonly ActionRequester Me = new(RequesterKind.User, "me");
    public static ActionRequest Call(string tool, string args) => new(tool, args, Me, null);
    public static ActionReview Approve() => new("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow);
}

/// <summary>
/// The Sql module against one real server, through two logins: a read login (SELECT only) for schema and query, and a write login
/// for write, delete and the test's own setup. Skips unless <c>OTAIL_SQL_{Env}_READ</c> and <c>OTAIL_SQL_{Env}_WRITE</c> are set.
/// </summary>
public abstract class SqlServerTests : IAsyncLifetime
{
    protected abstract string Env { get; }
    protected abstract SqlProvider Provider { get; }
    protected SqlDialect Dialect => Provider.Dialect;
    protected abstract DbConnection Create(string connectionString);

    /// <summary>A decimal 12.50, the date 2026-01-02 and a null, in this database's SQL.</summary>
    protected virtual string ValuesQuery => "SELECT CAST(12.50 AS DECIMAL(10,2)) AS d, CAST('2026-01-02' AS DATE) AS dt, NULL AS n";

    /// <summary>The guard bypasses for this dialect (the same strings as <c>SqlGuardTests</c>); each hides a DELETE on <c>accounts</c>.</summary>
    public abstract IReadOnlyList<string> Bypasses { get; }

    private string? ReadCs => Environment.GetEnvironmentVariable($"OTAIL_SQL_{Env}_READ");
    private string? WriteCs => Environment.GetEnvironmentVariable($"OTAIL_SQL_{Env}_WRITE");
    private bool Configured => ReadCs is { Length: > 0 } && WriteCs is { Length: > 0 };

    public async ValueTask InitializeAsync()
    {
        if (!Configured) return;
        await Exec("DROP TABLE IF EXISTS customers_t");
        await Exec("DROP TABLE IF EXISTS accounts");
        await Exec("CREATE TABLE customers_t (id INT PRIMARY KEY, name VARCHAR(50), city VARCHAR(50))");
        await Exec("INSERT INTO customers_t (id, name, city) VALUES (1, 'Ada', 'London'), (2, 'Bob', 'Paris'), (3, 'Cy', 'London')");
        await Exec("CREATE TABLE accounts (id INT PRIMARY KEY, owner VARCHAR(50))");
        await Exec("INSERT INTO accounts (id, owner) VALUES (1, 'a'), (2, 'b'), (3, 'c')");
    }

    public async ValueTask DisposeAsync()
    {
        if (!Configured) return;
        await Exec("DROP TABLE IF EXISTS customers_t");
        await Exec("DROP TABLE IF EXISTS accounts");
    }

    private void RequireServer() =>
        Assert.SkipUnless(Configured, $"Set OTAIL_SQL_{Env}_READ and OTAIL_SQL_{Env}_WRITE to run against a real server.");

    private async Task Exec(string sql)
    {
        await using var c = Create(WriteCs!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> Count(string table, string where = "1=1")
    {
        await using var c = Create(WriteCs!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private OpenTailHost Host() => new OpenTailHostBuilder()
        .AddModule(new SqlModule(new SqlModuleOptions(Provider)
        {
            ConnectionFactory = () => Create(ReadCs!),
            WriteConnectionFactory = () => Create(WriteCs!),
            AllowWrites = true,
        }))
        .UsePolicy(PolicyConfig.Parse("""
            { "DefaultReadMode": "Execute", "Tools": { "sql.write": { "Mode": "Queue" }, "sql.delete": { "Mode": "Queue" } } }
            """))
        .Build();

    [Fact]
    public void Every_bypass_is_refused_by_the_guard_for_this_dialect()
    {
        // Runs without a server, so the project always has a test that runs.
        Assert.NotEmpty(Bypasses);
        foreach (var sql in Bypasses) Assert.Throws<ArgumentException>(() => SqlGuard.Classify(sql, Dialect));
    }

    [Fact]
    public async Task The_module_refuses_every_bypass_and_the_rows_survive()
    {
        RequireServer();
        var host = Host();
        foreach (var sql in Bypasses)
        {
            var args = System.Text.Json.JsonSerializer.Serialize(new { sql });
            var r = await host.Queue.SubmitAsync(Req.Call("sql.query", args));
            Assert.NotEqual(ActionStatus.Succeeded, r.Status);
            Assert.Equal(3, await Count("accounts"));
        }
    }

    [Fact]
    public async Task Schema_lists_the_table_and_a_parameterised_query_reads_through_the_read_login()
    {
        RequireServer();
        var host = Host();
        var schema = await host.Queue.SubmitAsync(Req.Call("sql.schema", "{\"table\":\"customers_t\"}"));
        Assert.True(schema.Status == ActionStatus.Succeeded, schema.Error?.Message);
        Assert.Contains("customers_t", schema.ResultJson, StringComparison.OrdinalIgnoreCase); // Oracle upper-cases names
        Assert.Contains("city", schema.ResultJson, StringComparison.OrdinalIgnoreCase);

        var q = await host.Queue.SubmitAsync(Req.Call("sql.query",
            "{\"sql\":\"SELECT name FROM customers_t WHERE city = @city ORDER BY name\",\"parameters\":{\"city\":\"London\"}}"));
        Assert.True(q.Status == ActionStatus.Succeeded, q.Error?.Message);
        Assert.Contains("Ada", q.ResultJson);
        Assert.Contains("Cy", q.ResultJson);
        Assert.DoesNotContain("Bob", q.ResultJson);
    }

    [Fact]
    public async Task The_read_login_cannot_write()
    {
        RequireServer();
        await using var c = Create(ReadCs!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE customers_t SET city = 'Rome' WHERE id = 1";
        await Assert.ThrowsAnyAsync<DbException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(0, await Count("customers_t", "city = 'Rome'"));
    }

    [Fact]
    public async Task Write_is_previewed_by_rollback_then_runs_on_approval_through_the_write_login()
    {
        RequireServer();
        var host = Host();
        var pending = await host.Queue.SubmitAsync(Req.Call("sql.write",
            "{\"sql\":\"UPDATE customers_t SET city = @c WHERE city = @old\",\"parameters\":{\"c\":\"Berlin\",\"old\":\"London\"}}"));
        Assert.Equal(ActionStatus.Pending, pending.Status);
        Assert.Contains("UPDATE would affect 2 row(s)", pending.PreviewJson);
        Assert.Equal(0, await Count("customers_t", "city = 'Berlin'"));

        var done = await host.Queue.ReviewAsync(pending.Id, Req.Approve());
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal(2, await Count("customers_t", "city = 'Berlin'"));
    }

    [Fact]
    public async Task Delete_is_queued_previewed_and_runs_on_approval()
    {
        RequireServer();
        var host = Host();
        var pending = await host.Queue.SubmitAsync(Req.Call("sql.delete",
            "{\"sql\":\"DELETE FROM customers_t WHERE id = @id\",\"parameters\":{\"id\":1}}"));
        Assert.Contains("DELETE would affect 1 row(s)", pending.PreviewJson);
        Assert.Equal(3, await Count("customers_t"));

        await host.Queue.ReviewAsync(pending.Id, Req.Approve());
        Assert.Equal(2, await Count("customers_t"));
    }

    [Fact]
    public async Task Decimals_dates_and_nulls_come_back_as_values()
    {
        RequireServer();
        var host = Host();
        var q = await host.Queue.SubmitAsync(Req.Call("sql.query", System.Text.Json.JsonSerializer.Serialize(new { sql = ValuesQuery })));
        Assert.True(q.Status == ActionStatus.Succeeded, q.Error?.Message);
        Assert.Contains("12.5", q.ResultJson);
        Assert.Contains("2026-01-02", q.ResultJson);
    }
}

public sealed class SqlServerProviderTests : SqlServerTests
{
    protected override string Env => "SQLSERVER";
    protected override SqlProvider Provider => SqlProviders.SqlServer;
    protected override DbConnection Create(string connectionString) => new SqlConnection(connectionString);

    public override IReadOnlyList<string> Bypasses =>
    [
        "SELECT [a]]'] ; DELETE FROM accounts WHERE 1=1; SELECT '",
        "SELECT 1 /* /* */ ' */ ; DELETE FROM accounts WHERE 1=1; -- '",
    ];
}

public sealed class PostgresProviderTests : SqlServerTests
{
    protected override string Env => "POSTGRES";
    protected override SqlProvider Provider => SqlProviders.Postgres;
    protected override DbConnection Create(string connectionString) => new NpgsqlConnection(connectionString);

    public override IReadOnlyList<string> Bypasses =>
    [
        "SELECT $$ ' $$; DELETE FROM accounts WHERE true; SELECT ' ",
        "SELECT $tag$ ' $tag$; DELETE FROM accounts WHERE true; SELECT ' ",
        "SELECT E'a\\'' ; DELETE FROM accounts WHERE true; SELECT ' '",
        "SELECT 'a\\'' ; DELETE FROM accounts WHERE true; SELECT ' '",
        "SELECT 1 /* /* */ ' */ ; DELETE FROM accounts WHERE true; -- '",
    ];
}

public sealed class MySqlProviderTests : SqlServerTests
{
    protected override string Env => "MYSQL";
    protected override SqlProvider Provider => SqlProviders.MySql;
    protected override DbConnection Create(string connectionString) => new MySqlConnection(connectionString);

    public override IReadOnlyList<string> Bypasses =>
    [
        "SELECT 'a\\'' ; DELETE FROM accounts WHERE 1=1; SELECT ' '",
        "SELECT \"a\\\"\" ; DELETE FROM accounts WHERE 1=1; SELECT \" \"",
        "SELECT 1 # '\n; DELETE FROM accounts WHERE 1=1; -- '",
        "SELECT 1 /*! ; DELETE FROM accounts WHERE 1=1 */",
        "SELECT 1 /*M! ; DELETE FROM accounts WHERE 1=1 */",
        "SELECT 1 --;DELETE FROM accounts WHERE 1=1",
    ];
}

/// <summary>MariaDB 11.4 (LTS) through the same driver and dialect as MySQL, under the <c>mariadb</c> provider name.</summary>
public sealed class MariaDbProviderTests : SqlServerTests
{
    protected override string Env => "MARIADB";
    protected override SqlProvider Provider => SqlProviders.MariaDb;
    protected override DbConnection Create(string connectionString) => new MySqlConnection(connectionString);

    public override IReadOnlyList<string> Bypasses =>
    [
        "SELECT 'a\\'' ; DELETE FROM accounts WHERE 1=1; SELECT ' '",
        "SELECT \"a\\\"\" ; DELETE FROM accounts WHERE 1=1; SELECT \" \"",
        "SELECT 1 # '\n; DELETE FROM accounts WHERE 1=1; -- '",
        "SELECT 1 /*M! ; DELETE FROM accounts WHERE 1=1 */",           // MariaDB's own executable comment
        "SELECT 1 /*M!100100 ; DELETE FROM accounts WHERE 1=1 */",     // with a version
        "SELECT 1 --;DELETE FROM accounts WHERE 1=1",
    ];
}

/// <summary>Oracle 23ai (Oracle Database Free): DROP TABLE IF EXISTS, multi-row VALUES and SELECT without FROM need 23ai.</summary>
public sealed class OracleProviderTests : SqlServerTests
{
    protected override string Env => "ORACLE";
    protected override SqlProvider Provider => OracleSqlProvider.Provider;
    protected override DbConnection Create(string connectionString) => new OracleConnection(connectionString);
    protected override string ValuesQuery => "SELECT CAST(12.50 AS NUMBER(10,2)) AS d, DATE '2026-01-02' AS dt, NULL AS n FROM dual";

    public override IReadOnlyList<string> Bypasses =>
    [
        "SELECT q'[ ' ]' ; DELETE FROM accounts WHERE 1=1; SELECT ' ' FROM dual",
        "SELECT nq'< ' >' ; DELETE FROM accounts WHERE 1=1; SELECT ' ' FROM dual",
        "SELECT UTL_HTTP.REQUEST('http://127.0.0.1:9') FROM dual",
    ];
}
