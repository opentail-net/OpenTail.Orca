using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTail.Orca;

/// <summary>
/// Replaces personal and payment details in text with stable placeholders before anything leaves the deployment: email addresses,
/// phone numbers, IBANs and card-like or account-like digit runs. The same value gets the same placeholder within one record, so a
/// scenario built from it still makes sense ("the reply went to person2, the address that asked"). Best effort: a person reviews
/// every candidate before it is used.
/// </summary>
public sealed partial class ContentRedactor
{
    private readonly Dictionary<string, string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>How many values of each kind were replaced.</summary>
    public IReadOnlyDictionary<string, int> Replaced => _counts;

    public string Redact(string text)
    {
        // A name shown with an address ("Daniel Okafor <d.okafor@...>") is a name: remember it, so it is replaced wherever it
        // appears again (a signature, a greeting). Names written nowhere near an address are not found: patterns are not a
        // named-entity model, and the export says a person must check it.
        foreach (Match m in NamedAddress().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim().Trim('"').Trim();
            if (name.Length < 3 || name.Contains('@')) continue;
            var placeholder = Placeholder("name", name, n => $"[name {n}]");
            foreach (var part in name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 4 && char.IsUpper(p[0])))
                _seen.TryAdd(part, placeholder);
        }
        // Addresses first, so a name part inside one ("d.okafor@") goes with the address, not into it.
        text = Email().Replace(text, m => Placeholder("email", m.Value, n => $"person{n}@redacted.example"));
        foreach (var (value, placeholder) in _seen.Where(kv => kv.Value.StartsWith("[name", StringComparison.Ordinal)).OrderByDescending(kv => kv.Key.Length).ToList())
            text = Regex.Replace(text, @"(?<![\w.@-])" + Regex.Escape(value) + @"(?![\w@-])", placeholder, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        text = Iban().Replace(text, m => Placeholder("iban", m.Value, n => $"[account {n}]"));
        text = Phone().Replace(text, m => Placeholder("phone", m.Value, n => $"[phone {n}]"));
        text = Digits().Replace(text, m => IsoDate().IsMatch(m.Value) ? m.Value : Placeholder("number", m.Value, n => $"[number {n}]"));
        return text;
    }

    /// <summary>Redacts every text value in a JSON document, keeping its structure.</summary>
    public JsonNode? Redact(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Redact(kv.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Redact)]),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Redact(s)),
        JsonValue v => v.DeepClone(),
        _ => null,
    };

    private string Placeholder(string kind, string value, Func<int, string> make)
    {
        if (_seen.TryGetValue(value, out var p)) return p;
        var n = _counts[kind] = (_counts.TryGetValue(kind, out var c) ? c : 0) + 1;
        return _seen[value] = make(n);
    }

    [GeneratedRegex(@"(?<name>""?[\p{L}][\p{L}.'-]*(?: [\p{L}][\p{L}.'&-]*){0,4}""?)\s*<[^<>@\s]+@[^<>\s]+>")]
    private static partial Regex NamedAddress();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,3})?\b")]
    private static partial Regex Iban();

    [GeneratedRegex(@"(?<!\w)(?:\+\d{1,3}[ -]?)?(?:\(?0\d{2,4}\)?[ -]?)\d{3}[ -]?\d{3,4}(?!\w)")]
    private static partial Regex Phone();

    [GeneratedRegex(@"(?<![\w-])\d(?:[ -]?\d){7,}(?![\w-])")]
    private static partial Regex Digits();
}

/// <summary>One rejected or sent-back action, with what the person said and what the agent had read, ready to become a scenario.</summary>
public sealed record ReviewOutcome(
    string ActionId, string Tool, string Status, DateTimeOffset CreatedAt, IReadOnlyList<string> Reasons,
    JsonNode? Arguments, IReadOnlyList<string> SkillsServed, IReadOnlyList<ReviewOutcomeInput> Inputs, IReadOnlyDictionary<string, int> Redacted);

/// <summary>Outside content the agent read in the same session before the action (an email it was handling, a ticket), redacted.</summary>
public sealed record ReviewOutcomeInput(string Tool, JsonNode? Result);

