# Guide: querying Snowflake from the Host

Agents can ask a Snowflake warehouse read-only questions through the Sql module. Every query costs warehouse time, so most of
this guide is about setting Snowflake up so that cost stays bounded whatever the agent does.

**State: Tested, not Verified.** The provider has not yet been run against a real account. See the [design](../design/snowflake.md)
for the open steps and [what was built](../done/snowflake-s1-s2.md).

## What the agent gets

| Tool | What it does |
|---|---|
| `<id>.schema` | Tables and columns of the connected database (`table_schema`, `table_name`, `column_name`, `data_type`); `table` narrows it to one table. |
| `<id>.query` | One `SELECT` or `WITH ... SELECT`, with `@name` parameters. At most `MaxRows` rows come back. |

There are no write tools. Results are marked untrusted, so they taint the session like any outside content.

What the Host adds to each query:

- `LIMIT MaxRows + 1` when the query has no LIMIT, FETCH or TOP of its own.
- A timeout (`CommandTimeoutSeconds`, default 15); the driver cancels the query on Snowflake when it passes.
- The query tag `opentail:<action id>`, so each query in Snowflake's history can be traced back to the Host's audit record.

What the guard refuses, beyond anything that is not a single SELECT: `//` comments, `$` outside literals (dollar quotes, `$1`,
session variables, `SYSTEM$` functions), backslashes in literals, stage references (`@stage` is read as a parameter with no value).

## 1. On the Snowflake side (the account admin)

The role is the real boundary; the Host's guard is a second line. Give the agent its own warehouse, role and service user:

```sql
-- A small warehouse of its own, with a timeout and a credit cap.
CREATE WAREHOUSE OT_AGENT_WH WAREHOUSE_SIZE = XSMALL AUTO_SUSPEND = 60 AUTO_RESUME = TRUE INITIALLY_SUSPENDED = TRUE
  STATEMENT_TIMEOUT_IN_SECONDS = 60 STATEMENT_QUEUED_TIMEOUT_IN_SECONDS = 60;
CREATE RESOURCE MONITOR OT_AGENT_RM WITH CREDIT_QUOTA = 5 FREQUENCY = DAILY START_TIMESTAMP = IMMEDIATELY
  TRIGGERS ON 80 PERCENT DO NOTIFY ON 100 PERCENT DO SUSPEND_IMMEDIATE;
ALTER WAREHOUSE OT_AGENT_WH SET RESOURCE_MONITOR = OT_AGENT_RM;

-- A read-only role: USAGE and SELECT, nothing else.
CREATE ROLE OT_AGENT_RO;
GRANT USAGE ON WAREHOUSE OT_AGENT_WH TO ROLE OT_AGENT_RO;
GRANT USAGE ON DATABASE SALES TO ROLE OT_AGENT_RO;
GRANT USAGE ON ALL SCHEMAS IN DATABASE SALES TO ROLE OT_AGENT_RO;
GRANT SELECT ON ALL TABLES IN DATABASE SALES TO ROLE OT_AGENT_RO;
GRANT SELECT ON FUTURE TABLES IN DATABASE SALES TO ROLE OT_AGENT_RO;
GRANT SELECT ON ALL VIEWS IN DATABASE SALES TO ROLE OT_AGENT_RO;

-- A service user that signs in with a key pair (no password).
CREATE USER OT_AGENT TYPE = SERVICE DEFAULT_ROLE = OT_AGENT_RO DEFAULT_WAREHOUSE = OT_AGENT_WH
  RSA_PUBLIC_KEY = '<public key, without the BEGIN and END lines>';
GRANT ROLE OT_AGENT_RO TO USER OT_AGENT;
```

Replace `SALES` with the database the agent may read, and narrow the grants to schemas or views if it should see less. Row access
and masking policies on the role apply to the agent as they would to a person.

