using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace OpenTail.Orca;

public sealed record NoticeRow(string ActionId, string Requester, string? SessionId, DateTimeOffset CreatedAt);

/// <summary>Decisions the caller has not been told about yet. SQLite in a real deployment, so the news survives a restart.</summary>
public interface INoticeStore
{
    /// <summary>Adds a notice; false if this action already has one.</summary>
    bool Add(NoticeRow row);

    /// <summary>The caller's undelivered notices, the given session's first, then oldest first.</summary>
    IReadOnlyList<NoticeRow> Undelivered(string requester, string? sessionId, int max);

    int CountUndelivered(string requester);

    /// <summary>Compare-and-set: true only for the one caller that actually took it.</summary>
    bool MarkDelivered(string actionId, DateTimeOffset at);

    int Purge(DateTimeOffset createdBefore);
}

public sealed class InMemoryNoticeStore : INoticeStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (NoticeRow Row, bool Delivered)> _rows = new(StringComparer.Ordinal);

    public bool Add(NoticeRow row)
    {
        lock (_gate) return _rows.TryAdd(row.ActionId, (row, false));
    }

    public IReadOnlyList<NoticeRow> Undelivered(string requester, string? sessionId, int max)
    {
        lock (_gate)
            return _rows.Values.Where(v => !v.Delivered && v.Row.Requester == requester)
                .OrderByDescending(v => sessionId is not null && v.Row.SessionId == sessionId).ThenBy(v => v.Row.CreatedAt)
                .Take(max).Select(v => v.Row).ToList();
    }

    public int CountUndelivered(string requester)
    {
        lock (_gate) return _rows.Values.Count(v => !v.Delivered && v.Row.Requester == requester);
    }

    public bool MarkDelivered(string actionId, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(actionId, out var v) || v.Delivered) return false;
            _rows[actionId] = (v.Row, true);
            return true;
        }
    }

    public int Purge(DateTimeOffset createdBefore)
    {
        lock (_gate)
        {
            var old = _rows.Where(kv => kv.Value.Row.CreatedAt < createdBefore).Select(kv => kv.Key).ToList();
            foreach (var k in old) _rows.Remove(k);
            return old.Count;
        }
    }
}

public sealed class SqliteNoticeStore(SqliteActionStore store) : INoticeStore
{
    private static string T(DateTimeOffset d) => d.ToUniversalTime().ToString("O");