/// <summary>
/// The flywheel's export: rejected and changes-requested actions with the reviewers' reasons, the skill versions served in the same
/// session before them, and the outside content the agent had read, all redacted. Each line is a scenario candidate: a person
/// reviews it, replaces what redaction missed, and only then turns it into a scenario. Nothing is executed or changed.
/// </summary>
public static class ReviewOutcomeExport
{
    private const int MaxInputChars = 20_000;

    /// <param name="approvedSample">Also export this share (0 to 1) of actions people approved, chosen by a hash of the action id so the
    /// same share comes out on every run. Approved is not the same as good: these are for the grading queue, to measure quality on
    /// real work, not only failures.</param>
    public static async Task<IReadOnlyList<ReviewOutcome>> CollectAsync(OpenTailHost host, DateTimeOffset? since = null, CancellationToken ct = default,
        double approvedSample = 0)
    {
        var decided = (await host.Store.QueryAsync(new ActionQuery
        {
            AnyOfStatus = [ActionStatus.Rejected, ActionStatus.ChangesRequested],
            UpdatedSince = since,
            Limit = int.MaxValue,
        }, ct)).ToList();
        if (approvedSample > 0)
            decided.AddRange((await host.Store.QueryAsync(new ActionQuery { Status = ActionStatus.Succeeded, UpdatedSince = since, Limit = int.MaxValue }, ct))
                .Where(a => a.Reviews.Any(r => r.Decision == ReviewDecision.Approve) && Unit(a.Id) < approvedSample));

        var outcomes = new List<ReviewOutcome>();
        foreach (var a in decided.OrderBy(a => a.CreatedAt))
        {
            var session = a.SessionId is null ? [] : await host.Store.QueryAsync(new ActionQuery { SessionId = a.SessionId, Limit = int.MaxValue }, ct);
            var before = session.Where(s => s.CreatedAt <= a.CreatedAt && s.Id != a.Id && s.Status == ActionStatus.Succeeded).OrderBy(s => s.CreatedAt).ToList();

            var skills = before
                .Where(s => host.Catalog.TryGet(s.ToolName, out var t) && t.Descriptor.AuditResult?.Event == "skill_served")
                .Select(s => Field(s.ResultJson, host.Catalog.TryGet(s.ToolName, out var t) ? t.Descriptor.AuditResult!.Field : "served"))
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList();

            var redactor = new ContentRedactor();
            var inputs = before
                .Where(s => host.Catalog.TryGet(s.ToolName, out var t) && t.Descriptor.ReadsUntrusted && s.ResultJson is { Length: > 0 and <= MaxInputChars })
                .Select(s => new ReviewOutcomeInput(s.ToolName, redactor.Redact(Parse(s.ResultJson!))))
                .ToList();
            var args = redactor.Redact(Parse(a.EffectiveArgumentsJson));
            if (args is JsonObject o) o.Remove("sessionId");
            var reasons = a.Reviews.Where(r => r.Decision is ReviewDecision.Reject or ReviewDecision.RequestChanges && !string.IsNullOrWhiteSpace(r.Reason))
                .Select(r => redactor.Redact(r.Reason!)).ToList();

            var status = a.Status switch { ActionStatus.Rejected => "rejected", ActionStatus.ChangesRequested => "changes_requested", _ => "approved" };
            outcomes.Add(new ReviewOutcome(a.Id, a.ToolName, status, a.CreatedAt,
                reasons, args, skills, inputs, new Dictionary<string, int>(redactor.Replaced)));
        }
        return outcomes;
    }

    /// <summary>One JSON object per line.</summary>
    public static string ToJsonLines(IEnumerable<ReviewOutcome> outcomes) =>
        string.Concat(outcomes.Select(o => JsonSerializer.Serialize(new
        {
            format = 1,
            actionId = o.ActionId,
            tool = o.Tool,
            status = o.Status,
            createdAt = o.CreatedAt,
            reasons = o.Reasons,
            arguments = o.Arguments,
            skillsServed = o.SkillsServed,
            inputs = o.Inputs.Select(i => new { tool = i.Tool, result = i.Result }),
            redacted = o.Redacted,
            reviewNeeded = "Redaction is best effort: a person checks this before it becomes a scenario.",
        }) + "\n"));

