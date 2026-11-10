using System.Globalization;
using System.Text.RegularExpressions;
using OpenTail.Host;

namespace OpenTail.Host.Admin;

/// <summary>
/// Operator commands that look at policy without running a server. Both runners call this first; if the command line asks for one
/// of these, it runs and the process exits, so none of them touch MCP or start anything.
/// <code>
///   --explain &lt;tool&gt; '&lt;json arguments&gt;' [--tainted] [--at 2026-10-15T09:00:00Z]    how the policy would treat this call, rule by rule
///             [--profile &lt;agent profile&gt;]                                          ... for a caller with this agent profile
///             [--labels finance,personal]                                        ... in a session that has read data with these labels
///   --replay-policy &lt;new.json&gt; [--since 7d]                                         what would have changed for the actions already stored
///   --list-modules                                                                    modules present and enabled, their tools and risks, policy warnings
///   --lint                                                                            only the policy warnings
///   --adopt-policy &lt;module id&gt; [--allow-execute] [--replace]                         write a module's suggested policy to policy.d/&lt;id&gt;.json
///   --test-policy &lt;cases.json&gt;                                                       check expected decisions (exit 1 on any mismatch)
///   --export-review-outcomes &lt;out.jsonl&gt; [--since 30d]                               rejected and sent-back actions, redacted, as scenario candidates
///   --skill-outcomes [--since 30d] [--min-cases 20] [--worse-by 0.15]                 decisions on actions after each skill version, and rollback suggestions
///   --export-shadow-cases &lt;out.jsonl&gt; [--since 7d]                                   real sessions for shadow runs (not redacted: stays in the deployment)
///   --usage [--since 7d]                                                              daily use per identity and tool: outcomes, records, bytes
///   --verify-audit [--checkpoint &lt;seq:hash&gt; ...]                                      walk the audit hash chain; exit 1 at the first break
///   --audit-checkpoint                                                                the chain's newest entry and hash, to keep somewhere else
///   --levels                                                                          identities that are Elevated or Suspended (or would be, report only)
///   --review-report [--since 7d]                                                      how people reviewed: approval rates, time to decide, quick and burst approvals
///   --grants | --revoke-grant &lt;id&gt; --by &lt;name&gt;                                       temporary access grants now in force; end one early
///   --clear-level &lt;subject&gt; --by &lt;name&gt; [--key-rotated]                             return an identity to Normal (a person's act, audited)
///   --policy-versions                                                                 every policy this database has run under, by hash
///   --policy-diff &lt;hash&gt; &lt;hash&gt;                                                       what changed between two stored policy versions
///   --export-audit &lt;folder&gt; [--since 90d | --from &lt;time&gt;] [--until &lt;time&gt;]                  a bundle an auditor can verify with plain Python
///   --circuits                                                                        paused routes (from the audit trail) and the calls they hold
///   --suspend &lt;subject&gt; --by &lt;name&gt; --reason &lt;text&gt;                                    suspend an identity by hand (an incident)
///   --cancel-pending &lt;subject&gt; --by &lt;name&gt; --reason &lt;text&gt;                             cancel all of an identity's pending actions
///   --record-restore --by &lt;name&gt; --note &lt;text&gt;                                         record a restore from backup in the audit trail
///   --access-report [--at &lt;time&gt;]                                                       keys, their profiles and what they allowed, grants and levels, as of a time
///   --work [--state open|claimed|done|cancelled|expired|dead] [--thread &lt;id&gt;]               work items, oldest first
///   --work-item &lt;id&gt;                                                                   one item: goal, note, outcomes, runs, thread note
///   --redrive &lt;id&gt; --by &lt;name&gt; [--for profile:x] [--exclude-failed] [--drop-note]          put a dead or expired item back, with the inspection's findings
///   --requeue &lt;id&gt; --by &lt;name&gt; | --cancel-work &lt;id&gt; --by &lt;name&gt;                         put an ended item back in the inbox; cancel one
///   (--export-review-outcomes also takes --approved-sample 0.1: a share of approved actions, redacted, for grading)
/// </code>
/// Every one of them accepts <c>--modules &lt;file&gt;</c> and builds the modules without needing their credentials.
/// </summary>
public static class PolicyCli
{
    /// <summary>Who exported what, from this machine's account (audit D5). Exports take data out of the Host, so they are audited.</summary>
    private static Task AuditExportAsync(string dbPath, string what) =>
        new SqliteAuditSink(new SqliteActionStore(dbPath)).AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "export", null, null,
            "os:" + Environment.UserName, what));

    /// <summary>Returns an exit code when the command line was a policy command; null otherwise.</summary>
    public static async Task<int?> TryRunAsync(string[] args, string name, Action<OpenTailHostBuilder> addModules, TextWriter output)
    {
        var explainAt = Array.IndexOf(args, "--explain");
        var replayPath = ValueOf(args, "--replay-policy");
        var listModules = args.Contains("--list-modules");
        var lint = args.Contains("--lint");
        var adoptId = ValueOf(args, "--adopt-policy");
        var testPath = ValueOf(args, "--test-policy");
        var exportPath = ValueOf(args, "--export-review-outcomes");
        var skillOutcomes = args.Contains("--skill-outcomes");
        var shadowPath = ValueOf(args, "--export-shadow-cases");
        var usage = args.Contains("--usage");
        var verifyAudit = args.Contains("--verify-audit");
        var auditCheckpoint = args.Contains("--audit-checkpoint");
        var listLevels = args.Contains("--levels");
        var clearLevel = ValueOf(args, "--clear-level");
        var reviewReport = args.Contains("--review-report");
        var listGrants = args.Contains("--grants");
        var revokeGrant = ValueOf(args, "--revoke-grant");
        var policyVersions = args.Contains("--policy-versions");
        var diffAt = Array.IndexOf(args, "--policy-diff");
        var listWork = args.Contains("--work");
        var accessReport = args.Contains("--access-report");
        var exportAudit = ValueOf(args, "--export-audit");
        var circuits = args.Contains("--circuits");
        var suspend = ValueOf(args, "--suspend");
        var cancelPending = ValueOf(args, "--cancel-pending");
        var recordRestore = args.Contains("--record-restore");
        var workItem = ValueOf(args, "--work-item");
        var requeue = ValueOf(args, "--requeue");
        var cancelWork = ValueOf(args, "--cancel-work");
        var redrive = ValueOf(args, "--redrive");
        if (explainAt < 0 && replayPath is null && !listModules && !lint && adoptId is null && testPath is null && exportPath is null && !skillOutcomes
            && shadowPath is null && !usage && !verifyAudit && !auditCheckpoint && !listLevels && clearLevel is null && !reviewReport
            && !listGrants && revokeGrant is null && !policyVersions && diffAt < 0
            && !listWork && workItem is null && requeue is null && cancelWork is null && !accessReport && exportAudit is null && redrive is null
            && !circuits && suspend is null && cancelPending is null && !recordRestore) return null;

        var policyPath = ValueOf(args, "--policy");
        try
        {
            if (listModules || lint || adoptId is not null || testPath is not null)
                return await RunModuleCommandAsync(args, name, addModules, output, policyPath, listModules, adoptId, testPath);

            if (explainAt >= 0)
            {
                if (policyPath is null || explainAt + 2 >= args.Length)
                {
                    await Console.Error.WriteLineAsync($"Usage: {name} --policy <file> --explain <tool> '<json arguments>' [--tainted] [--at <time>] [--profile <name>]");
                    return 64;
                }
                var builder = new OpenTailHostBuilder().WithName(name).UsePolicy(PolicyConfig.Load(policyPath));
                await ModulesCli.ApplyAsync(builder, args, ModuleLoadMode.Describe);
                addModules(builder);
                var host = builder.Build(); // in memory: nothing is stored
                DateTimeOffset? at = ValueOf(args, "--at") is { } t ? DateTimeOffset.Parse(t, CultureInfo.InvariantCulture) : null;
                if (ValueOf(args, "--profile") is { } profile)
                {
                    if (!host.Policy.Config.Agents.ContainsKey(profile))
                    {
                        await Console.Error.WriteLineAsync($"The policy defines no agent profile '{profile}'. Defined: " +
                            (host.Policy.Config.Agents.Count == 0 ? "none" : string.Join(", ", host.Policy.Config.Agents.Keys.Order(StringComparer.Ordinal))) + ".");
                        return 2;
                    }
                    if (host.Catalog.TryGet(args[explainAt + 1], out var reg) && !host.Policy.Admits(reg.Descriptor, profile))
                    {
                        await output.WriteLineAsync($"{args[explainAt + 1]} -> Deny  (not in agent profile '{profile}')");
                        await output.WriteLineAsync("  Not advertised to callers with this profile; a call by name is refused and audited.");
                        return 0;
                    }
                    await output.WriteLineAsync($"Agent profile '{profile}' includes this tool; policy decides:");
                }
                var d = host.Policy.Simulate(host.Catalog, args[explainAt + 1], args[explainAt + 2], args.Contains("--tainted"), at);
                await output.WriteLineAsync($"{args[explainAt + 1]} -> {d.Mode}  ({d.Rule})");
                foreach (var line in d.Trace) await output.WriteLineAsync("  " + line);
                if (host.Catalog.TryGet(args[explainAt + 1], out var described))
                {
                    if (host.Labels.Of(described.Descriptor) is { Count: > 0 } own)
                        await output.WriteLineAsync($"  returns data labelled: {string.Join(", ", own)} (the session gains them)");
                    if (ValueOf(args, "--labels") is { } held)
                    {
                        var broken = host.Labels.Check(described.Descriptor, held.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), args[explainAt + 2]);
                        var mode = host.Labels.Mode == LimitMode.Enforce ? "" : $" (DataFlowMode {host.Labels.Mode}: not enforced)";
                        if (broken.Count == 0) await output.WriteLineAsync($"  data flow: allowed for {held}");
                        foreach (var b in broken) await output.WriteLineAsync($"  data flow: {b.Label} may not go to {described.Descriptor.Name} -> {b.Mode}{mode}");
                    }
                }
                return 0;
            }

            var dbPath = ValueOf(args, "--db") ?? "opentail-host.db";
            if (!File.Exists(dbPath))
            {
                await Console.Error.WriteLineAsync($"No database at {dbPath} (use --db). Nothing to {(exportPath is null ? "replay" : "export")}.");
                return 2;
            }
            DateTimeOffset? since = ValueOf(args, "--since") is { } s ? DateTimeOffset.UtcNow - ParseSince(s) : null;

            if (suspend is not null || cancelPending is not null || recordRestore)
            {
                var db = new SqliteActionStore(dbPath);
                var audit = new SqliteAuditSink(db);
                var by = ValueOf(args, "--by");
                var text = ValueOf(args, recordRestore ? "--note" : "--reason");
                if (by is not { Length: > 0 } || text is not { Length: > 0 })
                {
                    await Console.Error.WriteLineAsync($"Usage: {name} --db <file> " + (recordRestore ? "--record-restore --by <your name> --note <what was restored>"
                        : $"{(suspend is not null ? "--suspend" : "--cancel-pending")} <subject> --by <your name> --reason <why>"));
                    return 64;
                }
                if (recordRestore)
                {
                    var head = audit.Head();
                    await audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "restore_recorded", null, null, by, $"head {head?.ToString() ?? "none"}: {text}"));
                    await output.WriteLineAsync($"Recorded: restored to {head?.ToString() ?? "an empty trail"}. Checkpoints taken after it will show as MISSING in --verify-audit.");
                    return 0;
                }
                if (suspend is not null)
                {
                    var levels = new IdentityLevels(new SqliteIdentityLevelStore(db), new LimitsConfig(), audit, TimeProvider.System);
                    var done = await levels.SuspendAsync(suspend, by, text);
                    await output.WriteLineAsync(done ? $"{suspend} is suspended: every call is refused until a person clears it." : $"{suspend} was already suspended.");
                    return 0;
                }
                var cancelled = 0;
                foreach (var r in await db.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, RequesterId = cancelPending, Limit = int.MaxValue }))
                {
                    // The cancellation and its entry are one transaction: the trail is this same database (see audit above).
                    var done = await db.TransitionAuditedAsync(r.Id, ActionStatus.Pending, ActionStatus.Cancelled, x => x with
                    {
                        Reviews = [.. x.Reviews, new ActionReview(by, ReviewDecision.Reject, "Cancelled by an operator: " + text, DateTimeOffset.UtcNow) { Subject = "operator:" + by }],
                    }, d => new AuditEntry(d.UpdatedAt, "cancelled", d.Id, d.ToolName, by, $"by {by}: {text}"));
                    if (done is null) continue;
                    cancelled++;
                }
                await output.WriteLineAsync($"Cancelled {cancelled} pending action(s) of {cancelPending}.");
                return 0;
            }

            if (circuits)
            {
                // Circuits live in the Host's memory; the audit trail says what each did last. A restart clears them.
                var db = new SqliteActionStore(dbPath);
                var last = new SortedDictionary<string, AuditEntry>(StringComparer.Ordinal);
                foreach (var e in new SqliteAuditSink(db).Read().Where(e => e.Event.StartsWith("circuit.", StringComparison.Ordinal) && e.ToolName is not null))
                    last[e.ToolName!] = e;
                var held = (await db.QueryAsync(new ActionQuery { Status = ActionStatus.Failed, Limit = int.MaxValue }))
                    .Where(a => a.Error?.Code == "circuit_open" && a.RetryAt is not null).GroupBy(a => a.ToolName).ToDictionary(g => g.Key, g => g.Count());
                var paused = last.Where(kv => kv.Value.Event is "circuit.opened" or "circuit.reopened").ToList();
                if (paused.Count == 0) await output.WriteLineAsync("No route is paused (as of the last circuit entries in the audit trail).");
                foreach (var (tool, e) in paused)
                    await output.WriteLineAsync($"{tool}  paused since {e.At:u}  {e.Detail}  holding {held.GetValueOrDefault(tool)} call(s)");
                foreach (var (tool, e) in last.Where(kv => kv.Value.Event == "circuit.would_open"))
                    await output.WriteLineAsync($"{tool}  would be paused (report only) since {e.At:u}  {e.Detail}");
                await output.WriteLineAsync("Circuits are kept in each Host process's memory: a restart clears them, and this list reads the audit trail.");
                return 0;
            }

            if (exportAudit is not null)
            {
                var from = ValueOf(args, "--from") is { } f ? DateTimeOffset.Parse(f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                    : since ?? DateTimeOffset.MinValue;
                var until = ValueOf(args, "--until") is { } u ? DateTimeOffset.Parse(u, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : DateTimeOffset.UtcNow;
                var result = AuditExport.Write(exportAudit, new SqliteActionStore(dbPath), from, until);
                await output.WriteLineAsync($"Wrote {result.Folder}: {result.AuditEntries} audit entries, {result.DecisionRecords} decision records, " +
                                            $"{result.PolicyVersions} policy versions; head {result.Head?.ToString() ?? "none"}.");
                await output.WriteLineAsync($"Check it with: python3 {Path.Combine(result.Folder, "verify.py")} {result.Folder}");
                await AuditExportAsync(dbPath, $"audit evidence: {result.AuditEntries} entries, {result.DecisionRecords} decision records to {result.Folder}");
                return 0;
            }

            if (accessReport)
            {
                var db = new SqliteActionStore(dbPath);
                var at = ValueOf(args, "--at") is { } a ? DateTimeOffset.Parse(a, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : DateTimeOffset.UtcNow;
                var trail = new SqliteAuditSink(db).Read();
                foreach (var line in AccessChanges.Report(trail, new SqlitePolicyVersionStore(db), new SqliteAccessGrantStore(db).AllEver(), at))
                    await output.WriteLineAsync(line);
                return 0;
            }

            if (listWork || workItem is not null || requeue is not null || cancelWork is not null || redrive is not null)
            {
                var db = new SqliteActionStore(dbPath);
                var store = new SqliteWorkStore(db);
                if (requeue is not null || cancelWork is not null || redrive is not null)
                {
                    if (ValueOf(args, "--by") is not { Length: > 0 } by)
                    {
                        await Console.Error.WriteLineAsync($"Usage: {name} --db <file> {(requeue is not null ? "--requeue" : redrive is not null ? "--redrive" : "--cancel-work")} <id> --by <your name>");
                        return 64;
                    }
                    var service = new WorkService(store, db, new SqliteAuditSink(db), new SessionRegistry(new SqliteSessionStore(db)),
                        new DataLabels(new SqliteLabelStore(db), PolicyConfig.Deny, TimeProvider.System), PolicyConfig.Deny, TimeProvider.System);
                    if (redrive is not null)
                    {
                        var again = await service.RedriveAsync(redrive, by, ValueOf(args, "--for"), args.Contains("--exclude-failed"), args.Contains("--drop-note"));
                        await output.WriteLineAsync(again is null ? $"{redrive} is not a dead or expired item." : $"{again.Id} is open again, for {again.For}.");
                        return again is null ? 1 : 0;
                    }
                    var changed = requeue is not null ? await service.RequeueAsync(requeue, by) : await service.CancelAsync(cancelWork!, by);
                    await output.WriteLineAsync(changed is null
                        ? $"{requeue ?? cancelWork} is not {(requeue is not null ? "an ended item" : "open or claimed")}."
                        : $"{changed.Id} is now {changed.Status.ToString().ToLowerInvariant()}.");
                    return changed is null ? 1 : 0;
                }
                if (workItem is not null)
                {
                    if (store.Get(workItem) is not { } item)
                    {
                        await output.WriteLineAsync($"No work item {workItem}.");
                        return 1;
                    }
                    var view = WorkService.View(item, store.GetThreadNote(item.Thread));
                    view["for"] = item.For;
                    view["chain"] = new System.Text.Json.Nodes.JsonArray(item.Chain.Select(c => (System.Text.Json.Nodes.JsonNode)c).ToArray());
                    view["claimedBy"] = item.ClaimedBy;
                    view["labels"] = new System.Text.Json.Nodes.JsonArray(item.Labels.Select(l => (System.Text.Json.Nodes.JsonNode)$"{l.Label} ({l.Source})").ToArray());
                    await output.WriteLineAsync(view.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
                WorkStatus? state = ValueOf(args, "--state") is { } st
                    ? Enum.TryParse<WorkStatus>(st, ignoreCase: true, out var parsed) ? parsed : throw new ArgumentException($"Unknown state '{st}'.")
                    : null;
                var rows = store.Query(state, ValueOf(args, "--thread"), 1000);
                if (rows.Count == 0) await output.WriteLineAsync("No work items.");
                foreach (var i in rows)
                    await output.WriteLineAsync($"{i.Id}  {i.Status.ToString().ToLowerInvariant(),-9} {i.Kind.ToString().ToLowerInvariant(),-6} for {i.For}  from {i.From}  runs {i.Runs}  created {i.CreatedAt:u}  thread {i.Thread}");
                return 0;
            }

            if (policyVersions || diffAt >= 0)
            {
                var versions = new SqlitePolicyVersionStore(new SqliteActionStore(dbPath));
                if (policyVersions)
                {
                    var all = versions.List();
                    if (all.Count == 0) await output.WriteLineAsync("No policy versions recorded yet (a Host records one at start-up).");
                    foreach (var v in all)
                        await output.WriteLineAsync($"{v.Hash}  first {v.FirstLoaded:u}  last {v.LastLoaded:u}");
                    return 0;
                }
                if (diffAt + 2 >= args.Length)
                {
                    await Console.Error.WriteLineAsync($"Usage: {name} --db <file> --policy-diff <hash> <hash>");
                    return 64;
                }
                PolicyVersion? Find(string h) => versions.Get(h) ?? versions.List().SingleOrDefault(v => v.Hash.StartsWith(h.StartsWith("sha256:") ? h : "sha256:" + h, StringComparison.Ordinal));
                if (Find(args[diffAt + 1]) is not { } from || Find(args[diffAt + 2]) is not { } to)
                {
                    await Console.Error.WriteLineAsync("Unknown policy version (see --policy-versions; a unique prefix of the hash is enough).");
                    return 1;
                }
                var lines = PolicyLoad.Diff(from.Json, to.Json);
                if (lines.Count == 0) await output.WriteLineAsync("The two versions are identical.");
                foreach (var l in lines) await output.WriteLineAsync(l);
                return 0;
            }

            if (listGrants || revokeGrant is not null)
            {
                var db = new SqliteActionStore(dbPath);
                var grants = new AccessGrants(new SqliteAccessGrantStore(db), TimeProvider.System);
                if (revokeGrant is not null)
                {
                    if (ValueOf(args, "--by") is not { Length: > 0 } by)
                    {
                        await Console.Error.WriteLineAsync($"Usage: {name} --db <file> --revoke-grant <id> --by <your name>");
                        return 64;
                    }
                    var revoked = grants.Revoke(revokeGrant);
                    if (revoked) await new SqliteAuditSink(db).AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "grant_revoked", null, null, by, revokeGrant));
                    await output.WriteLineAsync(revoked ? $"{revokeGrant} revoked." : $"No grant {revokeGrant} in force.");
                    return revoked ? 0 : 1;
                }
                var all = grants.All();
                if (all.Count == 0) await output.WriteLineAsync("No temporary access grants in force.");
                foreach (var g in all)
                    await output.WriteLineAsync($"{g.Id}  {g.Subject}  {g.Tool}: until {g.Until:u} or {g.CallsLeft} more call(s); reason: {g.Reason}");
                return 0;
            }

            if (reviewReport)
            {
                var from = since ?? DateTimeOffset.UtcNow.AddDays(-7);
                var (reviewers, tools) = await ReviewQuality.CollectAsync(new SqliteActionStore(dbPath), from);
                await output.WriteLineAsync($"Reviews since {from:u}. For the operator: each reviewer's figures are theirs, not for other reviewers.");
                if (reviewers.Count == 0) await output.WriteLineAsync("No decisions by people in this period.");
                foreach (var r in reviewers)
                {
                    var flags = new List<string>();
                    if (r.QuickApprovals > 0) flags.Add($"{r.QuickApprovals} approved within {ReviewQuality.QuickSeconds:0} s of arriving");
                    if (r.BurstApprovals > 0) flags.Add($"{r.BurstApprovals} approved in bursts of {ReviewQuality.BurstSize}+ a minute");
                    if (r.ApprovedThenFailed > 0) flags.Add($"{r.ApprovedThenFailed} approved then failed");
                    await output.WriteLineAsync($"  {r.Reviewer}  {r.Tool}: {r.Decisions} decided, {r.ApprovalRate:P0} approved, {r.Rejected} rejected, " +
                        $"{r.ChangesRequested} sent back; median {r.MedianSecondsToDecide:0} s to decide" + (flags.Count > 0 ? "; " + string.Join("; ", flags) : ""));
                }
                foreach (var t in tools.Where(t => t.Suggestion is not null))
                    await output.WriteLineAsync($"  tool {t.Tool}: {t.Approved} of {t.Decisions} approved, {t.Rejected} rejected: {t.Suggestion}.");
                return 0;
            }

            if (listLevels || clearLevel is not null)
            {
                var db = new SqliteActionStore(dbPath);
                var levels = new IdentityLevels(new SqliteIdentityLevelStore(db), new LimitsConfig(), new SqliteAuditSink(db), TimeProvider.System);
                if (clearLevel is not null)
                {
                    if (ValueOf(args, "--by") is not { Length: > 0 } by)
                    {
                        await Console.Error.WriteLineAsync($"Usage: {name} --db <file> --clear-level <subject> --by <your name> [--key-rotated]");
                        return 64;
                    }
                    try
                    {
                        var cleared = await levels.ClearAsync(clearLevel, by, args.Contains("--key-rotated"));
                        await output.WriteLineAsync(cleared ? $"{clearLevel} is back to Normal." : $"{clearLevel} has no level to clear.");
                        return cleared ? 0 : 1;
                    }
                    catch (InvalidOperationException ex)
                    {
                        await Console.Error.WriteLineAsync(ex.Message);
                        return 2;
                    }
                }
                var rows = levels.List();
                if (rows.Count == 0) await output.WriteLineAsync("Every identity is at Normal.");
                foreach (var r in rows)
                    await output.WriteLineAsync($"{r.Subject}  {r.Level}{(r.Enforced ? "" : " (report only: no effect)")}  since {r.Since:u}: {r.Reason}");
                return 0;
            }

            if (verifyAudit || auditCheckpoint)
            {
                var chain = new SqliteAuditSink(new SqliteActionStore(dbPath)).ReadChain();
                if (auditCheckpoint)
                {
                    if (new SqliteAuditSink(new SqliteActionStore(dbPath)).Head() is not { } head) { await output.WriteLineAsync("The audit trail has no chained entries yet."); return 1; }
                    await output.WriteLineAsync(head.ToString());
                    return 0;
                }
                var checkpoints = args.Select((a, i) => (a, i)).Where(x => x.a == "--checkpoint" && x.i + 1 < args.Length)
                    .Select(x => AuditCheckpoint.Parse(args[x.i + 1])).ToList();
                var v = AuditChain.Verify(chain, checkpoints);
                await output.WriteLineAsync($"{v.Entries} audit entries; {v.Unchained} written before the chain existed (not verifiable); newest {(v.Head?.ToString() ?? "none")}.");
                await output.WriteLineAsync(v.FirstBreak is null ? "Chain intact." : "BROKEN: " + v.FirstBreak + ".");
                foreach (var line in v.Checkpoints) await output.WriteLineAsync(line);
                if (checkpoints.Count == 0)
                    await output.WriteLineAsync("Removing the newest entries is only caught against a checkpoint kept elsewhere: pass --checkpoint <seq:hash>.");
                // Decision records kept when actions were purged, against the digests the (chained) trail holds for them.
                var records = DecisionRecordCheck.Verify(chain, new SqliteActionStore(dbPath).ReadDecisionRecords());
                await output.WriteLineAsync($"{records.Records} decision record(s) in {records.Batches} purge batch(es): {(records.Intact ? "intact" : "BROKEN")}.");
                foreach (var p in records.Problems) await output.WriteLineAsync("BROKEN: " + p + ".");
                foreach (var n in records.Notes) await output.WriteLineAsync(n + ".");
                return v.Intact && records.Intact ? 0 : 1;
            }

            if (usage)
            {
                // Reads the rollups only: no policy or modules needed, and nothing in them is an argument value or a result.
                var store = new SqliteActionStore(dbPath);
                var to = DateOnly.FromDateTime(DateTime.UtcNow);
                var from = DateOnly.FromDateTime((since ?? DateTimeOffset.UtcNow.AddDays(-7)).UtcDateTime);
                var rows = store.ReadUsage(from, to);
                if (rows.Count == 0) await output.WriteLineAsync($"No usage recorded from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.");
                foreach (var day in rows.GroupBy(r => r.Day))
                {
                    await output.WriteLineAsync($"{day.Key:yyyy-MM-dd}");
                    foreach (var r in day)
                        await output.WriteLineAsync($"  {r.Subject}{(r.Profile is null ? "" : $" ({r.Profile})")}  {r.Tool}: {r.Calls} call(s), " +
                            $"{r.Succeeded} ok, {r.Failed} failed, {r.Denied} denied, {r.Rejected} rejected; {r.Records} record(s), " +
                            $"{r.Entities} distinct entit{(r.Entities == 1 ? "y" : "ies")}, {r.Recipients} recipient(s), {r.Bytes} bytes");
                }
                return 0;
            }

            if (exportPath is not null)
            {
                if (policyPath is null)
                {
                    await Console.Error.WriteLineAsync($"Usage: {name} --policy <file> --db <file> --export-review-outcomes <out.jsonl> [--since 30d]");
                    return 64;
                }
                var exporting = new OpenTailHostBuilder().WithName(name).UsePolicy(PolicyConfig.Load(policyPath)).UseSqlite(dbPath);
                await ModulesCli.ApplyAsync(exporting, args, ModuleLoadMode.Describe);
                addModules(exporting);
                var sample = ValueOf(args, "--approved-sample") is { } sa && double.TryParse(sa, NumberStyles.Float, CultureInfo.InvariantCulture, out var sv) ? sv : 0;
                var outcomes = await ReviewOutcomeExport.CollectAsync(exporting.Build(), since, approvedSample: sample);
                await File.WriteAllTextAsync(exportPath, ReviewOutcomeExport.ToJsonLines(outcomes));
                await output.WriteLineAsync($"Exported {outcomes.Count} rejected or sent-back action(s) to {exportPath}" + (since is null ? "" : $" (since {since:u})") + ".");
                await AuditExportAsync(dbPath, $"review outcomes: {outcomes.Count} action(s) to {exportPath}");
                await output.WriteLineAsync("  Redaction is best effort. Check every line before it leaves this deployment or becomes a scenario.");
                return 0;
            }

            if (shadowPath is not null)
            {
                if (policyPath is null)
                {
                    await Console.Error.WriteLineAsync($"Usage: {name} --policy <file> --db <file> --export-shadow-cases <out.jsonl> [--since 7d]");
                    return 64;
                }
                var reading = new OpenTailHostBuilder().WithName(name).UsePolicy(PolicyConfig.Load(policyPath)).UseSqlite(dbPath);
                await ModulesCli.ApplyAsync(reading, args, ModuleLoadMode.Describe);
                addModules(reading);
                var cases = await ShadowCases.CollectAsync(reading.Build(), since);
                await File.WriteAllTextAsync(shadowPath, ShadowCases.ToJsonLines(cases));
                await output.WriteLineAsync($"Exported {cases.Count} session(s) for shadow runs to {shadowPath}.");
                await AuditExportAsync(dbPath, $"shadow cases (not redacted): {cases.Count} session(s) to {shadowPath}");
                await output.WriteLineAsync("  Not redacted: real content. Keep it inside this deployment and delete it with the proposal.");
                return 0;
            }

            if (skillOutcomes)
            {
                if (policyPath is null)
                {
                    await Console.Error.WriteLineAsync($"Usage: {name} --policy <file> --db <file> --skill-outcomes [--since 30d] [--min-cases 20] [--worse-by 0.15]");
                    return 64;
                }
                var reading = new OpenTailHostBuilder().WithName(name).UsePolicy(PolicyConfig.Load(policyPath)).UseSqlite(dbPath);
                await ModulesCli.ApplyAsync(reading, args, ModuleLoadMode.Describe);
                addModules(reading);
                var versions = await SkillOutcomes.CollectAsync(reading.Build(), since);
                if (versions.Count == 0) await output.WriteLineAsync("No decided actions after a served skill" + (since is null ? "" : $" since {since:u}") + ".");
                foreach (var v in versions)
                {
                    var (lo, hi) = v.Interval;
                    await output.WriteLineAsync($"{v.Skill}@{v.Version}: {v.Decided} decided, {v.Approved} approved, {v.Rejected} rejected, {v.ChangesRequested} sent back; " +
                        $"turned down {v.TurnedDown:P0} ({lo:P0} to {hi:P0}); last served {v.LastServed:u}");
                }
                var minCases = ValueOf(args, "--min-cases") is { } mc && int.TryParse(mc, out var m) ? m : 20;
                var worseBy = ValueOf(args, "--worse-by") is { } wb && double.TryParse(wb, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 0.15;
                foreach (var r in SkillOutcomes.Suggest(versions, minCases, worseBy))
                    await output.WriteLineAsync($"Suggest rolling back {r.Skill} from {r.Current} to {r.Previous}: {r.Why}");
                await output.WriteLineAsync("  (A suggestion only. Nothing was changed; a rollback goes through approval like any publish.)");
                return 0;
            }

            var cand = new OpenTailHostBuilder().WithName(name).UsePolicy(PolicyConfig.Load(replayPath!)).UseSqlite(dbPath);
            await ModulesCli.ApplyAsync(cand, args, ModuleLoadMode.Describe);
            addModules(cand);
            var report = await PolicyReplay.RunAsync(cand.Build(), since);

            await output.WriteLineAsync($"Replayed {report.Examined} stored action(s) against {replayPath}" + (since is null ? "" : $" (since {since:u})"));
            await output.WriteLineAsync($"  unchanged: {report.Unchanged}; not policy decisions: {report.SkippedNotPolicy}; tool no longer loaded: {report.SkippedUnknownTool}");
            if (report.Changes.Count == 0) await output.WriteLineAsync("  No action would have been handled differently.");
            else
            {
                await output.WriteLineAsync($"  {report.Changes.Count} would have been handled differently:");
                foreach (var line in report.Summary()) await output.WriteLineAsync("    " + line);
                foreach (var c in report.Changes.Take(10))
                    await output.WriteLineAsync($"      e.g. {c.ActionId}  {c.Tool}  {c.At:u}  {c.Was} -> {c.WouldBe}  [{c.Rule}]");
            }
            await output.WriteLineAsync("  (Nothing was executed or changed. Budgets are not replayed; time rules are judged at each action's own time.)");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or System.Text.Json.JsonException or FormatException)
        {
            await Console.Error.WriteLineAsync("Cannot run: " + ex.Message);
            return 2;
        }
    }

    private static async Task<int> RunModuleCommandAsync(string[] args, string name, Action<OpenTailHostBuilder> addModules, TextWriter output,
        string? policyPath, bool listModules, string? adoptId, string? testPath)
    {
        if (adoptId is not null && (policyPath is null || ValueOf(args, "--modules") is null))
        {
            await Console.Error.WriteLineAsync($"Usage: {name} --policy <file> --modules <file> --adopt-policy <module id> [--allow-execute] [--replace]");
            return 64;
        }
        if (testPath is not null && policyPath is null)
        {
            await Console.Error.WriteLineAsync($"Usage: {name} --policy <file> [--modules <file>] --test-policy <cases.json>");
            return 64;
        }

        var loadWarnings = new StringWriter();
        var builder = new OpenTailHostBuilder().WithName(name).UsePolicy(policyPath is null ? PolicyConfig.Deny : PolicyConfig.Load(policyPath));
        var loaded = await ModulesCli.ApplyAsync(builder, args, ModuleLoadMode.Describe, loadWarnings);
        addModules(builder);
        var host = builder.Build(); // in memory, no credentials needed

        if (adoptId is not null) return await AdoptAsync(host, policyPath!, adoptId, args.Contains("--allow-execute"), args.Contains("--replace"), output);

        if (testPath is not null)
        {
            var outcomes = PolicyTests.Run(host, PolicyTests.Parse(await File.ReadAllTextAsync(testPath)));
            foreach (var o in outcomes)
                await output.WriteLineAsync($"{(o.Passed ? "pass" : "FAIL")}  {o.Case.Name ?? o.Case.Tool}: expected {o.Case.Expect}, got {o.Actual}  ({o.Rule})");
            var failed = outcomes.Count(o => !o.Passed);
            await output.WriteLineAsync($"{outcomes.Count - failed} passed, {failed} failed.");
            return failed == 0 ? 0 : 1;
        }

        if (listModules)
        {
            if (policyPath is null) await output.WriteLineAsync("(no --policy given: shown against a deny-everything policy)");
            foreach (var line in ModulesCli.Describe(host)) await output.WriteLineAsync(line);
            foreach (var m in host.Catalog.Modules)
                foreach (var t in m.Tools)
                {
                    host.Catalog.TryGet(t, out var reg);
                    var d = host.Policy.Resolve(reg.Descriptor, tainted: false);
                    var flags = string.Join(" ", new[] { reg.Descriptor.ReadsUntrusted ? "reads-untrusted" : null, reg.Preview is not null ? "preview" : null }.Where(x => x is not null));
                    await output.WriteLineAsync($"    {t,-36} {reg.Descriptor.Risk,-15} {d.Mode,-8} {d.Rule} {flags}");
                }
            if (loaded is not null)
                foreach (var c in loaded.Modules.Where(c => c.AllowedHosts.Count > 0))
                    await output.WriteLineAsync($"  {c.Entry.Id}: may reach {string.Join(", ", c.AllowedHosts)}; credentials {(c.Credentials.Count == 0 ? "none" : string.Join(", ", c.Credentials))}");
        }
        var warnings = loadWarnings.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(PolicyLint.Run(host).Select(w => "warning: " + w)).ToList();
        foreach (var w in warnings) await output.WriteLineAsync(w);
        if (warnings.Count == 0) await output.WriteLineAsync("No warnings.");
        return 0;
    }

    private static async Task<int> AdoptAsync(OpenTailHost host, string policyPath, string moduleId, bool allowExecute, bool replace, TextWriter output)
    {
        var result = PolicyAdoption.Check(host.Catalog, moduleId, allowExecute);
        foreach (var n in result.Notes) await output.WriteLineAsync("note: " + n);
        if (result.Errors.Count > 0)
        {
            await output.WriteLineAsync($"Not adopted. The suggested policy of '{moduleId}' was refused:");
            foreach (var e in result.Errors) await output.WriteLineAsync("  " + e);
            return 2;
        }

        var policyDir = Path.GetDirectoryName(Path.GetFullPath(policyPath))!;
        var dir = Path.Combine(policyDir, "policy.d");
        var file = Path.Combine(dir, moduleId + ".json");
        var before = File.Exists(file) ? PolicyConfig.ReadFragment(file) : new Dictionary<string, ToolPolicy>(StringComparer.Ordinal);
        var diff = PolicyAdoption.Diff(before, result.Tools);
        if (diff.Count == 0)
        {
            await output.WriteLineAsync($"{file} already holds this suggestion. Nothing changed.");
            return 0;
        }
        foreach (var line in diff) await output.WriteLineAsync("  " + line);
        if (File.Exists(file) && !replace)
        {
            await output.WriteLineAsync($"{file} exists and differs. Nothing written; pass --replace to overwrite it.");
            return 3;
        }

        // The operator's own file is never rewritten. Entries there for the same tools would clash, so say so.
        using (var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(policyPath),
                   new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }))
        {
            var own = doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("Tools", StringComparison.OrdinalIgnoreCase));
            if (own.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var clash = own.Value.EnumerateObject().Select(p => p.Name).Where(result.Tools.ContainsKey).ToList();
                if (clash.Count > 0)
                {
                    await output.WriteLineAsync($"Not written: {policyPath} already has entries for {string.Join(", ", clash)}. Remove them there first (one place per tool).");
                    return 2;
                }
            }
        }

        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(file, result.Json + Environment.NewLine);
        await output.WriteLineAsync($"Wrote {file}.");
        var main = PolicyConfig.Load(policyPath);
        if (!main.IncludedFiles.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)))
            await output.WriteLineAsync($"It is not in effect yet: add  \"Include\": [\"policy.d\"]  to {policyPath}, then run --list-modules to check.");
        else
            await output.WriteLineAsync("It takes effect when the Host next starts. Check it with --list-modules and --explain.");
        return 0;
    }

    private static TimeSpan ParseSince(string s)
    {
        var m = Regex.Match(s, @"^(\d+)([hdw])$");
        if (!m.Success) throw new ArgumentException("--since looks like 12h, 7d or 2w.");
        var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "h" => TimeSpan.FromHours(n), "d" => TimeSpan.FromDays(n), _ => TimeSpan.FromDays(7 * n) };
    }

    private static string? ValueOf(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
