namespace OpenTail.Modules.Sql.Databricks;

/// <summary>
/// <c>"Provider": "databricks"</c> for the Sql module: Databricks SQL warehouses through the Statement Execution API, over the
/// Host's guarded HTTP client, so the module's <c>Network.Hosts</c> must list the workspace. The guard's Databricks rules live in
/// <see cref="SqlGuard"/>.
/// <code>
/// "Config": { "Provider": "databricks", "ConnectionString": "Host=https://dbc-1234.cloud.databricks.com;WarehouseId=abc123;Catalog=main;ClientId=..." },
/// "Credentials": { "password": { "Env": "DATABRICKS_SECRET", "Kind": "Secret" } },
/// "Network": { "Hosts": [ "dbc-1234.cloud.databricks.com" ] }
/// </code>
/// </summary>
public static class DatabricksSqlProvider
{
    /// <summary>Columns of the connection's catalog (Unity Catalog's <c>information_schema</c>), without the information schema itself.</summary>
    public const string SchemaQuery =
        "SELECT table_schema AS table_schema, table_name AS table_name, column_name AS column_name, data_type AS data_type " +
        "FROM information_schema.columns WHERE table_schema <> 'information_schema' ORDER BY table_schema, table_name, ordinal_position";

    public static readonly SqlProvider Provider = new()
    {
        Name = "databricks",
        Dialect = SqlDialect.Databricks,
        CreateFactory = context => new DatabricksClientFactory(context.Http.Create()),
        Package = "OpenTail.Modules.Sql.Databricks",
        ParameterStyle = SqlParameterStyle.ColonName,
        AddRowLimit = true,
        TagQueries = true,
        ReadOnly = true,
        SchemaQuery = SchemaQuery,
        Notes =
        [
            "Databricks: every query runs on a SQL warehouse and costs DBUs. Give the service principal a small warehouse with auto-stop " +
            "and only USE CATALOG, USE SCHEMA and SELECT grants; queries get a LIMIT and the query tag opentail:<action id>. " +
            "List the workspace host in Network.Hosts.",
        ],
    };
}
