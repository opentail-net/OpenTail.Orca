using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OpenTail.Host.A2A;

/// <summary>
/// The A2A task id table (schema 13 of the Host's database; docs/design/a2a.md section 10). One row per task a partner was given: the id
/// the partner sees, the partner that owns it, and the action that runs it. It keeps no message text, only a hash of the content.
/// It opens its own connection to the Host's database file, at the same durability level as the Host's own store.
/// </summary>
internal sealed class A2ATaskTable
{
    private readonly string _connectionString;
    private readonly string _synchronousPragma;

    public A2ATaskTable(string databasePath, SqliteSynchronous synchronous)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
        }.ToString();
        _synchronousPragma = SqliteDurability.Pragma(synchronous);
    }

    /// <summary>One task. <see cref="SubmitActionId"/> is null until the <c>a2a.submit</c> action has been recorded (a crash can leave it so).</summary>
    public sealed record Row(string TaskId, string PartnerId, string ContextId, string MessageId, string ContentHash, string SkillId,
        string? SubmitActionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    /// <summary>What <see cref="GetOrCreate"/> found.</summary>
    public enum Outcome
    {
        /// <summary>No row for this (partner, messageId): a new one was inserted.</summary>
        Created,

        /// <summary>A row with the same content: the task the partner already has.</summary>
        Existing,

        /// <summary>A row for this messageId with different content. Nothing was changed.</summary>
        ReusedWithOtherContent,
    }

    /// <summary>The row for a partner's messageId, creating it in the same transaction when there is none.</summary>
    public (Row Row, Outcome Outcome) GetOrCreate(string partnerId, string messageId, string contentHash, string skillId, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        if (Find(c, tx, "partner_id=$p AND message_id=$m", p => { p.AddWithValue("$p", partnerId); p.AddWithValue("$m", messageId); }) is { } existing)
            return (existing, existing.ContentHash == contentHash ? Outcome.Existing : Outcome.ReusedWithOtherContent);

        var row = new Row(Guid.NewGuid().ToString("D"), partnerId, Guid.NewGuid().ToString("D"), messageId, contentHash, skillId, null, now, now);
        Insert(c, tx, row);
        tx.Commit();
        return (row, Outcome.Created);
    }

    /// <summary>Inserts a row without an action. Used by the crash test to stand in for a crash between the two steps.</summary>
    internal Row InsertWithoutAction(string partnerId, string messageId, string contentHash, string skillId, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        var row = new Row(Guid.NewGuid().ToString("D"), partnerId, Guid.NewGuid().ToString("D"), messageId, contentHash, skillId, null, now, now);
        Insert(c, tx, row);
        tx.Commit();
        return row;
    }

    /// <summary>The task with this id that belongs to this partner. Foreign and unknown ids both give null.</summary>
    public Row? FindByTask(string partnerId, string taskId) =>
        Find("partner_id=$p AND task_id=$t", p => { p.AddWithValue("$p", partnerId); p.AddWithValue("$t", taskId); });

    /// <summary>A task of this partner in this context, if any.</summary>
    public Row? FindByContext(string partnerId, string contextId) =>
        Find("partner_id=$p AND context_id=$c", p => { p.AddWithValue("$p", partnerId); p.AddWithValue("$c", contextId); });

    public void SetSubmitAction(string taskId, string actionId, DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE a2a_tasks SET submit_action_id=$a, updated_at=$n WHERE task_id=$t";
        cmd.Parameters.AddWithValue("$a", actionId);
        cmd.Parameters.AddWithValue("$n", now.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The number of rows, for tests.</summary>
    internal int Count()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM a2a_tasks";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using (var busy = c.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=10000;";
            busy.ExecuteNonQuery();
        }
        using (var sync = c.CreateCommand())
        {
            sync.CommandText = _synchronousPragma;
            sync.ExecuteNonQuery();
        }
        return c;
    }

    private Row? Find(string where, Action<SqliteParameterCollection> bind)
    {
        using var c = Open();
        return Find(c, null, where, bind);
    }

    private static Row? Find(SqliteConnection c, SqliteTransaction? tx, string where, Action<SqliteParameterCollection> bind)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT task_id, partner_id, context_id, message_id, content_hash, skill_id, submit_action_id, created_at, updated_at " +
                          "FROM a2a_tasks WHERE " + where + " LIMIT 1";
        bind(cmd.Parameters);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Row(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static void Insert(SqliteConnection c, SqliteTransaction tx, Row row)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO a2a_tasks(task_id, partner_id, context_id, message_id, content_hash, skill_id, submit_action_id, created_at, updated_at)
            VALUES($t, $p, $c, $m, $h, $s, $a, $created, $updated)
            """;
        cmd.Parameters.AddWithValue("$t", row.TaskId);
        cmd.Parameters.AddWithValue("$p", row.PartnerId);
        cmd.Parameters.AddWithValue("$c", row.ContextId);
        cmd.Parameters.AddWithValue("$m", row.MessageId);
        cmd.Parameters.AddWithValue("$h", row.ContentHash);
        cmd.Parameters.AddWithValue("$s", row.SkillId);
        cmd.Parameters.AddWithValue("$a", (object?)row.SubmitActionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", row.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$updated", row.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
