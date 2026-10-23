using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using OpenTail.Modules.Sql;

namespace OpenTail.Host.Tests;

/// <summary>
/// Snowflake as a read-only Sql target, steps S1 and S2: the guard's Snowflake rules, positional binding, the added LIMIT and the
/// query tag. Checked against a recording connection; the real driver needs an account (step S5).
/// </summary>
public class SnowflakeTests
{
    [Theory]
    [InlineData("SELECT 1 // '\n; DELETE FROM accounts WHERE 1=1; -- '")]                  // // comments
    [InlineData("SELECT $$ ' $$; DELETE FROM accounts WHERE 1=1; SELECT ' ")]               // dollar quotes
    [InlineData("SELECT 'a\\'' ; DELETE FROM accounts WHERE 1=1; SELECT ' '")]              // backslash escapes
    [InlineData("SELECT SYSTEM$CANCEL_ALL_QUERIES(1)")]                                       // SYSTEM$ functions
    [InlineData("SELECT $1 FROM t")]                                                          // positional columns, stages
    [InlineData("SELECT * FROM IDENTIFIER($table)")]                                          // session variables
    [InlineData("SELECT x[ 1; DELETE FROM accounts WHERE 1=1; SELECT 1 ]")]                   // [ ] is not quoting here
    [InlineData("CALL refresh()")]
    [InlineData("COPY INTO @stage FROM t")]
    [InlineData("SHOW TABLES")]
    public void The_guard_refuses_what_Snowflake_would_read_differently(string sql)
    {
        Assert.Throws<ArgumentException>(() => SqlGuard.Classify(sql, SqlDialect.Snowflake));
        Assert.Throws<ArgumentException>(() => SqlGuard.Classify(sql)); // Unknown applies Snowflake's refusals too
    }

    [Theory]
    [InlineData("SELECT v['name'] FROM t")]
    [InlineData("SELECT \"Order\" FROM t WHERE note = 'it''s' -- comment")]
    [InlineData("WITH x AS (SELECT 1 AS n) SELECT n FROM x QUALIFY ROW_NUMBER() OVER (ORDER BY n) = 1")]
    public void The_guard_accepts_ordinary_Snowflake_queries(string sql) =>
        Assert.Equal(SqlKind.Select, SqlGuard.Classify(sql, SqlDialect.Snowflake));

    [Fact]
    public void Named_parameters_become_positional_outside_quotes_and_comments()
    {
        var sql = SqlGuard.RewriteParameters("SELECT '@no', \"@no\" FROM t /* @no */ WHERE a = @a AND b = @b OR a2 = @a -- @no", SqlParameterStyle.Positional, out var names);
        Assert.Equal("SELECT '@no', \"@no\" FROM t /* @no */ WHERE a = ? AND b = ? OR a2 = ? -- @no", sql);
        Assert.Equal(["a", "b", "a"], names);
    }

    [Theory]
    [InlineData("SELECT a FROM t", false)]
    [InlineData("SELECT a FROM (SELECT a FROM t LIMIT 5)", false)]       // an inner LIMIT does not limit the outer rows
    [InlineData("SELECT a FROM t WHERE b = 'LIMIT 5' -- LIMIT 5", false)]
    [InlineData("SELECT a FROM t ORDER BY a LIMIT 10", true)]
    [InlineData("SELECT TOP 10 a FROM t", true)]
    [InlineData("SELECT a FROM t FETCH FIRST 10 ROWS ONLY", true)]
    public void An_outer_limit_is_recognised(string sql, bool limited) => Assert.Equal(limited, SqlGuard.HasOuterLimit(sql));

