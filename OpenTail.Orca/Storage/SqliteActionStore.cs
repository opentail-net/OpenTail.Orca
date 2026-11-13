using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace OpenTail.Orca;

/// <summary>
/// Durable store (one SQLite file per deployment, WAL). Every state change is a compare-and-set inside a
/// <c>BEGIN IMMEDIATE</c> transaction, so concurrent reviewers or executors, even in separate processes,
/// cannot apply the same transition twice. Limits (admissions) are counted in the same transaction that inserts, so
/// they hold across processes too. The full record is kept as JSON; the columns exist for indexing.
/// </summary>
public sealed class SqliteActionStore : IActionStore, IUsageStore, IAtomicAuditStore
{
    /// <summary>Schema 2: requester-scoped idempotency, sessions, subject taint, trust promotions. Schema 3: notices. Schema 4: daily
    /// usage rollups, entity hashes and the settings table (the entity salt). Schema 5: the audit hash chain. Schema 6: identity levels. Schema 7: session labels. Schema 8: access grants. Schema 9: policy versions. Schema 10: work items, continuations, thread notes. Schema 11: decision records. Schema 12: policy proposals. Schema 13: A2A task ids (a2a_tasks; OpenTail.Orca.A2A owns the rows).</summary>
    private const int SchemaVersion = 14;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _connectionString;
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly string _synchronousPragma;

    /// <param name="synchronous">How hard each commit is pushed to disk; null reads <see cref="SqliteDurability.Variable"/>.</param>
    public SqliteActionStore(string path, TimeProvider? clock = null, SqliteSynchronous? synchronous = null)
    {
        _clock = clock ?? TimeProvider.System;
        Synchronous = synchronous ?? SqliteDurability.FromEnvironment();
        _synchronousPragma = SqliteDurability.Pragma(Synchronous);
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        Initialise();
    }

    /// <summary>The durability level every connection of this store uses.</summary>
    public SqliteSynchronous Synchronous { get; }

