# Guide: querying Databricks from the Host

Databricks is a Sql module provider, `"Provider": "databricks"`, in its own package: `OpenTail.Modules.Sql.Databricks`. There is
no driver. The package is a small ADO.NET adapter over Databricks' **SQL Statement Execution API** (`/api/2.0/sql/statements`), so
the Sql module's guard, row caps, schema tool, policy and audit apply as they do for any database. Unlike the database drivers, its
traffic goes through the Host's guarded HTTP client: the module reaches only the hosts in its `Network.Hosts`.

**State: Tested, not Verified.** Checked against a stub of the API (requests, polling, result chunks, cancel on timeout, OAuth,
the network allow-list); not yet against a real workspace. Items marked *to verify* need one.

## What the agent gets

`<id>.schema` and `<id>.query`, read-only (`AllowWrites` is refused). For each query the provider:

- rewrites `@name` to Databricks' `:name` markers and sends each value with a type (`STRING`, `BIGINT`, `DOUBLE`, `BOOLEAN`);
- adds `LIMIT MaxRows + 1` when the query has no limit of its own;
- tags it with `query_tags` `opentail` = the action id (query tags are in public preview), visible in the query history system
  table;
- waits up to 10 seconds in the first request, then polls; past `CommandTimeoutSeconds` it calls the API's cancel, so the
  warehouse stops the statement;
- converts values by column type (numbers, booleans, dates, timestamps); complex types (`ARRAY`, `MAP`, `STRUCT`) come back as text.

What the guard refuses on Databricks, beyond anything that is not a single SELECT:

- backslashes inside `'...'` and `"..."` (both are strings in Databricks SQL, with backslash escapes), and `r'...'` raw strings;
- `$` outside literals and `${` anywhere, because Spark substitutes `${var}` in the text, string literals included;
- `http_request`, `read_files` and `cloud_files`, which reach URLs and files past the table grants;
- nested comments.

Backticks quote identifiers; `[ ]` is element access.

## 1. In Databricks

1. Create a **service principal** in the workspace and an **OAuth secret** for it (machine-to-machine). A personal access token
   also works, but ties the agent to a person.
2. Give it a **small SQL warehouse** of its own (serverless, the smallest size, auto-stop after a few minutes) and *Can use* on it.
3. Grant **read only**, in Unity Catalog:

   ```sql
   GRANT USE CATALOG ON CATALOG main TO `<service principal application id>`;
   GRANT USE SCHEMA ON SCHEMA main.sales TO `<service principal application id>`;
   GRANT SELECT ON SCHEMA main.sales TO `<service principal application id>`;
   ```

   Row filters and column masks on those tables apply to the agent as they would to a person.
4. Optionally set a statement timeout on the warehouse (the `STATEMENT_TIMEOUT` SQL configuration parameter) as a ceiling under
   the Host's own.

## 2. In the Host deployment

Reference `OpenTail.Modules.Sql.Databricks`, then:

```json
{
  "Type": "sql",
  "Id": "lakehouse",
  "Config": {
    "Provider": "databricks",
    "ConnectionString": "Host=https://dbc-1234abcd-5678.cloud.databricks.com;WarehouseId=<warehouse id>;Catalog=main;Schema=sales;ClientId=<application id>",
    "MaxRows": 200,
    "CommandTimeoutSeconds": 60
  },
  "Credentials": {
    "password": { "Env": "DATABRICKS_AGENT_SECRET", "Kind": "Secret" }
  },
  "Network": { "Hosts": [ "dbc-1234abcd-5678.cloud.databricks.com" ] }
}
```

- `password` holds the OAuth secret when `ClientId` is set, or a personal access token without it. Never put either in the
  connection string; the module refuses to start if you do.
- `WarehouseId` is in the warehouse's *Connection details*; `HttpPath=/sql/1.0/warehouses/<id>` works too.
- `Network.Hosts` must list the workspace, or every call is refused before it leaves the Host. On Azure the host is
  `adb-<id>.<n>.azuredatabricks.net`.
- `Network.MaxResponseBytes` (default 4 MB) caps each response; with `MaxRows` at 200 that is rarely reached, but very wide rows
  may need more.

## 3. Cost

A SQL warehouse bills DBUs while it runs. The ceilings, from hardest to softest:

| Layer | Set by | What it stops |
|---|---|---|
| Warehouse size and auto-stop | Workspace admin | The rate of spend, and idle time. |
| Budget policies and alerts on the warehouse | Account admin | Spend past a budget (alerts; enforcement depends on the plan, *to verify*). |
| `STATEMENT_TIMEOUT` on the warehouse | Workspace admin | Any single long statement. |
| `CommandTimeoutSeconds` | Host config | Statements past it, cancelled through the API. |
| Added LIMIT | Host, automatic | Producing rows nobody reads. |

## 4. Trying it

**Databricks Free Edition** is free and does not expire. It has one serverless SQL warehouse (2X-Small) and sample data in the
`samples` catalog. *To verify on Free Edition:* that a service principal with an OAuth secret, or a personal access token, can call
the Statement Execution API, and that query tags are recorded.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Provider 'databricks' was not found: reference package OpenTail.Modules.Sql.Databricks" | The deployment does not reference the package. |
| Every call fails and nothing reaches Databricks | The workspace is missing from `Network.Hosts`. |
| "Databricks sign-in failed (401)" | Wrong `ClientId` or secret, or the secret expired. |
| "Databricks returned 403" | The service principal lacks *Can use* on the warehouse. |
| `TABLE_OR_VIEW_NOT_FOUND` | No `USE CATALOG` / `USE SCHEMA` / `SELECT` grant, or the name needs its catalog and schema. |
| "ran past the timeout and was cancelled" | The statement took longer than `CommandTimeoutSeconds`; narrow it or raise the timeout. |
