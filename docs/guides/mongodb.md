# Guide: querying MongoDB from the Host

The MongoDB module (`OpenTail.Modules.MongoDb`, `"Type": "mongodb"`) lets agents read allowed collections: governed, read-only,
one collection per query. The design and its reviews are in opentail-connectivity, `docs/design/redis-pinecone-mongodb.md`.

**State: Tested.** Unit tests for the guard and the output limits, and a suite against `mongo:8` in CI (`mongodb.yml`) with a
read-only user. Not yet run against Atlas (step M5, needs an account).

## What the agent gets

| Tool | What it does |
|---|---|
| `<id>.collections` | The allowed collections, with field names and types sampled from a few documents. |
| `<id>.find` | `filter`, `projection`, `sort` and `limit` on one allowed collection. |
| `<id>.aggregate` | A pipeline on one allowed collection. |
| `<id>.count` | The number of documents matching a filter. |

All four are **Read** risk and mark their results untrusted (they taint the session, like any outside content). Every operation
carries a **server-side time limit** (`maxTimeMS`, from `CommandTimeoutSeconds`) and the comment **`opentail:<action id>`**, which
shows in the profiler, `currentOp` and Atlas's query insights, so each operation can be traced to the Host's audit record.

## What the guard allows

Every key starting with `$` must be on an allow-list for the place it appears; everything else is refused, including operators
MongoDB adds later.

- **Stages:** `$match`, `$project`, `$addFields`, `$set`, `$unset`, `$group`, `$sort`, `$limit`, `$skip`, `$unwind`, `$count`,
  `$sortByCount`, `$replaceRoot`, `$replaceWith`, `$sample` (at most 200).
- **In filters:** `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`, `$and`, `$or`, `$nor`, `$not`, `$exists`, `$type`,
  `$all`, `$elemMatch`, `$size`, `$expr`.
- **In expressions:** arithmetic, comparison, boolean, conditional, a few string, date and type conversions, reading arrays
  (`$size`, `$arrayElemAt`, `$in`, `$first`, `$last`), `$literal`. The exact list is `MongoGuard.ExpressionOperators`.
- **Accumulators** (in `$group` only): `$sum`, `$avg`, `$min`, `$max`, `$count`, `$first`, `$last`.
- **Values:** plain JSON, and Extended JSON wrappers for types (`{"$date": "..."}`, `{"$oid": "..."}`, `$numberLong`,
  `$numberInt`, `$numberDouble`, `$numberDecimal`).

**Never:** server-side JavaScript (`$where`, `$function`, `$accumulator`), writes (`$out`, `$merge`), server internals
(`$currentOp`, `$collStats` and others). **Not in this version:** joins (`$lookup`, `$graphLookup`, `$unionWith`), `$facet`,
`$regex`, `$push` and `$addToSet`; see the design for why and what would bring them back.

**Bounds:** the request at most 16 KB; nesting 32 levels; 20 stages; 100 values per `$in`/`$nin`/`$all`; 20 clauses per
`$and`/`$or`/`$nor`; 200 operators in all. Results: at most `MaxRows` documents (200), strings cut at `MaxCellChars` (2,000),
nested documents and arrays summarised past depth 8 or 200 elements, and the whole result cut at 256 KB.

**Not bounded by the guard:** the work inside `$group` and an unindexed `$sort`. The server limits each stage to 100 MB without
disk use (the module sets `allowDiskUse: false`) and `maxTimeMS` stops the operation.

## 1. In MongoDB

Give the agent a user with the built-in **`read`** role on one database and nothing else. The server is the read-only boundary;
the module's guard is the second line.

```js
db.getSiblingDB("sales").createUser({ user: "ot_agent", pwd: "<password>", roles: [ { role: "read", db: "sales" } ] })
```

On Atlas: a database user with *Only read any database* scoped to the cluster, or a custom role with `find` on the collections the
agent may read; and the Host's address in the project's IP access list.

## 2. In the Host deployment

Reference `OpenTail.Modules.MongoDb`, then:

```json
{
  "Type": "mongodb",
  "Id": "sales",
  "Config": {
    "ConnectionString": "mongodb+srv://ot_agent@cluster0.example.mongodb.net/?readPreference=secondaryPreferred",
    "Database": "sales",
    "Collections": [ "orders", "customers" ],
    "MaxRows": 200,
    "CommandTimeoutSeconds": 15
  },
  "Credentials": {
    "password": { "Env": "SALES_MONGO_PASSWORD", "Kind": "Secret" }
  }
}
```

- The password is added from the `password` credential; a connection string with a password in it is refused. Or map the whole
  string as the `connection` credential.
- `Collections` is required; `system.*` collections are never allowed.
- `readPreference=secondaryPreferred` in the connection string sends agents' reads to secondaries.
- Database traffic does not go through the Host's HTTP guard.

A starting policy, running all four tools directly:

```json
{ "Tools": { "sales.collections": { "Mode": "Execute" }, "sales.find": { "Mode": "Execute" }, "sales.aggregate": { "Mode": "Execute" }, "sales.count": { "Mode": "Execute" } } }
```

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "'$x' is not allowed: ..." | The operator is outside the policy; the message says why. Rewrite the query without it. |
| "'x' is not one of the collections this module may read" | Add it to `Collections`, if the agent should read it. |
| "must not contain a password" at start-up | Move the password to the `password` credential. |
| `MaxTimeMSExpired` | The operation ran past `CommandTimeoutSeconds`; the server stopped it. Narrow the filter or add an index. |
| `Unauthorized` | The user lacks `read` on the database, or the connection string names another database. |
