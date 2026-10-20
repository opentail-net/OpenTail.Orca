using Microsoft.Data.Sqlite;

namespace OpenTail.Host.Http.Teams;

/// <summary>Where the bot can post (a conversation reference, learnt when the app is installed or someone writes to it) and which card
/// shows which action, in the Host's SQLite file so both survive a restart.</summary>
internal sealed class TeamsStore
{
    private readonly string _cs;

    public sealed record Conversation(string Key, string ServiceUrl, string ConversationId, string Kind);

    public TeamsStore(string dbPath)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS teams_conversations (key TEXT PRIMARY KEY, service_url TEXT NOT NULL, conversation_id TEXT NOT NULL,
                kind TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS teams_cards (action_id TEXT NOT NULL, conv_key TEXT NOT NULL, activity_id TEXT NOT NULL,
                PRIMARY KEY (action_id, conv_key));
            CREATE TABLE IF NOT EXISTS teams_digests (conv_key TEXT PRIMARY KEY, activity_id TEXT NOT NULL);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=10000;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar() as string;
    }

    public void Remember(Conversation conv)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO teams_conversations(key, service_url, conversation_id, kind, updated_at) VALUES($k,$s,$c,$t,$n)
            ON CONFLICT(key) DO UPDATE SET service_url=$s, conversation_id=$c, kind=$t, updated_at=$n
            """, ("$k", conv.Key), ("$s", conv.ServiceUrl), ("$c", conv.ConversationId), ("$t", conv.Kind), ("$n", DateTimeOffset.UtcNow.ToString("O")));
    }

    public Conversation? Find(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT service_url, conversation_id, kind FROM teams_conversations WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new Conversation(key, r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }

    public void Card(string actionId, string convKey, string activityId)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO teams_cards(action_id, conv_key, activity_id) VALUES($a,$k,$i)", ("$a", actionId), ("$k", convKey), ("$i", activityId));
    }

    public IReadOnlyList<(string ConvKey, string ActivityId)> CardsOf(string actionId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT conv_key, activity_id FROM teams_cards WHERE action_id=$a";
        cmd.Parameters.AddWithValue("$a", actionId);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public string? Digest(string convKey)
    {
        using var c = Open();
        return Scalar(c, "SELECT activity_id FROM teams_digests WHERE conv_key=$k", ("$k", convKey));
    }

    public void Digest(string convKey, string? activityId)
    {
        using var c = Open();
        if (activityId is null) Exec(c, "DELETE FROM teams_digests WHERE conv_key=$k", ("$k", convKey));
        else Exec(c, "INSERT OR REPLACE INTO teams_digests(conv_key, activity_id) VALUES($k,$i)", ("$k", convKey), ("$i", activityId));
    }
}
