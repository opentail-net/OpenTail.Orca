# Design: Snowflake as a read-only Sql target

**Status:** proposal, 2026-10-23. Nothing is built. Snowflake facts below are from knowledge and marked *to verify*; check them
against Snowflake's documentation before building.

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

## Testing

There is no official local Snowflake emulator (*to verify*). Unit tests use a fake `DbProviderFactory`, as the other providers'
tests do. Real tests need a Snowflake account (a trial is enough), a key-pair service user and a CI secret, run by hand or on
changes to the module, sparingly, since they cost credits. Until then the provider is labelled Tested, not Verified.

## Steps

- [ ] **S1** Provider and dialect: driver factory (or adapter), `SqlDialect.Snowflake`, guard rules, schema query, key-pair
  authentication from the credential store.
- [ ] **S2** Per-call guards: timeout with server-side cancel, row cap with cancel, `LIMIT` when absent, `QUERY_TAG` = action id.
- [ ] **S3** Start-up checks: warehouse size and timeouts read from Snowflake; refuse or warn by configuration.
- [ ] **S4** Daily budget per identity from query history, through adaptive limits, report only by default.
- [ ] **S5** Real-account suite (owner provides the account), then Verified.

Size: S1 and S2 about a session; S3 and S4 about a session; S5 depends on the account.

## Open questions (owner)

- **Is it needed yet?** The owner asked for the plan; building waits on a project that needs a warehouse.
- **The budget's unit:** bytes scanned (cheap to read, rough), elapsed time, or credits (exact, but reported late).
- **Refuse or warn** when the warehouse is too large or has no timeout (proposed: refuse; a person can override in configuration).
- **Which account** for the real tests, and who pays for its credits.
