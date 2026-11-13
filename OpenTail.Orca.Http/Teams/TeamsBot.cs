using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTail.Orca.Notifications;

namespace OpenTail.Orca.Http.Teams;

/// <summary>
/// Approvals inside Teams. Waiting actions arrive as Adaptive Cards in the chat of the person whose AI asked (or in a channel), with
/// Approve, Approve with changes (the draft's text fields are editable in the card), Request changes, Reject and Open; many waiting actions
/// become one digest. A reviewer may also type <c>waiting</c> or <c>help</c> in their own chat. A click is accepted only from the Bot
/// Framework (signed token for this bot and service URL), from this tenant, from a user listed under <see cref="TeamsBotOptions.Reviewers"/>,
/// on a card signed for that action and conversation, for a tool whose risk chat may decide; and then the same review rules apply as on the
/// page. The card updates in place, so the chat is also a log.
/// </summary>
public sealed class TeamsBot
{
    /// <summary>The outcomes that end a card's "waiting" state. Used for card refresh only; the route alias <c>decided</c> is a different list.</summary>
    private static readonly string[] Decided = ["approved", "rejected", "changes_requested", "succeeded", "failed", "expired", "cancelled"];

    /// <summary>The digest scope a signed <c>back</c> button names (it is not an action id).</summary>
    private const string DigestScope = "digest";

    /// <summary>The A2A reply tool's name, as <c>OpenTail.Orca.A2A</c> names it (this project does not reference that one).</summary>
    private const string PartnerReplyTool = "a2a.reply";

    /// <summary>What a reviewer is told when a partner's reply is not approved from chat.</summary>
    private const string PartnerReplyNote = "Partner replies are approved on the review page.";

    /// <summary>The audit event written when a card cannot be refreshed in chat.</summary>
    internal const string RefreshFailedEvent = "teams_refresh_failed";

    private const string PersonalReply = "I post here what your AI prepares for your approval. Approve, change or reject it from the card.";

    /// <summary>The help a reviewer gets: the commands this bot has, then the extensions' commands (batch 05, B2), then the card actions.</summary>
    private string HelpText() =>
        "Commands: waiting (the actions waiting for you), work (the work items: counts and the oldest open or dead ones), " +
        string.Concat(_commands.Select(c => c.Name + " (" + c.Help + "), ")) +
        "help (this list). On a card: Approve; Approve with changes " +
        "(edit the text, then approve); Request changes and Reject (both need a reason); Stop and Run now (delayed actions only); " +
        "Open (the review page). Any other text gets the usual reply.";

    private readonly TeamsBotOptions _o;
    private readonly Dictionary<string, TeamsReviewer> _reviewers;
    private readonly BotConnector _bot;
    private readonly TeamsStore _store;
    private IReadOnlyList<ChatCommand> _commands = [];
    private readonly string? _publicBaseUrl;
    private readonly Lazy<byte[]> _cardKey;
    private OpenTailHost? _host;

