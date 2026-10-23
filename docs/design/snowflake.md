# Design: Snowflake as a read-only Sql target

**Status:** S1 and S2 built 2026-10-23, **Tested, not Verified**: checked against a recording connection and the real driver's
factory, never against a Snowflake account. The owner wants the capability now, though no project needs a warehouse yet; when one
does, it may be the customer's. Facts still marked *to verify* need Snowflake's documentation or an account.

**Checked in the driver's source** (`snowflakedb/snowflake-connector-net`, 2026-10-23; the tests pin `Snowflake.Data` 6.3.0):
`SnowflakeDbFactory : DbProviderFactory` with a static `Instance`; a command timeout or a cancelled token sends an abort request
to the server (`SFStatement`, `/queries/v1/abort-request`); `SnowflakeDbCommand.QueryTag` tags one statement; parameters bind by
position (`?`, named `"1"`, `"2"`, ...), not by `@name`; a single statement per request unless `MULTI_STATEMENT_COUNT` says
otherwise; key-pair sign-in is `authenticator=snowflake_jwt` with `private_key` (PEM text) or `private_key_file`, and
`private_key_pwd`. The driver parses its connection string with `DbConnectionStringBuilder`, so a key set through that builder
arrives intact; the "replace = with ==" advice applies only to hand-written strings.

## Why, and what it is not

Agents asking a warehouse questions ("Q3 sales by region") is useful, and the Sql module already does governed, read-only
querying of SQL Server, Postgres and MySQL. Snowflake is the same shape with one new risk: **every query costs money**. A
warehouse bills compute while it runs, so a curious or looping agent can run up a bill without touching a single forbidden row.

