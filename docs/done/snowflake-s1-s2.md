# Done: Snowflake provider, steps S1 and S2

Built 2026-10-23 in host commit `4625a04`. Open work (S3 to S5) is in the [design](../design/snowflake.md); how to set it up is in
the [guide](../guides/snowflake.md).

**State: Tested, not Verified.** Checked against a recording connection and the real driver's factory, never against a Snowflake
account (that is S5).

## Where the code is

There is no separate Snowflake project. Snowflake is a provider of the Sql module, like SQL Server, Postgres and MySQL:

| What | Where |
|---|---|
| Provider name, key-pair credentials, writes refused, secret checks | `OpenTail.Modules.Sql/SqlModuleFactory.cs` |
| `AddRowLimit`, `PositionalParameters`, `TagQueries`, `SnowflakeSchemaQuery` | `OpenTail.Modules.Sql/SqlModule.cs` |
| `SqlDialect.Snowflake`, `ToPositional`, `HasOuterLimit` | `OpenTail.Modules.Sql/SqlGuard.cs` |
| Tests with a recording connection (no driver) | `OpenTail.Host.Tests/SnowflakeTests.cs` |
| Tests with the real driver, a test project of their own (the real-server workflow fails on skipped tests); account tests skip without `OTAIL_SNOWFLAKE_*` | `OpenTail.Host.Tests.Snowflake/SnowflakeAccountTests.cs` |

The module does not reference the driver. A deployment that uses Snowflake references `Snowflake.Data`; the test project pins
6.3.0 (`Directory.Packages.props`).

## S1: provider and dialect

- `"Provider": "snowflake"` resolves `Snowflake.Data.Client.SnowflakeDbFactory, Snowflake.Data`.
- **Read-only.** `AllowWrites` is refused at start-up. `ReadOnlyQueryTransaction` stays off, because Snowflake has no read-only
  transaction; the read-only role is the boundary and the guard is the second line.
- **Guard rules** (`SqlDialect.Snowflake`), each closing a way to make the guard and Snowflake read a statement differently:
  - `//` comments are refused (use `-- `).
  - `$` outside literals is refused: dollar-quoted strings, `$1` stage columns, session variables, `SYSTEM$` functions.
  - Backslashes inside literals are refused (Snowflake treats them as escapes).
  - `[ ]` is element access (`v['name']`), not quoting.
  - `SqlDialect.Unknown`, used for custom provider factories, now applies these refusals too, and refuses `;` inside `[ ]`.
- **Key-pair sign-in.** The `private-key` credential (PEM text) and optional `private-key-password` are set through the
  connection-string builder; `authenticator=snowflake_jwt` is added when absent. A connection string that holds `private_key`,
  `private_key_pwd`, `token` or `proxypassword` is refused like one holding a password.
- **Schema tool.** `information_schema.columns` of the connection's database, without `INFORMATION_SCHEMA`, with quoted lower-case
  aliases (Snowflake upper-cases unquoted names, and the other providers return lower case).

## S2: per-call guards

- **Timeout.** `CommandTimeoutSeconds` (default 15) is passed to the driver, which cancels the query on the server.
- **LIMIT.** A query with no LIMIT, FETCH or TOP outside parentheses gets `LIMIT MaxRows + 1`, on a new line so a trailing `--`
  comment cannot swallow it. The extra row tells the module the result was truncated.
- **Query tag.** Each schema and query command carries `opentail:<action id>`, so Snowflake's query history ties every query
  to a Host action.
- **Parameters.** `@name` is rewritten to `?` outside quotes and comments and bound in order (a name used twice is bound twice). A
  name with no value is refused before anything is sent.

## Decisions made while building

- **Row cap with cancel: dropped.** Snowflake finishes a query before the first row is fetched, so cancelling after enough rows
  saves nothing. The added LIMIT is what saves compute.
- **No session statement for the timeout.** The driver's own cancel is enough; an `ALTER SESSION` per call would cost a round trip.
- **Key not doubled.** Advice found online says to replace `=` with `==` in a private key. That applies to hand-written
  connection strings only: the driver parses with `DbConnectionStringBuilder`, so a key set through the builder arrives intact.

## Facts checked in the driver's source

`snowflakedb/snowflake-connector-net`, read 2026-10-23 (latest release then: 6.4.0, 2026-10-16):

- `SnowflakeDbFactory : DbProviderFactory`, with `public static readonly SnowflakeDbFactory Instance`.
- A command timeout or a cancelled token calls `Cancel()`, which posts to `/queries/v1/abort-request` (`SFStatement`).
- `SnowflakeDbCommand.QueryTag` tags one statement; `QUERY_TAG` in the connection string tags the whole connection.
- Parameters bind by position: `?`, with parameter names `"1"`, `"2"`, and so on.
- One statement per request, unless `MULTI_STATEMENT_COUNT` says otherwise.
- Key-pair sign-in: `authenticator=snowflake_jwt` with `private_key` or `private_key_file`, plus `private_key_pwd`.
- The connection string is parsed with `DbConnectionStringBuilder` (`SFSessionProperties.ParseConnectionString`).

## Tests

- `SnowflakeTests` (Host tests): the guard's refusals and acceptances, positional rewriting, outer-limit detection, and a query
  and a schema call through the Host against a recording connection (LIMIT, binds, query tag).
- `SnowflakeAccountTests`: the factory resolves the real driver and refuses secrets in the connection string (runs without an
  account); key-pair sign-in, a parameterised query with the row cap, the schema tool, and a timeout that should be cancelled on
  the server (skip until `OTAIL_SNOWFLAKE_*` is set).
- Host suite 950 passed after the change.