    public TeamsBot(TeamsBotOptions options, string dbPath, string? publicBaseUrl)
    {
        if (options.DigestThreshold < 1) throw new ArgumentException("Teams.DigestThreshold must be at least 1.");
        if (options.AppPasswordEnv is not { Length: > 0 } secretEnv || string.IsNullOrWhiteSpace(options.Env(secretEnv)))
            throw new InvalidOperationException($"Teams: the bot's client secret is not available (set AppPasswordEnv and the environment variable {options.AppPasswordEnv}).");
        _o = options;
        // Object ids compare without case, whatever the config file's comparer was (the JSON loader does not keep the one it was given).
        _reviewers = new Dictionary<string, TeamsReviewer>(options.Reviewers, StringComparer.OrdinalIgnoreCase);
        _bot = new BotConnector(options);
        _store = new TeamsStore(dbPath);
        _publicBaseUrl = publicBaseUrl?.TrimEnd('/');
        // Cards must stay valid across restarts, so the key comes from the bot's secret; without one, cards last as long as the process.
        _cardKey = new(() => options.AppPasswordEnv is { } e && options.Env(e) is { Length: > 0 } s
            ? SHA256.HashData(Encoding.UTF8.GetBytes("opentail-teams-card|" + s))
            : RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Binds the bot to the Host, and the extensions' read-only commands (batch 05, B2) that a reviewer may type in a personal chat.
    /// A call without commands keeps the ones already set: the HTTP host attaches again when it starts.</summary>
    public void Attach(OpenTailHost host, IReadOnlyList<ChatCommand>? commands = null)
    {
        _host = host;
        _commands = commands ?? _commands;
    }

    private OpenTailHost Host => _host ?? throw new InvalidOperationException("The Teams bot is not attached to a host.");

    /// <summary>The notification channel (<c>"Type": "teams-bot"</c>) that posts and updates cards.</summary>
    public INotificationChannel Channel(ChannelConfig c) => new BotChannel(this, c.Name, c.Target ?? "requester");

    public void Map(IEndpointRouteBuilder app) => app.MapPost(_o.MessagingPath, (Func<HttpContext, Task<IResult>>)HandleAsync).AllowAnonymous();

    // ---------------------------------------------------------------------------------------------------------- incoming

    private async Task<IResult> HandleAsync(HttpContext ctx)
    {
        JsonObject activity;
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            if (ms.Length > 256 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            activity = JsonNode.Parse(ms.ToArray()) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException) { return Results.BadRequest(); }

        var serviceUrl = Str(activity, "serviceUrl");
        if (serviceUrl is null || !Uri.TryCreate(serviceUrl, UriKind.Absolute, out _)
            || !await _bot.IsGenuineAsync(ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null, serviceUrl, ctx.RequestAborted))
            return Results.Unauthorized();

        var tenant = activity["channelData"]?["tenant"]?["id"]?.GetValue<string>() ?? activity["conversation"]?["tenantId"]?.GetValue<string>();
        if (!string.Equals(tenant, _o.TenantId, StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(StatusCodes.Status403Forbidden);

        var conversationId = activity["conversation"]?["id"]?.GetValue<string>();
        if (conversationId is null) return Results.BadRequest();
        var personal = activity["conversation"]?["conversationType"]?.GetValue<string>() is "personal" or null;
        var from = activity["from"]?["aadObjectId"]?.GetValue<string>()?.ToLowerInvariant();
        var convKey = personal ? (from is null ? null : "user:" + from) : "channel:" + conversationId.Split(';')[0];
        if (convKey is null) return Results.BadRequest();
        var conv = new TeamsStore.Conversation(convKey, serviceUrl, personal ? conversationId : conversationId.Split(';')[0], personal ? "personal" : "channel");
        _store.Remember(conv);

        switch (Str(activity, "type"))
        {
            case "invoke" when Str(activity, "name") == "adaptiveCard/action":
                return Results.Json(await InvokeAsync(activity, convKey, from, ctx.RequestAborted));
            case "message" when personal:
                await ReplyToPersonalMessageAsync(activity, conv, from, ctx.RequestAborted);
                return Results.Ok();
            default:
                return Results.Ok();
        }
    }

    /// <summary>A typed message in a personal chat. Only the exact words <c>waiting</c> and <c>help</c> do anything, and only for a listed
    /// reviewer; no other text is parsed into an action, an argument or a reason.</summary>
    private async Task ReplyToPersonalMessageAsync(JsonObject activity, TeamsStore.Conversation conv, string? from, CancellationToken ct)
    {
        if (from is null || !_reviewers.ContainsKey(from))
        {
            await _bot.SendAsync(conv, Message(PersonalReply), ct);
            return;
        }
        var command = (Str(activity, "text") ?? "").Trim();
        if (command.Equals("waiting", StringComparison.OrdinalIgnoreCase)) await PostWaitingAsync(conv, from, ct);
        else if (command.Equals("help", StringComparison.OrdinalIgnoreCase)) await _bot.SendAsync(conv, Message(HelpText()), ct);
        else if (command.Equals("work", StringComparison.OrdinalIgnoreCase)) await PostLinesAsync(conv, WorkLines(), ct);
        else if (_commands.FirstOrDefault(c => c.Name.Equals(command, StringComparison.OrdinalIgnoreCase)) is { } chat)
            await PostLinesAsync(conv, await chat.Lines(ct), ct);
        else await _bot.SendAsync(conv, Message(PersonalReply + " Type help to see the commands."), ct);
    }

    /// <summary>Posts a read-only answer. Every line is escaped, the extensions' lines too (batch 05, B3): a chat is an exit.</summary>
    private async Task PostLinesAsync(TeamsStore.Conversation conv, IReadOnlyList<string> lines, CancellationToken ct) =>
        await _bot.SendAsync(conv, Message(string.Join("\n", lines.Select(line => TeamsChannel.Escape(line, 300)))), ct);

    /// <summary>The <c>work</c> command (batch 05, B1): counts of work items by status, and the ten oldest open or dead ones, with their short
    /// id, who they are for, the status and the age. No goal, summary or inputs: a work item's text may carry labelled data.</summary>
    private IReadOnlyList<string> WorkLines()
    {
        if (!Host.Policy.Config.Work.Enabled) return ["Work items are not enabled on this Host."];
        var now = DateTimeOffset.UtcNow;
        const int all = 100_000;
        var store = Host.Work.Store;
        var open = store.Query(WorkStatus.Open, limit: all);
        var claimed = store.Query(WorkStatus.Claimed, limit: all);
        var dead = store.Query(WorkStatus.Dead, limit: all);
        var expired = store.Query(WorkStatus.Expired, limit: all).Count(i => i.UpdatedAt >= now - TimeSpan.FromDays(7));
        var lines = new List<string>
        {
            $"Work items: open {open.Count}, claimed {claimed.Count}, dead {dead.Count}, expired in the last 7 days {expired}.",
        };
        foreach (var item in open.Concat(dead).OrderBy(i => i.CreatedAt).Take(10))
            // The id's hex part: the "wi_" prefix's underscore is a Markdown character the escape turns into a quote, so it is left out.
            lines.Add($"work {item.Id.Replace("wi_", "", StringComparison.Ordinal)[..Math.Min(8, item.Id.Length - 3)]} for {item.For}: "
                + $"{item.Status.ToString().ToLowerInvariant()}, {Age(now - item.CreatedAt)} old");
        if (lines.Count == 1) lines.Add("No open or dead work item.");
        return lines;
    }

    private static string Age(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "under a minute"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min"
        : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h"
        : $"{(int)age.TotalDays} d";

    /// <summary>The actions waiting that this reviewer may decide, oldest first, as the digest card (up to 20 shown). Decided with the same
    /// rules the queue enforces (<see cref="ReviewAuthority"/>, checked for Reject, the least restrictive decision).</summary>
    private async Task PostWaitingAsync(TeamsStore.Conversation conv, string from, CancellationToken ct)
    {
        var reviewer = _reviewers[from];
        var subject = TeamsBotOptions.SubjectOf(from);
        var waiting = new List<ActionRecord>();
        foreach (var r in await Host.Store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, Limit = 500 }, ct))
            if (ReviewAuthority.Check(r, ReviewDecision.Reject, subject, reviewer.Groups, Host.Policy.People).Allowed) waiting.Add(r);
        if (waiting.Count == 0)
        {
            await _bot.SendAsync(conv, Message("Nothing is waiting for you."), ct);
            return;
        }
        await _bot.SendAsync(conv, Attachment(DigestCard(waiting, conv.Key)), ct);
    }

    private async Task<JsonObject> InvokeAsync(JsonObject activity, string convKey, string? userObjectId, CancellationToken ct)
    {
        var action = activity["value"]?["action"] as JsonObject;
        var verb = action?["verb"]?.GetValue<string>();
        var data = action?["data"] as JsonObject ?? [];

        // Every verb passes the same signature check for this card and this chat, back included (the digest is signed under DigestScope).
        var actionId = data["actionId"]?.GetValue<string>();
        if (actionId is null || data["t"]?.GetValue<string>() is not { } t || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(t), Encoding.ASCII.GetBytes(Sign(actionId, convKey))))
            return MessageResponse("This card is not valid here.");
        if (verb == "back") return CardResponse(await DigestCardAsync(convKey, ct));

        var record = await Host.Store.GetAsync(actionId, ct);
        if (record is null) return MessageResponse("This action no longer exists.");
        if (verb == "open") return CardResponse(FullCard(record, convKey, back: true));

        if (userObjectId is null || !_reviewers.TryGetValue(userObjectId, out var reviewer))
            return CardResponse(FullCard(record, convKey, note: "You are not set up to decide from Teams. Open the review page."));
        // Stopping a delayed action is always the safe direction: any configured reviewer may, whatever the risk, with no reason.
        if (verb == "stop")
        {
            try { record = await Host.Queue.StopAsync(actionId, TeamsBotOptions.SubjectOf(userObjectId), reviewer.Groups, ct); }
            catch (UnauthorizedAccessException ex) { return CardResponse(FullCard(record, convKey, note: ex.Message)); }
            catch (InvalidOperationException ex) { return CardResponse(FullCard(await Host.Store.GetAsync(actionId, ct) ?? record, convKey, note: ex.Message)); }
            _ = RefreshAsync(record, exceptConv: convKey, CancellationToken.None);
            return CardResponse(FullCard(record, convKey));
        }
        var risk = RiskOf(record);
        if (!ChatMayDecide(risk, _o.ChatMaxRisk))
            return CardResponse(FullCard(record, convKey, note: risk is { } rk
                ? $"{rk} actions are decided on the review page."
                : "This tool is not loaded, so its risk is unknown: decide it on the review page."));

        // A partner's reply is approved on the review page when the operator has turned chat approval off. Reject and request changes stay.
        if (verb is "approve" or "edit" && !ChatMayApprove(record))
            return CardResponse(FullCard(record, convKey, note: PartnerReplyNote));

        // B3, on the click as well as on the card: a preview cut on the card cannot be approved, or changed, from it, whatever the button sent.
        if (verb is "approve" or "edit" && PreviewCut(record))
            return CardResponse(FullCard(record, convKey, note: CutNote(_publicBaseUrl is not null)));

        ReviewDecision decision;
        string? reason = data["reason"]?.GetValue<string>()?.Trim();
        string? edited = null;
        switch (verb)
        {
            case "approve": decision = ReviewDecision.Approve; reason = null; break;
            case "reject":
            case "request_changes":
                if (string.IsNullOrEmpty(reason))
                    return CardResponse(FullCard(record, convKey, note: verb == "reject"
                        ? "Type a reason first, then press Reject."
                        : "Type a reason first, then press Request changes."));
                decision = verb == "reject" ? ReviewDecision.Reject : ReviewDecision.RequestChanges;
                break;
            case "edit":
                decision = ReviewDecision.Approve;
                reason = null;
                var args = JsonNode.Parse(record.EffectiveArgumentsJson) as JsonObject ?? [];
                foreach (var (k, v) in data)
                {
                    if (!k.StartsWith("edit.", StringComparison.Ordinal)) continue;
                    var field = k[5..];
                    // A field the draft does not have is refused, not dropped: the reviewer must know nothing was changed.
                    if (args[field] is not JsonValue old || !old.TryGetValue<string>(out _))
                        return CardResponse(FullCard(record, convKey, note: $"\"{TeamsChannel.Escape(field, 40)}\" is not in this draft, so it cannot be changed."));
                    args[field] = v?.GetValue<string>() ?? "";
                }
                edited = args.ToJsonString();
                if (Host.Catalog.TryGet(record.ToolName, out var reg) && reg.Preview is not null)
                {
                    try { await reg.Preview.PreviewAsync(edited, ct); }
                    catch (ArgumentException ex) { return CardResponse(FullCard(record, convKey, note: "The changed version is not valid: " + Redaction.Scrub(ex.Message))); }
                }
                break;
            default: return MessageResponse("Unknown action.");
        }

        try
        {
            record = await Host.Queue.ReviewAsync(actionId, new ActionReview(reviewer.Name, decision, reason, DateTimeOffset.UtcNow)
            {
                Subject = TeamsBotOptions.SubjectOf(userObjectId),
                Groups = reviewer.Groups,
                EditedArgumentsJson = edited,
            }, ct);
        }
        catch (UnauthorizedAccessException ex) { return CardResponse(FullCard(record, convKey, note: ex.Message)); }
        catch (InvalidOperationException ex) { return CardResponse(FullCard(await Host.Store.GetAsync(actionId, ct) ?? record, convKey, note: ex.Message)); }
        catch (KeyNotFoundException) { return MessageResponse("This action no longer exists."); }

        _ = RefreshAsync(record, exceptConv: convKey, CancellationToken.None);   // other copies and the digest; the clicker gets the response
        return CardResponse(FullCard(record, convKey));
    }

    // ---------------------------------------------------------------------------------------------------------- outgoing

    private sealed class BotChannel(TeamsBot bot, string name, string target) : INotificationChannel
    {
        public string Name => name;
        public Task SendAsync(NotificationMessage message, CancellationToken ct = default) => bot.PostAsync(message, target, ct);
    }

    private IEnumerable<string> TargetsOf(ActionRecord r, string target) =>
        target == "requester"
            ? Host.Policy.People.SubjectsOfSamePerson(r.Requester.Id).Where(s => s.StartsWith("teams:", StringComparison.Ordinal)).Select(s => "user:" + s[6..])
            : [target["conversation:".Length..]];

    private async Task PostAsync(NotificationMessage m, string target, CancellationToken ct)
    {
        var r = await Host.Store.GetAsync(m.Event.ActionId, ct);
        if (r is null) return;
        if (Decided.Contains(m.Name)) { await RefreshAsync(r, exceptConv: null, ct); return; }
        if (m.Name is not ("queued" or "reminder")) return;

        foreach (var key in TargetsOf(r, target).Distinct())
        {
            var conv = _store.Find(key)
                ?? throw new InvalidOperationException($"No Teams conversation for {key}: the Teams app is not installed there yet.");
            var waiting = (await Host.Store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, Limit = 500 }, ct))
                .Count(x => TargetsOf(x, target).Contains(key));
            if (waiting > _o.DigestThreshold)
            {
                _store.Card(r.Id, key, "digest");
                await PostDigestAsync(conv, ct);
            }
            else if (!_store.CardsOf(r.Id).Any(c => c.ConvKey == key))
                _store.Card(r.Id, key, await _bot.SendAsync(conv, Attachment(FullCard(r, key)), ct));
        }
    }