    public bool Add(NoticeRow row)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO notices(action_id, requester, session_id, created_at) VALUES($a,$r,$s,$c)";
        cmd.Parameters.AddWithValue("$a", row.ActionId);
        cmd.Parameters.AddWithValue("$r", row.Requester);
        cmd.Parameters.AddWithValue("$s", (object?)row.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", T(row.CreatedAt));
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<NoticeRow> Undelivered(string requester, string? sessionId, int max)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT action_id, requester, session_id, created_at FROM notices
            WHERE requester=$r AND delivered_at IS NULL
            ORDER BY (CASE WHEN $s IS NOT NULL AND session_id=$s THEN 0 ELSE 1 END), created_at, action_id
            LIMIT $max
            """;
        cmd.Parameters.AddWithValue("$r", requester);
        cmd.Parameters.AddWithValue("$s", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$max", max);
        var rows = new List<NoticeRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new NoticeRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3))));
        return rows;
    }

    public int CountUndelivered(string requester)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM notices WHERE requester=$r AND delivered_at IS NULL";
        cmd.Parameters.AddWithValue("$r", requester);
        return (int)(long)(cmd.ExecuteScalar() ?? 0L);
    }

    public bool MarkDelivered(string actionId, DateTimeOffset at)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE notices SET delivered_at=$d WHERE action_id=$a AND delivered_at IS NULL";
        cmd.Parameters.AddWithValue("$d", T(at));
        cmd.Parameters.AddWithValue("$a", actionId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int Purge(DateTimeOffset createdBefore)
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM notices WHERE created_at<$b";
        cmd.Parameters.AddWithValue("$b", T(createdBefore));
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// The AI's turn can end before a reviewer decides, and the protocol cannot push to a closed chat. So decisions are remembered per
/// caller and handed over, a few at a time, on the caller's next tool call of any kind (and on <c>get_updates</c>). A notice names the
/// action, what became of it and, for a rejection or change request, the reviewer's reason as quoted text. It never carries a result:
/// reading a result goes through <c>get_action_status</c>, which also handles taint.
/// </summary>
public sealed class NoticeService(INoticeStore notices, IActionStore store, TimeProvider? clock = null)
{
    public const int MaxPerResponse = 5;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Remember a decision for later delivery: only for actions that waited in the queue (the caller already saw the outcome of
    /// anything that ran at once), and not for the requester's own cancellation.</summary>
    internal void OnEvent(ActionEventKind kind, ActionRecord r)
    {
        if (r.PolicyMode != ToolMode.Queue && r.ParentActionId is null && !r.Reviews.Any()) return;
        if (kind is not (ActionEventKind.Succeeded or ActionEventKind.Failed or ActionEventKind.Rejected or ActionEventKind.ChangesRequested or ActionEventKind.Expired))
            return;
        if (r.PolicyMode != ToolMode.Queue) return;
        notices.Add(new NoticeRow(r.Id, r.Requester.Id, r.SessionId, _clock.GetUtcNow()));
    }

    /// <summary>The caller has now seen where this action ended up (through a status call), so a notice would be a repeat.</summary>
    public void MarkSeen(ActionRecord r)
    {
        if (ActionStateMachine.IsTerminal(r.Status, r.Error?.Retryable ?? false) || r.Status == ActionStatus.Failed)
            notices.MarkDelivered(r.Id, _clock.GetUtcNow());
    }

    public int Pending(string requester) => notices.CountUndelivered(requester);

    /// <summary>Takes up to <see cref="MaxPerResponse"/> notices for this caller. Each is handed to exactly one response.</summary>
    public async Task<IReadOnlyList<JsonObject>> TakeAsync(string requester, string? sessionId, int max = MaxPerResponse, CancellationToken ct = default)
    {
        var result = new List<JsonObject>();
        foreach (var row in notices.Undelivered(requester, sessionId, max))
        {
            var r = await store.GetAsync(row.ActionId, ct);
            if (r is null) { notices.MarkDelivered(row.ActionId, _clock.GetUtcNow()); continue; }
            if (!notices.MarkDelivered(row.ActionId, _clock.GetUtcNow())) continue; // another response took it
            result.Add(Describe(r));
        }
        return result;
    }

    private static JsonObject Describe(ActionRecord r)
    {
        var o = new JsonObject
        {
            ["actionId"] = r.Id,
            ["tool"] = r.ToolName,
            ["status"] = ActionViews.StatusName(r.Status),
            ["at"] = r.UpdatedAt,
        };
        var review = r.Reviews.LastOrDefault(x => x.Reason is not null);
        switch (r.Status)
        {
            case ActionStatus.Succeeded:
                o["next"] = "It ran. Call get_action_status with this actionId to read the result.";
                break;
            case ActionStatus.Rejected:
                if (review?.Reason is { } why) o["reviewerReason"] = why;
                o["next"] = "A reviewer rejected it. Do not submit the same action again; tell the user.";
                break;
            case ActionStatus.ChangesRequested:
                if (review?.Reason is { } ask) o["reviewerReason"] = ask;
                o["next"] = "A reviewer asked for changes. Fix what the reason says and call resubmit_action.";
                break;
            case ActionStatus.Failed:
                o["error"] = new JsonObject { ["code"] = r.Error?.Code, ["message"] = r.Error?.Message, ["retryable"] = r.Error?.Retryable ?? false,
                    ["kind"] = r.Error is { } fe ? FailureKinds.Name(fe.EffectiveKind) : null };
                o["next"] = "It was approved but failed. Tell the user; a reviewer can retry it.";
                break;
            case ActionStatus.Expired:
                o["next"] = "Nobody decided before it expired. Submit it again if it is still needed.";
                break;
        }
        return o;
    }

    /// <summary>Adds <c>notices</c> to a JSON response, only when there is something to say.</summary>
    public async Task<string> AttachAsync(string responseJson, string requester, string? sessionId, CancellationToken ct = default)
    {
        if (notices.CountUndelivered(requester) == 0) return responseJson;
        var taken = await TakeAsync(requester, sessionId, MaxPerResponse, ct);
        if (taken.Count == 0) return responseJson;
        if (JsonNode.Parse(responseJson) is not JsonObject o) return responseJson;
        o["notices"] = new JsonArray(taken.Select(n => (JsonNode)n).ToArray());
        return o.ToJsonString();
    }
}