Make the key pair (Snowflake's key-pair guide; 2048-bit RSA at least):

```sh
openssl genrsa 2048 | openssl pkcs8 -topk8 -inform PEM -out rsa_key.p8 -nocrypt
openssl rsa -in rsa_key.p8 -pubout -out rsa_key.pub
```

`rsa_key.pub` goes into `RSA_PUBLIC_KEY`; `rsa_key.p8` goes into the Host's credential store, never into a config file.

## 2. In the Host deployment

Reference the driver package next to the Host: `Snowflake.Data` (tested with 6.3.0). Then add the module to `modules.json`:

```json
{
  "Type": "sql",
  "Id": "warehouse",
  "Config": {
    "Provider": "snowflake",
    "ConnectionString": "account=myorg-myaccount;user=OT_AGENT;role=OT_AGENT_RO;warehouse=OT_AGENT_WH;db=SALES;schema=PUBLIC",
    "MaxRows": 200,
    "CommandTimeoutSeconds": 30,
    "Schemas": [ "PUBLIC" ]
  },
  "Credentials": {
    "private-key": { "Env": "SNOWFLAKE_AGENT_KEY", "Kind": "Secret" }
  }
}
```

- `account` is the account identifier (`<org>-<account>`), as shown in Snowsight's account details.
- Add `private-key-password` if the key is encrypted.
- The connection string must not hold a password, key, token or proxy password; the module refuses to start if it does.
- `AllowWrites` is refused for Snowflake.
- `AddRowLimit: false` turns the added LIMIT off. Leave it on unless every query the agent can run already limits itself.

A starting policy: run `schema` and `query` directly, and keep the warehouse small instead of queueing every question:

```json
{ "Tools": { "warehouse.schema": { "Mode": "Execute" }, "warehouse.query": { "Mode": "Execute" } } }
```

## 3. Where the cost limits are

| Layer | Set by | What it stops |
|---|---|---|
| Resource monitor | Snowflake admin | Total spend: suspends the warehouse at the credit quota. The hard ceiling. |
| Warehouse size and statement timeouts | Snowflake admin | Expensive single queries; queries waiting in a queue. |
| `CommandTimeoutSeconds` | Host config | Any query running longer; cancelled on the server. |
| Added LIMIT | Host, automatic | Producing rows nobody reads. |
| Query tag | Host, automatic | Nothing by itself; it makes every query attributable. |

Not built yet: the Host checking the warehouse settings at start-up (S3), and a daily budget per identity (S4).

To see what the agent ran and what it cost:

```sql
SELECT query_tag, query_text, execution_status, total_elapsed_time, bytes_scanned
FROM SNOWFLAKE.ACCOUNT_USAGE.QUERY_HISTORY
WHERE query_tag LIKE 'opentail:%' ORDER BY start_time DESC LIMIT 100;
```

`ACCOUNT_USAGE` lags by up to about 45 minutes; `INFORMATION_SCHEMA.QUERY_HISTORY_BY_USER` is current but covers a shorter window.

## 4. Trying it on a trial account

Snowflake's 30-day trial is the official way to try this without a contract: free credit, and only a running warehouse uses it.
The 30 days start at sign-up, so sign up when you are ready to run the tests. Trial accounts normally include the shared sample
database `SNOWFLAKE_SAMPLE_DATA`; grant the role `IMPORTED PRIVILEGES` on it instead of the `SALES` grants above:

```sql
GRANT IMPORTED PRIVILEGES ON DATABASE SNOWFLAKE_SAMPLE_DATA TO ROLE OT_AGENT_RO;
```

Then set `OTAIL_SNOWFLAKE_ACCOUNT`, `OTAIL_SNOWFLAKE_USER`, `OTAIL_SNOWFLAKE_PRIVATE_KEY` (the PEM text of `rsa_key.p8`),
`OTAIL_SNOWFLAKE_ROLE` and `OTAIL_SNOWFLAKE_WAREHOUSE`, and run:

```sh
dotnet run --project OpenTail.Host.Tests.SqlServers
```

The account tests sign in with the key, run a parameterised query against `TPCH_SF1.CUSTOMER` with the row cap, read the schema,
and check that a 20-second query is cancelled after a 2-second timeout. They use a few seconds of an X-Small warehouse.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Provider type 'Snowflake.Data.Client.SnowflakeDbFactory, Snowflake.Data' was not found" | The deployment does not reference the `Snowflake.Data` package. |
| "must not contain a password" at start-up | The connection string holds `password`, `private_key`, `private_key_pwd`, `token` or `proxypassword`; move it to a credential. |
| "@x has no value in parameters" | The query uses `@x` (or a stage, `@stage`) with no matching parameter. |
| "'$' is not allowed" | Dollar quotes, `$1`, session variables or a `SYSTEM$` function; rewrite with `@name` parameters. |
| Sign-in fails with a JWT error | The public key on the user does not match the private key, or the account identifier is wrong. |
| Queries fail after a while with a warehouse error | The resource monitor suspended the warehouse at its quota. |
