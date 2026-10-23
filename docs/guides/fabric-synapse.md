# Guide: querying Microsoft Fabric or Azure Synapse from the Host

Fabric warehouses, Fabric SQL analytics endpoints and Synapse SQL pools speak SQL Server's protocol, so they use the Sql module's
`sqlserver` provider with Microsoft Entra ID sign-in. No Fabric-specific code is involved.

**State: not tested against Fabric or Synapse.** The `sqlserver` provider itself is tested against SQL Server in CI. Items marked
*to verify* need a real tenant.

## What the agent gets

`<id>.schema` and `<id>.query`, read-only, as for any Sql target. What differs from Snowflake and Redshift:

- **No added row limit.** T-SQL has no `LIMIT`; the module still returns at most `MaxRows` rows, and the model can write `TOP`.
- **No read-only transaction.** SQL Server has no such mode; the login's permissions are the boundary.
- **No query tag** yet. The Host's audit has the statement and the action id.

## 1. In Entra ID and Fabric (or Synapse)

1. Register an app (a service principal) for the agent and create a client secret, or use a managed identity where the Host runs
   in Azure.
2. **Fabric:** let service principals use Fabric (tenant setting *Service principals can use Fabric APIs*), then give the app the
   narrowest access that lets it read: share the warehouse with it with *Read data*, rather than a workspace role. One
   third-party guide says a workspace role of Member or Contributor is needed for the SQL endpoint (*to verify*). If so, keep
   writes out with T-SQL: `DENY INSERT, UPDATE, DELETE, ALTER TO [app name]`.
3. **Synapse serverless:** create a database user for the app (`CREATE USER [app name] FROM EXTERNAL PROVIDER`) and add it to
   `db_datareader` only.

## 2. In the Host deployment

Reference `Microsoft.Data.SqlClient` **and `Microsoft.Data.SqlClient.Extensions.Azure`**: since SqlClient 7, Entra ID sign-in is
in the extension package. Then:

```json
{
  "Type": "sql",
  "Id": "fabric",
  "Config": {
    "Provider": "sqlserver",
    "ConnectionString": "Server=<endpoint>.datawarehouse.fabric.microsoft.com;Database=<warehouse>;Authentication=Active Directory Service Principal;User Id=<app client id>;Encrypt=True",
    "MaxRows": 200,
    "CommandTimeoutSeconds": 30
  },
  "Credentials": {
    "password": { "Env": "FABRIC_AGENT_SECRET", "Kind": "Secret" }
  }
}
```

- The client secret goes in the `password` credential; the module adds it as `Password` when it connects. Never put it in the
  connection string (the module refuses to start if you do).
- With a managed identity, use `Authentication=Active Directory Managed Identity` and no credential.
- Synapse serverless: `Server=<workspace>-ondemand.sql.azuresynapse.net`; dedicated pool: `Server=<workspace>.sql.azuresynapse.net`.

## 3. Cost

| Platform | How it bills | The ceiling |
|---|---|---|
| Fabric | Capacity units you have already bought; heavy queries slow other work on the capacity, then get throttled | Capacity size; a separate capacity for agents keeps them from slowing people down |
| Synapse serverless | Per terabyte of data processed | Synapse's cost control: daily, weekly and monthly limits on data processed (`sp_set_data_processed_limit`) |
| Synapse dedicated | The pool's size, while it runs | Pause the pool when not in use |

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Cannot find an authentication provider" | The deployment does not reference `Microsoft.Data.SqlClient.Extensions.Azure`. |
| "must not contain a password" at start-up | The secret is in the connection string; move it to the `password` credential. |
| Login fails for the service principal | The tenant setting is off, or the app has no access to the warehouse or workspace. |
