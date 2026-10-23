using Oracle.ManagedDataAccess.Client;

namespace OpenTail.Modules.Sql.Oracle;

/// <summary>
/// <c>"Provider": "oracle"</c> for the Sql module. The module finds <see cref="Provider"/> by name when this package is in the
/// deployment; the guard's Oracle rules (<c>q'...'</c> quoting, <c>DBMS_</c> and <c>UTL_</c> packages) live in <see cref="SqlGuard"/>.
/// <code>
/// "Config": { "Provider": "oracle", "ConnectionString": "User Id=agent_ro;Data Source=db1:1521/SALES" },
/// "Credentials": { "password": { "Env": "SALES_DB_PASSWORD", "Kind": "Secret" } }
/// </code>
/// </summary>
public static class OracleSqlProvider
{
    /// <summary>
    /// The schema tool's query: columns of tables and views in schemas that are not Oracle's own (<c>ORACLE_MAINTAINED</c>, 12c and
    /// later). Quoted aliases, because Oracle upper-cases unquoted names.
    /// </summary>
    public const string SchemaQuery =
        "SELECT c.owner AS \"table_schema\", c.table_name AS \"table_name\", c.column_name AS \"column_name\", c.data_type AS \"data_type\" " +
        "FROM all_tab_columns c JOIN all_users u ON u.username = c.owner WHERE u.oracle_maintained = 'N' " +
        "ORDER BY c.owner, c.table_name, c.column_id";

    public static readonly SqlProvider Provider = new()
    {
        Name = "oracle",
        Dialect = SqlDialect.Oracle,
        Factory = OracleClientFactory.Instance,
        Package = "OpenTail.Modules.Sql.Oracle",
        ParameterStyle = SqlParameterStyle.ColonName,
        // ODP.NET binds by position unless told otherwise, even when parameters have names.
        PrepareCommand = command => { if (command is OracleCommand oracle) oracle.BindByName = true; },
        // Must open the transaction; ODP.NET commits each statement outside an explicit one.
        ReadOnlyStatement = "SET TRANSACTION READ ONLY",
        ReadOnlyStatementInTransaction = true,
        // ORA-01466: the read-only transaction's snapshot predates a change to a table's definition (a table just created,
        // truncated or rebuilt by a load job). A new transaction a few seconds later sees the new definition: Oracle maps change
        // numbers to time coarsely, so one started within about three seconds of the change can still fail.
        RetryQueryOn = e => e is OracleException { Number: 1466 },
        AddRowLimit = true,
        RowLimitClause = "FETCH FIRST {0} ROWS ONLY",
        SchemaQuery = SchemaQuery,
        Notes = ["Oracle 12c or later (FETCH FIRST, ORACLE_MAINTAINED). Queries run in a read-only transaction and get FETCH FIRST when they have no limit."],
    };
}
