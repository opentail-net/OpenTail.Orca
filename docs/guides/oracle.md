# Guide: querying Oracle Database from the Host

Oracle is a Sql module provider, `"Provider": "oracle"`, that lives in its own package: `OpenTail.Modules.Sql.Oracle`. The
package brings Oracle's driver (`Oracle.ManagedDataAccess.Core`) and Oracle's settings; the guard's Oracle rules stay in the Sql
module, because the guard is one parser and must know every dialect it protects.

**State: Verified** against Oracle Database Free 23ai in the `sql-servers` workflow (a read login and a write login), 2026-10-23.
An intermittent failure in CI was ORA-01466 (see troubleshooting); the provider now retries it, after 1 s and 3 s. Oracle 12c or later
is required.

## What the agent gets

`<id>.schema` and `<id>.query`, and with `AllowWrites` the previewed, queued `<id>.write` and `<id>.delete`, as for SQL Server,
Postgres and MySQL. What the provider adds:

- **`@name` becomes `:name`**, bound by name (ODP.NET binds by position unless told otherwise).
- **A read-only transaction:** each query runs after `SET TRANSACTION READ ONLY` and is rolled back.
- **`FETCH FIRST MaxRows + 1 ROWS ONLY`** when the query has no limit of its own.
- **A trailing semicolon is dropped**, which Oracle's driver would refuse.
- **Schema tool:** columns of tables and views in schemas that are not Oracle's own (`ALL_USERS.ORACLE_MAINTAINED = 'N'`).

What the guard refuses on Oracle, beyond anything that is not a single statement of the allowed kind:

- `q'...'` and `nq'...'` alternative quoting.
- `DBMS_` and `UTL_` packages: `UTL_HTTP` and `UTL_TCP` reach the network, `DBMS_PIPE` waits, `DBMS_SCHEDULER` starts jobs.
- PL/SQL blocks (`BEGIN`, `DECLARE`) and `CALL`.
- Database links: `@remote` is read as a parameter, and one with no value is refused.

## 1. In Oracle

```sql
CREATE USER ot_agent IDENTIFIED BY "<password>";
GRANT CREATE SESSION TO ot_agent;
GRANT SELECT ON sales.orders TO ot_agent;       -- one grant per table or view; avoid SELECT ANY TABLE outside tests
ALTER USER ot_agent PROFILE agent_profile;      -- a profile with CPU_PER_CALL and LOGICAL_READS_PER_CALL limits
```

For unqualified table names, either create synonyms or a logon trigger that sets `CURRENT_SCHEMA` (the CI setup does this).

With `AllowWrites`, give writes their own user (`WriteConnectionString` and the `write-password` credential) with `INSERT`,
`UPDATE` and `DELETE` on the tables it may change, and keep the query user read-only.

## 2. In the Host deployment

Reference `OpenTail.Modules.Sql.Oracle` (it brings the driver), then:

```json
{
  "Type": "sql",
  "Id": "sales",
  "Config": {
    "Provider": "oracle",
    "ConnectionString": "User Id=ot_agent;Data Source=db1.example.com:1521/SALES",
    "MaxRows": 200,
    "CommandTimeoutSeconds": 30,
    "Schemas": [ "SALES" ]
  },
  "Credentials": {
    "password": { "Env": "SALES_DB_PASSWORD", "Kind": "Secret" }
  }
}
```

Schema names in `Schemas` are compared without case, so `sales` also matches Oracle's `SALES`.

## 3. Trying it

Oracle Database Free runs in a container: `docker run -p 1521:1521 -e ORACLE_PASSWORD=<pw> gvenzl/oracle-free:23-slim-faststart`.
The CI setup creates the users; see `.github/workflows/sql-servers.yml`. Set `OTAIL_SQL_ORACLE_READ` and `OTAIL_SQL_ORACLE_WRITE`
and run `OpenTail.Orca.Tests.SqlServers`. Oracle Cloud's Always Free Autonomous Database is another option, with a wallet.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Provider 'oracle' was not found: reference package OpenTail.Modules.Sql.Oracle" | The deployment does not reference the package. |
| ORA-00942 (table or view does not exist) | The user has no grant on the table, or the name needs the schema (`sales.orders`) or a synonym. |
| ORA-01008 (not all variables bound) | The query uses a `:name` written by hand; use `@name` with a value in parameters. |
| ORA-00933 near `FETCH` | The database is older than 12c. |
| ORA-01466 (table definition has changed) | A table was created, truncated or rebuilt just as the read-only transaction started. The provider retries such a query in a new transaction after 1 s and again after 3 s; a failure after that means the table is still changing. |
