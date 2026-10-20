using Microsoft.Data.SqlClient;
using OpenTail.Host.Admin;
using OpenTail.Modules.Sql;

//   SQL_READ_CONNECTION=Server=db;Database=sales;User Id=ot_reader;Password=...;Encrypt=True
//       a login with SELECT (db_datareader) only: schema and query run through it.
//   SQL_WRITE_CONNECTION=Server=db;Database=sales;User Id=ot_writer;Password=...;Encrypt=True
//       optional: a second login allowed to INSERT, UPDATE and DELETE. Without it the server is read only.
// Run: opentail-host-sqlserver --policy policy.sqlserver.json   (with a write login: --policy policy.sqlserver-writes.json)
// Pass connection strings through the environment or a secrets store, never on the command line.
static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Set the environment variable {name}.");

return await DeploymentRunner.RunAsync(args, "opentail-host-sqlserver", builder =>
{
    var read = Required("SQL_READ_CONNECTION");
    var write = Environment.GetEnvironmentVariable("SQL_WRITE_CONNECTION") is { Length: > 0 } w ? w : null;
    builder.AddModule(new SqlModule(new SqlModuleOptions
    {
        Dialect = SqlDialect.SqlServer,
        ConnectionFactory = () => new SqlConnection(read),
        WriteConnectionFactory = write is null ? null : () => new SqlConnection(write),
        AllowWrites = write is not null,
    }));
});
