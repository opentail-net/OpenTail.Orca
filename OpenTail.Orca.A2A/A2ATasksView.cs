using A2A;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.A2A;

/// <summary>
/// The operator's <c>tasks</c> chat command (batch 05, B2): the A2A tasks in flight, newest first, with the state a partner would see, the
/// age, and what each one waits for. It reads statuses, times and links only. It never reads a goal, a summary or an answer, because a chat
/// is an exit: the text stays in the store and on the review page.
/// </summary>
internal static class A2ATasksView
{
    /// <summary>The exact word a reviewer types.</summary>
    public const string Command = "tasks";

    /// <summary>How many tasks the command shows.</summary>
    public const int Shown = 10;

    public static async Task<IReadOnlyList<string>> LinesAsync(OpenTailHost host, A2aOptions config, A2ATaskTable tasks, DateTimeOffset now, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var row in tasks.ListRecent(200))
        {
            if (lines.Count == Shown) break;
            var submit = row.SubmitActionId is { } s ? await host.Store.GetAsync(s, ct) : null;
            var item = row.WorkItemId is { } w ? host.Work.Store.Get(w) : null;
            var reply = row.ReplyActionId is { } r ? await host.Store.GetAsync(r, ct) : null;
            var (state, _, _) = A2ARequestHandler.StateOf(submit, item, reply, A2AWork.SubjectOf(row.PartnerId));
            if (state is not (TaskState.Submitted or TaskState.Working)) continue;

            var partner = config.Partners.FirstOrDefault(p => string.Equals(p.Id, row.PartnerId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                          ?? "an unlisted partner";
            var word = state == TaskState.Submitted ? "submitted" : "working";
            lines.Add($"{Short(row.TaskId)} · {partner} · {word} · {Age(now - row.CreatedAt)} · waits for {WaitsFor(submit, item, reply)}");
        }
        return lines.Count == 0 ? ["No A2A task is in flight."] : lines;
    }

    /// <summary>What the task waits for, in the words a reviewer needs. A reply comes before the item: a reply that is queued is what a person decides.</summary>
    internal static string WaitsFor(ActionRecord? submit, WorkItem? item, ActionRecord? reply) =>
        reply is { Status: ActionStatus.Pending or ActionStatus.Approved or ActionStatus.Executing } ? "a reviewer: reply"
        : item is { Status: WorkStatus.Open or WorkStatus.Claimed } ? "an agent"
        : item is { Status: WorkStatus.Done } ? "the reply to be queued"
        : submit is { Status: ActionStatus.Pending } ? "a reviewer: request"
        : "the Host";

    internal static string Short(string id) => id.Length <= 8 ? id : id[..8];

    internal static string Age(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "under a minute"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min"
        : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h"
        : $"{(int)age.TotalDays} d";
}
