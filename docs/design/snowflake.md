# Design: Snowflake as a read-only Sql target

**Status (2026-10-23):** S1 and S2 are built ([done](../done/snowflake-s1-s2.md)); how to set it up is in the
[guide](../guides/snowflake.md). This page holds what is still open: S3 to S5 and the owner's questions.

**Why.** Agents asking a warehouse questions ("Q3 sales by region") is useful, and the Sql module already does governed, read-only
querying. Snowflake adds one risk: **every query costs money**, so a curious or looping agent can run up a bill without touching a
single forbidden row. Owner, 2026-10-23: no project needs a warehouse yet, and when one does it may be the customer's, not ours;
build the capability anyway and prove it on an official test account.

**Not in scope:** writes of any kind, and loading data into a warehouse (that is ETL; if a project wants a landing zone, a Parquet
exporter to object storage is the cheap route, in Connectivity's optional to-do).

## Steps

- [x] **S1** Provider and dialect. *Built 2026-10-23.*
- [x] **S2** Per-call guards: timeout with server-side cancel, LIMIT when absent, query tag. *Built 2026-10-23.*
- [ ] **S3** Start-up checks on the warehouse.
- [ ] **S4** Daily budget per identity.
- [ ] **S5** Real-account suite, then Verified. *Waits on the owner signing up for a trial.*

Size: S3 and S4 together about a session; S5 about an hour once the account exists.

## S3: start-up checks on the warehouse

The resource monitor and timeouts are set by the customer's admin, and the Host cannot rely on them being there. At start-up (and
every few hours after) the module reads the configured warehouse and checks:

- **Size** at most `MaxWarehouseSize` (default `XSMALL`): `SHOW WAREHOUSES LIKE '<name>'`.
- **`STATEMENT_TIMEOUT_IN_SECONDS`** set and at most `MaxStatementTimeoutSeconds` (default 300):
  `SHOW PARAMETERS LIKE 'STATEMENT_TIMEOUT_IN_SECONDS' IN WAREHOUSE <name>`.
- **A resource monitor** attached (the `resource_monitor` column of `SHOW WAREHOUSES`).

On a failed check the module refuses to start, or starts with a warning when `WarehouseChecks: "Warn"`. Proposed default: refuse.
These are module-internal statements and do not pass through the guard. *To verify on the account:* that a role with only
`USAGE` on the warehouse can run both `SHOW` commands and sees the monitor column.

## S4: daily budget per identity

Read what each identity's queries cost from query history, by the `opentail:` tag, and feed it to the adaptive-limits machinery:
report only by default, then queue or deny past the budget.

- **Unit (owner):** bytes scanned (available per query, rough), elapsed time (available per query, closer to cost on a fixed-size
  warehouse), or credits (exact, but only per warehouse and hour, not per query).
- **Source:** `INFORMATION_SCHEMA.QUERY_HISTORY_BY_USER` (current, short window) rather than `ACCOUNT_USAGE.QUERY_HISTORY`
  (up to about 45 minutes late).
- **Mapping:** the tag carries the action id; the Host's audit gives the identity behind each action.

## S5: real-account suite

`SnowflakeAccountTests` exists and skips until `OTAIL_SNOWFLAKE_*` is set; account setup is in the
[guide](../guides/snowflake.md#4-trying-it-on-a-trial-account). To verify on the account:

- Key-pair sign-in with the PEM text from the credential store.
- Trial accounts include `SNOWFLAKE_SAMPLE_DATA`.
- A timeout cancels the query on the server: its history shows it stopped, not that it ran to the end. The test reads history
  through `SNOWFLAKE_SAMPLE_DATA.INFORMATION_SCHEMA.QUERY_HISTORY_BY_USER`, which may need a database of the account's own.
- The schema query's quoted aliases come back lower case.
- Snowflake's dates for retiring single-factor password sign-in, which decide whether `password` should be refused for Snowflake.

Then the provider is labelled Verified. A CI job (manual only, the key as a repository secret) is a pipeline change for the owner
to approve.

## Layers of cost control (all steps)

| Layer | Set by | Step |
|---|---|---|
| Dedicated X-Small warehouse, auto-suspend, statement timeouts, resource monitor | Snowflake admin | Guide; checked by S3 |
| Module timeout, cancelled on the server | Host | S2, built |
| LIMIT when absent | Host | S2, built |
| Query tag = action id | Host | S2, built |
| Daily budget per identity | Host | S4 |
| Policy: queue unusual queries for a person | Host policy | Possible today with rules; no Snowflake-specific default yet |

Snowflake's result cache answers an identical query within 24 hours without compute (*to verify*), so repeated agent questions
cost little.

## Alternative considered: the SQL API through an integration profile

Snowflake's SQL API (REST) runs statements asynchronously with handles, cancel and polling, without a driver, and fits the
OpenAPI-plus-profile approach. It loses the Sql module's guard, row caps and schema tool, which would have to be rebuilt in the
profile. Decision: the Sql module first; the SQL API only if the driver cannot cancel reliably or long-running queries need
handles.

## Open questions (owner)

- **The budget's unit** (S4): bytes scanned, elapsed time, or credits.
- **Refuse or warn** when the warehouse fails a check (S3). Proposed: refuse; a person can switch to warn in configuration.
- **The trial account** for S5: who signs up, and when (the 30 days start at sign-up).
- ~~A separate package?~~ Owner, 2026-10-23: no split. S3 and S4 go into `OpenTail.Modules.Sql`; how a split could be done
  later is below.

## Splitting Snowflake out later (not planned)

Snowflake stays a provider inside `OpenTail.Modules.Sql`. This records how to split it out if it outgrows the module, so S3 and S4
can be written to make that easy.

**What would move, and what would stay.**

| Stays in `OpenTail.Modules.Sql` | Would move to `OpenTail.Modules.Sql.Snowflake` |
|---|---|
| `SqlDialect.Snowflake` and its guard rules: the guard is one parser, and its safety depends on knowing every dialect | Warehouse checks at start-up (S3): `SHOW WAREHOUSES`, `SHOW PARAMETERS`, the resource monitor |
| `AddRowLimit`, `PositionalParameters`, `TagQueries`: generic, and other pay-per-use warehouses (BigQuery, Redshift, Databricks) would use them | Reading per-query cost from query history (S4); the budget itself stays in the Host's adaptive limits |
| `"Provider": "snowflake"` and the `private-key` credentials, so existing configs keep working | A reference to `Snowflake.Data`, so a deployment adds one package instead of the module plus the driver |
| The schema query | Anything later that only Snowflake has: asynchronous query handles, OAuth or workload-identity sign-in, Cortex |

**The seams the module would need** (about 30 lines each, no Snowflake code in them):

- `ISqlTargetCheck`: run once at start-up and every few hours, given a connection; returns problems, which the module turns into a
  refusal or a warning (`WarehouseChecks`).
- `ISqlCostSource`: given action ids (the query tags), returns what each query cost in the configured unit, for S4.
- The factory would find both by provider name, the way it finds the driver: a type named in configuration, or an assembly
  attribute in the add-on package.

**How to write S3 and S4 now so a split stays cheap:** put the Snowflake-specific code in its own files (for example
`SnowflakeWarehouseCheck.cs`, `SnowflakeQueryCost.cs`) behind those two interfaces, even while they live in the Sql module. A
split is then a project move and a factory registration, with no change to behaviour or configuration.

**When to split:** when the Snowflake-only code passes a few hundred lines, when a second warehouse arrives and would use the same
seams, or when a deployment wants Snowflake without carrying the Sql module's other providers' notes and options. Not before:
each package adds publishing and versioning work.
