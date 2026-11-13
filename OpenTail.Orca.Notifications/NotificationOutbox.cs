using Microsoft.Data.Sqlite;

namespace OpenTail.Orca.Notifications;

public sealed record OutboxItem(long Id, string DedupeKey, string Channel, string PayloadJson, int Attempts);

public sealed record OutboxStats(int Pending, int Sent, int Dead);

/// <summary>
/// Notifications waiting to be delivered, in the same SQLite file as the action queue. A row is written first and delivered afterwards, so
/// an outage on the other side (or a restart here) loses nothing; delivery is at-least-once and receivers dedupe on the event id.
/// </summary>
public sealed class NotificationOutbox
{
    public const int MaxAttempts = 6;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromHours(1);

    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string _synchronousPragma;

    /// <param name="synchronous"><c>normal</c>, <c>full</c> or <c>extra</c>; null reads <c>OPENTAIL_DB_SYNCHRONOUS</c>, as the Host's
    /// action store does for the same file (see <c>SqliteDurability</c> in OpenTail.Orca).</param>
    public NotificationOutbox(string dbPath, TimeProvider? clock = null, string? synchronous = null)
    {
        _clock = clock ?? TimeProvider.System;
        _synchronousPragma = SynchronousPragma(synchronous ?? Environment.GetEnvironmentVariable("OPENTAIL_DB_SYNCHRONOUS"));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        using var c = Open();
        using (var wal = c.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; wal.ExecuteNonQuery(); }
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS notification_outbox (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                dedupe_key      TEXT NOT NULL UNIQUE,
                channel         TEXT NOT NULL,
                payload_json    TEXT NOT NULL,
                status          TEXT NOT NULL,
                attempts        INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NOT NULL,
                lease_until     TEXT NULL,
                last_error      TEXT NULL,
                created_at      TEXT NOT NULL,
                sent_at         TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_outbox_due ON notification_outbox(status, next_attempt_at);
            CREATE TABLE IF NOT EXISTS notification_state (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // Same values and rule as the Host's SqliteDurability; this package does not reference OpenTail.Orca.
    private static string SynchronousPragma(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "normal" => "PRAGMA synchronous=NORMAL;",
        "full" => "PRAGMA synchronous=FULL;",
        "extra" => "PRAGMA synchronous=EXTRA;",
        _ => throw new InvalidOperationException($"OPENTAIL_DB_SYNCHRONOUS is '{value}'; use normal, full or extra."),
    };

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000; " + _synchronousPragma;
        cmd.ExecuteNonQuery();
        return c;
    }

    private string Now() => _clock.GetUtcNow().ToString("O");

    /// <summary>A small value the notifier keeps between runs (how far the audit stream got).</summary>
    public string? GetState(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM notification_state WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetState(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO notification_state(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Adds a notification. Returns false when one with this key already exists (so enqueueing is idempotent).</summary>
    public bool Enqueue(string dedupeKey, string channel, string payloadJson)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO notification_outbox(dedupe_key, channel, payload_json, status, next_attempt_at, created_at)
            VALUES($k,$ch,$p,'pending',$n,$n)
            """;
        cmd.Parameters.AddWithValue("$k", dedupeKey);
        cmd.Parameters.AddWithValue("$ch", channel);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.Parameters.AddWithValue("$n", Now());
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Exists(string dedupeKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM notification_outbox WHERE dedupe_key=$k";
        cmd.Parameters.AddWithValue("$k", dedupeKey);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Takes due items under a lease so two dispatchers (or two processes) never send the same one at once.</summary>
    public IReadOnlyList<OutboxItem> ClaimDue(int max, TimeSpan lease)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        var now = Now();
        var items = new List<OutboxItem>();
        using (var select = c.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT id, dedupe_key, channel, payload_json, attempts FROM notification_outbox
                WHERE status='pending' AND next_attempt_at<=$n AND (lease_until IS NULL OR lease_until<$n)
                ORDER BY id LIMIT $max
                """;
            select.Parameters.AddWithValue("$n", now);
            select.Parameters.AddWithValue("$max", max);
            using var r = select.ExecuteReader();
            while (r.Read()) items.Add(new OutboxItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4)));
        }
        foreach (var item in items)
        {
            using var upd = c.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE notification_outbox SET lease_until=$l WHERE id=$id";
            upd.Parameters.AddWithValue("$l", (_clock.GetUtcNow() + lease).ToString("O"));
            upd.Parameters.AddWithValue("$id", item.Id);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        return items;
    }

    /// <summary>Marks an item sent by its key without delivering it (a marker, such as the hour an audit checkpoint was queued).</summary>
    public void MarkSentByKey(string dedupeKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE notification_outbox SET status='sent', sent_at=$n, lease_until=NULL WHERE dedupe_key=$k";
        cmd.Parameters.AddWithValue("$n", Now());
        cmd.Parameters.AddWithValue("$k", dedupeKey);
        cmd.ExecuteNonQuery();
    }

    public void MarkSent(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE notification_outbox SET status='sent', sent_at=$n, lease_until=NULL, last_error=NULL WHERE id=$id";
        cmd.Parameters.AddWithValue("$n", Now());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records a failed attempt. Returns true when this was the last one and the item is now dead.</summary>
    public bool MarkFailed(long id, int attemptsSoFar, string error)
    {
        var attempts = attemptsSoFar + 1;
        var dead = attempts >= MaxAttempts;
        var delay = TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Min(attempts - 1, 20)));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE notification_outbox SET status=$s, attempts=$a, next_attempt_at=$next, lease_until=NULL, last_error=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", dead ? "dead" : "pending");
        cmd.Parameters.AddWithValue("$a", attempts);
        cmd.Parameters.AddWithValue("$next", (_clock.GetUtcNow() + delay).ToString("O"));
        cmd.Parameters.AddWithValue("$e", error.Length > 200 ? error[..200] : error);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return dead;
    }

    public OutboxStats Stats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM notification_outbox GROUP BY status";
        int pending = 0, sent = 0, dead = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var n = r.GetInt32(1);
            switch (r.GetString(0)) { case "pending": pending = n; break; case "sent": sent = n; break; case "dead": dead = n; break; }
        }
        return new OutboxStats(pending, sent, dead);
    }

    /// <summary>Last error text per dead item, for operators.</summary>
    public IReadOnlyList<(string DedupeKey, string Channel, string? Error)> Dead()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT dedupe_key, channel, last_error FROM notification_outbox WHERE status='dead' ORDER BY id";
        var rows = new List<(string, string, string?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return rows;
    }

    /// <summary>How many notifications created since <paramref name="since"/> ended dead (gave up after retries). For the health check.</summary>
    public int DeadSince(DateTimeOffset since)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM notification_outbox WHERE status='dead' AND created_at>=$s";
        cmd.Parameters.AddWithValue("$s", since.ToUniversalTime().ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Deletes delivered and dead items older than <paramref name="before"/>.</summary>
    public int Purge(DateTimeOffset before)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM notification_outbox WHERE status IN ('sent','dead') AND created_at<$b";
        cmd.Parameters.AddWithValue("$b", before.ToUniversalTime().ToString("O"));
        return cmd.ExecuteNonQuery();
    }
}