    private async Task PostDigestAsync(TeamsStore.Conversation conv, CancellationToken ct)
    {
        var card = await DigestCardAsync(conv.Key, ct);
        if (_store.Digest(conv.Key) is { } existing) await _bot.UpdateAsync(conv, existing, Attachment(card), ct);
        else _store.Digest(conv.Key, await _bot.SendAsync(conv, Attachment(card), ct));
    }

    /// <summary>Updates every card showing this action (and the digests it is in) to its current state. A failure is recorded in the audit
    /// trail; it never reaches the click that caused it, and the card stays as it was.</summary>
    private async Task RefreshAsync(ActionRecord r, string? exceptConv, CancellationToken ct)
    {
        try
        {
            foreach (var (key, activityId) in _store.CardsOf(r.Id))
            {
                if (_store.Find(key) is not { } conv) continue;
                if (activityId == "digest") { await PostDigestAsync(conv, ct); continue; }
                if (key != exceptConv) await _bot.UpdateAsync(conv, activityId, Attachment(FullCard(r, key)), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { await RecordRefreshFailureAsync(r, ex); }
    }

    private async Task RecordRefreshFailureAsync(ActionRecord r, Exception ex)
    {
        var detail = Redaction.Scrub(ex.Message);
        try { await Host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, RefreshFailedEvent, r.Id, r.ToolName, "system", detail), CancellationToken.None); }
        catch (Exception audit) when (audit is not OperationCanceledException)
        {
            // The audit trail itself failed: say so on the console, the one place left.
            Console.Error.WriteLine($"Teams: a card for {r.Id} could not be refreshed ({detail}), and the audit entry failed too: {Redaction.Scrub(audit.Message)}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------- cards

    private string Sign(string actionId, string convKey) =>
        Convert.ToBase64String(HMACSHA256.HashData(_cardKey.Value, Encoding.UTF8.GetBytes(actionId + "|" + convKey)))[..32];

    private ToolRisk? RiskOf(ActionRecord r) => Host.Catalog.TryGet(r.ToolName, out var reg) ? reg.Descriptor.Risk : null;

    /// <summary>The order of risk for chat decisions: Read, Write, ExternalEffect, Destructive. A tool with no registered risk is not
    /// decidable from chat (it counts as the highest).</summary>
    internal static bool ChatMayDecide(ToolRisk? risk, ToolRisk chatMax) => risk is { } r && ChatRank(r) <= ChatRank(chatMax);

    /// <summary>Whether this action may be approved, or edited and approved, from chat. A partner's reply may not, when the switch is off.</summary>
    private bool ChatMayApprove(ActionRecord r) => _o.ApprovePartnerRepliesInChat || r.ToolName != PartnerReplyTool;

    internal static int ChatRank(ToolRisk risk) => risk switch
    {
        ToolRisk.Read => 0,
        ToolRisk.Write => 1,
        ToolRisk.ExternalEffect => 2,
        _ => 3,
    };

    private static string Headline(ActionRecord r) => r.Status switch
    {
        ActionStatus.Pending when r.RunAt is not null => "Going out soon",
        ActionStatus.Pending => "Approval needed",
        ActionStatus.Cancelled when r.Reviews.LastOrDefault()?.Reason?.StartsWith("Stopped", StringComparison.Ordinal) == true => "Stopped",
        ActionStatus.Approved or ActionStatus.Executing => "Approved, running",
        ActionStatus.Succeeded => "Done",
        ActionStatus.Rejected => "Rejected",
        ActionStatus.ChangesRequested => "Changes requested",
        ActionStatus.Failed => "Failed",
        ActionStatus.Expired => "Expired",
        _ => r.Status.ToString(),
    };

    private static JsonObject Text(string text, bool bold = false, bool subtle = false, string? color = null)
    {
        var o = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };
        if (bold) o["weight"] = "Bolder";
        if (subtle) o["isSubtle"] = true;
        if (color is not null) o["color"] = color;
        return o;
    }

    internal JsonObject Execute(string title, string verb, string actionId, string convKey, string? style = null)
    {
        var a = new JsonObject
        {
            ["type"] = "Action.Execute", ["title"] = title, ["verb"] = verb,
            ["data"] = new JsonObject { ["actionId"] = actionId, ["t"] = Sign(actionId, convKey) },
        };
        if (style is not null) a["style"] = style;
        return a;
    }

    /// <summary>The draft's text fields a reviewer may change in the card: top-level text arguments, longest first, at most three.</summary>
    private static List<(string Name, string Value)> Editable(ActionRecord r)
    {
        if (JsonNode.Parse(r.EffectiveArgumentsJson) is not JsonObject args) return [];
        return args.Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length is > 0 and <= 20_000)
            .Select(p => (p.Key, p.Value!.GetValue<string>())).OrderByDescending(p => p.Item2.Length).Take(3).ToList();
    }

    internal JsonObject FullCard(ActionRecord r, string convKey, string? note = null, bool back = false)
    {
        var risk = RiskOf(r);
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = Headline(r), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            Text(TeamsChannel.Escape(r.ToolName, 80) + (risk is { } rk ? $" · {rk}" : ""), subtle: true),
        };
        var cut = PreviewCut(r);
        if (PreviewSummary(r) is { } summary)
        {
            body.Add(Text(TeamsChannel.Escape(summary, CardPreviewMax)));
            if (cut) body.Add(Text(CutNote(_publicBaseUrl is not null), bold: true));
        }
        if (risk is ToolRisk.ExternalEffect) body.Add(Text("Reaches other people.", bold: true));
        if (risk is ToolRisk.Destructive) body.Add(Text("Deletes or closes something; may not be undoable.", bold: true));
        if (r.Status == ActionStatus.Pending && r.Tainted) body.Add(Text("Held because the caller read outside content first. Check that this is what you want.", subtle: true));
        if (r.SessionLabels.Count > 0) body.Add(Text("This session has read: " + TeamsChannel.Escape(string.Join(", ", r.SessionLabels), 300), subtle: true));
        if (r.Status == ActionStatus.Pending && r.RunAt is { } runAt) body.Add(Text($"Going out at {runAt:HH:mm} UTC unless someone stops it.", bold: true));
        body.Add(Text("Requested by " + TeamsChannel.Escape(r.Requester.Id, 80), subtle: true));
        if (r.Reviews.LastOrDefault() is { } last)
            body.Add(Text($"{last.Decision} by {TeamsChannel.Escape(last.Reviewer, 60)} · {last.At:HH:mm}" + (last.Reason is { } why ? " · " + TeamsChannel.Escape(why, 200) : "")
                + (last.EditedArgumentsJson is not null ? " · with changes" : "")));
        if (note is not null) body.Add(Text(note, color: "Attention"));

        var actions = new JsonArray();
        var delayed = r.Status == ActionStatus.Pending && r.RunAt is not null;
        if (delayed) actions.Add(Execute("Stop", "stop", r.Id, convKey, "destructive"));
        var canDecide = r.Status == ActionStatus.Pending && ChatMayDecide(risk, _o.ChatMaxRisk);
        // B3: a preview cut on this card cannot be approved, or changed, from it. Reject and request changes stay: neither sends text out.
        var approvable = canDecide && !cut && ChatMayApprove(r);
        if (approvable && delayed) actions.Add(Execute("Run now", "approve", r.Id, convKey, "positive"));
        else if (canDecide)
        {
            var editable = approvable ? Editable(r) : new List<(string, string)>();
            foreach (var (name, value) in editable)
                body.Add(new JsonObject
                {
                    ["type"] = "Input.Text", ["id"] = "edit." + name, ["label"] = name, ["value"] = value,
                    ["isMultiline"] = value.Length > 80, ["maxLength"] = 20_000,
                });
            body.Add(new JsonObject { ["type"] = "Input.Text", ["id"] = "reason", ["label"] = "Reason (needed to reject or request changes)", ["maxLength"] = 1000 });
            if (!ChatMayApprove(r)) body.Add(Text(PartnerReplyNote, subtle: true));
            if (approvable) actions.Add(Execute("Approve", "approve", r.Id, convKey, "positive"));
            if (approvable && editable.Count > 0)
            {
                if (Host.Catalog.TryGet(r.ToolName, out var reg) && reg.Preview is null)
                    body.Add(Text("Changes to this text are not checked by the tool (it has no preview).", subtle: true));
                actions.Add(Execute("Approve with changes", "edit", r.Id, convKey));
            }
            actions.Add(Execute("Request changes", "request_changes", r.Id, convKey));
            actions.Add(Execute("Reject", "reject", r.Id, convKey, "destructive"));
        }
        else if (r.Status == ActionStatus.Pending)
            body.Add(Text($"Decide this on the review page (Teams may decide up to {_o.ChatMaxRisk}).", subtle: true));
        if (_publicBaseUrl is not null) actions.Add(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open", ["url"] = $"{_publicBaseUrl}/#{r.Id}" });
        if (back) actions.Add(Execute("Back to the list", "back", DigestScope, convKey));

        var card = new JsonObject { ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body };
        if (actions.Count > 0) card["actions"] = actions;
        return card;
    }

    /// <summary>The threshold digest for one chat: the waiting actions that have a card there.</summary>
    internal async Task<JsonObject> DigestCardAsync(string convKey, CancellationToken ct)
    {
        var items = new List<ActionRecord>();
        foreach (var r in await Host.Store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, Limit = 500 }, ct))
            if (_store.CardsOf(r.Id).Any(c => c.ConvKey == convKey)) items.Add(r);
        return DigestCard(items, convKey);
    }