    private static double Unit(string text) =>
        BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), 0) / (double)uint.MaxValue * 0.999999;

    internal static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return JsonValue.Create(json); }
    }

    private static string? Field(string? json, string field) => json is null ? null : Parse(json)?[field]?.ToString();
}

/// <summary>A real session for a shadow run: the messages the agent read, the skills it was served, and what people decided.</summary>
public sealed record ShadowCase(string SessionId, IReadOnlyList<string> MessageIds, IReadOnlyList<string> SkillsServed, IReadOnlyList<ShadowDecision> Decisions);

public sealed record ShadowDecision(string ActionId, string Tool, JsonNode? Arguments, string Decision, string? Reason);

/// <summary>
/// Cases for shadow runs: sessions in which a skill was served and people decided at least one action. Unlike the review-outcome
/// export this is <b>not redacted</b>: a shadow run re-reads the same messages through a shadow Host, so it needs their real ids.
/// It holds real content and must stay inside the deployment, under the Host's retention, and be deleted with the proposal.
/// </summary>
public static class ShadowCases
{
    public static async Task<IReadOnlyList<ShadowCase>> CollectAsync(OpenTailHost host, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var serveTools = host.Catalog.All.Where(t => t.Descriptor.AuditResult?.Event == "skill_served")
            .ToDictionary(t => t.Descriptor.Name, t => t.Descriptor.AuditResult!.Field, StringComparer.Ordinal);
        var served = new List<ActionRecord>();
        foreach (var tool in serveTools.Keys)
            served.AddRange(await host.Store.QueryAsync(new ActionQuery { ToolName = tool, Status = ActionStatus.Succeeded, UpdatedSince = since, Limit = int.MaxValue }, ct));

        var cases = new List<ShadowCase>();
        foreach (var session in served.Where(s => s.SessionId is not null).Select(s => s.SessionId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var actions = (await host.Store.QueryAsync(new ActionQuery { SessionId = session, Limit = int.MaxValue }, ct)).OrderBy(a => a.CreatedAt).ToList();
            var skills = actions.Where(a => serveTools.ContainsKey(a.ToolName) && a.Status == ActionStatus.Succeeded)
                .Select(a => ReviewOutcomeExport.Parse(a.ResultJson ?? "null")?[serveTools[a.ToolName]]?.ToString())
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var messages = actions.Where(a => host.Catalog.TryGet(a.ToolName, out var t) && t.Descriptor.ReadsUntrusted)
                .Select(a => ReviewOutcomeExport.Parse(a.EffectiveArgumentsJson)?["messageId"]?.ToString())
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var decisions = actions
                .Select(a => (Action: a, Review: a.Reviews.LastOrDefault(r => r.Decision is ReviewDecision.Approve or ReviewDecision.Reject or ReviewDecision.RequestChanges)))
                .Where(x => x.Review is not null)
                .Select(x => new ShadowDecision(x.Action.Id, x.Action.ToolName, ReviewOutcomeExport.Parse(x.Action.EffectiveArgumentsJson),
                    x.Review!.Decision switch { ReviewDecision.Approve => "approved", ReviewDecision.Reject => "rejected", _ => "changes_requested" },
                    x.Review.Reason))
                .ToList();
            if (messages.Count > 0 && decisions.Count > 0) cases.Add(new ShadowCase(session, messages, skills, decisions));
        }
        return cases;
    }

    public static string ToJsonLines(IEnumerable<ShadowCase> cases) =>
        string.Concat(cases.Select(c => JsonSerializer.Serialize(new
        {
            format = 1,
            sessionId = c.SessionId,
            messageIds = c.MessageIds,
            skillsServed = c.SkillsServed,
            decisions = c.Decisions.Select(d => new { actionId = d.ActionId, tool = d.Tool, arguments = d.Arguments, decision = d.Decision, reason = d.Reason }),
            warning = "Real content, not redacted: keep inside the deployment and delete with the proposal.",
        }) + "\n"));
}