    /// <summary>The <c>PRAGMA synchronous</c> value on a connection the store opens (0 off, 1 normal, 2 full, 3 extra). For tests.</summary>
    internal long ReadSynchronous()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous;";
        return (long)(cmd.ExecuteScalar() ?? -1L);
    }

    /// <summary>Path of the backup taken before the last schema migration, if one was needed.</summary>
    public string? MigrationBackupPath { get; private set; }

    /// <summary>The database file. Packages that keep their own tables in it (the A2A task table) open their own connection to this path.</summary>
    public string DatabasePath => _path;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000; " + _synchronousPragma;
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Initialise()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        long existing;
        using (var v = c.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            existing = (long)(v.ExecuteScalar() ?? 0L);
        }

        if (existing > SchemaVersion)
            throw new InvalidOperationException(
                $"Database schema {existing} is newer than this Host understands ({SchemaVersion}). Upgrade the Host.");

        // The migration is one-way (an older Host refuses the new schema), so keep a copy of what was there.
        if (existing is >= 1 and < SchemaVersion)
        {
            var backup = $"{Path.GetFullPath(_path)}.bak-v{existing}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            using var b = c.CreateCommand();
            b.CommandText = "VACUUM INTO $path";
            b.Parameters.AddWithValue("$path", backup);
            b.ExecuteNonQuery();
            MigrationBackupPath = backup;
        }

        using var tx = c.BeginTransaction(deferred: false);
        long version;
        using (var v = c.CreateCommand())
        {
            v.Transaction = tx;
            v.CommandText = "PRAGMA user_version;";
            version = (long)(v.ExecuteScalar() ?? 0L);
        }

        // Forward-only migrations: each step runs once, in order.
        if (version < 1)
        {
            Exec(c, tx, """
                CREATE TABLE actions (
                    id           TEXT PRIMARY KEY,
                    tool         TEXT NOT NULL,
                    module       TEXT NOT NULL,
                    status       TEXT NOT NULL,
                    requester_id TEXT NOT NULL,
                    session_id   TEXT NULL,
                    idem_key     TEXT NULL,
                    args_hash    TEXT NOT NULL,
                    created_at   TEXT NOT NULL,
                    updated_at   TEXT NOT NULL,
                    json         TEXT NOT NULL
                );
                CREATE UNIQUE INDEX ux_actions_idem ON actions(tool, idem_key, args_hash) WHERE idem_key IS NOT NULL;
                CREATE INDEX ix_actions_status ON actions(status, created_at);
                CREATE INDEX ix_actions_requester ON actions(requester_id, created_at);
                CREATE TABLE audit (
                    seq       INTEGER PRIMARY KEY AUTOINCREMENT,
                    at        TEXT NOT NULL,
                    event     TEXT NOT NULL,
                    action_id TEXT NULL,
                    tool      TEXT NULL,
                    actor     TEXT NULL,
                    detail    TEXT NULL
                );
                CREATE INDEX ix_audit_action ON audit(action_id);
                """);
        }

        if (version < 2)
        {
            Exec(c, tx, """
                DROP INDEX IF EXISTS ux_actions_idem;
                CREATE UNIQUE INDEX ux_actions_idem2 ON actions(requester_id, tool, idem_key, args_hash) WHERE idem_key IS NOT NULL;
                CREATE TABLE sessions (
                    id           TEXT PRIMARY KEY,
                    owner        TEXT NOT NULL,
                    tainted      INTEGER NOT NULL,
                    created_at   TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL
                );
                CREATE INDEX ix_sessions_seen ON sessions(last_seen_at);
                CREATE TABLE subject_taint (
                    subject TEXT PRIMARY KEY,
                    until   TEXT NOT NULL
                );
                CREATE TABLE trust_promotions (
                    tool TEXT PRIMARY KEY,
                    by   TEXT NOT NULL,
                    at   TEXT NOT NULL
                );
                """);
        }

        if (version < 3)
        {
            Exec(c, tx, """
                CREATE TABLE notices (
                    action_id    TEXT PRIMARY KEY,
                    requester    TEXT NOT NULL,
                    session_id   TEXT NULL,
                    created_at   TEXT NOT NULL,
                    delivered_at TEXT NULL
                );
                CREATE INDEX ix_notices_req ON notices(requester, delivered_at);
                """);
        }

        if (version < 4)
        {
            Exec(c, tx, """
                CREATE TABLE usage_daily (
                    day        TEXT NOT NULL,
                    subject    TEXT NOT NULL,
                    tool       TEXT NOT NULL,
                    module     TEXT NOT NULL,
                    profile    TEXT NULL,
                    calls      INTEGER NOT NULL DEFAULT 0,
                    succeeded  INTEGER NOT NULL DEFAULT 0,
                    failed     INTEGER NOT NULL DEFAULT 0,
                    denied     INTEGER NOT NULL DEFAULT 0,
                    rejected   INTEGER NOT NULL DEFAULT 0,
                    other      INTEGER NOT NULL DEFAULT 0,
                    records    INTEGER NOT NULL DEFAULT 0,
                    recipients INTEGER NOT NULL DEFAULT 0,
                    bytes      INTEGER NOT NULL DEFAULT 0,
                    entities   INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (day, subject, tool)
                );
                CREATE TABLE usage_entities (
                    day     TEXT NOT NULL,
                    subject TEXT NOT NULL,
                    tool    TEXT NOT NULL,
                    hash    TEXT NOT NULL,
                    PRIMARY KEY (day, subject, tool, hash)
                );
                CREATE TABLE settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """);
        }

        if (version < 5)
        {
            // Entries written before schema 5 have no hash: they are the chain's unverified prefix.
            Exec(c, tx, """
                ALTER TABLE audit ADD COLUMN prev_hash TEXT NULL;
                ALTER TABLE audit ADD COLUMN hash TEXT NULL;
                """);
        }

        if (version < 6)
        {
            Exec(c, tx, """
                CREATE TABLE identity_levels (
                    subject  TEXT PRIMARY KEY,
                    level    TEXT NOT NULL,
                    reason   TEXT NOT NULL,
                    since    TEXT NOT NULL,
                    enforced INTEGER NOT NULL
                );
                """);
        }

        if (version < 7)
        {
            Exec(c, tx, """
                CREATE TABLE session_labels (
                    scope  TEXT NOT NULL,
                    label  TEXT NOT NULL,
                    source TEXT NOT NULL,
                    until  TEXT NOT NULL,
                    PRIMARY KEY (scope, label, source)
                );
                """);
        }

        if (version < 8)
        {
            Exec(c, tx, """
                CREATE TABLE access_grants (
                    id         TEXT PRIMARY KEY,
                    subject    TEXT NOT NULL,
                    tool       TEXT NOT NULL,
                    until      TEXT NOT NULL,
                    calls_left INTEGER NOT NULL,
                    granted_by TEXT NOT NULL,
                    reason     TEXT NOT NULL,
                    at         TEXT NOT NULL
                );
                CREATE INDEX ix_access_grants_subject ON access_grants(subject);
                """);
        }

        if (version < 9)
        {
            Exec(c, tx, """
                CREATE TABLE policy_versions (
                    hash         TEXT PRIMARY KEY,
                    json         TEXT NOT NULL,
                    first_loaded TEXT NOT NULL,
                    last_loaded  TEXT NOT NULL
                );
                """);
        }

        if (version < 10)
        {
            Exec(c, tx, """
                CREATE TABLE work_items (
                    id     TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    thread TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    json   TEXT NOT NULL
                );
                CREATE INDEX ix_work_items_status ON work_items(status);
                CREATE TABLE continuations (
                    id     TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    json   TEXT NOT NULL
                );
                CREATE INDEX ix_continuations_status ON continuations(status);
                CREATE TABLE thread_notes (
                    thread TEXT PRIMARY KEY,
                    json   TEXT NOT NULL
                );
                """);
        }

        if (version < 11)
        {
            Exec(c, tx, """
                CREATE TABLE decision_records (
                    action_id  TEXT PRIMARY KEY,
                    created_at TEXT NOT NULL,
                    batch      TEXT NOT NULL,
                    hash       TEXT NOT NULL,
                    json       TEXT NOT NULL
                );
                CREATE INDEX ix_decision_records_created ON decision_records(created_at);
                """);
        }

        if (version < 12)
        {
            Exec(c, tx, """
                CREATE TABLE policy_proposals (
                    id         TEXT PRIMARY KEY,
                    created_at TEXT NOT NULL,
                    status     TEXT NOT NULL,
                    json       TEXT NOT NULL
                );
                """);
        }

        if (version < 13)
        {
            // The A2A task id table: one row per task a partner was given. No message text is stored here (see OpenTail.Orca.A2A).
            Exec(c, tx, """
                CREATE TABLE a2a_tasks (
                    task_id          TEXT PRIMARY KEY,
                    partner_id       TEXT NOT NULL,
                    context_id       TEXT NOT NULL,
                    message_id       TEXT NOT NULL,
                    content_hash     TEXT NOT NULL,
                    skill_id         TEXT NOT NULL,
                    submit_action_id TEXT NULL,
                    work_item_id     TEXT NULL,
                    reply_action_id  TEXT NULL,
                    created_at       TEXT NOT NULL,
                    updated_at       TEXT NOT NULL,
                    UNIQUE (partner_id, message_id)
                );
                CREATE INDEX ix_a2a_tasks_context ON a2a_tasks(partner_id, context_id);
                """);
        }

        if (version < 14)
        {
            // The audit query (an admin's page of one reviewer's decisions, and counts of them) filters by event and actor and pages by seq.
            Exec(c, tx, "CREATE INDEX IF NOT EXISTS ix_audit_event_actor ON audit(event, actor, seq);");
        }

        Exec(c, tx, $"PRAGMA user_version={SchemaVersion};");
        tx.Commit();
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public Task<ActionRecord> CreateAsync(ActionRecord record, CancellationToken ct = default)
    {
        if (!ActionStateMachine.IsValidInitial(record.Status))
            throw new InvalidOperationException($"{record.Status} is not a valid initial status.");
        return Task.FromResult(Create(record, [], static _ => null)!);
    }

    public Task<ActionRecord?> TryCreateAsync(ActionRecord record, IReadOnlyList<Admission> admissions, CancellationToken ct = default) =>
        TryCreateAsync(record, admissions, static _ => null, ct);

    public Task<ActionRecord?> TryCreateAsync(ActionRecord record, IReadOnlyList<Admission> admissions, Func<int, ActionRecord?> refused,
        CancellationToken ct = default)
    {
        if (!ActionStateMachine.IsValidInitial(record.Status))
            throw new InvalidOperationException($"{record.Status} is not a valid initial status.");
        return Task.FromResult(Create(record, admissions, refused));
    }

    private ActionRecord? Create(ActionRecord record, IReadOnlyList<Admission> admissions, Func<int, ActionRecord?> refused,
        Func<ActionRecord, AuditEntry>? audit = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);

        if (record.IdempotencyKey is { } key)
        {
            using var find = c.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT json FROM actions WHERE requester_id=$r AND tool=$t AND idem_key=$k AND args_hash=$h";
            find.Parameters.AddWithValue("$r", record.Requester.Id);
            find.Parameters.AddWithValue("$t", record.ToolName);
            find.Parameters.AddWithValue("$k", key);
            find.Parameters.AddWithValue("$h", record.ArgumentsHash);
            if (find.ExecuteScalar() is string existing)
                return Deserialize(existing);
        }

        // A refused admission stores what the caller says to store (for example the action, failed), or nothing: either way in this
        // transaction, so the count and the insert cannot be split by another caller.
        var refusedBy = FirstRefusal(c, tx, admissions);
        var toStore = refusedBy < 0 ? record : refused(refusedBy);
        if (toStore is null) return null; // rolls back on dispose

        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO actions(id, tool, module, status, requester_id, session_id, idem_key, args_hash, created_at, updated_at, json)
            VALUES($id,$tool,$module,$status,$req,$ses,$idem,$hash,$c,$u,$json)
            """;
        Bind(ins, toStore);
        ins.ExecuteNonQuery();
        if (Usage.IsEnd(toStore.Status)) AddUsage(c, tx, toStore);
        if (audit is not null) SqliteAuditSink.Append(c, tx, audit(toStore));
        tx.Commit();
        return toStore;
    }

    public Task<ActionRecord?> TryCreateAdmittedAuditedAsync(ActionRecord record, IReadOnlyList<Admission> admissions,
        Func<int, ActionRecord?> refused, Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default)
    {
        if (!ActionStateMachine.IsValidInitial(record.Status))
            throw new InvalidOperationException($"{record.Status} is not a valid initial status.");
        return Task.FromResult(Create(record, admissions, refused, audit));
    }

    /// <summary>The index of the first admission already at its limit, or -1. Runs inside the caller's transaction.</summary>
    private int FirstRefusal(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<Admission> admissions)
    {
        for (var i = 0; i < admissions.Count; i++)
        {
            using var count = c.CreateCommand();
            count.Transaction = tx;
            var where = new List<string>();
            AddFilter(count, where, admissions[i].Filter, "a" + i);
            if (admissions[i].Window is { } w)
            {
                where.Add($"updated_at>=$a{i}w");
                count.Parameters.AddWithValue($"$a{i}w", (_clock.GetUtcNow() - w).ToUniversalTime().ToString("O"));
            }
            count.CommandText = "SELECT COUNT(*) FROM actions" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "");
            if ((long)(count.ExecuteScalar() ?? 0L) >= admissions[i].MaxCount) return i;
        }
        return -1;
    }

    public Task<ActionRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM actions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return Task.FromResult(cmd.ExecuteScalar() is string json ? Deserialize(json) : null);
    }

    public Task<IReadOnlyList<ActionRecord>> QueryAsync(ActionQuery q, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        AddFilter(cmd, where, q, "q");
        cmd.CommandText = "SELECT json FROM actions"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + (q.NewestFirst ? " ORDER BY created_at DESC, id DESC" : " ORDER BY created_at, id")
            + " LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, q.Limit));

        var rows = new List<ActionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(Deserialize(reader.GetString(0)));
        return Task.FromResult<IReadOnlyList<ActionRecord>>(rows);
    }

    public Task<int> CountAsync(ActionQuery q, TimeSpan? window = null, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        AddFilter(cmd, where, q, "q");
        if (window is { } w)
        {
            where.Add("updated_at>=$qw");
            cmd.Parameters.AddWithValue("$qw", (_clock.GetUtcNow() - w).ToUniversalTime().ToString("O"));
        }
        cmd.CommandText = "SELECT COUNT(*) FROM actions" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "");
        return Task.FromResult((int)(long)(cmd.ExecuteScalar() ?? 0L));
    }

    private static void AddFilter(SqliteCommand cmd, List<string> where, ActionQuery q, string p)
    {
        if (q.Status is { } s) { where.Add($"status=${p}s"); cmd.Parameters.AddWithValue($"${p}s", s.ToString()); }
        if (q.AnyOfStatus is { Count: > 0 } any)
        {
            var names = new List<string>();
            for (var i = 0; i < any.Count; i++)
            {
                names.Add($"${p}m{i}");
                cmd.Parameters.AddWithValue($"${p}m{i}", any[i].ToString());
            }
            where.Add($"status IN ({string.Join(",", names)})");
        }
        if (q.SessionId is { } ses) { where.Add($"session_id=${p}ses"); cmd.Parameters.AddWithValue($"${p}ses", ses); }
        if (q.RequesterId is { } r) { where.Add($"requester_id=${p}r"); cmd.Parameters.AddWithValue($"${p}r", r); }
        if (q.ModuleId is { } m) { where.Add($"module=${p}m"); cmd.Parameters.AddWithValue($"${p}m", m); }
        if (q.ToolName is { } tn) { where.Add($"tool=${p}tn"); cmd.Parameters.AddWithValue($"${p}tn", tn); }
        if (q.UpdatedSince is { } since) { where.Add($"updated_at>=${p}since"); cmd.Parameters.AddWithValue($"${p}since", since.ToUniversalTime().ToString("O")); }
        if (q.CreatedBefore is { } before) { where.Add($"created_at<${p}before"); cmd.Parameters.AddWithValue($"${p}before", before.ToUniversalTime().ToString("O")); }
    }

    public Task<ActionRecord?> TransitionAsync(
        string id, ActionStatus expected, ActionStatus next,
        Func<ActionRecord, ActionRecord> update, CancellationToken ct = default) =>
        TransitionAsync(id, expected, current =>
            ActionStateMachine.CanTransition(expected, next, current.Error?.Retryable ?? false)
                ? new ActionTransition(next, update(current))
                : null, ct);

    public Task<ActionRecord?> TransitionAsync(
        string id, ActionStatus expected, ActionStatus next, Func<ActionRecord, ActionRecord> update,
        IReadOnlyList<Admission> admissions, Func<int, ActionRecord, ActionRecord> refused, CancellationToken ct = default) =>
        TransitionCore(id, expected, current =>
            ActionStateMachine.CanTransition(expected, next, current.Error?.Retryable ?? false)
                ? new ActionTransition(next, update(current))
                : null, admissions, refused);

    public Task<ActionRecord?> TransitionAsync(
        string id, ActionStatus expected, Func<ActionRecord, ActionTransition?> decide, CancellationToken ct = default) =>
        TransitionCore(id, expected, decide, [], null);

    /// <summary>The transition, the admissions it checks, and (when given) the audit entry it is committed with, all in one transaction.</summary>
    private Task<ActionRecord?> TransitionCore(string id, ActionStatus expected, Func<ActionRecord, ActionTransition?> decide,
        IReadOnlyList<Admission> admissions, Func<int, ActionRecord, ActionRecord>? refused, Func<ActionRecord, AuditEntry>? audit = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false); // write lock up front: this is the compare-and-set

        using var read = c.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT json FROM actions WHERE id=$id";
        read.Parameters.AddWithValue("$id", id);
        if (read.ExecuteScalar() is not string json) return Task.FromResult<ActionRecord?>(null);

        var current = Deserialize(json);
        if (current.Status != expected) return Task.FromResult<ActionRecord?>(null);
        // Admissions are counted in this transaction. A refusal changes the action only to what the caller's refusal says (for example failed).
        var refusedBy = FirstRefusal(c, tx, admissions);
        ActionTransition? t;
        if (refusedBy >= 0)
        {
            if (refused is null) return Task.FromResult<ActionRecord?>(null);
            var alt = refused(refusedBy, current);
            t = new ActionTransition(alt.Status, alt);
        }
        else t = decide(current);
        if (t is null) return Task.FromResult<ActionRecord?>(null);
        if (!ActionStateMachine.CanTransition(expected, t.Next, current.Error?.Retryable ?? false))
            return Task.FromResult<ActionRecord?>(null);

        var updated = t.Record with { Status = t.Next, UpdatedAt = _clock.GetUtcNow() };

        using var write = c.CreateCommand();
        write.Transaction = tx;
        write.CommandText = "UPDATE actions SET status=$status, updated_at=$u, json=$json WHERE id=$id";
        write.Parameters.AddWithValue("$status", updated.Status.ToString());
        write.Parameters.AddWithValue("$u", updated.UpdatedAt.ToUniversalTime().ToString("O"));
        write.Parameters.AddWithValue("$json", JsonSerializer.Serialize(updated, Json));
        write.Parameters.AddWithValue("$id", id);
        write.ExecuteNonQuery();
        if (Usage.IsEnd(updated.Status)) AddUsage(c, tx, updated);
        if (audit is not null) SqliteAuditSink.Append(c, tx, audit(updated));
        tx.Commit();
        return Task.FromResult<ActionRecord?>(updated);
    }

    public Task<ActionRecord?> TransitionAuditedAsync(
        string id, ActionStatus expected, ActionStatus next, Func<ActionRecord, ActionRecord> update,
        Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default) =>
        TransitionCore(id, expected, current =>
            ActionStateMachine.CanTransition(expected, next, current.Error?.Retryable ?? false)
                ? new ActionTransition(next, update(current))
                : null, [], null, audit);

    public Task<ActionRecord?> TransitionDecidedAuditedAsync(
        string id, ActionStatus expected, Func<ActionRecord, ActionTransition?> decide,
        Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default) =>
        TransitionCore(id, expected, decide, [], null, audit);

    public Task<ActionRecord?> TransitionAdmittedAuditedAsync(
        string id, ActionStatus expected, ActionStatus next, Func<ActionRecord, ActionRecord> update,
        IReadOnlyList<Admission> admissions, Func<int, ActionRecord, ActionRecord> refused,
        Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default) =>
        TransitionCore(id, expected, current =>
            ActionStateMachine.CanTransition(expected, next, current.Error?.Retryable ?? false)
                ? new ActionTransition(next, update(current))
                : null, admissions, refused, audit);

    public Task<ActionRecord?> TryCreateAuditedAsync(ActionRecord record, IReadOnlyList<Admission> admissions,
        Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default)
    {
        if (!ActionStateMachine.IsValidInitial(record.Status))
            throw new InvalidOperationException($"{record.Status} is not a valid initial status.");
        return Task.FromResult(Create(record, admissions, static _ => null, audit));
    }

    // ---- usage rollups ----

    /// <summary>Adds one ended action to its day's rollup, inside the transaction that ended it, so the counts are exact.</summary>
    private static void AddUsage(SqliteConnection c, SqliteTransaction tx, ActionRecord r)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO usage_daily(day, subject, tool, module, profile, calls, succeeded, failed, denied, rejected, other, records, recipients, bytes)
            VALUES($day, $s, $t, $m, $p, 1, $ok, $fail, $deny, $rej, $other, $rec, $to, $bytes)
            ON CONFLICT(day, subject, tool) DO UPDATE SET
                module = excluded.module, profile = excluded.profile, calls = calls + 1,
                succeeded = succeeded + excluded.succeeded, failed = failed + excluded.failed, denied = denied + excluded.denied,
                rejected = rejected + excluded.rejected, other = other + excluded.other, records = records + excluded.records,
                recipients = recipients + excluded.recipients, bytes = bytes + excluded.bytes
            """;
        cmd.Parameters.AddWithValue("$day", Day(r.UpdatedAt));
        cmd.Parameters.AddWithValue("$s", r.Requester.Id);
        cmd.Parameters.AddWithValue("$t", r.ToolName);
        cmd.Parameters.AddWithValue("$m", r.ModuleId);
        cmd.Parameters.AddWithValue("$p", (object?)r.Profile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ok", r.Status == ActionStatus.Succeeded ? 1 : 0);
        cmd.Parameters.AddWithValue("$fail", r.Status == ActionStatus.Failed ? 1 : 0);
        cmd.Parameters.AddWithValue("$deny", r.Status == ActionStatus.Denied ? 1 : 0);
        cmd.Parameters.AddWithValue("$rej", r.Status is ActionStatus.Rejected or ActionStatus.ChangesRequested ? 1 : 0);
        cmd.Parameters.AddWithValue("$other", r.Status is ActionStatus.Expired or ActionStatus.Cancelled ? 1 : 0);
        cmd.Parameters.AddWithValue("$rec", r.ResultStats?.Records ?? 0);
        cmd.Parameters.AddWithValue("$to", r.ResultStats?.Recipients ?? 0);
        cmd.Parameters.AddWithValue("$bytes", r.ResultBytes ?? 0);
        cmd.ExecuteNonQuery();
    }

    private static string Day(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private byte[]? _salt;

    public byte[] EntitySalt
    {
        get
        {
            if (_salt is { } known) return known;
            using var c = Open();
            using var tx = c.BeginTransaction(deferred: false);
            using var read = c.CreateCommand();
            read.Transaction = tx;
            read.CommandText = "SELECT value FROM settings WHERE key='entity_salt'";
            if (read.ExecuteScalar() is not string value)
            {
                value = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                using var write = c.CreateCommand();
                write.Transaction = tx;
                write.CommandText = "INSERT INTO settings(key, value) VALUES('entity_salt', $v)";
                write.Parameters.AddWithValue("$v", value);
                write.ExecuteNonQuery();
            }
            tx.Commit();
            return _salt = Convert.FromBase64String(value);
        }
    }

    public void RecordEntities(DateOnly day, string subject, string tool, IReadOnlyCollection<string> hashes)
    {
        if (hashes.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        var d = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var added = 0;
        foreach (var h in hashes)
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO usage_entities(day, subject, tool, hash) VALUES($d, $s, $t, $h)";
            ins.Parameters.AddWithValue("$d", d);
            ins.Parameters.AddWithValue("$s", subject);
            ins.Parameters.AddWithValue("$t", tool);
            ins.Parameters.AddWithValue("$h", h);
            added += ins.ExecuteNonQuery();
        }
        using var up = c.CreateCommand();
        up.Transaction = tx;
        up.CommandText = "UPDATE usage_daily SET entities = entities + $n WHERE day=$d AND subject=$s AND tool=$t";
        up.Parameters.AddWithValue("$n", added);
        up.Parameters.AddWithValue("$d", d);
        up.Parameters.AddWithValue("$s", subject);
        up.Parameters.AddWithValue("$t", tool);
        up.ExecuteNonQuery();
        tx.Commit();
    }

    public IReadOnlyList<UsageRow> ReadUsage(DateOnly from, DateOnly to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT day, subject, profile, tool, module, calls, succeeded, failed, denied, rejected, other, records, recipients, bytes, entities
            FROM usage_daily WHERE day >= $f AND day <= $t ORDER BY day, subject, tool
            """;
        cmd.Parameters.AddWithValue("$f", from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        using var rd = cmd.ExecuteReader();
        var rows = new List<UsageRow>();
        while (rd.Read())
            rows.Add(new UsageRow(DateOnly.ParseExact(rd.GetString(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetString(3), rd.GetString(4),
                rd.GetInt64(5), rd.GetInt64(6), rd.GetInt64(7), rd.GetInt64(8), rd.GetInt64(9), rd.GetInt64(10),
                rd.GetInt64(11), rd.GetInt64(12), rd.GetInt64(13), rd.GetInt64(14)));
        return rows;
    }

    public int PurgeUsage(DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM usage_daily WHERE day < $keep; DELETE FROM usage_entities WHERE day < $entities;";
        cmd.Parameters.AddWithValue("$keep", Usage.Retention(now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$entities", Day(now - Usage.EntityRetention));
        return cmd.ExecuteNonQuery();
    }

    private const string Terminal = "('Succeeded','Failed','Denied','Rejected','ChangesRequested','Expired','Cancelled')";

    /// <summary>What a purge removed and kept: the actions deleted, and the digest of the decision records written in their place.</summary>
    public sealed record PurgeResult(int Removed, string Batch, string? Digest);

    /// <summary>Deletes terminal actions created before <paramref name="before"/> (arguments and results live in these rows), keeping a
    /// decision record of each (audit D3: how it was decided, never what it carried), in one transaction. The audit trail is kept;
    /// the caller writes the batch's digest into it, binding the records into the chain.</summary>
    public PurgeResult PurgeTerminalBefore(DateTimeOffset before)
    {
        var batch = "purge_" + Guid.NewGuid().ToString("N")[..16];
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var read = c.CreateCommand();
        read.Transaction = tx;
        read.CommandText = $"SELECT json FROM actions WHERE created_at<$b AND status IN {Terminal} ORDER BY id";
        read.Parameters.AddWithValue("$b", before.ToUniversalTime().ToString("O"));
        var hashes = new List<string>();
        var kept = new List<DecisionRecord>();
        using (var r = read.ExecuteReader())
            while (r.Read()) kept.Add(DecisionRecord.From(Deserialize(r.GetString(0)), batch));
        foreach (var record in kept)
        {
            var json = record.ToJson();
            var hash = DecisionRecord.HashOf(json);
            hashes.Add(hash);
            using var insert = c.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR REPLACE INTO decision_records(action_id,created_at,batch,hash,json) VALUES($i,$c,$b,$h,$j)";
            insert.Parameters.AddWithValue("$i", record.ActionId);
            insert.Parameters.AddWithValue("$c", record.CreatedAt.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$b", batch);
            insert.Parameters.AddWithValue("$h", hash);
            insert.Parameters.AddWithValue("$j", json);
            insert.ExecuteNonQuery();
        }
        using var delete = c.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = $"DELETE FROM actions WHERE created_at<$b AND status IN {Terminal}";
        delete.Parameters.AddWithValue("$b", before.ToUniversalTime().ToString("O"));
        var removed = delete.ExecuteNonQuery();
        tx.Commit();
        return new PurgeResult(removed, batch, kept.Count == 0 ? null : DecisionRecord.Digest(hashes));
    }

    /// <summary>Decision records for actions created in [from, until), oldest first, with their stored hash and batch.</summary>
    public IReadOnlyList<(DecisionRecord Record, string Json, string Hash)> ReadDecisionRecords(DateTimeOffset? from = null, DateTimeOffset? until = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json, hash FROM decision_records WHERE created_at>=$f AND created_at<$u ORDER BY created_at, action_id";
        cmd.Parameters.AddWithValue("$f", (from ?? DateTimeOffset.MinValue).ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$u", (until ?? DateTimeOffset.MaxValue).ToUniversalTime().ToString("O"));
        using var r = cmd.ExecuteReader();
        var rows = new List<(DecisionRecord, string, string)>();
        while (r.Read()) rows.Add((DecisionRecord.Parse(r.GetString(0)), r.GetString(0), r.GetString(1)));
        return rows;
    }

    /// <summary>Deletes decision records older than the audit retention period. Returns how many.</summary>
    public int PurgeDecisionRecordsBefore(DateTimeOffset before)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM decision_records WHERE created_at<$b";
        cmd.Parameters.AddWithValue("$b", before.ToUniversalTime().ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    internal SqliteConnection OpenConnection() => Open();

    private static void Bind(SqliteCommand cmd, ActionRecord r)
    {
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$tool", r.ToolName);
        cmd.Parameters.AddWithValue("$module", r.ModuleId);
        cmd.Parameters.AddWithValue("$status", r.Status.ToString());
        cmd.Parameters.AddWithValue("$req", r.Requester.Id);
        cmd.Parameters.AddWithValue("$ses", (object?)r.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$idem", (object?)r.IdempotencyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", r.ArgumentsHash);
        cmd.Parameters.AddWithValue("$c", r.CreatedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$u", r.UpdatedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(r, Json));
    }

    private static ActionRecord Deserialize(string json) =>
        JsonSerializer.Deserialize<ActionRecord>(json, Json) ?? throw new InvalidOperationException("Corrupt action row.");
}

/// <summary>
/// Append-only audit log in the same database file, hash-chained: each entry stores the previous entry's hash and its own, over its
/// sequence number, fields and the previous hash. Editing, removing or reordering an entry breaks every hash after it, which
/// <see cref="AuditChain.Verify"/> reports. Removing the newest entries is only caught against a checkpoint held somewhere else.
/// </summary>
public sealed class SqliteAuditSink(SqliteActionStore store) : IAuditSink, IAuditReader, IAuditQuery
{
    /// <summary>The store whose database holds the trail. The executor commits an outcome together with its entry only when this is the
    /// store it runs on (see <see cref="IAtomicAuditStore"/>).</summary>
    public SqliteActionStore Store => store;

    /// <summary>One page of the trail, newest first, from the database: the filters and the cursor are applied there, so a page does not read the
    /// rest of the trail.</summary>
    public IReadOnlyList<AuditRow> Page(AuditQuery query)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        var where = Filters(cmd, query);
        if (query.Before is { } before)
        {
            where.Add("seq < $before");
            cmd.Parameters.AddWithValue("$before", before);
        }
        cmd.CommandText = "SELECT seq,at,event,action_id,tool,actor,detail FROM audit" + Where(where) + " ORDER BY seq DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", query.Limit);
        var rows = new List<AuditRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new AuditRow(r.GetInt64(0), new AuditEntry(DateTimeOffset.Parse(r.GetString(1)), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6))));
        return rows;
    }

    public long Count(AuditQuery query)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit" + Where(Filters(cmd, query));
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<string> Actors(string evt)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT actor FROM audit WHERE event=$e AND actor IS NOT NULL ORDER BY actor";
        cmd.Parameters.AddWithValue("$e", evt);
        var actors = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) actors.Add(r.GetString(0));
        return actors;
    }

    /// <summary>The filters of a query as SQL conditions, with their parameters added to <paramref name="cmd"/>. Timestamps are stored in
    /// the "O" format, so a time filter compares the same text (UTC entries, as the Host writes them).</summary>
    private static List<string> Filters(SqliteCommand cmd, AuditQuery q)
    {
        var where = new List<string>();
        if (q.Event is not null) { where.Add("event=$event"); cmd.Parameters.AddWithValue("$event", q.Event); }
        if (q.Actor is not null) { where.Add("actor=$actor"); cmd.Parameters.AddWithValue("$actor", q.Actor); }
        if (q.DetailStartsWith is not null)
        {
            where.Add("substr(detail,1,$plen)=$prefix");
            cmd.Parameters.AddWithValue("$plen", q.DetailStartsWith.Length);
            cmd.Parameters.AddWithValue("$prefix", q.DetailStartsWith);
        }
        if (q.Since is { } since) { where.Add("at>=$since"); cmd.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O")); }
        return where;
    }

    private static string Where(List<string> where) => where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        using var c = store.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false); // the chain needs the previous hash and the next seq to be stable
        Append(c, tx, e);
        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>Appends one chained entry inside the caller's transaction, so it commits or rolls back with whatever else that transaction does.</summary>
    internal static void Append(SqliteConnection c, SqliteTransaction tx, AuditEntry e)
    {
        using var last = c.CreateCommand();
        last.Transaction = tx;
        last.CommandText = "SELECT hash FROM audit ORDER BY seq DESC LIMIT 1";
        var prev = last.ExecuteScalar() as string;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO audit(at,event,action_id,tool,actor,detail,prev_hash) VALUES($at,$e,$a,$t,$who,$d,$p); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$at", e.At.ToString("O"));
        cmd.Parameters.AddWithValue("$e", e.Event);
        cmd.Parameters.AddWithValue("$a", (object?)e.ActionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)e.ToolName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$who", (object?)e.Actor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)e.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)prev ?? DBNull.Value);
        var seq = (long)cmd.ExecuteScalar()!;
        using var set = c.CreateCommand();
        set.Transaction = tx;
        set.CommandText = "UPDATE audit SET hash=$h WHERE seq=$s";
        set.Parameters.AddWithValue("$h", AuditChain.Hash(seq, e.At.ToString("O"), e.Event, e.ActionId, e.ToolName, e.Actor, e.Detail, prev));
        set.Parameters.AddWithValue("$s", seq);
        set.ExecuteNonQuery();
    }

    /// <summary>The newest chained entry, without reading the whole trail.</summary>
    public AuditCheckpoint? Head()
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq, hash FROM audit ORDER BY seq DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() && !r.IsDBNull(1) ? new AuditCheckpoint(r.GetInt64(0), r.GetString(1)) : null;
    }

    /// <summary>Every entry with its chain fields, in order (for <see cref="AuditChain.Verify"/>).</summary>
    /// <summary>Chained entries after a sequence number, oldest first, at most <paramref name="limit"/> (for streaming them out).</summary>
    public IReadOnlyList<ChainedAuditEntry> ReadChainAfter(long seq, int limit = 500)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq,at,event,action_id,tool,actor,detail,prev_hash,hash FROM audit WHERE seq>$s ORDER BY seq LIMIT $n";
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$n", limit);
        var rows = new List<ChainedAuditEntry>();
        using var r = cmd.ExecuteReader();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            rows.Add(new ChainedAuditEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), S(3), S(4), S(5), S(6), S(7), S(8)));
        return rows;
    }

    public IReadOnlyList<ChainedAuditEntry> ReadChain()
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq,at,event,action_id,tool,actor,detail,prev_hash,hash FROM audit ORDER BY seq";
        var rows = new List<ChainedAuditEntry>();
        using var r = cmd.ExecuteReader();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            rows.Add(new ChainedAuditEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), S(3), S(4), S(5), S(6), S(7), S(8)));
        return rows;
    }

    public IReadOnlyList<AuditEntry> ReadRecent(int limit)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at,event,action_id,tool,actor,detail FROM (SELECT * FROM audit ORDER BY seq DESC LIMIT $n) ORDER BY seq";
        cmd.Parameters.AddWithValue("$n", limit);
        return ReadEntries(cmd);
    }

    public IReadOnlyList<AuditEntry> Read(string? actionId = null)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at,event,action_id,tool,actor,detail FROM audit"
            + (actionId is null ? "" : " WHERE action_id=$a") + " ORDER BY seq";
        if (actionId is not null) cmd.Parameters.AddWithValue("$a", actionId);
        return ReadEntries(cmd);
    }

    private static List<AuditEntry> ReadEntries(SqliteCommand cmd)
    {
        var rows = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new AuditEntry(DateTimeOffset.Parse(r.GetString(0)), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return rows;
    }
}

/// <summary>Identity levels in the same database file, so a suspension survives restarts and holds across processes.</summary>
public sealed class SqliteIdentityLevelStore(SqliteActionStore store) : IIdentityLevelStore
{
    public IdentityLevelRow? Get(string subject)
    {
        using var c = store.OpenConnection();
        return Read(c, null, subject);
    }

    private static IdentityLevelRow? Read(SqliteConnection c, SqliteTransaction? tx, string subject)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT subject, level, reason, since, enforced FROM identity_levels WHERE subject=$s";
        cmd.Parameters.AddWithValue("$s", subject);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Row(r) : null;
    }

    private static IdentityLevelRow Row(SqliteDataReader r) =>
        new(r.GetString(0), Enum.Parse<IdentityLevel>(r.GetString(1)), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture), r.GetInt64(4) != 0);

    public IReadOnlyList<IdentityLevelRow> List()
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT subject, level, reason, since, enforced FROM identity_levels ORDER BY subject";
        using var r = cmd.ExecuteReader();
        var rows = new List<IdentityLevelRow>();
        while (r.Read()) rows.Add(Row(r));
        return rows;
    }

    public bool Raise(IdentityLevelRow row)
    {
        using var c = store.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        if (Read(c, tx, row.Subject) is { } old && !IdentityLevels.Tightens(old, row)) return false;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO identity_levels(subject, level, reason, since, enforced) VALUES($s, $l, $r, $t, $e)
            ON CONFLICT(subject) DO UPDATE SET level=excluded.level, reason=excluded.reason, since=excluded.since, enforced=excluded.enforced
            """;
        cmd.Parameters.AddWithValue("$s", row.Subject);
        cmd.Parameters.AddWithValue("$l", row.Level.ToString());
        cmd.Parameters.AddWithValue("$r", row.Reason);
        cmd.Parameters.AddWithValue("$t", row.Since.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$e", row.Enforced ? 1 : 0);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public bool Clear(string subject)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM identity_levels WHERE subject=$s";
        cmd.Parameters.AddWithValue("$s", subject);
        return cmd.ExecuteNonQuery() > 0;
    }
}

/// <summary>Session and caller labels in the same database file, so a restart does not launder them.</summary>
public sealed class SqliteLabelStore(SqliteActionStore store) : ILabelStore
{
    private static string T(DateTimeOffset d) => d.ToUniversalTime().ToString("O");

    public void Add(string scope, string label, string source, DateTimeOffset until)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_labels(scope, label, source, until) VALUES($s, $l, $src, $u)
            ON CONFLICT(scope, label, source) DO UPDATE SET until = CASE WHEN excluded.until > session_labels.until THEN excluded.until ELSE session_labels.until END
            """;
        cmd.Parameters.AddWithValue("$s", scope);
        cmd.Parameters.AddWithValue("$l", label);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$u", T(until));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Label, string Source)> Get(string scope, DateTimeOffset now)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT label, source FROM session_labels WHERE scope=$s AND until > $n";
        cmd.Parameters.AddWithValue("$s", scope);
        cmd.Parameters.AddWithValue("$n", T(now));
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, string)>();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        return rows;
    }

    public int Purge(DateTimeOffset now)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM session_labels WHERE until <= $n";
        cmd.Parameters.AddWithValue("$n", T(now));
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>Temporary access grants in the same database file; a call uses one in the same transaction that checks it.</summary>
public sealed class SqliteAccessGrantStore(SqliteActionStore store) : IAccessGrantStore
{
    private static string T(DateTimeOffset d) => d.ToUniversalTime().ToString("O");

    public void Add(AccessGrant g)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO access_grants(id,subject,tool,until,calls_left,granted_by,reason,at) VALUES($i,$s,$t,$u,$n,$b,$r,$a)";
        cmd.Parameters.AddWithValue("$i", g.Id);
        cmd.Parameters.AddWithValue("$s", g.Subject);
        cmd.Parameters.AddWithValue("$t", g.Tool);
        cmd.Parameters.AddWithValue("$u", T(g.Until));
        cmd.Parameters.AddWithValue("$n", g.CallsLeft);
        cmd.Parameters.AddWithValue("$b", g.GrantedBy);
        cmd.Parameters.AddWithValue("$r", g.Reason);
        cmd.Parameters.AddWithValue("$a", T(g.At));
        cmd.ExecuteNonQuery();
    }

    private static List<AccessGrant> Read(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var rows = new List<AccessGrant>();
        while (r.Read())
            rows.Add(new AccessGrant(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                (int)r.GetInt64(4), r.GetString(5), r.GetString(6), DateTimeOffset.Parse(r.GetString(7), System.Globalization.CultureInfo.InvariantCulture)));
        return rows;
    }

    private const string Columns = "id,subject,tool,until,calls_left,granted_by,reason,at";

    public IReadOnlyList<AccessGrant> Active(string subject, DateTimeOffset now)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM access_grants WHERE subject=$s AND until > $n AND calls_left > 0";
        cmd.Parameters.AddWithValue("$s", subject);
        cmd.Parameters.AddWithValue("$n", T(now));
        return Read(cmd);
    }

    public IReadOnlyList<AccessGrant> All(DateTimeOffset now)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM access_grants WHERE until > $n AND calls_left > 0 ORDER BY at";
        cmd.Parameters.AddWithValue("$n", T(now));
        return Read(cmd);
    }

    /// <summary>Every grant still stored, ended ones included (revoked grants are deleted), for the access report.</summary>
    public IReadOnlyList<AccessGrant> AllEver()
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM access_grants ORDER BY at";
        return Read(cmd);
    }

    public AccessGrant? Use(string subject, string tool, DateTimeOffset now)
    {
        using var c = store.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        using var find = c.CreateCommand();
        find.Transaction = tx;
        find.CommandText = $"SELECT {Columns} FROM access_grants WHERE subject=$s AND tool=$t AND until > $n AND calls_left > 0 ORDER BY until LIMIT 1";
        find.Parameters.AddWithValue("$s", subject);
        find.Parameters.AddWithValue("$t", tool);
        find.Parameters.AddWithValue("$n", T(now));
        if (Read(find).FirstOrDefault() is not { } g) return null;
        using var use = c.CreateCommand();
        use.Transaction = tx;
        use.CommandText = "UPDATE access_grants SET calls_left = calls_left - 1 WHERE id=$i";
        use.Parameters.AddWithValue("$i", g.Id);
        use.ExecuteNonQuery();
        tx.Commit();
        return g with { CallsLeft = g.CallsLeft - 1 };
    }

    public bool Revoke(string id)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM access_grants WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}

/// <summary>Sessions and per-subject taint in the same database file.</summary>
public sealed class SqliteSessionStore(SqliteActionStore store) : ISessionStore
{
    private static string T(DateTimeOffset d) => d.ToUniversalTime().ToString("O");

    public void Create(SessionRow row)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions(id,owner,tainted,created_at,last_seen_at) VALUES($i,$o,$t,$c,$l)";
        cmd.Parameters.AddWithValue("$i", row.Id);
        cmd.Parameters.AddWithValue("$o", row.Owner);
        cmd.Parameters.AddWithValue("$t", row.Tainted ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", T(row.CreatedAt));
        cmd.Parameters.AddWithValue("$l", T(row.LastSeenAt));
        cmd.ExecuteNonQuery();
    }

    public SessionRow? Get(string id)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,owner,tainted,created_at,last_seen_at FROM sessions WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new SessionRow(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, DateTimeOffset.Parse(r.GetString(3)), DateTimeOffset.Parse(r.GetString(4)))
            : null;
    }

    public void Touch(string id, DateTimeOffset at)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET last_seen_at=$l WHERE id=$i";
        cmd.Parameters.AddWithValue("$l", T(at));
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkTainted(string id)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET tainted=1 WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    public void TaintSubject(string subject, DateTimeOffset until)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        // Taint only ever extends.
        cmd.CommandText = """
            INSERT INTO subject_taint(subject, until) VALUES($s,$u)
            ON CONFLICT(subject) DO UPDATE SET until = CASE WHEN excluded.until > subject_taint.until THEN excluded.until ELSE subject_taint.until END
            """;
        cmd.Parameters.AddWithValue("$s", subject);
        cmd.Parameters.AddWithValue("$u", T(until));
        cmd.ExecuteNonQuery();
    }

    public DateTimeOffset? SubjectTaintedUntil(string subject)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT until FROM subject_taint WHERE subject=$s";
        cmd.Parameters.AddWithValue("$s", subject);
        return cmd.ExecuteScalar() is string s ? DateTimeOffset.Parse(s) : null;
    }

    public int Purge(DateTimeOffset idleBefore, DateTimeOffset now)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE last_seen_at<$i; DELETE FROM subject_taint WHERE until<$n;";
        cmd.Parameters.AddWithValue("$i", T(idleBefore));
        cmd.Parameters.AddWithValue("$n", T(now));
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>Trust promotions in the same database file, so every process on it agrees.</summary>
public sealed class SqliteTrustStore(SqliteActionStore store) : ITrustStore
{
    public bool IsPromoted(string tool)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM trust_promotions WHERE tool=$t";
        cmd.Parameters.AddWithValue("$t", tool);
        return cmd.ExecuteScalar() is not null;
    }

    public void Promote(string tool, string by, DateTimeOffset at)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO trust_promotions(tool,by,at) VALUES($t,$b,$a)";
        cmd.Parameters.AddWithValue("$t", tool);
        cmd.Parameters.AddWithValue("$b", by);
        cmd.Parameters.AddWithValue("$a", at.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public bool Demote(string tool)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM trust_promotions WHERE tool=$t";
        cmd.Parameters.AddWithValue("$t", tool);
        return cmd.ExecuteNonQuery() > 0;
    }
    public IReadOnlyList<string> ListPromoted()
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT tool FROM trust_promotions ORDER BY tool";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}

/// <summary>Policy versions in the same database file (audit D2).</summary>
/// <summary>Policy proposals in the same database file. The record is stored as JSON; status is a column so a decision is a compare-and-set.</summary>
public sealed class SqlitePolicyProposalStore(SqliteActionStore store) : IPolicyProposalStore
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public void Add(PolicyProposal proposal)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO policy_proposals(id,created_at,status,json) VALUES($i,$c,$s,$j)";
        cmd.Parameters.AddWithValue("$i", proposal.Id);
        cmd.Parameters.AddWithValue("$c", proposal.CreatedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$s", proposal.Status.ToString());
        cmd.Parameters.AddWithValue("$j", System.Text.Json.JsonSerializer.Serialize(proposal, Json));
        cmd.ExecuteNonQuery();
    }

    public PolicyProposal? Get(string id) => Read("WHERE id=$i", id).FirstOrDefault();

    public IReadOnlyList<PolicyProposal> List() => Read("ORDER BY created_at DESC", null);

    public bool Update(PolicyProposal proposal, ProposalStatus expected)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE policy_proposals SET status=$s, json=$j WHERE id=$i AND status=$e";
        cmd.Parameters.AddWithValue("$i", proposal.Id);
        cmd.Parameters.AddWithValue("$s", proposal.Status.ToString());
        cmd.Parameters.AddWithValue("$e", expected.ToString());
        cmd.Parameters.AddWithValue("$j", System.Text.Json.JsonSerializer.Serialize(proposal, Json));
        return cmd.ExecuteNonQuery() == 1;
    }

    private List<PolicyProposal> Read(string where, string? id)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM policy_proposals " + where;
        if (id is not null) cmd.Parameters.AddWithValue("$i", id);
        var rows = new List<PolicyProposal>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(System.Text.Json.JsonSerializer.Deserialize<PolicyProposal>(r.GetString(0), Json)!);
        return rows;
    }
}

public sealed class SqlitePolicyVersionStore(SqliteActionStore store) : IPolicyVersionStore
{
    private static string T(DateTimeOffset d) => d.ToUniversalTime().ToString("O");
    private static DateTimeOffset P(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    public bool Record(string hash, string json, DateTimeOffset at)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO policy_versions(hash,json,first_loaded,last_loaded) VALUES($h,$j,$a,$a) ON CONFLICT(hash) DO NOTHING";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$j", json);
        cmd.Parameters.AddWithValue("$a", T(at));
        if (cmd.ExecuteNonQuery() == 1) return true;
        using var touch = c.CreateCommand();
        touch.CommandText = "UPDATE policy_versions SET last_loaded=$a WHERE hash=$h";
        touch.Parameters.AddWithValue("$h", hash);
        touch.Parameters.AddWithValue("$a", T(at));
        touch.ExecuteNonQuery();
        return false;
    }

    public PolicyVersion? Get(string hash) => Read("WHERE hash=$h", hash).FirstOrDefault();

    public IReadOnlyList<PolicyVersion> List() => Read("ORDER BY first_loaded", null);

    private List<PolicyVersion> Read(string where, string? hash)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT hash,json,first_loaded,last_loaded FROM policy_versions " + where;
        if (hash is not null) cmd.Parameters.AddWithValue("$h", hash);
        using var r = cmd.ExecuteReader();
        var rows = new List<PolicyVersion>();
        while (r.Read()) rows.Add(new PolicyVersion(r.GetString(0), r.GetString(1), P(r.GetString(2)), P(r.GetString(3))));
        return rows;
    }
}

/// <summary>Work items, continuations and thread notes in the same database file. Changes are compare-and-set in one transaction, so
/// two Host processes never both claim one item.</summary>
public sealed class SqliteWorkStore(SqliteActionStore store) : IWorkStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public void Add(WorkItem item)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO work_items(id,status,thread,created_at,json) VALUES($i,$s,$t,$c,$j)";
        cmd.Parameters.AddWithValue("$i", item.Id);
        cmd.Parameters.AddWithValue("$s", item.Status.ToString());
        cmd.Parameters.AddWithValue("$t", item.Thread);
        cmd.Parameters.AddWithValue("$c", item.CreatedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(item, Json));
        cmd.ExecuteNonQuery();
    }

    public WorkItem? Get(string id) => Scalar<WorkItem>("SELECT json FROM work_items WHERE id=$i", id);

    public WorkItem? Transition(string id, WorkStatus expected, Func<WorkItem, WorkItem> change)
    {
        using var c = store.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        using var read = c.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT json FROM work_items WHERE id=$i AND status=$s";
        read.Parameters.AddWithValue("$i", id);
        read.Parameters.AddWithValue("$s", expected.ToString());
        if (read.ExecuteScalar() is not string json) return null;
        var next = change(JsonSerializer.Deserialize<WorkItem>(json, Json)!);
        using var write = c.CreateCommand();
        write.Transaction = tx;
        write.CommandText = "UPDATE work_items SET status=$s, json=$j WHERE id=$i";
        write.Parameters.AddWithValue("$i", id);
        write.Parameters.AddWithValue("$s", next.Status.ToString());
        write.Parameters.AddWithValue("$j", JsonSerializer.Serialize(next, Json));
        write.ExecuteNonQuery();
        tx.Commit();
        return next;
    }

    public IReadOnlyList<WorkItem> Query(WorkStatus? status = null, string? thread = null, int limit = 200)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (status is not null) { where.Add("status=$s"); cmd.Parameters.AddWithValue("$s", status.ToString()); }
        if (thread is not null) { where.Add("thread=$t"); cmd.Parameters.AddWithValue("$t", thread); }
        cmd.CommandText = "SELECT json FROM work_items" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY created_at, id LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        return Read<WorkItem>(cmd);
    }

    public void AddContinuation(Continuation k)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO continuations(id,status,json) VALUES($i,$s,$j)";
        cmd.Parameters.AddWithValue("$i", k.Id);
        cmd.Parameters.AddWithValue("$s", k.Status.ToString());
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(k, Json));
        cmd.ExecuteNonQuery();
    }

    public Continuation? GetContinuation(string id) => Scalar<Continuation>("SELECT json FROM continuations WHERE id=$i", id);

    public Continuation? TransitionContinuation(string id, ContinuationStatus expected, Func<Continuation, Continuation> change)
    {
        using var c = store.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        using var read = c.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT json FROM continuations WHERE id=$i AND status=$s";
        read.Parameters.AddWithValue("$i", id);
        read.Parameters.AddWithValue("$s", expected.ToString());
        if (read.ExecuteScalar() is not string json) return null;
        var next = change(JsonSerializer.Deserialize<Continuation>(json, Json)!);
        using var write = c.CreateCommand();
        write.Transaction = tx;
        write.CommandText = "UPDATE continuations SET status=$s, json=$j WHERE id=$i";
        write.Parameters.AddWithValue("$i", id);
        write.Parameters.AddWithValue("$s", next.Status.ToString());
        write.Parameters.AddWithValue("$j", JsonSerializer.Serialize(next, Json));
        write.ExecuteNonQuery();
        tx.Commit();
        return next;
    }

    public IReadOnlyList<Continuation> Parked()
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM continuations WHERE status='Parked'";
        return Read<Continuation>(cmd);
    }

    public ThreadNote? GetThreadNote(string thread) => Scalar<ThreadNote>("SELECT json FROM thread_notes WHERE thread=$i", thread);

    public void SetThreadNote(ThreadNote note)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO thread_notes(thread,json) VALUES($t,$j) ON CONFLICT(thread) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$t", note.Thread);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(note, Json));
        cmd.ExecuteNonQuery();
    }

    private T? Scalar<T>(string sql, string id) where T : class
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Json) : null;
    }

    private static List<T> Read<T>(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var rows = new List<T>();
        while (r.Read()) rows.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Json)!);
        return rows;
    }
}

/// <summary>Access snapshots (the API keys last seen) in the database's settings table.</summary>
public sealed class SqliteAccessSnapshotStore(SqliteActionStore store) : IAccessSnapshotStore
{
    public string? Get(string key)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
