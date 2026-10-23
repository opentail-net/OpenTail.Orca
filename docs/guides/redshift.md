# Guide: querying Amazon Redshift from the Host

Redshift is a Sql module provider, `"Provider": "redshift"`, through the Postgres driver (Npgsql) in its Redshift compatibility
mode.

**State: Tested, not Verified.** Its defaults are checked against a recording connection; it has not run against a real cluster.
Items marked *to verify* need one.

## What the agent gets

`<id>.schema` and `<id>.query`, read-only. What the provider adds to each query:

- `Server Compatibility Mode=Redshift` in the connection string, unless the operator set the mode.
- `SET query_group TO 'opentail:<action id>'` before the query, so `STL_QUERY.label` and `SYS_QUERY_HISTORY` tie each query to
  a Host action (*to verify*: the column that shows it in Serverless).
- `START TRANSACTION READ ONLY` around the query, rolled back afterwards (*to verify* that Redshift accepts it; if not, set
  `"ReadOnlyQueryTransaction": false` and rely on the user's grants).
- `LIMIT MaxRows + 1` when the query has no limit of its own.

The guard applies Postgres's rules (no `$` quoting, no `E'...'`, no backslashes in literals).

## 1. In Redshift

```sql
CREATE USER ot_agent PASSWORD '<password>';
GRANT USAGE ON SCHEMA sales TO ot_agent;
GRANT SELECT ON ALL TABLES IN SCHEMA sales TO ot_agent;
ALTER DEFAULT PRIVILEGES IN SCHEMA sales GRANT SELECT ON TABLES TO ot_agent;
ALTER USER ot_agent SET statement_timeout TO 60000;  -- milliseconds
```

On Serverless, set a usage limit on the workgroup (RPU-hours per day, week or month) so a busy agent cannot spend past it.

## 2. In the Host deployment

Reference `Npgsql`, then:

```json
{
  "Type": "sql",
  "Id": "redshift",
  "Config": {
    "Provider": "redshift",
    "ConnectionString": "Host=<workgroup>.<account>.<region>.redshift-serverless.amazonaws.com;Port=5439;Database=dev;Username=ot_agent;SSL Mode=Require",
    "MaxRows": 200,
    "CommandTimeoutSeconds": 30,
    "Schemas": [ "sales" ]
  },
  "Credentials": {
    "password": { "Env": "REDSHIFT_AGENT_PASSWORD", "Kind": "Secret" }
  }
}
```

## 3. Trying it

Redshift Serverless offers new accounts a free trial credit (*to verify* the current amount). A suite like the Snowflake one, gated
on environment variables, is the next step once an account exists.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Provider type 'Npgsql.NpgsqlFactory, Npgsql' was not found" | The deployment does not reference `Npgsql`. |
| Every query fails at `START TRANSACTION READ ONLY` | Redshift refused the read-only transaction; set `"ReadOnlyQueryTransaction": false`. |
| Errors loading types at connect | The compatibility mode was overridden; remove `Server Compatibility Mode` from the connection string. |