Not in scope: loading data into a warehouse (that is ETL, done by tools built for it; if a project wants a landing zone, a Parquet
exporter to object storage is the cheap route, and it is in Connectivity's optional to-do), and writes of any kind.

## What exists

`OpenTail.Modules.Sql`: providers by name (`sqlserver`, `postgres`, `mysql`) or any `DbProviderFactory` type; `SqlGuard` parses
the statement and allows reads only; `MaxRows` (200), `MaxCellChars` (2,000), `CommandTimeoutSeconds` (15); a schema tool with a
schema allow-list; content marked untrusted; every call goes through the Host's policy, audit, adaptive limits and data labels.

## Design

1. **Provider.** `"Provider": "snowflake"`, through the official ADO.NET driver (`Snowflake.Data`, *to verify* that it exposes a
   `DbProviderFactory` with a static `Instance`; if not, a small factory adapter). A `SqlDialect.Snowflake` for the guard and the
   schema query (`INFORMATION_SCHEMA.COLUMNS` per database).
2. **Read-only is the role, not the hope.** Snowflake has no read-only transaction mode the module can switch on (*to verify*), so:
   a dedicated role with `USAGE` and `SELECT` only, a service user (key-pair authentication; Snowflake is retiring single-factor
   password logins, *to verify* the dates), and the guard as a second line. Row access and masking policies on that role apply as
   they would to a person, which is the point.
3. **Cost guards, in layers.**
   - *Snowflake side, set by the customer's admin, checked by the Host at start-up:* a dedicated, smallest warehouse (X-Small) for
     the agent role with a short auto-suspend; `STATEMENT_TIMEOUT_IN_SECONDS` and `STATEMENT_QUEUED_TIMEOUT_IN_SECONDS` on it; a
     resource monitor with a credit quota that suspends the warehouse. The Host refuses to start the module (or warns, by
     configuration) when the warehouse is larger than allowed or has no timeout.
   - *Host side, per call:* the module's own timeout (the driver cancels the query on the server, *to verify*), `MaxRows` with the
     query cancelled once enough rows are read, a `LIMIT` added when the statement has none, and `QUERY_TAG` set to the Host's
     action id so every query is attributable in Snowflake's history.
   - *Host side, per identity and day:* a **budget** in the units Snowflake reports after the fact (bytes scanned, elapsed time, or
     credits), read from query history by query tag, enforced through the adaptive-limits machinery that already exists: report
     only first, then queue or deny past the budget. The exact unit is an open question.
   - *Free help:* Snowflake's result cache answers an identical query within 24 hours without compute (*to verify*), so repeated
     agent questions cost little.
4. **Policy defaults.** Snowflake tools are marked as costly in the manifest, so the default policy can queue unusual ones (a
   query over a table not in the allow-list, or past a scanned-bytes estimate) for a person, as the undo window and review do for
   risky writes elsewhere.

## Alternative considered: the SQL API through an integration profile

Snowflake's SQL API (REST) runs statements asynchronously with handles, cancel and polling, without a driver, and fits the
OpenAPI-plus-profile approach the Host bets on. It loses the Sql module's guard, row caps and schema tool, which would have to be
rebuilt in the profile. Proposal: the Sql module first; the SQL API only if the driver cannot cancel reliably or long-running
queries need handles.

## What was built (S1, S2)

- `"Provider": "snowflake"` resolves `Snowflake.Data.Client.SnowflakeDbFactory, Snowflake.Data` (the deployment references the
  package). `AllowWrites` is refused. `ReadOnlyQueryTransaction` stays off: the role is the read-only boundary.
- `SqlDialect.Snowflake` in the guard: refuses `//` comments, `$` anywhere outside literals (dollar quotes, `$1` stage columns,
  session variables, `SYSTEM$` functions), backslashes inside literals; `[ ]` is element access, not quoting. `Unknown` applies
  these refusals too, and a `;` inside `[ ]` is refused there, since Snowflake would end the statement at it.
- `@name` parameters are rewritten to `?` outside quotes and comments and bound in order; a name without a value is refused before
  anything is sent.
- A query with no LIMIT, FETCH or TOP outside parentheses gets `LIMIT MaxRows + 1` on a new line (a trailing `--` comment cannot
  swallow it). The row cap with cancel is not needed: Snowflake has finished the query before the first row is read, so the
  LIMIT is what saves compute.
- `QueryTag = opentail:<action id>` on every schema and query command.
- Credentials: `private-key` (PEM text) and `private-key-password`; `authenticator=snowflake_jwt` is added when absent. A
  connection string holding `private_key`, `private_key_pwd`, `token` or `proxypassword` is refused like a password.
- Schema query: `information_schema.columns` of the connection's database without `INFORMATION_SCHEMA`, with quoted lower-case
  aliases (Snowflake upper-cases unquoted names).
- Tests: `SnowflakeTests` (Host tests, recording connection) and `SnowflakeAccountTests` (`OpenTail.Host.Tests.SqlServers`, the
  real driver; the account tests skip unless `OTAIL_SNOWFLAKE_ACCOUNT`, `_USER`, `_PRIVATE_KEY`, `_ROLE` and `_WAREHOUSE` are set).

## Testing on a real account

**The official route is Snowflake's 30-day trial**: free usage credit, no commitment, and the account is suspended at the end. Only
running warehouses use credit. Cloud, region and edition are chosen at sign-up and cannot be changed. Trial accounts normally
include the shared sample database `SNOWFLAKE_SAMPLE_DATA` (TPC-H), which the account tests read, so no data has to be loaded
(*to verify* on the account). Start the trial only when the tests are ready to run, since the 30 days start at sign-up.

Not official: **LocalStack for Snowflake**, a commercial emulator in one container. It lists Python, Go, JDBC, ODBC and Node.js
clients, not .NET, and needs a LocalStack key in CI. It is no substitute for the real service when the questions are cancel,
cost and roles.

Setup on the trial, by the account admin:

```sql
CREATE WAREHOUSE OT_AGENT_WH WAREHOUSE_SIZE = XSMALL AUTO_SUSPEND = 60 AUTO_RESUME = TRUE INITIALLY_SUSPENDED = TRUE
  STATEMENT_TIMEOUT_IN_SECONDS = 60 STATEMENT_QUEUED_TIMEOUT_IN_SECONDS = 60;
CREATE RESOURCE MONITOR OT_AGENT_RM WITH CREDIT_QUOTA = 5 TRIGGERS ON 100 PERCENT DO SUSPEND_IMMEDIATE;
ALTER WAREHOUSE OT_AGENT_WH SET RESOURCE_MONITOR = OT_AGENT_RM;
CREATE ROLE OT_AGENT_RO;
GRANT USAGE ON WAREHOUSE OT_AGENT_WH TO ROLE OT_AGENT_RO;
GRANT IMPORTED PRIVILEGES ON DATABASE SNOWFLAKE_SAMPLE_DATA TO ROLE OT_AGENT_RO;
CREATE USER OT_AGENT TYPE = SERVICE DEFAULT_ROLE = OT_AGENT_RO DEFAULT_WAREHOUSE = OT_AGENT_WH RSA_PUBLIC_KEY = '<public key body>';
GRANT ROLE OT_AGENT_RO TO USER OT_AGENT;
```

The key pair: `openssl genrsa 2048 | openssl pkcs8 -topk8 -inform PEM -out rsa_key.p8 -nocrypt`, then
`openssl rsa -in rsa_key.p8 -pubout -out rsa_key.pub` (Snowflake's key-pair guide). Then set the five `OTAIL_SNOWFLAKE_*`
variables and run `OpenTail.Host.Tests.SqlServers`. A CI job for it (manual only, the key as a secret) is a pipeline change for
the owner to approve. Until the account tests pass, the provider is labelled Tested, not Verified.

## Steps

- [x] **S1** Provider and dialect: driver factory (or adapter), `SqlDialect.Snowflake`, guard rules, schema query, key-pair
  authentication from the credential store.
- [x] **S2** Per-call guards: timeout with server-side cancel (the driver's), `LIMIT` when absent, `QUERY_TAG` = action id. The row
  cap with cancel was dropped as useless (see above).
- [ ] **S3** Start-up checks: warehouse size and timeouts read from Snowflake; refuse or warn by configuration.
- [ ] **S4** Daily budget per identity from query history, through adaptive limits, report only by default.
- [ ] **S5** Real-account suite (owner provides the account), then Verified.

Size: S1 and S2 about a session; S3 and S4 about a session; S5 depends on the account.

## Open questions (owner)

- ~~Is it needed yet?~~ Owner, 2026-10-23: build the capability; prove it on an official test account.
- **The budget's unit:** bytes scanned (cheap to read, rough), elapsed time, or credits (exact, but reported late).
- **Refuse or warn** when the warehouse is too large or has no timeout (proposed: refuse; a person can override in configuration).
- **Which account** for the real tests, and who pays for its credits.