    /// <summary>A digest card over these actions, oldest first, at most 20 with a button each.</summary>
    internal JsonObject DigestCard(IReadOnlyList<ActionRecord> items, string convKey)
    {
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock", ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true,
                ["text"] = items.Count switch
                {
                    0 => "Nothing is waiting",
                    1 => "1 action is waiting for you",
                    _ => $"{items.Count} actions are waiting for you",
                },
            },
        };
        var actions = new JsonArray();
        foreach (var r in items.OrderBy(x => x.CreatedAt).Take(20))
        {
            var risk = RiskOf(r);
            body.Add(Text($"• {TeamsChannel.Escape(PreviewSummary(r) ?? r.ToolName, 140)}" + (risk is ToolRisk.ExternalEffect or ToolRisk.Destructive ? $" ({risk})" : "")
                + (PreviewSummary(r) is { } digestText && TeamsChannel.IsCut(digestText, 140) ? " (cut: open it to read it all)" : "")
                + (r.Tainted ? " · after outside content" : "")));
            actions.Add(Execute("Review: " + TeamsChannel.Escape(r.ToolName, 40), "open", r.Id, convKey));
        }
        var card = new JsonObject { ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body };
        if (actions.Count > 0) card["actions"] = actions;
        return card;
    }

    /// <summary>The longest preview a card shows whole (batch 04, B3). A longer one is cut, and a cut preview is not decided from the card.</summary>
    internal const int CardPreviewMax = 400;

    /// <summary>True when this action's preview is cut on a card (more than <see cref="CardPreviewMax"/> characters after escaping).</summary>
    internal bool PreviewCut(ActionRecord r) => PreviewSummary(r) is { } s && TeamsChannel.IsCut(s, CardPreviewMax);

    private string CutNote(bool hasOpen) => hasOpen
        ? "The text is cut on this card, so it cannot be approved or changed here. Open shows all of it on the review page."
        : "The text is cut on this card, so it cannot be approved or changed here. The review page shows all of it.";

    private static string? PreviewSummary(ActionRecord r)
    {
        if (r.PreviewJson is null) return null;
        try { using var doc = JsonDocument.Parse(r.PreviewJson); return doc.RootElement.TryGetProperty("Summary", out var s) ? s.GetString() : null; }
        catch (JsonException) { return null; }
    }

    private static JsonObject Attachment(JsonObject card) => new()
    {
        ["type"] = "message",
        ["attachments"] = new JsonArray { new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card } },
    };

    private static JsonObject Message(string text) => new() { ["type"] = "message", ["text"] = text };

    private static JsonObject CardResponse(JsonObject card) =>
        new() { ["statusCode"] = 200, ["type"] = "application/vnd.microsoft.card.adaptive", ["value"] = card };

    private static JsonObject MessageResponse(string text) =>
        new() { ["statusCode"] = 200, ["type"] = "application/vnd.microsoft.activity.message", ["value"] = text };

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
