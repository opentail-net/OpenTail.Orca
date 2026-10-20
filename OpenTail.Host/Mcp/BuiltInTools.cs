using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace OpenTail.Host;

/// <summary>Tools every Host exposes: sessions, the AI's view of its own queue, and the news about decisions made while it was away.</summary>
internal static class BuiltInTools
{
    public static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "start_session", "get_action_status", "list_my_actions", "cancel_action", "resubmit_action", "get_updates", "wait_for_action",
        "park", "next_work", "get_work", "complete_work", "fail_work", "hand_back_work", "update_thread_note",
    };

    public const int MaxWaitSeconds = 25;

    /// <summary>The one path by which a caller reads one of its own actions (get_action_status and the Tasks extension's tasks/get):
    /// someone else's id and an unknown id both give null; reading the result of a tool that reads outside content taints the
    /// session (or the caller); the decision's notice counts as seen.</summary>
    public static async Task<ActionRecord?> ReadOwnAsync(string actionId, string subject, string? sessionId, SessionRegistry sessions,
        IActionStore store, ToolCatalog catalog, NoticeService notices, CancellationToken ct = default)
    {
        var r = await store.GetAsync(actionId, ct);
        if (r is null || r.Requester.Id != subject) return null;
        if (r.Status == ActionStatus.Succeeded && r.ResultJson is not null && catalog.TryGet(r.ToolName, out var reg) && reg.Descriptor.ReadsUntrusted)
        {
            if (sessionId is not null && sessions.Exists(sessionId, subject)) sessions.Taint(sessionId);
            else sessions.TaintSubject(subject);
        }
        notices.MarkSeen(r);
        return r;
    }

    public static IEnumerable<McpServerTool> Create(SessionRegistry sessions, IActionStore store, ActionQueue queue, ToolCatalog catalog, NoticeService notices,
        WorkService? work = null)
    {
        // The caller is read here, once per call, at the edge. Everything below keys off this subject, so a caller can only
        // ever see and act on its own actions (an unknown id and someone else's id look the same).
        static ActionRequester Who() => new(RequesterKind.AiSession, CallerContext.Current.Subject);

        // Reading the RESULT of an untrusted-content tool through a status call is a read of untrusted content too.
        void TaintIfUntrustedResult(ActionRecord r, string? sessionId)
        {
            if (r.Status != ActionStatus.Succeeded || r.ResultJson is null) return;
            if (!catalog.TryGet(r.ToolName, out var reg) || !reg.Descriptor.ReadsUntrusted) return;
            if (sessionId is not null && sessions.Exists(sessionId, Who().Id)) sessions.Taint(sessionId);
            else sessions.TaintSubject(Who().Id);
        }

        // Every response carries any decisions the caller has not been told about yet (only when there are some).
        async Task<JsonElement> Out(string json, string? sessionId = null)
        {
            var s = sessionId is not null && sessions.Exists(sessionId, Who().Id) ? sessionId : null;
            return J(await notices.AttachAsync(json, Who().Id, s));
        }

        yield return Tool(
            "start_session",
            "Start an optional session. Pass the returned sessionId to later calls so the Host can track this conversation.",
            async () => await Out(new JsonObject { ["v"] = ActionViews.Version, ["sessionId"] = sessions.Start(Who().Id) }.ToJsonString()),
            readOnly: false);

        yield return Tool(
            "get_action_status",
            "Get the current status of one of your actions: pending_approval, succeeded, failed, rejected, changes_requested and so on, with reviewer feedback.",
            async (string actionId, string? sessionId = null) =>
            {
                var r = await ReadOwnAsync(actionId, Who().Id, sessionId, sessions, store, catalog, notices);
                if (r is null)
                    return await Out(new JsonObject { ["v"] = ActionViews.Version, ["status"] = "not_found", ["actionId"] = actionId }.ToJsonString(), sessionId);
                return await Out(ActionViews.DetailJson(r), sessionId);
            });

        yield return Tool(
            "wait_for_action",
            $"Wait up to {MaxWaitSeconds} seconds for one of your actions to leave the approval queue, then return its status (same as get_action_status). " +
            "If it is still pending, say so to the user and carry on with other work instead of waiting again in a loop.",
            async (string actionId, int? timeoutSeconds = null, string? sessionId = null, CancellationToken ct = default) =>
            {
                var limit = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 20, 1, MaxWaitSeconds));
                var watch = Stopwatch.StartNew();
                ActionRecord? r;
                while (true)
                {
                    r = await store.GetAsync(actionId, ct);
                    if (r is null || r.Requester.Id != Who().Id)
                        return await Out(new JsonObject { ["v"] = ActionViews.Version, ["status"] = "not_found", ["actionId"] = actionId }.ToJsonString(), sessionId);
                    if (r.Status is not (ActionStatus.Pending or ActionStatus.Approved or ActionStatus.Executing) || watch.Elapsed >= limit) break;
                    try { await Task.Delay(250, ct); } catch (OperationCanceledException) { break; }
                }
                TaintIfUntrustedResult(r, sessionId);
                notices.MarkSeen(r);
                var o = ActionViews.Detail(r);
                o["waitedSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1);
                if (r.Status == ActionStatus.Pending) o["stillWaiting"] = true;
                return await Out(o.ToJsonString(), sessionId);
            });

        yield return Tool(
            "get_updates",
            "Call this at the start of a conversation, and whenever the user asks what happened to earlier requests: it returns decisions made " +
            "on your earlier actions since you last looked, and lists what is still waiting for approval.",
            async (string? sessionId = null) =>
            {
                var s = sessionId is not null && sessions.Exists(sessionId, Who().Id) ? sessionId : null;
                var taken = await notices.TakeAsync(Who().Id, s, 20);
                var waiting = await store.QueryAsync(new ActionQuery { RequesterId = Who().Id, Status = ActionStatus.Pending, Limit = 50 });
                var o = new JsonObject
                {
                    ["v"] = ActionViews.Version,
                    ["notices"] = new JsonArray(taken.Select(n => (JsonNode)n).ToArray()),
                    ["stillWaiting"] = new JsonArray(waiting.Select(w => (JsonNode)new JsonObject
                    {
                        ["actionId"] = w.Id, ["tool"] = w.ToolName, ["since"] = w.CreatedAt, ["expiresAt"] = w.ExpiresAt,
                    }).ToArray()),
                };
                // Work waiting for this caller (resume items and tasks it may claim): ids only; next_work claims and reads one.
                if (work is not null)
                {
                    var caller = CallerContext.Current;
                    o["workReady"] = new JsonArray(work.Store.Query(WorkStatus.Open, limit: 500).Where(i => WorkService.MayClaim(i, caller)).Take(20)
                        .Select(i => (JsonNode)new JsonObject { ["workId"] = i.Id, ["kind"] = i.Kind.ToString().ToLowerInvariant(), ["due"] = i.Due }).ToArray());
                }
                return J(o.ToJsonString());
            });

        yield return Tool(
            "list_my_actions",
            "List your actions, newest first. Optionally filter by status, by sessionId, or to those updated since a time (ISO 8601).",
            async (string? sessionId = null, string? status = null, string? since = null, int? limit = null) =>
            {
                ActionStatus? filter = null;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    var wanted = status.Replace("_approval", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
                    if (!Enum.TryParse<ActionStatus>(wanted, ignoreCase: true, out var parsed))
                        return await Out(new JsonObject { ["v"] = ActionViews.Version, ["error"] = $"Unknown status '{status}'." }.ToJsonString(), sessionId);
                    filter = parsed;
                }
                DateTimeOffset? sinceAt = null;
                if (!string.IsNullOrWhiteSpace(since))
                {
                    if (!DateTimeOffset.TryParse(since, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
                        return await Out(new JsonObject { ["v"] = ActionViews.Version, ["error"] = $"'{since}' is not a time. Use ISO 8601, like 2026-10-15T09:00:00Z." }.ToJsonString(), sessionId);
                    sinceAt = t;
                }
                var rows = await store.QueryAsync(new ActionQuery
                {
                    RequesterId = Who().Id,
                    Status = filter,
                    SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                    UpdatedSince = sinceAt,
                    Limit = Math.Clamp(limit ?? 50, 1, 200),
                    NewestFirst = true,
                });
                var arr = new JsonArray();
                foreach (var r in rows)
                {
                    TaintIfUntrustedResult(r, sessionId);
                    notices.MarkSeen(r);
                    arr.Add(ActionViews.Detail(r));
                }
                return await Out(new JsonObject { ["v"] = ActionViews.Version, ["actions"] = arr }.ToJsonString(), sessionId);
            });

        yield return Tool(
            "cancel_action",
            "Cancel one of your actions while it is still pending approval.",
            async (string actionId, string? sessionId = null) =>
            {
                try { return await Out(ActionViews.DetailJson(await queue.CancelAsync(actionId, Who())), sessionId); }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
                { return await Out(ErrorJson(ex is KeyNotFoundException or UnauthorizedAccessException ? "No such action." : ex.Message), sessionId); }
            },
            readOnly: false);

        yield return Tool(
            "resubmit_action",
            "After a reviewer asked for changes, submit the action again. editedArgumentsJson is the corrected arguments as a JSON object string.",
            async (string actionId, string? editedArgumentsJson = null, string? sessionId = null) =>
            {
                try { return await Out(ActionViews.Response(await queue.ResubmitAsync(actionId, editedArgumentsJson, Who(), profile: CallerContext.Current.Profile)), sessionId); }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
                { return await Out(ErrorJson(ex is KeyNotFoundException or UnauthorizedAccessException ? "No such action." : ex.Message), sessionId); }
            },
            readOnly: false);
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string ErrorJson(string message) =>
        new JsonObject { ["v"] = ActionViews.Version, ["status"] = "error", ["error"] = new JsonObject { ["message"] = message } }.ToJsonString();

    /// <summary>The work tools (docs/design/work-and-continuations.md): the Host's ledger of work owed and work waited on. Offered when
    /// the policy's <c>Work.Enabled</c> is set.</summary>
    public static IEnumerable<McpServerTool> CreateWork(WorkService work, SessionRegistry sessions)
    {
        static CallerIdentity Caller() => CallerContext.Current;
        string? Own(string? sessionId) => sessionId is not null && sessions.Exists(sessionId, Caller().Subject) ? sessionId : null;
        static JsonElement Error(string message) => J(new JsonObject { ["v"] = ActionViews.Version, ["status"] = "error", ["error"] = message }.ToJsonString());
        static JsonElement View(WorkItem i, ThreadNote? note = null)
        {
            var o = WorkService.View(i, note);
            o["v"] = ActionViews.Version;
            return J(o.ToJsonString());
        }

        yield return Tool(
            "park",
            "Save your place before you stop, when you are waiting for something: your actions (act_...), work you delegated (wi_...) or a time " +
            "(timer:2026-10-25T09:00:00Z). When they come back (or 'until' passes) you get a resume item, through next_work, carrying your note and " +
            "what each wait came to. Write the note for yourself in a fresh conversation: what you are doing, why, and what to do next for each outcome.",
            async (string note, string[]? waitingOn = null, string wakeWhen = "all", string? until = null, string? fromWork = null, string? sessionId = null) =>
            {
                DateTimeOffset? untilAt = null;
                if (until is not null)
                {
                    if (!DateTimeOffset.TryParse(until, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var u)) return Error($"'{until}' is not a time (ISO 8601).");
                    untilAt = u;
                }
                if (wakeWhen is not ("all" or "any")) return Error("wakeWhen is 'all' or 'any'.");
                try
                {
                    var c = await work.ParkAsync(Caller(), Own(sessionId), note, waitingOn ?? [], wakeWhen == "any", untilAt, fromWork);
                    return J(new JsonObject { ["v"] = ActionViews.Version, ["continuationId"] = c.Id, ["until"] = c.Until }.ToJsonString());
                }
                catch (WorkException ex) { return Error(ex.Message); }
            },
            readOnly: false);

        yield return Tool(
            "next_work",
            $"Claim the next work item for you (a task, or a resume item from park) and read it. You hold it for a while (a lease); finish it with " +
            $"complete_work, fail_work or hand_back_work. Optionally wait up to {MaxWaitSeconds} seconds for one to arrive. Returns status 'none' when there is nothing.",
            async (string? sessionId = null, int waitSeconds = 0) =>
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
                while (true)
                {
                    if (await work.ClaimNextAsync(Caller(), Own(sessionId)) is { } item)
                        return View(item, work.Read(item.Id, Caller(), Own(sessionId), out var note) is null ? null : note);
                    if (DateTimeOffset.UtcNow >= deadline)
                        return J(new JsonObject { ["v"] = ActionViews.Version, ["status"] = "none" }.ToJsonString());
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            },
            readOnly: false);

        yield return Tool(
            "get_work",
            "Read one work item you hold, created or may claim: its goal, inputs, note, outcomes and the thread note.",
            (string workId, string? sessionId = null) =>
                work.Read(workId, Caller(), Own(sessionId), out var note) is { } item ? View(item, note) : Error("No such work item."));

        yield return Tool(
            "complete_work",
            "Finish a work item you hold. The summary goes to whoever is waiting on it.",
            async (string workId, string? summary = null) =>
            {
                try { return View(await work.CompleteAsync(workId, Caller(), summary)); }
                catch (WorkException ex) { return Error(ex.Message); }
            },
            readOnly: false);

        yield return Tool(
            "fail_work",
            "Give up on a work item you hold, with the reason. It is tried again by a fresh run until it has failed too often; then a person looks at it.",
            async (string workId, string reason) =>
            {
                try { return View(await work.FailAsync(workId, Caller(), reason)); }
                catch (WorkException ex) { return Error(ex.Message); }
            },
            readOnly: false);

        yield return Tool(
            "hand_back_work",
            "Return a work item you hold that is not yours to do, with the reason. It goes back to the inbox for another agent.",
            async (string workId, string reason) =>
            {
                try { return View(await work.HandBackAsync(workId, Caller(), reason)); }
                catch (WorkException ex) { return Error(ex.Message); }
            },
            readOnly: false);

        yield return Tool(
            "update_thread_note",
            "Rewrite the thread note of a work item you hold: what you know, what you tried, what is next. The next run on this thread reads it.",
            (string workId, string note, string? sessionId = null) =>
            {
                try
                {
                    var n = work.UpdateThreadNote(workId, Caller(), Own(sessionId), note);
                    return J(new JsonObject { ["v"] = ActionViews.Version, ["thread"] = n.Thread, ["at"] = n.At }.ToJsonString());
                }
                catch (WorkException ex) { return Error(ex.Message); }
            },
            readOnly: false);
    }

    private static McpServerTool Tool(string name, string description, Delegate fn, bool readOnly = true) =>
        McpServerTool.Create(
            AIFunctionFactory.Create(fn, name, description),
            new McpServerToolCreateOptions { ReadOnly = readOnly, Idempotent = readOnly, OpenWorld = false });
}
