using System.Data.Common;

namespace OpenTail.Modules.Sql;

/// <summary>How the driver expects parameters. The model always writes <c>@name</c>; the module rewrites it for the driver.</summary>
public enum SqlParameterStyle
{
    /// <summary><c>@name</c>, bound by name (SQLite, SQL Server, Postgres, MySQL).</summary>
    AtName,

    /// <summary><c>?</c>, bound in order (Snowflake, ODBC).</summary>
    Positional,

    /// <summary><c>:name</c>, bound by name (Oracle; the provider's <see cref="SqlProvider.PrepareCommand"/> turns name binding on).</summary>
    ColonName,
}

/// <summary>
/// A database <c>Config.Provider</c> can name: where its driver is, and the defaults the module applies to it. The built-in ones
/// are in <see cref="SqlProviders"/>. An add-on package (such as <c>OpenTail.Modules.Sql.Oracle</c>) declares its own with a
/// public static <c>Provider</c> field, references its driver and sets <see cref="Factory"/> directly.
/// </summary>
public sealed record SqlProvider
{
    public required string Name { get; init; }

    /// <summary>The quoting and comment rules the guard applies. The rules live in <see cref="SqlGuard"/>, whoever declares the provider.</summary>
    public required SqlDialect Dialect { get; init; }

    /// <summary>The driver's factory, for providers whose package references the driver.</summary>
    public DbProviderFactory? Factory { get; init; }

    /// <summary>
    /// Or a factory made per module instance, for drivers that call HTTP through the Host's guarded client
    /// (<see cref="ModuleContext.Http"/>, limited to the module's <c>Network.Hosts</c>).
    /// </summary>
    public Func<ModuleContext, DbProviderFactory>? CreateFactory { get; init; }

    /// <summary>Otherwise the factory's assembly-qualified type name, resolved when the module starts (it needs a static <c>Instance</c>).</summary>
    public string? FactoryType { get; init; }

    /// <summary>The package a deployment references to get the driver, named in the error when it is missing.</summary>
    public string? Package { get; init; }

    public SqlParameterStyle ParameterStyle { get; init; }

    /// <summary>Run each query after this statement and roll back afterwards (a second layer behind the read-only login). Null: none.</summary>
    public string? ReadOnlyStatement { get; init; }

    /// <summary>The read-only statement must be the first statement of an ADO.NET transaction (Oracle, whose driver otherwise commits it at once).</summary>
    public bool ReadOnlyStatementInTransaction { get; init; }

    /// <summary>Add <see cref="RowLimitClause"/> to queries without a limit of their own (warehouses that bill by compute).</summary>
    public bool AddRowLimit { get; init; }

    /// <summary>The clause appended on a new line; <c>{0}</c> is <c>MaxRows + 1</c>.</summary>
    public string RowLimitClause { get; init; } = "LIMIT {0}";

    /// <summary>Set the command's <c>QueryTag</c> property to <c>opentail:&lt;action id&gt;</c> (Snowflake).</summary>
    public bool TagQueries { get; init; }

    /// <summary>A statement run before each query to tag it, <c>{0}</c> being the tag (Redshift's <c>SET query_group</c>). Null: none.</summary>
    public string? TagStatement { get; init; }

    /// <summary>Called on every command the module creates, before parameters are added.</summary>
    public Action<DbCommand>? PrepareCommand { get; init; }

    /// <summary>Called on every connection string the module builds, after credentials are added.</summary>
    public Action<DbConnectionStringBuilder>? PrepareConnection { get; init; }

    /// <summary>Refuse <c>AllowWrites</c>.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The schema tool's query. Null: <see cref="SqlModuleOptions.SchemaQueryFor"/> the dialect.</summary>
    public string? SchemaQuery { get; init; }

    /// <summary>Lines added to the module's manifest notes.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The providers the Sql module knows by name. Each deployment references the driver package itself.</summary>
public static class SqlProviders
{
    public static readonly SqlProvider SqlServer = new()
    {
        Name = "sqlserver",
        Dialect = SqlDialect.SqlServer,
        FactoryType = "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
        Package = "Microsoft.Data.SqlClient",
    };

    public static readonly SqlProvider Postgres = new()
    {
        Name = "postgres",
        Dialect = SqlDialect.Postgres,
        FactoryType = "Npgsql.NpgsqlFactory, Npgsql",
        Package = "Npgsql",
        ReadOnlyStatement = "START TRANSACTION READ ONLY",
    };

    public static readonly SqlProvider MySql = new()
    {
        Name = "mysql",
        Dialect = SqlDialect.MySql,
        FactoryType = "MySqlConnector.MySqlConnectorFactory, MySqlConnector",
        Package = "MySqlConnector",
        ReadOnlyStatement = "START TRANSACTION READ ONLY",
    };

    public static readonly SqlProvider Snowflake = new()
    {
        Name = "snowflake",
        Dialect = SqlDialect.Snowflake,
        FactoryType = "Snowflake.Data.Client.SnowflakeDbFactory, Snowflake.Data",
        Package = "Snowflake.Data",
        ParameterStyle = SqlParameterStyle.Positional,
        AddRowLimit = true,
        TagQueries = true,
        ReadOnly = true,
        SchemaQuery = SqlModuleOptions.SnowflakeSchemaQuery,
        Notes =
        [
            "Snowflake: every query runs on a warehouse and costs credits. Give the role its own X-Small warehouse with auto-suspend, " +
            "STATEMENT_TIMEOUT_IN_SECONDS and a resource monitor; queries get a LIMIT and the tag opentail:<action id>.",
        ],
    };

    /// <summary>Amazon Redshift through Npgsql in its Redshift compatibility mode. Not yet tested against a real cluster.</summary>
    public static readonly SqlProvider Redshift = new()
    {
        Name = "redshift",
        Dialect = SqlDialect.Postgres,
        FactoryType = "Npgsql.NpgsqlFactory, Npgsql",
        Package = "Npgsql",
        ReadOnlyStatement = "START TRANSACTION READ ONLY",
        AddRowLimit = true,
        TagStatement = "SET query_group TO '{0}'",
        PrepareConnection = b =>
        {
            if (!b.ContainsKey("Server Compatibility Mode")) b["Server Compatibility Mode"] = "Redshift";
        },
        SchemaQuery = SqlModuleOptions.RedshiftSchemaQuery,
        Notes =
        [
            "Redshift: Serverless bills per query (RPU-seconds); give the user a workgroup with a usage limit. Queries get a LIMIT and " +
            "the query group opentail:<action id> (STL_QUERY.label).",
        ],
    };

    /// <summary>
    /// Built-in providers, and add-ons by the type that declares them (a public static <c>Provider</c> field) and their package.
    /// </summary>
    internal static readonly Dictionary<string, SqlProvider> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        [SqlServer.Name] = SqlServer,
        [Postgres.Name] = Postgres,
        [MySql.Name] = MySql,
        [Snowflake.Name] = Snowflake,
        [Redshift.Name] = Redshift,
    };

    internal static readonly Dictionary<string, (string TypeName, string Package)> AddOns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oracle"] = ("OpenTail.Modules.Sql.Oracle.OracleSqlProvider, OpenTail.Modules.Sql.Oracle", "OpenTail.Modules.Sql.Oracle"),
        ["databricks"] = ("OpenTail.Modules.Sql.Databricks.DatabricksSqlProvider, OpenTail.Modules.Sql.Databricks", "OpenTail.Modules.Sql.Databricks"),
    };
}
