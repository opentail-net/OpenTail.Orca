using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.A2A;

/// <summary>
/// What the two A2A tools do when they run, and the reconcile step that turns a finished work item into a queued reply (batch 03b-2:
/// E3 to E5). The bodies run after the action was approved (or the policy let it run). Each checks that the requester is the partner the
/// arguments name and that the task row links to this action; a failed check throws the same generic refusal every time.
/// </summary>
internal sealed class A2AWork(OpenTailHost host, A2aOptions config, A2ATaskTable tasks)
{
    /// <summary>The only text a refused tool call says. It names no row, id or partner.</summary>
    internal const string Refusal = "This request cannot be accepted.";

    /// <summary>The subject a partner's requests are recorded under. The handler uses the same form.</summary>
    public static string SubjectOf(string partnerId) => "partner:" + partnerId;

    /// <summary>The thread every work item of one A2A task is on.</summary>
    public static string ThreadOf(string contextId) => "a2a:" + contextId;

    private static object Accepted => new { accepted = true };

    /// <summary>The body of <c>a2a.submit</c> (E3). The work item is created on approval, once, for the task's context.</summary>
    public async Task<object> SubmitAsync(string partner, string taskId, string skill, string goal, string inputs, AIFunctionArguments? arguments)
    {
        var (row, actionId) = Authorise(partner, taskId, arguments, reply: false);
        if (row.SkillId != skill || !config.Skills.TryGetValue(skill, out var definition)) throw new InvalidOperationException(Refusal);
        if (row.WorkItemId is not null) return Accepted;

        var subject = SubjectOf(partner);
        var thread = ThreadOf(row.ContextId);
        // A crash between the create and the link leaves the item without its id in the row: the thread finds it again, so there is one.
        var item = host.Work.Store.Query(thread: thread).FirstOrDefault(i => i.From == subject);
        if (item is null)
        {
            var session = (await host.Store.GetAsync(actionId, CancellationToken.None))?.SessionId;
            var carried = host.Work.Carry(subject, session).Labels;
            item = await host.Work.CreateAsync(goal, WorkService.ForProfile(definition.Inbox), subject,
                inputs.Length == 0 ? null : inputs, tainted: true, carried: carried, thread: thread);
        }
        tasks.SetWorkItem(taskId, item.Id, DateTimeOffset.UtcNow);
        return Accepted;
    }

    /// <summary>The body of <c>a2a.reply</c> (E4). Nothing leaves from here: the partner reads the text from this action's arguments once the
    /// action has succeeded (see the handler), and the text is stored nowhere else.</summary>
    public object Reply(string partner, string taskId, AIFunctionArguments? arguments)
    {
        Authorise(partner, taskId, arguments, reply: true);
        return new { delivered = true };
    }

    /// <summary>
    /// E5. When the row's work item is Done and no reply is linked, queues <c>a2a.reply</c> for the item's summary (or "Done." when it has
    /// none), as the partner, in the submit's session and profile. The item's taint and labels are put on that session first, so the exit
    /// check sees them. Two runs at once make one reply: the idempotency key makes the submits one action, and the link is set only if none
    /// is set. Returns true when this run set the link.
    /// </summary>
    public async Task<bool> ReconcileAsync(A2ATaskTable.Row row, CancellationToken ct)
    {
        if (row.WorkItemId is not { } workId || row.ReplyActionId is not null) return false;
        if (host.Work.Store.Get(workId) is not { Status: WorkStatus.Done } item) return false;

        var subject = SubjectOf(row.PartnerId);
        var submit = row.SubmitActionId is { } submitId ? await host.Store.GetAsync(submitId, ct) : null;
        var session = submit?.SessionId is { } sid && host.Sessions.Exists(sid, subject) ? sid : host.Sessions.Start(subject);

        // Step 2: the reply's exit check reads the labels the session holds, so the item's taint and labels go onto it first.
        if (item.Tainted) host.Sessions.Taint(session);
        if (item.Labels.Count > 0) host.Labels.GainCarried(session, subject, item.Labels);

        var text = string.IsNullOrWhiteSpace(item.Summary) ? "Done." : item.Summary;
        var args = new JsonObject { ["partner"] = row.PartnerId, ["taskId"] = row.TaskId, ["text"] = text };
        var reply = await host.Queue.SubmitAsync(
            new ActionRequest(A2ASubmitModule.ReplyTool, args.ToJsonString(), new ActionRequester(RequesterKind.AiSession, subject), session,
                row.TaskId + ":reply")
            {
                Profile = submit?.Profile,
            }, ct);

        var now = DateTimeOffset.UtcNow;
        if (!tasks.SetReplyAction(row.TaskId, reply.Id, now)) return false;
        var bytes = Encoding.UTF8.GetByteCount(text);
        await host.Audit.AppendAsync(new AuditEntry(now, "a2a.reply_queued", reply.Id, A2ASubmitModule.ReplyTool, subject,
            $"task={row.TaskId} partner={row.PartnerId} hash={Sha256(text)} bytes={bytes}"), ct);
        return true;
    }

    /// <summary>Reconciles and never throws. A failure is audited with its exception type only, and the next call tries again; the task
    /// reads as WORKING meanwhile.</summary>
    public async Task TryReconcileAsync(A2ATaskTable.Row row, CancellationToken ct)
    {
        try
        {
            await ReconcileAsync(row, ct);
        }
        catch (Exception ex)
        {
            try
            {
                await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "a2a.reconcile_failed", null, A2ASubmitModule.ReplyTool,
                    SubjectOf(row.PartnerId), $"task={row.TaskId} error={ex.GetType().Name}"), CancellationToken.None);
            }
            catch (Exception)
            {
                // The audit trail is down too; the task still reads WORKING and the next call tries again.
            }
        }
    }

    /// <summary>The <c>Completed</c> event (E2). It finds the task by the item's thread and partner and reconciles it, on its own task, so the
    /// completer's call is not held up. Never throws.</summary>
    public void OnCompleted(WorkItem item)
    {
        if (!item.From.StartsWith("partner:", StringComparison.Ordinal) || !item.Thread.StartsWith("a2a:", StringComparison.Ordinal)) return;
        var partner = item.From["partner:".Length..];
        var context = item.Thread["a2a:".Length..];
        _ = Task.Run(async () =>
        {
            try
            {
                if (tasks.FindByContext(partner, context) is { } row) await TryReconcileAsync(row, CancellationToken.None);
            }
            catch (Exception)
            {
                // Nothing to report from here: GetTask reconciles again when it is asked.
            }
        });
    }

    /// <summary>Checks the requester and the row. Each tool body passes its own link check: submit (the row has no action or this one) and
    /// reply (the row has no reply or this one).</summary>
    private (A2ATaskTable.Row Row, string ActionId) Authorise(string partner, string taskId, AIFunctionArguments? arguments, bool reply)
    {
        var context = arguments?.Context;
        var requester = ContextString(context, HostContract.RequesterContextKey);
        var actionId = ContextString(context, HostContract.ActionIdContextKey);
        if (requester is null || actionId is null || requester != SubjectOf(partner)) throw new InvalidOperationException(Refusal);
        var row = tasks.FindByTask(partner, taskId) ?? throw new InvalidOperationException(Refusal);
        var linked = reply ? row.ReplyActionId : row.SubmitActionId;
        if (linked is not null && linked != actionId) throw new InvalidOperationException(Refusal);
        return (row, actionId);
    }

    private static string? ContextString(IDictionary<object, object?>? context, string key) =>
        context is not null && context.ContainsKey(key) ? context[key] as string : null;

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