    [Fact]
    public async Task A_query_is_sent_with_a_limit_positional_values_and_the_action_id_as_query_tag()
    {
        var sent = new List<RecordingCommand>();
        var host = Host(sent);

        var r = await host.Queue.SubmitAsync(Req.Call("wh.query",
            """{"sql":"SELECT region, SUM(amount) FROM sales WHERE year = @year AND region <> @skip GROUP BY region -- totals;","parameters":{"skip":"none","year":2026}}"""));

        Assert.Equal(ActionStatus.Succeeded, r.Status);
        var cmd = Assert.Single(sent);
        Assert.Equal("SELECT region, SUM(amount) FROM sales WHERE year = ? AND region <> ? GROUP BY region -- totals\nLIMIT 51", cmd.CommandText);
        Assert.Equal(["1", "2"], cmd.Parameters.Cast<DbParameter>().Select(p => p.ParameterName));
        Assert.Equal(["2026", "none"], cmd.Parameters.Cast<DbParameter>().Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("opentail:" + r.Id, cmd.QueryTag);
    }

    [Fact]
    public async Task A_query_with_its_own_limit_is_left_alone_and_a_missing_value_is_refused()
    {
        var sent = new List<RecordingCommand>();
        var host = Host(sent);

        await host.Queue.SubmitAsync(Req.Call("wh.query", """{"sql":"SELECT a FROM t LIMIT 5"}"""));
        Assert.Equal("SELECT a FROM t LIMIT 5", Assert.Single(sent).CommandText);

        var missing = await host.Queue.SubmitAsync(Req.Call("wh.query", """{"sql":"SELECT a FROM t WHERE b = @b"}"""));
        Assert.Equal(ActionStatus.Failed, missing.Status);
        Assert.Single(sent); // refused before anything was sent
    }

    [Fact]
    public async Task The_schema_query_keeps_lower_case_column_names_and_is_tagged()
    {
        var sent = new List<RecordingCommand>();
        var r = await Host(sent).Queue.SubmitAsync(Req.Call("wh.schema", "{}"));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        var cmd = Assert.Single(sent);
        Assert.Equal(SqlModuleOptions.SnowflakeSchemaQuery, cmd.CommandText);
        Assert.Contains("AS \"table_name\"", cmd.CommandText);
        Assert.Equal("opentail:" + r.Id, cmd.QueryTag);
    }

    [Fact]
    public async Task The_factory_keeps_Snowflake_read_only_and_names_the_driver_package()
    {
        static Task<ModuleLoadResult> Load(string config) => ModuleLoader.LoadAsync(
            ModulesConfig.Parse($$"""{ "Modules": [ { "Type": "sql", "Id": "wh", "Config": {{config}} } ] }""", null), ModuleLoadMode.Describe);

        var writes = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Load("""{ "Provider": "snowflake", "ConnectionString": "account=a;user=u", "AllowWrites": true }"""));
        Assert.Contains("read-only", writes.Message);

        // Secrets in the connection string are checked with the driver present: OpenTail.Host.Tests.SqlServers.
        var driver = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Load("""{ "Provider": "snowflake", "ConnectionString": "account=a;user=u" }"""));
        Assert.Contains("reference package Snowflake.Data", driver.Message);
    }

    private static OpenTailHost Host(List<RecordingCommand> sent) => new OpenTailHostBuilder()
        .AddModule(new SqlModule(new SqlModuleOptions(SqlProviders.Snowflake)
        {
            Name = "wh",
            ConnectionFactory = () => new RecordingConnection(sent),
            MaxRows = 50,
        }))
        .UsePolicy(PolicyConfig.Parse("""{ "DefaultReadMode": "Execute" }"""))
        .Build();

    internal sealed class RecordingConnection(List<RecordingCommand> sent) : DbConnection
    {
        private ConnectionState state;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "db";
        public override string DataSource => "snowflake";
        public override string ServerVersion => "0";
        public override ConnectionState State => state;
        public override void Open() => state = ConnectionState.Open;
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new RecordingCommand(sent) { Connection = this };
    }

    /// <summary>Like the driver's command: a QueryTag property. Records itself when run; returns one empty result.</summary>
    internal sealed class RecordingCommand(List<RecordingCommand> sent) : DbCommand
    {
        private readonly SqliteCommand parameters = new();
        public string? QueryTag { get; set; }
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() { sent.Add(this); return 0; }
        public override object? ExecuteScalar() { sent.Add(this); return null; }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => parameters.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            sent.Add(this);
            var table = new DataTable();
            table.Columns.Add("table_name");
            return table.CreateDataReader();
        }
    }
}
